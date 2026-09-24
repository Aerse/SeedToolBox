using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Views;

namespace SeedToolBox.Clips;

/// <summary>
/// Searchable clipboard history popup shown next to the cursor.
/// Choosing an entry copies it and, if enabled, pastes it into the window that was active before.
/// </summary>
sealed class ClipboardWindow : Window
{
    readonly ClipboardHistory _history;
    readonly TextBox _search = new() { Style = DialogWindow.TextBoxStyle };
    readonly ListBox _list = new();
    readonly TextBlock _empty = new() { Foreground = DialogWindow.HintBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly CheckBox _enabled = new() { Content = "记录", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _autoPaste = new() { Content = "选中后自动粘贴", VerticalAlignment = VerticalAlignment.Center };
    IntPtr _previous;
    bool _dirty = true;

    public ClipboardWindow(ClipboardHistory history)
    {
        _history = history;
        Title = "剪贴板历史";
        Width = 400;
        Height = 520;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Foreground = (Brush)FindResource("TextBrush");

        var hint = new TextBlock { Text = "搜索剪贴板历史…", Foreground = DialogWindow.HintBrush, IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
        _search.PreviewKeyDown += OnSearchKey;

        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.SelectionMode = SelectionMode.Extended;
        _list.ItemContainerStyle = (Style)FindResource("ListCard");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        _list.MouseLeftButtonUp += (_, e) =>
        {
            // Ctrl/Shift+click only builds a multi-selection for merging
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: ClipboardEntry entry }) Choose(entry);
        };
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Delete or Key.Escape || NumberOf(e.Key) > 0) OnSearchKey(_list, e);
        };

