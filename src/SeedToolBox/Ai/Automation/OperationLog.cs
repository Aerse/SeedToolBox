using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Ai.Automation;

public enum OperationKind { Move, Copy, CreateFolder, WriteFile, Reminder, Note, Other }

/// <summary>Something the automation mode changed, with what is needed to put it back.</summary>
public sealed class Operation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Summary { get; set; } = "";
    public OperationKind Kind { get; set; }
    /// <summary>Move: (from, to) of each file. Copy / CreateFolder / WriteFile: the created path first; Copy / WriteFile end with its <see cref="FileActions.Stamp"/> when written.</summary>
    public List<string[]> Items { get; set; } = new();
    /// <summary>WriteFile: the copy of the file as it was before, or null when it was new.</summary>
    public string? Backup { get; set; }
    /// <summary>Reminder / Note: the id of what was added.</summary>
    public string? Target { get; set; }
    public bool Undone { get; set; }

    public bool CanUndo => !Undone && Kind != OperationKind.Other;
}

sealed class OperationData
{
    public List<Operation> Items { get; set; } = new();
}

/// <summary>Everything the automation mode changed, newest first, kept in Data\ai-log.json; the last 300 are kept.</summary>
sealed class OperationLog
{
    const string SettingsName = "ai-log";
    const int Limit = 300;

    readonly ISettingsStore _store;
    readonly OperationData _data;

    public static string BackupDir => Path.Combine(AppPaths.Data, "AiBackup");

    /// <summary>Undoes a reminder or note by id; set by the app, which owns those services.</summary>
    public Func<string, bool>? RemoveReminder { get; set; }
    public Func<string, bool>? RemoveNote { get; set; }

    public event Action? Changed;

    public OperationLog(ISettingsStore store)
    {
        _store = store;
        _data = store.Load<OperationData>(SettingsName);
    }

    public IReadOnlyList<Operation> Items => _data.Items;

