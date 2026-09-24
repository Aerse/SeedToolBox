using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SeedToolBox.Core.Native;
using SeedToolBox.Core.Services;
using SeedToolBox.Host;
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox;

public partial class MainWindow : Window
{
    /// <summary>Current layout version, see <see cref="WindowSettings.Layout"/>.</summary>
    const int LayoutVersion = 3;

    readonly LauncherData _data;
    readonly ISettingsStore _settings;
    readonly DispatcherTimer _saveTimer;
    readonly DispatcherTimer _trimTimer;
    bool _exiting;

    // Drag-to-reorder state
    const string ItemFormat = "SeedToolBox.LaunchItem";
    const string GroupFormat = "SeedToolBox.ItemGroup";
    Point _pressPoint;
    LaunchItem? _pressedItem;
    ItemGroup? _pressedGroup;
    FrameworkElement? _pressedGroupElement;
    DragAdorner? _dragAdorner;

    // Search state
    const double TileSlotWidth = 80; // tile width + margins
    List<LaunchItem> _results = new();
    int _highlight = -1;

    public MainWindow(LauncherData data, ISettingsStore settings)
    {
        InitializeComponent();
        _data = data;
        _settings = settings;
        DataContext = data;

        // Debounce saves so dragging/resizing doesn't hammer the disk
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();

        ApplyWindowSettings();
        GroupList.SelectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            UpdateViewButtons();
        };
        SourceInitialized += (_, _) => WindowEffects.RoundCorners(this);
        GroupList.SelectedIndex = 0;

        LocationChanged += (_, _) => RequestSave();
        SizeChanged += (_, _) => RequestSave();

