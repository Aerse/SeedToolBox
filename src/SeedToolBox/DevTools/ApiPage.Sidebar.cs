using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.DevTools;

sealed partial class ApiPage
{
    readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, AllowDrop = true };
    readonly ListBox _history = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox _filter = Ui.Field();
    readonly HashSet<object> _expanded = new();
    Point _dragStart;

    UIElement BuildSidebar()
    {
        var tabs = new TabControl();

        var dock = new DockPanel();
        var newMenu = new ContextMenu();
        newMenu.Items.Add(MenuItem("新建请求", () => OpenTab(null, new ApiRequest())));
        newMenu.Items.Add(MenuItem("新建集合", NewCollection));
        var importMenu = new ContextMenu();
        importMenu.Items.Add(MenuItem("Postman 集合或环境文件…", ImportFile));
        importMenu.Items.Add(MenuItem("cURL 命令…", ImportCurl));
        var toolbar = Ui.Row(MenuButton("新建", newMenu), MenuButton("导入", importMenu));
        toolbar.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        _filter.Tag = "搜索请求";
        _filter.ToolTip = "按名称或 URL 过滤";
        _filter.Margin = new Thickness(0, 0, 0, 6);
        _filter.TextChanged += (_, _) => RefreshTree();
        DockPanel.SetDock(_filter, Dock.Top);
        dock.Children.Add(_filter);
        dock.Children.Add(_tree);
        _tree.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        _tree.PreviewMouseMove += TreeDragStart;
        _tree.Drop += TreeDrop;
        _tree.DragOver += (_, e) => { e.Effects = DropTarget(e) is { } t && CanDrop(DragNode(e), t) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        tabs.Items.Add(new TabItem { Header = "集合", Content = dock });

        var historyDock = new DockPanel();
        var clear = Ui.Button("清空", () =>
        {
            if (_data.History.Count == 0 || !ApiDialogs.Confirm(Owner, "清空全部请求历史？")) return;
            _data.History.Clear();
            Persist();
            RefreshHistory();
        });
        var historyBar = Ui.Row(clear);
        historyBar.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(historyBar, Dock.Top);
        historyDock.Children.Add(historyBar);
        historyDock.Children.Add(_history);
        _history.MouseDoubleClick += (_, _) =>
        {
            if (_history.SelectedItem is ListBoxItem { Tag: ApiHistoryEntry h }) OpenTab(null, h.Request.Clone(newId: true));
        };
        tabs.Items.Add(new TabItem { Header = "历史", Content = historyDock });
        return tabs;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    static Button MenuButton(string text, ContextMenu menu)
    {
        Button? b = null;
        b = Ui.Button(text + " ▾", () => { menu.PlacementTarget = b; menu.IsOpen = true; });
        return b;
    }

    void NewCollection()
    {
        var name = Ai.AiService.AskText(Owner, "新建集合", "集合名称", "新集合");
        if (string.IsNullOrWhiteSpace(name)) return;
        var c = new ApiCollection { Name = name!.Trim() };
        _data.Collections.Add(c);
        _expanded.Add(c);
        Persist();
        RefreshTree();
    }

    // ---------- tree ----------

    void RefreshTree()
    {
        _tree.Items.Clear();
        var filter = _filter.Text.Trim();
        foreach (var c in _data.Collections)
            if (FolderNode(c, filter) is { } node) _tree.Items.Add(node);
        if (_data.Collections.Count == 0)
            _tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "还没有集合，点「新建」或「导入」", Foreground = Views.DialogWindow.HintBrush }, IsEnabled = false });
    }

    static bool Matches(ApiRequest r, string filter) =>
        filter.Length == 0 || r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || r.Url.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    TreeViewItem? FolderNode(ApiFolder folder, string filter)
    {
        var item = new TreeViewItem { Tag = folder, Header = NodeHeader(folder is ApiCollection ? "" : "", folder.Name, null) };
        foreach (var f in folder.Folders)
            if (FolderNode(f, filter) is { } sub) item.Items.Add(sub);
        foreach (var r in folder.Requests.Where(r => Matches(r, filter)))
        {
            var ri = new TreeViewItem { Tag = r, Header = NodeHeader(null, r.Name, r.Method) };
            foreach (var ex in r.Examples)
            {
                var ei = new TreeViewItem { Tag = ex, Header = NodeHeader("", ex.Name, null) };
                ei.MouseDoubleClick += (_, e) => { if (e.Source == ei) { ShowExample(r, ex); e.Handled = true; } };
                ei.ContextMenu = new ContextMenu { Items = { MenuItem("删除示例", () => { r.Examples.Remove(ex); Persist(); RefreshTree(); }) } };
                ri.Items.Add(ei);
            }
            ri.IsExpanded = _expanded.Contains(r);
            ri.Expanded += (_, e) => { if (e.Source == ri) _expanded.Add(r); };
            ri.Collapsed += (_, e) => { if (e.Source == ri) _expanded.Remove(r); };
            ri.MouseDoubleClick += (_, e) => { if (e.Source == ri) { OpenTab(r); e.Handled = true; } };
            ri.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenTab(r); };
            ri.ContextMenu = RequestMenu(r);
            item.Items.Add(ri);
        }
        if (filter.Length > 0 && item.Items.Count == 0 && folder.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return null;
        item.IsExpanded = filter.Length > 0 || _expanded.Contains(folder);
        item.Expanded += (_, e) => { if (e.Source == item) _expanded.Add(folder); };
        item.Collapsed += (_, e) => { if (e.Source == item) _expanded.Remove(folder); };
        item.ContextMenu = FolderMenu(folder);
        return item;
    }

    static StackPanel NodeHeader(string? glyph, string name, string? method)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (method != null)
            panel.Children.Add(new TextBlock { Text = ApiUi.MethodShort(method), Foreground = ApiUi.MethodBrush(method), FontSize = 10, FontWeight = FontWeights.Bold, Width = 40, VerticalAlignment = VerticalAlignment.Center });
        else
            panel.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    ContextMenu FolderMenu(ApiFolder folder)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("新建请求", () =>
        {
            var r = new ApiRequest { Name = "新请求" };
            folder.Requests.Add(r);
            _expanded.Add(folder);
            Persist();
            RefreshTree();
            OpenTab(r);
        }));
        menu.Items.Add(MenuItem("新建文件夹", () =>
        {
            var name = Ai.AiService.AskText(Owner, "新建文件夹", "文件夹名称", "新文件夹");
            if (string.IsNullOrWhiteSpace(name)) return;
            folder.Folders.Add(new ApiFolder { Name = name!.Trim() });
            _expanded.Add(folder);
            Persist();
            RefreshTree();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("运行…", () => new RunnerWindow(Owner, _data, folder, Persist).Show()));
        menu.Items.Add(MenuItem(folder is ApiCollection ? "集合设置（认证、脚本、变量）…" : "文件夹设置（认证、脚本）…", () => { FolderWindow.Show(Owner, folder, Persist); RefreshTree(); }));
        menu.Items.Add(MenuItem("重命名…", () => Rename(folder.Name, n => folder.Name = n)));
        if (folder is ApiCollection c)
        {
            menu.Items.Add(MenuItem("复制一份", () =>
            {
                var copy = JsonConvert.DeserializeObject<ApiCollection>(JsonConvert.SerializeObject(c))!;
                copy.Name += " 副本";
                copy.Id = Guid.NewGuid().ToString("N");
                foreach (var r in copy.AllRequests()) r.Id = Guid.NewGuid().ToString("N");
                foreach (var f in copy.AllFolders()) f.Id = Guid.NewGuid().ToString("N");
                _data.Collections.Insert(_data.Collections.IndexOf(c) + 1, copy);
                Persist();
                RefreshTree();
            }));
            menu.Items.Add(MenuItem("导出为 Postman v2.1…", () => ExportCollection(c)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("删除", () =>
        {
            var count = folder.AllRequests().Count();
            if (!ApiDialogs.Confirm(Owner, $"删除「{folder.Name}」和其中的 {count} 个请求？")) return;
            if (folder is ApiCollection col) _data.Collections.Remove(col);
            else foreach (var root in _data.Collections) if (ApiTree.Remove(root, folder)) break;
            DetachTabs();
            Persist();
            RefreshTree();
        }));
        return menu;
    }

    ContextMenu RequestMenu(ApiRequest r)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("打开", () => OpenTab(r)));
        menu.Items.Add(MenuItem("重命名…", () => Rename(r.Name, n =>
        {
            r.Name = n;
            foreach (var t in _tabs.Where(t => t.Saved == r)) { t.Draft.Name = n; UpdateChip(t); }
            if (_current?.Saved == r) LoadEditor(_current.Draft);
        })));
        menu.Items.Add(MenuItem("复制一份", () =>
        {
            var parent = ApiTree.ParentOf(_data, r);
            if (parent == null) return;
            var copy = r.Clone(newId: true);
            copy.Name += " 副本";
            parent.Requests.Insert(parent.Requests.IndexOf(r) + 1, copy);
            Persist();
            RefreshTree();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("删除", () =>
        {
            if (!ApiDialogs.Confirm(Owner, $"删除请求「{r.Name}」？")) return;
            foreach (var root in _data.Collections) if (ApiTree.Remove(root, r)) break;
            DetachTabs();
            Persist();
            RefreshTree();
        }));
        return menu;
    }

    /// <summary>Tabs whose request was deleted keep their draft as an unsaved request.</summary>
    void DetachTabs()
    {
        foreach (var t in _tabs.Where(t => t.Saved != null && ApiTree.PathOf(_data, t.Saved) == null))
        {
            t.Saved = null;
            UpdateChip(t);
        }
    }

    void Rename(string current, Action<string> apply)
    {
        var name = Ai.AiService.AskText(Owner, "重命名", "新名称", current);
        if (string.IsNullOrWhiteSpace(name)) return;
        apply(name!.Trim());
        Persist();
        RefreshTree();
    }

    // ---------- drag and drop ----------

    static object? DragNode(DragEventArgs e) => e.Data.GetDataPresent("ApiNode") ? e.Data.GetData("ApiNode") : null;

    void TreeDragStart(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _filter.Text.Length > 0) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemAt(e.OriginalSource as DependencyObject) is not { Tag: ApiRequest or ApiFolder } item) return;
        DragDrop.DoDragDrop(item, new DataObject("ApiNode", item.Tag), DragDropEffects.Move);
    }

    static TreeViewItem? ItemAt(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    static object? DropTarget(DragEventArgs e) => ItemAt(e.OriginalSource as DependencyObject)?.Tag;

    bool CanDrop(object? source, object target)
    {
        if (source == null || ReferenceEquals(source, target) || target is ApiExample) return false;
        if (source is ApiCollection) return target is ApiCollection;
        if (source is ApiFolder f)
        {
            var into = target as ApiFolder ?? ApiTree.ParentOf(_data, target);
            return into != null && !ApiTree.Contains(f, into);
        }
        return source is ApiRequest;
    }

    void TreeDrop(object sender, DragEventArgs e)
    {
        var source = DragNode(e);
        var target = DropTarget(e);
        e.Handled = true;
        if (target == null || !CanDrop(source, target)) return;
        if (source is ApiCollection sc && target is ApiCollection tc)
        {
            _data.Collections.Remove(sc);
            _data.Collections.Insert(_data.Collections.IndexOf(tc), sc);
        }
        else
        {
            var into = target as ApiFolder ?? ApiTree.ParentOf(_data, target)!;
            foreach (var root in _data.Collections) if (ApiTree.Remove(root, source!)) break;
            if (source is ApiRequest r)
            {
                var index = target is ApiRequest tr ? into.Requests.IndexOf(tr) : -1;
                if (index >= 0) into.Requests.Insert(index, r); else into.Requests.Add(r);
            }
            else if (source is ApiFolder f)
            {
                var index = target is ApiFolder tf && tf != into ? into.Folders.IndexOf(tf) : -1;
                if (index >= 0) into.Folders.Insert(index, f); else into.Folders.Add(f);
            }
            _expanded.Add(into);
        }
        Persist();
        RefreshTree();
    }

    // ---------- import and export ----------

    void ImportFile()
    {
        var dialog = new OpenFileDialog { Filter = "Postman 文件 (*.json)|*.json|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(Owner) != true) return;
        var done = new List<string>();
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                if (ApiImport.IsPostmanCollection(o))
                {
                    var c = ApiImport.ReadCollection(o);
                    _data.Collections.Add(c);
                    _expanded.Add(c);
                    done.Add("集合「" + c.Name + "」");
                }
                else if (ApiImport.IsPostmanEnvironment(o))
                {
                    var env = ApiImport.ReadEnvironment(o);
                    if (o["_postman_variable_scope"]?.ToString() == "globals")
                    {
                        foreach (var v in env.Variables) ApiVariables.Set(_data.Globals, v.Key, v.Value);
                        done.Add("全局变量");
                    }
                    else
                    {
                        _data.Environments.Add(env);
                        done.Add("环境「" + env.Name + "」");
                    }
                }
                else done.Add(Path.GetFileName(path) + " 不是 Postman 集合或环境");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidCastException or ArgumentException)
            {
                done.Add(Path.GetFileName(path) + " 读取失败：" + ex.Message);
            }
        }
        Persist();
        RefreshTree();
        RefreshEnvironments();
        Ui.SetStatus(_status, "导入：" + string.Join("，", done));
    }

    void ImportCurl()
    {
        var box = Ui.Area(wrap: true);
        box.Height = 220;
        box.Width = 520;
        var ok = Views.DialogWindow.OkButton("导入");
        var body = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = "粘贴 cURL 命令（bash 或 cmd 格式都可以）", Margin = new Thickness(0, 0, 0, 8) }, box } };
        var window = Views.DialogWindow.Create("从 cURL 导入", body, ok, Views.DialogWindow.CancelButton());
        window.Owner = Owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) => window.DialogResult = true;
        if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("curl", StringComparison.OrdinalIgnoreCase)) box.Text = Clipboard.GetText();
        if (window.ShowDialog() != true) return;
        var r = ApiImport.ParseCurl(box.Text);
        if (r == null) { Ui.SetStatus(_status, "没认出 cURL 命令", true); return; }
        r.Name = GuessName(r);
        OpenTab(null, r);
    }

    void ExportCollection(ApiCollection c)
    {
        var dialog = new SaveFileDialog { Filter = "Postman 集合 (*.json)|*.json", FileName = c.Name + ".postman_collection.json" };
        if (dialog.ShowDialog(Owner) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ApiImport.WriteCollection(c).ToString(Formatting.Indented));
            Ui.SetStatus(_status, "已导出到 " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.SetStatus(_status, "导出失败：" + ex.Message, true); }
    }

    // ---------- history ----------

    void RefreshHistory()
    {
        _history.Items.Clear();
        for (var i = _data.History.Count - 1; i >= 0; i--)
        {
            var h = _data.History[i];
            var panel = new DockPanel();
            var status = new TextBlock
            {
                Text = h.Status > 0 ? h.Status.ToString() : "失败",
                Foreground = h.Status > 0 ? ApiUi.StatusBrush(h.Status) : ApiUi.Res("DangerBrush"),
                Margin = new Thickness(6, 0, 0, 0), FontSize = 11,
            };
            DockPanel.SetDock(status, Dock.Right);
            panel.Children.Add(status);
            var line = NodeHeader(null, h.Request.Url.Length > 0 ? h.Request.Url : h.Request.Name, h.Request.Method);
            ((TextBlock)line.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(line);
            _history.Items.Add(new ListBoxItem { Content = panel, Tag = h, ToolTip = $"{h.Time:yyyy-MM-dd HH:mm:ss}  {h.Milliseconds} ms\n双击在新标签页打开" });
        }
    }
}
