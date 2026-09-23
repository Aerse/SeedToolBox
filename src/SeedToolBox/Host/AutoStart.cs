using Microsoft.Win32;
using SeedToolBox.Launcher;

namespace SeedToolBox.Host;

/// <summary>Start with Windows via the per-user Run key (no admin rights needed).</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SeedToolBox";

    /// <summary>Passed on autostart so the app starts hidden in the tray.</summary>
    public const string BackgroundArg = "--background";

    static string Command => $"\"{ProcessLauncher.ExePath}\" {BackgroundArg}";

    static string? CurrentValue
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    public static bool IsEnabled => CurrentValue != null;

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>Points an existing entry at this exe, in case the folder was moved.</summary>
    public static void Refresh()
    {
        var value = CurrentValue;
        if (value != null && value != Command) Set(true);
    }
}
