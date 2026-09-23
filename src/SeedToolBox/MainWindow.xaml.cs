using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SeedToolBox.Models;
using SeedToolBox.Services;
using SeedToolBox.Views;

namespace SeedToolBox;

public partial class MainWindow : Window
{
    readonly AppData _data;
    readonly DispatcherTimer _saveTimer;
    bool _exiting;

    public MainWindow(AppData data)
    {
        InitializeComponent();
        _data = data;
        DataContext = data;

        // Debounce saves so dragging/resizing doesn't hammer the disk
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();

        ApplyWindowSettings();
        GroupList.SelectionChanged += (_, _) => UpdateEmptyHint();
        GroupList.SelectedIndex = 0;

        LocationChanged += (_, _) => RequestSave();
        SizeChanged += (_, _) => RequestSave();
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
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowAndActivate();
    }

    public void PrepareExit()
    {
        _exiting = true;
        SaveNow();
    }

    void RequestSave()
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
            DataStore.Save(_data);
        }
        catch (Exception ex)
        {
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

    void UpdateEmptyHint() =>
        EmptyHint.Visibility = CurrentGroup is { Items.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    #endregion

    #region Item actions

    static LaunchItem ItemOf(object sender) => (LaunchItem)((FrameworkElement)sender).DataContext;

    void OnItemClick(object sender, MouseButtonEventArgs e) => Launcher.Launch(ItemOf(sender));

    void OnItemOpen(object sender, RoutedEventArgs e) => Launcher.Launch(ItemOf(sender));

    void OnItemRunAsAdmin(object sender, RoutedEventArgs e) => Launcher.Launch(ItemOf(sender), asAdmin: true);

    void OnItemOpenLocation(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (IconHelper.IsUrl(item.Path)) Launcher.Launch(item);
        else Launcher.OpenLocation(item.Path);
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

    void OnItemDelete(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (MessageBox.Show($"确定删除「{item.Name}」吗？\n（只删除快捷项，不会删除原文件）", "SeedToolBox",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        CurrentGroup?.Items.Remove(item);
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
}
