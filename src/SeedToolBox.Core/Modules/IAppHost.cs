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

    /// <summary>
    /// Adds a page to the toolbox window under <paramref name="group"/>. <paramref name="create"/> returns a WPF element
    /// and runs the first time the page is opened.
    /// </summary>
    void AddPage(string id, string group, string glyph, string name, Func<object> create);

    /// <summary>
    /// Adds a global hotkey the user can change on the settings page. <paramref name="defaultHotkey"/> is like "Ctrl+Alt+T",
    /// or "" for none. The user's choice is remembered under <paramref name="id"/>.
    /// </summary>
    void AddHotkey(string id, string label, string defaultHotkey, Action onPressed);

    /// <summary>Adds a section to the settings page; <paramref name="create"/> returns a WPF element.</summary>
    void AddSettingsSection(string title, Func<object> create);

    /// <summary>Marshals work from background threads back to the UI thread.</summary>
    void RunOnUiThread(Action action);
}
