using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

sealed partial class TerminalPage
{
    sealed record GroupNode(string Path);

    readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox _search = Ui.Field();
    readonly HashSet<string> _collapsed = new();
    Point _dragStart;
    object? _dragSource;

    FrameworkElement BuildSidebar()
    {
        var panel = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ApiUi.Icon("\uE710", "新建主机", () => NewHost(SelectedGroup())));
        buttons.Children.Add(ApiUi.Icon("\uE8F4", "新建分组", () => NewGroup(SelectedGroup())));
        var import = ApiUi.Icon("\uE8B5", "导入主机", () => { });
        import.Click += (_, _) => { var m = ImportMenu(); m.PlacementTarget = import; m.IsOpen = true; };
        buttons.Children.Add(import);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new TextBlock { Text = "主机", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);

        _search.Margin = new Thickness(0, 0, 0, 6);
        _search.ToolTip = "搜索名称、地址、用户或备注";
        _search.TextChanged += (_, _) => RefreshTree();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && FirstHost(_tree.Items) is { } h) { OpenHost(h); e.Handled = true; }
            else if (e.Key == Key.Escape) _search.Clear();
        };
        DockPanel.SetDock(_search, Dock.Top);
        panel.Children.Add(_search);

        _tree.MouseDoubleClick += (_, e) =>
        {
            if (Source(e.OriginalSource) is TreeViewItem { Tag: var tag } item && item.IsSelected)
            {
                if (tag is HostEntry h) { OpenHost(h); e.Handled = true; }
                else if (tag is ShellProfile s) { OpenLocal(s); e.Handled = true; }
            }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (_tree.SelectedItem is not TreeViewItem { Tag: var tag }) return;
            if (e.Key == Key.Enter) { if (tag is HostEntry h) OpenHost(h); else if (tag is ShellProfile s) OpenLocal(s); e.Handled = true; }
            else if (e.Key == Key.Delete && tag is HostEntry dh) { DeleteHost(dh); e.Handled = true; }
            else if (e.Key == Key.F2 && tag is HostEntry eh) { EditHost(eh); e.Handled = true; }
        };
        _tree.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(_tree); _dragSource = Source(e.OriginalSource)?.Tag; };
        _tree.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragSource is not (HostEntry or GroupNode)) return;
            var d = e.GetPosition(_tree) - _dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var source = _dragSource;
            _dragSource = null;
            DragDrop.DoDragDrop(_tree, new DataObject(typeof(object), source), DragDropEffects.Move);
        };
        _tree.AllowDrop = true;
        _tree.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData(typeof(object)) is HostEntry or GroupNode ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        _tree.Drop += (_, e) => { OnDrop(e.Data.GetData(typeof(object)), Source(e.OriginalSource)?.Tag); e.Handled = true; };
        _tree.ContextMenu = new ContextMenu();
        _tree.ContextMenuOpening += (_, e) =>
        {
            var item = Source(e.OriginalSource);
            if (item != null) item.IsSelected = true;
            _tree.ContextMenu = TreeMenu(item?.Tag);
        };
        panel.Children.Add(_tree);
        return panel;
    }

    static TreeViewItem? Source(object? element)
    {
        var d = element as DependencyObject;
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    static HostEntry? FirstHost(ItemCollection items)
    {
        foreach (TreeViewItem i in items)
        {
            if (i.Tag is HostEntry h) return h;
            if (FirstHost(i.Items) is { } inner) return inner;
        }
        return null;
    }

    string SelectedGroup() => _tree.SelectedItem switch
    {
        TreeViewItem { Tag: GroupNode g } => g.Path,
        TreeViewItem { Tag: HostEntry h } => h.Group,
        _ => "",
    };

    void RefreshTree()
    {
        if (!_tree.IsLoaded && _tree.Items.Count == 0 && _sessions.Count > 0) { }
        var selected = (_tree.SelectedItem as TreeViewItem)?.Tag;
        _tree.Items.Clear();
        var query = _search.Text.Trim();
        var online = new HashSet<string>(AllTabs.SelectMany(t => t.Panes).Where(p => p.Host != null && p.Open).Select(p => p.Host!.Id));

        if (query.Length > 0)
        {
            foreach (var h in _data.Hosts.Where(h => Matches(h, query)).OrderBy(h => h.Title, StringComparer.CurrentCultureIgnoreCase))
                _tree.Items.Add(HostItem(h, online, showGroup: true));
            if (_tree.Items.Count == 0) _tree.Items.Add(new TreeViewItem { Header = "没有匹配的主机", IsEnabled = false });
            return;
        }

        var local = new TreeViewItem { Header = Header("\uE756", "本地终端", null, null), Tag = new GroupNode("\0local"), IsExpanded = !_collapsed.Contains("\0local") };
        Track(local, "\0local");
        foreach (var s in _shells) local.Items.Add(new TreeViewItem { Header = Header("\uE756", s.Name, null, s.Id == DefaultShell().Id ? "默认" : null), Tag = s, ToolTip = s.Command + " " + s.Arguments });
        _tree.Items.Add(local);

        var nodes = new Dictionary<string, TreeViewItem>();
        TreeViewItem Folder(string path)
        {
            if (nodes.TryGetValue(path, out var n)) return n;
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var count = _data.Hosts.Count(h => h.Group == path || h.Group.StartsWith(path + "/"));
            n = new TreeViewItem { Header = Header("\uE8B7", name, null, count > 0 ? count.ToString() : null), Tag = new GroupNode(path), IsExpanded = !_collapsed.Contains(path) };
            Track(n, path);
            nodes[path] = n;
            var slash = path.LastIndexOf('/');
            (slash < 0 ? _tree.Items : Folder(path.Substring(0, slash)).Items).Add(n);
            return n;
        }
        foreach (var g in _data.AllGroups()) Folder(g);
        foreach (var h in _data.Hosts)
            (h.Group.Length == 0 ? _tree.Items : Folder(h.Group).Items).Add(HostItem(h, online, showGroup: false));
        if (_data.Hosts.Count == 0)
            _tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "还没有主机。点上面的 + 新建，\n或者从 ~/.ssh/config、Xshell、FinalShell 导入。", Foreground = ApiUi.Res("HintTextBrush"), FontSize = 11 }, Focusable = false });
        if (selected != null) Reselect(_tree.Items, selected);
    }

    void Track(TreeViewItem item, string key)
    {
        item.Expanded += (_, e) => { if (e.OriginalSource == item) _collapsed.Remove(key); };
        item.Collapsed += (_, e) => { if (e.OriginalSource == item) _collapsed.Add(key); };
    }

    static bool Reselect(ItemCollection items, object tag)
    {
        foreach (var o in items)
        {
            if (o is not TreeViewItem i) continue;
            if (Equals(i.Tag, tag)) { i.IsSelected = true; return true; }
            if (Reselect(i.Items, tag)) return true;
        }
        return false;
    }

    static bool Matches(HostEntry h, string q) =>
        new[] { h.Name, h.Host, h.User, h.Notes, h.Group }.Any(s => s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);

    TreeViewItem HostItem(HostEntry h, HashSet<string> online, bool showGroup)
    {
        var item = new TreeViewItem
        {
            Header = Header(online.Contains(h.Id) ? "\uE703" : "\uE7F4", h.Title, h.Color, showGroup && h.Group.Length > 0 ? h.Group : h.Name.Length > 0 ? h.Address : null, online.Contains(h.Id)),
            Tag = h,
            ToolTip = h.Address + (h.JumpHostId.Length > 0 && _data.Find(h.JumpHostId) is { } j ? "\n经跳板机 " + j.Title : "")
                + (h.LastConnected is { } t ? $"\n上次连接 {t:yyyy-MM-dd HH:mm}" : "") + (h.Notes.Length > 0 ? "\n" + h.Notes : ""),
        };
        return item;
    }

    static FrameworkElement Header(string glyph, string text, string? color, string? note, bool online = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        if (glyph.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = online ? ApiUi.Frozen(0x2E, 0xCC, 0x71) : ApiUi.Res("HintTextBrush"),
            });
        if (!string.IsNullOrEmpty(color))
            row.Children.Add(new Border { Width = 4, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (note != null) row.Children.Add(new TextBlock { Text = note, Foreground = ApiUi.Res("HintTextBrush"), FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    ContextMenu TreeMenu(object? tag)
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        switch (tag)
        {
            case HostEntry h:
                Item("连接", () => OpenHost(h));
                Item("连接并打开 SFTP", () => { OpenHost(h); ShowSide(SideSftp); });
                Item("连接并打开监控", () => { OpenHost(h); ShowSide(SideMonitor); });
                menu.Items.Add(new Separator());
                Item("编辑…", () => EditHost(h));
                Item("复制一份", () =>
                {
                    var copy = h.Clone(newId: true);
                    copy.Name = h.Title + " 副本";
                    copy.LastConnected = null;
                    _data.Hosts.Insert(_data.Hosts.IndexOf(h) + 1, copy);
                    ScheduleSave();
                    RefreshTree();
                });
                Item("复制 ssh 命令", () => TrySetClipboard(SshCommand(h)));
                Item("在系统终端中打开 ssh", () => OpenLocalSsh(h));
                menu.Items.Add(new Separator());
                Item("删除", () => DeleteHost(h));
                break;
            case GroupNode { Path: "\0local" }:
                foreach (var s in _shells) Item("打开 " + s.Name, () => OpenLocal(s));
                break;
            case ShellProfile s:
                Item("打开", () => OpenLocal(s));
                Item("设为默认", () => { _data.Settings.DefaultShell = s.Id; ScheduleSave(); RefreshTree(); });
                break;
            case GroupNode g:
                Item("在这里新建主机…", () => NewHost(g.Path));
                Item("新建子分组…", () => NewGroup(g.Path));
                Item("全部连接", () =>
                {
                    var hosts = _data.Hosts.Where(x => x.Group == g.Path || x.Group.StartsWith(g.Path + "/")).ToList();
                    if (hosts.Count > 8 && !ApiDialogs.Confirm(Owner, $"要同时连接 {hosts.Count} 台主机吗？")) return;
                    foreach (var x in hosts) OpenHost(x);
                });
                Item("重命名…", () => RenameGroup(g.Path));
                Item("删除分组", () => DeleteGroup(g.Path));
                break;
            default:
                Item("新建主机…", () => NewHost(""));
                Item("新建分组…", () => NewGroup(""));
                menu.Items.Add(new Separator());
                foreach (MenuItem mi in ImportMenu().Items.OfType<MenuItem>().ToList()) { ((ItemsControl)mi.Parent).Items.Remove(mi); menu.Items.Add(mi); }
                break;
        }
        return menu;
    }

    static string SshCommand(HostEntry h) =>
        "ssh " + (h.Port != 22 ? $"-p {h.Port} " : "") + (h.Auth == AuthKinds.Key && h.KeyPath.Length > 0 ? $"-i \"{h.KeyPath}\" " : "") + $"{h.User}@{h.Host}";

    void OpenLocalSsh(HostEntry h)
    {
        var shell = _shells.FirstOrDefault(s => s.Id is "pwsh" or "powershell") ?? DefaultShell();
        var session = NewSession(null, shell);
        session.CustomTitle = h.Title;
        var pane = AddPane(NewTab(session), null, shell);
        SelectSession(session);
        void Send(TerminalView _) { pane.View.Ready -= Send; Dispatcher.BeginInvoke(new Action(() => pane.View.Send(SshCommand(h) + "\r")), System.Windows.Threading.DispatcherPriority.Background); }
        if (pane.View.IsReady) Send(pane.View); else pane.View.Ready += Send;
    }

    // ---------- host and group editing ----------

    void NewHost(string group)
    {
        var h = TerminalDialogs.EditHost(Owner, _data, new HostEntry { Group = group }, isNew: true);
        if (h == null) return;
        _data.Hosts.Add(h);
        ScheduleSave();
        RefreshTree();
        Reselect(_tree.Items, h);
    }

    void EditHost(HostEntry h)
    {
        var edited = TerminalDialogs.EditHost(Owner, _data, h, isNew: false);
        if (edited == null) return;
        _data.Hosts[_data.Hosts.IndexOf(h)] = edited;
        // Open panes keep their HostEntry; point them at the new one so reconnects use the new settings.
        foreach (var p in AllTabs.SelectMany(t => t.Panes).Where(p => p.Host == h)) p.Host = edited;
        foreach (var t in AllTabs) UpdateTab(t);
        ScheduleSave();
        RefreshTree();
    }

    void DeleteHost(HostEntry h)
    {
        if (!ApiDialogs.Confirm(Owner, $"删除主机「{h.Title}」吗？")) return;
        _data.Hosts.Remove(h);
        _data.History.Remove(h.Id);
        foreach (var other in _data.Hosts.Where(x => x.JumpHostId == h.Id)) other.JumpHostId = "";
        ScheduleSave();
        RefreshTree();
    }

    void NewGroup(string parent)
    {
        var name = TerminalDialogs.Ask(Owner, "新建分组", parent.Length > 0 ? $"在「{parent}」下新建分组：" : "分组名称：", false);
        name = name?.Trim().Replace("/", "／");
        if (string.IsNullOrEmpty(name)) return;
        var path = parent.Length > 0 ? parent + "/" + name : name!;
        if (!_data.Groups.Contains(path)) _data.Groups.Add(path);
        _collapsed.Remove(parent);
        ScheduleSave();
        RefreshTree();
    }

    void RenameGroup(string path)
    {
        var old = path.Substring(path.LastIndexOf('/') + 1);
        var name = TerminalDialogs.Ask(Owner, "重命名分组", "新名称：", false, old)?.Trim().Replace("/", "／");
        if (string.IsNullOrEmpty(name) || name == old) return;
        var slash = path.LastIndexOf('/');
        MoveGroup(path, (slash < 0 ? "" : path.Substring(0, slash + 1)) + name);
    }

    void MoveGroup(string from, string to)
    {
        string Map(string g) => g == from ? to : g.StartsWith(from + "/") ? to + g.Substring(from.Length) : g;
        foreach (var h in _data.Hosts) h.Group = Map(h.Group);
        _data.Groups = _data.Groups.Select(Map).Distinct().ToList();
        ScheduleSave();
        RefreshTree();
    }

    void DeleteGroup(string path)
    {
        var hosts = _data.Hosts.Where(h => h.Group == path || h.Group.StartsWith(path + "/")).ToList();
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path.Substring(0, slash);
        if (hosts.Count > 0 && !ApiDialogs.Confirm(Owner, $"分组「{path}」里有 {hosts.Count} 台主机，删除分组后它们会移到{(parent.Length > 0 ? "「" + parent + "」" : "最外层")}。继续吗？")) return;
        foreach (var h in hosts) h.Group = parent;
        _data.Groups.RemoveAll(g => g == path || g.StartsWith(path + "/"));
        ScheduleSave();
        RefreshTree();
    }

    void OnDrop(object? dragged, object? target)
    {
        var group = target switch { GroupNode { Path: "\0local" } => null, GroupNode g => g.Path, HostEntry h => h.Group, ShellProfile => null, _ => "" };
        if (group == null) return;
        if (dragged is HostEntry host)
        {
            if (target is HostEntry before && before != host)
            {
                _data.Hosts.Remove(host);
                _data.Hosts.Insert(_data.Hosts.IndexOf(before), host);
            }
            host.Group = group;
            ScheduleSave();
            RefreshTree();
            Reselect(_tree.Items, host);
        }
        else if (dragged is GroupNode { Path: var from } && from != "\0local")
        {
            if (group == from || group.StartsWith(from + "/")) return;
            var name = from.Substring(from.LastIndexOf('/') + 1);
            var to = group.Length > 0 ? group + "/" + name : name;
            if (to != from) MoveGroup(from, to);
        }
    }
}
