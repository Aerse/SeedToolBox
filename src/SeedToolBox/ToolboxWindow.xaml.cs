using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox;

/// <summary>Everything besides the launcher: screen tools, clipboard history, developer tools and settings, behind a sidebar.</summary>
public partial class ToolboxWindow : Window
{
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
    readonly ObservableCollection<ToolCommand> _tools = new();
    readonly LauncherData _data;
    readonly Action _requestSave;
    readonly Action<Action> _runHidden;
    bool _exiting;

    /// <param name="runHidden">Hides the app's windows, then runs a screen tool.</param>
    public ToolboxWindow(LauncherData data, Action requestSave, Action<Action> runHidden)
    {
        InitializeComponent();
        _data = data;
        _requestSave = requestSave;
        _runHidden = runHidden;
        Tools.ItemsSource = _tools;
        SourceInitialized += (_, _) => WindowEffects.RoundCorners(this);
        BuildNav();
        Loaded += (_, _) => { if (NavList.SelectedItem != null) NavList.ScrollIntoView(NavList.SelectedItem); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) Hide(); };
    }

    /// <summary>Shows the window, optionally on a given page.</summary>
    public void ShowAndActivate(string? page = null)
    {
        if (page != null) ShowPage(page);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Pages whose inputs are not remembered: they hold secrets, live data or their own files.</summary>
    static readonly HashSet<string> Unsaved = new() { "clipboard", "settings", "generator", "hosts", "network", "jwt", "imagebase64" };

    public void PrepareExit()
    {
        _exiting = true;
        PageState.Flush();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        // Keep the pages (and what was typed in them) for next time
        e.Cancel = true;
        Hide();
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    /// <param name="hide">Hide the windows first, for tools that work on the screen.</param>
    public void AddTool(string id, string glyph, string label, Action action, bool hide = true) =>
        _tools.Add(new ToolCommand(id, glyph, label, hide ? () => _runHidden(action) : action));

    public void SetToolHotkey(string id, string hotkey) => _tools.FirstOrDefault(t => t.Id == id)?.SetHotkey(hotkey);

    void OnToolClick(object sender, RoutedEventArgs e) => ((ToolCommand)((FrameworkElement)sender).DataContext).Action();

    const string FooterGroup = "footer";

    bool _buildingNav;

    /// <summary>Adds a top-level page (below the screen tools) for a feature owned by the app.</summary>
    public void AddPage(string id, string glyph, string name, Func<FrameworkElement> create)
    {
        _pages.Insert(_pages.FindLastIndex(p => p.Group == "") + 1, new Page(id, "", glyph, name, create));
        BuildNav();
    }

    /// <summary>Adds a page at the end of <paramref name="group"/>, creating the group if it is new.</summary>
    public void AddGroupPage(string id, string group, string glyph, string name, Func<FrameworkElement> create)
    {
        int last = _pages.FindLastIndex(p => p.Group == group);
        if (last < 0) last = _pages.FindLastIndex(p => p.Group != FooterGroup);
        _pages.Insert(last + 1, new Page(id, group, glyph, name, create));
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
                var caption = new TextBlock { Text = p.Group, FontSize = 12, Foreground = hint, Margin = new Thickness(12, 10, 0, 2) };
                // A thin line stands in for the caption when the sidebar is collapsed
                var line = new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Margin = new Thickness(8, 7, 8, 5) };
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

    /// <summary>Switches to a page by id; unknown ids fall back to the first page.</summary>
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
        ToolsView.Visibility = page.Id == "tools" ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = page.Create != null ? Visibility.Visible : Visibility.Collapsed;
        if (page.Create != null)
        {
            if (!_created.TryGetValue(page.Id, out var content))
            {
                _created[page.Id] = content = page.Create();
                if (content is FrameworkElement element && !Unsaved.Contains(page.Id)) PageState.Attach(element, page.Id);
            }
            PageHost.Content = content;
        }
        else PageHost.Content = null;
        if (!_buildingNav && _data.Window.LastPage != page.Id)
        {
            _data.Window.LastPage = page.Id;
            _requestSave();
        }
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _data.Window.NavCollapsed = !_data.Window.NavCollapsed;
        ApplyNavCollapsed();
        _requestSave();
    }

    void ApplyNavCollapsed()
    {
        bool collapsed = _data.Window.NavCollapsed;
        NavPanel.Width = collapsed ? NavCollapsedWidth : NavWidth;
        // The scroll bar would cover the icons in the narrow bar; the wheel still scrolls
        ScrollViewer.SetVerticalScrollBarVisibility(NavList, collapsed ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        foreach (var t in _navTexts) t.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // Captions and lines alternate in the list: show one of each pair
        for (int i = 0; i < _navCaptions.Count; i++)
            _navCaptions[i].Visibility = (i % 2 == 0) != collapsed ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Content = collapsed ? "\uE76C" : "\uE76B";
        CollapseButton.ToolTip = collapsed ? "展开侧边栏" : "收起侧边栏";
        System.Windows.Automation.AutomationProperties.SetName(CollapseButton, (string)CollapseButton.ToolTip);
    }

    /// <summary>Tools and pages whose name matches <paramref name="query"/>, best first; all of them for an empty query.</summary>
    public IReadOnlyList<(string Name, Action Open)> SearchTools(string query)
    {
        var q = ItemSearch.Normalize(query);
        return _tools.Select(t => (Name: t.Label, Open: t.Action))
            .Concat(_pages.Select(p => (Name: p.Name, Open: (Action)(() => ShowAndActivate(p.Id)))))
            .Select(x => (x, score: q.Length == 0 ? 0 : ItemSearch.Match(x.Name, q)))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score)
            .Select(x => x.x)
            .ToList();
    }
}
