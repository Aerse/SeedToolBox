using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SeedToolBox.Core.Services;

/// <summary>
/// Backs up the Data folder (settings of the app and its modules) to a zip, and restores one.
/// Logs and the backups themselves are left out.
/// </summary>
public static class DataBackup
{
    public static string Folder { get; } = Path.Combine(AppPaths.Data, "Backups");

    const string AutoPrefix = "自动备份_";
    const int AutoKeep = 7;

    public static void Create(string zipPath) => Create(zipPath, Files(false), CompressionLevel.Optimal);

    static void Create(string zipPath, string[] files, CompressionLevel level)
    {
        var temp = zipPath + ".tmp";
        File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var file in files)
                zip.CreateEntryFromFile(file, Relative(file).Replace('\\', '/'), level);
        }
        // Written aside first so a failure never leaves a broken backup under the real name
        File.Delete(zipPath);
        File.Move(temp, zipPath);
    }

    /// <summary>Restored files waiting for the next start, so nothing the running app writes on exit can overwrite them.</summary>
    static string PendingFolder => Path.Combine(Folder, "pending-restore");

    /// <summary>
    /// Unpacks the backup aside; <see cref="ApplyPending"/> moves it over the data files at the next start.
    /// The app must restart afterwards.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a SeedToolBox backup.</exception>
    public static void Restore(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(AppPaths.Data) + Path.DirectorySeparatorChar;
        var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        if (entries.Count == 0 || !entries.Any(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("不是 SeedToolBox 的备份文件");

        foreach (var entry in entries)
        {
            var target = Path.GetFullPath(Path.Combine(AppPaths.Data, entry.FullName));
            // Refuse entries that would land outside Data ("..\" in the name)
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"备份中含有无效路径：{entry.FullName}");
        }
        if (Directory.Exists(PendingFolder)) Directory.Delete(PendingFolder, true);
        var staging = PendingFolder + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        foreach (var entry in entries)
        {
            var target = Path.Combine(staging, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        // Renamed only once complete, so a half-unpacked backup is never applied
        Directory.Move(staging, PendingFolder);
    }

    /// <summary>Moves a restore left by <see cref="Restore"/> into place. Runs at startup before anything reads the data.</summary>
    public static void ApplyPending()
    {
        if (!Directory.Exists(PendingFolder)) return;
        var root = Path.GetFullPath(PendingFolder).TrimEnd(Path.DirectorySeparatorChar).Length + 1;
        foreach (var file in Directory.GetFiles(PendingFolder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(AppPaths.Data, Path.GetFullPath(file).Substring(root));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Copied, not moved: if this is interrupted the whole restore is applied again next time
            File.Copy(file, target, true);
            // An older .bak must not win over the restored file if it turns out unreadable
            File.Delete(target + ".bak");
        }
        Directory.Delete(PendingFolder, true);
        Log.Info("Applied restored data");
    }

    /// <summary>One backup a day into <see cref="Folder"/>, keeping the last week.</summary>
    public static void AutoBackup()
    {
        var files = Files(true);
        if (files.Length == 0) return;
        Directory.CreateDirectory(Folder);
        var today = Path.Combine(Folder, $"{AutoPrefix}{DateTime.Now:yyyyMMdd}.zip");
        if (File.Exists(today)) return;
        // Runs in the background on every first start of the day, so speed matters more than size
        Create(today, files, CompressionLevel.Fastest);

        foreach (var old in Directory.GetFiles(Folder, AutoPrefix + "*.zip").OrderByDescending(f => f).Skip(AutoKeep))
            File.Delete(old);
    }

    /// <summary>Large folders that the daily backup leaves out: screenshots, caches and copies the app keeps anyway.</summary>
    static readonly string[] SkippedByAuto = { "captures", "AiBackup", "PiAgent", "WebView2", "TerminalLogs", "hosts-backup", "env-backup" };

    static string[] Files(bool auto)
    {
        if (!Directory.Exists(AppPaths.Data)) return new string[0];
        var skipped = new[] { AppPaths.Logs, Folder, Path.Combine(AppPaths.Data, "Clipboard") }
            .Concat(auto ? SkippedByAuto.Select(d => Path.Combine(AppPaths.Data, d)) : Enumerable.Empty<string>())
            .Select(d => Path.GetFullPath(d) + Path.DirectorySeparatorChar).ToArray();
        return Directory.GetFiles(AppPaths.Data, "*", SearchOption.AllDirectories)
            .Where(f => !skipped.Any(s => Path.GetFullPath(f).StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    static string Relative(string file) => Path.GetFullPath(file).Substring(Path.GetFullPath(AppPaths.Data).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
}
