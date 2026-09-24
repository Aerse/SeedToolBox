using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Clips;

/// <summary>The full clipboard history in the toolbox: list on the left, the whole entry on the right.</summary>
sealed class ClipboardPage : DockPanel
{
    const string AllTags = "全部标签";

    readonly ClipboardHistory _history;
    readonly TextBox _search = Ui.Field(200);
    readonly ComboBox _tagFilter = new() { MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _empty = new() { Foreground = DialogWindow.HintBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBox _text = Ui.Area(wrap: true);
    readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _tags = Ui.Field(220);
    readonly TextBlock _status = Ui.Status();
    readonly Button _pin, _edit, _cancelEdit;
    ClipboardEntry? _editing;
    bool _dirty = true, _updatingTags;

    public ClipboardPage(ClipboardHistory history)
    {
        _history = history;
        var header = Ui.Header("剪贴板历史", $"自动记录复制过的文字、富文本、文件和图片；在任意位置按 {history.Settings.Hotkey} 可快速呼出并粘贴");
        _search.TextChanged += (_, _) => Refresh();
        _search.ToolTip = "搜索文字或标签，输入“图片”“文件”只看这类，#标签 只搜标签";
        _tagFilter.SelectionChanged += (_, _) => { if (!_updatingTags) Refresh(); };
        _pin = Ui.Button("置顶", () => { if (Selected is { } e) { history.TogglePin(e); Refresh(); } });
        _edit = Ui.Button("编辑", ToggleEdit);
        _cancelEdit = Ui.Button("取消", () => { EndEdit(save: false); if (_dirty) Refresh(); });
        _cancelEdit.Visibility = Visibility.Collapsed;
        var toolbar = Ui.Row(Ui.Label("搜索"), _search, _tagFilter, Ui.Label("", 4),
            Ui.Button("复制", Copy, accent: true),
            Ui.Button("纯文本复制", () => Copy(plainText: true)),
            Ui.Button("合并复制", CopyMerged),
            _pin,
            Ui.Button("删除", DeleteSelected),
            Ui.Label("", 4),
            Ui.Button("导出…", Export),
            Ui.Button("清空未置顶", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), "删除所有未置顶的剪贴板记录？", "剪贴板历史", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) history.Clear();
            }));

