using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Region screenshot: hover picks a window/control, dragging selects a rectangle,
/// then the selection can be adjusted, annotated, copied, saved or pinned.
/// In record mode the selection is only adjusted, then handed to the screen recorder;
/// in text and QR code modes it goes straight to recognition.
/// </summary>
enum CaptureMode { Screenshot, Record, Text, QrCode }

sealed class CaptureWindow : OverlayWindow
{
    enum Tool { None, Rectangle, Ellipse, Arrow, Pen, Text, Mosaic }

    enum DragMode { None, Select, Move, Resize, Draw }

    [Flags]
    enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    static readonly Color[] Palette =
    {
        Color.FromRgb(255, 59, 48), Color.FromRgb(255, 204, 0), Color.FromRgb(52, 199, 89),
        Color.FromRgb(0, 122, 255), Colors.Black, Colors.White,
    };
    // Per size choice, in DIPs
    static readonly double[] StrokeSizes = { 2, 4, 7 };
    static readonly double[] MosaicSizes = { 14, 24, 40 };
    static readonly double[] FontSizes = { 16, 22, 30 };
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(30, 144, 255));
    static readonly Brush ButtonHover = new SolidColorBrush(Color.FromRgb(229, 229, 229));
    static readonly Brush ButtonSelected = new SolidColorBrush(Color.FromRgb(204, 228, 247));

    readonly ScreenToolService _service;
    readonly WindowFinder _finder;
    readonly CaptureMode _mode;
    bool RecordMode => _mode == CaptureMode.Record;

    // Surface (pixel) layer
    readonly RectangleGeometry _selectionGeometry = new();
    readonly RectangleGeometry _clipGeometry = new();
    readonly Canvas _annotations;
    readonly Rectangle _frame = new() { Stroke = Accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    // Ui (DIP) layer
    readonly Magnifier _magnifier;
    readonly Border _sizeLabel;
    readonly Rectangle[] _handles = new Rectangle[8];
    readonly StackPanel _toolbar = new() { Visibility = Visibility.Collapsed };
    readonly StackPanel _styleBar = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<Tool, Border> _toolButtons = new();
    readonly List<Border> _sizeButtons = new();
    readonly List<Border> _colorButtons = new();
    readonly List<Action> _toggleUpdates = new();

    bool _editing;
    Rect _selection = Rect.Empty;
    Tool _tool;
    DragMode _drag;
    Point _dragStart;
    Rect _dragOrigin;
    Edge _resizeEdges;
    bool _moved;
    Shape? _drawing;
    TextBox? _text;

    public CaptureWindow(ScreenShot shot, WindowFinder finder, ScreenToolService service, CaptureMode mode = CaptureMode.Screenshot) : base(shot)
    {
        _service = service;
        _finder = finder;
        _mode = mode;

        _annotations = new Canvas { Width = shot.Width, Height = shot.Height, Clip = _clipGeometry };
        var mask = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, shot.Width, shot.Height)), _selectionGeometry),
            IsHitTestVisible = false,
        };
        Surface.Children.Add(_annotations);
        Surface.Children.Add(mask);
        Surface.Children.Add(_frame);

        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)),
            Padding = new Thickness(6, 2, 6, 3),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 },
        };
        Ui.Children.Add(_sizeLabel);
        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, Stroke = Accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Ui.Children.Add(_handles[i]);
        }
        BuildToolbar();
        Ui.Children.Add(_toolbar);
        _magnifier = new Magnifier(shot);
        Ui.Children.Add(_magnifier);

        Loaded += (_, _) => Hover(Mouse.GetPosition(Surface));
        // Preview (tunneling) events, so no child element can swallow a selection drag
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += OnRightClick;
        PreviewKeyDown += OnKey;
    }

    protected override void OnScaleChanged()
    {
        _frame.StrokeThickness = Scale;
        if (!_selection.IsEmpty) ShowSelection(_selection);
    }

    #region Toolbar

    void BuildToolbar()
    {
        if (RecordMode)
        {
            BuildRecordToolbar();
            return;
        }
        var main = new StackPanel { Orientation = Orientation.Horizontal };
        AddTool(main, Tool.Rectangle, "□", "矩形");
        AddTool(main, Tool.Ellipse, "○", "椭圆");
        AddTool(main, Tool.Arrow, "↗", "箭头");
        AddTool(main, Tool.Pen, "✎", "画笔");
        AddTool(main, Tool.Text, "A", "文字");
        AddTool(main, Tool.Mosaic, "▦", "马赛克");
        main.Children.Add(Divider());
        main.Children.Add(Button("↶", "撤销 (Ctrl+Z)", Undo));
        main.Children.Add(Divider());
        main.Children.Add(Button("文", "识别文字", RecognizeText));
        main.Children.Add(Button("码", "识别二维码", DecodeQrCodes));
        main.Children.Add(Divider());
        main.Children.Add(Button("📌", "贴到屏幕", Pin));
        main.Children.Add(Button("💾", "保存 (Ctrl+S)", Save));
        main.Children.Add(Button("✕", "退出 (Esc)", Close));
        main.Children.Add(Button("✓", "复制到剪贴板 (Enter / 双击)", CopyAndClose, Accent));

        for (int i = 0; i < StrokeSizes.Length; i++)
        {
            int index = i;
            double dot = 4 + i * 3;
            var button = Button("", $"{new[] { "细", "中", "粗" }[i]}", () => SetPenSize(index));
            button.Child = new Ellipse { Width = dot, Height = dot, Fill = Brushes.DimGray };
            _sizeButtons.Add(button);
            _styleBar.Children.Add(button);
        }
        _styleBar.Children.Add(Divider());
        for (int i = 0; i < Palette.Length; i++)
        {
            int index = i;
            var button = Button("", "", () => SetPenColor(index));
            button.Width = 26;
            button.Child = new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(Palette[i]), Stroke = Brushes.Gray, StrokeThickness = 1 };
            _colorButtons.Add(button);
            _styleBar.Children.Add(button);
        }

        _toolbar.Children.Add(Card(main));
        var style = Card(_styleBar);
        style.Margin = new Thickness(0, 4, 0, 0);
        style.HorizontalAlignment = HorizontalAlignment.Left;
        style.Visibility = Visibility.Collapsed;
        _toolbar.Children.Add(style);
        UpdateStyleButtons();
    }

    void BuildRecordToolbar()
    {
        var settings = _service.Settings.Record;
        var main = new StackPanel { Orientation = Orientation.Horizontal };
        main.Children.Add(Toggle("🔊", "录制系统声音", () => settings.SystemAudio, v => settings.SystemAudio = v));
        main.Children.Add(Toggle("🎤", "录制麦克风", () => settings.Microphone, v => settings.Microphone = v));
        main.Children.Add(Divider());
        main.Children.Add(Button("⚙", "录屏设置", () =>
        {
            _service.RecordSettings(this);
            foreach (var update in _toggleUpdates) update();
        }));
        main.Children.Add(Button("✕", "退出 (Esc)", Close));
        var start = Button("●", "开始录制 (Enter / 双击)", StartRecording, new SolidColorBrush(Color.FromRgb(230, 40, 40)));
        start.Width = 36;
        main.Children.Add(start);
        _toolbar.Children.Add(Card(main));
        // PlaceToolbar expects a style row
        _toolbar.Children.Add(new Border { Visibility = Visibility.Collapsed });
    }

    Border Toggle(string glyph, string tip, Func<bool> get, Action<bool> set)
    {
        Border? button = null;
        void Update()
        {
            button!.Background = get() ? ButtonSelected : Brushes.Transparent;
            button.ToolTip = $"{tip}：{(get() ? "开" : "关")}";
            button.Child.Opacity = get() ? 1 : 0.4;
        }
        button = Button(glyph, tip, () =>
        {
            set(!get());
            _service.SaveSettings();
            Update();
        });
        Update();
        _toggleUpdates.Add(Update);
        return button;
    }

    void StartRecording()
    {
        if (!_editing) return;
        // The encoder needs even dimensions
        int width = Math.Max(2, (int)_selection.Width & ~1), height = Math.Max(2, (int)_selection.Height & ~1);
        var region = new System.Drawing.Rectangle((int)_selection.X + Shot.X, (int)_selection.Y + Shot.Y, width, height);
        double scale = Scale;
        Close();
        _service.StartRecording(region, scale);
    }

    static Border Card(UIElement child) => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(3),
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3 },
        Child = child,
    };

    static Rectangle Divider() => new() { Width = 1, Height = 18, Fill = new SolidColorBrush(Color.FromRgb(221, 221, 221)), Margin = new Thickness(4, 0, 4, 0) };

    void AddTool(Panel panel, Tool tool, string glyph, string tip)
    {
        var button = Button(glyph, tip, () => SetTool(_tool == tool ? Tool.None : tool));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    static readonly FontFamily ToolbarFont = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");

    /// <summary>Flat button made from a Border, so it never takes keyboard focus from the overlay.</summary>
    Border Button(string glyph, string tip, Action onClick, Brush? foreground = null)
    {
        var button = new Border
        {
            Width = 32,
            Height = 30,
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow,
            ToolTip = tip.Length > 0 ? tip : null,
            Child = new TextBlock
            {
                Text = glyph,
                // Segoe UI Symbol ships with Win7 (with updates) through Win11, so the glyphs render everywhere
                FontFamily = ToolbarFont,
                FontSize = 16,
                Foreground = foreground ?? new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.MouseEnter += (_, _) => { if (button.Background != ButtonSelected) button.Background = ButtonHover; };
        button.MouseLeave += (_, _) => { if (button.Background != ButtonSelected) button.Background = Brushes.Transparent; };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    void SetTool(Tool tool)
    {
        CommitText();
        _tool = tool;
        if (RecordMode)
        {
            UpdateHandles();
            PlaceToolbar();
            return;
        }
        foreach (var pair in _toolButtons)
            pair.Value.Background = pair.Key == tool ? ButtonSelected : Brushes.Transparent;
        _toolbar.Children[1].Visibility = tool == Tool.None ? Visibility.Collapsed : Visibility.Visible;
        UpdateHandles();
        PlaceToolbar();
    }

    void SetPenSize(int index)
    {
        _service.Settings.PenSize = index;
        _service.SaveSettings();
        UpdateStyleButtons();
    }

    void SetPenColor(int index)
    {
        _service.Settings.PenColor = index;
        _service.SaveSettings();
        UpdateStyleButtons();
    }

    void UpdateStyleButtons()
    {
        for (int i = 0; i < _sizeButtons.Count; i++)
            _sizeButtons[i].Background = i == SizeIndex ? ButtonSelected : Brushes.Transparent;
        for (int i = 0; i < _colorButtons.Count; i++)
            _colorButtons[i].Background = i == ColorIndex ? ButtonSelected : Brushes.Transparent;
    }

    int SizeIndex => Math.Max(0, Math.Min(StrokeSizes.Length - 1, _service.Settings.PenSize));
    int ColorIndex => Math.Max(0, Math.Min(Palette.Length - 1, _service.Settings.PenColor));
    Brush PenBrush => new SolidColorBrush(Palette[ColorIndex]);

    void PlaceToolbar()
    {
        if (!_editing || _drag is DragMode.Move or DragMode.Resize)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            return;
        }
        _toolbar.Visibility = Visibility.Visible;
        // Measure with the style row shown so picking a tool doesn't flip the toolbar to the other side
        var style = _toolbar.Children[1];
        var styleVisibility = style.Visibility;
        style.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        style.Visibility = styleVisibility;
        var sel = UiRect(_selection);
        var screen = MonitorOf(_selection);
        const double gap = 6;

        double x = Math.Max(screen.Left, Math.Min(screen.Right - size.Width, sel.Right - size.Width));
        double y = sel.Bottom + gap;
        if (y + size.Height > screen.Bottom) y = sel.Top - gap - size.Height;
        if (y < screen.Top) y = sel.Bottom - gap - size.Height; // no room outside: go inside
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    /// <summary>Bounds of the monitor containing the selection, in Ui coordinates.</summary>
    Rect MonitorOf(Rect pixelRect)
    {
        var center = new System.Drawing.Point((int)(pixelRect.X + pixelRect.Width / 2) + Shot.X, (int)(pixelRect.Y + pixelRect.Height / 2) + Shot.Y);
        var b = WinForms.Screen.FromPoint(center).Bounds;
        return UiRect(new Rect(b.X - Shot.X, b.Y - Shot.Y, b.Width, b.Height));
    }

    Rect UiRect(Rect pixel) => new(pixel.X / Scale, pixel.Y / Scale, pixel.Width / Scale, pixel.Height / Scale);

    #endregion

    #region Selection

    /// <summary>Selecting phase: preview the window/control under the cursor.</summary>
    void Hover(Point pixel)
    {
        var p = Clamp(pixel);
        if (_finder.Hit((int)p.X, (int)p.Y) is { } hit)
        {
            var r = new Rect(hit.X, hit.Y, hit.Width, hit.Height);
            r.Intersect(new Rect(0, 0, Shot.Width, Shot.Height));
            ShowSelection(r);
        }
        else
        {
            ShowSelection(Rect.Empty);
        }
        UpdateMagnifier(p);
    }

    void UpdateMagnifier(Point p)
    {
        if (_editing)
        {
            _magnifier.Visibility = Visibility.Collapsed;
            return;
        }
        int x = (int)p.X, y = (int)p.Y;
        var color = Shot.GetColor(x, y);
        _magnifier.Visibility = Visibility.Visible;
        _magnifier.Update(x, y, $"{x + Shot.X}, {y + Shot.Y}\n{ColorText.Format(color, _service.Settings.ColorFormat)}\nC 复制颜色 · 右键/Esc 退出");
        PlaceNear(_magnifier, ToUi(p), 24);
    }

    void ShowSelection(Rect r)
    {
        _selection = r;
        _selectionGeometry.Rect = r.IsEmpty ? new Rect() : r;
        _clipGeometry.Rect = r.IsEmpty ? new Rect() : r;

        if (r.IsEmpty || r.Width < 1 || r.Height < 1)
        {
            _frame.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            UpdateHandles();
            return;
        }

        // The frame sits just outside the selection so it is never part of the capture
        double t = _frame.StrokeThickness;
        Canvas.SetLeft(_frame, r.X - t);
        Canvas.SetTop(_frame, r.Y - t);
        _frame.Width = r.Width + 2 * t;
        _frame.Height = r.Height + 2 * t;
        _frame.Visibility = Visibility.Visible;

        var ui = UiRect(r);
        var screen = MonitorOf(r);
        ((TextBlock)_sizeLabel.Child).Text = $"{(int)r.Width} × {(int)r.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeLabel, ui.Left);
        Canvas.SetTop(_sizeLabel, ui.Top - 24 >= screen.Top ? ui.Top - 24 : ui.Top + 4);
        UpdateHandles();
    }

    void UpdateHandles()
    {
        bool show = _editing && _tool == Tool.None && !_selection.IsEmpty;
        var r = UiRect(_selection.IsEmpty ? new Rect() : _selection);
        double[] xs = { r.Left, r.Left + r.Width / 2, r.Right };
        double[] ys = { r.Top, r.Top + r.Height / 2, r.Bottom };
        int i = 0;
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                if (row == 1 && col == 1) continue;
                var handle = _handles[i++];
                handle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(handle, xs[col] - handle.Width / 2);
                Canvas.SetTop(handle, ys[row] - handle.Height / 2);
            }
        }
    }

    /// <summary>Which selection edges are within grabbing distance of the point.</summary>
    Edge EdgesAt(Point p)
    {
        double tol = 6 * Scale;
        var r = _selection;
        if (p.X < r.Left - tol || p.X > r.Right + tol || p.Y < r.Top - tol || p.Y > r.Bottom + tol) return Edge.None;
        var edges = Edge.None;
        if (Math.Abs(p.X - r.Left) <= tol) edges |= Edge.Left;
        else if (Math.Abs(p.X - r.Right) <= tol) edges |= Edge.Right;
        if (Math.Abs(p.Y - r.Top) <= tol) edges |= Edge.Top;
        else if (Math.Abs(p.Y - r.Bottom) <= tol) edges |= Edge.Bottom;
        return edges;
    }

    static Cursor CursorFor(Edge edges) => edges switch
    {
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        _ => Cursors.SizeNS,
    };

    Point Clamp(Point p) => new(Math.Max(0, Math.Min(Shot.Width - 1, Math.Floor(p.X))), Math.Max(0, Math.Min(Shot.Height - 1, Math.Floor(p.Y))));

    /// <summary>Rectangle covering both pixels, inclusive.</summary>
    static Rect Span(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    void BeginEditing(Rect selection)
    {
        _editing = true;
        ShowSelection(selection);
        _magnifier.Visibility = Visibility.Collapsed;
        SetTool(Tool.None);
        // Recognition modes act on the first selection; deferred so the mouse-up finishes first
        if (_mode == CaptureMode.Text) Dispatcher.BeginInvoke(new Action(RecognizeText));
        else if (_mode == CaptureMode.QrCode) Dispatcher.BeginInvoke(new Action(DecodeQrCodes));
    }

    void CancelEditing()
    {
        CommitText();
        _annotations.Children.Clear();
        _editing = false;
        SetTool(Tool.None);
        _toolbar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Cross;
        Hover(Mouse.GetPosition(Surface));
    }

    #endregion

    #region Mouse

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var raw = PixelOf(e);
        var p = Clamp(raw);
        switch (_drag)
        {
            case DragMode.Select:
                if (!_moved && (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3)) _moved = true;
                if (_moved) ShowSelection(Span(_dragStart, p));
                UpdateMagnifier(p);
                return;
            case DragMode.Move:
            {
                double x = Math.Max(0, Math.Min(Shot.Width - _dragOrigin.Width, _dragOrigin.X + Math.Round(raw.X - _dragStart.X)));
                double y = Math.Max(0, Math.Min(Shot.Height - _dragOrigin.Height, _dragOrigin.Y + Math.Round(raw.Y - _dragStart.Y)));
                ShowSelection(new Rect(x, y, _dragOrigin.Width, _dragOrigin.Height));
                return;
            }
            case DragMode.Resize:
                Resize(raw);
                return;
            case DragMode.Draw:
                UpdateDrawing(p);
                return;
        }

        if (!_editing)
        {
            Hover(raw);
            return;
        }
        if (_toolbar.IsMouseOver)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        if (_tool == Tool.None)
        {
            var edges = EdgesAt(raw);
            Cursor = edges != Edge.None ? CursorFor(edges) : _selection.Contains(raw) ? Cursors.SizeAll : Cursors.Arrow;
        }
        else
        {
            Cursor = _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    void Resize(Point raw)
    {
        double dx = Math.Round(raw.X - _dragStart.X), dy = Math.Round(raw.Y - _dragStart.Y);
        double left = _dragOrigin.Left, top = _dragOrigin.Top, right = _dragOrigin.Right, bottom = _dragOrigin.Bottom;
        if (_resizeEdges.HasFlag(Edge.Left)) left += dx;
        if (_resizeEdges.HasFlag(Edge.Right)) right += dx;
        if (_resizeEdges.HasFlag(Edge.Top)) top += dy;
        if (_resizeEdges.HasFlag(Edge.Bottom)) bottom += dy;

        // Rect normalizes, so dragging an edge past the opposite one flips the selection
        var r = new Rect(new Point(left, top), new Point(right, bottom));
        r.Intersect(new Rect(0, 0, Shot.Width, Shot.Height));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        ShowSelection(r);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Toolbar buttons and the text being typed handle their own clicks
        if (_toolbar.IsMouseOver || _text is { IsMouseOver: true }) return;

        var raw = PixelOf(e);
        var p = Clamp(raw);
        _dragStart = p;
        _moved = false;

        if (!_editing)
        {
            _drag = DragMode.Select;
            CaptureMouse();
            return;
        }

        if (_text != null)
        {
            CommitText();
            if (_tool != Tool.Text) return;
        }

        if (_tool == Tool.None)
        {
            if (e.ClickCount == 2 && _selection.Contains(raw))
            {
                CopyAndClose();
                return;
            }
            _dragOrigin = _selection;
            _dragStart = raw;
            _resizeEdges = EdgesAt(raw);
            if (_resizeEdges != Edge.None) _drag = DragMode.Resize;
            else if (_selection.Contains(raw)) _drag = DragMode.Move;
            else return;
            UpdateHandles();
            _toolbar.Visibility = Visibility.Collapsed;
            CaptureMouse();
            return;
        }

        if (!_selection.Contains(raw)) return;
        if (_tool == Tool.Text)
        {
            BeginText(p);
            return;
        }
        BeginDrawing(p);
        _drag = DragMode.Draw;
        CaptureMouse();
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var drag = _drag;
        _drag = DragMode.None;
        // Releasing capture raises a MouseMove; while still selecting that would re-run the hover
        // detection and replace the dragged rectangle, so release only after the phase has changed
        Dispatcher.BeginInvoke(new Action(ReleaseMouseCapture));

        switch (drag)
        {
            case DragMode.Select:
                if (_moved)
                {
                    if (_selection.Width >= 2 && _selection.Height >= 2) BeginEditing(_selection);
                    else Hover(PixelOf(e));
                }
                else if (!_selection.IsEmpty)
                {
                    // A click takes the highlighted window/control
                    BeginEditing(_selection);
                }
                break;
            case DragMode.Move:
            case DragMode.Resize:
                UpdateHandles();
                PlaceToolbar();
                break;
            case DragMode.Draw:
                EndDrawing();
                break;
        }
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_text != null)
        {
            _annotations.Children.Remove(_text);
            _text = null;
            Focus();
        }
        else if (_editing)
        {
            CancelEditing();
        }
        else
        {
            Close();
        }
    }

    #endregion

    #region Annotations

    void BeginDrawing(Point p)
    {
        double stroke = StrokeSizes[SizeIndex] * Scale;
        switch (_tool)
        {
            case Tool.Rectangle:
                _drawing = new Path { Stroke = PenBrush, StrokeThickness = stroke, Data = new RectangleGeometry(new Rect(p, p)) };
                break;
            case Tool.Ellipse:
                _drawing = new Path { Stroke = PenBrush, StrokeThickness = stroke, Data = new EllipseGeometry(new Rect(p, p)) };
                break;
            case Tool.Arrow:
                _drawing = new Path { Stroke = PenBrush, Fill = PenBrush, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round };
                break;
            case Tool.Pen:
                _drawing = Stroke(PenBrush, stroke, p);
                break;
            case Tool.Mosaic:
                // Painting with a pixelated copy of the screen reveals blocks wherever the brush goes
                var brush = new ImageBrush(Shot.Mosaic(Math.Max(6, (int)(10 * Scale))))
                {
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, Shot.Width, Shot.Height),
                    Stretch = Stretch.Fill,
                };
                brush.Freeze();
                _drawing = Stroke(brush, MosaicSizes[SizeIndex] * Scale, p);
                break;
        }
        if (_drawing != null) _annotations.Children.Add(_drawing);
    }

    static Polyline Stroke(Brush brush, double thickness, Point start) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        // Two points so a single click leaves a dot
        Points = new PointCollection { start, start },
    };

    void UpdateDrawing(Point p)
    {
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case Path { Data: RectangleGeometry rect }:
                rect.Rect = new Rect(_dragStart, p);
                break;
            case Path { Data: EllipseGeometry ellipse }:
                ellipse.RadiusX = Math.Abs(p.X - _dragStart.X) / 2;
                ellipse.RadiusY = Math.Abs(p.Y - _dragStart.Y) / 2;
                ellipse.Center = new Point((p.X + _dragStart.X) / 2, (p.Y + _dragStart.Y) / 2);
                break;
            case Path arrow:
                arrow.Data = Arrow(_dragStart, p, arrow.StrokeThickness);
                break;
        }
    }

    void EndDrawing()
    {
        if (_drawing is Path path && !(path.Data is { } data && data.Bounds.Width + data.Bounds.Height >= 4))
            _annotations.Children.Remove(path);
        _drawing = null;
    }

    Geometry Arrow(Point from, Point to, double stroke)
    {
        var v = to - from;
        double length = v.Length;
        if (length < 1) return Geometry.Empty;
        v /= length;
        var normal = new Vector(-v.Y, v.X);
        double head = Math.Min(length, Math.Max(12 * Scale, stroke * 4));
        var basePoint = to - v * head;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(basePoint, true, true);
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(basePoint + normal * head * 0.45, true, true);
            ctx.LineTo(basePoint - normal * head * 0.45, true, true);
        }
        g.Freeze();
        return g;
    }

    void BeginText(Point p)
    {
        var color = PenBrush;
        _text = new TextBox
        {
            Foreground = color,
            CaretBrush = color,
            Background = Brushes.Transparent,
            BorderBrush = Accent,
            BorderThickness = new Thickness(Math.Max(1, Math.Round(Scale))),
            Padding = new Thickness(0),
            FontSize = FontSizes[SizeIndex] * Scale,
            AcceptsReturn = true,
            MinWidth = 24 * Scale,
        };
        Canvas.SetLeft(_text, p.X);
        Canvas.SetTop(_text, p.Y - _text.FontSize * 0.7);
        _annotations.Children.Add(_text);
        _text.Loaded += (_, _) => _text?.Focus();
    }

    /// <summary>Freezes the text being typed into a plain label.</summary>
    void CommitText()
    {
        if (_text == null) return;
        var text = _text;
        _text = null;
        if (text.Text.Trim().Length == 0)
        {
            _annotations.Children.Remove(text);
        }
        else
        {
            // Keep the TextBox so layout doesn't shift, just make it inert
            text.Select(0, 0);
            text.IsReadOnly = true;
            text.Focusable = false;
            text.IsHitTestVisible = false;
            text.BorderBrush = Brushes.Transparent;
        }
        Focus();
    }

    void Undo()
    {
        if (_text != null)
        {
            _annotations.Children.Remove(_text);
            _text = null;
            Focus();
            return;
        }
        int count = _annotations.Children.Count;
        if (count > 0) _annotations.Children.RemoveAt(count - 1);
    }

    #endregion

    #region Output

    BitmapSource Render()
    {
        CommitText();
        var r = new Int32Rect((int)_selection.X, (int)_selection.Y, (int)_selection.Width, (int)_selection.Height);
        var target = new Rect(0, 0, r.Width, r.Height);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(new CroppedBitmap(Shot.Image, r), target);
            if (_annotations.Children.Count > 0)
            {
                var brush = new VisualBrush(_annotations)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = _selection,
                    Stretch = Stretch.Fill,
                };
                dc.DrawRectangle(brush, null, target);
            }
        }
        // 96 DPI: one drawing unit is one pixel
        var bitmap = new RenderTargetBitmap(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    void CopyAndClose()
    {
        if (!_editing) return;
        if (RecordMode)
        {
            StartRecording();
            return;
        }
        var image = Render();
        Close();
        ScreenToolService.CopyImage(image);
    }

    void Save()
    {
        if (_service.SaveImage(Render(), this)) Close();
    }

    void RecognizeText()
    {
        if (!_editing) return;
        var image = Render();
        Close();
        _service.RecognizeText(image);
    }

    void DecodeQrCodes()
    {
        if (!_editing) return;
        var image = Render();
        Close();
        _service.DecodeQrCodes(image);
    }

    void Pin()
    {
        var image = Render();
        _service.Pin(image, (int)_selection.X + Shot.X, (int)_selection.Y + Shot.Y);
        Close();
    }

    #endregion

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_text != null && _text.IsKeyboardFocused)
        {
            // Enter finishes the text, Shift+Enter adds a line
            if (e.Key == Key.Escape || (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                CommitText();
                e.Handled = true;
            }
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (!_editing)
        {
            if (e.Key == Key.C)
            {
                var p = Clamp(Mouse.GetPosition(Surface));
                var text = ColorText.Format(Shot.GetColor((int)p.X, (int)p.Y), _service.Settings.ColorFormat);
                Close();
                ScreenToolService.CopyText(text);
            }
            else
            {
                e.Handled = NudgeCursor(e.Key);
            }
        }
        else if (e.Key == Key.Enter || (ctrl && e.Key == Key.C)) CopyAndClose();
        else if (ctrl && e.Key == Key.S && !RecordMode) Save();
        else if (ctrl && e.Key == Key.Z && !RecordMode) Undo();
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _drag == DragMode.None)
        {
            // Arrow keys nudge the selection by one pixel
            double dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
            double dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            var r = _selection;
            r.X = Math.Max(0, Math.Min(Shot.Width - r.Width, r.X + dx));
            r.Y = Math.Max(0, Math.Min(Shot.Height - r.Height, r.Y + dy));
            ShowSelection(r);
            PlaceToolbar();
        }
        else e.Handled = false;
    }
}
