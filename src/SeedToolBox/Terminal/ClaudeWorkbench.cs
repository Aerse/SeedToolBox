using System;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;
using SeedToolBox.Views;

namespace SeedToolBox.Terminal;

/// <summary>Claude Code in several folders at once: a folder list on the left, resizable split terminals on the right.</summary>
sealed class ClaudeWorkbench : Window
{
    static ClaudeWorkbench? _open;

    /// <summary>The terminal of one tab.</summary>
    sealed class Pane
    {
        public ClaudeTab Tab = null!;
        public ClaudeFolder Folder = null!;
        public TerminalView View = null!;
        public LocalSession? Session;
        /// <summary>Output arrived while another tab was in front.</summary>
        public bool Unread;
        public Action<byte[]>? OnOutput;
        public FolderWindow Window = null!;
    }

    readonly ClaudeWorkbenchData _data;
    readonly TerminalSettings _terminal;
    readonly Dictionary<string, Pane> _panes = new();
    readonly Border _area = new();
    readonly Canvas _canvas = new() { ClipToBounds = true };
    readonly TextBlock _empty = new() { Text = "在左边点一个文件夹，就在那里启动 Claude Code", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
    readonly StackPanel _list = new();
    readonly ColumnDefinition _sideColumn;
    readonly DispatcherTimer _saveTimer, _sessionTimer;
    bool _tracking;
    readonly CheckBox _autoBox = new() { Content = "自动整理", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "打开、关闭或最小化窗口时自动平铺" };
    Pane? _active;

    public static void Open()
    {
        if (_open != null)
        {
            if (!_open.IsVisible) _open.Show();
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new ClaudeWorkbench();
        _open.Show();
        _open.Activate();
    }

    /// <summary>At startup: reopens the workbench with its terminals if the user asked for that.</summary>
    public static void RestoreIfWanted()
    {
        if (ClaudeWorkbenchStore.Load() is { RestoreOnStartup: true }) Open();
    }

    /// <summary>The toolbox page: opens the window whenever the page is shown.</summary>
    public static FrameworkElement Launcher()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Claude 工作台在单独的窗口里打开", FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
        var button = Ui.Button("打开 Claude 工作台", Open, accent: true);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(button);
        panel.Loaded += (_, _) => Open();
        return panel;
    }

    ClaudeWorkbench()
    {
        Title = "Claude 工作台";
        MinWidth = 720;
        MinHeight = 420;
        _data = ClaudeWorkbenchStore.Load();
        _terminal = TerminalStore.Load().Settings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sessionTimer.Tick += async (_, _) => await TrackConversations();
        _sessionTimer.Start();

        var root = new Grid();
        _sideColumn = new ColumnDefinition { Width = new GridLength(Math.Max(160, _data.SidebarWidth)), MinWidth = 140 };
        root.ColumnDefinitions.Add(_sideColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var side = BuildSidebar();
        root.Children.Add(side);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        splitter.DragCompleted += (_, _) => { _data.SidebarWidth = _sideColumn.ActualWidth; ScheduleSave(); };
        Grid.SetColumn(splitter, 1);
        root.Children.Add(splitter);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        _area.Child = new Grid { Children = { _canvas, _empty } };
        _canvas.SizeChanged += (_, _) => Arrange();
        var area = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        area.Children.Add(toolbar);
        area.Children.Add(_area);
        Grid.SetColumn(area, 2);
        root.Children.Add(area);
        Content = root;
        DialogWindow.ApplyTheme(this);

        var b = _data.WindowBounds;
        if (b.Length == 4 && b[2] >= MinWidth && b[3] >= MinHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b[0]; Top = b[1]; Width = b[2]; Height = b[3];
        }
        else
        {
            Width = Math.Min(1600, SystemParameters.WorkArea.Width * 0.9);
            Height = Math.Min(960, SystemParameters.WorkArea.Height * 0.9);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_data.WindowMaximized) WindowState = WindowState.Maximized;

        // Nothing starts by itself: last time's tabs wait until their folder is clicked.
        // Layouts from before windows (split panes) only give their tabs back.
        foreach (var leaf in _data.Layout?.Leaves() ?? Enumerable.Empty<LayoutNode>())
        {
            if (leaf.Folder != null && FolderById(leaf.Folder) is { } folder)
                _data.Parked.Add(new ClaudeTab { Folder = folder.Id, SessionId = ClaudeWorkbenchStore.LatestConversation(folder.Path) });
            _data.Parked.AddRange(leaf.Tabs.Where(t => !t.Shell && FolderById(t.Folder) != null));
        }
        _data.Layout = null;
        _data.Parked.RemoveAll(t => FolderById(t.Folder) == null);
        _autoBox.IsChecked = _data.AutoArrange;
        _autoBox.Click += (_, _) => SetAutoArrange(_autoBox.IsChecked == true);
        RefreshList();
    }

    FrameworkElement BuildToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 4) };
        var tile = Ui.Button("平铺", Tile);
        tile.ToolTip = "把打开的窗口排满右边";
        var cascade = Ui.Button("层叠", Cascade);
        cascade.Margin = new Thickness(6, 0, 0, 0);
        bar.Children.Add(tile);
        bar.Children.Add(cascade);
        bar.Children.Add(_autoBox);
        return bar;
    }

    ClaudeFolder? FolderById(string? id) => _data.Folders.FirstOrDefault(f => f.Id == id);

    // ---------- sidebar ----------

    FrameworkElement BuildSidebar()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var add = Ui.Button("+ 添加文件夹", AddFolder, accent: true);
        DockPanel.SetDock(add, Dock.Left);
        top.Children.Add(add);
        var settings = new Button { Content = "", FontFamily = (FontFamily)FindResource("IconFont"), ToolTip = "设置", Style = (Style)FindResource("IconButton"), HorizontalAlignment = HorizontalAlignment.Right };
        settings.Click += (_, _) => EditSettings();
        top.Children.Add(settings);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);

        var hint = new TextBlock { Text = "单击：打开或切到它的窗口\n右键：新页签、编辑、删除\n也可以把文件夹直接拖进来", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        DockPanel.SetDock(hint, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // Whole window, so folders can be dropped anywhere outside the terminals
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        return panel;
    }

    void RefreshList()
    {
        _list.Children.Clear();
        if (_data.Folders.Count == 0)
        {
            var empty = new TextBlock { Text = "还没有文件夹，点上面的「添加文件夹」。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            _list.Children.Add(empty);
            return;
        }
        foreach (var folder in _data.Folders)
        {
            var count = _panes.Values.Count(p => p.Folder == folder && p.Session is { IsOpen: true });
            var minimized = _windows.TryGetValue(folder, out var fw) && fw.Minimized;
            var running = count > 0;
            var dot = new TextBlock { Text = "●", FontSize = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = running ? Brushes.LimeGreen : Brushes.Gray, ToolTip = running ? $"{count} 个在运行" + (minimized ? "（已最小化）" : "") : "未启动", Opacity = minimized ? 0.5 : 1 };
            var name = new TextBlock { Text = folder.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var path = new TextBlock { Text = folder.Path, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            path.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            var text = new StackPanel { Children = { name, path } };
            var row = new DockPanel { Children = { dot, text } };
            var item = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, ToolTip = folder.Path };
            item.SetResourceReference(Border.BackgroundProperty, _active?.Folder == folder ? "AccentBrush" : "CardBrush");
            if (_active?.Folder == folder) { name.Foreground = Brushes.White; path.Foreground = Brushes.White; }
            var f = folder;
            item.MouseLeftButtonUp += (_, _) => ShowFolder(f);
            item.ContextMenu = FolderMenu(f);
            _list.Children.Add(item);
        }
    }

    ContextMenu FolderMenu(ClaudeFolder folder)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action action) { var m = new MenuItem { Header = text }; m.Click += (_, _) => action(); return m; }
        menu.Items.Add(Item("新建 Claude 页签", () => OpenTab(folder)));
        menu.Items.Add(Item("新建终端页签", () => OpenTab(folder, shell: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("在资源管理器中打开", () => OpenExplorer(folder)));
        menu.Items.Add(Item("复制启动命令", () =>
        {
            var command = folder.Command.Length > 0 ? folder.Command : _data.DefaultCommand;
            try { Clipboard.SetText(_data.Shell == "cmd" ? $"cd /d \"{folder.Path}\" && {command}" : $"cd \"{folder.Path}\"; {command}"); } catch (System.Runtime.InteropServices.COMException) { }
        }));
        menu.Items.Add(Item("编辑…", () => EditFolder(folder)));
        menu.Items.Add(Item("上移", () => Move(folder, -1)));
        menu.Items.Add(Item("下移", () => Move(folder, 1)));
        menu.Items.Add(Item("删除", () => RemoveFolder(folder)));
        return menu;
    }

    static void OpenExplorer(ClaudeFolder folder)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + folder.Path + "\""); }
        catch (Exception ex) { Log.Error("Opening explorer failed", ex); }
    }

    void AddFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要启动 Claude Code 的文件夹" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        AddFolderPath(dialog.SelectedPath);
    }

    /// <summary>Folders dragged in from Explorer; for a file, the folder it is in.</summary>
    static List<string> DroppedFolders(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)).Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()!
            : new List<string>();

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolders(e).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        foreach (var path in DroppedFolders(e)) AddFolderPath(path);
        e.Handled = true;
    }

    void AddFolderPath(string path)
    {
        var existing = _data.Folders.FirstOrDefault(f => string.Equals(f.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ShowFolder(existing); return; }
        var folder = new ClaudeFolder { Path = path };
        _data.Folders.Add(folder);
        ScheduleSave();
        RefreshList();
        OpenTab(folder);
    }

    void EditFolder(ClaudeFolder folder)
    {
        var name = Ui.Field(); name.Text = folder.Name;
        var path = Ui.Field(); path.Text = folder.Path;
        var command = Ui.Field(); command.Text = folder.Command;
        var cont = new CheckBox { Content = "重新打开时每个页签接着自己的会话", IsChecked = folder.Continue, Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock { Text = $"启动命令留空则用默认的「{_data.DefaultCommand}」", FontSize = 11, Foreground = DialogWindow.HintBrush };
        var body = Form(("名称", name), ("文件夹", path), ("启动命令", command), (null, hint), (null, cont));
        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create("编辑文件夹", body, ok, DialogWindow.CancelButton());
        window.Owner = this;
        ok.Click += (_, _) =>
        {
            if (!Directory.Exists(path.Text.Trim())) { MessageBox.Show(window, "文件夹不存在", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            window.DialogResult = true;
        };
        if (window.ShowDialog() != true) return;
        folder.Name = name.Text.Trim();
        folder.Path = path.Text.Trim();
        folder.Command = command.Text.Trim();
        folder.Continue = cont.IsChecked == true;
        if (_windows.TryGetValue(folder, out var fw)) fw.Name.Text = folder.DisplayName;
        ScheduleSave();
        RefreshList();
        RefreshTabs();
    }

    void EditSettings()
    {
        var command = Ui.Field(360); command.Text = _data.DefaultCommand;
        var shells = LocalSession.DetectShells().Where(sh => sh.Id is "pwsh" or "powershell" or "cmd").ToList();
        // Names, not the profiles: the themed ComboBox would show the record's ToString
        var shellBox = new ComboBox { ItemsSource = shells.Select(sh => sh.Name).ToList(), SelectedIndex = Math.Max(0, shells.FindIndex(sh => sh.Id == _data.Shell)) };
        var restore = new CheckBox { Content = "启动工具箱时自动打开工作台", IsChecked = _data.RestoreOnStartup, Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock { Text = "例如 claude --dangerously-skip-permissions。可以写多条语句（按所选终端的语法）。每个页签会在末尾加上自己的 --session-id 或 --resume", FontSize = 11, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        var body = Form(("运行于", shellBox), ("默认启动命令", command), (null, hint), (null, restore));
        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create("工作台设置", body, ok, DialogWindow.CancelButton());
        window.Owner = this;
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return;
        _data.DefaultCommand = command.Text.Trim().Length > 0 ? command.Text.Trim() : "claude";
        _data.RestoreOnStartup = restore.IsChecked == true;
        if (shellBox.SelectedIndex >= 0) _data.Shell = shells[shellBox.SelectedIndex].Id;
        ScheduleSave();
    }

    static FrameworkElement Form(params (string? Label, FrameworkElement Field)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(16, 4, 16, 8), MinWidth = 420 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (rows[i].Label != null)
            {
                var label = Ui.Label(rows[i].Label!, 12);
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(label, i);
                grid.Children.Add(label);
            }
            rows[i].Field.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(rows[i].Field, i);
            Grid.SetColumn(rows[i].Field, 1);
            grid.Children.Add(rows[i].Field);
        }
        return grid;
    }

    void Move(ClaudeFolder folder, int by)
    {
        var i = _data.Folders.IndexOf(folder);
        var j = i + by;
        if (j < 0 || j >= _data.Folders.Count) return;
        _data.Folders.RemoveAt(i);
        _data.Folders.Insert(j, folder);
        ScheduleSave();
        RefreshList();
    }

    void RemoveFolder(ClaudeFolder folder)
    {
        if (!ApiDialogs.Confirm(this, $"从列表中删除「{folder.DisplayName}」？它的终端也会关闭，文件夹和会话记录不受影响。")) return;
        if (_windows.TryGetValue(folder, out var window)) RemoveWindow(window);
        _data.Folders.Remove(folder);
        _data.Parked.RemoveAll(t => t.Folder == folder.Id);
        RefreshList();
        ScheduleSave();
    }

    // ---------- terminals ----------

    string CommandFor(Pane pane)
    {
        var folder = pane.Folder;
        var command = folder.Command.Length > 0 ? folder.Command : _data.DefaultCommand;
        var id = pane.Tab.SessionId ??= Guid.NewGuid().ToString();
        // Resume the tab's own conversation once Claude has saved it; before that, start it under this id
        // so the tab finds it again later
        return command + (folder.Continue && ClaudeWorkbenchStore.HasConversation(id) ? " --resume " : " --session-id ") + id;
    }

    void OpenTab(ClaudeFolder folder, bool shell = false) => AddTab(new ClaudeTab { Folder = folder.Id, Shell = shell });

    void AddTab(ClaudeTab tab)
    {
        if (FolderById(tab.Folder) is not { } folder) return;
        var window = WindowFor(folder);
        var pane = StartPane(tab, folder, window);
        window.Current = pane;
        if (window.Minimized) Restore(window);
        ShowCurrent(window);
        Activate(pane);
        ScheduleSave();
    }

    /// <summary>Clicking a folder: brings its window up, or opens it with last time's tabs (or a new one).</summary>
    void ShowFolder(ClaudeFolder folder)
    {
        if (_windows.TryGetValue(folder, out var window) && window.Tabs.Count > 0)
        {
            if (window.Minimized) Restore(window);
            Activate(window.Current ?? window.Tabs[0]);
            return;
        }
        // Each comes back resuming its own conversation
        var parked = _data.Parked.Where(t => t.Folder == folder.Id).ToList();
        if (parked.Count == 0) { OpenTab(folder); return; }
        _data.Parked.RemoveAll(parked.Contains);
        foreach (var tab in parked) AddTab(tab);
    }

    Pane StartPane(ClaudeTab tab, ClaudeFolder folder, FolderWindow window)
    {
        // Native WebView2: composition copies every frame into WPF and gets slow with several terminals
        var view = new TerminalView();
        view.Apply(_terminal);
        var pane = new Pane { Tab = tab, Folder = folder, View = view, Window = window };
        view.Focused += _ => { if (_active != pane) Activate(pane, focus: false); };
        view.Input += (_, text) =>
        {
            if (pane.Session is { IsOpen: true }) pane.Session.Write(text);
            else if (text == "\r") Restart(pane);
        };
        view.Copy += (_, s) => { if (s.Length > 0) try { Clipboard.SetText(s); } catch (System.Runtime.InteropServices.COMException) { } };
        view.PasteRequested += _ => { try { if (Clipboard.ContainsText()) view.Paste(Clipboard.GetText()); } catch (System.Runtime.InteropServices.COMException) { } };
        view.Link += (_, url) => { if (url.StartsWith("http://") || url.StartsWith("https://")) try { System.Diagnostics.Process.Start(url); } catch (Exception ex) { Log.Error("Opening a link failed", ex); } };
        view.SessionClosed += _ => { view.WriteText("\x1b[90m按 Enter 重新启动\x1b[0m\r\n"); RefreshList(); RefreshTabs(); };
        view.Ready += _ => Launch(pane);
        window.Tabs.Add(pane);
        window.Body.Children.Add(view);
        _panes[tab.Id] = pane;
        return pane;
    }

    void Launch(Pane pane)
    {
        if (pane.Session != null) pane.Session.Output -= pane.OnOutput;
        pane.View.Detach()?.Dispose();
        pane.Session = null;
        if (!Directory.Exists(pane.Folder.Path))
        {
            pane.View.WriteText("\x1b[31m文件夹不存在：" + pane.Folder.Path + "\x1b[0m\r\n");
            return;
        }
        // Inherited when the toolbox itself was started from a Claude Code session; Claude would then
        // treat these as child sessions and not save their transcripts, so they could not be resumed
        // (session id, effort, context limits, messaging socket...): drop every one of them
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
            if (name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable(name, null);
        var shell = ShellFor(pane.Tab.Shell ? null : CommandFor(pane));
        ScheduleSave();
        try
        {
            var session = new LocalSession(shell, pane.View.Cols, pane.View.Rows, pane.Folder.Path);
            pane.View.Attach(session);
            pane.Session = session;
            // Raised on the reader thread: marks a tab in the background as having new output
            pane.OnOutput = _ => { if (!pane.Unread) Dispatcher.BeginInvoke(() => { if (!pane.Unread && pane != _active) { pane.Unread = true; RefreshTabs(); } }); };
            session.Output += pane.OnOutput;
            session.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            pane.View.WriteText("\x1b[31m无法启动：" + ex.Message + "\x1b[0m\r\n");
        }
        RefreshList();
        RefreshTabs();
    }

    /// <summary>The shell that runs the command (none: just the shell) and stays open when it exits.</summary>
    ShellProfile ShellFor(string? command)
    {
        var shells = LocalSession.DetectShells();
        var shell = shells.FirstOrDefault(s => s.Id == _data.Shell) ?? shells.FirstOrDefault(s => s.Id == "powershell") ?? shells[0];
        if (command == null) return shell;
        if (shell.Id == "cmd") return shell with { Arguments = "/k " + command };
        // Encoded, so the command's quotes and semicolons reach PowerShell exactly as typed
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        return shell with { Arguments = "-NoLogo -NoExit -EncodedCommand " + encoded };
    }

    void Restart(Pane pane)
    {
        if (pane.Session is { IsOpen: true } && !ApiDialogs.Confirm(this, $"重启「{FullTitle(pane)}」？正在运行的任务会被中断。")) return;
        pane.View.Reset();
        Launch(pane);
    }

    void Select(Pane pane)
    {
        pane.Window.Current = pane;
        ShowCurrent(pane.Window);
        Activate(pane);
    }

    void Activate(Pane pane, bool focus = true)
    {
        _active = pane;
        pane.Unread = false;
        Panel.SetZIndex(pane.Window.Frame, ++_z);
        // The terminals are native windows above WPF: their order follows the activation, not ZIndex
        foreach (var p in pane.Window.Tabs) p.View.BringToFront();
        RefreshList();
        RefreshTabs();
        if (focus) pane.View.FocusTerminal();
    }

    /// <summary>"Claude", "Claude 2", "终端"…: numbered within the window when there are several of a kind.</summary>
    static string TabTitle(Pane pane)
    {
        var title = pane.Tab.Shell ? "终端" : "Claude";
        var same = pane.Window.Tabs.Where(p => p.Tab.Shell == pane.Tab.Shell).ToList();
        return same.Count > 1 ? title + " " + (same.IndexOf(pane) + 1) : title;
    }

    static string FullTitle(Pane pane) => pane.Folder.DisplayName + " · " + TabTitle(pane);

    void CloseTab(Pane pane)
    {
        if (pane.Session is { IsOpen: true } && !pane.Tab.Shell &&
            !ApiDialogs.Confirm(this, $"关闭「{FullTitle(pane)}」？Claude 会结束，对话记录还在，可以用 /resume 找回。")) return;
        var window = pane.Window;
        var index = window.Tabs.IndexOf(pane);
        RemovePane(pane);
        if (window.Tabs.Count == 0) { RemoveWindow(window); ScheduleSave(); return; }
        if (window.Current == pane) window.Current = window.Tabs[Math.Min(index, window.Tabs.Count - 1)];
        ShowCurrent(window);
        Activate(window.Current!);
        ScheduleSave();
    }

    /// <summary>Ends the tab's terminal.</summary>
    void RemovePane(Pane pane)
    {
        _panes.Remove(pane.Tab.Id);
        pane.Window.Tabs.Remove(pane);
        pane.Window.Body.Children.Remove(pane.View);
        pane.View.Close();
        if (_active == pane) _active = null;
    }

    // ---------- windows ----------
    // One window per folder, floating in the area on the right. Positions are kept as shares of the area,
    // so the windows follow when the workbench is resized.

    sealed class FolderWindow
    {
        public ClaudeFolder Folder = null!;
        public readonly List<Pane> Tabs = new();
        public Pane? Current;
        public Border Frame = null!;
        public StackPanel Strip = null!;
        public Grid Body = null!;
        public Button MaxButton = null!;
        public TextBlock Name = null!;
        public bool Minimized, Maximized;
    }

    readonly Dictionary<ClaudeFolder, FolderWindow> _windows = new();
    int _z;
    const double TitleHeight = 32, MinWindowWidth = 280, MinWindowHeight = 160, WindowGap = 3;

    FolderWindow WindowFor(ClaudeFolder folder)
    {
        if (_windows.TryGetValue(folder, out var window)) return window;
        window = new FolderWindow { Folder = folder };
        BuildFrame(window);
        _windows[folder] = window;
        _canvas.Children.Add(window.Frame);
        if (folder.Window.Length != 4)
        {
            // Cascaded from the top left
            var i = (_windows.Count - 1) % 8;
            folder.Window = new[] { 0.03 * i, 0.04 * i, 0.6, 0.62 };
        }
        _empty.Visibility = Visibility.Collapsed;
        if (_data.AutoArrange) Tile(); else Arrange();
        return window;
    }

    void ShowCurrent(FolderWindow window)
    {
        foreach (var p in window.Tabs) p.View.Visibility = p == window.Current ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildFrame(FolderWindow window)
    {
        var name = new TextBlock { Text = window.Folder.DisplayName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = window.Folder.Path };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        Button? plus = null;
        plus = BarButton("", "新页签", () => ShowNewTabMenu(plus!, window));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, strip, plus } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(BarButton("", "最小化（在左边点文件夹恢复）", () => Minimize(window)));
        window.MaxButton = BarButton("", "最大化", () => ToggleMaximize(window));
        buttons.Children.Add(window.MaxButton);
        buttons.Children.Add(BarButton("", "关闭窗口", () => CloseWindow(window)));
        var header = new DockPanel { Height = TitleHeight, LastChildFill = false };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        header.SetResourceReference(Panel.BackgroundProperty, "CardBrush");
        DragByHeader(window, header);

        var body = new Grid { Background = new SolidColorBrush(TerminalThemes.Background(_terminal.Theme)) };
        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        content.Children.Add(body);

        var grid = new Grid { Children = { content } };
        // Edges and corners resize
        foreach (var (h, v, cursor) in new[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE), (HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE),
            (HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS), (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS),
            (HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE), (HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE),
            (HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW), (HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW),
        })
            grid.Children.Add(Grip(window, h, v, cursor));

        window.Frame = new Border { Child = grid, BorderThickness = new Thickness(1), ClipToBounds = true };
        window.Strip = strip;
        window.Name = name;
        window.Body = body;
        window.Frame.PreviewMouseDown += (_, _) => { if (window.Current != null && _active != window.Current) Activate(window.Current, focus: false); };
    }

    /// <summary>Dragging the title bar moves the window; a double click maximizes it.</summary>
    void DragByHeader(FolderWindow window, FrameworkElement header)
    {
        Point? start = null;
        Rect from = default;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ToggleMaximize(window); e.Handled = true; return; }
            if (window.Maximized) return;
            start = e.GetPosition(_canvas);
            from = RectOf(window);
            header.CaptureMouse();
            e.Handled = true;
        };
        header.MouseMove += (_, e) =>
        {
            if (start == null) return;
            var d = e.GetPosition(_canvas) - start.Value;
            var r = from;
            // Keep the title bar inside the area so the window can always be grabbed again
            r.X = Math.Max(80 - r.Width, Math.Min(_canvas.ActualWidth - 80, from.X + d.X));
            r.Y = Math.Max(0, Math.Min(_canvas.ActualHeight - TitleHeight, from.Y + d.Y));
            SetRect(window, r);
            Place(window);
        };
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (start == null) return;
            start = null;
            header.ReleaseMouseCapture();
            ScheduleSave();
        };
        header.LostMouseCapture += (_, _) => start = null;
    }

    Thumb Grip(FolderWindow window, HorizontalAlignment h, VerticalAlignment v, Cursor cursor)
    {
        var grip = new Thumb { Template = SplitterTemplate, Cursor = cursor, HorizontalAlignment = h, VerticalAlignment = v };
        if (h != HorizontalAlignment.Stretch) grip.Width = 6;
        if (v != VerticalAlignment.Stretch) grip.Height = 6;
        grip.DragStarted += (_, _) =>
        {
            if (!window.Maximized) return;
            // Resizing a maximized window starts from the full area
            window.Maximized = false;
            SetRect(window, new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
            Place(window);
        };
        grip.DragDelta += (_, e) =>
        {
            var r = RectOf(window);
            if (h == HorizontalAlignment.Left) { var w = Math.Max(MinWindowWidth, r.Width - e.HorizontalChange); r.X += r.Width - w; r.Width = w; }
            else if (h == HorizontalAlignment.Right) r.Width = Math.Max(MinWindowWidth, r.Width + e.HorizontalChange);
            if (v == VerticalAlignment.Top) { var ht = Math.Max(MinWindowHeight, r.Height - e.VerticalChange); r.Y += r.Height - ht; r.Height = ht; }
            else if (v == VerticalAlignment.Bottom) r.Height = Math.Max(MinWindowHeight, r.Height + e.VerticalChange);
            SetRect(window, r);
            Place(window);
        };
        grip.DragCompleted += (_, _) => ScheduleSave();
        return grip;
    }

    Rect RectOf(FolderWindow window)
    {
        var b = window.Folder.Window;
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        return b.Length == 4 ? new Rect(b[0] * w, b[1] * h, b[2] * w, b[3] * h) : new Rect(0, 0, w * 0.6, h * 0.6);
    }

    void SetRect(FolderWindow window, Rect r)
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        window.Folder.Window = new[] { r.X / w, r.Y / h, r.Width / w, r.Height / h };
    }

    /// <summary>Puts every window where its bounds say, for the current size of the area.</summary>
    void Arrange()
    {
        foreach (var window in _windows.Values) Place(window);
        _empty.Visibility = _windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Place(FolderWindow window)
    {
        window.Frame.Visibility = window.Minimized ? Visibility.Collapsed : Visibility.Visible;
        if (window.Minimized) return;
        var r = window.Maximized ? new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight) : RectOf(window);
        // A small gap so tiled windows don't touch
        var gap = window.Maximized ? 0 : WindowGap;
        Canvas.SetLeft(window.Frame, r.X + gap);
        Canvas.SetTop(window.Frame, r.Y + gap);
        window.Frame.Width = Math.Max(MinWindowWidth, r.Width - 2 * gap);
        window.Frame.Height = Math.Max(MinWindowHeight, r.Height - 2 * gap);
        window.MaxButton.Content = window.Maximized ? "" : "";
        window.MaxButton.ToolTip = window.Maximized ? "还原" : "最大化";
    }

    /// <summary>Lays the open windows out in a grid filling the area, in the order of the folder list.</summary>
    void Tile()
    {
        var shown = _data.Folders.Where(f => _windows.TryGetValue(f, out var w) && !w.Minimized).Select(f => _windows[f]).ToList();
        var n = shown.Count;
        if (n > 0)
        {
            var cols = (int)Math.Ceiling(Math.Sqrt(n));
            var rows = (int)Math.Ceiling(n / (double)cols);
            for (var i = 0; i < n; i++)
            {
                int row = i / cols, col = i % cols;
                // The last row may have fewer windows: they share its width
                var inRow = row == rows - 1 ? n - row * cols : cols;
                shown[i].Maximized = false;
                shown[i].Folder.Window = new[] { col / (double)inRow, row / (double)rows, 1.0 / inRow, 1.0 / rows };
            }
        }
        Arrange();
        ScheduleSave();
    }

    void Cascade()
    {
        var shown = _data.Folders.Where(f => _windows.TryGetValue(f, out var w) && !w.Minimized).Select(f => _windows[f]).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Maximized = false;
            var k = i % 8;
            shown[i].Folder.Window = new[] { 0.03 * k, 0.045 * k, 0.6, 0.62 };
            Panel.SetZIndex(shown[i].Frame, ++_z);
        }
        // Cascading is a manual layout: stop tiling over it
        SetAutoArrange(false);
        Arrange();
        ScheduleSave();
    }

    void SetAutoArrange(bool on)
    {
        _data.AutoArrange = on;
        _autoBox.IsChecked = on;
        if (on) Tile();
        ScheduleSave();
    }

    void Minimize(FolderWindow window)
    {
        window.Minimized = true;
        if (_active?.Window == window) _active = null;
        if (_data.AutoArrange) Tile(); else Arrange();
        RefreshList();
    }

    void Restore(FolderWindow window)
    {
        window.Minimized = false;
        if (_data.AutoArrange) Tile(); else Arrange();
    }

    void ToggleMaximize(FolderWindow window)
    {
        window.Maximized = !window.Maximized;
        Panel.SetZIndex(window.Frame, ++_z);
        Place(window);
    }

    /// <summary>The × of a window: its terminals end, and its Claude tabs come back resuming next time the folder is clicked.</summary>
    void CloseWindow(FolderWindow window)
    {
        var running = window.Tabs.Count(p => p.Session is { IsOpen: true } && !p.Tab.Shell);
        if (running > 0 && !ApiDialogs.Confirm(this, $"关闭「{window.Folder.DisplayName}」窗口？{running} 个 Claude 会结束，下次点这个文件夹时接着这些会话。")) return;
        foreach (var pane in window.Tabs.ToList())
        {
            if (!pane.Tab.Shell) _data.Parked.Add(pane.Tab);
            RemovePane(pane);
        }
        RemoveWindow(window);
        ScheduleSave();
    }

    void RemoveWindow(FolderWindow window)
    {
        foreach (var pane in window.Tabs.ToList()) RemovePane(pane);
        _canvas.Children.Remove(window.Frame);
        _windows.Remove(window.Folder);
        if (_data.AutoArrange) Tile(); else Arrange();
        if (_active == null && _windows.Values.Where(w => !w.Minimized && w.Current != null).OrderByDescending(w => Panel.GetZIndex(w.Frame)).FirstOrDefault() is { } next)
            Activate(next.Current!);
        RefreshList();
    }

    void ShowNewTabMenu(UIElement anchor, FolderWindow window)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var claude = new MenuItem { Header = "Claude 新会话" };
        claude.Click += (_, _) => OpenTab(window.Folder);
        var terminal = new MenuItem { Header = "终端" };
        terminal.Click += (_, _) => OpenTab(window.Folder, shell: true);
        menu.Items.Add(claude);
        menu.Items.Add(terminal);
        menu.IsOpen = true;
    }

    /// <summary>Refills every window's tabs: titles, the selected one, running state and new output.</summary>
    void RefreshTabs()
    {
        foreach (var window in _windows.Values)
        {
            window.Strip.Children.Clear();
            foreach (var pane in window.Tabs) window.Strip.Children.Add(TabChip(pane, pane == window.Current));
            var active = _active?.Window == window;
            if (active) window.Frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else window.Frame.BorderBrush = InactiveBorder;
        }
    }

    static readonly Brush InactiveBorder = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));

    FrameworkElement TabChip(Pane pane, bool selected)
    {
        var running = pane.Session is { IsOpen: true };
        var title = new TextBlock { Text = TabTitle(pane), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        if (selected) title.FontWeight = FontWeights.SemiBold;
        if (!running) title.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        var dot = new TextBlock { Text = "●", FontSize = 8, Foreground = Brushes.DodgerBlue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), ToolTip = "有新输出", Visibility = pane.Unread ? Visibility.Visible : Visibility.Collapsed };
        var close = new Button { Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0), Style = (Style)FindResource("IconButton"), ToolTip = "关闭页签" };
        close.Click += (_, _) => CloseTab(pane);
        var chip = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, dot, close } },
            Padding = new Thickness(10, 0, 4, 0), Height = TitleHeight - 2, BorderThickness = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand,
            ToolTip = pane.Tab.Shell ? pane.Folder.Path : "会话 " + pane.Tab.SessionId,
        };
        chip.BorderBrush = selected ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        if (selected) chip.SetResourceReference(Border.BackgroundProperty, "WindowBrush");
        else chip.Background = Brushes.Transparent;
        // Selects on press; handled so the title bar doesn't start moving the window
        chip.PreviewMouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not DependencyObject d || !IsInside(d, close)) { Select(pane); e.Handled = true; } };
        chip.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(pane); e.Handled = true; } };
        chip.ContextMenu = TabMenu(pane);
        return chip;
    }

    static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == ancestor) return true;
        return false;
    }

    ContextMenu TabMenu(Pane pane)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action action) { var m = new MenuItem { Header = text }; m.Click += (_, _) => action(); return m; }
        menu.Items.Add(Item("重启", () => Restart(pane)));
        if (!pane.Tab.Shell && pane.Tab.SessionId != null)
            menu.Items.Add(Item("复制会话 ID", () => { try { Clipboard.SetText(pane.Tab.SessionId); } catch (System.Runtime.InteropServices.COMException) { } }));
        menu.Items.Add(Item("在资源管理器中打开", () => OpenExplorer(pane.Folder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("关闭", () => CloseTab(pane)));
        menu.Items.Add(Item("关闭其他页签", () =>
        {
            var others = pane.Window.Tabs.Where(p => p != pane).ToList();
            var running = others.Count(p => p.Session is { IsOpen: true });
            if (running > 0 && !ApiDialogs.Confirm(this, $"关闭这个窗口里另外 {others.Count} 个页签？其中 {running} 个还在运行。")) return;
            foreach (var p in others) RemovePane(p);
            Select(pane);
            ScheduleSave();
        }));
        return menu;
    }

    static readonly ControlTemplate SplitterTemplate = CreateSplitterTemplate();

    static ControlTemplate CreateSplitterTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    Button BarButton(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, ToolTip = tip, Style = (Style)FindResource("IconButton"), Width = 30, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>Follows /resume and /clear: each Claude tab keeps the conversation its Claude is in now,
    /// found by the Claude process under the tab's shell.</summary>
    async System.Threading.Tasks.Task TrackConversations()
    {
        if (_tracking) return;
        var shells = _panes.Values.Where(p => !p.Tab.Shell && p.Session is { IsOpen: true }).Select(p => (Pane: p, Shell: p.Session!.ProcessId)).ToList();
        if (shells.Count == 0) return;
        _tracking = true;
        try
        {
            var found = await System.Threading.Tasks.Task.Run(() =>
            {
                var running = ClaudeWorkbenchStore.RunningConversations();
                var parents = running.Count > 0 ? ClaudeWorkbenchStore.ParentProcesses() : new Dictionary<int, int>();
                var result = new List<(Pane Pane, string Id)>();
                foreach (var (pane, shell) in shells)
                    foreach (var pair in running)
                    {
                        // Walk up from the Claude process to the tab's shell
                        var x = pair.Key;
                        for (var depth = 0; depth < 16 && x != shell && parents.TryGetValue(x, out var parent) && parent != x; depth++) x = parent;
                        if (x == shell) { result.Add((pane, pair.Value)); break; }
                    }
                return result;
            });
            var changed = false;
            foreach (var (pane, id) in found)
                if (pane.Tab.SessionId != id) { pane.Tab.SessionId = id; changed = true; }
            if (changed) { ScheduleSave(); RefreshTabs(); }
        }
        finally { _tracking = false; }
    }

    // ---------- saving ----------


    void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    void SaveNow()
    {
        // Open Claude tabs are saved as parked too, so they come back even if the toolbox never closes cleanly (crash, power loss)
        var open = _panes.Values.Where(p => !p.Tab.Shell && !_data.Parked.Contains(p.Tab)).Select(p => p.Tab).ToList();
        _data.Parked.AddRange(open);
        try { ClaudeWorkbenchStore.Save(_data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Saving the Claude workbench failed", ex); }
        finally { _data.Parked.RemoveAll(open.Contains); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var running = _panes.Values.Count(p => p.Session is { IsOpen: true });
        if (running > 0 && !App.Exiting)
        {
            var answer = MessageBox.Show(this,
                $"还有 {running} 个终端在运行，关闭窗口会结束它们，正在执行的任务会中断。\n布局会保存，下次打开时接着上次的会话。\n\n是：关闭    否：最小化，让它们继续运行    取消：返回",
                "关闭 Claude 工作台", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                if (answer == MessageBoxResult.No) WindowState = WindowState.Minimized;
                return;
            }
        }
        // Claude tabs still open come back next time their folder is clicked
        foreach (var p in _panes.Values) if (!p.Tab.Shell && !_data.Parked.Contains(p.Tab)) _data.Parked.Add(p.Tab);
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _data.WindowBounds = new[] { r.Left, r.Top, r.Width, r.Height };
        _data.WindowMaximized = WindowState == WindowState.Maximized;
        _saveTimer.Stop();
        _sessionTimer.Stop();
        SaveNow();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var p in _panes.Values) p.View.Close();
        _panes.Clear();
        _open = null;
        base.OnClosed(e);
    }
}
