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

static class AuthKinds
{
    public const string Password = "password", Key = "key", Interactive = "interactive";
}

sealed class TunnelSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>L (local → remote), R (remote → local) or D (dynamic SOCKS).</summary>
    public string Kind { get; set; } = "L";
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; }
    public bool AutoStart { get; set; }

    [JsonIgnore]
    public string Describe => Kind switch
    {
        "R" => $"远程 {BindHost}:{BindPort} → 本机 {TargetHost}:{TargetPort}",
        "D" => $"SOCKS 代理 {BindHost}:{BindPort}",
        _ => $"本机 {BindHost}:{BindPort} → 远程 {TargetHost}:{TargetPort}",
    };
}

sealed class HostEntry
{
    public List<string> Favorites { get; set; } = new();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Folder path, "a/b"; empty for the top level.</summary>
    public string Group { get; set; } = "";
    /// <summary>ssh, telnet, serial, ftp or ftps (see <see cref="Protocols"/>).</summary>
    public string Protocol { get; set; } = Protocols.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    /// <summary>none, odd, even, mark or space.</summary>
    public string Parity { get; set; } = "none";
    /// <summary>1, 1.5 or 2.</summary>
    public string StopBits { get; set; } = "1";
    /// <summary>none, rtscts or xonxoff.</summary>
    public string FlowControl { get; set; } = "none";
    public string User { get; set; } = "root";
    public string Auth { get; set; } = AuthKinds.Password;
    public string Password { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string KeyPassphrase { get; set; } = "";
    /// <summary>Another host to connect through (ProxyJump).</summary>
    public string JumpHostId { get; set; } = "";
    /// <summary>none, socks4, socks5 or http.</summary>
    public string Proxy { get; set; } = "none";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyUser { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string Encoding { get; set; } = "utf-8";
    public string StartupCommand { get; set; } = "";
    public int KeepAliveSeconds { get; set; } = 30;
    public bool AutoReconnect { get; set; } = true;
    public string Color { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<TunnelSpec> Tunnels { get; set; } = new();
    public DateTime? LastConnected { get; set; }

    [JsonIgnore] public string Title => Name.Length > 0 ? Name : $"{User}@{Host}";
    [JsonIgnore] public bool IsSsh => Protocol is Protocols.Ssh or "" or null;
    [JsonIgnore] public string Address => Protocol switch
    {
        Protocols.Serial => $"{SerialPort} {BaudRate}",
        Protocols.Telnet => $"telnet://{Host}" + (Port == 23 ? "" : ":" + Port),
        Protocols.Ftp or Protocols.Ftps => $"{Protocol}://{(User.Length > 0 ? User + "@" : "")}{Host}" + (Port == 21 ? "" : ":" + Port),
        _ => Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}",
    };

    public HostEntry Clone(bool newId = false)
    {
        var c = JsonConvert.DeserializeObject<HostEntry>(JsonConvert.SerializeObject(this))!;
        if (newId) c.Id = Guid.NewGuid().ToString("N");
        return c;
    }
}

sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>May contain {{name}} or {{name:default}} placeholders, filled in when it's sent.</summary>
    public string Command { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Press Enter after sending.</summary>
    public bool Run { get; set; } = true;
}

sealed class TerminalSettings
{
    public string Theme { get; set; } = "默认深色";
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, Microsoft YaHei UI";
    public int FontSize { get; set; } = 14;
    public double LineHeight { get; set; } = 1.1;
    /// <summary>block, bar or underline.</summary>
    public string CursorStyle { get; set; } = "block";
    public bool CursorBlink { get; set; } = true;
    public int Scrollback { get; set; } = 5000;
    public bool CopyOnSelect { get; set; }
    public bool RightClickPaste { get; set; } = true;
    public bool Suggestions { get; set; } = true;
    public bool ConfirmMultilinePaste { get; set; } = true;
    public bool BellSound { get; set; }
    public string DefaultShell { get; set; } = "";
    public string LogFolder { get; set; } = "";
    public bool LogAlways { get; set; }
}

sealed class TerminalData
{
    public int Version { get; set; } = 1;
    public List<HostEntry> Hosts { get; set; } = new();
    /// <summary>Empty folders survive here; folders with hosts come from the hosts themselves.</summary>
    public List<string> Groups { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();
    /// <summary>Per host id ("local" for local shells), newest last.</summary>
    public Dictionary<string, List<string>> History { get; set; } = new();
    /// <summary>"host:port" → "type SHA256:…" of the host keys the user accepted.</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();
    public TerminalSettings Settings { get; set; } = new();
    public double SidebarWidth { get; set; } = 230;
    /// <summary>Terminal window placement: left, top, width, height; empty until first closed.</summary>
    public double[] WindowBounds { get; set; } = new double[0];
    public bool WindowMaximized { get; set; }
    public double PanelWidth { get; set; } = 340;

    public HostEntry? Find(string id) => Hosts.FirstOrDefault(h => h.Id == id);

    public IEnumerable<string> AllGroups() =>
        Hosts.Select(h => h.Group).Concat(Groups).Where(g => g.Length > 0)
            .SelectMany(g => { var parts = g.Split('/'); return Enumerable.Range(1, parts.Length).Select(n => string.Join("/", parts.Take(n))); })
            .Distinct().OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase);

    public void AddHistory(string key, string command)
    {
        command = command.Trim();
        if (command.Length == 0 || command.Length > 2000) return;
        if (!History.TryGetValue(key, out var list)) History[key] = list = new List<string>();
        list.Remove(command);
        list.Add(command);
        if (list.Count > TerminalStore.MaxHistory) list.RemoveRange(0, list.Count - TerminalStore.MaxHistory);
    }
}

/// <summary>
/// Passwords are stored with DPAPI. When the file is synced they travel as "plain:…" inside the
/// sync's own encryption, and are protected again on the next load.
/// </summary>
static class Secret
{
    const string PlainPrefix = "plain:";
    public static string Protect(string value) => SyncCrypto.Protect(value);
    public static string Reveal(string stored) =>
        stored.StartsWith(PlainPrefix) ? stored.Substring(PlainPrefix.Length) : SyncCrypto.Unprotect(stored);

    public static readonly string[] Fields = { nameof(HostEntry.Password), nameof(HostEntry.KeyPassphrase), nameof(HostEntry.ProxyPassword) };

    /// <summary>For sync: the same JSON with secrets readable on another computer.</summary>
    public static void MakePortable(JObject root)
    {
        foreach (var h in root["Hosts"] as JArray ?? new JArray())
            foreach (var f in Fields)
                if (h[f] is JValue { Type: JTokenType.String } v && ((string)v!).Length > 0 && !((string)v!).StartsWith(PlainPrefix))
                    h[f] = PlainPrefix + SyncCrypto.Unprotect((string)v!);
    }
}

static class TerminalStore
{
    public static readonly string FilePath = Path.Combine(AppPaths.Data, "terminal.json");
    public const int MaxHistory = 500;

    public static TerminalData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonConvert.DeserializeObject<TerminalData>(File.ReadAllText(FilePath)) ?? new TerminalData();
                bool changed = false;
                foreach (var h in data.Hosts)
                {
                    string Fix(string s) { if (!s.StartsWith("plain:")) return s; changed = true; return Secret.Protect(Secret.Reveal(s)); }
                    h.Password = Fix(h.Password);
                    h.KeyPassphrase = Fix(h.KeyPassphrase);
                    h.ProxyPassword = Fix(h.ProxyPassword);
                }
                if (changed) Save(data);
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Reading terminal hosts failed", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
        return new TerminalData();
    }

    public static void Save(TerminalData data)
    {
        Directory.CreateDirectory(AppPaths.Data);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }
}
