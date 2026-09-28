using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>An xTerminal-style terminal: sessions in a green top bar, each with several terminals sharing one SSH connection, and a side panel for hosts, SFTP, monitoring, snippets and history.</summary>
sealed partial class TerminalPage : DockPanel, IConnectPrompts
{
    sealed class Pane
    {
        public Tab Tab = null!;
        public TerminalView View = null!;
        public HostEntry? Host;
        public ShellProfile? Shell;
        public string? Directory;
        /// <summary>Run once the shell is up (docker exec and the like).</summary>
        public string? Command;
        public SshConnection? Connection;
        public CancellationTokenSource? Cancel;
        public bool Connecting;
        public int Retries;
        public Border Frame = null!;
        public string HistoryKey => Host?.Id ?? "local";
        public bool Open => View.Session is { IsOpen: true };
        public string Title => Host != null ? Host.Title : Shell?.Name ?? "终端";
    }

    /// <summary>One terminal of a session (a sub-tab), possibly split into panes.</summary>
    sealed class Tab
    {
        public Session Session = null!;
        public int Number;
        public readonly List<Pane> Panes = new();
        public readonly Grid Root = new();
        public bool Vertical;
        public bool Broadcast;
        public Pane? Active;
        public Border Chip = null!;
        public TextBlock Caption = null!, Close = null!;
        public string? CustomTitle;
    }

    /// <summary>A host or local shell in the top bar; its terminals share one SSH connection.</summary>
    sealed class Session
    {
        public HostEntry? Host;
        public ShellProfile? Shell;
        public readonly List<Tab> Tabs = new();
        public Tab? Current;
        public readonly DockPanel Root = new();
        public readonly Grid Body = new();
        public readonly StackPanel Strip = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        public Border Chip = null!;
        public TextBlock Caption = null!, Dot = null!, Grip = null!, Close = null!;
        public string? CustomTitle;
        public SshConnection? Connection;
        public Task<SshConnection>? Pending;
        /// <summary>Cancels <see cref="Pending"/>; owned by the session, not by whichever pane started it.</summary>
        public CancellationTokenSource? PendingCancel;
        public int Counter;
        public string Title => CustomTitle ?? Host?.Title ?? Shell?.Name ?? "终端";
    }

    static readonly SolidColorBrush Brand = ApiUi.Frozen(0x3C, 0xBF, 0x6E);
    static readonly SolidColorBrush BrandText = ApiUi.Frozen(0x22, 0x9A, 0x50);
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly Func<string, Task<string>>? _ai;
    readonly TerminalData _data;
    readonly List<ShellProfile> _shells;
    readonly List<Session> _sessions = new();
    Session? _session;
    double _zoom;
    readonly DispatcherTimer _saveTimer;
    readonly StackPanel _sessionStrip = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Grid _content = new();
    readonly TextBlock _status = Ui.Status();
    readonly Border _welcome = null!;
    Point _chipDragStart;
    Session? _chipDrag;

    Window? Owner => Window.GetWindow(this);
    IEnumerable<Tab> AllTabs => _sessions.SelectMany(s => s.Tabs);
    Pane? ActivePane => _session?.Current?.Active;

