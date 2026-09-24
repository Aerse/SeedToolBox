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
    /// <summary>Move: (from, to) of each file. Copy / CreateFolder / WriteFile: the created path first.</summary>
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

    public void Add(Operation operation)
    {
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

    /// <summary>Puts things back; returns what could not be undone, or an empty string.</summary>
    public string Undo(Operation o)
    {
        if (!o.CanUndo) return "这一步不能撤销";
        var problems = new List<string>();
        void Try(string what, Action action)
        {
            try { action(); }
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
                    });
                break;
            case OperationKind.Copy:
                foreach (var pair in o.Items)
                    Try(Path.GetFileName(pair[0]), () => FileActions.Recycle(pair[0]));
                break;
            case OperationKind.CreateFolder:
                foreach (var pair in o.Items)
                    Try(pair[0], () =>
                    {
                        if (Directory.Exists(pair[0]) && Directory.EnumerateFileSystemEntries(pair[0]).Any()) throw new IOException("文件夹里已经有东西，没有删除");
                        if (Directory.Exists(pair[0])) Directory.Delete(pair[0]);
                    });
                break;
            case OperationKind.WriteFile:
                var path = o.Items[0][0];
                Try(Path.GetFileName(path), () =>
                {
                    if (o.Backup != null) File.Copy(o.Backup, path, true);
                    else if (File.Exists(path)) FileActions.Recycle(path);
                });
                break;
            case OperationKind.Reminder:
                if (RemoveReminder?.Invoke(o.Target ?? "") != true) problems.Add("提醒已经不在了");
                break;
            case OperationKind.Note:
                if (RemoveNote?.Invoke(o.Target ?? "") != true) problems.Add("笔记已经不在了");
                break;
        }
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
        var folders = Folders(s).Where(f => Forbidden(f) == null).Select(Normalize).ToList();
        if (!folders.Any(f => IsUnder(full, f) && !full.Equals(f, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"不允许改动 {full}。只能改动这些文件夹里面的内容：{string.Join("；", folders)}（用户可以在 AI 设置里添加）");
        return full;
    }

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
