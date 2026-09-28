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

/// <summary>An xTerminal-style terminal: local shells and SSH hosts in tabs and split panes, with SFTP, monitoring, snippets, tunnels and AI.</summary>
sealed partial class TerminalPage : DockPanel, IConnectPrompts
{
    sealed class Pane
    {
        public Tab Tab = null!;
        public TerminalView View = null!;
        public HostEntry? Host;
        public ShellProfile? Shell;
        public string? Directory;
        public SshConnection? Connection;
        public CancellationTokenSource? Cancel;
        public bool Connecting;
        public int Retries;
        public Border Frame = null!;
        public string HistoryKey => Host?.Id ?? "local";
        public bool Open => View.Session is { IsOpen: true };
        public string Title => Host != null ? Host.Title : Shell?.Name ?? "终端";
    }

    sealed class Tab
    {
        public readonly List<Pane> Panes = new();
        public readonly Grid Root = new();
        public bool Vertical;
        public bool Broadcast;
        public Pane? Active;
        public Border Chip = null!;
        public TextBlock Caption = null!, Dot = null!;
        public string? CustomTitle;
    }

    readonly Func<string, Task<string>>? _ai;
    readonly TerminalData _data;
    readonly List<ShellProfile> _shells;
    readonly List<Tab> _tabs = new();
    Tab? _current;
    double _zoom;
    readonly DispatcherTimer _saveTimer;
    readonly WrapPanel _tabStrip = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _content = new();
    readonly TextBlock _status = Ui.Status();
    readonly ToggleButton _broadcast = new() { Content = "广播输入", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "在一个分屏里输入，同时发到这个标签页的所有分屏" };
    readonly Border _welcome = null!;

