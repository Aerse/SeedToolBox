using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;
using SeedToolBox.Views;

namespace SeedToolBox.Terminal;

/// <summary>Saved command snippets with {{placeholders}}, and the command history of the active host.</summary>
sealed class SnippetPanel : DockPanel
{
    static readonly Regex Placeholder = new(@"\{\{\s*([^}:]+?)\s*(?::([^}]*))?\}\}");

    readonly TerminalData _data;
    readonly Func<Window?> _owner;
    readonly Action<string, bool, bool> _send;
    readonly Action _save;
    readonly TextBox _filter = Ui.Field();
    readonly ListBox _snippets = new() { BorderThickness = new Thickness(0) };
    readonly ListBox _history = new() { BorderThickness = new Thickness(0) };
    readonly CheckBox _all = new() { Content = "发到本标签页所有分屏", Margin = new Thickness(0, 4, 0, 4) };
    string _historyKey = "local";

    /// <param name="send">(text, press Enter, to every pane of the tab)</param>
    readonly TabControl _tabs = new() { BorderThickness = new Thickness(0) };

    /// <summary>Shows the snippets or the history tab.</summary>
    public void ShowTab(bool history) => _tabs.SelectedIndex = history ? 1 : 0;

    public SnippetPanel(TerminalData data, Func<Window?> owner, Action<string, bool, bool> send, Action save)
    {
        _data = data;
        _owner = owner;
        _send = send;
        _save = save;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ApiUi.Icon("\uE710", "新建片段", () => Edit(null)));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        _filter.ToolTip = "搜索片段和历史";
        _filter.TextChanged += (_, _) => Refresh();
        head.Children.Add(_filter);
        DockPanel.SetDock(head, Dock.Top);
        Children.Add(head);
        DockPanel.SetDock(_all, Dock.Top);
        Children.Add(_all);