        _enabled.IsChecked = history.Settings.Enabled;
        _enabled.Click += (_, _) => history.Recording = _enabled.IsChecked == true;
        history.RecordingChanged += () => _enabled.IsChecked = history.Recording;
        history.EntryChanged += () =>
        {
            if (IsVisible) Refresh();
            else _dirty = true;
        };
        _autoPaste.IsChecked = history.Settings.AutoPaste;
        _autoPaste.Click += (_, _) => { history.Settings.AutoPaste = _autoPaste.IsChecked == true; history.SaveSettings(); };
        var clear = new Button { Content = "清空", MinWidth = 60, ToolTip = "删除所有未置顶的记录" };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "删除所有未置顶的剪贴板记录？", "剪贴板历史", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            history.Clear();
        };

        var footer = new DockPanel { Margin = new Thickness(12, 8, 12, 10) };
        DockPanel.SetDock(clear, Dock.Right);
        footer.Children.Add(clear);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _enabled, _autoPaste } });

        var tips = new TextBlock
        {
            Text = "Enter 粘贴 · Shift+Enter 纯文本 · 1-9 快速粘贴 · Ctrl+单击多选合并 · Delete 删除 · Ctrl+P 置顶",
            TextWrapping = TextWrapping.Wrap,
            Foreground = DialogWindow.HintBrush,
            FontSize = 11,
            Margin = new Thickness(14, 0, 12, 0),
        };

        var root = new DockPanel();
        var top = new Grid { Margin = new Thickness(12, 12, 12, 8), Children = { _search, hint } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(tips, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(tips);
        root.Children.Add(new Grid { Margin = new Thickness(6, 0, 6, 4), Children = { _list, _empty } });

        Content = new Border
        {
            Margin = new Thickness(12),
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("PopupShadow"),
            Child = root,
        };

        history.Entries.CollectionChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
            else _dirty = true;
        };
        Deactivated += (_, _) => Hide();
    }

    /// <summary>Shows the popup at the cursor, remembering the window to paste into.</summary>
    public void ShowAtCursor()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        _previous = GetForegroundWindow();
        _search.Clear();
        if (_dirty) Refresh();
        _enabled.IsChecked = _history.Settings.Enabled;

        // Place in device pixels, clamped to the work area of the monitor under the cursor
        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        Left = -10000;
        Top = -10000;
        Show();
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double w = ActualWidth * scale, h = ActualHeight * scale;
        double x = Math.Max(area.Left, Math.Min(cursor.X - 12 * scale, area.Right - w));
        double y = cursor.Y + h <= area.Bottom ? cursor.Y - 12 * scale : Math.Max(area.Top, cursor.Y - h);
        Left = x / scale;
        Top = y / scale;

        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(handle);
        _search.Focus();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    void Refresh()
    {
        _dirty = false;
        var query = _search.Text.Trim();
        var entries = _history.Entries
            .Where(e => Matches(e, query))
            .OrderByDescending(e => e.Pinned)
            .ToList();

        var selected = (_list.SelectedItem as ListBoxItem)?.Tag;
        _list.Items.Clear();
        for (int i = 0; i < entries.Count; i++) _list.Items.Add(CreateItem(entries[i], i + 1));
        _empty.Text = _history.Entries.Count == 0 ? "还没有记录，复制点东西试试" : "没有匹配的记录";
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == selected);
        _list.SelectedItem = keep ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
    }

    /// <summary>Search over the text and tags; "图片", "文件" and "#tag" pick kinds and tags.</summary>
    internal static bool Matches(ClipboardEntry e, string query)
    {
        if (query.Length == 0) return true;
        if (query.StartsWith("#") && query.Length > 1)
            return e.Tags.Any(t => t.IndexOf(query.Substring(1), StringComparison.OrdinalIgnoreCase) >= 0);
        if (e.Tags.Any(t => t.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
        if (e.IsImage) return "图片".Contains(query);
        if (e.IsFiles && "文件".Contains(query)) return true;
        return e.Text!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>One-line summary shown in the lists.</summary>
    internal static string Preview(ClipboardEntry entry, int lines, int max)
    {
        var text = entry.Text ?? "";
        if (entry.IsFiles)
        {
            var names = entry.Files!.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f);
            text = $"[{entry.Files!.Count} 个文件] " + string.Join("、", names.Take(8));
        }
        var preview = string.Join(" ⏎ ", text.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(lines));
        return preview.Length > max ? preview.Substring(0, max) : preview;
    }

    ListBoxItem CreateItem(ClipboardEntry entry, int number)
    {
        var hint = DialogWindow.HintBrush;
        FrameworkElement body;
        if (entry.Text != null)
        {
            var preview = Preview(entry, 4, 300);
            body = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38 };
        }
        else
        {
            body = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Image { Source = _history.LoadImage(entry, 240), MaxHeight = 72, MaxWidth = 200, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left },
                    new TextBlock { Text = $"{entry.ImageWidth}×{entry.ImageHeight}", Foreground = hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) },
                },
            };
        }

        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        foreach (var tag in entry.Tags.Take(2))
            meta.Children.Add(new TextBlock { Text = "#" + tag, Foreground = (Brush)FindResource("AccentBrush"), FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        if (entry.IsRich)
            meta.Children.Add(new TextBlock { Text = "富文本", Foreground = hint, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        if (entry.Pinned)
            meta.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 2, 6, 0) });
        meta.Children.Add(new TextBlock { Text = FormatTime(entry.Time), Foreground = hint, FontSize = 11 });

        var grid = new DockPanel();
        DockPanel.SetDock(meta, Dock.Right);
        grid.Children.Add(meta);
        if (number <= 9)
        {
            var badge = new TextBlock { Text = number.ToString(), Foreground = hint, FontSize = 11, Width = 14, Margin = new Thickness(0, 1, 4, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(badge, Dock.Left);
            grid.Children.Add(badge);
        }
        grid.Children.Add(body);

        var item = new ListBoxItem { Content = grid, Tag = entry, ToolTip = entry.Text is { Length: > 0 } t ? (t.Length > 2000 ? t.Substring(0, 2000) + "…" : t) : null };
        ToolTipService.SetInitialShowDelay(item, 800);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("粘贴", () => Choose(entry)));
        if (entry.Text != null && (entry.IsRich || entry.IsFiles)) menu.Items.Add(MenuItem("粘贴为纯文本", () => Choose(entry, plainText: true)));
        menu.Items.Add(MenuItem("合并粘贴选中项", ChooseMerged));
        menu.Items.Add(MenuItem("仅复制", () => { _history.Copy(entry); Hide(); }));
        menu.Items.Add(MenuItem(entry.Pinned ? "取消置顶" : "置顶", () => { _history.TogglePin(entry); Refresh(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("删除", () => _history.Remove(entry)));
        item.ContextMenu = menu;
        return item;
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    internal static string FormatTime(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (time.Date == DateTime.Today) return time.ToString("HH:mm");
        if (time.Date == DateTime.Today.AddDays(-1)) return "昨天 " + time.ToString("HH:mm");
        return time.Year == DateTime.Now.Year ? time.ToString("M月d日") : time.ToString("yyyy-M-d");
    }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        var entry = (_list.SelectedItem as ListBoxItem)?.Tag as ClipboardEntry;
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when sender == _search && _list.Items.Count > 0:
                int index = Math.Max(0, Math.Min(_list.Items.Count - 1, _list.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
                _list.SelectedIndex = index;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter when _list.SelectedItems.Count > 1:
                ChooseMerged();
                e.Handled = true;
                break;
            case Key.Enter when entry != null:
                Choose(entry, plainText: Keyboard.Modifiers == ModifierKeys.Shift);
                e.Handled = true;
                break;
            case var key when NumberOf(key) is int n and > 0 && n <= _list.Items.Count
                && (Keyboard.Modifiers == ModifierKeys.Control || (Keyboard.Modifiers == ModifierKeys.None && (sender != _search || _search.Text.Length == 0))):
                // Plain digits while nothing is typed (or from the list), Ctrl+digit always
                Choose((ClipboardEntry)((ListBoxItem)_list.Items[n - 1]).Tag);
                e.Handled = true;
                break;
            case Key.Delete when entry != null && (sender != _search || _search.Text.Length == 0):
                int at = _list.SelectedIndex;
                _history.Remove(entry);
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(at, _list.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.P when entry != null && Keyboard.Modifiers == ModifierKeys.Control:
                _history.TogglePin(entry);
                Refresh();
                e.Handled = true;
                break;
        }
    }

    static int NumberOf(Key key) =>
        key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0;

    /// <summary>Pastes the selected entries, in list order, joined by new lines.</summary>
    void ChooseMerged()
    {
        var entries = _list.Items.Cast<ListBoxItem>().Where(i => i.IsSelected).Select(i => (ClipboardEntry)i.Tag).ToList();
        if (entries.Count == 1) Choose(entries[0]);
        else if (entries.Count > 1 && _history.CopyMerged(entries)) PasteIntoPrevious();
    }

    void Choose(ClipboardEntry entry, bool plainText = false)
    {
        if (!_history.Copy(entry, plainText))
        {
            if (!plainText) _history.Remove(entry);
            return;
        }
        PasteIntoPrevious();
    }

    void PasteIntoPrevious()
    {
        Hide();
        if (!_history.Settings.AutoPaste || _previous == IntPtr.Zero) return;

        var target = _previous;
        SetForegroundWindow(target);
        // Give the target a moment to take focus before sending Ctrl+V
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetForegroundWindow() == target) SendCtrlV();
        };
        timer.Start();
    }

    static void SendCtrlV()
    {
        const byte VK_CONTROL = 0x11, VK_V = 0x56;
        const uint KEYUP = 0x0002;
        // Release modifiers still held from the hotkey, which would turn Ctrl+V into Ctrl+Alt+V
        foreach (byte vk in new byte[] { 0x10, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
