using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    public long OnDisk { get; set; }
    public int Files { get; set; }
    public double Percent { get; set; }
    public DateTime Modified { get; set; }
    public string Icon => IsFolder ? "" : "";
    public string SizeText => Ui.FormatSize(Size);
    public string OnDiskText => Ui.FormatSize(OnDisk);
    public string PercentText => $"{Percent:0.0}%";
    public string FilesText => IsFolder ? Files.ToString("N0") : "";
    public double Bar => Math.Max(1, Percent * 1.2);
}

sealed class ExtensionUsage
{
    public string Extension { get; set; } = "";
    public int Count { get; set; }
    public long Size { get; set; }
    public long OnDisk { get; set; }
    public double Percent { get; set; }
    public string SizeText => Ui.FormatSize(Size);
    public string OnDiskText => Ui.FormatSize(OnDisk);
    public string PercentText => $"{Percent:0.0}%";
}

/// <summary>Ranks the folders and files under a folder by size; double-click a folder to drill in.</summary>
sealed class DiskUsagePage : DockPanel
{
    const int TopCount = 100;

    readonly ObservableCollection<UsageItem> _items = new();
    readonly ObservableCollection<ExtensionUsage> _extensions = new();
    readonly ObservableCollection<UsageItem> _largest = new();
    readonly ListView _list = new();
    readonly ListView _extList = new();
    readonly ListView _topList = new();
    readonly Canvas _treemap = new() { ClipToBounds = true };
    readonly TabControl _tabs = new();
    readonly TextBox _folder = Ui.Field(250);
    readonly TextBlock _status = Ui.Status();
    readonly Button _scan;
    /// <summary>Folder sizes from the last scan, so drilling in is instant.</summary>
    readonly ConcurrentDictionary<string, (long Size, long OnDisk, int Files)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _cancel;
    string? _root;
    long _cluster = 4096;

    // Collected only while the scan root is walked in full
    Dictionary<string, ExtensionUsage>? _extStats;
    List<UsageItem>? _top;

