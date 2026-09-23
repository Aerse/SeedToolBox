using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools;

/// <summary>Developer utilities: navigation on the left, the selected tool on the right.</summary>
sealed class DevToolsWindow : Window
{
    static DevToolsWindow? _current;

    public static void ShowSingle(Window? owner, int page = 0)
    {
        if (_current != null)
        {
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
            return;
        }
        _current = new DevToolsWindow(page);
        if (owner is { IsVisible: true }) _current.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _current.Closed += (_, _) => _current = null;
        _current.Show();
    }

    readonly ContentControl _page = new() { Margin = new Thickness(24, 20, 24, 20) };

    DevToolsWindow(int page)
    {
        Title = "开发工具";
        Width = 1000;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DialogWindow.ApplyTheme(this);
        SourceInitialized += (_, _) => WindowEffects.RoundCorners(this);

        var pages = new (string Glyph, string Name, Func<FrameworkElement> Create)[]
        {
            ("\uE943", "代码格式化", () => new FormatPage()),
            ("\uE8C8", "Base64 编解码", () => new Base64TextPage()),
            ("\uEB9F", "图片 Base64", () => new ImageBase64Page()),
            ("\uE968", "Hosts 管理", () => new HostsPage()),
            ("\uE7B8", "文件压缩", () => new CompressPage()),
            ("\uE8AB", "编码转换", () => new EncodePage()),
            ("\uE9D5", "数据格式互转", () => new DataConvertPage()),
            ("\uE8B9", "图片格式转换", () => new ImageConvertPage()),
            ("\uE823", "时间戳/进制", () => new TimePage()),
            ("\uE73E", "哈希校验", () => new HashPage()),
            ("\uE8D7", "UUID/密码", () => new GeneratorPage()),
            ("\uE8EC", "JWT 解码", () => new JwtPage()),
            ("\uE721", "正则测试", () => new RegexPage()),
            ("\uE7C3", "文本对比", () => new DiffPage()),
            ("\uE701", "端口/网络", () => new NetworkPage()),
            ("\uE8AC", "批量重命名", () => new RenamePage()),
            ("\uE7C4", "重复文件", () => new DuplicatePage()),
            ("\uEDA2", "空间占用", () => new DiskUsagePage()),
        };
        var created = new FrameworkElement?[pages.Length];

        var nav = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = (Style)FindResource("NavItem"),
            Margin = new Thickness(8, 8, 8, 8),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(nav, ScrollBarVisibility.Disabled);
        foreach (var p in pages)
        {
            nav.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = p.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                    new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
        nav.SelectionChanged += (_, _) =>
        {
            int i = nav.SelectedIndex;
            if (i < 0) return;
            // Pages are kept once created so their input survives switching
            _page.Content = created[i] ??= pages[i].Create();
        };

        var side = new Border { Width = 200, Child = nav };
        var content = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(8, 0, 0, 0),
            Child = _page,
        };
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(content);
        Content = root;
        nav.SelectedIndex = page;
    }
}
