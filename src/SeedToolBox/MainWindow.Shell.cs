using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.DevTools;

namespace SeedToolBox;

/// <summary>The sidebar and page switching; the launcher itself lives in MainWindow.xaml.cs.</summary>
public partial class MainWindow
{
    /// <summary>Current layout version, see <see cref="Launcher.WindowSettings.Layout"/>.</summary>
    const int LayoutVersion = 2;
    const double NavWidth = 200, NavCollapsedWidth = 56;

    sealed class Page(string id, string group, string glyph, string name, Func<FrameworkElement>? create)
    {
        public string Id { get; } = id;
        public string Group { get; } = group;
        public string Glyph { get; } = glyph;
        public string Name { get; } = name;
        public Func<FrameworkElement>? Create { get; } = create;
    }

    readonly List<Page> _pages = new()
    {
        new("launcher", "", "\uE80F", "启动器", null),
        new("tools", "", "\uE7A8", "屏幕工具", null),
        new("format", "格式与编码", "\uE943", "代码格式化", () => new FormatPage()),
        new("convert", "格式与编码", "\uE9D5", "数据格式互转", () => new DataConvertPage()),
        new("encode", "格式与编码", "\uE8AB", "编码转换", () => new EncodePage()),
        new("base64", "格式与编码", "\uE8C8", "Base64 编解码", () => new Base64TextPage()),
        new("time", "格式与编码", "\uE823", "时间戳/进制", () => new TimePage()),
        new("regex", "开发调试", "\uE721", "正则测试", () => new RegexPage()),
        new("diff", "开发调试", "\uE7C3", "文本对比", () => new DiffPage()),
        new("jwt", "开发调试", "\uE8EC", "JWT 解码", () => new JwtPage()),
        new("hash", "开发调试", "\uE73E", "哈希校验", () => new HashPage()),
        new("generator", "开发调试", "\uE8D7", "UUID/密码", () => new GeneratorPage()),
        new("image", "图片", "\uE8B9", "图片格式转换", () => new ImageConvertPage()),
        new("imagebase64", "图片", "\uEB9F", "图片 Base64", () => new ImageBase64Page()),
        new("rename", "文件", "\uE8AC", "批量重命名", () => new RenamePage()),
        new("duplicates", "文件", "\uE7C4", "重复文件", () => new DuplicatePage()),
        new("diskusage", "文件", "\uEDA2", "空间占用", () => new DiskUsagePage()),
        new("compress", "文件", "\uE7B8", "文件压缩", () => new CompressPage()),
        new("network", "系统与网络", "\uE701", "端口/网络", () => new NetworkPage()),
        new("hosts", "系统与网络", "\uE968", "Hosts 管理", () => new HostsPage()),
    };

    /// <summary>Pages are kept once created so their input survives switching.</summary>
    readonly Dictionary<string, FrameworkElement> _created = new();
    readonly List<TextBlock> _navTexts = new();
    readonly List<FrameworkElement> _navCaptions = new();

    const string FooterGroup = "footer";

    string CurrentPage => (NavList.SelectedItem ?? FooterList.SelectedItem) is ListBoxItem { Tag: Page p } ? p.Id : "launcher";

    bool _buildingNav;

    /// <summary>Adds a top-level page (below the launcher and screen tools) for a feature owned by the app.</summary>
    public void AddPage(string id, string glyph, string name, Func<FrameworkElement> create)
    {
        _pages.Insert(_pages.FindLastIndex(p => p.Group == "") + 1, new Page(id, "", glyph, name, create));
        BuildNav();
    }

    /// <summary>Adds a page pinned to the bottom of the sidebar, such as settings.</summary>
    public void AddFooterPage(string id, string glyph, string name, Func<FrameworkElement> create)
    {
        _pages.Add(new Page(id, FooterGroup, glyph, name, create));
        BuildNav();
    }

