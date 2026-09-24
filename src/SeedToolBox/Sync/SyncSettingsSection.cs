using System;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Sync;

/// <summary>The 同步 section of the settings page: the WebDAV account, the sync password and what to sync.</summary>
static class SyncSettingsSection
{
    static readonly int[] Intervals = { 5, 10, 30, 60, 0 };

    public static FrameworkElement Create(SyncService service, Action restart)
    {
        var s = service.Settings;
        var panel = new StackPanel();
        var body = new StackPanel();

        var enabled = new CheckBox { Content = "通过 WebDAV 在多台电脑间同步（坚果云、Nextcloud、群晖等）", IsChecked = s.Enabled, Margin = new Thickness(0, 0, 0, 8) };
        enabled.Click += (_, _) => { s.Enabled = enabled.IsChecked == true; service.Save(); body.IsEnabled = s.Enabled; };
        panel.Children.Add(enabled);
        body.IsEnabled = s.Enabled;
        panel.Children.Add(body);

        var url = Ui.Field(360);
        url.Text = s.Url;
        url.LostKeyboardFocus += (_, _) => { s.Url = url.Text.Trim(); service.Save(); };
        var user = Ui.Field(360);
        user.Text = s.User;
        user.LostKeyboardFocus += (_, _) => { s.User = user.Text.Trim(); service.Save(); };
        var password = new PasswordBox { Width = 360, Style = DialogWindow.PasswordBoxStyle, Password = SyncCrypto.Unprotect(s.Password) };
        password.LostKeyboardFocus += (_, _) => { s.Password = SyncCrypto.Protect(password.Password); service.Save(); };
        var folder = Ui.Field(360);
        folder.Text = s.Folder;
        folder.LostKeyboardFocus += (_, _) => { s.Folder = folder.Text.Trim().Trim('/').Length > 0 ? folder.Text.Trim().Trim('/') : "SeedToolBox"; folder.Text = s.Folder; service.Save(); };
        var passphrase = new PasswordBox { Width = 360, Style = DialogWindow.PasswordBoxStyle, Password = SyncCrypto.Unprotect(s.Passphrase) };
        passphrase.LostKeyboardFocus += (_, _) => { s.Passphrase = SyncCrypto.Protect(passphrase.Password); service.Save(); };

        body.Children.Add(Spaced(Ui.Row(Label("服务器地址"), url)));
        body.Children.Add(Spaced(Ui.Row(Label("账号"), user)));
        body.Children.Add(Spaced(Ui.Row(Label("密码"), password)));
        body.Children.Add(Spaced(Hint("坚果云：地址 https://dav.jianguoyun.com/dav/，密码填「账户信息 → 安全选项」里生成的应用密码")));
        body.Children.Add(Spaced(Ui.Row(Label("云端文件夹"), folder)));
        body.Children.Add(Spaced(Ui.Row(Label("同步密码"), passphrase)));
        body.Children.Add(Spaced(Hint("上传前用它加密，服务器上只有密文。每台电脑要填同一个；忘了就只能换个云端文件夹重新同步")));

        CheckBox Option(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 16, 6) };
            box.Click += (_, _) => { set(box.IsChecked == true); service.Save(); };
            return box;
        }
        var images = Option("包括剪贴板图片（流量较大）", s.ClipboardImages, v => s.ClipboardImages = v);
        images.IsEnabled = s.Clipboard;
        var what = new WrapPanel
        {
            Children =
            {
                Option("快速笔记", s.Notes, v => s.Notes = v),
                Option("剪贴板历史", s.Clipboard, v => { s.Clipboard = v; images.IsEnabled = v; }),
                images,
                Option("启动器项目", s.Launcher, v => s.Launcher = v),
                Option("其他设置和快捷键", s.Settings, v => s.Settings = v),
            },
        };
        body.Children.Add(Ui.Row(Label("同步内容"), what));
        body.Children.Add(Spaced(Hint("AI 的 API Key 不会同步。启动器项目和设置从云端下载后，重启程序才生效")));

        var interval = new ComboBox { Width = 160 };
        foreach (var minutes in Intervals) interval.Items.Add(minutes > 0 ? $"每 {minutes} 分钟" : "只在手动时");
        interval.SelectedIndex = Math.Max(0, Array.IndexOf(Intervals, s.IntervalMinutes));
        interval.SelectionChanged += (_, _) => { s.IntervalMinutes = Intervals[interval.SelectedIndex]; service.Save(); };
        body.Children.Add(Spaced(Ui.Row(Label("自动同步"), interval, Hint("  开启后启动时和改动后也会自动同步"))));

        var status = Ui.Status();
        Button now = null!;
        now = Ui.Button("立即同步", async () =>
        {
            // Take what is still in the boxes, in case one has focus
            s.Url = url.Text.Trim();
            s.User = user.Text.Trim();
            s.Password = SyncCrypto.Protect(password.Password);
            s.Passphrase = SyncCrypto.Protect(passphrase.Password);
            service.Save();
            await service.SyncAsync(manual: true);
        }, accent: true);
        var restartButton = Ui.Button("重启生效", restart);
        void Update()
        {
            now.IsEnabled = !service.Running;
            now.Content = service.Running ? "正在同步…" : "立即同步";
            restartButton.Visibility = service.RestartNeeded ? Visibility.Visible : Visibility.Collapsed;
            if (service.Running) Ui.SetStatus(status, "正在同步…");
            else if (service.LastResult.Length > 0) Ui.SetStatus(status, service.LastResult, service.LastFailed);
            else Ui.SetStatus(status, s.LastSync is { } last ? $"上次同步 {last:yyyy-MM-dd HH:mm}" : "还没有同步过");
        }
        service.StateChanged += () => panel.Dispatcher.BeginInvoke(new Action(Update));
        Update();
        body.Children.Add(Spaced(Ui.Row(now, restartButton, Spacer(), status)));
        return panel;
    }

    static TextBlock Label(string text) { var l = Ui.Label(text); l.Width = 96; return l; }
    static FrameworkElement Spacer() => new Border { Width = 8 };
    static TextBlock Hint(string text) => new() { Text = text, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 0, 8);
        return element;
    }
}
