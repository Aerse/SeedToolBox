using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Notes;

/// <summary>Toolbox page listing every quick note, with an editor for the selected one.</summary>
sealed class NotesPage : DockPanel
{
    readonly NoteStore _store;
    readonly NoteWindows _windows;
    readonly TextBox _search = Ui.Field(200);
    readonly ListBox _list = new();
    readonly TextBox _text = Ui.Area(wrap: true);
    readonly TextBlock _info = Ui.Status();
    readonly Button _desktop;
    bool _loading, _dirty = true;

    public NotesPage(NoteStore store, NoteWindows windows)
    {
        _store = store;
        _windows = windows;
        var header = Ui.Header("快速笔记", $"在任意位置按 {store.Data.Hotkey} 新建便签，内容自动保存；便签可以贴在桌面上、置顶、换颜色");
        _search.TextChanged += (_, _) => Refresh();
        _desktop = Ui.Button("贴到桌面", () =>
        {
            if (Selected is not { } n) return;
            if (n.Open) windows.Hide(n); else windows.Show(n);
            UpdateButtons();
        });
        var toolbar = Ui.Row(Ui.Label("搜索"), _search,
            Ui.Button("新建", () =>
            {
                var note = store.Add();
                Refresh();
                Select(note);
                _text.Focus();
            }, accent: true),
            _desktop,
            Ui.Button("复制", () => { if (Selected is { } n && n.Text.Length > 0) ScreenTools.ScreenToolService.CopyText(n.Text); }),
            Ui.Button("删除", () => { if (Selected is { } n) windows.Delete(n, Window.GetWindow(this)); }),
            Ui.Button("全部显示 / 隐藏", windows.ToggleAll));

        _list.ItemContainerStyle = (Style)Application.Current.FindResource("ListCard");
        _list.BorderThickness = new Thickness(1);
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _list.Padding = new Thickness(4);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => ShowSelected();

        _text.TextChanged += (_, _) =>
        {
            if (_loading || Selected is not { } n) return;
            n.Text = _text.Text;
            n.Updated = DateTime.Now;
            store.RequestSave();
        };

        var editor = new DockPanel();
        DockPanel.SetDock(_info, Dock.Bottom);
        editor.Children.Add(_info);
        editor.Children.Add(_text);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(editor, 2);
        body.Children.Add(_list);
        body.Children.Add(editor);

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(body);

        // Edits in a sticky note show up here; the list itself only needs rebuilding while visible
        store.Changed += () =>
        {
            if (!IsVisible) { _dirty = true; return; }
            if (_text.IsKeyboardFocusWithin) { UpdateItemTitles(); return; }
            Refresh();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Refresh(); };
    }

    Note? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Note;

    void Refresh()
    {
        _dirty = false;
        var selected = Selected?.Id;
        var query = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var note in _store.Notes.OrderByDescending(n => n.Updated))
        {
            if (query.Length > 0 && note.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _list.Items.Add(Item(note));
        }
        var again = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((Note)i.Tag).Id == selected) ?? _list.Items.Cast<ListBoxItem>().FirstOrDefault();
        _list.SelectedItem = again;
        ShowSelected();
    }

    void Select(Note note) => _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == note);

    static ListBoxItem Item(Note note)
    {
        var title = new TextBlock { Text = note.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var time = new TextBlock { Text = note.Updated.ToString("yyyy-MM-dd HH:mm") + (note.Open ? "  · 在桌面上" : ""), Foreground = DialogWindow.HintBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        var swatch = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = NoteWindow.BrushOf(note.Color), Margin = new Thickness(0, 0, 8, 0) };
        var panel = new DockPanel { Children = { swatch, new StackPanel { Children = { title, time } } }, Tag = title };
        return new ListBoxItem { Content = panel, Tag = note };
    }

    /// <summary>Updates titles in place so typing in the editor doesn't reorder the list under the caret.</summary>
    void UpdateItemTitles()
    {
        foreach (ListBoxItem item in _list.Items)
            if (item.Content is DockPanel { Tag: TextBlock title }) title.Text = ((Note)item.Tag).Title;
        UpdateButtons();
    }

    void ShowSelected()
    {
        _loading = true;
        var note = Selected;
        _text.Text = note?.Text ?? "";
        _text.IsEnabled = note != null;
        _info.Text = note == null ? (_store.Notes.Count == 0 ? "还没有笔记，点“新建”或按快捷键试试" : "") : $"创建于 {note.Created:yyyy-MM-dd HH:mm}，{note.Text.Length} 字";
        _loading = false;
        UpdateButtons();
    }

    void UpdateButtons() => _desktop.Content = Selected is { Open: true } ? "从桌面收起" : "贴到桌面";
}
