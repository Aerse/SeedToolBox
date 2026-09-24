using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

sealed class DuplicateItem : ObservableObject
{
    bool _check;
    public string Path { get; set; } = "";
    public string Group { get; set; } = "";
    public long Size { get; set; }
    public string SizeText => Ui.FormatSize(Size);
    public DateTime Modified { get; set; }
    public bool Check { get => _check; set => Set(ref _check, value); }
}

/// <summary>Finds files with identical content: grouped by size, then by a hash of the first 64 KB, then by full SHA256.</summary>
sealed class DuplicatePage : DockPanel
{
    readonly ObservableCollection<DuplicateItem> _items = new();
    readonly ListView _list = new();
    readonly TextBox _folder = Ui.Field(300);
    readonly TextBox _minSize = Ui.Field(60);
    readonly TextBox _include = Ui.Field(130);
    readonly TextBox _exclude = Ui.Field(130);
    readonly CheckBox _byName = new() { Content = "仅按文件名匹配", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), ToolTip = "文件名相同即视为重复，不比较内容" };
    readonly TextBlock _status = Ui.Status();
    readonly Button _scan;
    CancellationTokenSource? _cancel;

    public DuplicatePage()
    {
        var header = Ui.Header("重复文件查找", "按内容查找完全相同的文件（大小 → 部分哈希 → 完整 SHA256，多线程），勾选后移到回收站");
        _minSize.Text = "1";
        _folder.ToolTip = "可填多个文件夹，用 ; 分隔；拖入文件夹会追加";
        _include.ToolTip = "只查找这些扩展名，如 jpg;png；留空表示全部";
        _exclude.ToolTip = "跳过这些扩展名，如 tmp;log";
        var browse = Ui.Button("添加…", Browse);
        browse.Margin = new Thickness(8, 0, 16, 0);
        _scan = Ui.Button("开始查找", Scan, accent: true);
        var row1 = Ui.Row(Ui.Label("文件夹"), _folder, browse, Ui.Label("最小"), _minSize, Ui.Label("KB", 16), _scan, Ui.Button("停止", () => _cancel?.Cancel()));
        var rowFilter = Ui.Row(Ui.Label("包含扩展名"), _include, Ui.Label("", 8), Ui.Label("排除扩展名"), _exclude, Ui.Label("", 16), _byName);
        var row2 = Ui.Row(Ui.Button("每组保留最早的", () => AutoCheck(keepOldest: true)), Ui.Button("每组保留最新的", () => AutoCheck(keepOldest: false)), Ui.Button("取消勾选", () => { foreach (var i in _items) i.Check = false; }), Ui.Label("", 16), Ui.Button("删除勾选（回收站）", DeleteChecked), ListTools.ExportButton(_list, _status, "重复文件.csv"));
        Ui.FileDrop(_folder, p => { foreach (var d in p.Where(Directory.Exists)) AddFolder(d); });

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButtonIsChecked, new Binding(nameof(DuplicateItem.Check)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        view.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
        view.Columns.Add(new GridViewColumn { Header = "组", Width = 50, DisplayMemberBinding = new Binding(nameof(DuplicateItem.Group)) });
        view.Columns.Add(new GridViewColumn { Header = "大小", Width = 90, DisplayMemberBinding = new Binding(nameof(DuplicateItem.SizeText)) });
        view.Columns.Add(new GridViewColumn { Header = "修改时间", Width = 140, DisplayMemberBinding = new Binding(nameof(DuplicateItem.Modified)) { StringFormat = "yyyy-MM-dd HH:mm" } });
        view.Columns.Add(new GridViewColumn { Header = "路径", Width = 470, DisplayMemberBinding = new Binding(nameof(DuplicateItem.Path)) });
        _list.View = view;
        ListTools.Sortable(_list);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is DuplicateItem item) ProcessLauncher.OpenLocation(item.Path); };
        _list.ToolTip = "双击打开所在位置";

        SetDock(header, Dock.Top);
        SetDock(row1, Dock.Top);
        SetDock(rowFilter, Dock.Top);
        SetDock(row2, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(row1);
        Children.Add(rowFilter);
        Children.Add(row2);
        Children.Add(_status);
        Children.Add(_list);
    }

    static readonly DependencyProperty ToggleButtonIsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Folders().LastOrDefault() ?? "" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddFolder(dialog.SelectedPath);
    }

    List<string> Folders() => _folder.Text.Split(';').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

    void AddFolder(string folder)
    {
        var folders = Folders();
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        _folder.Text = string.Join("; ", folders);
    }

    static HashSet<string> Extensions(string text) => new(
        text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    async void Scan()
    {
        var folders = Folders();
        if (folders.Count == 0) { Ui.SetStatus(_status, "请选择文件夹", true); return; }
        if (folders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing) { Ui.SetStatus(_status, "文件夹不存在：" + missing, true); return; }
        var filter = new DuplicateFilter { Include = Extensions(_include.Text), Exclude = Extensions(_exclude.Text), ByName = _byName.IsChecked == true };
        if (!long.TryParse(_minSize.Text, out var minKb) || minKb < 0) minKb = 0;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _items.Clear();
        _scan.IsEnabled = false;
        var progress = new Progress<string>(s => Ui.SetStatus(_status, s));
        try
        {
            var groups = await Task.Run(() => Duplicates.Find(folders, Math.Max(1, minKb * 1024), filter, progress, cancel.Token));
            int n = 0;
            foreach (var group in groups)
            {
                n++;
                foreach (var file in group) _items.Add(new DuplicateItem { Path = file.FullName, Size = file.Length, Modified = file.LastWriteTime, Group = n.ToString() });
            }
            long wasted = groups.Sum(g => g.Sum(f => f.Length) - g.Max(f => f.Length));
            Ui.SetStatus(_status, groups.Count == 0 ? "没有找到重复文件" : filter.ByName ? $"找到 {groups.Count} 组同名文件" : $"找到 {groups.Count} 组重复文件，共可释放 {Ui.FormatSize(wasted)}");
        }
        catch (OperationCanceledException) { Ui.SetStatus(_status, "已停止"); }
        catch (Exception ex) { Ui.SetStatus(_status, "查找失败：" + ex.Message, true); }
        finally { _scan.IsEnabled = true; }
    }

    void AutoCheck(bool keepOldest)
    {
        foreach (var group in _items.GroupBy(i => i.Group))
        {
            var keep = keepOldest ? group.OrderBy(i => i.Modified).First() : group.OrderByDescending(i => i.Modified).First();
            foreach (var item in group) item.Check = item != keep;
        }
    }

    void DeleteChecked()
    {
        var checkedItems = _items.Where(i => i.Check).ToList();
        if (checkedItems.Count == 0) { Ui.SetStatus(_status, "没有勾选的文件"); return; }
        // Never let a whole group go: at least one copy must survive
        var wholeGroup = _items.GroupBy(i => i.Group).FirstOrDefault(g => g.All(i => i.Check));
        if (wholeGroup != null) { Ui.SetStatus(_status, $"第 {wholeGroup.Key} 组全部被勾选，至少保留一个", true); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"把勾选的 {checkedItems.Count} 个文件（{Ui.FormatSize(checkedItems.Sum(i => i.Size))}）移到回收站？", "删除重复文件", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int done = 0;
        foreach (var item in checkedItems)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _items.Remove(item);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        // Groups left with one file are no longer duplicates
        foreach (var single in _items.GroupBy(i => i.Group).Where(g => g.Count() < 2).SelectMany(g => g).ToList()) _items.Remove(single);
        Ui.SetStatus(_status, $"已将 {done} 个文件移到回收站" + (done < checkedItems.Count ? $"，{checkedItems.Count - done} 个失败" : ""), done < checkedItems.Count);
    }
}

sealed class DuplicateFilter
{
    public HashSet<string> Include = new(), Exclude = new();
    public bool ByName;
}

static class Duplicates
{
    public static List<List<FileInfo>> Find(IEnumerable<string> folders, long minSize, DuplicateFilter filter, IProgress<string>? progress, CancellationToken cancel)
    {
        var files = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var file in DiskWalker.Files(folder, cancel))
            {
                if (file.Length < minSize) continue;
                var ext = file.Extension;
                if (filter.Include.Count > 0 && !filter.Include.Contains(ext)) continue;
                if (filter.Exclude.Contains(ext)) continue;
                // Overlapping roots would otherwise report a file as its own duplicate
                if (!seen.Add(file.FullName)) continue;
                files.Add(file);
                if (files.Count % 2000 == 0) progress?.Report($"扫描中… 已找到 {files.Count} 个文件");
            }
        }

        if (filter.ByName)
            return files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .Select(g => g.ToList()).OrderByDescending(g => g.Count).ThenBy(g => g[0].Name).ToList();

        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)) };
        var candidates = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        int done = 0, total = candidates.Count;

        // Cheap first pass over the head of each file, then the full hash only for files that still collide
        var heads = new ConcurrentDictionary<FileInfo, string?>();
        Parallel.ForEach(candidates, options, file =>
        {
            heads[file] = Hash(file, 64 * 1024);
            int n = Interlocked.Increment(ref done);
            if (n % 200 == 0) progress?.Report($"比较内容… {n} / {total}");
        });
        cancel.ThrowIfCancellationRequested();

        var headGroups = candidates.Where(f => heads[f] != null).GroupBy(f => (f.Length, Head: heads[f]!)).Where(g => g.Count() > 1).ToList();
        var needFull = headGroups.Where(g => !g.Key.Head.StartsWith("full:")).SelectMany(g => g).ToList();
        var fulls = new ConcurrentDictionary<FileInfo, string?>();
        done = 0;
        total = needFull.Count;
        Parallel.ForEach(needFull, options, file =>
        {
            fulls[file] = Hash(file, long.MaxValue);
            int n = Interlocked.Increment(ref done);
            if (n % 20 == 0) progress?.Report($"计算完整哈希… {n} / {total}");
        });
        cancel.ThrowIfCancellationRequested();

        var result = new List<List<FileInfo>>();
        foreach (var byHead in headGroups)
        {
            if (byHead.Key.Head.StartsWith("full:")) result.Add(byHead.ToList());
            else result.AddRange(byHead.Where(f => fulls[f] != null).GroupBy(f => fulls[f]).Where(g => g.Count() > 1).Select(g => g.ToList()));
        }
        return result.OrderByDescending(g => g[0].Length * (g.Count - 1)).ToList();
    }

    /// <summary>SHA256 of the first <paramref name="limit"/> bytes; prefixed "full:" when that covered the whole file.</summary>
    static string? Hash(FileInfo file, long limit)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long left = limit;
            int read;
            while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                left -= read;
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return (file.Length <= limit ? "full:" : "") + Convert.ToBase64String(sha.Hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Recursive file listing that skips folders it cannot read and does not follow junctions.</summary>
static class DiskWalker
{
    public static IEnumerable<FileInfo> Files(string folder, CancellationToken cancel)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(folder));
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                }
                else if (entry is FileInfo file) yield return file;
            }
        }
    }
}
