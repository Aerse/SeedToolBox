using System;
using System.Threading.Tasks;
using System.Windows;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;
using SeedToolBox.Views;

namespace SeedToolBox.Terminal;

/// <summary>The terminal in a window of its own; one at a time.</summary>
sealed class TerminalWindow : Window
{
    static TerminalWindow? _open;
    readonly TerminalPage _page;

    /// <summary>The toolbox page for the terminal: opens the window whenever the page is shown.</summary>
    public static FrameworkElement Launcher(Action open)
    {
        var panel = new System.Windows.Controls.StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "终端 / SSH 在单独的窗口里打开", FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
        var button = Ui.Button("打开终端窗口", open, accent: true);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(button);
        panel.Loaded += (_, _) => open();
        return panel;
    }

    public static void Open(Func<string, Task<string>>? ai)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new TerminalWindow(ai);
        _open.Show();
        _open.Activate();
    }

    TerminalWindow(Func<string, Task<string>>? ai)
    {
        Title = "终端 / SSH";
        MinWidth = 720;
        MinHeight = 420;
        _page = new TerminalPage(ai) { Margin = new Thickness(10) };
        Content = _page;
        DialogWindow.ApplyTheme(this);

        var b = _page.Data.WindowBounds;
        if (b.Length == 4 && b[2] >= MinWidth && b[3] >= MinHeight && OnScreen(b))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b[0]; Top = b[1]; Width = b[2]; Height = b[3];
        }
        else
        {
            Width = Math.Min(1360, SystemParameters.WorkArea.Width * 0.85);
            Height = Math.Min(820, SystemParameters.WorkArea.Height * 0.85);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_page.Data.WindowMaximized) WindowState = WindowState.Maximized;
    }

    static bool OnScreen(double[] b) =>
        b[0] + 100 > SystemParameters.VirtualScreenLeft && b[1] >= SystemParameters.VirtualScreenTop - 10 &&
        b[0] + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        b[1] + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var connected = _page.ConnectedCount;
        if (connected > 0 && !ApiDialogs.Confirm(this, $"还有 {connected} 个 SSH 连接，关闭窗口会全部断开。继续吗？")) { e.Cancel = true; return; }
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _page.Data.WindowBounds = new[] { r.Left, r.Top, r.Width, r.Height };
        _page.Data.WindowMaximized = WindowState == WindowState.Maximized;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _page.Shutdown();
        _open = null;
        base.OnClosed(e);
    }
}
