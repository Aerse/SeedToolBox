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

    public event Action? ToggleWindowRequested;
    public event Action? ShowWindowRequested;
    public event Action? OpenAppLocationRequested;
    public event Action<bool>? LockSizeChanged;
    public event Action? ExitRequested;

    public TrayIcon(bool sizeLocked)
    {
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add("显示主窗口", null, (_, _) => ShowWindowRequested?.Invoke());
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_moduleSeparator);
        _menu.Items.Add("打开本程序位置", null, (_, _) => OpenAppLocationRequested?.Invoke());

        var lockItem = new WinForms.ToolStripMenuItem("锁定窗体尺寸") { CheckOnClick = true, Checked = sizeLocked };
        lockItem.CheckedChanged += (_, _) => LockSizeChanged?.Invoke(lockItem.Checked);
        _menu.Items.Add(lockItem);

        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

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
