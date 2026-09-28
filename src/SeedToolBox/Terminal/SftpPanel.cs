using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>Remote file browser beside the terminal: follows the shell's directory, transfers with a queue, edits files in place.</summary>
sealed class SftpPanel : DockPanel
{
    sealed class Entry
    {
        public string Name { get; set; } = "";
        public string FullName = "";
        public bool IsDirectory;
        public bool IsLink;
        public long Length;
        public string Size { get; set; } = "";
        public string Modified { get; set; } = "";
        public string Permissions { get; set; } = "";
        public string Glyph { get; set; } = "";
        public string Owner { get; set; } = "";
    }

    sealed class Transfer : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public bool Upload;
        public long Total;
        long _done;
        string _state = "等待";
        public CancellationTokenSource Cancel = new();
        public long Done { get => _done; set { _done = value; Changed(nameof(Percent)); Changed(nameof(Text)); } }
        public double Percent => Total > 0 ? 100.0 * _done / Total : 0;
        public string State { get => _state; set { _state = value; Changed(nameof(Text)); } }
        public string Text => $"{(Upload ? "↑" : "↓")} {Name}  {Ui.FormatSize(_done)} / {Ui.FormatSize(Total)}  {_state}";
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    readonly TextBox _path = Ui.Field();
    readonly ListView _list = new() { BorderThickness = new Thickness(0) };
    readonly TextBlock _status = Ui.Status();
    readonly ToggleButton _follow = new() { Content = "跟随", IsChecked = true, Padding = new Thickness(6, 2, 6, 2), ToolTip = "终端切换目录时跟着切换（需要 shell 报告目录，bash/zsh 可自动开启）" };
    readonly ToggleButton _hidden = new() { Content = ".*", Padding = new Thickness(6, 2, 6, 2), ToolTip = "显示隐藏文件" };
    readonly ObservableCollection<Transfer> _transfers = new();
    readonly Dictionary<SshConnection, SftpClient> _clients = new();
    readonly List<FileSystemWatcher> _watchers = new();
    readonly Func<Window?> _owner;
    readonly Action<string> _cdInTerminal;
    SshConnection? _connection;
    string _current = "";
    int _version;

    /// <summary>The user turned on follow; the page can ask the shell to report its directory.</summary>
    public event Action? FollowEnabled;

