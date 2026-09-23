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

    public static void Create(string zipPath)
    {
        var temp = zipPath + ".tmp";
        File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var file in Files())
                zip.CreateEntryFromFile(file, Relative(file).Replace('\\', '/'), CompressionLevel.Optimal);
        }
        // Written aside first so a failure never leaves a broken backup under the real name
        File.Delete(zipPath);
        File.Move(temp, zipPath);
    }

    /// <summary>Overwrites the data files with those in the backup. The app must restart afterwards.</summary>
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
        foreach (var entry in entries)
        {
            var target = Path.Combine(AppPaths.Data, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    /// <summary>One backup a day into <see cref="Folder"/>, keeping the last week.</summary>
    public static void AutoBackup()
    {
        if (!Files().Any()) return;
        Directory.CreateDirectory(Folder);
        var today = Path.Combine(Folder, $"{AutoPrefix}{DateTime.Now:yyyyMMdd}.zip");
        if (File.Exists(today)) return;
        Create(today);

        foreach (var old in Directory.GetFiles(Folder, AutoPrefix + "*.zip").OrderByDescending(f => f).Skip(AutoKeep))
            File.Delete(old);
    }

    static string[] Files()
    {
        if (!Directory.Exists(AppPaths.Data)) return new string[0];
        var skipped = new[] { AppPaths.Logs, Folder }.Select(d => Path.GetFullPath(d) + Path.DirectorySeparatorChar).ToArray();
        return Directory.GetFiles(AppPaths.Data, "*", SearchOption.AllDirectories)
            .Where(f => !skipped.Any(s => Path.GetFullPath(f).StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    static string Relative(string file) => Path.GetFullPath(file).Substring(Path.GetFullPath(AppPaths.Data).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
}