    public TerminalPage(Func<string, Task<string>>? ai)
    {
        _ai = ai;
        _data = TerminalStore.Load();
        _shells = LocalSession.DetectShells();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        if (!TerminalView.RuntimeAvailable())
        {
            Children.Add(new TextBlock
            {
                Text = "终端需要 Microsoft Edge WebView2 运行时（Windows 11 自带）。\n请从 https://go.microsoft.com/fwlink/p/?LinkId=2124703 安装后重新打开。",
                Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        Children.Add(BuildTopBar());

        // rail | panel | splitter | terminals
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        _sideColumn = root.ColumnDefinitions[1];
        root.Children.Add(BuildRail());
        var side = BuildSidePanel();
        Grid.SetColumn(side, 1);
        root.Children.Add(side);
        _sideSplitter = new GridSplitter { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Collapsed };
        _sideSplitter.SetResourceReference(BackgroundProperty, "CardBorderBrush");
        _sideSplitter.DragCompleted += (_, _) => { _data.PanelWidth = _sideColumn.ActualWidth; ScheduleSave(); };
        Grid.SetColumn(_sideSplitter, 2);
        root.Children.Add(_sideSplitter);

        var main = new DockPanel();
        main.Children.Add(BuildFindBar());
        _status.Margin = new Thickness(8, 2, 8, 3);
        DockPanel.SetDock(_status, Dock.Bottom);
        main.Children.Add(_status);
        _welcome = BuildWelcome();
        _content.Children.Add(_welcome);
        main.Children.Add(_content);
        Grid.SetColumn(main, 3);
        root.Children.Add(main);
        Children.Add(root);

        RefreshTree();
        ShowSide(SideHosts);
        Application.Current.Exit += OnAppExit;
        UpdateStatus();
    }

    /// <summary>Open SSH connections, for asking before the window closes.</summary>
    public int ConnectedCount => _sessions.Count(s => s.Connection is { IsConnected: true });

    public TerminalData Data => _data;

    void OnAppExit(object? sender, ExitEventArgs e) => Shutdown();

    /// <summary>Closes every session and saves; the page is not used again afterwards.</summary>
    public void Shutdown()
    {
        Application.Current.Exit -= OnAppExit;
        foreach (var s in _sessions.ToList()) DisposeSession(s);
        _sessions.Clear();
        _sftp?.CloseAll();
        SaveNow();
    }

    // ---------- top bar ----------

    FrameworkElement BuildTopBar()
    {
        var bar = new DockPanel { Background = Brand, Height = 40 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(BarIcon("", "主机管理", () => ToggleSide(SideHosts)));
        var local = BarIcon("", "新建本地终端（右键选择 Shell）", () => OpenLocal(DefaultShell()));
        local.ContextMenu = ShellMenu(s => OpenLocal(s));
        local.PreviewMouseRightButtonUp += (_, e) => { local.ContextMenu.PlacementTarget = local; local.ContextMenu.IsOpen = true; e.Handled = true; };
        tools.Children.Add(local);
        tools.Children.Add(BarIcon("", "命令片段", () => ToggleSide(SideSnippets)));
        Button more = null!;
        more = BarIcon("", "工具", () => ShowMenu(ToolsMenu(), more));
        tools.Children.Add(more);
        DockPanel.SetDock(tools, Dock.Left);
        bar.Children.Add(tools);

        Button list = null!;
        list = BarIcon("", "所有会话", () => ShowMenu(SessionListMenu(), list));
        list.VerticalAlignment = VerticalAlignment.Center;
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(_sessionStrip);
        strip.Children.Add(list);
        bar.Children.Add(strip);
        DockPanel.SetDock(bar, Dock.Top);
        return bar;
    }

    static void ShowMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    static Button BarIcon(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15 },
            ToolTip = tip, Foreground = Brushes.White, Padding = new Thickness(9, 6, 9, 6), Style = FlatButton,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A button with only a faint hover shade, for the coloured bar and the rail.</summary>
    static readonly Style FlatButton = CreateFlatButton();

    static Style CreateFlatButton()
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80)), "bd"));
        template.Triggers.Add(hover);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Seal();
        return style;
    }

    ContextMenu ToolsMenu()
    {
        var menu = new ContextMenu();
        var pane = ActivePane;
        void Item(string header, Action a, bool enabled = true, bool check = false)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = check };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Item("新建终端", () => NewTerminal(_session), _session != null);
        Item("左右分屏", () => Split(false), pane != null);
        Item("上下分屏", () => Split(true), pane != null);
        Item("广播输入到所有分屏", ToggleBroadcast, pane?.Tab.Panes.Count > 1, pane?.Tab.Broadcast == true);
        menu.Items.Add(new Separator());
        Item("查找…（Ctrl+Shift+F）", ShowFind, pane != null);
        Item("清屏", () => pane?.View.Clear(), pane != null);
        Item(pane?.View.IsLogging == true ? "停止记录会话日志" : "开始记录会话日志", ToggleLog, pane != null);
        menu.Items.Add(new Separator());
        Item("端口转发…", ShowTunnels, pane?.Host is { IsSsh: true });
        Item("AI 生成命令…", () => AiGenerate());
        menu.Items.Add(new Separator());
        var import = new MenuItem { Header = "导入主机" };
        var source = ImportMenu();
        var items = source.Items.Cast<object>().ToList();
        source.Items.Clear();
        foreach (var i in items) import.Items.Add(i);
        menu.Items.Add(import);
        Item("终端设置…", EditSettings);
        return menu;
    }

    void ToggleBroadcast()
    {
        if (_session?.Current is not { } tab) return;
        tab.Broadcast = !tab.Broadcast;
        UpdateFrames(tab);
        UpdateStatus();
    }

    ContextMenu SessionListMenu()
    {
        var menu = new ContextMenu();
        foreach (var s in _sessions)
        {
            var mi = new MenuItem { Header = s.Title + (s.Tabs.Count > 1 ? $"（{s.Tabs.Count} 个终端）" : ""), IsChecked = s == _session };
            mi.Click += (_, _) => SelectSession(s);
            menu.Items.Add(mi);
        }
        if (_sessions.Count > 0) menu.Items.Add(new Separator());
        foreach (var s in _shells)
        {
            var mi = new MenuItem { Header = "新建 " + s.Name };
            mi.Click += (_, _) => OpenLocal(s);
            menu.Items.Add(mi);
        }
        return menu;
    }

    ContextMenu ShellMenu(Action<ShellProfile> open)
    {
        var menu = new ContextMenu();
        foreach (var s in _shells)
        {
            var item = new MenuItem { Header = s.Name };
            item.Click += (_, _) => open(s);
            menu.Items.Add(item);
        }
        return menu;
    }

    Border BuildWelcome()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440 };
        stack.Children.Add(new TextBlock { Text = "终端 / SSH", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock
        {
            Text = "双击左侧的主机连接服务器，或者打开一个本地终端。\n在一个会话里点终端标签后面的 + 可以再开终端，它们共用同一条 SSH 连接。\n\n快捷键：Ctrl+Shift+C / V 复制粘贴，Ctrl+Shift+F 查找，Ctrl+加减号 缩放。\n输入时会根据历史命令和片段提示补全，按 → 或 Tab 采用。",
            TextWrapping = TextWrapping.Wrap, Foreground = ApiUi.Res("HintTextBrush"), Margin = new Thickness(0, 0, 0, 14),
        });
        var buttons = new WrapPanel();
        foreach (var s in _shells.Take(4))
        {
            var b = Ui.Button(s.Name, () => OpenLocal(s), s == DefaultShell());
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }
        var add = Ui.Button("新建主机…", () => NewHost(""));
        add.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(add);
        stack.Children.Add(buttons);
        return new Border { Background = ApiUi.Res("CardBrush"), Child = stack };
    }

    ShellProfile DefaultShell() => _shells.FirstOrDefault(s => s.Id == _data.Settings.DefaultShell) ?? _shells[0];

    // ---------- sessions ----------

    static TextBlock Glyph(string glyph, double size) => new() { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    Session NewSession(HostEntry? host, ShellProfile? shell)
    {
        var s = new Session { Host = host, Shell = shell };
        s.Grip = Glyph("", 11);
        s.Grip.Margin = new Thickness(0, 0, 8, 0);
        s.Grip.Opacity = 0.7;
        s.Dot = new TextBlock { Text = "●", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        s.Caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        s.Close = Glyph("", 9);
        s.Close.Margin = new Thickness(16, 0, 0, 0);
        s.Close.Cursor = Cursors.Hand;
        s.Close.ToolTip = "关闭会话";
        s.Close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        s.Close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseSession(s); };
        var row = new DockPanel();
        row.Children.Add(s.Grip);
        row.Children.Add(s.Dot);
        DockPanel.SetDock(s.Close, Dock.Right);
        row.Children.Add(s.Close);
        row.Children.Add(s.Caption);
        s.Chip = new Border
        {
            Child = row, Padding = new Thickness(10, 0, 10, 0), Height = 32, MinWidth = 140, Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(6, 6, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom, AllowDrop = true, Background = Brushes.Transparent,
        };
        s.Chip.MouseLeftButtonDown += (_, e) => { SelectSession(s); _chipDragStart = e.GetPosition(this); _chipDrag = s; };
        s.Chip.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _chipDrag != s) return;
            if (Math.Abs((e.GetPosition(this) - _chipDragStart).X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
            _chipDrag = null;
            DragDrop.DoDragDrop(s.Chip, new DataObject(typeof(Session), s), DragDropEffects.Move);
        };
        s.Chip.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(Session)) is not Session moved || moved == s) return;
            _sessions.Remove(moved);
            _sessions.Insert(_sessions.IndexOf(s) + (e.GetPosition(s.Chip).X > s.Chip.ActualWidth / 2 ? 1 : 0), moved);
            _sessionStrip.Children.Clear();
            foreach (var x in _sessions) _sessionStrip.Children.Add(x.Chip);
        };
        s.Chip.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseSession(s); };
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("新建终端", () => NewTerminal(s));
        Item("复制会话", () => { if (s.Host != null) OpenHost(s.Host); else OpenLocal(s.Shell!); });
        Item("重命名…", () =>
        {
            var name = TerminalDialogs.Ask(Owner, "重命名会话", "名称（留空恢复默认）", false, s.Title);
            if (name != null) { s.CustomTitle = name.Trim().Length > 0 ? name.Trim() : null; UpdateSession(s); }
        });
        Item("重新连接", () => ReconnectSession(s));
        menu.Items.Add(new Separator());
        Item("关闭", () => CloseSession(s));
        Item("关闭其他会话", () => { foreach (var x in _sessions.Where(x => x != s).ToList()) CloseSession(x); });
        Item("关闭右侧会话", () => { foreach (var x in _sessions.Skip(_sessions.IndexOf(s) + 1).ToList()) CloseSession(x); });
        s.Chip.ContextMenu = menu;

        // The session's own strip: its terminals, +, and a close button at the far right.
        var head = new DockPanel { Height = 32 };
        var closeAll = ApiUi.Icon("", "关闭会话", () => CloseSession(s));
        closeAll.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(closeAll, Dock.Right);
        head.Children.Add(closeAll);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        line.Children.Add(s.Strip);
        line.Children.Add(ApiUi.Icon("", "新建终端（共用这条连接）", () => NewTerminal(s)));
        head.Children.Add(line);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        headBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        DockPanel.SetDock(headBorder, Dock.Top);
        s.Root.Children.Add(headBorder);
        s.Body.Background = Brushes.Black;
        s.Root.Children.Add(s.Body);
        s.Root.Visibility = Visibility.Collapsed;

        _sessions.Add(s);
        _sessionStrip.Children.Add(s.Chip);
        _content.Children.Add(s.Root);
        return s;
    }

    void SelectSession(Session? s)
    {
        _session = s;
        foreach (var x in _sessions)
        {
            var on = x == s;
            x.Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                x.Chip.SetResourceReference(Border.BackgroundProperty, "CardBrush");
                foreach (var t in new[] { x.Caption, x.Grip, x.Close }) t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }
            else
            {
                x.Chip.Background = Brushes.Transparent;
                foreach (var t in new[] { x.Caption, x.Grip, x.Close }) t.Foreground = Brushes.White;
            }
        }
        _welcome.Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
        if (s?.Current != null) SelectTab(s.Current);
        else OnActivePaneChanged();
    }

    void UpdateSession(Session s)
    {
        s.Caption.Text = s.Title;
        s.Chip.ToolTip = s.Host != null ? s.Host.Address : s.Shell?.Name;
        var color = s.Host?.Color ?? "";
        s.Dot.Foreground = !s.Tabs.SelectMany(t => t.Panes).Any(x => x.Open) ? Brushes.Gray
            : color.Length > 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            : ApiUi.Frozen(0x2E, 0xCC, 0x71);
    }

    void DisposeSession(Session s)
    {
        foreach (var p in s.Tabs.SelectMany(t => t.Panes).ToList()) DisposePane(p);
        DropConnection(s);
    }

    /// <summary>Stops the session's tunnels and closes its shared connection.</summary>
    void DropConnection(Session s)
    {
        StopTunnels(s);
        if (s.Connection != null) _systemInfo.Remove(s.Connection);
        s.Connection?.Dispose();
        s.Connection = null;
        s.PendingCancel?.Cancel();
        s.PendingCancel = null;
        s.Pending = null;
    }

    void CloseSession(Session s)
    {
        if (s.Connection is { IsConnected: true } && !ApiDialogs.Confirm(Owner, $"关闭「{s.Title}」会断开连接，确定吗？")) return;
        DisposeSession(s);
        var index = _sessions.IndexOf(s);
        _sessions.Remove(s);
        _sessionStrip.Children.Remove(s.Chip);
        _content.Children.Remove(s.Root);
        if (_session == s) SelectSession(_sessions.Count == 0 ? null : _sessions[Math.Min(index, _sessions.Count - 1)]);
        RefreshTree();
    }

    void ReconnectSession(Session s)
    {
        foreach (var p in s.Tabs.SelectMany(t => t.Panes)) { p.Cancel?.Cancel(); p.View.Detach()?.Dispose(); p.Connection = null; }
        DropConnection(s);
        foreach (var p in s.Tabs.SelectMany(t => t.Panes)) Reconnect(p);
    }

    // ---------- terminals of a session ----------

    Tab NewTab(Session s)
    {
        var tab = new Tab { Session = s, Number = s.Counter++ };
        tab.Caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        tab.Close = Glyph("", 8);
        tab.Close.Margin = new Thickness(8, 1, 0, 0);
        tab.Close.Cursor = Cursors.Hand;
        tab.Close.ToolTip = "关闭";
        tab.Close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        tab.Close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseTab(tab); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tab.Caption);
        row.Children.Add(tab.Close);
        tab.Chip = new Border { Child = row, Padding = new Thickness(8, 2, 7, 2), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, BorderThickness = new Thickness(1), Background = Brushes.Transparent };
        tab.Chip.MouseLeftButtonDown += (_, _) => SelectTab(tab);
        tab.Chip.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("新建终端", () => NewTerminal(s));
        Item("重命名…", () =>
        {
            var name = TerminalDialogs.Ask(Owner, "重命名终端", "名称（留空恢复默认）", false, tab.Caption.Text);
            if (name != null) { tab.CustomTitle = name.Trim().Length > 0 ? name.Trim() : null; UpdateTab(tab); }
        });
        Item("重新连接", () => { foreach (var p in tab.Panes) Reconnect(p); });
        menu.Items.Add(new Separator());
        Item("关闭", () => CloseTab(tab));
        Item("关闭其他终端", () => { foreach (var t in s.Tabs.Where(t => t != tab).ToList()) CloseTab(t); });
        tab.Chip.ContextMenu = menu;
        tab.Root.Visibility = Visibility.Collapsed;
        s.Current ??= tab;
        s.Tabs.Add(tab);
        s.Strip.Children.Add(tab.Chip);
        s.Body.Children.Add(tab.Root);
        return tab;
    }

    /// <summary>Another terminal in the session; for SSH it opens a new shell on the same connection.</summary>
    void NewTerminal(Session? s)
    {
        if (s == null) { OpenLocal(DefaultShell()); return; }
        var tab = NewTab(s);
        AddPane(tab, s.Host, s.Shell, s.Host == null ? s.Current?.Active?.View.Directory : null);
        SelectSession(s);
        SelectTab(tab);
    }

    /// <summary>A new terminal on the active SSH session that runs the command.</summary>
    void RunInNewTerminal(string title, string command)
    {
        if (ActivePane is not { Host: { IsSsh: true } } active) return;
        var s = active.Tab.Session;
        var tab = NewTab(s);
        tab.CustomTitle = title;
        var pane = AddPane(tab, s.Host, null);
        pane.Command = command;
        SelectTab(tab);
    }

    void SelectTab(Tab tab)
    {
        var s = tab.Session;
        s.Current = tab;
        foreach (var t in s.Tabs)
        {
            var on = t == tab;
            t.Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            t.Chip.BorderBrush = on ? Brand : Brushes.Transparent;
            foreach (var x in new[] { t.Caption, t.Close })
                if (on) x.Foreground = BrandText;
                else x.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        }
        tab.Active?.View.FocusTerminal();
        OnActivePaneChanged();
    }

    void UpdateTab(Tab tab)
    {
        var title = tab.CustomTitle ?? (tab.Number == 0 ? "终端" : "终端 " + tab.Number);
        if (tab.Panes.Count > 1 && tab.CustomTitle == null) title += $" (+{tab.Panes.Count - 1})";
        tab.Caption.Text = title;
        tab.Chip.ToolTip = tab.Active?.View.Title is { Length: > 0 } t ? t : null;
        UpdateSession(tab.Session);
    }

    void CloseTab(Tab tab)
    {
        var s = tab.Session;
        if (s.Tabs.Count == 1) { CloseSession(s); return; }
        foreach (var p in tab.Panes.ToList()) DisposePane(p);
        var index = s.Tabs.IndexOf(tab);
        s.Tabs.Remove(tab);
        s.Strip.Children.Remove(tab.Chip);
        s.Body.Children.Remove(tab.Root);
        if (s.Current == tab) SelectTab(s.Tabs[Math.Min(index, s.Tabs.Count - 1)]);
        UpdateSession(s);
    }

    // ---------- panes ----------

    Pane AddPane(Tab tab, HostEntry? host, ShellProfile? shell, string? directory = null)
    {
        var view = new TerminalView();
        var pane = new Pane { Tab = tab, View = view, Host = host, Shell = shell, Directory = directory };
        pane.Frame = new Border { Child = view, BorderThickness = new Thickness(1), Margin = new Thickness(1) };
        view.Apply(_data.Settings, _zoom);
        view.Input += (v, text) => OnInput(pane, text);
        view.Focused += _ => { if (tab.Active != pane) { tab.Active = pane; UpdateFrames(tab); UpdateTab(tab); OnActivePaneChanged(); } };
        view.TitleChanged += _ => UpdateTab(tab);
        view.SessionClosed += _ => OnSessionClosed(pane);
        view.Copy += (_, s) => { if (s.Length > 0) TrySetClipboard(s); };
        view.PasteRequested += _ => PasteInto(pane);
        view.Hotkey += (_, k) => OnHotkey(k);
        view.MenuRequested += (_, sel) => ShowPaneMenu(pane, sel);
        view.Link += (_, url) => OpenLink(url);
        view.Bell += _ => { if (_data.Settings.BellSound) SystemSounds.Beep.Play(); };
        view.CommandRun += (_, cmd) => { _data.AddHistory(pane.HistoryKey, cmd); ScheduleSave(); OnHistoryChanged(); };
        view.LineChanged += (_, line) => SuggestFor(pane, line);
        view.DirectoryChanged += (_, dir) => { if (pane == ActivePane) OnDirectoryChanged(pane, dir); };
        view.Found += ok => SetFindResult(ok);
        tab.Panes.Add(pane);
        tab.Active = pane;
        LayoutTab(tab);
        UpdateTab(tab);
        if (_data.Settings.LogAlways) StartLog(pane);
        _ = StartAsync(pane);
        return pane;
    }

    static Task WhenReady(TerminalView view)
    {
        if (view.IsReady) return Task.CompletedTask;
        var tcs = new TaskCompletionSource<bool>();
        void Handler(TerminalView _) { view.Ready -= Handler; tcs.TrySetResult(true); }
        view.Ready += Handler;
        return tcs.Task;
    }

    async Task StartAsync(Pane pane)
    {
        // Start the SSH handshake while the terminal view is still loading instead of after it.
        if (pane.Host is { IsSsh: true }) BeginConnect(pane);
        await WhenReady(pane.View);
        if (!pane.Tab.Panes.Contains(pane)) return;
        if (pane.Host != null) await ConnectAsync(pane);
        else StartLocal(pane);
    }

    void StartLocal(Pane pane)
    {
        try
        {
            var session = new LocalSession(pane.Shell!, pane.View.Cols, pane.View.Rows, pane.Directory);
            pane.View.Attach(session);
            session.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            pane.View.WriteText("\x1b[31m无法启动 " + pane.Shell!.Name + "：" + ex.Message + "\x1b[0m\r\n");
        }
        UpdateTab(pane.Tab);
        UpdateStatus();
    }

    void BeginConnect(Pane pane)
    {
        var owner = pane.Tab.Session;
        if (owner.Connection is { IsConnected: true } || owner.Pending != null) return;
        if (owner.Connection != null) DropConnection(owner);
        pane.Cancel = new CancellationTokenSource();
        owner.PendingCancel = new CancellationTokenSource();
        owner.Pending = SshConnection.OpenAsync(pane.Host!, _data, this, owner.PendingCancel.Token);
    }

    /// <summary>Waits for the session's shared connect; cancelling one pane only stops its own wait, unless no other pane is waiting.</summary>
    async Task<SshConnection> AwaitPending(Session owner, Task<SshConnection> pending, Pane pane)
    {
        var token = pane.Cancel!.Token;
        var cancelled = new TaskCompletionSource<bool>();
        using (token.Register(() => cancelled.TrySetResult(true)))
            if (await Task.WhenAny(pending, cancelled.Task) == pending) return await pending;
        if (!owner.Tabs.SelectMany(t => t.Panes).Any(p => p != pane && p.Connecting))
        {
            owner.PendingCancel?.Cancel();
            // Nobody will pick the result up; clear it and close the connection if it opened anyway.
            _ = pending.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (owner.Pending == pending) owner.Pending = null;
                if (t.Status == TaskStatus.RanToCompletion && owner.Connection != t.Result) t.Result.Dispose();
            })), TaskScheduler.Default);
        }
        throw new OperationCanceledException(token);
    }

    async Task ConnectAsync(Pane pane)
    {
        var host = pane.Host!;
        if (pane.Connecting) return;
        pane.Connecting = true;
        if (pane.Cancel == null || pane.Cancel.IsCancellationRequested) pane.Cancel = new CancellationTokenSource();
        pane.View.WriteText($"\x1b[90m正在连接 {host.Address}{(host.JumpHostId.Length > 0 ? "（经跳板机）" : "")} …\x1b[0m\r\n");
        UpdateStatus();
        try
        {
            if (!host.IsSsh)
            {
                ISession other = host.Protocol == Protocols.Serial
                    ? await Task.Run(() => new SerialSession(host))
                    : await TelnetSession.OpenAsync(host, host.Password.Length > 0 ? Secret.Reveal(host.Password) : "", pane.View.Cols, pane.View.Rows, pane.Cancel.Token);
                if (!pane.Tab.Panes.Contains(pane)) { other.Dispose(); return; }
                pane.Retries = 0;
                pane.View.Attach(other);
                other.Start();
                host.LastConnected = DateTime.Now;
                ScheduleSave();
                if (pane == ActivePane) OnActivePaneChanged();
                return;
            }
            var owner = pane.Tab.Session;
            var fresh = false;
            if (owner.Connection is not { IsConnected: true })
            {
                if (owner.Connection != null) DropConnection(owner);
                if (owner.Pending == null)
                {
                    owner.PendingCancel = new CancellationTokenSource();
                    owner.Pending = SshConnection.OpenAsync(host, _data, this, owner.PendingCancel.Token);
                }
                var pending = owner.Pending;
                SshConnection opened;
                try { opened = await AwaitPending(owner, pending, pane); }
                finally { if (owner.Pending == pending && pending.IsCompleted) owner.Pending = null; }
                if (owner.Connection != opened)
                {
                    fresh = true;
                    if (!_sessions.Contains(owner)) { opened.Dispose(); return; }
                    owner.Connection = opened;
                }
            }
            var connection = owner.Connection!;
            if (!pane.Tab.Panes.Contains(pane)) return;
            pane.Connection = connection;
            pane.Retries = 0;
            var session = new SshSession(connection, pane.View.Cols, pane.View.Rows, ownsConnection: false);
            pane.View.Attach(session);
            session.Start();
            if (pane.Command != null) { session.Write(pane.Command + "\r"); pane.Command = null; }
            host.LastConnected = DateTime.Now;
            if (fresh) StartAutoTunnels(owner, pane);
            ScheduleSave();
            if (pane == ActivePane) OnActivePaneChanged();
        }
        catch (OperationCanceledException)
        {
            pane.View.WriteText("\x1b[90m已取消。按回车重新连接。\x1b[0m\r\n");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException or ArgumentException or NotSupportedException)
        {
            Log.Info(host.Protocol + " connect to " + host.Address + " failed: " + ex.Message);
            pane.View.WriteText("\x1b[31m连接失败：" + ex.Message.Replace("\n", "\r\n") + "\x1b[0m\r\n\x1b[90m按回车重新连接。\x1b[0m\r\n");
        }
        finally
        {
            pane.Connecting = false;
            UpdateTab(pane.Tab);
            UpdateStatus();
            RefreshTree();
        }
    }

    void OnSessionClosed(Pane pane)
    {
        var connection = pane.Connection;
        pane.View.Detach()?.Dispose();
        pane.Connection = null;
        if (connection != null && !connection.IsConnected && pane.Tab.Session.Connection == connection) DropConnection(pane.Tab.Session);
        UpdateTab(pane.Tab);
        UpdateStatus();
        if (pane == ActivePane) OnActivePaneChanged();
        // Dropped connections (not "exit") come back by themselves, a few times with a growing delay.
        if (pane.Host is { AutoReconnect: true } && connection != null && !connection.IsConnected && pane.Retries < 5 && pane.Tab.Panes.Contains(pane))
        {
            var delay = 2 + pane.Retries * 3;
            pane.Retries++;
            pane.View.WriteText($"\x1b[90m{delay} 秒后自动重连（第 {pane.Retries} 次），按回车立即重连。\x1b[0m\r\n");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            timer.Tick += (_, _) => { timer.Stop(); if (!pane.Open && !pane.Connecting && pane.Tab.Panes.Contains(pane)) _ = ConnectAsync(pane); };
            timer.Start();
        }
        else pane.View.WriteText("\x1b[90m按回车" + (pane.Host != null ? "重新连接" : "重新启动") + "。\x1b[0m\r\n");
    }

    void Reconnect(Pane pane)
    {
        pane.Cancel?.Cancel();
        pane.View.Detach()?.Dispose();
        pane.Connection = null;
        pane.View.WriteText("\r\n");
        if (pane.Host != null) _ = ConnectAsync(pane);
        else StartLocal(pane);
    }

    void OnInput(Pane pane, string text)
    {
        if (!pane.Open)
        {
            if (text == "\r" && !pane.Connecting) { pane.Retries = 0; Reconnect(pane); }
            else if (text == "\x03" && pane.Connecting) pane.Cancel?.Cancel();
            return;
        }
        if (pane.Tab.Broadcast)
            foreach (var p in pane.Tab.Panes) { if (p.Open) p.View.Send(text); }
        else pane.View.Send(text);
    }

    void DisposePane(Pane pane)
    {
        pane.Cancel?.Cancel();
        pane.View.Close();
        pane.Connection = null;
    }

    void ClosePane(Pane pane)
    {
        var tab = pane.Tab;
        if (tab.Panes.Count == 1) { CloseTab(tab); return; }
        DisposePane(pane);
        tab.Panes.Remove(pane);
        if (tab.Active == pane) tab.Active = tab.Panes.Last();
        LayoutTab(tab);
        UpdateTab(tab);
        OnActivePaneChanged();
    }

    void LayoutTab(Tab tab)
    {
        var g = tab.Root;
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        var n = tab.Panes.Count;
        int cols = n <= 2 ? (tab.Vertical ? 1 : n) : 2, rows = n <= 2 ? (tab.Vertical ? n : 1) : 2;
        for (var c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition());
        for (var i = 0; i < n; i++)
        {
            var frame = tab.Panes[i].Frame;
            Grid.SetColumn(frame, n <= 2 ? (tab.Vertical ? 0 : i) : i % 2);
            Grid.SetRow(frame, n <= 2 ? (tab.Vertical ? i : 0) : i / 2);
            // Three panes: the third spans the bottom row.
            Grid.SetColumnSpan(frame, n == 3 && i == 2 ? 2 : 1);
            g.Children.Add(frame);
        }
        UpdateFrames(tab);
    }

    void UpdateFrames(Tab tab)
    {
        foreach (var p in tab.Panes)
            p.Frame.BorderBrush = tab.Panes.Count < 2 ? Brushes.Transparent
                : tab.Broadcast ? ApiUi.Frozen(0xE6, 0x7E, 0x22)
                : p == tab.Active ? ApiUi.Res("AccentBrush") : Brushes.Transparent;
    }

    // ---------- opening ----------

    public void OpenLocal(ShellProfile shell, string? directory = null)
    {
        var s = NewSession(null, shell);
        AddPane(NewTab(s), null, shell, directory);
        SelectSession(s);
    }

    void OpenHost(HostEntry host)
    {
        if (Protocols.IsFiles(host.Protocol))
        {
            FtpWindow.Open(Owner, host, () => Dispatcher.BeginInvoke(() => { host.LastConnected = DateTime.Now; ScheduleSave(); }));
            return;
        }
        var s = NewSession(host, null);
        AddPane(NewTab(s), host, null);
        SelectSession(s);
        if (host.IsSsh && (_side == SideHosts || _side < 0)) ShowSide(SideSftp);
    }

    void Duplicate(Pane source, Tab into)
    {
        AddPane(into, source.Host, source.Shell, source.Host == null ? source.View.Directory : null);
        SelectTab(into);
    }

    void Split(bool vertical)
    {
        var pane = ActivePane;
        if (pane == null) { OpenLocal(DefaultShell()); return; }
        if (pane.Tab.Panes.Count >= 4) { Ui.SetStatus(_status, "一个终端最多分 4 屏", true); return; }
        if (pane.Tab.Panes.Count == 1) pane.Tab.Vertical = vertical;
        Duplicate(pane, pane.Tab);
    }

    // ---------- clipboard, menu, hotkeys ----------

    static void TrySetClipboard(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
    }

    void PasteInto(Pane pane)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (text.Length == 0) return;
        text = text.Replace("\r\n", "\n");
        var lines = text.TrimEnd('\n').Count(c => c == '\n') + 1;
        if (lines > 1 && _data.Settings.ConfirmMultilinePaste && pane.Open
            && !ApiDialogs.Confirm(Owner, $"要粘贴 {lines} 行内容吗？每一行都可能被当作命令执行。\n\n" + (text.Length > 400 ? text.Substring(0, 400) + "…" : text)))
            return;
        pane.View.Paste(text);
        pane.View.FocusTerminal();
    }

    void OnHotkey(string key)
    {
        switch (key)
        {
            case "find": ShowFind(); break;
            case "zoom+": Zoom(1); break;
            case "zoom-": Zoom(-1); break;
            case "zoom0": _zoom = 0; Zoom(0); break;
        }
    }

    void Zoom(int delta)
    {
        _zoom = Math.Max(-8, Math.Min(24, _zoom + delta));
        foreach (var p in AllTabs.SelectMany(t => t.Panes)) p.View.Apply(_data.Settings, _zoom);
        Ui.SetStatus(_status, $"字号 {_data.Settings.FontSize + _zoom}");
    }

    void ShowPaneMenu(Pane pane, string selection)
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("复制", () => TrySetClipboard(selection), selection.Length > 0);
        Item("粘贴", () => PasteInto(pane));
        Item("全选", () => pane.View.SelectAll());
        Item("查找…", ShowFind);
        Item("清屏", () => pane.View.Clear());
        Item("重置终端", () => pane.View.Reset());
        menu.Items.Add(new Separator());
        Item("用 AI 解释" + (selection.Length > 0 ? "选中内容" : "最近的输出"), () => AiExplain(pane, selection));
        Item("AI 生成命令…", () => AiGenerate());
        Item("存为命令片段…", () => SaveSnippet(selection), selection.Length > 0);
        menu.Items.Add(new Separator());
        Item("新建终端", () => NewTerminal(pane.Tab.Session));
        Item("左右分屏", () => Split(false), pane.Tab.Panes.Count < 4);
        Item("上下分屏", () => Split(true), pane.Tab.Panes.Count < 4);
        Item("关闭这个分屏", () => ClosePane(pane), pane.Tab.Panes.Count > 1);
        Item(pane.Host != null ? "重新连接" : "重新启动", () => Reconnect(pane));
        Item(pane.View.IsLogging ? "停止记录日志" : "开始记录日志", () => { if (pane.View.IsLogging) pane.View.StopLog(); else StartLog(pane); UpdateStatus(); });
        if (pane.Host is { IsSsh: true })
        {
            menu.Items.Add(new Separator());
            Item("在 SFTP 中打开当前目录", () => ShowSide(SideSftp));
            Item("端口转发…", ShowTunnels);
        }
        menu.PlacementTarget = pane.View;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    void OpenLink(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Ui.SetStatus(_status, "打不开链接：" + ex.Message, true); }
    }

    // ---------- suggestions ----------

    static readonly string[] CommonCommands =
    {
        "ls -la", "cd ..", "pwd", "df -h", "du -sh *", "free -h", "top", "htop", "ps aux | grep ", "kill -9 ", "tail -f ", "tail -n 200 ", "cat ", "less ",
        "grep -rn ", "find . -name ", "chmod +x ", "chown -R ", "mkdir -p ", "rm -rf ", "cp -r ", "mv ", "tar -zxvf ", "tar -zcvf ", "unzip ", "vim ", "nano ",
        "systemctl status ", "systemctl restart ", "systemctl stop ", "systemctl start ", "journalctl -u ", "journalctl -f", "netstat -tlnp", "ss -tlnp", "ip addr",
        "ping ", "curl -I ", "curl -s ", "wget ", "docker ps", "docker ps -a", "docker images", "docker logs -f ", "docker exec -it ", "docker compose up -d",
        "docker compose down", "docker compose logs -f", "git status", "git pull", "git log --oneline -20", "git diff", "sudo ", "apt update", "apt install ",
        "yum install ", "uname -a", "cat /etc/os-release", "history", "whoami", "uptime", "nginx -t", "nginx -s reload", "crontab -l", "crontab -e", "exit",
    };

    void SuggestFor(Pane pane, string line)
    {
        if (!_data.Settings.Suggestions || line.Trim().Length < 2 || !pane.Open) { pane.View.Suggest(Array.Empty<(string, string)>()); return; }
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        void Add(IEnumerable<string> source, string kind)
        {
            foreach (var s in source)
            {
                if (list.Count >= 6) return;
                if (s.Length > line.Length && s.StartsWith(line, StringComparison.Ordinal) && seen.Add(s)) list.Add((s, kind));
            }
        }
        if (_data.History.TryGetValue(pane.HistoryKey, out var history)) Add(Enumerable.Reverse(history), "历史");
        Add(_data.Snippets.Where(s => !s.Command.Contains("{{") && !s.Command.Contains('\n')).Select(s => s.Command), "片段");
        foreach (var other in _data.History.Where(h => h.Key != pane.HistoryKey)) Add(Enumerable.Reverse(other.Value), "其他主机");
        if (pane.Host != null) Add(CommonCommands, "常用");
        pane.View.Suggest(list);
    }

    // ---------- find ----------

    readonly TextBox _findBox = Ui.Field(220);
    readonly CheckBox _findCase = new() { Content = "区分大小写", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly CheckBox _findRegex = new() { Content = "正则", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _findResult = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.IndianRed };
    DockPanel _findBar = null!;

    FrameworkElement BuildFindBar()
    {
        _findBar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_findBox);
        row.Children.Add(ApiUi.Icon("\uE70E", "上一个（Shift+Enter）", () => DoFind(true)));
        row.Children.Add(ApiUi.Icon("\uE70D", "下一个（Enter）", () => DoFind(false)));
        row.Children.Add(_findCase);
        row.Children.Add(_findRegex);
        row.Children.Add(_findResult);
        var close = ApiUi.Icon("\uE711", "关闭（Esc）", HideFind);
        DockPanel.SetDock(close, Dock.Right);
        _findBar.Children.Add(close);
        _findBar.Children.Add(row);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DoFind(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        };
        _findBox.TextChanged += (_, _) => { if (_findBox.Text.Length > 0) DoFind(false); else ActivePane?.View.ClearFind(); };
        DockPanel.SetDock(_findBar, Dock.Top);
        return _findBar;
    }

    void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    void HideFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        ActivePane?.View.ClearFind();
        ActivePane?.View.FocusTerminal();
    }

    void DoFind(bool back)
    {
        if (_findBox.Text.Length > 0) ActivePane?.View.Find(_findBox.Text, back, _findCase.IsChecked == true, _findRegex.IsChecked == true);
    }

    void SetFindResult(bool ok) => _findResult.Text = ok ? "" : "没有找到";

    // ---------- log ----------

    void ToggleLog()
    {
        var pane = ActivePane;
        if (pane == null) return;
        if (pane.View.IsLogging) { pane.View.StopLog(); Ui.SetStatus(_status, "已停止记录日志"); }
        else StartLog(pane);
        UpdateStatus();
    }

    void StartLog(Pane pane)
    {
        var folder = _data.Settings.LogFolder.Length > 0 ? _data.Settings.LogFolder : Path.Combine(AppPaths.Data, "TerminalLogs");
        var name = string.Concat((pane.Host?.Title ?? pane.Shell?.Name ?? "terminal").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        try
        {
            pane.View.StartLog(path);
            Ui.SetStatus(_status, "正在记录日志到 " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.SetStatus(_status, "无法记录日志：" + ex.Message, true); }
    }

    // ---------- settings and state ----------

    void EditSettings()
    {
        if (!TerminalDialogs.EditSettings(Owner, _data.Settings, _shells)) return;
        foreach (var p in AllTabs.SelectMany(t => t.Panes)) p.View.Apply(_data.Settings, _zoom);
        ScheduleSave();
    }

    void UpdateStatus()
    {
        var p = ActivePane;
        if (p == null) { Ui.SetStatus(_status, $"{_data.Hosts.Count} 台主机 · 检测到 {_shells.Count} 种本地终端"); return; }
        var parts = new List<string>();
        if (p.Host != null)
        {
            parts.Add(p.Host.Address);
            parts.Add(p.Connecting ? "连接中…" : p.Open ? "已连接" : "未连接");
            parts.Add(p.Host.Encoding.ToUpperInvariant());
            var tunnels = ActiveTunnelCount(p.Tab.Session);
            if (tunnels > 0) parts.Add($"{tunnels} 个端口转发");
        }
        else parts.Add(p.Shell?.Name + (p.Open ? "" : " · 已退出"));
        parts.Add($"{p.View.Cols}×{p.View.Rows}");
        if (p.View.IsLogging) parts.Add("● 记录日志中");
        if (p.Tab.Broadcast) parts.Add("广播输入已开启");
        Ui.SetStatus(_status, string.Join(" · ", parts));
    }

    void OnActivePaneChanged()
    {
        UpdateStatus();
        BindSidePanel();
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        try { lock (_data) TerminalStore.Save(_data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving terminal data failed", ex);
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
        }
    }

    // ---------- IConnectPrompts (called on the connecting thread) ----------

    bool IConnectPrompts.TrustHostKey(string endpoint, string fingerprint, string? known)
    {
        var trusted = Dispatcher.Invoke(() => TerminalDialogs.TrustHostKey(Owner, endpoint, fingerprint, known));
        if (trusted) Dispatcher.BeginInvoke(ScheduleSave);
        return trusted;
    }

    string? IConnectPrompts.Ask(string title, string prompt, bool secret) => Dispatcher.Invoke(() => TerminalDialogs.Ask(Owner, title, prompt, secret));
}
