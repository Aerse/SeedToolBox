using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

sealed class UsageItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public int Files { get; set; }
    public double Percent { get; set; }
    public string Icon => IsFolder ? "" : "";
    public string SizeText => Ui.FormatSize(Size);
    public string PercentText => $"{Percent:0.0}%";
    public string FilesText => IsFolder ? Files.ToString("N0") : "";
    public double Bar => Math.Max(1, Percent * 1.2);
}

/// <summary>Ranks the folders and files under a folder by size; double-click a folder to drill in.</summary>
sealed class DiskUsagePage : DockPanel
{
    readonly ObservableCollection<UsageItem> _items = new();
    readonly ListView _list = new();
    readonly TextBox _folder = Ui.Field(250);
    readonly TextBlock _status = Ui.Status();
    readonly Button _scan;
    /// <summary>Folder sizes from the last scan, so drilling in is instant.</summary>
    readonly ConcurrentDictionary<string, (long Size, int Files)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _cancel;
    string? _root;

    public DiskUsagePage()
    {
        var header = Ui.Header("空间占用分析", "统计文件夹下各子文件夹和文件的大小并排序，双击文件夹进入");
        var browse = Ui.Button("浏览…", Browse);
        browse.Margin = new Thickness(8, 0, 16, 0);
        _scan = Ui.Button("分析", () => Scan(_folder.Text.Trim(), rescan: true), accent: true);
        var row = Ui.Row(Ui.Label("文件夹"), _folder, browse, _scan, Ui.Button("上一级", Up), Ui.Button("停止", () => _cancel?.Cancel()), Ui.Button("打开", () => { if (Directory.Exists(_folder.Text)) ProcessLauncher.OpenLocation(_folder.Text); }), ListTools.ExportButton(_list, _status, "磁盘占用.csv", ("名称", "Path"), ("占比", "PercentText")));
        _folder.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Scan(_folder.Text.Trim(), rescan: false); };
        Ui.FileDrop(_folder, p => { if (Directory.Exists(p[0])) Scan(p[0], rescan: true); });

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 330, CellTemplate = NameTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "大小", Width = 100, DisplayMemberBinding = new Binding(nameof(UsageItem.SizeText)) });
        view.Columns.Add(new GridViewColumn { Header = "占比", Width = 190, CellTemplate = BarTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "文件数", Width = 90, DisplayMemberBinding = new Binding(nameof(UsageItem.FilesText)) });
        _list.View = view;
        ListTools.Sortable(_list, ("名称", "Name"), ("占比", "Percent"));
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is not UsageItem item) return;
            if (item.IsFolder) Scan(item.Path, rescan: false);
            else ProcessLauncher.OpenLocation(item.Path);
        };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "在资源管理器中显示" };
        open.Click += (_, _) => { if (_list.SelectedItem is UsageItem item) ProcessLauncher.OpenLocation(item.Path); };
        menu.Items.Add(open);
        _list.ContextMenu = menu;

        SetDock(header, Dock.Top);
        SetDock(row, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(row);
        Children.Add(_status);
        Children.Add(_list);
    }

    DataTemplate NameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(UsageItem.Icon)));
        icon.SetValue(TextBlock.FontFamilyProperty, Application.Current.FindResource("IconFont"));
        icon.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["AccentBrush"]);
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(UsageItem.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(icon);
        panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }

    DataTemplate BarTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(WidthProperty, 120.0);
        track.SetValue(HeightProperty, 8.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        track.SetValue(Border.BackgroundProperty, Application.Current.Resources["PressedBrush"]);
        track.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var bar = new FrameworkElementFactory(typeof(Rectangle));
        bar.SetBinding(WidthProperty, new Binding(nameof(UsageItem.Bar)));
        bar.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        bar.SetValue(Rectangle.RadiusXProperty, 4.0);
        bar.SetValue(Rectangle.RadiusYProperty, 4.0);
        bar.SetValue(Shape.FillProperty, Application.Current.Resources["AccentBrush"]);
        track.AppendChild(bar);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(UsageItem.PercentText)));
        text.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        panel.AppendChild(track);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Scan(dialog.SelectedPath, rescan: true);
    }

    void Up()
    {
        var parent = Directory.Exists(_folder.Text) ? Directory.GetParent(_folder.Text.TrimEnd('\\'))?.FullName : null;
        if (parent != null) Scan(parent, rescan: false);
    }

    async void Scan(string folder, bool rescan)
    {
        if (!Directory.Exists(folder)) { Ui.SetStatus(_status, "请选择存在的文件夹", true); return; }
        folder = System.IO.Path.GetFullPath(folder);
        _folder.Text = folder;
        // Reuse sizes only when moving inside the folder that was last scanned
        bool inside = _root != null && (folder + "\\").StartsWith(_root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (rescan || !inside)
        {
            _sizes.Clear();
            _root = folder;
        }
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _items.Clear();
        _scan.IsEnabled = false;
        var progress = new Progress<string>(s => Ui.SetStatus(_status, s));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var items = await Task.Run(() => Measure(folder, progress, cancel.Token));
            if (cancel.IsCancellationRequested) return;
            long total = items.Sum(i => i.Size);
            foreach (var item in items.OrderByDescending(i => i.Size))
            {
                item.Percent = total > 0 ? item.Size * 100.0 / total : 0;
                _items.Add(item);
            }
            Ui.SetStatus(_status, $"合计 {Ui.FormatSize(total)}，{items.Sum(i => i.IsFolder ? i.Files : 1):N0} 个文件，用时 {watch.Elapsed.TotalSeconds:0.0} 秒");
        }
        catch (OperationCanceledException) { Ui.SetStatus(_status, "已停止"); }
        catch (Exception ex) { Ui.SetStatus(_status, "分析失败：" + ex.Message, true); }
        finally { if (cancel == _cancel) _scan.IsEnabled = true; }
    }

    List<UsageItem> Measure(string folder, IProgress<string> progress, CancellationToken cancel)
    {
        var result = new List<UsageItem>();
        var dir = new DirectoryInfo(folder);
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("无法读取此文件夹：" + ex.Message); }

        var subfolders = entries.OfType<DirectoryInfo>().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).ToList();
        int done = 0;
        Parallel.ForEach(subfolders, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, sub =>
        {
            var (size, files) = _sizes.TryGetValue(sub.FullName, out var cached) ? cached : SizeOf(sub, cancel);
            lock (result) result.Add(new UsageItem { Path = sub.FullName, Name = sub.Name, IsFolder = true, Size = size, Files = files });
            progress.Report($"分析中… {Interlocked.Increment(ref done)} / {subfolders.Count} 个文件夹");
        });
        foreach (var file in entries.OfType<FileInfo>())
            result.Add(new UsageItem { Path = file.FullName, Name = file.Name, Size = file.Length });
        return result;
    }

    /// <summary>Total size of a folder, caching every folder beneath it along the way.</summary>
    (long, int) SizeOf(DirectoryInfo dir, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        long size = 0;
        int files = 0;
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (0, 0); }
        foreach (var entry in entries)
        {
            if (entry is FileInfo file)
            {
                size += file.Length;
                files++;
            }
            else if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                var (s, f) = SizeOf(sub, cancel);
                size += s;
                files += f;
            }
        }
        _sizes[dir.FullName] = (size, files);
        return (size, files);
    }
}
