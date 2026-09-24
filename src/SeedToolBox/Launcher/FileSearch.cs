using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Launcher;

/// <summary>
/// Finds files by name for the launcher's "f " prefix: through Everything's command line (es.exe) when it is
/// installed, otherwise through the Windows Search index.
/// </summary>
static class FileSearch
{
    public const int MaxResults = 30;
    static string? _es;
    static bool _esChecked;

    /// <summary>Which engine answers searches, for the hint line.</summary>
    public static string Engine => FindEs() != null ? "Everything" : "Windows 搜索";

    /// <summary>Blocking; call from a background thread.</summary>
    public static List<string> Find(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return new();
        if (FindEs() is { } es)
        {
            try { return RunEs(es, query); }
            catch (Exception ex) { Log.Error("Everything search failed", ex); }
        }
        return WindowsSearch(query);
    }

    static string? FindEs()
    {
        if (_esChecked) return _es;
        _esChecked = true;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Where(p => p.Length > 0).Select(p => Path.Combine(p.Trim(), "es.exe"))
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "es.exe"),
            });
        foreach (var path in candidates)
        {
            try { if (File.Exists(path)) return _es = path; }
            catch (ArgumentException) { }
        }
        return null;
    }

    static List<string> RunEs(string es, string query)
    {
        var info = new ProcessStartInfo(es, $"-n {MaxResults} \"{query.Replace("\"", "")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        // es.exe exits non-zero when Everything isn't running
        if (process.ExitCode != 0) throw new InvalidOperationException($"es.exe exited with {process.ExitCode}");
        return output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    static List<string> WindowsSearch(string query)
    {
        var list = new List<string>();
        // Each word must appear in the name; quotes and wildcards are escaped for the LIKE pattern
        var words = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]"));
        var where = string.Join(" AND ", words.Select(w => $"System.FileName LIKE '%{w}%'"));
        var sql = $"SELECT TOP {MaxResults} System.ItemPathDisplay FROM SYSTEMINDEX WHERE {where} AND SCOPE='file:' ORDER BY System.DateModified DESC";
        try
        {
            using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (reader[0] is string path) list.Add(path);
        }
        catch (Exception ex)
        {
            Log.Error("Windows Search query failed", ex);
        }
        return list;
    }
}
