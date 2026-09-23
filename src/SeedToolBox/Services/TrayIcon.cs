using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Services;

/// <summary>Notification-area icon with the right-click menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _notifyIcon;

    public event Action? ToggleWindowRequested;
    public event Action? ShowWindowRequested;
    public event Action? OpenAppLocationRequested;
    public event Action<bool>? LockSizeChanged;
    public event Action? ExitRequested;

    public TrayIcon(bool sizeLocked)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("打开本程序位置", null, (_, _) => OpenAppLocationRequested?.Invoke());

        var lockItem = new WinForms.ToolStripMenuItem("锁定窗体尺寸") { CheckOnClick = true, Checked = sizeLocked };
        lockItem.CheckedChanged += (_, _) => LockSizeChanged?.Invoke(lockItem.Checked);
        menu.Items.Add(lockItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "SeedToolBox",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleWindowRequested?.Invoke();
        };
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
    }
}