    public DiskUsagePage()
    {
        var header = Ui.Header("空间占用分析", "统计文件夹下各子文件夹和文件的大小并排序，双击文件夹进入；树状图中单击文件夹进入");
        var browse = Ui.Button("浏览…", Browse);
        browse.Margin = new Thickness(8, 0, 16, 0);
        _scan = Ui.Button("分析", () => Scan(_folder.Text.Trim(), rescan: true), accent: true);
        var row = Ui.Row(Ui.Label("文件夹"), _folder, browse, _scan, Ui.Button("上一级", Up), Ui.Button("停止", () => _cancel?.Cancel()), Ui.Button("打开", () => { if (Directory.Exists(_folder.Text)) ProcessLauncher.OpenLocation(_folder.Text); }), Ui.Button("移到回收站", () => Recycle(SelectedItem())), ListTools.ExportButton(_list, _status, "磁盘占用.csv", ("名称", "Path"), ("占比", "PercentText")));
        _folder.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Scan(_folder.Text.Trim(), rescan: false); };
        Ui.FileDrop(_folder, p => { if (Directory.Exists(p[0])) Scan(p[0], rescan: true); });

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 330, CellTemplate = NameTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "大小", Width = 100, DisplayMemberBinding = new Binding(nameof(UsageItem.SizeText)) });
        view.Columns.Add(new GridViewColumn { Header = "占用空间", Width = 100, DisplayMemberBinding = new Binding(nameof(UsageItem.OnDiskText)) });
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
        _list.ContextMenu = ItemMenu(() => _list.SelectedItem as UsageItem);
        _list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) Recycle(_list.SelectedItem as UsageItem); };

        _extList.ItemsSource = _extensions;
        _extList.BorderBrush = _list.BorderBrush;
        var extView = new GridView();
        extView.Columns.Add(new GridViewColumn { Header = "扩展名", Width = 140, DisplayMemberBinding = new Binding(nameof(ExtensionUsage.Extension)) });
        extView.Columns.Add(new GridViewColumn { Header = "文件数", Width = 90, DisplayMemberBinding = new Binding(nameof(ExtensionUsage.Count)) { StringFormat = "N0" } });
        extView.Columns.Add(new GridViewColumn { Header = "大小", Width = 100, DisplayMemberBinding = new Binding(nameof(ExtensionUsage.SizeText)) });
        extView.Columns.Add(new GridViewColumn { Header = "占用空间", Width = 100, DisplayMemberBinding = new Binding(nameof(ExtensionUsage.OnDiskText)) });
        extView.Columns.Add(new GridViewColumn { Header = "占比", Width = 80, DisplayMemberBinding = new Binding(nameof(ExtensionUsage.PercentText)) });
        _extList.View = extView;
        ListTools.Sortable(_extList);

        _topList.ItemsSource = _largest;
        _topList.BorderBrush = _list.BorderBrush;
        var topView = new GridView();
        topView.Columns.Add(new GridViewColumn { Header = "大小", Width = 100, DisplayMemberBinding = new Binding(nameof(UsageItem.SizeText)) });
        topView.Columns.Add(new GridViewColumn { Header = "占用空间", Width = 100, DisplayMemberBinding = new Binding(nameof(UsageItem.OnDiskText)) });
        topView.Columns.Add(new GridViewColumn { Header = "修改时间", Width = 140, DisplayMemberBinding = new Binding(nameof(UsageItem.Modified)) { StringFormat = "yyyy-MM-dd HH:mm" } });
        topView.Columns.Add(new GridViewColumn { Header = "路径", Width = 480, DisplayMemberBinding = new Binding(nameof(UsageItem.Path)) });
        _topList.View = topView;
        ListTools.Sortable(_topList);
        _topList.MouseDoubleClick += (_, _) => { if (_topList.SelectedItem is UsageItem item) ProcessLauncher.OpenLocation(item.Path); };
        _topList.ContextMenu = ItemMenu(() => _topList.SelectedItem as UsageItem);
        _topList.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) Recycle(_topList.SelectedItem as UsageItem); };

        _treemap.Background = Brushes.Transparent;
        _treemap.SizeChanged += (_, _) => DrawTreemap();
        var treemapBorder = new Border { BorderBrush = _list.BorderBrush, BorderThickness = new Thickness(1), Child = _treemap };

        _tabs.Items.Add(new TabItem { Header = "列表", Content = _list });
        _tabs.Items.Add(new TabItem { Header = "树状图", Content = treemapBorder });
        _tabs.Items.Add(new TabItem { Header = "按扩展名", Content = Ui.Titled("分析的根文件夹中各扩展名的占用", _extList, ListTools.ExportButton(_extList, _status, "扩展名占用.csv")) });
        _tabs.Items.Add(new TabItem { Header = "最大的 100 个文件", Content = Ui.Titled("分析的根文件夹中最大的文件，双击打开位置，Delete 移到回收站", _topList, ListTools.ExportButton(_topList, _status, "最大文件.csv")) });
        _tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == _tabs) DrawTreemap(); };

        SetDock(header, Dock.Top);
        SetDock(row, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(row);
        Children.Add(_status);
        Children.Add(_tabs);
    }

    UsageItem? SelectedItem() => _tabs.SelectedIndex switch
    {
        0 => _list.SelectedItem as UsageItem,
        3 => _topList.SelectedItem as UsageItem,
        _ => null,
    };

    ContextMenu ItemMenu(Func<UsageItem?> selected)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "在资源管理器中显示" };
        open.Click += (_, _) => { if (selected() is { } item) ProcessLauncher.OpenLocation(item.Path); };
        var recycle = new MenuItem { Header = "移到回收站…" };
        recycle.Click += (_, _) => Recycle(selected());
        menu.Items.Add(open);
        menu.Items.Add(recycle);
        return menu;
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

    static readonly Color[] Palette =
    {
        Color.FromRgb(0x3B, 0x7D, 0xD8), Color.FromRgb(0x2E, 0x9E, 0x7A), Color.FromRgb(0xD8, 0x8B, 0x2E), Color.FromRgb(0x9B, 0x59, 0xB6),
        Color.FromRgb(0xC0, 0x4B, 0x4B), Color.FromRgb(0x2A, 0x9D, 0xB8), Color.FromRgb(0x7F, 0x9A, 0x2E), Color.FromRgb(0xB8, 0x5C, 0x96),
    };

    void DrawTreemap()
    {
        if (_tabs.SelectedIndex != 1) return;
        _treemap.Children.Clear();
        double width = _treemap.ActualWidth, height = _treemap.ActualHeight;
        var items = _items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        if (width < 4 || height < 4 || items.Count == 0) return;
        double total = items.Sum(i => (double)i.Size);
        var areas = items.Select(i => i.Size / total * width * height).ToList();
        var rects = Squarify(areas, new Rect(0, 0, width, height));
        var fileBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        fileBrush.Freeze();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            var brush = item.IsFolder ? new SolidColorBrush(Palette[i % Palette.Length]) : fileBrush;
            var cell = new Border
            {
                Width = r.Width,
                Height = r.Height,
                Background = brush,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(0.5),
                ToolTip = $"{item.Name}\n{item.SizeText}（{item.PercentText}）" + (item.IsFolder ? $"\n{item.FilesText} 个文件，单击进入" : ""),
                Cursor = item.IsFolder ? System.Windows.Input.Cursors.Hand : null,
                ContextMenu = ItemMenu(() => item),
            };
            if (r.Width > 40 && r.Height > 18)
                cell.Child = new TextBlock
                {
                    Text = item.Name + (r.Height > 34 ? "\n" + item.SizeText : ""),
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(4, 2, 4, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                };
            cell.MouseLeftButtonUp += (_, _) => { if (item.IsFolder) Scan(item.Path, rescan: false); };
            Canvas.SetLeft(cell, r.X);
            Canvas.SetTop(cell, r.Y);
            _treemap.Children.Add(cell);
        }
    }

    /// <summary>Squarified treemap layout; <paramref name="areas"/> are sorted largest first and sum to the area of <paramref name="bounds"/>.</summary>
    static Rect[] Squarify(IList<double> areas, Rect bounds)
    {
        var rects = new Rect[areas.Count];
        var r = bounds;
        int start = 0;
        while (start < areas.Count)
        {
            double side = Math.Min(r.Width, r.Height);
            if (side <= 0) break;
            int end = start + 1;
            double sum = areas[start];
            double worst = Worst(areas[start], areas[start], sum, side);
            while (end < areas.Count)
            {
                double next = sum + areas[end];
                double w = Worst(areas[start], areas[end], next, side);
                if (w > worst) break;
                sum = next;
                worst = w;
                end++;
            }
            bool wide = r.Width >= r.Height;
            double thick = sum / side, offset = 0;
            for (int k = start; k < end; k++)
            {
                double length = thick > 0 ? areas[k] / thick : 0;
                rects[k] = wide ? new Rect(r.X, r.Y + offset, thick, length) : new Rect(r.X + offset, r.Y, length, thick);
                offset += length;
            }
            r = wide ? new Rect(r.X + thick, r.Y, Math.Max(0, r.Width - thick), r.Height) : new Rect(r.X, r.Y + thick, r.Width, Math.Max(0, r.Height - thick));
            start = end;
        }
        return rects;
    }

    static double Worst(double largest, double smallest, double sum, double side)
    {
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * largest / sum2, sum2 / (s2 * Math.Max(smallest, 1e-9)));
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
        bool fresh = rescan || !inside;
        if (fresh)
        {
            _sizes.Clear();
            _root = folder;
            _cluster = ClusterSize(folder);
            _extStats = new Dictionary<string, ExtensionUsage>(StringComparer.OrdinalIgnoreCase);
            _top = new List<UsageItem>();
            _extensions.Clear();
            _largest.Clear();
        }
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _items.Clear();
        _treemap.Children.Clear();
        _scan.IsEnabled = false;
        var progress = new Progress<string>(s => Ui.SetStatus(_status, s));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var items = await Task.Run(() => Measure(folder, fresh, progress, cancel.Token));
            if (cancel.IsCancellationRequested) return;
            long total = items.Sum(i => i.Size);
            foreach (var item in items.OrderByDescending(i => i.Size))
            {
                item.Percent = total > 0 ? item.Size * 100.0 / total : 0;
                _items.Add(item);
            }
            if (fresh) ShowStats();
            DrawTreemap();
            Ui.SetStatus(_status, $"合计 {Ui.FormatSize(total)}，占用空间 {Ui.FormatSize(items.Sum(i => i.OnDisk))}（簇大小 {Ui.FormatSize(_cluster)}），{items.Sum(i => i.IsFolder ? i.Files : 1):N0} 个文件，用时 {watch.Elapsed.TotalSeconds:0.0} 秒");
        }
        catch (OperationCanceledException) { Ui.SetStatus(_status, "已停止"); }
        catch (Exception ex) { Ui.SetStatus(_status, "分析失败：" + ex.Message, true); }
        finally { if (cancel == _cancel) _scan.IsEnabled = true; }
    }

    void ShowStats()
    {
        _extensions.Clear();
        _largest.Clear();
        if (_extStats == null || _top == null) return;
        long total = _extStats.Values.Sum(e => e.Size);
        foreach (var ext in _extStats.Values.OrderByDescending(e => e.Size))
        {
            ext.Percent = total > 0 ? ext.Size * 100.0 / total : 0;
            _extensions.Add(ext);
        }
        foreach (var file in _top.OrderByDescending(f => f.Size).Take(TopCount)) _largest.Add(file);
    }

    List<UsageItem> Measure(string folder, bool collect, IProgress<string> progress, CancellationToken cancel)
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
            var (size, onDisk, files) = _sizes.TryGetValue(sub.FullName, out var cached) ? cached : SizeOf(sub, collect, cancel);
            lock (result) result.Add(new UsageItem { Path = sub.FullName, Name = sub.Name, IsFolder = true, Size = size, OnDisk = onDisk, Files = files });
            progress.Report($"分析中… {Interlocked.Increment(ref done)} / {subfolders.Count} 个文件夹");
        });
        foreach (var file in entries.OfType<FileInfo>())
        {
            var item = new UsageItem { Path = file.FullName, Name = file.Name, Size = file.Length, OnDisk = OnDisk(file), Modified = file.LastWriteTime };
            if (collect) Record(file, item.OnDisk);
            result.Add(item);
        }
        return result;
    }

    /// <summary>Total size of a folder, caching every folder beneath it along the way.</summary>
    (long, long, int) SizeOf(DirectoryInfo dir, bool collect, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        long size = 0, onDisk = 0;
        int files = 0;
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (0, 0, 0); }
        foreach (var entry in entries)
        {
            if (entry is FileInfo file)
            {
                long disk = OnDisk(file);
                size += file.Length;
                onDisk += disk;
                files++;
                if (collect) Record(file, disk);
            }
            else if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                var (s, d, f) = SizeOf(sub, collect, cancel);
                size += s;
                onDisk += d;
                files += f;
            }
        }
        _sizes[dir.FullName] = (size, onDisk, files);
        return (size, onDisk, files);
    }

    void Record(FileInfo file, long onDisk)
    {
        var extStats = _extStats;
        var top = _top;
        if (extStats == null || top == null) return;
        var ext = file.Extension.Length > 0 ? file.Extension.ToLowerInvariant() : "（无扩展名）";
        lock (extStats)
        {
            if (!extStats.TryGetValue(ext, out var stat)) extStats[ext] = stat = new ExtensionUsage { Extension = ext };
            stat.Count++;
            stat.Size += file.Length;
            stat.OnDisk += onDisk;
        }
        lock (top)
        {
            if (top.Count >= TopCount * 2)
            {
                top.Sort((a, b) => b.Size.CompareTo(a.Size));
                top.RemoveRange(TopCount, top.Count - TopCount);
            }
            if (top.Count < TopCount || file.Length > top[TopCount - 1].Size)
                top.Add(new UsageItem { Path = file.FullName, Name = file.Name, Size = file.Length, OnDisk = onDisk, Modified = file.LastWriteTime });
        }
    }

    void Recycle(UsageItem? item)
    {
        if (item == null) { Ui.SetStatus(_status, "请先选择要删除的文件或文件夹", true); return; }
        var kind = item.IsFolder ? "文件夹" : "文件";
        if (MessageBox.Show(Window.GetWindow(this), $"把{kind}「{item.Path}」（{item.SizeText}）移到回收站？", "移到回收站", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            if (item.IsFolder || Directory.Exists(item.Path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Ui.SetStatus(_status, "删除失败：" + ex.Message, true);
            return;
        }
        // Cached sizes of the item, its contents and its ancestors are now wrong
        var prefix = item.Path.TrimEnd('\\') + "\\";
        foreach (var key in _sizes.Keys.ToList())
            if (key.Equals(item.Path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(key.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                _sizes.TryRemove(key, out _);
        foreach (var gone in _largest.Where(f => f.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _largest.Remove(gone);
            _top?.Remove(gone);
        }
        var shown = _items.FirstOrDefault(i => i.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        if (shown != null)
        {
            _items.Remove(shown);
            long total = _items.Sum(i => i.Size);
            var rest = _items.ToList();
            _items.Clear();
            foreach (var i in rest)
            {
                i.Percent = total > 0 ? i.Size * 100.0 / total : 0;
                _items.Add(i);
            }
            DrawTreemap();
        }
        Ui.SetStatus(_status, $"已将「{item.Name}」移到回收站，释放 {item.SizeText}（扩展名统计需重新分析才会更新）");
    }

    long OnDisk(FileInfo file)
    {
        long size = file.Length;
        var path = file.FullName;
        if (path.Length >= 260 && !path.StartsWith(@"\\?\")) path = path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0) size = ((long)high << 32) | low;
        long cluster = _cluster;
        return cluster > 0 ? (size + cluster - 1) / cluster * cluster : size;
    }

    static long ClusterSize(string folder)
    {
        var root = System.IO.Path.GetPathRoot(folder);
        if (root == null) return 4096;
        if (!root.EndsWith("\\")) root += "\\";
        return GetDiskFreeSpaceW(root, out uint sectors, out uint bytes, out _, out _) ? (long)sectors * bytes : 4096;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);
}