    public SftpPanel(Func<Window?> owner, Action<string> cdInTerminal)
    {
        _owner = owner;
        _cdInTerminal = cdInTerminal;
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ApiUi.Icon("\uE74A", "上级目录", () => Go(ParentOf(_current))));
        buttons.Children.Add(ApiUi.Icon("\uE80F", "主目录", () => Go("~")));
        buttons.Children.Add(ApiUi.Icon("\uE72C", "刷新", () => Go(_current)));
        buttons.Children.Add(ApiUi.Icon("\uE8F4", "新建文件夹", NewFolder));
        buttons.Children.Add(ApiUi.Icon("\uE898", "上传文件…", UploadPick));
        buttons.Children.Add(_hidden);
        buttons.Children.Add(_follow);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_path);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        _path.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(_path.Text.Trim()); e.Handled = true; } };
        _hidden.Click += (_, _) => Go(_current);
        _follow.Checked += (_, _) => FollowEnabled?.Invoke();

        var queue = new ListBox { MaxHeight = 140, BorderThickness = new Thickness(0), ItemsSource = _transfers, Visibility = Visibility.Collapsed };
        queue.ItemTemplate = TransferTemplate();
        _transfers.CollectionChanged += (_, _) => queue.Visibility = _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var queueMenu = new ContextMenu();
        var cancel = new MenuItem { Header = "取消传输" };
        cancel.Click += (_, _) => { if (queue.SelectedItem is Transfer t) t.Cancel.Cancel(); };
        var clear = new MenuItem { Header = "清除已完成" };
        clear.Click += (_, _) => { foreach (var t in _transfers.Where(t => t.Cancel.IsCancellationRequested || t.State is "完成" or "失败" or "已取消").ToList()) _transfers.Remove(t); };
        queueMenu.Items.Add(cancel);
        queueMenu.Items.Add(clear);
        queue.ContextMenu = queueMenu;
        DockPanel.SetDock(queue, Dock.Bottom);
        Children.Add(queue);
        _status.Margin = new Thickness(0, 4, 0, 4);
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "名称", Width = 170, CellTemplate = NameTemplate() });
        grid.Columns.Add(new GridViewColumn { Header = "大小", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Entry.Size)) });
        grid.Columns.Add(new GridViewColumn { Header = "修改时间", Width = 118, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Entry.Modified)) });
        grid.Columns.Add(new GridViewColumn { Header = "权限", Width = 86, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Entry.Permissions)) });
        grid.Columns.Add(new GridViewColumn { Header = "所有者", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Entry.Owner)) });
        _list.View = grid;
        _list.SelectionMode = SelectionMode.Extended;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Entry e) Activate(e); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Entry en) { Activate(en); e.Handled = true; }
            else if (e.Key == Key.Back) { Go(ParentOf(_current)); e.Handled = true; }
            else if (e.Key == Key.Delete) { Delete(); e.Handled = true; }
            else if (e.Key == Key.F2) { Rename(); e.Handled = true; }
            else if (e.Key == Key.F5) { Go(_current); e.Handled = true; }
        };
        _list.ContextMenuOpening += (_, _) => _list.ContextMenu = ItemMenu();
        _list.ContextMenu = new ContextMenu();
        Ui.FileDrop(_list, files => _ = UploadAsync(files, _current));
        Children.Add(_list);
        Show("没有连接。选中一个 SSH 标签页后这里会显示服务器上的文件。");
    }

    static DataTemplate NameTemplate()
    {
        var f = new FrameworkElementFactory(typeof(StackPanel));
        f.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Entry.Glyph)));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 6, 0));
        icon.SetValue(TextBlock.ForegroundProperty, ApiUi.Frozen(0xD4, 0xA0, 0x17));
        f.AppendChild(icon);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Entry.Name)));
        f.AppendChild(name);
        return new DataTemplate { VisualTree = f };
    }

    static DataTemplate TransferTemplate()
    {
        var f = new FrameworkElementFactory(typeof(StackPanel));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Transfer.Text)));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        f.AppendChild(text);
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new System.Windows.Data.Binding(nameof(Transfer.Percent)) { Mode = System.Windows.Data.BindingMode.OneWay });
        bar.SetValue(FrameworkElement.HeightProperty, 4.0);
        bar.SetValue(FrameworkElement.WidthProperty, 280.0);
        bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        f.AppendChild(bar);
        return new DataTemplate { VisualTree = f };
    }

    void Show(string message)
    {
        _list.ItemsSource = null;
        Ui.SetStatus(_status, message);
    }

    // ---------- connection ----------

    public void Bind(SshConnection? connection, string? directory)
    {
        if (connection == _connection && connection != null) { if (directory != null && _follow.IsChecked == true) Follow(directory); return; }
        _connection = connection;
        _current = "";
        _path.Text = "";
        foreach (var dead in _clients.Keys.Where(c => !c.IsConnected).ToList()) { Dispose(_clients[dead]); _clients.Remove(dead); }
        if (connection == null) { Show("没有连接。选中一个 SSH 标签页后这里会显示服务器上的文件。"); return; }
        Go(directory ?? "~");
    }

    public void Follow(string directory)
    {
        if (_connection != null && _follow.IsChecked == true && directory != _current) Go(directory);
    }

    public bool IsBound => _connection != null;

    async Task<SftpClient> ClientAsync()
    {
        var connection = _connection ?? throw new InvalidOperationException("没有连接");
        if (_clients.TryGetValue(connection, out var c) && c.IsConnected) return c;
        if (c != null) Dispose(c);
        c = await Task.Run(connection.OpenSftp);
        _clients[connection] = c;
        return c;
    }

    static void Dispose(SftpClient c)
    {
        try { c.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
    }

    // ---------- listing ----------

    static string ParentOf(string path)
    {
        if (path.Length <= 1) return "/";
        var i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path.Substring(0, i);
    }

    static string Join(string dir, string name) => dir.EndsWith("/") ? dir + name : dir + "/" + name;

    async void Go(string path)
    {
        if (_connection == null) return;
        var version = ++_version;
        var hidden = _hidden.IsChecked == true;
        Ui.SetStatus(_status, "正在读取 " + path + " …");
        try
        {
            var client = await ClientAsync();
            var (full, entries) = await Task.Run(() =>
            {
                var target = path == "~" || path.Length == 0 ? client.WorkingDirectory : path.StartsWith("~/") ? Join(client.WorkingDirectory, path.Substring(2)) : path;
                var files = client.ListDirectory(target).Where(f => f.Name != "." && f.Name != "..").ToList();
                return (target, files);
            });
            if (version != _version) return;
            _current = full;
            _path.Text = full;
            var items = entries.Where(f => hidden || !f.Name.StartsWith("."))
                .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Entry
                {
                    Name = f.Name, FullName = f.FullName, IsDirectory = f.IsDirectory, IsLink = f.IsSymbolicLink, Length = f.Length,
                    Size = f.IsDirectory ? "" : Ui.FormatSize(f.Length),
                    Modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    Permissions = Mode(f),
                    Owner = f.UserId.ToString(),
                    Glyph = f.IsDirectory ? "\uE8B7" : f.IsSymbolicLink ? "\uE71B" : "\uE8A5",
                }).ToList();
            _list.ItemsSource = items;
            Ui.SetStatus(_status, $"{items.Count(i => i.IsDirectory)} 个文件夹，{items.Count(i => !i.IsDirectory)} 个文件" + (hidden ? "" : $"（隐藏了 {entries.Count - items.Count} 个）"));
        }
        catch (Exception ex) when (IsSftpError(ex))
        {
            if (version == _version) Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    static bool IsSftpError(Exception ex) => ex is SshException or IOException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or UnauthorizedAccessException;

    static string Mode(ISftpFile f)
    {
        char B(bool v, char c) => v ? c : '-';
        return (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-")
            + B(f.OwnerCanRead, 'r') + B(f.OwnerCanWrite, 'w') + B(f.OwnerCanExecute, 'x')
            + B(f.GroupCanRead, 'r') + B(f.GroupCanWrite, 'w') + B(f.GroupCanExecute, 'x')
            + B(f.OthersCanRead, 'r') + B(f.OthersCanWrite, 'w') + B(f.OthersCanExecute, 'x');
    }

    static string Octal(string mode)
    {
        int Part(int at) => (mode[at] == 'r' ? 4 : 0) + (mode[at + 1] == 'w' ? 2 : 0) + (mode[at + 2] == 'x' ? 1 : 0);
        return mode.Length < 10 ? "644" : $"{Part(1)}{Part(4)}{Part(7)}";
    }

    void Activate(Entry e)
    {
        if (e.IsDirectory || e.IsLink && e.Length < 4096 && !e.Name.Contains('.')) Go(e.FullName);
        else _ = EditAsync(e);
    }

    IEnumerable<Entry> Selected => _list.SelectedItems.OfType<Entry>().ToList();

    ContextMenu ItemMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled && _connection != null }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        var one = _list.SelectedItem as Entry;
        var any = _list.SelectedItems.Count > 0;
        Item(one?.IsDirectory == true ? "打开" : "编辑（保存后自动上传）", () => { if (one != null) Activate(one); }, one != null);
        Item("下载…", Download, any);
        Item("上传文件…", UploadPick);
        menu.Items.Add(new Separator());
        Item("重命名…", Rename, one != null);
        Item("修改权限…", Chmod, any);
        Item("删除", Delete, any);
        Item("新建文件夹…", NewFolder);
        Item("新建文件…", NewFile);
        menu.Items.Add(new Separator());
        Item("复制路径", () => Clipboard.SetText(one?.FullName ?? _current));
        Item("在终端中进入" + (one?.IsDirectory == true ? "这个文件夹" : "当前目录"), () => _cdInTerminal(one?.IsDirectory == true ? one.FullName : _current));
        Item("刷新", () => Go(_current));
        return menu;
    }

    // ---------- operations ----------

    async Task Run(string doing, Action<SftpClient> action, bool refresh = true)
    {
        try
        {
            var client = await ClientAsync();
            Ui.SetStatus(_status, doing + "…");
            await Task.Run(() => action(client));
            Ui.SetStatus(_status, doing + "完成");
            if (refresh) Go(_current);
        }
        catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, doing + "失败：" + ex.Message, true); }
    }

    void NewFolder()
    {
        var name = TerminalDialogs.Ask(_owner(), "新建文件夹", "文件夹名称：", false)?.Trim();
        if (!string.IsNullOrEmpty(name)) _ = Run("新建文件夹", c => c.CreateDirectory(Join(_current, name!)));
    }

    void NewFile()
    {
        var name = TerminalDialogs.Ask(_owner(), "新建文件", "文件名称：", false)?.Trim();
        if (!string.IsNullOrEmpty(name)) _ = Run("新建文件", c => { using var s = new MemoryStream(); c.UploadFile(s, Join(_current, name!), false); });
    }

    void Rename()
    {
        if (_list.SelectedItem is not Entry e) return;
        var name = TerminalDialogs.Ask(_owner(), "重命名", "新名称：", false, e.Name)?.Trim();
        if (!string.IsNullOrEmpty(name) && name != e.Name) _ = Run("重命名", c => c.RenameFile(e.FullName, Join(_current, name!)));
    }

    void Delete()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (!ApiDialogs.Confirm(_owner(), items.Count == 1 ? $"删除「{items[0].Name}」吗？{(items[0].IsDirectory ? "\n文件夹里的所有内容都会被删除。" : "")}" : $"删除选中的 {items.Count} 项吗？")) return;
        _ = Run("删除", c => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDirectory); });
    }

    static void DeleteRecursive(SftpClient c, string path, bool directory)
    {
        if (!directory) { c.DeleteFile(path); return; }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name != "." && f.Name != ".."))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink);
        c.DeleteDirectory(path);
    }

    void Chmod()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        var mode = TerminalDialogs.Ask(_owner(), "修改权限", $"八进制权限，例如 755、644（{items.Count} 项）：", false, Octal(items[0].Permissions))?.Trim();
        if (mode == null) return;
        if (mode.Length != 3 || mode.Any(ch => ch < '0' || ch > '7')) { Ui.SetStatus(_status, "权限要写成 3 位八进制数字，例如 755", true); return; }
        _ = Run("修改权限", c => { foreach (var i in items) c.ChangePermissions(i.FullName, short.Parse(mode)); });
    }

    // ---------- transfers ----------

    void UploadPick()
    {
        if (_connection == null) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = "上传到 " + _current };
        if (dlg.ShowDialog(_owner()) == true) _ = UploadAsync(dlg.FileNames, _current);
    }

    async Task UploadAsync(string[] paths, string remoteDir)
    {
        if (_connection == null) return;
        SftpClient client;
        try { client = await ClientAsync(); }
        catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, "上传失败：" + ex.Message, true); return; }
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var root = Join(remoteDir, Path.GetFileName(path));
                foreach (var dir in new[] { path }.Concat(Directory.GetDirectories(path, "*", SearchOption.AllDirectories)))
                {
                    var target = Join(root, dir.Substring(path.Length).Replace('\\', '/').TrimStart('/')).TrimEnd('/');
                    try { await Task.Run(() => { if (!client.Exists(target)) client.CreateDirectory(target); }); }
                    catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, "创建文件夹失败：" + ex.Message, true); return; }
                    foreach (var file in Directory.GetFiles(dir)) await UploadOne(client, file, Join(target, Path.GetFileName(file)));
                }
            }
            else if (File.Exists(path)) await UploadOne(client, path, Join(remoteDir, Path.GetFileName(path)));
        }
        if (remoteDir == _current) Go(_current);
    }

    async Task<bool> UploadOne(SftpClient client, string local, string remote)
    {
        var t = new Transfer { Name = Path.GetFileName(local), Upload = true, Total = new FileInfo(local).Length, State = "上传中" };
        _transfers.Insert(0, t);
        try
        {
            using var stream = File.OpenRead(local);
            await client.UploadFileAsync(stream, remote, true, new Progress<UploadFileProgressReport>(p => t.Done = (long)p.TotalBytesUploaded), t.Cancel.Token);
            t.Done = t.Total;
            t.State = "完成";
            return true;
        }
        catch (OperationCanceledException) { t.State = "已取消"; }
        catch (Exception ex) when (IsSftpError(ex)) { t.State = "失败：" + ex.Message; }
        return false;
    }

    void Download()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        string? folder;
        if (items.Count == 1 && !items[0].IsDirectory)
        {
            var dlg = new SaveFileDialog { FileName = items[0].Name, Title = "下载 " + items[0].Name };
            if (dlg.ShowDialog(_owner()) != true) return;
            _ = DownloadOne(items[0].FullName, dlg.FileName, items[0].Length);
            return;
        }
        var pick = new System.Windows.Forms.FolderBrowserDialog { Description = "下载到哪个文件夹？" };
        if (pick.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        folder = pick.SelectedPath;
        _ = DownloadMany(items, folder);
    }

    async Task DownloadMany(List<Entry> items, string folder)
    {
        SftpClient client;
        try { client = await ClientAsync(); }
        catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, "下载失败：" + ex.Message, true); return; }
        foreach (var e in items)
        {
            if (!e.IsDirectory) { await DownloadOne(e.FullName, Path.Combine(folder, e.Name), e.Length); continue; }
            List<(string Remote, string Local, long Length)> files;
            try
            {
                files = await Task.Run(() =>
                {
                    var list = new List<(string, string, long)>();
                    void Walk(string remote, string local)
                    {
                        Directory.CreateDirectory(local);
                        foreach (var f in client.ListDirectory(remote).Where(f => f.Name != "." && f.Name != ".."))
                            if (f.IsDirectory) Walk(f.FullName, Path.Combine(local, f.Name));
                            else if (f.IsRegularFile) list.Add((f.FullName, Path.Combine(local, f.Name), f.Length));
                    }
                    Walk(e.FullName, Path.Combine(folder, e.Name));
                    return list;
                });
            }
            catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, "下载失败：" + ex.Message, true); continue; }
            foreach (var f in files) await DownloadOne(f.Remote, f.Local, f.Length);
        }
        Ui.SetStatus(_status, "下载完成：" + folder);
    }

    async Task<bool> DownloadOne(string remote, string local, long length)
    {
        var t = new Transfer { Name = Path.GetFileName(local), Total = length, State = "下载中" };
        _transfers.Insert(0, t);
        try
        {
            var client = await ClientAsync();
            var tmp = local + ".part";
            using (var stream = File.Create(tmp))
                await client.DownloadFileAsync(remote, stream, new Progress<DownloadFileProgressReport>(p => t.Done = (long)p.TotalBytesDownloaded), t.Cancel.Token);
            if (File.Exists(local)) File.Delete(local);
            File.Move(tmp, local);
            t.Done = t.Total;
            t.State = "完成";
            return true;
        }
        catch (OperationCanceledException) { t.State = "已取消"; TryDelete(local + ".part"); }
        catch (Exception ex) when (IsSftpError(ex)) { t.State = "失败：" + ex.Message; TryDelete(local + ".part"); }
        return false;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------- edit in place ----------

    async Task EditAsync(Entry e)
    {
        if (e.Length > 50L * 1024 * 1024 && !ApiDialogs.Confirm(_owner(), $"「{e.Name}」有 {Ui.FormatSize(e.Length)}，确定下载下来编辑吗？")) return;
        var folder = Path.Combine(Path.GetTempPath(), "SeedToolBox", "sftp-edit", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, e.Name);
        if (!await DownloadOne(e.FullName, local, e.Length)) return;
        var connection = _connection;
        var remote = e.FullName;
        var watcher = new FileSystemWatcher(folder, e.Name) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        var lastWrite = File.GetLastWriteTimeUtc(local);
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (!File.Exists(local) || File.GetLastWriteTimeUtc(local) == lastWrite) return;
            lastWrite = File.GetLastWriteTimeUtc(local);
            if (connection != _connection || connection == null || !connection.IsConnected) { Ui.SetStatus(_status, $"「{e.Name}」已修改，但连接已经变了，没有上传", true); return; }
            try
            {
                var client = await ClientAsync();
                // Editors may still hold the file for a moment after saving.
                byte[] bytes = Array.Empty<byte>();
                for (var i = 0; i < 5; i++)
                {
                    try { bytes = File.ReadAllBytes(local); break; }
                    catch (IOException) when (i < 4) { await Task.Delay(200); }
                }
                using var stream = new MemoryStream(bytes);
                await client.UploadFileAsync(stream, remote, true, null, CancellationToken.None);
                Ui.SetStatus(_status, $"已上传「{e.Name}」 {DateTime.Now:HH:mm:ss}");
                if (ParentOf(remote) == _current) Go(_current);
            }
            catch (Exception ex) when (IsSftpError(ex)) { Ui.SetStatus(_status, $"上传「{e.Name}」失败：" + ex.Message, true); }
        };
        watcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        _watchers.Add(watcher);
        try
        {
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No program registered for this extension.
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + local + "\"") { UseShellExecute = true });
        }
        Ui.SetStatus(_status, $"已打开「{e.Name}」，保存后会自动上传");
    }

    public void CloseAll()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var t in _transfers) t.Cancel.Cancel();
        foreach (var c in _clients.Values) Dispose(c);
        _clients.Clear();
    }
}
