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
        lock (Gate)
        {
            try { RotateIfFull(_file); }
            catch (Exception) { }
        }
    }

    static void RotateIfFull(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= MaxSize) return;
        File.Copy(file, file + ".old", true);
        File.Delete(file);
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
            // Checked on every write: the app can stay in the tray for weeks
            try
            {
                RotateIfFull(_file);
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            // Logging must never throw: it is often called from catch blocks
            catch (Exception) { }
        }
    }
}