    void BuildNav()
    {
        _buildingNav = true;
        NavList.Items.Clear();
        FooterList.Items.Clear();
        _navTexts.Clear();
        _navCaptions.Clear();
        var hint = (Brush)FindResource("HintTextBrush");
        var iconFont = (FontFamily)FindResource("IconFont");
        string group = "";
        foreach (var p in _pages)
        {
            var list = p.Group == FooterGroup ? FooterList : NavList;
            if (list == NavList && p.Group != group)
            {
                group = p.Group;
                var caption = new TextBlock { Text = p.Group, FontSize = 12, Foreground = hint, Margin = new Thickness(12, 14, 0, 4) };
                // A thin line stands in for the caption when the sidebar is collapsed
                var line = new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Margin = new Thickness(8, 10, 8, 6) };
                _navCaptions.Add(caption);
                _navCaptions.Add(line);
                // Captions are disabled items with a bare template, so they can't be selected or reached by keyboard
                NavList.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                    Content = new Grid { Children = { caption, line } },
                });
            }
            var text = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _navTexts.Add(text);
            list.Items.Add(new ListBoxItem
            {
                Tag = p,
                ToolTip = p.Name,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = p.Glyph, FontFamily = iconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                        text,
                    },
                },
            });
        }
        ApplyNavCollapsed();
        // Pages added later may include the saved one, so don't overwrite it while building
        ShowPage(_data.Window.LastPage);
        _buildingNav = false;
    }

    /// <summary>Switches to a page by id; unknown ids fall back to the launcher.</summary>
    public void ShowPage(string id)
    {
        var items = NavList.Items.OfType<ListBoxItem>().Concat(FooterList.Items.OfType<ListBoxItem>());
        var item = items.FirstOrDefault(i => i.Tag is Page p && p.Id == id) ?? items.First(i => i.Tag is Page);
        (NavList.Items.Contains(item) ? NavList : FooterList).SelectedItem = item;
    }

    void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is not ListBoxItem { Tag: Page page }) return;
        // The two lists act as one: selecting in one clears the other
        (sender == NavList ? FooterList : NavList).SelectedItem = null;
        LauncherView.Visibility = page.Id == "launcher" ? Visibility.Visible : Visibility.Collapsed;
        ToolsView.Visibility = page.Id == "tools" ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = page.Create != null ? Visibility.Visible : Visibility.Collapsed;
        if (page.Create != null)
        {
            if (!_created.TryGetValue(page.Id, out var content)) _created[page.Id] = content = page.Create();
            PageHost.Content = content;
        }
        else PageHost.Content = null;
        if (!_buildingNav && _data.Window.LastPage != page.Id)
        {
            _data.Window.LastPage = page.Id;
            RequestSave();
        }
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _data.Window.NavCollapsed = !_data.Window.NavCollapsed;
        ApplyNavCollapsed();
        RequestSave();
    }

    void ApplyNavCollapsed()
    {
        bool collapsed = _data.Window.NavCollapsed;
        NavPanel.Width = collapsed ? NavCollapsedWidth : NavWidth;
        foreach (var t in _navTexts) t.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // Captions and lines alternate in the list: show one of each pair
        for (int i = 0; i < _navCaptions.Count; i++)
            _navCaptions[i].Visibility = (i % 2 == 0) != collapsed ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Content = collapsed ? "\uE76C" : "\uE76B";
        CollapseButton.ToolTip = collapsed ? "展开侧边栏" : "收起侧边栏";
        System.Windows.Automation.AutomationProperties.SetName(CollapseButton, (string)CollapseButton.ToolTip);
    }

    sealed class Feature
    {
        public Feature(string glyph, string name, Action open)
        {
            Glyph = glyph;
            Name = name;
            Open = open;
        }

        public string Glyph { get; }
        public string Name { get; }
        public Action Open { get; }
    }

    List<Feature> _features = new();

    /// <summary>Lists the pages and screen tools matching the launcher search, so it doubles as a global search.</summary>
    void UpdateFeatureResults(string query)
    {
        var candidates = _pages.Where(p => p.Id != "launcher")
            .Select(p => (p.Glyph, p.Name, Open: (Action)(() => ShowPage(p.Id))))
            .Concat(_tools.Select(t => (t.Glyph, Name: t.Label, Open: t.Action)));
        _features = query.Length == 0 ? new List<Feature>() : candidates
            .Select(c => (c, score: Launcher.ItemSearch.Match(c.Name, query)))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score)
            .Take(8)
            .Select(x => new Feature(x.c.Glyph, x.c.Name, () => { SearchBox.Clear(); x.c.Open(); }))
            .ToList();

        FeatureResults.Children.Clear();
        var iconFont = (FontFamily)FindResource("IconFont");
        foreach (var f in _features)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 5, 12, 5),
                ToolTip = "打开功能：" + f.Name,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = f.Glyph, FontFamily = iconFont, FontSize = 14, Foreground = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                        new TextBlock { Text = f.Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) => f.Open();
            FeatureResults.Children.Add(button);
        }
        FeatureResults.Visibility = _features.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