    /// <summary>Tools run on worker threads; the list and <see cref="Changed"/> belong to the UI thread.</summary>
    public void Add(Operation operation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Add(operation));
            return;
        }
        _data.Items.Insert(0, operation);
        foreach (var old in _data.Items.Skip(Limit).ToList())
        {
            _data.Items.Remove(old);
            DeleteBackup(old);
        }
        Save();
    }

    void Save()
    {
        _store.Save(SettingsName, _data);
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var o in _data.Items) DeleteBackup(o);
        _data.Items.Clear();
        Save();
    }

    static void DeleteBackup(Operation o)
    {
        try { if (o.Backup != null && File.Exists(o.Backup)) File.Delete(o.Backup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What was changed by someone else since the operation, so undoing it would lose that; empty when nothing was.</summary>
    public static List<string> ChangedSince(Operation o)
    {
        var changed = new List<string>();
        if (o.Undone || o.Kind is not (OperationKind.Copy or OperationKind.WriteFile)) return changed;
        foreach (var item in o.Items)
            if (item.Length > 0 && FileActions.Exists(item[0]) && item[item.Length - 1] is { } stamp && stamp.StartsWith("stamp:") && FileActions.Stamp(item[0]) != stamp)
                changed.Add(item[0]);
        return changed;
    }

    /// <summary>Puts things back; returns what could not be undone, or an empty string. Items that come back are dropped, so a retry only does the rest.</summary>
    public string Undo(Operation o)
    {
        if (!o.CanUndo) return "这一步不能撤销";
        var problems = new List<string>();
        var restored = new List<string[]>();
        void Try(string what, Action action, string[]? item = null)
        {
            try
            {
                action();
                if (item != null) restored.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
            {
                problems.Add(what + "：" + ex.Message);
            }
        }
        switch (o.Kind)
        {
            case OperationKind.Move:
                // Last move first, so chains like a→b, b→c come back in order
                foreach (var pair in Enumerable.Reverse(o.Items))
                    Try(Path.GetFileName(pair[1]), () =>
                    {
                        if (FileActions.Exists(pair[0])) throw new IOException("原位置已经有同名文件");
                        Directory.CreateDirectory(Path.GetDirectoryName(pair[0])!);
                        if (Directory.Exists(pair[1])) Directory.Move(pair[1], pair[0]);
                        else File.Move(pair[1], pair[0]);
                    }, pair);
                break;
            case OperationKind.Copy:
                foreach (var pair in o.Items)
                    // To the recycle bin, so changes made to the copy since can still be got back
                    Try(Path.GetFileName(pair[0]), () => { if (FileActions.Exists(pair[0])) FileActions.Recycle(pair[0]); }, pair);
                break;
            case OperationKind.CreateFolder:
                foreach (var pair in o.Items)
                    Try(pair[0], () =>
                    {
                        if (Directory.Exists(pair[0]) && Directory.EnumerateFileSystemEntries(pair[0]).Any()) throw new IOException("文件夹里已经有东西，没有删除");
                        if (Directory.Exists(pair[0])) Directory.Delete(pair[0]);
                    }, pair);
                break;
            case OperationKind.WriteFile:
                var path = o.Items[0][0];
                Try(Path.GetFileName(path), () =>
                {
                    // The current content goes to the recycle bin first, in case it was edited since
                    if (File.Exists(path)) FileActions.Recycle(path);
                    if (o.Backup != null) File.Copy(o.Backup, path);
                });
                break;
            case OperationKind.Reminder:
                if (RemoveReminder?.Invoke(o.Target ?? "") != true) problems.Add("提醒已经不在了");
                break;
            case OperationKind.Note:
                if (RemoveNote?.Invoke(o.Target ?? "") != true) problems.Add("笔记已经不在了");
                break;
        }
        o.Items.RemoveAll(restored.Contains);
        if (problems.Count == 0)
        {
            o.Undone = true;
            DeleteBackup(o);
        }
        Save();
        return string.Join("\n", problems);
    }
}

/// <summary>File operations and the folder rules shared by the tools and the undo.</summary>
static class FileActions
{
    public static void Recycle(string path)
    {
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else throw new FileNotFoundException("文件不存在", path);
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Size and write time of a file (write time only for a folder), to notice later changes.</summary>
    public static string Stamp(string path)
    {
        try
        {
            if (File.Exists(path)) { var f = new FileInfo(path); return $"stamp:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }
            if (Directory.Exists(path)) return $"stamp:dir:{Directory.GetLastWriteTimeUtc(path).Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "stamp:";
    }

    /// <summary>The folders the automation mode may change: the user's choice, or Desktop, Documents and Downloads.</summary>
    public static List<string> Folders(AiSettings s)
    {
        var list = s.AutomationFolders.Where(f => f.Trim().Length > 0).ToList();
        if (list.Count > 0) return list;
        return new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
    }

    /// <summary>Why the folder can't be allowed (a drive root or a system folder), or null.</summary>
    public static string? Forbidden(string folder)
    {
        string full;
        try { full = Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "路径无效"; }
        if (Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return "不能是整个磁盘";
        var system = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.Base,
        };
        foreach (var s in system.Where(s => s.Length > 0).Select(Normalize))
            if (IsUnder(full, s) || IsUnder(s, full)) return "不能包含系统或程序文件夹";
        if (Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Equals(full, StringComparison.OrdinalIgnoreCase)) return "不能是整个用户文件夹";
        return null;
    }

    public static string Normalize(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\');

    public static bool IsUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path, when it is inside one of the allowed folders; otherwise throws with a message for the model.</summary>
    public static string Writable(AiSettings s, string path)
    {
        var full = Readable(path);
        var folders = AllowedFolders(s);
        var folder = folders.FirstOrDefault(f => IsUnder(full, f) && !full.Equals(f, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"不允许改动 {full}。只能改动这些文件夹里面的内容：{string.Join("；", folders)}（用户可以在 AI 设置里添加）");
        // A junction or symbolic link inside an allowed folder could lead anywhere, so the path mustn't go through one
        for (var p = full; p.Length > folder.Length; p = Path.GetDirectoryName(p) ?? folder)
        {
            try
            {
                if (Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new ToolException($"不允许改动 {full}：{p} 是联接或符号链接，指向允许的文件夹以外");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolException($"读不了 {p} 的属性：{ex.Message}"); }
        }
        return full;
    }

    static List<string> AllowedFolders(AiSettings s) => Folders(s).Where(f => Forbidden(f) == null).Select(Normalize).ToList();

    /// <summary>Keys, passwords and browser logins: never handed to the model, whatever the user allows.</summary>
    static IEnumerable<string> Sensitive()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return AppPaths.Data;
        if (profile.Length > 0)
        {
            yield return Path.Combine(profile, ".ssh");
            yield return Path.Combine(profile, ".gnupg");
            yield return Path.Combine(profile, ".aws");
            yield return Path.Combine(profile, ".azure");
            yield return Path.Combine(profile, ".kube");
            yield return Path.Combine(profile, ".docker");
            yield return Path.Combine(profile, ".pi");
        }
        if (local.Length > 0)
        {
            yield return Path.Combine(local, "Google", "Chrome", "User Data");
            yield return Path.Combine(local, "Microsoft", "Edge", "User Data");
            yield return Path.Combine(local, "BraveSoftware");
            yield return Path.Combine(local, "Microsoft", "Credentials");
            yield return Path.Combine(local, "Microsoft", "Vault");
        }
        if (roaming.Length > 0)
        {
            yield return Path.Combine(roaming, "Mozilla");
            yield return Path.Combine(roaming, "Opera Software");
            yield return Path.Combine(roaming, "Microsoft", "Credentials");
            yield return Path.Combine(roaming, "Microsoft", "Protect");
        }
    }

    /// <summary>The full path for a read tool; throws for the sensitive places listed in <see cref="Sensitive"/>.</summary>
    public static string ReadableChecked(string path)
    {
        var full = Readable(path);
        if (Sensitive().Select(Normalize).Any(f => IsUnder(full, f)))
            throw new ToolException($"不允许读取 {full}：这里放的是密钥、密码或登录信息");
        return full;
    }

    /// <summary>What to ask the user before reading outside the allowed folders, or null when the path is inside one.</summary>
    public static string? ReadConfirm(AiSettings s, string path, string what)
    {
        var full = ReadableChecked(path);
        return AllowedFolders(s).Any(f => IsUnder(full, f)) ? null : $"{what}（在允许的文件夹以外，内容会发给模型）：\n{full}";
    }

    /// <summary>Whether a path is inside the allowed folders, and not somewhere sensitive.</summary>
    public static bool InAllowed(AiSettings s, string full) =>
        AllowedFolders(s).Any(f => IsUnder(full, f)) && !Sensitive().Select(Normalize).Any(f => IsUnder(full, f));

    public static string Readable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolException("缺少路径");
        try { return Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new ToolException("路径无效：" + path); }
    }
}

/// <summary>A tool failure the model should see as-is.</summary>
sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}
