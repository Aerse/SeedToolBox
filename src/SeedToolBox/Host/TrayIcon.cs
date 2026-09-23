using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Host;

/// <summary>Notification-area icon with the right-click menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _notifyIcon;
    readonly WinForms.ContextMenuStrip _menu;
    // Module entries are inserted just above this separator
    readonly WinForms.ToolStripSeparator _moduleSeparator = new() { Visible = false };
    readonly Dictionary<string, WinForms.ToolStripMenuItem> _submenus = new();
    // Commands such as screenshot go above this separator, next to "show main window"
    readonly WinForms.ToolStripSeparator _commandSeparator = new();
    readonly Dictionary<string, WinForms.ToolStripMenuItem> _commands = new();

    public event Action? ToggleWindowRequested;
    public event Action? ShowWindowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public const string ShowWindowCommand = "show";

    public TrayIcon()
    {
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add(_commandSeparator);
        AddCommand(ShowWindowCommand, "显示主窗口", () => ShowWindowRequested?.Invoke());
        _menu.Items.Add(_moduleSeparator);
        _menu.Items.Add("设置…", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        FluentMenuRenderer.Apply(_menu);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "SeedToolBox",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleWindowRequested?.Invoke();
        };
    }

    public void AddModuleItem(string text, Action onClick, string? submenu)
    {
        var item = new WinForms.ToolStripMenuItem(text, null, (_, _) => onClick());
        if (submenu == null)
        {
            _menu.Items.Insert(_menu.Items.IndexOf(_moduleSeparator), item);
        }
        else
        {
            if (!_submenus.TryGetValue(submenu, out var parent))
            {
                parent = new WinForms.ToolStripMenuItem(submenu);
                _submenus[submenu] = parent;
                _menu.Items.Insert(_menu.Items.IndexOf(_moduleSeparator), parent);
            }
            parent.DropDownItems.Add(item);
        }
        _moduleSeparator.Visible = true;
    }

    public void AddCommand(string id, string text, Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem(text, null, (_, _) => onClick());
        _commands[id] = item;
        _menu.Items.Insert(_menu.Items.IndexOf(_commandSeparator), item);
    }

    /// <summary>Shows the command's hotkey right-aligned in the menu.</summary>
    public void SetShortcutText(string id, string hotkey)
    {
        if (_commands.TryGetValue(id, out var item)) item.ShortcutKeyDisplayString = hotkey;
    }

    public void ShowMessage(string text) =>
        _notifyIcon.ShowBalloonTip(5000, "SeedToolBox", text, WinForms.ToolTipIcon.Warning);

    static Icon LoadIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        return stream != null ? new Icon(stream, WinForms.SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