        // Release memory shortly after hiding to tray; cancelled if shown again
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); MemoryTrimmer.Trim(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _trimTimer.Stop();
            else { _trimTimer.Start(); SearchBox.Clear(); }
        };

        AppIndex.Loaded += () => Dispatcher.BeginInvoke(new Action(() => { if (IsSearching) UpdateSearch(); }));
        AppIndex.StartLoading();
    }

    ItemGroup? CurrentGroup => GroupList.SelectedItem as ItemGroup;

    #region Window state

    void ApplyWindowSettings()
    {
        var w = _data.Window;
        if (w.Layout < LayoutVersion)
        {
            // Layout 2 had a sidebar and a wide window; the tools have since moved to the toolbox window
            if (w.Layout == 2)
            {
                w.Width = 460;
                w.Height = 640;
                w.Left = w.Top = null;
            }
            w.Layout = LayoutVersion;
        }
        Width = w.Width;
        Height = w.Height;
        if (w.Left is double left && w.Top is double top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        ResizeMode = w.SizeLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
    }

    static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 10 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    public void SetSizeLocked(bool locked)
    {
        _data.Window.SizeLocked = locked;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResize;
        RequestSave();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowAndActivate();
    }

    /// <summary>Hotkey: bring the window to front, or hide it if it's already in front.</summary>
    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide();
        else ShowAndActivate();
    }

    /// <param name="save">False when the data files were just replaced (restore) and must not be overwritten.</param>
    public void PrepareExit(bool save = true)
    {
        _exiting = true;
        foreach (var hotkey in _itemHotkeys.Values) hotkey.Dispose();
        _itemHotkeys.Clear();
        if (save) SaveNow();
        else _saveTimer.Stop();
    }

    /// <summary>Writes pending changes now, e.g. before a backup.</summary>
    public void PrepareSave() => SaveNow();

    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        _saveTimer.Stop();
        if (WindowState == WindowState.Normal && IsLoaded)
        {
            _data.Window.Left = Left;
            _data.Window.Top = Top;
            _data.Window.Width = Width;
            _data.Window.Height = Height;
        }
        try
        {
            _settings.Save(LauncherData.SettingsName, _data);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save launcher settings", ex);
            MessageBox.Show($"保存配置失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        // Closing the window only hides it; exit from the tray menu
        e.Cancel = true;
        Hide();
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    #endregion

    /// <summary>The title bar button that opens the toolbox window.</summary>
    public event Action? ToolboxRequested;

    void OnToolboxClick(object sender, RoutedEventArgs e) => ToolboxRequested?.Invoke();

    /// <summary>Adds an icon button to the bar under the items.</summary>
    public void AddQuickButton(string glyph, string label, Action action, Func<string>? hotkey = null)
    {
        var keys = new System.Windows.Controls.TextBlock { FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 10, Opacity = 0.6, Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        if (hotkey != null)
        {
            void Refresh() { var k = hotkey(); keys.Text = k; keys.Visibility = k.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
            Refresh();
            Activated += (_, _) => Refresh();
        }
        else keys.Visibility = Visibility.Collapsed;
        var button = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 76,
            Height = 62,
            Margin = new Thickness(2, 0, 2, 0),
            ToolTip = label,
            Content = new System.Windows.Controls.StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center },
                    new System.Windows.Controls.TextBlock { Text = label, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                    keys,
                },
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        QuickBar.Children.Add(button);
    }

    #region Views

    void UpdateViewButtons()
    {
        var view = CurrentGroup?.View;
        LargeViewButton.IsChecked = view is not (ItemGroup.ViewSmall or ItemGroup.ViewList);
        SmallViewButton.IsChecked = view == ItemGroup.ViewSmall;
        ListViewButton.IsChecked = view == ItemGroup.ViewList;
    }

    void OnViewChecked(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group) return;
        var view = (string)((FrameworkElement)sender).Tag;
        if (group.View == view) return;
        group.View = view;
        RequestSave();
    }

    /// <summary>The wheel over the tabs switches group.</summary>
    void OnGroupMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int index = GroupList.SelectedIndex + (e.Delta < 0 ? 1 : -1);
        if (index >= 0 && index < _data.Groups.Count)
        {
            GroupList.SelectedIndex = index;
            GroupList.ScrollIntoView(GroupList.SelectedItem);
        }
        e.Handled = true;
    }

    #endregion

    #region Adding items

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ItemFormat) || e.Data.GetDataPresent(GroupFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths) AddPath(path);
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && IconHelper.IsUrl(text.Trim()))
        {
            AddPath(text.Trim());
        }
    }

    void OnAddFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加程序/文件",
            Filter = "程序和快捷方式|*.exe;*.lnk;*.bat;*.cmd|所有文件|*.*",
            Multiselect = true,
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog(this) == true)
            foreach (var path in dialog.FileNames) AddPath(path);
    }

    void OnAddUrl(object sender, RoutedEventArgs e)
    {
        var url = InputDialog.Show(this, "添加网址", "https://");
        if (url == null) return;
        if (!IconHelper.IsUrl(url))
        {
            MessageBox.Show("请输入以 http:// 或 https:// 开头的网址", "SeedToolBox");
            return;
        }
        AddPath(url);
    }

    void AddPath(string path)
    {
        if (CurrentGroup is not { } group) return;
        if (group.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        group.Items.Add(new LaunchItem { Name = DefaultName(path), Path = path });
        UpdateEmptyHint();
        RequestSave();
    }

    static string DefaultName(string path)
    {
        if (IconHelper.IsUrl(path)) return new Uri(path).Host;
        if (Directory.Exists(path)) return new DirectoryInfo(path).Name;
        return Path.GetFileNameWithoutExtension(path);
    }

    void UpdateEmptyHint()
    {
        if (IsSearching)
        {
            EmptyHint.Text = "没有找到匹配的项目";
            EmptyHint.Visibility = _entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            EmptyHint.Text = "把程序、快捷方式、文件夹或网址\n拖到这里即可添加";
            EmptyHint.Visibility = CurrentGroup is { Items.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    #endregion

    #region Item actions

    static LaunchItem ItemOf(object sender) => (LaunchItem)((FrameworkElement)sender).DataContext;

    void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        // Only launch if the press also started on this tile
        var item = ItemOf(sender);
        if (_pressedItem != item) return;
        _pressedItem = null;
        Launch(item);
    }

    void Launch(LaunchItem item, bool asAdmin = false)
    {
        if (!ProcessLauncher.Launch(item, asAdmin)) return;
        item.RunCount++;
        item.LastRun = DateTime.Now;
        RequestSave();
        // A search is a one-shot: get out of the way once something is launched
        if (IsSearching) Hide();
    }

    ItemGroup? GroupOf(LaunchItem item) => _data.Groups.FirstOrDefault(g => g.Items.Contains(item));

    void OnItemOpen(object sender, RoutedEventArgs e) => Launch(ItemOf(sender));

    void OnItemRunAsAdmin(object sender, RoutedEventArgs e) => Launch(ItemOf(sender), asAdmin: true);

    void OnItemOpenLocation(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (IconHelper.IsUrl(item.Path)) ProcessLauncher.Launch(item);
        else ProcessLauncher.OpenLocation(item.Path);
    }

    void OnItemRename(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (InputDialog.Show(this, "重命名", item.Name) is { } name)
        {
            item.Name = name;
            RequestSave();
        }
    }

    void OnItemProperties(object sender, RoutedEventArgs e)
    {
        if (PropertiesDialog.Show(this, ItemOf(sender))) RequestSave();
    }

    void OnItemDelete(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (MessageBox.Show($"确定删除「{item.Name}」吗？\n（只删除快捷项，不会删除原文件）", "SeedToolBox",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        GroupOf(item)?.Items.Remove(item);
        if (!AllItems.Contains(item)) UnregisterItemHotkey(item);
        if (IsSearching) UpdateSearch();
        UpdateEmptyHint();
        RequestSave();
    }

    #endregion

    #region Group actions

    void OnAddGroup(object sender, RoutedEventArgs e)
    {
        if (InputDialog.Show(this, "新建分组", "新分组") is not { } name) return;
        var group = new ItemGroup { Name = name };
        _data.Groups.Add(group);
        GroupList.SelectedItem = group;
        GroupList.ScrollIntoView(group);
        RequestSave();
    }

    void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group) return;
        if (InputDialog.Show(this, "重命名分组", group.Name) is { } name)
        {
            group.Name = name;
            RequestSave();
        }
    }

    void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group) return;
        if (_data.Groups.Count <= 1)
        {
            MessageBox.Show("至少要保留一个分组", "SeedToolBox");
            return;
        }
        if (group.Items.Count > 0 &&
            MessageBox.Show($"分组「{group.Name}」里有 {group.Items.Count} 个项目，确定删除吗？", "SeedToolBox",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        int index = GroupList.SelectedIndex;
        _data.Groups.Remove(group);
        foreach (var item in group.Items.Except(AllItems).ToList()) UnregisterItemHotkey(item);
        GroupList.SelectedIndex = Math.Min(index, _data.Groups.Count - 1);
        RequestSave();
    }

    #endregion

    #region Drag to reorder

    bool IsDragGesture(MouseEventArgs e)
    {
        var delta = e.GetPosition(this) - _pressPoint;
        return Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = ItemOf(sender);
        _pressPoint = e.GetPosition(this);
    }

    void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        // Results mix groups, so reordering there has no meaning
        if (IsSearching || e.LeftButton != MouseButtonState.Pressed || _pressedItem is not { } item || !IsDragGesture(e)) return;
        _pressedItem = null;
        var tile = (FrameworkElement)sender;
        RunDrag(tile, tile, new DataObject(ItemFormat, item), dragging => item.IsDragging = dragging);
    }

    void OnTileDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ItemFormat) is not LaunchItem source || CurrentGroup is not { } group) return;

        // Reorder live while hovering so the tiles show where the item will land
        var target = ItemOf(sender);
        int from = group.Items.IndexOf(source), to = group.Items.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) group.Items.Move(from, to);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    void OnTileDrop(object sender, DragEventArgs e)
    {
        // Already moved during DragOver; files/URLs bubble up to the window handler
        if (e.Data.GetDataPresent(ItemFormat)) e.Handled = true;
    }

    void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedGroupElement = (FrameworkElement)sender;
        _pressedGroup = (ItemGroup)_pressedGroupElement.DataContext;
        _pressPoint = e.GetPosition(this);
    }

    void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedGroup is not { } group || !IsDragGesture(e)) return;
        _pressedGroup = null;
        RunDrag(GroupList, _pressedGroupElement!, new DataObject(GroupFormat, group), dragging => group.IsDragging = dragging);
    }

    /// <summary>Runs a drag with a ghost image under the cursor and the source faded in place.</summary>
    void RunDrag(DependencyObject source, FrameworkElement visual, DataObject data, Action<bool> setDragging)
    {
        var layer = AdornerLayer.GetAdornerLayer(Root);
        if (layer != null)
        {
            _dragAdorner = new DragAdorner(Root, visual, Mouse.GetPosition(visual));
            _dragAdorner.MoveTo(Mouse.GetPosition(Root));
            layer.Add(_dragAdorner);
        }
        setDragging(true);
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        }
        finally
        {
            setDragging(false);
            if (_dragAdorner != null) layer?.Remove(_dragAdorner);
            _dragAdorner = null;
            foreach (var g in _data.Groups) g.IsDropTarget = false;
            RequestSave();
        }
    }

    void OnPreviewDragOver(object sender, DragEventArgs e) => _dragAdorner?.MoveTo(e.GetPosition(Root));

    void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when really outside
        var p = e.GetPosition(Root);
        if (p.X < 0 || p.Y < 0 || p.X >= Root.ActualWidth || p.Y >= Root.ActualHeight) _dragAdorner?.Hide();
    }

    void OnGroupDragOver(object sender, DragEventArgs e)
    {
        var target = (ItemGroup)((FrameworkElement)sender).DataContext;

        if (e.Data.GetData(GroupFormat) is ItemGroup source)
        {
            int from = _data.Groups.IndexOf(source), to = _data.Groups.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to) _data.Groups.Move(from, to);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(ItemFormat))
        {
            // Dropping an item on another group moves it there
            bool canDrop = target != CurrentGroup;
            foreach (var g in _data.Groups) g.IsDropTarget = canDrop && g == target;
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }
    }

    void OnGroupDragLeave(object sender, DragEventArgs e) =>
        ((ItemGroup)((FrameworkElement)sender).DataContext).IsDropTarget = false;

    void OnGroupDrop(object sender, DragEventArgs e)
    {
        var target = (ItemGroup)((FrameworkElement)sender).DataContext;

        if (e.Data.GetData(ItemFormat) is LaunchItem item)
        {
            e.Handled = true;
            if (CurrentGroup is not { } source || source == target) return;
            source.Items.Remove(item);
            target.Items.Add(item);
            UpdateEmptyHint();
            RequestSave();
        }
        else if (e.Data.GetDataPresent(GroupFormat))
        {
            e.Handled = true;
        }
        else
        {
            // Files dropped on a group: select it, then let the window handler add them there
            GroupList.SelectedItem = target;
        }
    }

    #endregion

    #region Search

    const int MaxApps = 8;
    const string CalcGlyph = "\uE8EF", ConsoleGlyph = "\uE756", ToolGlyph = "\uE8FD", WebGlyph = "\uE774";
    List<SearchCommand> _commands = new();
    List<SystemApp> _apps = new();
    /// <summary>Commands, then items, then system apps: the keyboard selection runs through all three.</summary>
    readonly List<ObservableObject> _entries = new();

    /// <summary>Lists toolbox tools and pages matching a name, for the "t " prefix.</summary>
    public Func<string, IReadOnlyList<(string Name, Action Open)>>? ToolSearch { get; set; }
    /// <summary>Asks the AI assistant a question, for the "ai " prefix; null when the assistant is off.</summary>
    public Action<string>? AskAi { get; set; }

    bool IsSearching => ItemSearch.Normalize(SearchBox.Text).Length > 0;

    void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearch();
    }

    void UpdateSearch()
    {
        SetHighlight(-1);
        var query = ItemSearch.Normalize(SearchBox.Text);
        _commands = ParseCommand(SearchBox.Text.TrimStart());
        if (_commands.Count > 0)
        {
            // A prefix command takes over the results
            _results = new List<LaunchItem>();
            _apps = new List<SystemApp>();
        }
        else
        {
            _results = ItemSearch.Find(_data.Groups, query);
            var known = new HashSet<string>(_data.Groups.SelectMany(g => g.Items).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            _apps = AppIndex.Find(query, known, MaxApps);
        }
        _entries.Clear();
        _entries.AddRange(_commands);
        _entries.AddRange(_results);
        _entries.AddRange(_apps);

        bool searching = query.Length > 0;
        CommandResults.ItemsSource = searching ? _commands : null;
        SearchResults.ItemsSource = searching ? _results : null;
        AppResults.ItemsSource = searching ? _apps : null;
        CommandResults.Visibility = searching && _commands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        AppResults.Visibility = AppCaption.Visibility = searching && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupItems.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        if (_entries.Count > 0) SetHighlight(0);
        UpdateEmptyHint();
    }

    List<SearchCommand> ParseCommand(string text)
    {
        var list = new List<SearchCommand>();
        if (text.StartsWith("=", StringComparison.Ordinal))
        {
            var expression = text.Substring(1).Trim();
            if (expression.Length == 0)
                list.Add(new SearchCommand("\uE8EF", "计算器", "输入算式，如 =2^10+sqrt(9)*(1-3)", null));
            else if (Calculator.TryEvaluate(expression, out var value, out var error))
            {
                var result = Calculator.Format(value);
                SearchCommand? command = null;
                command = new SearchCommand("\uE8EF", "= " + result, $"{expression}　回车复制结果", () =>
                {
                    if (TryCopy(result)) command!.Subtitle = $"已复制 {result}";
                });
                list.Add(command);
            }
            else
                list.Add(new SearchCommand("\uE8EF", "= …", error, null));
        }
        else if (text.StartsWith(">", StringComparison.Ordinal))
        {
            var command = text.Substring(1).Trim();
            list.Add(command.Length == 0
                ? new SearchCommand("\uE756", "运行命令", "输入命令，回车在新的命令行窗口中运行", null)
                : new SearchCommand("\uE756", command, "回车在新的命令行窗口中运行（cmd /k）", () =>
                {
                    if (ProcessLauncher.RunInConsole(command)) Hide();
                }));
        }
        else if (SplitPrefix(text) is { } split)
        {
            var (prefix, rest) = split;
            if (string.Equals(prefix, "f", StringComparison.OrdinalIgnoreCase))
                list.AddRange(FileCommands(rest));
            else if (string.Equals(prefix, "ai", StringComparison.OrdinalIgnoreCase) && AskAi != null)
            {
                var ask = AskAi;
                list.Add(rest.Length == 0
                    ? new SearchCommand("\uE99A", "问 AI", "输入问题，回车在 AI 助手里回答", null)
                    : new SearchCommand("\uE99A", $"问 AI：{rest}", "回车在 AI 助手里回答", () =>
                    {
                        Hide();
                        ask(rest);
                    }));
            }
            else if (string.Equals(prefix, "t", StringComparison.OrdinalIgnoreCase) && ToolSearch != null)
            {
                foreach (var (name, open) in ToolSearch(rest))
                    list.Add(new SearchCommand("\uE8FD", name, "工具箱", () =>
                    {
                        open();
                    }));
                if (list.Count == 0) list.Add(new SearchCommand("\uE8FD", "工具箱", "没有找到匹配的工具", null));
            }
            else if (_data.SearchEngines.FirstOrDefault(s => string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase)) is { } engine
                     && rest.Length > 0)
            {
                var url = engine.Url.Replace("{0}", Uri.EscapeDataString(rest));
                list.Add(new SearchCommand("\uE774", $"在{engine.Name}中搜索「{rest}」", url, () =>
                {
                    if (ProcessLauncher.Start(url, displayName: engine.Name)) Hide();
                }));
            }
        }
        return list;
    }

    string? _fileQuery;
    List<string>? _fileResults;

    /// <summary>Found files for the "f " prefix; the search runs in the background and refreshes the list when done.</summary>
    List<SearchCommand> FileCommands(string query)
    {
        var list = new List<SearchCommand>();
        if (query.Length == 0)
        {
            list.Add(new SearchCommand("\uE721", "查找文件", $"输入文件名，回车打开，Ctrl+回车打开所在文件夹（{FileSearch.Engine}）", null));
            return list;
        }
        if (_fileQuery != query)
        {
            _fileQuery = query;
            _fileResults = null;
            System.Threading.Tasks.Task.Run(() => FileSearch.Find(query)).ContinueWith(t =>
            {
                if (_fileQuery != query) return;
                _fileResults = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<string>();
                UpdateSearch();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (_fileResults == null)
        {
            list.Add(new SearchCommand("\uE721", "正在查找…", FileSearch.Engine, null));
            return list;
        }
        foreach (var path in _fileResults)
            list.Add(new SearchCommand(Directory.Exists(path) ? "\uE8B7" : "\uE8A5", Path.GetFileName(path), path, () =>
            {
                if (ProcessLauncher.Start(path)) Hide();
            }) { RunAlt = () => { ProcessLauncher.OpenLocation(path); Hide(); } });
        if (list.Count == 0) list.Add(new SearchCommand("\uE721", "没有找到文件", $"{FileSearch.Engine} 里没有名字包含「{query}」的文件", null));
        return list;
    }

    /// <summary>"g hello" gives ("g", "hello"); text without a space gives null.</summary>
    static (string Prefix, string Query)? SplitPrefix(string text)
    {
        int space = text.IndexOf(' ');
        if (space <= 0) return null;
        return (text.Substring(0, space), text.Substring(space + 1).Trim());
    }

    static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            // The clipboard may be locked by another program
            Log.Error("Failed to copy calculator result", ex);
            return false;
        }
    }

    void SetHighlight(int index)
    {
        if (_highlight >= 0 && _highlight < _entries.Count) SetHighlighted(_entries[_highlight], false);
        _highlight = index;
        if (index < 0 || index >= _entries.Count) return;

        var entry = _entries[index];
        SetHighlighted(entry, true);
        var (list, offset) = entry switch
        {
            SearchCommand => (CommandResults, 0),
            LaunchItem => (SearchResults, _commands.Count),
            _ => (AppResults, _commands.Count + _results.Count),
        };
        (list.ItemContainerGenerator.ContainerFromIndex(index - offset) as FrameworkElement)?.BringIntoView();
    }

    static void SetHighlighted(ObservableObject entry, bool value)
    {
        switch (entry)
        {
            case LaunchItem item: item.IsHighlighted = value; break;
            case SystemApp app: app.IsHighlighted = value; break;
            case SearchCommand command: command.IsHighlighted = value; break;
        }
    }

    void RunEntry(ObservableObject entry)
    {
        switch (entry)
        {
            case LaunchItem item: Launch(item); break;
            case SystemApp app: LaunchApp(app); break;
            case SearchCommand command: command.Run?.Invoke(); break;
        }
    }

    void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // First Esc clears the search, second hides the window
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Hide();
            e.Handled = true;
            return;
        }
        if (!IsSearching || _entries.Count == 0) return;

        if (e.Key == Key.Enter)
        {
            if (_highlight >= 0 && _highlight < _entries.Count)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && _entries[_highlight] is SearchCommand { RunAlt: { } alt }) alt();
                else RunEntry(_entries[_highlight]);
            }
            e.Handled = true;
            return;
        }

        int tileStart = _commands.Count, tileEnd = tileStart + _results.Count;
        bool onTile = _highlight >= tileStart && _highlight < tileEnd;
        int next = int.MinValue;
        if (onTile)
        {
            int columns = Math.Max(1, (int)(SearchResults.ActualWidth / TileSlotWidth));
            next = e.Key switch
            {
                Key.Left => _highlight - 1,
                Key.Right => _highlight + 1,
                // Leaving the tiles upwards or downwards lands on the neighbouring rows
                Key.Up => _highlight - columns >= tileStart ? _highlight - columns : tileStart > 0 ? tileStart - 1 : _highlight,
                Key.Down => _highlight + columns >= tileEnd ? (tileEnd < _entries.Count ? tileEnd : tileEnd - 1) : _highlight + columns,
                _ => int.MinValue,
            };
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            next = _highlight + (e.Key == Key.Up ? -1 : 1);
        }

        if (next != int.MinValue)
        {
            SetHighlight(Math.Max(0, Math.Min(_entries.Count - 1, next)));
            e.Handled = true;
        }
    }

    void OnCommandClick(object sender, MouseButtonEventArgs e) =>
        ((SearchCommand)((FrameworkElement)sender).DataContext).Run?.Invoke();

    /// <summary>Typing anywhere in the window goes to the search box.</summary>
    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.AppendText(e.Text);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    #endregion

    #region System apps

    static SystemApp AppOf(object sender) => (SystemApp)((FrameworkElement)sender).DataContext;

    void LaunchApp(SystemApp app, bool asAdmin = false)
    {
        if (ProcessLauncher.Start(app.Path, displayName: app.Name, asAdmin: asAdmin)) Hide();
    }

    void OnAppMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) LaunchApp(AppOf(sender));
    }

    void OnAppOpen(object sender, RoutedEventArgs e) => LaunchApp(AppOf(sender));

    void OnAppRunAsAdmin(object sender, RoutedEventArgs e) => LaunchApp(AppOf(sender), asAdmin: true);

    void OnAppOpenLocation(object sender, RoutedEventArgs e) => ProcessLauncher.OpenLocation(AppOf(sender).Path);

    void OnAppAdd(object sender, RoutedEventArgs e)
    {
        var app = AppOf(sender);
        if (CurrentGroup is not { } group) return;
        group.Items.Add(new LaunchItem { Name = app.Name, Path = app.Path });
        RequestSave();
        UpdateSearch();
    }

    #endregion

    #region Item hotkeys

    readonly Dictionary<LaunchItem, GlobalHotkey> _itemHotkeys = new();

    /// <summary>Label of the app hotkey using a combination, or null; wired to the app's own hotkeys.</summary>
    public Func<string, string?>? AppHotkeyOwner { get; set; }

    IEnumerable<LaunchItem> AllItems => _data.Groups.SelectMany(g => g.Items).Distinct();

    /// <summary>Name of the launcher item using a combination, or null.</summary>
    public string? ItemHotkeyOwner(string hotkey) =>
        hotkey.Length == 0 ? null : AllItems.FirstOrDefault(i => i.Hotkey == hotkey)?.Name;

    /// <summary>Registers every item's hotkey; returns the ones that are taken.</summary>
    public IReadOnlyList<string> RegisterItemHotkeys()
    {
        var taken = new List<string>();
        foreach (var item in AllItems.Where(i => i.Hotkey.Length > 0))
            if (!RegisterItemHotkey(item, item.Hotkey)) taken.Add($"{item.Name} {item.Hotkey}");
        return taken;
    }

    bool RegisterItemHotkey(LaunchItem item, string text)
    {
        UnregisterItemHotkey(item);
        if (!Hotkey.TryParse(text, out var key)) return text.Length == 0;
        var hotkey = new GlobalHotkey();
        if (!hotkey.Register(key))
        {
            hotkey.Dispose();
            return false;
        }
        hotkey.Pressed += () => Launch(item);
        _itemHotkeys[item] = hotkey;
        return true;
    }

    void UnregisterItemHotkey(LaunchItem item)
    {
        if (!_itemHotkeys.TryGetValue(item, out var hotkey)) return;
        hotkey.Dispose();
        _itemHotkeys.Remove(item);
    }

    void OnItemSetHotkey(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (HotkeyDialog.Show(this, $"设置快捷键 - {item.Name}", item.Hotkey) is not { } value || value == item.Hotkey) return;

        if (value.Length > 0 && (AllItems.FirstOrDefault(i => i != item && i.Hotkey == value)?.Name ?? AppHotkeyOwner?.Invoke(value)) is { } owner)
        {
            MessageBox.Show($"{value} 已用于「{owner}」，未更改", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!RegisterItemHotkey(item, value))
        {
            RegisterItemHotkey(item, item.Hotkey);
            MessageBox.Show($"{value} 已被其他程序占用，未更改", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        item.Hotkey = value;
        RequestSave();
    }

    #endregion
}