        var tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = "命令片段", Content = _snippets });
        var historyDock = new DockPanel();
        var clear = Ui.Button("清空这台主机的历史", () =>
        {
            if (!ApiDialogs.Confirm(_owner(), "清空这台主机的命令历史吗？")) return;
            _data.History.Remove(_historyKey);
            _save();
            Refresh();
        });
        clear.Margin = new Thickness(0, 4, 0, 0);
        DockPanel.SetDock(clear, Dock.Bottom);
        historyDock.Children.Add(clear);
        historyDock.Children.Add(_history);
        tabs.Items.Add(new TabItem { Header = "历史命令", Content = historyDock });
        Children.Add(tabs);

        _snippets.MouseDoubleClick += (_, _) => SendSelected();
        _snippets.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SendSelected(); e.Handled = true; }
            else if (e.Key == Key.Delete && Selected is { } s) { Delete(s); e.Handled = true; }
            else if (e.Key == Key.F2 && Selected is { } s2) { Edit(s2); e.Handled = true; }
        };
        _snippets.ContextMenuOpening += (_, _) => _snippets.ContextMenu = SnippetMenu();
        _snippets.ContextMenu = new ContextMenu();
        _history.MouseDoubleClick += (_, _) => { if (_history.SelectedItem is ListBoxItem { Tag: string c }) _send(c, false, _all.IsChecked == true); };
        _history.KeyDown += (_, e) => { if (e.Key == Key.Enter && _history.SelectedItem is ListBoxItem { Tag: string c }) { _send(c, true, _all.IsChecked == true); e.Handled = true; } };
        var hm = new ContextMenu();
        void H(string header, Action<string> a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => { if (_history.SelectedItem is ListBoxItem { Tag: string c }) a(c); }; hm.Items.Add(mi); }
        H("插入到终端", c => _send(c, false, _all.IsChecked == true));
        H("执行", c => _send(c, true, _all.IsChecked == true));
        H("存为片段…", c => Edit(null, c));
        H("复制", c => Clipboard.SetText(c));
        H("从历史中删除", c => { if (_data.History.TryGetValue(_historyKey, out var l)) l.Remove(c); _save(); Refresh(); });
        _history.ContextMenu = hm;
        Refresh();
    }

    Snippet? Selected => (_snippets.SelectedItem as ListBoxItem)?.Tag as Snippet;

    public void SetHistoryKey(string key)
    {
        _historyKey = key;
        RefreshHistory();
    }

    public void Refresh()
    {
        var q = _filter.Text.Trim();
        bool M(string s) => q.Length == 0 || s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        _snippets.Items.Clear();
        foreach (var g in _data.Snippets.Where(s => M(s.Name) || M(s.Command) || M(s.Group) || M(s.Description)).GroupBy(s => s.Group).OrderBy(g => g.Key))
        {
            if (g.Key.Length > 0) _snippets.Items.Add(new ListBoxItem { Content = new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Foreground = ApiUi.Res("HintTextBrush") }, Focusable = false, IsHitTestVisible = false });
            foreach (var s in g)
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = s.Name.Length > 0 ? s.Name : s.Command });
                stack.Children.Add(new TextBlock { Text = s.Command.Replace("\n", " ⏎ "), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = ApiUi.Res("HintTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                _snippets.Items.Add(new ListBoxItem { Content = stack, Tag = s, ToolTip = s.Description.Length > 0 ? s.Description + "\n\n" + s.Command : s.Command });
            }
        }
        if (_data.Snippets.Count == 0)
            _snippets.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "还没有片段。点 + 新建，或在终端里选中文字后右键「存为命令片段」。\n命令里可以写 {{名称}} 或 {{名称:默认值}}，发送时会让你填写。", TextWrapping = TextWrapping.Wrap, Foreground = ApiUi.Res("HintTextBrush") }, Focusable = false });
        RefreshHistory();
    }

    void RefreshHistory()
    {
        var q = _filter.Text.Trim();
        _history.Items.Clear();
        if (!_data.History.TryGetValue(_historyKey, out var list)) return;
        foreach (var c in Enumerable.Reverse(list).Where(c => q.Length == 0 || c.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).Take(500))
            _history.Items.Add(new ListBoxItem { Content = c, Tag = c, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), ToolTip = c });
    }

    ContextMenu SnippetMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        var s = Selected;
        Item("发送并执行", () => SendSelected(true), s != null);
        Item("只插入不执行", () => SendSelected(false), s != null);
        Item("发到所有分屏并执行", () => { if (s != null && Fill(s.Command) is { } t) _send(t, true, true); }, s != null);
        menu.Items.Add(new Separator());
        Item("编辑…", () => Edit(s), s != null);
        Item("复制命令", () => Clipboard.SetText(s!.Command), s != null);
        Item("删除", () => Delete(s!), s != null);
        menu.Items.Add(new Separator());
        Item("新建片段…", () => Edit(null));
        return menu;
    }

    void SendSelected(bool? run = null)
    {
        if (Selected is not { } s) return;
        var text = Fill(s.Command);
        if (text != null) _send(text, run ?? s.Run, _all.IsChecked == true);
    }

    void Delete(Snippet s)
    {
        if (!ApiDialogs.Confirm(_owner(), $"删除片段「{(s.Name.Length > 0 ? s.Name : s.Command)}」吗？")) return;
        _data.Snippets.Remove(s);
        _save();
        Refresh();
    }

    /// <summary>Replaces {{name:default}} placeholders by asking for each one; null when cancelled.</summary>
    public string? Fill(string command)
    {
        var names = Placeholder.Matches(command).Cast<Match>().GroupBy(m => m.Groups[1].Value).Select(g => (Name: g.Key, Default: g.First().Groups[2].Value)).ToList();
        if (names.Count == 0) return command;
        var boxes = new Dictionary<string, TextBox>();
        var body = new StackPanel { MinWidth = 380 };
        foreach (var (name, def) in names)
        {
            var box = new TextBox { Style = DialogWindow.TextBoxStyle, Text = def };
            boxes[name] = box;
            body.Children.Add(TerminalDialogs.Line(name, box));
        }
        var ok = DialogWindow.OkButton("发送");
        var window = DialogWindow.Create("填写参数", body, ok, DialogWindow.CancelButton());
        TerminalDialogs.Place(window, _owner());
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { var first = boxes.Values.First(); first.Focus(); first.SelectAll(); };
        if (window.ShowDialog() != true) return null;
        return Replace(command, n => boxes.TryGetValue(n, out var b) ? b.Text : null);
    }

    public static string Replace(string command, Func<string, string?> value) =>
        Placeholder.Replace(command, m => value(m.Groups[1].Value) ?? m.Groups[2].Value);

    public void Edit(Snippet? original, string? command = null)
    {
        var s = original ?? new Snippet { Command = command ?? "" };
        var name = new TextBox { Style = DialogWindow.TextBoxStyle, Text = s.Name };
        var group = new ComboBox { IsEditable = true, Text = s.Group, ItemsSource = _data.Snippets.Select(x => x.Group).Where(g => g.Length > 0).Distinct().ToList() };
        var cmd = new TextBox { Style = DialogWindow.TextBoxStyle, Text = s.Command, AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
        var desc = new TextBox { Style = DialogWindow.TextBoxStyle, Text = s.Description };
        var run = new CheckBox { Content = "发送后按回车执行", IsChecked = s.Run };
        var body = new StackPanel { Width = 460 };
        body.Children.Add(TerminalDialogs.Line("名称", name));
        body.Children.Add(TerminalDialogs.Line("分组", group));
        body.Children.Add(TerminalDialogs.Line("命令", cmd, "可以写 {{名称}} 或 {{名称:默认值}}，发送时填写"));
        body.Children.Add(TerminalDialogs.Line("说明", desc));
        body.Children.Add(TerminalDialogs.Line("", run));
        var ok = DialogWindow.OkButton("保存");
        var window = DialogWindow.Create(original == null ? "新建命令片段" : "编辑命令片段", body, ok, DialogWindow.CancelButton());
        TerminalDialogs.Place(window, _owner());
        ok.IsDefault = false;
        ok.Click += (_, _) => { if (cmd.Text.Trim().Length > 0) window.DialogResult = true; };
        window.Loaded += (_, _) => (s.Command.Length > 0 ? name : cmd).Focus();
        if (window.ShowDialog() != true) return;
        s.Name = name.Text.Trim();
        s.Group = group.Text.Trim();
        s.Command = cmd.Text.TrimEnd();
        s.Description = desc.Text.Trim();
        s.Run = run.IsChecked == true;
        if (original == null) _data.Snippets.Add(s);
        _save();
        Refresh();
    }
}