        _list.ItemContainerStyle = (Style)Application.Current.FindResource("ListCard");
        _list.BorderThickness = new Thickness(1);
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _list.Padding = new Thickness(4);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        _list.SelectionChanged += (_, _) => ShowSelected();
        _list.MouseDoubleClick += (_, _) => Copy();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) DeleteSelected();
            else if (e.Key == Key.Enter && _list.SelectedItems.Count > 1) CopyMerged();
            else if (e.Key == Key.Enter) Copy(plainText: Keyboard.Modifiers == ModifierKeys.Shift);
            else return;
            e.Handled = true;
        };

        _text.IsReadOnly = true;
        _tags.ToolTip = "用逗号或空格分隔多个标签，清空即删除全部标签";
        _tags.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveTags(); e.Handled = true; } };
        var tagRow = Ui.Row(Ui.Label("标签"), _tags, Ui.Button("保存标签", SaveTags), Ui.Label("", 4), _edit, _cancelEdit);
        tagRow.Margin = new Thickness(0, 0, 0, 8);
        var detail = new DockPanel();
        SetDock(tagRow, Dock.Top);
        detail.Children.Add(tagRow);
        detail.Children.Add(new Grid { Children = { _text, new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var left = new Grid { Children = { _list, _empty } };
        Grid.SetColumn(detail, 2);
        body.Children.Add(left);
        body.Children.Add(detail);

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);

        history.Entries.CollectionChanged += (_, _) => Changed();
        history.EntryChanged += Changed;
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Refresh(); };
    }

    void Changed()
    {
        if (IsVisible) Refresh();
        else _dirty = true;
    }

    ClipboardEntry? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ClipboardEntry;

    /// <summary>Selected entries in list order.</summary>
    List<ClipboardEntry> SelectedEntries => _list.Items.Cast<ListBoxItem>().Where(i => i.IsSelected).Select(i => (ClipboardEntry)i.Tag).ToList();

    void Refresh()
    {
        _dirty = false;
        // Leaving an unsaved edit would lose it silently; keep the text box as is until saved or cancelled
        if (_editing != null && _history.Entries.Contains(_editing))
        {
            _dirty = true;
            return;
        }
        _editing = null;
        RefreshTags();
        var query = _search.Text.Trim();
        var tag = _tagFilter.SelectedItem as string;
        if (tag == AllTags) tag = null;
        var entries = _history.Entries
            .Where(e => ClipboardWindow.Matches(e, query))
            .Where(e => tag == null || e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Pinned)
            .ToList();
        var selected = SelectedEntries;
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(CreateItem(entry));
        _empty.Text = _history.Entries.Count == 0 ? "还没有记录，复制点东西试试" : "没有匹配的记录";
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = _list.Items.Cast<ListBoxItem>().Where(i => selected.Contains((ClipboardEntry)i.Tag)).ToList();
        if (keep.Count == 0 && _list.Items.Count > 0) keep.Add((ListBoxItem)_list.Items[0]);
        foreach (var item in keep) item.IsSelected = true;
        ShowSelected();
        var paused = _history.Recording ? "" : "（记录已暂停）";
        Ui.SetStatus(_status, $"共 {_history.Entries.Count} 条，置顶 {_history.Entries.Count(e => e.Pinned)} 条{paused}  双击或 Enter 复制，Shift+Enter 纯文本，Ctrl/Shift+单击多选后合并复制");
    }

    void RefreshTags()
    {
        var current = _tagFilter.SelectedItem as string ?? AllTags;
        var tags = _history.AllTags();
        var items = new List<string> { AllTags };
        items.AddRange(tags);
        if (_tagFilter.Items.Cast<string>().SequenceEqual(items)) return;
        _updatingTags = true;
        _tagFilter.Items.Clear();
        foreach (var t in items) _tagFilter.Items.Add(t);
        _tagFilter.SelectedItem = items.FirstOrDefault(t => string.Equals(t, current, StringComparison.OrdinalIgnoreCase)) ?? AllTags;
        _updatingTags = false;
    }

    ListBoxItem CreateItem(ClipboardEntry entry)
    {
        var hint = DialogWindow.HintBrush;
        var preview = entry.Text != null ? ClipboardWindow.Preview(entry, 3, 200) : $"[图片] {entry.ImageWidth}×{entry.ImageHeight}";
        var body = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38 };
        if (entry.IsImage) body.Foreground = hint;

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        foreach (var tag in entry.Tags.Take(3))
            meta.Children.Add(new TextBlock { Text = "#" + tag, Foreground = accent, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        if (entry.IsRich)
            meta.Children.Add(new TextBlock { Text = "富文本", Foreground = hint, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        if (entry.Pinned)
            meta.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, Foreground = accent, Margin = new Thickness(0, 2, 6, 0) });
        meta.Children.Add(new TextBlock { Text = ClipboardWindow.FormatTime(entry.Time), Foreground = hint, FontSize = 11 });

        var grid = new DockPanel();
        SetDock(meta, Dock.Right);
        grid.Children.Add(meta);
        grid.Children.Add(body);

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("复制", () => Copy()));
        if (entry.Text != null) menu.Items.Add(MenuItem("复制为纯文本", () => Copy(plainText: true)));
        menu.Items.Add(MenuItem("合并复制选中项", CopyMerged));
        menu.Items.Add(MenuItem(entry.Pinned ? "取消置顶" : "置顶", () => { _history.TogglePin(entry); Refresh(); }));
        if (entry.Text != null) menu.Items.Add(MenuItem("编辑", () => { if (Selected == entry && _editing == null) ToggleEdit(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("删除", DeleteSelected));
        return new ListBoxItem { Content = grid, Tag = entry, ContextMenu = menu };
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void ShowSelected()
    {
        if (_editing != null) EndEdit(save: false);
        var entry = Selected;
        _pin.Content = entry?.Pinned == true ? "取消置顶" : "置顶";
        _text.Visibility = entry?.IsImage == true ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = entry?.Text ?? "";
        _image.Source = entry?.IsImage == true ? _history.LoadImage(entry) : null;
        _tags.Text = entry != null ? string.Join(", ", entry.Tags) : "";
        _tags.IsEnabled = entry != null;
        _edit.IsEnabled = entry?.Text != null;
    }

    void ToggleEdit()
    {
        if (_editing != null)
        {
            EndEdit(save: true);
            if (_dirty) Refresh();
            return;
        }
        if (Selected is not { Text: not null } entry) return;
        _editing = entry;
        _text.IsReadOnly = false;
        _edit.Content = "保存";
        _cancelEdit.Visibility = Visibility.Visible;
        _text.Focus();
        Ui.SetStatus(_status, "正在编辑，保存后富文本格式和文件列表会被替换为纯文本");
    }

    void EndEdit(bool save)
    {
        var entry = _editing;
        _editing = null;
        _text.IsReadOnly = true;
        _edit.Content = "编辑";
        _cancelEdit.Visibility = Visibility.Collapsed;
        if (entry == null) return;
        if (save && _text.Text.Length > 0 && _text.Text != entry.Text)
        {
            _history.UpdateText(entry, _text.Text);
            Ui.SetStatus(_status, "已保存修改");
        }
        else _text.Text = entry.Text ?? "";
    }

    void SaveTags()
    {
        if (Selected is not { } entry) return;
        _history.SetTags(entry, _tags.Text.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        Ui.SetStatus(_status, entry.Tags.Count > 0 ? "标签已保存" : "已删除标签");
    }

    void DeleteSelected()
    {
        foreach (var entry in SelectedEntries) _history.Remove(entry);
    }

    void Copy() => Copy(false);

    void Copy(bool plainText)
    {
        if (Selected is not { } entry) return;
        if (_history.Copy(entry, plainText)) Ui.SetStatus(_status, plainText ? "已复制为纯文本" : "已复制到剪贴板");
        else if (plainText) Ui.SetStatus(_status, "图片没有文字可复制", true);
        else
        {
            _history.Remove(entry);
            Ui.SetStatus(_status, "这条记录的图片文件已丢失，已删除", true);
        }
    }

    void CopyMerged()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;
        if (_history.CopyMerged(entries)) Ui.SetStatus(_status, $"已合并复制 {entries.Count(e => e.Text != null)} 条文字");
        else Ui.SetStatus(_status, "选中的记录里没有文字", true);
    }

    void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "clipboard-" + DateTime.Now.ToString("yyyyMMdd"),
            Filter = "JSON 文件|*.json|文本文件|*.txt",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            int count = _history.Export(dialog.FileName);
            Ui.SetStatus(_status, $"已导出 {count} 条文字记录到 {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.SetStatus(_status, "导出失败：" + ex.Message, true);
        }
    }
}
