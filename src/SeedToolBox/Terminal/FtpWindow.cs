using System;
using System.Threading;
using System.Windows;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Terminal;

/// <summary>A file browser window for an FTP / FTPS host, reusing the SFTP panel.</summary>
static class FtpWindow
{
    public static void Open(Window? owner, HostEntry host, Action onConnected)
    {
        var password = host.Password.Length > 0 ? Secret.Reveal(host.Password) : "";
        if (password.Length == 0 && host.User.Length > 0)
        {
            var asked = TerminalDialogs.Ask(owner, "FTP 登录", $"{host.User}@{host.Host} 的密码：", true);
            if (asked == null) return;
            password = asked;
        }
        var cancel = new CancellationTokenSource();
        var panel = new SftpPanel(() => null, _ => { }) { Margin = new Thickness(10, 8, 10, 6), ServerName = host.Title };
        var tabs = new System.Windows.Controls.TabControl { Margin = new Thickness(6) };
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "文件", Content = panel });
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "传输", Content = new TransfersPanel { Margin = new Thickness(10, 8, 10, 6) } });
        var window = new Window
        {
            Title = host.Title + " — " + host.Address,
            Width = 560,
            Height = 720,
            Content = tabs,
            ShowInTaskbar = true,
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        TerminalDialogs.Place(window, owner);
        window.Closed += (_, _) => { cancel.Cancel(); panel.CloseAll(); };
        panel.BindFiles(host, async () =>
        {
            var files = await FtpFiles.OpenAsync(host, password, cancel.Token);
            Log.Info($"{host.Protocol} connected to {host.Host}:{host.Port}");
            onConnected();
            return files;
        });
        window.Show();
    }
}
