using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace SeedToolBox.Notes;

/// <summary>Opens, closes and keeps track of the sticky note windows.</summary>
sealed class NoteWindows
{
    readonly NoteStore _store;
    readonly Dictionary<string, NoteWindow> _open = new();

    public NoteWindows(NoteStore store)
    {
        _store = store;
        store.Changed += CloseRemoved;
    }

    /// <summary>Opens the notes that were on the desktop when the app last ran.</summary>
    public void RestoreOpen()
    {
        foreach (var note in _store.Notes.Where(n => n.Open).ToList()) Show(note, activate: false);
    }

    /// <summary>Creates a note near the cursor and focuses it.</summary>
    public void New()
    {
        var note = _store.Add();
        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        // Device pixels are close enough here; the window is clamped once it knows its DPI
        note.Left = Math.Max(area.Left, Math.Min(cursor.X - 40, area.Right - note.Width));
        note.Top = Math.Max(area.Top, Math.Min(cursor.Y - 20, area.Bottom - note.Height));
        Show(note);
    }

    public void Show(Note note, bool activate = true)
    {
        if (!_open.TryGetValue(note.Id, out var window))
        {
            window = new NoteWindow(note, _store, this);
            window.Closed += (_, _) => _open.Remove(note.Id);
            _open[note.Id] = window;
        }
        note.Open = true;
        _store.RequestSave();
        window.ShowActivated = activate;
        window.Show();
        if (activate) window.FocusText();
    }

    /// <summary>Takes the note off the desktop; it stays in the list.</summary>
    public void Hide(Note note)
    {
        if (_open.TryGetValue(note.Id, out var window)) window.Close();
        note.Open = false;
        _store.RequestSave();
    }

    public void Delete(Note note, Window? owner)
    {
        if (note.Text.Trim().Length > 0 &&
            MessageBox.Show(owner!, $"删除笔记“{Short(note.Title)}”？", "快速笔记", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_open.TryGetValue(note.Id, out var window)) window.Close();
        _store.Remove(note);
    }

    /// <summary>Hides every note, or shows them all again when none is showing.</summary>
    public void ToggleAll()
    {
        if (_open.Count > 0)
        {
            foreach (var window in _open.Values.ToList()) window.Close();
            return;
        }
        var notes = _store.Notes.Where(n => n.Open).ToList();
        if (notes.Count == 0) { New(); return; }
        foreach (var note in notes) Show(note);
    }

    /// <summary>Closes windows without saving their "open" state, for exit.</summary>
    public void CloseAllForExit()
    {
        foreach (var window in _open.Values.ToList()) window.CloseForExit();
    }

    void CloseRemoved()
    {
        foreach (var pair in _open.ToList())
        {
            var note = _store.Notes.FirstOrDefault(n => n.Id == pair.Key);
            if (note == null) pair.Value.Close();
            else pair.Value.Rebind(note);
        }
    }

    static string Short(string text) => text.Length > 20 ? text.Substring(0, 20) + "…" : text;
}

/// <summary>A sticky note on the desktop.</summary>
sealed class NoteWindow : Window
{
    readonly NoteStore _store;
    readonly NoteWindows _windows;
    readonly TextBox _text;
    readonly ToggleButton _pin;
    Note _note;
    bool _exiting, _loading;

    public NoteWindow(Note note, NoteStore store, NoteWindows windows)
    {
        _note = note;
        _store = store;
        _windows = windows;
        Title = "快速笔记";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        MinWidth = 180;
        MinHeight = 120;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        _text = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Foreground,
            Padding = new Thickness(10, 4, 10, 10),
            FontSize = 14,
        };
        _text.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _note.Text = _text.Text;
            _note.Updated = DateTime.Now;
            _store.RequestSave();
        };

        _pin = new ToggleButton { Content = Glyph("\uE718"), ToolTip = "置顶", Style = BarStyle(typeof(ToggleButton)) };
        _pin.Click += (_, _) => { _note.Topmost = Topmost = _pin.IsChecked == true; _store.RequestSave(); };

        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)), Height = 30 };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(BarButton("\uE790", "颜色", ShowColors));
        right.Children.Add(_pin);
        right.Children.Add(BarButton("\uE74D", "删除", () => _windows.Delete(_note, this)));
        right.Children.Add(BarButton("\uE711", "收起（Ctrl+W，在工具箱「快速笔记」里还能找到）", () => _windows.Hide(_note)));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { BarButton("\uE710", "新建笔记（Ctrl+N）", _windows.New) } });
        bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) DragMove(); };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_text);
        Content = root;

        Rebind(note);
        if (!double.IsNaN(note.Left)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = note.Left; Top = note.Top; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = note.Width;
        Height = note.Height;

        LocationChanged += (_, _) => SavePlacement();
        SizeChanged += (_, _) => SavePlacement();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _windows.New(); }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _windows.Hide(_note); }
        };
        Loaded += (_, _) => KeepOnScreen();
    }

    public void FocusText()
    {
        Activate();
        _text.Focus();
        _text.CaretIndex = _text.Text.Length;
    }

    /// <summary>Picks up changes made elsewhere (the notes page, sync).</summary>
    public void Rebind(Note note)
    {
        _note = note;
        if (_text.Text != note.Text)
        {
            _loading = true;
            _text.Text = note.Text;
            _loading = false;
        }
        Background = BrushOf(note.Color);
        Topmost = note.Topmost;
        _pin.IsChecked = note.Topmost;
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    public static SolidColorBrush BrushOf(int color)
    {
        var argb = NoteStore.Colors[Math.Max(0, Math.Min(NoteStore.Colors.Length - 1, color))].Argb;
        return new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    void SavePlacement()
    {
        if (_exiting || !IsLoaded || WindowState != WindowState.Normal) return;
        _note.Left = Left;
        _note.Top = Top;
        _note.Width = ActualWidth;
        _note.Height = ActualHeight;
        _store.RequestSave();
    }

    void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        if (Left + 60 > virtualLeft + SystemParameters.VirtualScreenWidth || Left + ActualWidth < virtualLeft + 60 ||
            Top < virtualTop || Top + 30 > virtualTop + SystemParameters.VirtualScreenHeight)
        {
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
        }
    }

    void ShowColors()
    {
        var menu = new ContextMenu();
        for (int i = 0; i < NoteStore.Colors.Length; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = NoteStore.Colors[i].Name,
                Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = BrushOf(i), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                IsChecked = _note.Color == i,
            };
            item.Click += (_, _) => { _note.Color = index; Background = BrushOf(index); _store.RequestSave(); };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    static TextBlock Glyph(string glyph) => new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 };

    static Button BarButton(string glyph, string tip, Action action)
    {
        var button = new Button { Content = Glyph(glyph), ToolTip = tip, Style = BarStyle(typeof(Button)) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Flat, square buttons that tint on hover, matching the note's colour.</summary>
    static Style BarStyle(Type type)
    {
        var style = new Style(type);
        style.Setters.Add(new Setter(WidthProperty, 30.0));
        style.Setters.Add(new Setter(HeightProperty, 30.0));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(FocusableProperty, false));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(type) { VisualTree = border }));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0))));
        style.Triggers.Add(hover);
        if (type == typeof(ToggleButton))
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))));
            style.Triggers.Add(on);
        }
        return style;
    }
}
