using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
enum CaptureMode { Screenshot, Record, Text, QrCode, Table, Ask }

sealed class CaptureWindow : OverlayWindow
{
    enum DragMode { None, Select, Move, Resize, Draw }

    [Flags]
    enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    readonly ScreenToolService _service;
    readonly WindowFinder _finder;
    readonly CaptureMode _mode;
    bool RecordMode => _mode == CaptureMode.Record;

    // Surface (pixel) layer
    readonly RectangleGeometry _selectionGeometry = new();
    readonly RectangleGeometry _clipGeometry = new();
    readonly AnnotationLayer _layer;
    readonly Rectangle _frame = new() { Stroke = ToolbarUi.Accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    // Ui (DIP) layer
    readonly Magnifier _magnifier;
    readonly Border _sizeLabel;
    readonly Rectangle[] _handles = new Rectangle[8];
    readonly StackPanel _toolbar = new() { Visibility = Visibility.Collapsed };
    readonly List<Action> _toggleUpdates = new();

    bool _editing;
    Rect _selection = Rect.Empty;
    DragMode _drag;
    Point _dragStart;
    Rect _dragOrigin;
    Edge _resizeEdges;
    bool _moved;

    static readonly string[] Ratios = { "", "1:1", "4:3", "3:2", "16:9", "9:16" };

    readonly Border _sizePanel;
    readonly TextBox _sizeBox = new() { Width = 96, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
    readonly List<(string Ratio, Border Button)> _ratioButtons = new();

    /// <param name="initial">Region (in screenshot pixels) to start editing right away, skipping the selection.</param>
    public CaptureWindow(ScreenShot shot, WindowFinder finder, ScreenToolService service, CaptureMode mode = CaptureMode.Screenshot, Rect? initial = null) : base(shot)
    {
        _service = service;
        _finder = finder;
        _mode = mode;

        _layer = new AnnotationLayer(shot, service, () => Scale);
        _layer.Content.Clip = _clipGeometry;
        _layer.ToolChanged += OnToolChanged;
        var mask = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, shot.Width, shot.Height)), _selectionGeometry),
            IsHitTestVisible = false,
        };
        Surface.Children.Add(_layer.Content);
        Surface.Children.Add(mask);
        Surface.Children.Add(_layer.Chrome);
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
            _handles[i] = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, Stroke = ToolbarUi.Accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Ui.Children.Add(_handles[i]);
        }
        _sizePanel = BuildSizePanel();
        BuildToolbar();
        _toolbar.Children.Add(_sizePanel);
        Ui.Children.Add(_toolbar);
        _magnifier = new Magnifier(shot);
        Ui.Children.Add(_magnifier);

        Loaded += (_, _) =>
        {
            if (initial is { } region && Fit(region) is { } fitted) BeginEditing(fitted);
            else Hover(Mouse.GetPosition(Surface));
        };
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
        _layer.AddToolButtons(main);
        main.Children.Add(Divider());
        main.Children.Add(Button("⬚", "尺寸与比例（直接输入数字也可）", ToggleSizePanel));
        main.Children.Add(Button("文", "识别文字", RecognizeText));
        main.Children.Add(Button("码", "识别二维码", DecodeQrCodes));
        main.Children.Add(Button("⇕", "长截图（手动滚动区域内容）", ScrollCapture));
        main.Children.Add(Divider());
        main.Children.Add(Button("📌", "贴到屏幕", Pin));
        main.Children.Add(Button("💾", "保存 (Ctrl+S)", Save));
        main.Children.Add(Button("✕", "退出 (Esc)", Close));
        main.Children.Add(Button("✓", "复制到剪贴板 (Enter / 双击)", CopyAndClose, ToolbarUi.Accent));

        _toolbar.Children.Add(Card(main));
        var style = Card(_layer.StyleBar);
        style.Margin = new Thickness(0, 4, 0, 0);
        style.HorizontalAlignment = HorizontalAlignment.Left;
        style.Visibility = Visibility.Collapsed;
        _toolbar.Children.Add(style);
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
        main.Children.Add(Button("⬚", "尺寸与比例（直接输入数字也可）", ToggleSizePanel));
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
            ToolbarUi.SetSelected(button!, get());
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
        _service.RememberRegion(region);
        Close();
        _service.StartRecording(region, scale);
    }

    static Border Card(UIElement child) => ToolbarUi.Card(child);
    static Rectangle Divider() => ToolbarUi.Divider();
    static Border Button(string glyph, string tip, Action onClick, Brush? foreground = null) => ToolbarUi.Button(glyph, tip, onClick, foreground);

    AnnotationTool CurrentTool => RecordMode ? AnnotationTool.None : _layer.Tool;

    void SetTool(AnnotationTool tool)
    {
        if (RecordMode) OnToolChanged();
        else _layer.SetTool(tool);
    }

    void OnToolChanged()
    {
        if (_toolbar.Children.Count > 1)
            _toolbar.Children[1].Visibility = !RecordMode && _layer.ShowsStyle ? Visibility.Visible : Visibility.Collapsed;
        UpdateHandles();
        PlaceToolbar();
    }

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

    #region Size and ratio

    double Ratio => ParseRatio(_service.Settings.SelectionRatio);

    static double ParseRatio(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h) && w > 0 && h > 0 ? w / h : 0;
    }

    static int? Digit(Key key) =>
        key is >= Key.D0 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad0 and <= Key.NumPad9 ? key - Key.NumPad0 : null;

    /// <summary>The drag end moved so the rectangle from the start keeps the locked ratio.</summary>
    Point Constrain(Point start, Point p)
    {
        double dx = p.X - start.X, dy = p.Y - start.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (w / Math.Max(1, h) > Ratio) w = h * Ratio;
        else h = w / Ratio;
        return new Point(start.X + Math.Sign(dx) * Math.Round(w), start.Y + Math.Sign(dy) * Math.Round(h));
    }

    /// <summary>The region clipped to the screenshot, or null if nothing is left.</summary>
    Rect? Fit(Rect region)
    {
        region.Intersect(new Rect(0, 0, Shot.Width, Shot.Height));
        return region.IsEmpty || region.Width < 2 || region.Height < 2 ? null : region;
    }

    Border BuildSizePanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "宽×高", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        _sizeBox.ToolTip = "如 800x600，回车应用";
        row.Children.Add(_sizeBox);
        row.Children.Add(ToolbarUi.TextButton("应用", "应用尺寸 (Enter)", ApplySize));
        row.Children.Add(Divider());
        foreach (var ratio in Ratios)
        {
            var value = ratio;
            var button = ToolbarUi.TextButton(ratio.Length == 0 ? "自由" : ratio, ratio.Length == 0 ? "不限比例" : $"锁定 {ratio}，拖动和调整都保持比例", () => SetRatio(value));
            _ratioButtons.Add((ratio, button));
            row.Children.Add(button);
        }
        var card = Card(row);
        card.Margin = new Thickness(0, 4, 0, 0);
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.Visibility = Visibility.Collapsed;
        UpdateRatioButtons();
        return card;
    }

    void UpdateRatioButtons()
    {
        foreach (var (ratio, button) in _ratioButtons) ToolbarUi.SetSelected(button, ratio == _service.Settings.SelectionRatio);
    }

    void ToggleSizePanel()
    {
        if (_sizePanel.Visibility == Visibility.Visible)
        {
            _sizePanel.Visibility = Visibility.Collapsed;
            Focus();
            PlaceToolbar();
        }
        else ShowSizePanel();
    }

    void ShowSizePanel()
    {
        _sizePanel.Visibility = Visibility.Visible;
        _sizeBox.Text = $"{(int)_selection.Width}x{(int)_selection.Height}";
        PlaceToolbar();
        _sizeBox.Focus();
        _sizeBox.SelectAll();
    }

    void ApplySize()
    {
        var m = System.Text.RegularExpressions.Regex.Match(_sizeBox.Text, @"(\d+)\s*[x×X*,，\s]\s*(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int w) || !int.TryParse(m.Groups[2].Value, out int h) || w < 2 || h < 2)
        {
            _sizeBox.SelectAll();
            return;
        }
        w = Math.Min(w, (int)Shot.Width);
        h = Math.Min(h, (int)Shot.Height);
        // Keep the top left corner unless the new size would run off the screen
        double x = Math.Max(0, Math.Min(_selection.X, Shot.Width - w)), y = Math.Max(0, Math.Min(_selection.Y, Shot.Height - h));
        ShowSelection(new Rect(x, y, w, h));
        _sizePanel.Visibility = Visibility.Collapsed;
        Focus();
        UpdateHandles();
        PlaceToolbar();
    }

    void SetRatio(string ratio)
    {
        _service.Settings.SelectionRatio = ratio;
        _service.SaveSettings();
        UpdateRatioButtons();
        if (Ratio > 0 && !_selection.IsEmpty)
        {
            // Fit the current selection to the ratio, keeping its width where possible
            double w = _selection.Width, h = Math.Round(w / Ratio);
            if (_selection.Y + h > Shot.Height) { h = Shot.Height - _selection.Y; w = Math.Round(h * Ratio); }
            ShowSelection(new Rect(_selection.X, _selection.Y, Math.Max(2, w), Math.Max(2, h)));
        }
        else if (!_selection.IsEmpty) ShowSelection(_selection);
        UpdateHandles();
        PlaceToolbar();
    }

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
        _magnifier.Update(x, y, $"{x + Shot.X}, {y + Shot.Y}\n{ColorText.Format(color, _service.Settings.ColorFormat)}\nC 复制颜色 · 右键/Esc 退出\nF 全屏 · W 窗口 · R 上次区域 · O 识字 · Q 扫码");
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
        var ratio = _service.Settings.SelectionRatio;
        ((TextBlock)_sizeLabel.Child).Text = $"{(int)r.Width} × {(int)r.Height}" + (Ratio > 0 ? $"  ·  {ratio}" : "");
        _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeLabel, ui.Left);
        Canvas.SetTop(_sizeLabel, ui.Top - 24 >= screen.Top ? ui.Top - 24 : ui.Top + 4);
        UpdateHandles();
    }

    void UpdateHandles()
    {
        bool show = _editing && CurrentTool == AnnotationTool.None && !_selection.IsEmpty;
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
        SetTool(AnnotationTool.None);
        // Recognition modes act on the first selection; deferred so the mouse-up finishes first
        if (_mode == CaptureMode.Text) Dispatcher.BeginInvoke(new Action(RecognizeText));
        else if (_mode == CaptureMode.QrCode) Dispatcher.BeginInvoke(new Action(DecodeQrCodes));
        else if (_mode == CaptureMode.Table) Dispatcher.BeginInvoke(new Action(RecognizeTable));
        else if (_mode == CaptureMode.Ask) Dispatcher.BeginInvoke(new Action(AskAi));
    }

    void CancelEditing()
    {
        _layer.Clear();
        _editing = false;
        SetTool(AnnotationTool.None);
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
                if (_moved) ShowSelection(Span(_dragStart, Ratio > 0 ? Clamp(Constrain(_dragStart, p)) : p));
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
                _layer.MouseMove(p);
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
        if (CurrentTool == AnnotationTool.None)
        {
            var edges = EdgesAt(raw);
            Cursor = edges != Edge.None ? CursorFor(edges) : _selection.Contains(raw) ? Cursors.SizeAll : Cursors.Arrow;
        }
        else
        {
            Cursor = _layer.CursorAt(p);
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
        if (Ratio > 0)
        {
            // Horizontal edges drive the size; the opposite corner stays put
            bool byWidth = _resizeEdges.HasFlag(Edge.Left) || _resizeEdges.HasFlag(Edge.Right);
            double w = byWidth ? r.Width : Math.Round(r.Height * Ratio), h = byWidth ? Math.Round(r.Width / Ratio) : r.Height;
            double x = _resizeEdges.HasFlag(Edge.Left) ? r.Right - w : r.Left;
            double y = _resizeEdges.HasFlag(Edge.Top) ? r.Bottom - h : r.Top;
            r = new Rect(x, y, w, h);
        }
        r.Intersect(new Rect(0, 0, Shot.Width, Shot.Height));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        ShowSelection(r);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Toolbar buttons and the text being typed handle their own clicks
        if (_toolbar.IsMouseOver || _layer.EditingText is { IsMouseOver: true }) return;

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

        if (CurrentTool == AnnotationTool.None)
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

        if (!_selection.Contains(raw))
        {
            _layer.CommitText();
            return;
        }
        if (!_layer.MouseDown(p)) return;
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
                _layer.MouseUp();
                break;
        }
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_layer.CancelText()) return;
        if (_editing)
        {
            CancelEditing();
        }
        else
        {
            Close();
        }
    }

    #endregion

    #region Output

    System.Drawing.Rectangle ScreenRegion =>
        new((int)_selection.X + Shot.X, (int)_selection.Y + Shot.Y, (int)_selection.Width, (int)_selection.Height);

    bool _captured;

    /// <summary>History, auto-save and the last region; once per overlay.</summary>
    void Captured(BitmapSource image)
    {
        if (_captured) return;
        _captured = true;
        _service.OnCaptured(image, ScreenRegion);
    }

    BitmapSource Render() =>
        _layer.Render(Shot.Image, new Int32Rect((int)_selection.X, (int)_selection.Y, (int)_selection.Width, (int)_selection.Height));

    void CopyAndClose()
    {
        if (!_editing) return;
        if (RecordMode)
        {
            StartRecording();
            return;
        }
        var image = Render();
        Captured(image);
        Close();
        ScreenToolService.CopyImage(image);
    }

    void Save()
    {
        var image = Render();
        if (!_service.SaveImage(image, this)) return;
        Captured(image);
        Close();
    }

    void RecognizeText()
    {
        if (!_editing) return;
        var image = Render();
        _service.RememberRegion(ScreenRegion);
        Close();
        _service.RecognizeText(image);
    }

    void ScrollCapture()
    {
        if (!_editing) return;
        var region = ScreenRegion;
        double scale = Scale;
        _service.RememberRegion(region);
        Close();
        _service.StartScrollCapture(region, scale);
    }

    void RecognizeTable()
    {
        if (!_editing) return;
        var image = Render();
        _service.RememberRegion(ScreenRegion);
        Close();
        _service.RecognizeTable(image);
    }

    void AskAi()
    {
        if (!_editing) return;
        var image = Render();
        _service.RememberRegion(ScreenRegion);
        Close();
        _service.AskAi(image);
    }

    void DecodeQrCodes()
    {
        if (!_editing) return;
        var image = Render();
        _service.RememberRegion(ScreenRegion);
        Close();
        _service.DecodeQrCodes(image);
    }

    void Pin()
    {
        var image = Render();
        Captured(image);
        _service.Pin(image, (int)_selection.X + Shot.X, (int)_selection.Y + Shot.Y);
        Close();
    }

    #endregion

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_layer.EditingText is { IsKeyboardFocused: true })
        {
            _layer.HandleTextKey(e);
            return;
        }
        if (_sizeBox.IsKeyboardFocused)
        {
            if (e.Key == Key.Enter)
            {
                ApplySize();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _sizePanel.Visibility = Visibility.Collapsed;
                Focus();
                PlaceToolbar();
                e.Handled = true;
            }
            return;
        }
        if (_editing && !RecordMode && _layer.HandleKey(e))
        {
            e.Handled = true;
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
            if (e.Key == Key.R && _service.HasLastRegion)
            {
                var last = _service.Settings.LastRegion!;
                if (Fit(new Rect(last[0] - Shot.X, last[1] - Shot.Y, last[2], last[3])) is { } region) BeginEditing(region);
            }
            else if (e.Key == Key.F && !ctrl)
            {
                BeginEditing(new Rect(0, 0, Shot.Width, Shot.Height));
            }
            else if (e.Key is Key.W or Key.O or Key.Q && !ctrl)
            {
                // Acts on the window under the cursor, as if it had been clicked
                if (_selection.IsEmpty || (RecordMode && e.Key != Key.W)) return;
                BeginEditing(_selection);
                // Text and QR modes already run their action from BeginEditing
                if (_mode != CaptureMode.Screenshot) return;
                if (e.Key == Key.O) RecognizeText();
                else if (e.Key == Key.Q) DecodeQrCodes();
            }
            else if (e.Key == Key.C)
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
        else if (!ctrl && !RecordMode && e.Key == Key.O && _drag == DragMode.None) RecognizeText();
        else if (!ctrl && !RecordMode && e.Key == Key.Q && _drag == DragMode.None) DecodeQrCodes();
        else if (!ctrl && Digit(e.Key) is { } digit && _drag == DragMode.None && CurrentTool == AnnotationTool.None)
        {
            // Typing a number starts entering an exact size
            ShowSizePanel();
            _sizeBox.Text = digit.ToString();
            _sizeBox.CaretIndex = 1;
        }
        else if (ctrl && e.Key == Key.S && !RecordMode) Save();
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
