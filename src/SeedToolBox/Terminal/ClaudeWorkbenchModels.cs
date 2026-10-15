using System;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Sync;

namespace SeedToolBox.Terminal;

/// <summary>A folder the workbench can start Claude Code in.</summary>
sealed class ClaudeFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Overrides <see cref="ClaudeWorkbenchData.DefaultCommand"/> when not empty.</summary>
    public string Command { get; set; } = "";
    /// <summary>When the workbench reopens, each tab resumes its conversation instead of starting a new one.</summary>
    public bool Continue { get; set; } = true;
    /// <summary>Its window in the workbench: left, top, width, height as shares of the area, 0..1.</summary>
    public double[] Window { get; set; } = Array.Empty<double>();

    [JsonIgnore] public string DisplayName => Name.Length > 0 ? Name : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
}

/// <summary>One terminal: a Claude Code conversation, or a plain shell, in a folder.</summary>
sealed class ClaudeTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Folder { get; set; } = "";
    /// <summary>The conversation, so it is resumed exactly even when the folder has several.</summary>
    public string? SessionId { get; set; }
    /// <summary>A plain shell instead of Claude Code.</summary>
    public bool Shell { get; set; }
}

/// <summary>The split layout: a leaf is a group of tabs, a split holds two children side by side or stacked.</summary>
sealed class LayoutNode
{
    /// <summary>Leaf: its tabs, and the id of the one shown.</summary>
    public List<ClaudeTab> Tabs { get; set; } = new();
    public string? Selected { get; set; }
    /// <summary>From before tabs, when a leaf showed one folder; moved into <see cref="Tabs"/> on load.</summary>
    public string? Folder { get; set; }
    /// <summary>True: A above B; false: A left of B.</summary>
    public bool Vertical { get; set; }
    /// <summary>Share of A, 0..1.</summary>
    public double Ratio { get; set; } = 0.5;
    public LayoutNode? A { get; set; }
    public LayoutNode? B { get; set; }

    [JsonIgnore] public bool IsLeaf => A == null;
    [JsonIgnore] public LayoutNode? Parent { get; set; }

    public IEnumerable<LayoutNode> Leaves() => IsLeaf ? new[] { this } : A!.Leaves().Concat(B!.Leaves());
    public IEnumerable<LayoutNode> Nodes() => IsLeaf ? new[] { this } : new[] { this }.Concat(A!.Nodes()).Concat(B!.Nodes());

    /// <summary>Restores parent links after loading.</summary>
    public void Link()
    {
        if (IsLeaf) return;
        A!.Parent = this; B!.Parent = this;
        A.Link(); B.Link();
    }
}

sealed class ClaudeWorkbenchData
{
    public string DefaultCommand { get; set; } = "claude";
    /// <summary>What runs the command: an id from <see cref="LocalSession.DetectShells"/> (pwsh, powershell or cmd).</summary>
    public string Shell { get; set; } = "powershell";
    /// <summary>Open the workbench and restore its terminals when the toolbox starts.</summary>
    public bool RestoreOnStartup { get; set; }
    /// <summary>Tile the windows whenever one opens, closes or is minimized.</summary>
    public bool AutoArrange { get; set; } = true;
    public List<ClaudeFolder> Folders { get; set; } = new();
    public LayoutNode? Layout { get; set; }
    /// <summary>Tabs from last time not reopened yet: they come back when their folder is clicked.</summary>
    public List<ClaudeTab> Parked { get; set; } = new();
    public double[] WindowBounds { get; set; } = Array.Empty<double>();
    public bool WindowMaximized { get; set; }
    public double SidebarWidth { get; set; } = 220;
}

static class ClaudeWorkbenchStore
{
    public static readonly string FilePath = System.IO.Path.Combine(AppPaths.Data, "claude-workbench.json");

    public static ClaudeWorkbenchData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonConvert.DeserializeObject<ClaudeWorkbenchData>(File.ReadAllText(FilePath)) ?? new ClaudeWorkbenchData();
                data.DefaultCommand = Reveal(data.DefaultCommand, "claude");
                foreach (var f in data.Folders) f.Command = Reveal(f.Command, "");
                // Written before commands were protected: encrypt it now rather than on the next change
                if (!data.DefaultCommand.StartsWith(ProtectedPrefix) && File.ReadAllText(FilePath).Contains("\"DefaultCommand\": \"" + ProtectedPrefix) == false)
                    try { Save(data); } catch (Exception save) when (save is IOException or UnauthorizedAccessException) { Log.Error("Saving the Claude workbench failed", save); }
                data.Layout?.Link();
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Reading the Claude workbench failed", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
        return new ClaudeWorkbenchData();
    }

    public static void Save(ClaudeWorkbenchData data)
    {
        Directory.CreateDirectory(AppPaths.Data);
        var tmp = FilePath + ".tmp";
        // Commands often carry API tokens: stored with DPAPI, readable only by this Windows account
        var root = JObject.FromObject(data);
        root[nameof(ClaudeWorkbenchData.DefaultCommand)] = Protect(data.DefaultCommand);
        foreach (var f in root[nameof(ClaudeWorkbenchData.Folders)] as JArray ?? new JArray())
            f[nameof(ClaudeFolder.Command)] = Protect((string?)f[nameof(ClaudeFolder.Command)] ?? "");
        File.WriteAllText(tmp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    const string ProtectedPrefix = "dpapi:";

    static string Protect(string command) => command.Length == 0 ? "" : ProtectedPrefix + SyncCrypto.Protect(command);

    /// <summary>Plain text is from before commands were protected and is kept as is.</summary>
    static string Reveal(string stored, string fallback)
    {
        if (!stored.StartsWith(ProtectedPrefix)) return stored;
        var plain = SyncCrypto.Unprotect(stored.Substring(ProtectedPrefix.Length));
        // Another Windows account or computer cannot decrypt it
        if (plain.Length == 0) Log.Error("A Claude workbench command could not be decrypted");
        return plain.Length > 0 ? plain : fallback;
    }

    static readonly string Projects = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Whether Claude Code saved the conversation, so it can be resumed rather than started with that id.</summary>
    public static bool HasConversation(string sessionId)
    {
        // Every project folder, not only the one named after the path: long paths get a shortened name
        try { return Directory.Exists(Projects) && Directory.EnumerateDirectories(Projects).Any(d => File.Exists(System.IO.Path.Combine(d, sessionId + ".jsonl"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The folder's most recent conversation, for layouts saved before tabs kept their own.</summary>
    public static string? LatestConversation(string folder)
    {
        // Claude Code names the project folder after the path with every non-alphanumeric character as '-'
        var key = new string(folder.TrimEnd('\\', '/').Select(c => c < 128 && char.IsLetterOrDigit(c) ? c : '-').ToArray());
        try
        {
            var dir = new DirectoryInfo(System.IO.Path.Combine(Projects, key));
            return dir.Exists ? dir.EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name)).FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
