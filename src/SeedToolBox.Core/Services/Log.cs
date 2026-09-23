using System;
using System.IO;

namespace SeedToolBox.Core.Services;

/// <summary>Minimal thread-safe file logger: Data/Logs/app.log, rotated at 1 MB.</summary>
public static class Log
{
    const long MaxSize = 1024 * 1024;
    static readonly object Gate = new();
    static string? _file;

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "app.log");
        if (File.Exists(_file) && new FileInfo(_file).Length > MaxSize)
        {
            File.Copy(_file, _file + ".old", true);
            File.Delete(_file);
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        if (_file == null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex;
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); }
            catch (IOException) { }
        }
    }
}
