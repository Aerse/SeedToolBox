using System;
using System.Collections.Generic;
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
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox;

public partial class MainWindow : Window
{
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
    const double TileSlotWidth = 84; // tile width + margins
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
        GroupList.SelectionChanged += (_, _) => UpdateEmptyHint();
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
    }

    ItemGroup? CurrentGroup => GroupList.SelectedItem as ItemGroup;

    #region Window state

    void ApplyWindowSettings()
    {
        var w = _data.Window;
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
            EmptyHint.Visibility = _results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
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
        _results = ItemSearch.Find(_data.Groups, query);

        bool searching = query.Length > 0;
        SearchResults.ItemsSource = searching ? _results : null;
        SearchResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        GroupItems.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        if (_results.Count > 0) SetHighlight(0);
        UpdateEmptyHint();
    }

    void SetHighlight(int index)
    {
        if (_highlight >= 0 && _highlight < _results.Count) _results[_highlight].IsHighlighted = false;
        _highlight = index;
        if (index < 0 || index >= _results.Count) return;

        _results[index].IsHighlighted = true;
        (SearchResults.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)?.BringIntoView();
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
        if (!IsSearching || _results.Count == 0) return;

        int columns = Math.Max(1, (int)((SearchResults.ActualWidth) / TileSlotWidth));
        int next = e.Key switch
        {
            Key.Left => _highlight - 1,
            Key.Right => _highlight + 1,
            Key.Up => _highlight - columns,
            Key.Down => _highlight + columns,
            _ => int.MinValue,
        };

        if (e.Key == Key.Enter)
        {
            if (_highlight >= 0) Launch(_results[_highlight]);
            e.Handled = true;
        }
        else if (next != int.MinValue)
        {
            SetHighlight(Math.Max(0, Math.Min(_results.Count - 1, next)));
            e.Handled = true;
        }
    }

    /// <summary>Typing anywhere in the window goes to the search box.</summary>
    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsKeyboardFocused || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        SearchBox.Focus();
        SearchBox.AppendText(e.Text);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    #endregion
}