    Window? Owner => Window.GetWindow(this);
    Pane? ActivePane => _current?.Active;

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

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(170, _data.SidebarWidth)), MinWidth = 150 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        var sidebar = BuildSidebar();
        root.Children.Add(sidebar);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.DragCompleted += (_, _) => { _data.SidebarWidth = root.ColumnDefinitions[0].ActualWidth; ScheduleSave(); };
        Grid.SetColumn(splitter, 1);
        root.Children.Add(splitter);

        var main = new DockPanel { Margin = new Thickness(6, 0, 0, 0) };
        main.Children.Add(BuildTopBar());
        main.Children.Add(BuildFindBar());
        _status.Margin = new Thickness(0, 4, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        main.Children.Add(_status);
        _welcome = BuildWelcome();
        _content.Children.Add(_welcome);
        main.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = _content });
        Grid.SetColumn(main, 2);
        root.Children.Add(main);

        _sideSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Collapsed };
        _sideSplitter.DragCompleted += (_, _) => { _data.PanelWidth = _sideColumn.ActualWidth; ScheduleSave(); };
        Grid.SetColumn(_sideSplitter, 3);
        root.Children.Add(_sideSplitter);
        _sideColumn = root.ColumnDefinitions[4];
        var side = BuildSidePanel();
        Grid.SetColumn(side, 4);
        root.Children.Add(side);
        Children.Add(root);

        _broadcast.Checked += (_, _) => { if (_current != null) { _current.Broadcast = true; UpdateFrames(_current); } };
        _broadcast.Unchecked += (_, _) => { if (_current != null) { _current.Broadcast = false; UpdateFrames(_current); } };
        RefreshTree();
        Application.Current.Exit += OnAppExit;
        UpdateStatus();
    }

    /// <summary>Open SSH connections, for asking before the window closes.</summary>
    public int ConnectedCount => _tabs.SelectMany(t => t.Panes).Count(p => p.Connection is { IsConnected: true });

    /// <summary>Closes every session and saves; the page is not used again afterwards.</summary>
    public TerminalData Data => _data;

    void OnAppExit(object? sender, ExitEventArgs e) => Shutdown();

    public void Shutdown()
    {
        Application.Current.Exit -= OnAppExit;
        foreach (var t in _tabs.ToList()) foreach (var p in t.Panes.ToList()) DisposePane(p);
        _tabs.Clear();
        _sftp?.CloseAll();
        SaveNow();
    }

    // ---------- top bar ----------

    FrameworkElement BuildTopBar()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var newLocal = ApiUi.Icon("\uE710", "新建本地终端（点右键选择 Shell）", () => OpenLocal(DefaultShell()));
        newLocal.ContextMenu = ShellMenu(s => OpenLocal(s));
        newLocal.PreviewMouseRightButtonUp += (_, e) => { newLocal.ContextMenu.PlacementTarget = newLocal; newLocal.ContextMenu.IsOpen = true; e.Handled = true; };
        tools.Children.Add(newLocal);
        tools.Children.Add(TextIcon("◫", "左右分屏", () => Split(false)));
        tools.Children.Add(TextIcon("⊟", "上下分屏", () => Split(true)));
        tools.Children.Add(_broadcast);
        tools.Children.Add(ApiUi.Icon("\uE721", "查找（Ctrl+Shift+F）", ShowFind));
        tools.Children.Add(ApiUi.Icon("\uE894", "清屏", () => ActivePane?.View.Clear()));
        tools.Children.Add(ApiUi.Icon("\uE8B7", "SFTP 文件", () => ToggleSide(0)));
        tools.Children.Add(ApiUi.Icon("\uE9D9", "服务器监控", () => ToggleSide(1)));
        tools.Children.Add(ApiUi.Icon("\uE943", "命令片段和历史", () => ToggleSide(2)));
        tools.Children.Add(ApiUi.Icon("\uE71B", "端口转发", ShowTunnels));
        tools.Children.Add(ApiUi.Icon("\uE99A", "AI 生成命令", () => AiGenerate()));
        tools.Children.Add(ApiUi.Icon("\uE7C3", "开始 / 停止记录会话日志", ToggleLog));
        tools.Children.Add(ApiUi.Icon("\uE713", "终端设置", EditSettings));
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(_tabStrip);
        DockPanel.SetDock(bar, Dock.Top);
        return bar;
    }

    static Button TextIcon(string text, string tip, Action click)
    {
        var b = ApiUi.Icon("", tip, click);
        b.Content = new TextBlock { Text = text, FontSize = 14, Margin = new Thickness(0, -3, 0, -1) };
        return b;
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
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        stack.Children.Add(new TextBlock { Text = "终端 / SSH", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock
        {
            Text = "双击左侧的主机连接服务器，或者打开一个本地终端。\n\n快捷键：Ctrl+Shift+C / V 复制粘贴，Ctrl+Shift+F 查找，Ctrl+加减号 缩放。\n输入时会根据历史命令和片段提示补全，按 → 或 Tab 采用。",
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

    // ---------- tabs ----------

    Tab NewTab()
    {
        var tab = new Tab();
        tab.Dot = new TextBlock { Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        tab.Caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "关闭" };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseTab(tab); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tab.Dot);
        row.Children.Add(tab.Caption);
        row.Children.Add(close);
        tab.Chip = new Border { Child = row, Padding = new Thickness(10, 5, 8, 5), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        tab.Chip.MouseLeftButtonDown += (_, _) => SelectTab(tab);
        tab.Chip.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("复制标签页", () => { if (tab.Active is { } p) Duplicate(p, null); });
        Item("重命名…", () =>
        {
            var name = TerminalDialogs.Ask(Owner, "重命名标签页", "标签页名称（留空恢复默认）", false, tab.Caption.Text);
            if (name != null) { tab.CustomTitle = name.Trim().Length > 0 ? name.Trim() : null; UpdateTab(tab); }
        });
        Item("重新连接", () => { foreach (var p in tab.Panes) Reconnect(p); });
        menu.Items.Add(new Separator());
        Item("关闭", () => CloseTab(tab));
        Item("关闭其他标签页", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); });
        Item("关闭右侧标签页", () => { foreach (var t in _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()) CloseTab(t); });
        tab.Chip.ContextMenu = menu;
        tab.Root.Visibility = Visibility.Collapsed;
        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Chip);
        _content.Children.Add(tab.Root);
        return tab;
    }

    void SelectTab(Tab? tab)
    {
        _current = tab;
        foreach (var t in _tabs)
        {
            var on = t == tab;
            t.Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            t.Chip.Background = on ? ApiUi.Res("CardBrush") : Brushes.Transparent;
            t.Chip.BorderBrush = on ? ApiUi.Res("CardBorderBrush") : Brushes.Transparent;
        }
        _welcome.Visibility = tab == null ? Visibility.Visible : Visibility.Collapsed;
        _broadcast.IsChecked = tab?.Broadcast == true;
        if (tab?.Active != null) tab.Active.View.FocusTerminal();
        OnActivePaneChanged();
    }

    void UpdateTab(Tab tab)
    {
        var p = tab.Active ?? tab.Panes.FirstOrDefault();
        var title = tab.CustomTitle ?? (p == null ? "终端" : p.Host != null ? p.Host.Title : p.View.Title.Length > 0 ? p.View.Title : p.Title);
        if (tab.Panes.Count > 1 && tab.CustomTitle == null) title += $" (+{tab.Panes.Count - 1})";
        tab.Caption.Text = title;
        tab.Chip.ToolTip = p?.Host != null ? p.Host.Address : p?.View.Title;
        var color = p?.Host?.Color ?? "";
        tab.Dot.Foreground = !tab.Panes.Any(x => x.Open) ? Brushes.Gray
            : color.Length > 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            : ApiUi.Frozen(0x2E, 0xCC, 0x71);
    }

    void CloseTab(Tab tab)
    {
        if (tab.Panes.Any(p => p.Connection != null && p.Open) && !ApiDialogs.Confirm(Owner, $"关闭「{tab.Caption.Text}」会断开连接，确定吗？"))
            return;
        foreach (var p in tab.Panes.ToList()) DisposePane(p);
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _tabStrip.Children.Remove(tab.Chip);
        _content.Children.Remove(tab.Root);
        if (_current == tab) SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
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

    async Task ConnectAsync(Pane pane)
    {
        var host = pane.Host!;
        if (pane.Connecting) return;
        pane.Connecting = true;
        pane.Cancel = new CancellationTokenSource();
        pane.View.WriteText($"\x1b[90m正在连接 {host.Address}{(host.JumpHostId.Length > 0 ? "（经跳板机）" : "")} …\x1b[0m\r\n");
        UpdateStatus();
        try
        {
            var connection = await SshConnection.OpenAsync(host, _data, this, pane.Cancel.Token);
            if (!pane.Tab.Panes.Contains(pane)) { connection.Dispose(); return; }
            pane.Connection = connection;
            pane.Retries = 0;
            var session = new SshSession(connection, pane.View.Cols, pane.View.Rows, ownsConnection: true);
            pane.View.Attach(session);
            session.Start();
            host.LastConnected = DateTime.Now;
            StartAutoTunnels(pane);
            ScheduleSave();
            if (pane == ActivePane) OnActivePaneChanged();
        }
        catch (OperationCanceledException)
        {
            pane.View.WriteText("\x1b[90m已取消。按回车重新连接。\x1b[0m\r\n");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException or ArgumentException or NotSupportedException)
        {
            Log.Info("SSH connect to " + host.Address + " failed: " + ex.Message);
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
        StopTunnels(pane);
        pane.View.Detach()?.Dispose();
        pane.Connection = null;
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
        StopTunnels(pane);
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
        StopTunnels(pane);
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
        var tab = NewTab();
        AddPane(tab, null, shell, directory);
        SelectTab(tab);
    }

    void OpenHost(HostEntry host)
    {
        var tab = NewTab();
        AddPane(tab, host, null);
        SelectTab(tab);
    }

    void Duplicate(Pane source, Tab? into)
    {
        var tab = into ?? NewTab();
        AddPane(tab, source.Host, source.Shell, source.Host == null ? source.View.Directory : null);
        SelectTab(tab);
    }

    void Split(bool vertical)
    {
        var pane = ActivePane;
        if (pane == null) { OpenLocal(DefaultShell()); return; }
        if (pane.Tab.Panes.Count >= 4) { Ui.SetStatus(_status, "一个标签页最多分 4 屏", true); return; }
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
        foreach (var t in _tabs) foreach (var p in t.Panes) p.View.Apply(_data.Settings, _zoom);
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
        Item("左右分屏", () => Split(false), pane.Tab.Panes.Count < 4);
        Item("上下分屏", () => Split(true), pane.Tab.Panes.Count < 4);
        Item("关闭这个分屏", () => ClosePane(pane), pane.Tab.Panes.Count > 1);
        Item(pane.Host != null ? "重新连接" : "重新启动", () => Reconnect(pane));
        Item(pane.View.IsLogging ? "停止记录日志" : "开始记录日志", () => { if (pane.View.IsLogging) pane.View.StopLog(); else StartLog(pane); UpdateStatus(); });
        if (pane.Host != null)
        {
            menu.Items.Add(new Separator());
            Item("在 SFTP 中打开当前目录", () => ToggleSide(0, true));
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
        foreach (var t in _tabs) foreach (var p in t.Panes) p.View.Apply(_data.Settings, _zoom);
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
            var tunnels = ActiveTunnelCount(p);
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
