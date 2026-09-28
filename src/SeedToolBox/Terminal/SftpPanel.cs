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
using Renci.SshNet.Common;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>Remote file browser beside the terminal: follows the shell's directory, transfers through the 传输 tab, edits files in place.</summary>
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

    readonly TextBox _path = Ui.Field();
    readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBlock _status = Ui.Status();
    readonly ToggleButton _follow = new() { IsChecked = true, ToolTip = "定位：终端切换目录时跟着展开（需要 shell 报告目录，bash/zsh 可自动开启）" };
    readonly ToggleButton _hidden = new() { ToolTip = "显示隐藏文件" };
    /// <summary>Transfers started here, cancelled when the panel closes; the list itself lives in <see cref="Transfers"/>.</summary>
    readonly List<Transfer> _mine = new();
    readonly Dictionary<object, IRemoteFiles> _clients = new();
    readonly List<FileSystemWatcher> _watchers = new();
    readonly Func<Window?> _owner;
    readonly Action<string> _cdInTerminal;
    object? _source;
    Func<Task<IRemoteFiles>>? _open;
    bool _linked = true;
    TreeViewItem? _root;
    string _home = "/";
    string _current = "";
    int _version;

    /// <summary>The user turned on follow; the page can ask the shell to report its directory.</summary>
    public event Action? FollowEnabled;

    /// <summary>Favourite folders of the bound host; changes are saved through <see cref="FavoritesChanged"/>.</summary>
    public List<string>? Favorites { get; set; }
    public event Action? FavoritesChanged;

    static readonly SolidColorBrush FolderBrush = ApiUi.Frozen(0xE8, 0xB3, 0x39);
    static readonly SolidColorBrush Green = ApiUi.Frozen(0x3C, 0xBF, 0x6E);
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public SftpPanel(Func<Window?> owner, Action<string> cdInTerminal)
    {
        _owner = owner;
        _cdInTerminal = cdInTerminal;
        _path.Margin = new Thickness(0, 0, 0, 6);
        _path.ToolTip = "输入路径后按回车跳转";
        DockPanel.SetDock(_path, Dock.Top);
        Children.Add(_path);
        _path.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(_path.Text.Trim()); e.Handled = true; } };

        var bar = new DockPanel { Margin = new Thickness(-4, 0, 0, 4) };
        var upload = ApiUi.Icon("", "上传文件到选中的文件夹…", UploadPick);
        DockPanel.SetDock(upload, Dock.Right);
        bar.Children.Add(upload);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ApiUi.Icon("", "主目录", () => Go("~")));
        Style(_follow, "");
        buttons.Children.Add(_follow);
        buttons.Children.Add(ApiUi.Icon("", "全部折叠", CollapseAll));
        Style(_hidden, "");
        buttons.Children.Add(_hidden);
        buttons.Children.Add(ApiUi.Icon("", "刷新（F5）", () => Refresh(_current)));
        Button fav = null!;
        fav = ApiUi.Icon("", "收藏的文件夹", () => Open(FavoritesMenu(), fav));
        buttons.Children.Add(fav);
        Button more = null!;
        more = ApiUi.Icon("", "更多", () => Open(MoreMenu(), more));
        buttons.Children.Add(more);
        bar.Children.Add(buttons);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        _hidden.Click += (_, _) => Reload();
        _follow.Checked += (_, _) => FollowEnabled?.Invoke();

        _status.Margin = new Thickness(0, 4, 0, 4);
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        _tree.SelectedItemChanged += (_, _) =>
        {
            if (Selected.FirstOrDefault() is not { } e) return;
            _current = e.IsDirectory ? e.FullName : ParentOf(e.FullName);
            _path.Text = e.FullName;
        };
        _tree.MouseDoubleClick += (_, e) =>
        {
            if (Selected.FirstOrDefault() is { IsDirectory: false } en && Up(e.OriginalSource as DependencyObject)?.Tag == en) { Activate(en); e.Handled = true; }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Selected.FirstOrDefault() is { } en) { Activate(en); e.Handled = true; }
            else if (e.Key == Key.Delete) { Delete(); e.Handled = true; }
            else if (e.Key == Key.F2) { Rename(); e.Handled = true; }
            else if (e.Key == Key.F5) { Refresh(_current); e.Handled = true; }
        };
        _tree.PreviewMouseRightButtonDown += (_, e) => { if (Up(e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true; };
        _tree.ContextMenuOpening += (_, _) => _tree.ContextMenu = ItemMenu();
        _tree.ContextMenu = new ContextMenu();
        Ui.FileDrop(_tree, files => _ = UploadAsync(files, _current));
        Children.Add(_tree);
        Show("没有连接。选中一个 SSH 会话后这里会显示服务器上的文件。");
    }

    static void Style(ToggleButton b, string glyph)
    {
        b.Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12 };
        b.Padding = new Thickness(6, 4, 6, 4);
        b.Margin = new Thickness(1, 0, 1, 0);
        b.BorderThickness = new Thickness(0);
        b.Background = Brushes.Transparent;
        b.Checked += (_, _) => b.Foreground = Green;
        b.Unchecked += (_, _) => b.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        if (b.IsChecked == true) b.Foreground = Green;
    }

    static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    ContextMenu FavoritesMenu()
    {
        var menu = new ContextMenu();
        var list = Favorites;
        var add = new MenuItem { Header = list?.Contains(_current) == true ? "取消收藏当前文件夹" : "收藏当前文件夹", IsEnabled = list != null && _current.Length > 0 };
        add.Click += (_, _) =>
        {
            if (list == null) return;
            if (!list.Remove(_current)) list.Add(_current);
            FavoritesChanged?.Invoke();
        };
        menu.Items.Add(add);
        if (list is { Count: > 0 })
        {
            menu.Items.Add(new Separator());
            foreach (var path in list)
            {
                var mi = new MenuItem { Header = path };
                mi.Click += (_, _) => Go(path);
                menu.Items.Add(mi);
            }
        }
        return menu;
    }

    ContextMenu MoreMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled && _source != null }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("新建文件夹…", NewFolder);
        Item("新建文件…", NewFile);
        Item("上级目录", () => Go(ParentOf(_current)), _current.Length > 1);
        menu.Items.Add(new Separator());
        if (_linked) Item("在终端中进入当前文件夹", () => _cdInTerminal(_current), _current.Length > 0);
        Item("复制路径", () => Clipboard.SetText(_current), _current.Length > 0);
        menu.Items.Add(new Separator());
        var folder = new MenuItem { Header = "打开下载目录" };
        folder.Click += (_, _) => Transfers.OpenFolder(Transfers.LastFolder);
        menu.Items.Add(folder);
        return menu;
    }

    void Show(string message)
    {
        _tree.Items.Clear();
        _root = null;
        Ui.SetStatus(_status, message);
    }

    // ---------- connection ----------

    public void Bind(SshConnection? connection, string? directory) =>
        BindSource(connection, connection == null ? null : async () => new SftpFiles(await Task.Run(connection.OpenSftp)), directory);

    /// <summary>Browses a server that has no terminal (FTP); follow and cd are hidden.</summary>
    public void BindFiles(object key, Func<Task<IRemoteFiles>> open)
    {
        _linked = false;
        _follow.Visibility = Visibility.Collapsed;
        BindSource(key, open, null);
    }

    void BindSource(object? source, Func<Task<IRemoteFiles>>? open, string? directory)
    {
        if (source == _source && source != null) { if (directory != null && _follow.IsChecked == true) Follow(directory); return; }
        _source = source;
        _open = open;
        _current = "";
        _path.Text = "";
        foreach (var dead in _clients.Where(kv => !kv.Value.IsConnected).Select(kv => kv.Key).ToList()) { _clients[dead].Dispose(); _clients.Remove(dead); }
        if (source == null) { Show("没有连接。选中一个 SSH 会话后这里会显示服务器上的文件。"); return; }
        Show("");
        Go(directory ?? "~");
    }

    public void Follow(string directory)
    {
        if (_source != null && _follow.IsChecked == true && directory != _current) Go(directory);
    }

    public bool IsBound => _source != null;

    async Task<IRemoteFiles> ClientAsync()
    {
        var source = _source ?? throw new InvalidOperationException("没有连接");
        if (_clients.TryGetValue(source, out var c) && c.IsConnected) return c;
        c?.Dispose();
        c = await _open!();
        if (source != _source) { c.Dispose(); throw new InvalidOperationException("连接已经切换"); }
        _clients[source] = c;
        return c;
    }

    // ---------- tree ----------

    static string ParentOf(string path)
    {
        if (path.Length <= 1) return "/";
        var i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path.Substring(0, i);
    }

    static string Join(string dir, string name) => dir.EndsWith("/") ? dir + name : dir + "/" + name;

    /// <summary>Placeholder child so unloaded folders show an expander.</summary>
    const string NotLoaded = "…";

    TreeViewItem MakeItem(Entry e)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = e.Glyph, FontFamily = Icons, Margin = new Thickness(0, 1, 6, 0), Foreground = e.IsDirectory ? FolderBrush : ApiUi.Res("SecondaryTextBrush") });
        row.Children.Add(new TextBlock { Text = e.Name });
        if (!e.IsDirectory && e.Size.Length > 0) row.Children.Add(new TextBlock { Text = e.Size, Margin = new Thickness(8, 0, 0, 0), Foreground = ApiUi.Res("HintTextBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = row, Tag = e, Padding = new Thickness(1, 2, 4, 2), ToolTip = $"{e.Permissions}  {e.Modified}" + (e.IsDirectory ? "" : "  " + e.Size) };
        if (e.IsDirectory)
        {
            item.Items.Add(NotLoaded);
            item.Expanded += async (_, args) =>
            {
                if (args.OriginalSource != item) return;
                if (item.Items.Count == 1 && item.Items[0] is string) await LoadAsync(item);
            };
        }
        return item;
    }

    async Task<bool> LoadAsync(TreeViewItem item)
    {
        var dir = ((Entry)item.Tag).FullName;
        var hidden = _hidden.IsChecked == true;
        var version = _version;
        try
        {
            var client = await ClientAsync();
            var files = await Task.Run(() => client.List(dir));
            if (version != _version) return false;
            var expanded = new HashSet<string>(item.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded).Select(i => ((Entry)i.Tag).FullName));
            item.Items.Clear();
            var shown = files.Where(f => hidden || !f.Name.StartsWith(".")).ToList();
            foreach (var f in shown.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var child = MakeItem(new Entry
                {
                    Name = f.Name, FullName = f.FullName, IsDirectory = f.IsDirectory, IsLink = f.IsLink, Length = f.Length,
                    Size = f.IsDirectory ? "" : Ui.FormatSize(f.Length),
                    Modified = f.Modified.ToString("yyyy-MM-dd HH:mm"),
                    Permissions = f.Mode,
                    Owner = f.Owner,
                    Glyph = f.IsDirectory ? "" : f.IsLink ? "" : "",
                });
                item.Items.Add(child);
                if (expanded.Contains(f.FullName)) child.IsExpanded = true;
            }
            Ui.SetStatus(_status, $"{dir}：{shown.Count(f => f.IsDirectory)} 个文件夹，{shown.Count(f => !f.IsDirectory)} 个文件" + (hidden || files.Count == shown.Count ? "" : $"（隐藏了 {files.Count - shown.Count} 个）"));
            return true;
        }
        catch (Exception ex) when (IsSftpError(ex))
        {
            if (version == _version) Ui.SetStatus(_status, "读取 " + dir + " 失败：" + ex.Message, true);
            return false;
        }
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it.</summary>
    async void Go(string path)
    {
        if (_source == null) return;
        var version = ++_version;
        try
        {
            var client = await ClientAsync();
            if (version != _version) return;
            _home = client.Home;
            var target = path == "~" || path.Length == 0 ? _home : path.StartsWith("~/") ? Join(_home, path.Substring(2)) : path;
            if (!target.StartsWith("/")) target = Join(_current.Length > 0 ? _current : _home, target);
            if (_root == null)
            {
                _root = MakeItem(new Entry { Name = "/", FullName = "/", IsDirectory = true, Glyph = "" });
                _tree.Items.Add(_root);
            }
            var item = _root;
            if (item.Items.Count == 1 && item.Items[0] is string && !await LoadAsync(item)) return;
            item.IsExpanded = true;
            foreach (var part in target.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (version != _version) return;
                var next = item.Items.OfType<TreeViewItem>().FirstOrDefault(i => ((Entry)i.Tag).Name == part);
                if (next == null) break;
                item = next;
                if (!((Entry)item.Tag).IsDirectory) break;
                if (item.Items.Count == 1 && item.Items[0] is string && !await LoadAsync(item)) break;
                item.IsExpanded = true;
            }
            if (version != _version) return;
            item.IsSelected = true;
            _current = ((Entry)item.Tag).IsDirectory ? ((Entry)item.Tag).FullName : ParentOf(((Entry)item.Tag).FullName);
            _path.Text = ((Entry)item.Tag).FullName;
            await Dispatcher.InvokeAsync(() => item.BringIntoView(), DispatcherPriority.Background);
        }
        catch (Exception ex) when (IsSftpError(ex))
        {
            if (version == _version) Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    TreeViewItem? Find(string dir)
    {
        var item = _root;
        foreach (var part in dir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            item = item?.Items.OfType<TreeViewItem>().FirstOrDefault(i => ((Entry)i.Tag).Name == part);
            if (item == null) return null;
        }
        return item;
    }

    /// <summary>Re-reads one loaded folder, keeping what is expanded under it.</summary>
    void Refresh(string dir)
    {
        if (Find(dir) is { } item && !(item.Items.Count == 1 && item.Items[0] is string)) _ = LoadAsync(item);
        else if (_source != null && _root == null) Go(dir);
    }

    void Reload()
    {
        var current = _current;
        Show("");
        Go(current.Length > 0 ? current : "~");
    }

    void CollapseAll()
    {
        void Walk(ItemsControl c) { foreach (var i in c.Items.OfType<TreeViewItem>()) { Walk(i); if (i != _root) i.IsExpanded = false; } }
        if (_root != null) Walk(_root);
    }

    static bool IsSftpError(Exception ex) => ex is SshException or FluentFTP.Exceptions.FtpException or TimeoutException or IOException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or UnauthorizedAccessException;

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

    IEnumerable<Entry> Selected => (_tree.SelectedItem as TreeViewItem)?.Tag is Entry e ? new[] { e } : Array.Empty<Entry>();

    ContextMenu ItemMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled && _source != null }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        var one = Selected.FirstOrDefault();
        var any = one != null && one.FullName != "/";
        Item(one?.IsDirectory == true ? "刷新这个文件夹" : "编辑（保存后自动上传）", () => { if (one?.IsDirectory == true) Refresh(one.FullName); else if (one != null) Activate(one); }, one != null);
        Item("下载…", Download, any);
        Item("上传文件到这里…", UploadPick);
        var file = one is { IsDirectory: false };
        Item("与本地文件比较…", () => _ = CompareLocal(one!), file);
        Item(Marked == null ? "标记用于比较" : "与已标记的「" + Marked.Value.Title + "」比较", () => _ = CompareMarked(one!), file);
        menu.Items.Add(new Separator());
        Item("重命名…", Rename, any);
        Item("修改权限…", Chmod, any);
        Item("删除", Delete, any);
        Item("新建文件夹…", NewFolder);
        Item("新建文件…", NewFile);
        menu.Items.Add(new Separator());
        Item("复制路径", () => Clipboard.SetText(one?.FullName ?? _current));
        if (_linked) Item("在终端中进入" + (one?.IsDirectory == true ? "这个文件夹" : "所在文件夹"), () => _cdInTerminal(_current));
        if (Favorites != null && one?.IsDirectory == true)
            Item(Favorites.Contains(one.FullName) ? "取消收藏" : "收藏这个文件夹", () => { if (!Favorites.Remove(one.FullName)) Favorites.Add(one.FullName); FavoritesChanged?.Invoke(); });
        return menu;
    }

    static TreeViewItem? Up(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    // ---------- operations ----------

    async Task Run(string doing, Action<IRemoteFiles> action, bool refresh = true)
    {
        try
        {
            var client = await ClientAsync();
            Ui.SetStatus(_status, doing + "…");
            await Task.Run(() => action(client));
            Ui.SetStatus(_status, doing + "完成");
            if (refresh) { Refresh(_current); Refresh(ParentOf(_current)); }
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
        if (!string.IsNullOrEmpty(name)) _ = Run("新建文件", c => c.CreateEmpty(Join(_current, name!)));
    }

    void Rename()
    {
        if (Selected.FirstOrDefault() is not { } e) return;
        var name = TerminalDialogs.Ask(_owner(), "重命名", "新名称：", false, e.Name)?.Trim();
        if (!string.IsNullOrEmpty(name) && name != e.Name) _ = Run("重命名", c => c.Rename(e.FullName, Join(_current, name!)));
    }

    void Delete()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (!ApiDialogs.Confirm(_owner(), items.Count == 1 ? $"删除「{items[0].Name}」吗？{(items[0].IsDirectory ? "\n文件夹里的所有内容都会被删除。" : "")}" : $"删除选中的 {items.Count} 项吗？")) return;
        _ = Run("删除", c => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDirectory); });
    }

    static void DeleteRecursive(IRemoteFiles c, string path, bool directory)
    {
        if (!directory) { c.DeleteFile(path); return; }
        foreach (var f in c.List(path))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsLink);
        c.DeleteDirectory(path);
    }

    void Chmod()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        var mode = TerminalDialogs.Ask(_owner(), "修改权限", $"八进制权限，例如 755、644（{items.Count} 项）：", false, Octal(items[0].Permissions))?.Trim();
        if (mode == null) return;
        if (mode.Length != 3 || mode.Any(ch => ch < '0' || ch > '7')) { Ui.SetStatus(_status, "权限要写成 3 位八进制数字，例如 755", true); return; }
        _ = Run("修改权限", c => { foreach (var i in items) c.Chmod(i.FullName, short.Parse(mode)); });
    }

    // ---------- transfers ----------

    void UploadPick()
    {
        if (_source == null) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = "上传到 " + _current };
        if (dlg.ShowDialog(_owner()) == true) _ = UploadAsync(dlg.FileNames, _current);
    }

    async Task UploadAsync(string[] paths, string remoteDir)
    {
        if (_source == null) return;
        IRemoteFiles client;
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
        Refresh(remoteDir);
    }

    async Task<bool> UploadOne(IRemoteFiles client, string local, string remote)
    {
        var t = Start(new Transfer { Name = Path.GetFileName(local), Upload = true, Local = local, Remote = remote, Total = new FileInfo(local).Length, State = "上传中" });
        try
        {
            using var stream = File.OpenRead(local);
            await client.UploadAsync(stream, remote, n => t.Done = n, t.Cancel.Token);
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
            var dlg = new SaveFileDialog { FileName = items[0].Name, Title = "下载 " + items[0].Name, InitialDirectory = Transfers.LastFolder };
            if (dlg.ShowDialog(_owner()) != true) return;
            _ = DownloadOne(items[0].FullName, dlg.FileName, items[0].Length);
            return;
        }
        var pick = new System.Windows.Forms.FolderBrowserDialog { Description = "下载到哪个文件夹？", SelectedPath = Transfers.LastFolder };
        if (pick.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        folder = pick.SelectedPath;
        _ = DownloadMany(items, folder);
    }

    async Task DownloadMany(List<Entry> items, string folder)
    {
        IRemoteFiles client;
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
                        foreach (var f in client.List(remote))
                            if (f.IsDirectory) Walk(f.FullName, Path.Combine(local, f.Name));
                            else if (f.IsRegular) list.Add((f.FullName, Path.Combine(local, f.Name), f.Length));
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

    /// <summary>Server name shown in the 传输 tab.</summary>
    public string ServerName { get; set; } = "";

    Transfer Start(Transfer t)
    {
        t.Server = ServerName;
        _mine.RemoveAll(x => !x.Active);
        _mine.Add(t);
        Transfers.Add(t);
        Ui.SetStatus(_status, $"{(t.Upload ? "上传" : "下载")}「{t.Name}」… 进度和历史在「传输」页签");
        return t;
    }

    async Task<bool> DownloadOne(string remote, string local, long length)
    {
        var t = Start(new Transfer { Name = Path.GetFileName(local), Local = local, Remote = remote, Total = length, State = "下载中" });
        try
        {
            var client = await ClientAsync();
            var tmp = local + ".part";
            using (var stream = File.Create(tmp))
                await client.DownloadAsync(remote, stream, n => t.Done = n, t.Cancel.Token);
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

    /// <summary>A remote file picked for comparison; may belong to another panel or server.</summary>
    static (string Title, Func<Task<string>> Read)? Marked;

    async Task<string> ReadText(IRemoteFiles client, string remote)
    {
        using var stream = new MemoryStream();
        await client.DownloadAsync(remote, stream, null, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    Func<Task<string>> Reader(Entry e)
    {
        var source = _source;
        var remote = e.FullName;
        return async () =>
        {
            if (source != _source) throw new InvalidOperationException("连接已经切换");
            return await ReadText(await ClientAsync(), remote);
        };
    }

    bool TooBig(Entry e) => e.Length > 5L * 1024 * 1024 && !ApiDialogs.Confirm(_owner(), $"「{e.Name}」有 {Ui.FormatSize(e.Length)}，确定要比较吗？");

    async Task CompareLocal(Entry e)
    {
        if (TooBig(e)) return;
        var dlg = new OpenFileDialog { Title = "选择要和「" + e.Name + "」比较的本地文件", FileName = e.Name };
        if (dlg.ShowDialog(_owner()) != true) return;
        await ShowDiff(e.FullName + "  ↔  " + dlg.FileName, Reader(e), () => Task.FromResult(File.ReadAllText(dlg.FileName)));
    }

    async Task CompareMarked(Entry e)
    {
        if (Marked is not { } marked)
        {
            if (TooBig(e)) return;
            Marked = (e.Name, Reader(e));
            Ui.SetStatus(_status, $"已标记「{e.Name}」，再右键另一个远程文件（可以在其他服务器上）选择比较");
            return;
        }
        Marked = null;
        await ShowDiff(marked.Title + "  ↔  " + e.FullName, marked.Read, Reader(e));
    }

    async Task ShowDiff(string title, Func<Task<string>> left, Func<Task<string>> right)
    {
        Ui.SetStatus(_status, "正在读取文件…");
        string a, b;
        try { a = await left(); b = await right(); }
        catch (Exception ex) when (IsSftpError(ex) || ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
            return;
        }
        Ui.SetStatus(_status, "");
        var page = new DiffPage { Margin = new Thickness(12) };
        var window = new Window { Title = "比较 " + title, Width = 1100, Height = 760, Content = page };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        TerminalDialogs.Place(window, _owner());
        window.Show();
        page.Load(a, b);
    }

    async Task EditAsync(Entry e)
    {
        if (e.Length > 50L * 1024 * 1024 && !ApiDialogs.Confirm(_owner(), $"「{e.Name}」有 {Ui.FormatSize(e.Length)}，确定下载下来编辑吗？")) return;
        var folder = Path.Combine(Path.GetTempPath(), "SeedToolBox", "sftp-edit", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, e.Name);
        if (!await DownloadOne(e.FullName, local, e.Length)) return;
        var source = _source;
        var remote = e.FullName;
        var watcher = new FileSystemWatcher(folder, e.Name) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        var lastWrite = File.GetLastWriteTimeUtc(local);
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (!File.Exists(local) || File.GetLastWriteTimeUtc(local) == lastWrite) return;
            lastWrite = File.GetLastWriteTimeUtc(local);
            if (source != _source || source == null) { Ui.SetStatus(_status, $"「{e.Name}」已修改，但连接已经变了，没有上传", true); return; }
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
                await client.UploadAsync(stream, remote, null, CancellationToken.None);
                Ui.SetStatus(_status, $"已上传「{e.Name}」 {DateTime.Now:HH:mm:ss}");
                Refresh(ParentOf(remote));
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
        foreach (var t in _mine) t.Cancel.Cancel();
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
