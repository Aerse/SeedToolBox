using System;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Core.Modules;

/// <summary>What the app exposes to modules.</summary>
public interface IAppHost
{
    ISettingsStore Settings { get; }

    /// <summary>
    /// Adds an entry to the tray menu. Pass <paramref name="submenu"/> to group entries under a submenu.
    /// Exceptions thrown by <paramref name="onClick"/> are logged and shown to the user.
    /// </summary>
    void AddTrayMenuItem(string text, Action onClick, string? submenu = null);

    /// <summary>Marshals work from background threads back to the UI thread.</summary>
    void RunOnUiThread(Action action);
}
