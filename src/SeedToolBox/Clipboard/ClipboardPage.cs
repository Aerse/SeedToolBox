using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Clips;

/// <summary>The full clipboard history in the main window: list on the left, the whole entry on the right.</summary>
sealed class ClipboardPage : DockPanel
{
    readonly ClipboardHistory _history;
    readonly TextBox _search = Ui.Field(260);
    readonly ListBox _list = new();
    readonly TextBlock _empty = new() { Foreground = DialogWindow.HintBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBox _text = Ui.Area(wrap: true);
    readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _status = Ui.Status();
    readonly Button _pin;
    bool _dirty = true;

    public ClipboardPage(ClipboardHistory history)
    {
        _history = history;
        var header = Ui.Header("剪贴板历史", $"自动记录复制过的文字和图片；在任意位置按 {history.Settings.Hotkey} 可快速呼出并粘贴");
        _search.TextChanged += (_, _) => Refresh();
        _search.ToolTip = "搜索文字，输入“图片”只看图片";
        _pin = Ui.Button("置顶", () => { if (Selected is { } e) { history.TogglePin(e); Refresh(); } });
        var toolbar = Ui.Row(Ui.Label("搜索"), _search, Ui.Label("", 12),
            Ui.Button("复制", Copy, accent: true), _pin,
            Ui.Button("删除", () => { if (Selected is { } e) history.Remove(e); }),
            Ui.Label("", 12),
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
            if (e.Key == Key.Delete && Selected is { } entry) history.Remove(entry);
            else if (e.Key == Key.Enter) Copy();
            else return;
            e.Handled = true;
        };

        _text.IsReadOnly = true;
        var detail = new Grid { Children = { _text, new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } };

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

        history.Entries.CollectionChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
            else _dirty = true;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Refresh(); };
    }

    ClipboardEntry? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ClipboardEntry;

    void Refresh()
    {
        _dirty = false;
        var query = _search.Text.Trim();
        var entries = _history.Entries
            .Where(e => query.Length == 0 || (e.Text != null ? e.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 : "图片".Contains(query)))
            .OrderByDescending(e => e.Pinned)
            .ToList();
        var selected = Selected;
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(CreateItem(entry));
        _empty.Text = _history.Entries.Count == 0 ? "还没有记录，复制点东西试试" : "没有匹配的记录";
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == selected);
        _list.SelectedItem = keep ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
        ShowSelected();
        Ui.SetStatus(_status, $"共 {_history.Entries.Count} 条，置顶 {_history.Entries.Count(e => e.Pinned)} 条  双击或 Enter 复制，Delete 删除");
    }

    ListBoxItem CreateItem(ClipboardEntry entry)
    {
        var hint = DialogWindow.HintBrush;
        var preview = entry.Text != null
            ? string.Join(" ⏎ ", entry.Text.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3))
            : $"[图片] {entry.ImageWidth}×{entry.ImageHeight}";
        if (preview.Length > 200) preview = preview.Substring(0, 200);
        var body = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38 };
        if (entry.IsImage) body.Foreground = hint;

        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        if (entry.Pinned)
            meta.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["AccentBrush"], Margin = new Thickness(0, 2, 6, 0) });
        meta.Children.Add(new TextBlock { Text = ClipboardWindow.FormatTime(entry.Time), Foreground = hint, FontSize = 11 });

        var grid = new DockPanel();
        SetDock(meta, Dock.Right);
        grid.Children.Add(meta);
        grid.Children.Add(body);
        return new ListBoxItem { Content = grid, Tag = entry };
    }

    void ShowSelected()
    {
        var entry = Selected;
        _pin.Content = entry?.Pinned == true ? "取消置顶" : "置顶";
        _text.Visibility = entry?.IsImage == true ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = entry?.Text ?? "";
        _image.Source = entry?.IsImage == true ? _history.LoadImage(entry) : null;
    }

    void Copy()
    {
        if (Selected is not { } entry) return;
        if (_history.Copy(entry)) Ui.SetStatus(_status, "已复制到剪贴板");
        else
        {
            _history.Remove(entry);
            Ui.SetStatus(_status, "这条记录的图片文件已丢失，已删除", true);
        }
    }
}
