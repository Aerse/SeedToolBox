using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Launcher;

public static class ProcessLauncher
{
    const int ERROR_CANCELLED = 1223;

    public static string ExePath { get; } = Process.GetCurrentProcess().MainModule!.FileName;

    public static bool Launch(LaunchItem item, bool asAdmin = false) =>
        Start(item.Path, item.Arguments, item.Name, asAdmin || item.RunAsAdmin, item.WorkingDirectory);

    /// <summary>Starts a program/file/URL through the shell, reporting failures to the user. Returns true if started.</summary>
    public static bool Start(string path, string arguments = "", string? displayName = null, bool asAdmin = false, string? workingDirectory = null)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Arguments = arguments,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(workingDirectory);
        else if (File.Exists(path))
            psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (asAdmin)
            psi.Verb = "runas";

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // User declined the UAC prompt
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start {path}", ex);
            MessageBox.Show($"启动失败：{displayName ?? path}\n{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public static void OpenLocation(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"");
        else
            MessageBox.Show($"找不到路径：\n{path}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
