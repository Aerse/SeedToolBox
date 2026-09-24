using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

enum AnnotationTool { None, Select, Rectangle, Ellipse, Line, Arrow, Pen, Highlighter, Text, Step, Mosaic, Blur }

/// <summary>
/// Annotations over an image, in image pixels. Every finished shape stays a separate element on
/// <see cref="Content"/>, so it can be selected, moved, deleted, and undone or redone.
/// The host forwards mouse input (already in image pixels) and renders <see cref="Content"/> over the image.
/// </summary>
sealed class AnnotationLayer
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(255, 59, 48), Color.FromRgb(255, 204, 0), Color.FromRgb(52, 199, 89),
        Color.FromRgb(0, 122, 255), Colors.Black, Colors.White,
    };
    // Per size choice, in DIPs
    static readonly double[] StrokeSizes = { 2, 4, 7 };
    static readonly double[] MosaicSizes = { 14, 24, 40 };
    static readonly double[] FontSizes = { 16, 22, 30 };
    static readonly double[] StepSizes = { 22, 28, 36 };
    const string StepTag = "step", EffectTag = "effect";

    readonly IAnnotationSource _source;
    readonly ScreenToolService _service;
    readonly Func<double> _scale;
    readonly List<(Action Undo, Action Redo)> _undo = new(), _redo = new();
    readonly Dictionary<AnnotationTool, Border> _toolButtons = new();
    readonly List<Border> _sizeButtons = new();
    readonly List<Border> _colorButtons = new();
    readonly Rectangle _customSwatch = new() { Width = 16, Height = 16, Stroke = Brushes.Gray, StrokeThickness = 1 };
    readonly Border _fillButton;
    readonly Rectangle _selectionBox = new()
    {
        Stroke = ToolbarUi.Accent,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    Shape? _drawing;
    TextBox? _text;
    Point _start;
    UIElement? _selected;
    bool _moving;
    Vector _moveOrigin;

    /// <summary>The annotations; this is what gets rendered into the output.</summary>
    public Canvas Content { get; }
    /// <summary>Selection outline; place it over <see cref="Content"/>, it is never rendered.</summary>
    public Canvas Chrome { get; }
    /// <summary>Size, colour and fill choices; the host shows it while a drawing tool is picked.</summary>
    public StackPanel StyleBar { get; } = new() { Orientation = Orientation.Horizontal };

    public AnnotationTool Tool { get; private set; }
    public event Action? ToolChanged;

    public TextBox? EditingText => _text;
    public bool IsEmpty => Content.Children.Count == 0 && _text == null;
    public bool ShowsStyle => Tool is not (AnnotationTool.None or AnnotationTool.Select);

    double Scale => _scale();

    /// <param name="scale">Image pixels per DIP, so pen sizes look the same on every monitor.</param>
    public AnnotationLayer(IAnnotationSource source, ScreenToolService service, Func<double> scale)
    {
        _source = source;
        _service = service;
        _scale = scale;
        Content = new Canvas { Width = source.Width, Height = source.Height };
        Chrome = new Canvas { Width = source.Width, Height = source.Height, IsHitTestVisible = false };
        Chrome.Children.Add(_selectionBox);
        _fillButton = ToolbarUi.Button("◼", "填充矩形和椭圆", ToggleFill);
        BuildStyleBar();
    }

    #region Toolbar

    /// <summary>Adds the tool buttons, undo and redo to a toolbar row.</summary>
    public void AddToolButtons(Panel panel)
    {
        AddTool(panel, AnnotationTool.Select, "✥", "选择/移动标注（Delete 删除）");
        AddTool(panel, AnnotationTool.Rectangle, "□", "矩形（Shift 正方形）");
        AddTool(panel, AnnotationTool.Ellipse, "○", "椭圆（Shift 正圆）");
        AddTool(panel, AnnotationTool.Line, "╱", "直线（Shift 45° 对齐）");
        AddTool(panel, AnnotationTool.Arrow, "↗", "箭头（Shift 45° 对齐）");
        AddTool(panel, AnnotationTool.Pen, "✎", "画笔");
        AddTool(panel, AnnotationTool.Highlighter, "▌", "荧光笔");
        AddTool(panel, AnnotationTool.Text, "A", "文字");
        AddTool(panel, AnnotationTool.Step, "①", "序号");
        AddTool(panel, AnnotationTool.Mosaic, "▦", "马赛克");
        AddTool(panel, AnnotationTool.Blur, "░", "模糊");
        panel.Children.Add(ToolbarUi.Divider());
        panel.Children.Add(ToolbarUi.Button("↶", "撤销 (Ctrl+Z)", Undo));
        panel.Children.Add(ToolbarUi.Button("↷", "重做 (Ctrl+Y)", Redo));
    }

    void AddTool(Panel panel, AnnotationTool tool, string glyph, string tip)
    {
        var button = ToolbarUi.Button(glyph, tip, () => SetTool(Tool == tool ? AnnotationTool.None : tool));
        if (tool == AnnotationTool.Highlighter) ((TextBlock)button.Child).Foreground = new SolidColorBrush(Color.FromRgb(230, 180, 0));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    void BuildStyleBar()
    {
        for (int i = 0; i < StrokeSizes.Length; i++)
        {
            int index = i;
            double dot = 4 + i * 3;
            var button = ToolbarUi.Button("", new[] { "细", "中", "粗" }[i], () => SetPenSize(index));
            button.Child = new Ellipse { Width = dot, Height = dot, Fill = Brushes.DimGray };
            _sizeButtons.Add(button);
            StyleBar.Children.Add(button);
        }
        StyleBar.Children.Add(ToolbarUi.Divider());
        for (int i = 0; i <= Palette.Length; i++)
        {
            int index = i;
            bool custom = i == Palette.Length;
            var button = ToolbarUi.Button("", custom ? "自定义颜色…" : "", () =>
            {
                if (custom) PickCustomColor();
                else SetPenColor(index);
            });
            button.Width = 26;
            button.Child = custom
                ? new Grid { Children = { _customSwatch, new TextBlock { Text = "+", FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } } }
                : new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(Palette[i]), Stroke = Brushes.Gray, StrokeThickness = 1 };
            _colorButtons.Add(button);
            StyleBar.Children.Add(button);
        }
        StyleBar.Children.Add(ToolbarUi.Divider());
        StyleBar.Children.Add(_fillButton);
        UpdateStyleButtons();
    }

    public void SetTool(AnnotationTool tool)
    {
        CommitText();
        if (tool != AnnotationTool.Select) Deselect();
        Tool = tool;
        foreach (var pair in _toolButtons) ToolbarUi.SetSelected(pair.Value, pair.Key == tool);
        ToolChanged?.Invoke();
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

    void ToggleFill()
    {
        _service.Settings.FillShapes = !_service.Settings.FillShapes;
        _service.SaveSettings();
        UpdateStyleButtons();
    }

    void PickCustomColor()
    {
        using var dialog = new WinForms.ColorDialog { FullOpen = true, AnyColor = true };
        var current = CustomColor;
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        var window = Window.GetWindow(Content);
        var owner = window != null ? new Owner(new WindowInteropHelper(window).Handle) : null;
        if (dialog.ShowDialog(owner) != WinForms.DialogResult.OK) return;
        var c = dialog.Color;
        _service.Settings.CustomColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        SetPenColor(Palette.Length);
        window?.Activate();
        window?.Focus();
    }

    sealed class Owner : WinForms.IWin32Window
    {
        public Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    void UpdateStyleButtons()
    {
        for (int i = 0; i < _sizeButtons.Count; i++) ToolbarUi.SetSelected(_sizeButtons[i], i == SizeIndex);
        for (int i = 0; i < _colorButtons.Count; i++) ToolbarUi.SetSelected(_colorButtons[i], i == ColorIndex);
        ToolbarUi.SetSelected(_fillButton, _service.Settings.FillShapes);
        _customSwatch.Fill = new SolidColorBrush(CustomColor);
    }

    int SizeIndex => Math.Max(0, Math.Min(StrokeSizes.Length - 1, _service.Settings.PenSize));
    int ColorIndex => Math.Max(0, Math.Min(Palette.Length, _service.Settings.PenColor));

    Color CustomColor =>
        ColorTools.TryParse(_service.Settings.CustomColor, out var c) ? c : Color.FromRgb(255, 105, 180);

    Color PenColor => ColorIndex < Palette.Length ? Palette[ColorIndex] : CustomColor;

    Brush PenBrush
    {
        get
        {
            var brush = new SolidColorBrush(PenColor);
            brush.Freeze();
            return brush;
        }
    }

    #endregion

    #region Mouse

    /// <summary>Starts drawing, typing or moving at an image pixel. Returns true when a drag follows (capture the mouse).</summary>
    public bool MouseDown(Point p)
    {
        if (_text != null)
        {
            CommitText();
            if (Tool != AnnotationTool.Text) return false;
        }
        switch (Tool)
        {
            case AnnotationTool.None:
                return false;
            case AnnotationTool.Select:
            {
                var hit = HitTest(p);
                Select(hit);
                if (hit == null) return false;
                _moving = true;
                _start = p;
                _moveOrigin = OffsetOf(hit);
                return true;
            }
            case AnnotationTool.Text:
                BeginText(p);
                return false;
            case AnnotationTool.Step:
                AddStep(p);
                return false;
            default:
                _start = p;
                BeginDrawing(p);
                return true;
        }
    }

    public void MouseMove(Point p)
    {
        if (_moving && _selected != null)
        {
            SetOffset(_selected, _moveOrigin + (p - _start));
            UpdateSelectionBox();
        }
        else if (_drawing != null)
        {
            UpdateDrawing(p);
        }
    }

    public void MouseUp()
    {
        if (_moving)
        {
            _moving = false;
            if (_selected is { } element)
            {
                var from = _moveOrigin;
                var to = OffsetOf(element);
                if (from != to) Push(() => SetOffset(element, from), () => SetOffset(element, to));
            }
            return;
        }
        EndDrawing();
    }

    public Cursor CursorAt(Point p) => Tool switch
    {
        AnnotationTool.Select => HitTest(p) != null ? Cursors.SizeAll : Cursors.Arrow,
        AnnotationTool.Text => Cursors.IBeam,
        AnnotationTool.Step => Cursors.Hand,
        _ => Cursors.Cross,
    };

    /// <summary>Handles undo, redo and delete; false for other keys.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z && modifiers.HasFlag(ModifierKeys.Shift)) Redo();
        else if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.Y) Redo();
        else if (e.Key is Key.Delete or Key.Back && _selected != null) DeleteSelected();
        else return false;
        return true;
    }

    /// <summary>Keys while typing a text annotation: Enter finishes, Shift+Enter adds a line, Esc finishes too.</summary>
    public void HandleTextKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            CommitText();
            e.Handled = true;
        }
    }

    #endregion

    #region Selection

    /// <summary>The smallest annotation whose bounds contain the point.</summary>
    UIElement? HitTest(Point p)
    {
        UIElement? best = null;
        double bestArea = double.MaxValue;
        double tolerance = 4 * Scale;
        foreach (UIElement child in Content.Children)
        {
            var bounds = BoundsOf(child);
            if (bounds.IsEmpty) continue;
            bounds.Inflate(tolerance, tolerance);
            if (!bounds.Contains(p)) continue;
            double area = bounds.Width * bounds.Height;
            if (area < bestArea)
            {
                best = child;
                bestArea = area;
            }
        }
        return best;
    }

    Rect BoundsOf(UIElement element)
    {
        try
        {
            var local = VisualTreeHelper.GetDescendantBounds(element);
            return local.IsEmpty ? Rect.Empty : element.TransformToAncestor(Content).TransformBounds(local);
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    void Select(UIElement? element)
    {
        _selected = element;
        UpdateSelectionBox();
    }

    public void Deselect() => Select(null);

    void UpdateSelectionBox()
    {
        if (_selected == null || !Content.Children.Contains(_selected))
        {
            _selected = null;
            _selectionBox.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = BoundsOf(_selected);
        if (bounds.IsEmpty) return;
        double pad = 3 * Scale;
        bounds.Inflate(pad, pad);
        _selectionBox.StrokeThickness = Math.Max(1, Scale);
        Canvas.SetLeft(_selectionBox, bounds.X);
        Canvas.SetTop(_selectionBox, bounds.Y);
        _selectionBox.Width = bounds.Width;
        _selectionBox.Height = bounds.Height;
        _selectionBox.Visibility = Visibility.Visible;
    }

    static Vector OffsetOf(UIElement element) =>
        element.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : new Vector();

    void SetOffset(UIElement element, Vector offset)
    {
        if (element.RenderTransform is not TranslateTransform t || t.IsFrozen)
            element.RenderTransform = t = new TranslateTransform();
        t.X = offset.X;
        t.Y = offset.Y;
        // Mosaic and blur strokes show what lies under them, so their brush must not travel along
        if (element is Shape { Tag: EffectTag, Stroke: ImageBrush brush } shape)
        {
            var moved = brush.Clone();
            moved.Viewport = new Rect(-offset.X, -offset.Y, _source.Width, _source.Height);
            moved.Freeze();
            shape.Stroke = moved;
        }
        if (element == _selected) UpdateSelectionBox();
    }

    void DeleteSelected()
    {
        if (_selected is not { } element) return;
        int index = Content.Children.IndexOf(element);
        Deselect();
        if (index < 0) return;
        Content.Children.RemoveAt(index);
        Push(() => Content.Children.Insert(Math.Min(index, Content.Children.Count), element), () => Content.Children.Remove(element));
    }

    #endregion

    #region Drawing

    void BeginDrawing(Point p)
    {
        double stroke = StrokeSizes[SizeIndex] * Scale;
        var fill = _service.Settings.FillShapes ? PenBrush : null;
        switch (Tool)
        {
            case AnnotationTool.Rectangle:
                _drawing = new Path { Stroke = PenBrush, Fill = fill, StrokeThickness = stroke, Data = new RectangleGeometry(new Rect(p, p)) };
                break;
            case AnnotationTool.Ellipse:
                _drawing = new Path { Stroke = PenBrush, Fill = fill, StrokeThickness = stroke, Data = new EllipseGeometry(new Rect(p, p)) };
                break;
            case AnnotationTool.Line:
                _drawing = new Path
                {
                    Stroke = PenBrush,
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = new LineGeometry(p, p),
                };
                break;
            case AnnotationTool.Arrow:
                _drawing = new Path { Stroke = PenBrush, Fill = PenBrush, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round, Tag = "arrow" };
                break;
            case AnnotationTool.Pen:
                _drawing = Stroke(PenBrush, stroke, p);
                break;
            case AnnotationTool.Highlighter:
            {
                var c = PenColor;
                var brush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B));
                brush.Freeze();
                _drawing = Stroke(brush, stroke * 5, p);
                break;
            }
            case AnnotationTool.Mosaic:
            case AnnotationTool.Blur:
            {
                // Painting with a pixelated or blurred copy of the image reveals it wherever the brush goes
                var image = Tool == AnnotationTool.Mosaic ? _source.Mosaic(Math.Max(6, (int)(10 * Scale))) : _source.Blurred();
                var brush = new ImageBrush(image)
                {
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, _source.Width, _source.Height),
                    Stretch = Stretch.Fill,
                };
                RenderOptions.SetBitmapScalingMode(brush, Tool == AnnotationTool.Mosaic ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                brush.Freeze();
                _drawing = Stroke(brush, MosaicSizes[SizeIndex] * Scale, p);
                _drawing.Tag = EffectTag;
                break;
            }
        }
        if (_drawing != null) Content.Children.Add(_drawing);
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
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case Path { Data: RectangleGeometry rect }:
                rect.Rect = new Rect(_start, shift ? Square(p) : p);
                break;
            case Path { Data: EllipseGeometry ellipse }:
            {
                var end = shift ? Square(p) : p;
                ellipse.RadiusX = Math.Abs(end.X - _start.X) / 2;
                ellipse.RadiusY = Math.Abs(end.Y - _start.Y) / 2;
                ellipse.Center = new Point((end.X + _start.X) / 2, (end.Y + _start.Y) / 2);
                break;
            }
            case Path { Data: LineGeometry segment }:
                segment.EndPoint = shift ? Snap(p) : p;
                break;
            case Path arrow:
                arrow.Data = Arrow(_start, shift ? Snap(p) : p, arrow.StrokeThickness);
                break;
        }
    }

    Point Square(Point p)
    {
        double size = Math.Max(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        return new Point(_start.X + Math.Sign(p.X - _start.X) * size, _start.Y + Math.Sign(p.Y - _start.Y) * size);
    }

    /// <summary>The point moved onto the nearest 45° direction from the start.</summary>
    Point Snap(Point p)
    {
        var v = p - _start;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return _start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    void EndDrawing()
    {
        var drawing = _drawing;
        _drawing = null;
        if (drawing == null) return;
        if (drawing is Path path && !(path.Data is { } data && data.Bounds.Width + data.Bounds.Height >= 4))
        {
            Content.Children.Remove(path);
            return;
        }
        Added(drawing);
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

    /// <summary>A numbered circle; numbers continue from the markers already placed.</summary>
    void AddStep(Point p)
    {
        int number = 1;
        foreach (UIElement child in Content.Children)
            if (child is FrameworkElement { Tag: StepTag }) number++;
        double size = StepSizes[SizeIndex] * Scale;
        var color = PenColor;
        // Dark digits on light colours
        bool light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 170;
        var marker = new Grid
        {
            Width = size,
            Height = size,
            Tag = StepTag,
            Children =
            {
                new Ellipse { Fill = PenBrush, Stroke = light ? Brushes.Gray : Brushes.White, StrokeThickness = Math.Max(1, Scale) },
                new TextBlock
                {
                    Text = number.ToString(),
                    Foreground = light ? Brushes.Black : Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = size * (number < 10 ? 0.58 : 0.46),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        Canvas.SetLeft(marker, p.X - size / 2);
        Canvas.SetTop(marker, p.Y - size / 2);
        Content.Children.Add(marker);
        Added(marker);
    }

    void BeginText(Point p)
    {
        var color = PenBrush;
        _text = new TextBox
        {
            Foreground = color,
            CaretBrush = color,
            Background = Brushes.Transparent,
            BorderBrush = ToolbarUi.Accent,
            BorderThickness = new Thickness(Math.Max(1, Math.Round(Scale))),
            Padding = new Thickness(0),
            FontSize = FontSizes[SizeIndex] * Scale,
            AcceptsReturn = true,
            MinWidth = 24 * Scale,
        };
        Canvas.SetLeft(_text, p.X);
        Canvas.SetTop(_text, p.Y - _text.FontSize * 0.7);
        Content.Children.Add(_text);
        _text.Loaded += (_, _) => _text?.Focus();
    }

    /// <summary>Freezes the text being typed into a plain label.</summary>
    public void CommitText()
    {
        if (_text == null) return;
        var text = _text;
        _text = null;
        if (text.Text.Trim().Length == 0)
        {
            Content.Children.Remove(text);
        }
        else
        {
            // Keep the TextBox so layout doesn't shift, just make it inert
            text.Select(0, 0);
            text.IsReadOnly = true;
            text.Focusable = false;
            text.IsHitTestVisible = false;
            text.BorderBrush = Brushes.Transparent;
            Added(text);
        }
        Window.GetWindow(Content)?.Focus();
    }

    /// <summary>Drops the text being typed; false if there was none.</summary>
    public bool CancelText()
    {
        if (_text == null) return false;
        Content.Children.Remove(_text);
        _text = null;
        Window.GetWindow(Content)?.Focus();
        return true;
    }

    #endregion

    #region History

    void Added(UIElement element) => Push(() => Content.Children.Remove(element), () => Content.Children.Add(element));

    void Push(Action undo, Action redo)
    {
        _undo.Add((undo, redo));
        _redo.Clear();
    }

    public void Undo()
    {
        if (CancelText()) return;
        Deselect();
        if (_undo.Count == 0) return;
        var step = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        step.Undo();
        _redo.Add(step);
    }

    public void Redo()
    {
        CommitText();
        Deselect();
        if (_redo.Count == 0) return;
        var step = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        step.Redo();
        _undo.Add(step);
    }

    public void Clear()
    {
        _text = null;
        _drawing = null;
        _moving = false;
        Deselect();
        Content.Children.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Call before rendering <see cref="Content"/>: finishes typing and hides the selection.</summary>
    public void PrepareRender()
    {
        CommitText();
        Deselect();
    }

    /// <summary>The image with the annotations drawn on top, at 96 DPI so one unit is one pixel.</summary>
    public BitmapSource Render(BitmapSource image, Int32Rect crop)
    {
        PrepareRender();
        var target = new Rect(0, 0, crop.Width, crop.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop.X == 0 && crop.Y == 0 && crop.Width == image.PixelWidth && crop.Height == image.PixelHeight ? image : new CroppedBitmap(image, crop), target);
            if (Content.Children.Count > 0)
            {
                var brush = new VisualBrush(Content)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    Stretch = Stretch.Fill,
                };
                dc.DrawRectangle(brush, null, target);
            }
        }
        var bitmap = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    #endregion
}
