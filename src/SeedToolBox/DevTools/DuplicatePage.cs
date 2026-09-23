using System;
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
    readonly TextBox _folder = Ui.Field(250);
    readonly TextBox _minSize = Ui.Field(60);
    readonly TextBlock _status = Ui.Status();
    readonly Button _scan;
    CancellationTokenSource? _cancel;

    public DuplicatePage()
    {
        var header = Ui.Header("重复文件查找", "按内容查找完全相同的文件（大小 → 部分哈希 → 完整 SHA256），勾选后移到回收站");
        _minSize.Text = "1";
        var browse = Ui.Button("浏览…", Browse);
        browse.Margin = new Thickness(8, 0, 16, 0);
        _scan = Ui.Button("开始查找", Scan, accent: true);
        var row1 = Ui.Row(Ui.Label("文件夹"), _folder, browse, Ui.Label("最小"), _minSize, Ui.Label("KB", 16), _scan, Ui.Button("停止", () => _cancel?.Cancel()));
        var row2 = Ui.Row(Ui.Button("每组保留最早的", () => AutoCheck(keepOldest: true)), Ui.Button("每组保留最新的", () => AutoCheck(keepOldest: false)), Ui.Button("取消勾选", () => { foreach (var i in _items) i.Check = false; }), Ui.Label("", 16), Ui.Button("删除勾选（回收站）", DeleteChecked));
        Ui.FileDrop(_folder, p => { if (Directory.Exists(p[0])) _folder.Text = p[0]; });

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
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is DuplicateItem item) ProcessLauncher.OpenLocation(item.Path); };
        _list.ToolTip = "双击打开所在位置";

        SetDock(header, Dock.Top);
        SetDock(row1, Dock.Top);
        SetDock(row2, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(row1);
        Children.Add(row2);
        Children.Add(_status);
        Children.Add(_list);
    }

    static readonly DependencyProperty ToggleButtonIsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    async void Scan()
    {
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { Ui.SetStatus(_status, "请选择存在的文件夹", true); return; }
        if (!long.TryParse(_minSize.Text, out var minKb) || minKb < 0) minKb = 0;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _items.Clear();
        _scan.IsEnabled = false;
        var progress = new Progress<string>(s => Ui.SetStatus(_status, s));
        try
        {
            var groups = await Task.Run(() => Duplicates.Find(folder, Math.Max(1, minKb * 1024), progress, cancel.Token));
            int n = 0;
            foreach (var group in groups)
            {
                n++;
                foreach (var file in group) _items.Add(new DuplicateItem { Path = file.FullName, Size = file.Length, Modified = file.LastWriteTime, Group = n.ToString() });
            }
            long wasted = groups.Sum(g => g[0].Length * (g.Count - 1));
            Ui.SetStatus(_status, groups.Count == 0 ? "没有找到重复文件" : $"找到 {groups.Count} 组重复文件，共可释放 {Ui.FormatSize(wasted)}");
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

static class Duplicates
{
    public static List<List<FileInfo>> Find(string folder, long minSize, IProgress<string>? progress, CancellationToken cancel)
    {
        var files = new List<FileInfo>();
        foreach (var file in DiskWalker.Files(folder, cancel))
        {
            if (file.Length < minSize) continue;
            files.Add(file);
            if (files.Count % 2000 == 0) progress?.Report($"扫描中… 已找到 {files.Count} 个文件");
        }

        var result = new List<List<FileInfo>>();
        var candidates = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
        int done = 0, total = candidates.Sum(g => g.Count());
        foreach (var bySize in candidates)
        {
            // Cheap first pass over the head of the file, then the full hash only for files that still collide
            foreach (var byHead in bySize.GroupBy(f => Hash(f, 64 * 1024)).Where(g => g.Key != null && g.Count() > 1))
            {
                cancel.ThrowIfCancellationRequested();
                var groups = byHead.Key!.StartsWith("full:")
                    ? new[] { byHead.ToList() }
                    : byHead.GroupBy(f => Hash(f, long.MaxValue)).Where(g => g.Key != null && g.Count() > 1).Select(g => g.ToList()).ToArray();
                result.AddRange(groups.Where(g => g.Count > 1));
            }
            done += bySize.Count();
            progress?.Report($"比较内容… {done} / {total}");
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
