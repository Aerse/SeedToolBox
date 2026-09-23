using System;
using System.Linq;
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
        Height = 760;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DialogWindow.ApplyTheme(this);
        SourceInitialized += (_, _) => WindowEffects.RoundCorners(this);

        var pages = new (string Group, string Glyph, string Name, Func<FrameworkElement> Create)[]
        {
            ("格式与编码", "\uE943", "代码格式化", () => new FormatPage()),
            ("格式与编码", "\uE9D5", "数据格式互转", () => new DataConvertPage()),
            ("格式与编码", "\uE8AB", "编码转换", () => new EncodePage()),
            ("格式与编码", "\uE8C8", "Base64 编解码", () => new Base64TextPage()),
            ("格式与编码", "\uE823", "时间戳/进制", () => new TimePage()),
            ("开发调试", "\uE721", "正则测试", () => new RegexPage()),
            ("开发调试", "\uE7C3", "文本对比", () => new DiffPage()),
            ("开发调试", "\uE8EC", "JWT 解码", () => new JwtPage()),
            ("开发调试", "\uE73E", "哈希校验", () => new HashPage()),
            ("开发调试", "\uE8D7", "UUID/密码", () => new GeneratorPage()),
            ("图片", "\uE8B9", "图片格式转换", () => new ImageConvertPage()),
            ("图片", "\uEB9F", "图片 Base64", () => new ImageBase64Page()),
            ("文件", "\uE8AC", "批量重命名", () => new RenamePage()),
            ("文件", "\uE7C4", "重复文件", () => new DuplicatePage()),
            ("文件", "\uEDA2", "空间占用", () => new DiskUsagePage()),
            ("文件", "\uE7B8", "文件压缩", () => new CompressPage()),
            ("系统与网络", "\uE701", "端口/网络", () => new NetworkPage()),
            ("系统与网络", "\uE968", "Hosts 管理", () => new HostsPage()),
        };
        var created = new FrameworkElement?[pages.Length];

        var nav = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = (Style)FindResource("NavItem"),
            Margin = new Thickness(8, 4, 8, 8),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(nav, ScrollBarVisibility.Disabled);
        var hint = DialogWindow.HintBrush;
        string? group = null;
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            if (p.Group != group)
            {
                group = p.Group;
                // Section captions are disabled items with a bare template, so they can't be selected or reached by keyboard
                nav.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                    Content = new TextBlock { Text = p.Group, FontSize = 12, Foreground = hint, Margin = new Thickness(12, i == 0 ? 6 : 14, 0, 4) },
                });
            }
            nav.Items.Add(new ListBoxItem
            {
                Tag = i,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = p.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                        new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
        }
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedItem is not ListBoxItem { Tag: int i }) return;
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
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().FirstOrDefault(x => x.Tag is int t && t == page);
        Loaded += (_, _) => { if (nav.SelectedItem != null) nav.ScrollIntoView(nav.SelectedItem); };
    }
}
