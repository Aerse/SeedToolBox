using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SeedToolBox.Models;

namespace SeedToolBox.Services;

public static class Launcher
{
    const int ERROR_CANCELLED = 1223;

    public static void Launch(LaunchItem item, bool asAdmin = false)
    {
        var psi = new ProcessStartInfo(item.Path)
        {
            UseShellExecute = true,
            Arguments = item.Arguments,
        };
        if (File.Exists(item.Path))
            psi.WorkingDirectory = Path.GetDirectoryName(item.Path)!;
        if (asAdmin)
            psi.Verb = "runas";

        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // User declined the UAC prompt
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{item.Name}\n{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenLocation(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            MessageBox.Show($"找不到路径：\n{path}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
