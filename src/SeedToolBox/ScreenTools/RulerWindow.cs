using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Pixel ruler over a frozen screen. The crosshair stretches to the nearest color edges,
/// which measures gaps and element sizes; dragging measures a box.
/// </summary>
sealed class RulerWindow : OverlayWindow
{
    // Max per-channel difference still treated as the same color
    const int Tolerance = 12;
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(255, 45, 125));

    readonly Magnifier _magnifier;
    readonly Path _crosshair = new() { Stroke = Accent, IsHitTestVisible = false };
    readonly Rectangle _box = new() { Stroke = Accent, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 45, 125)), Visibility = Visibility.Collapsed };
    readonly Border _boxLabel = Label();
    Point? _dragStart;
    string _lastMeasure = "";

    public RulerWindow(ScreenShot shot) : base(shot)
    {
        _magnifier = new Magnifier(shot);
        Surface.Children.Add(_box);
        Surface.Children.Add(_crosshair);
        Ui.Children.Add(_boxLabel);
        Ui.Children.Add(_magnifier);
        _boxLabel.Visibility = Visibility.Collapsed;

        MouseMove += (_, e) => UpdateAt(PixelOf(e));
        Loaded += (_, _) => UpdateAt(Mouse.GetPosition(Surface));
        MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = Snap(PixelOf(e));
            CaptureMouse();
            UpdateAt(PixelOf(e));
        };
        MouseLeftButtonUp += (_, e) =>
        {
            _dragStart = null;
            ReleaseMouseCapture();
            // A plain click clears the box instead of leaving a 1×1 measurement
            if (_box.Width <= 1 && _box.Height <= 1)
            {
                _box.Visibility = Visibility.Collapsed;
                _boxLabel.Visibility = Visibility.Collapsed;
            }
            UpdateAt(PixelOf(e));
        };
        MouseRightButtonUp += (_, _) => Close();
        PreviewKeyDown += OnKey;
    }

    protected override void OnScaleChanged()
    {
        _crosshair.StrokeThickness = Scale;
        _box.StrokeThickness = Scale;
    }

    static Border Label() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)),
        Padding = new Thickness(6, 2, 6, 3),
        IsHitTestVisible = false,
        Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 },
    };

    Point Snap(Point p) => new(Math.Max(0, Math.Min(Shot.Width - 1, (int)p.X)), Math.Max(0, Math.Min(Shot.Height - 1, (int)p.Y)));

    void UpdateAt(Point pixel)
    {
        var p = Snap(pixel);
        int x = (int)p.X, y = (int)p.Y;

        if (_dragStart is { } start)
        {
            // Inclusive of both end pixels
            int left = (int)Math.Min(start.X, x), top = (int)Math.Min(start.Y, y);
            int w = (int)Math.Abs(start.X - x) + 1, h = (int)Math.Abs(start.Y - y) + 1;
            Canvas.SetLeft(_box, left);
            Canvas.SetTop(_box, top);
            _box.Width = w;
            _box.Height = h;
            _box.Visibility = Visibility.Visible;
            _lastMeasure = $"{w} × {h}";
            ((TextBlock)_boxLabel.Child).Text = _lastMeasure;
            _boxLabel.Visibility = Visibility.Visible;
            Canvas.SetLeft(_boxLabel, (left + w) / Scale + 4);
            Canvas.SetTop(_boxLabel, (top + h) / Scale + 4);
        }

        int l = Scan(x, y, -1, 0), r = Scan(x, y, 1, 0), t = Scan(x, y, 0, -1), b = Scan(x, y, 0, 1);
        double tick = 5 * Scale;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            // +0.5 centers the lines on pixels
            double cy = y + 0.5, cx = x + 0.5;
            Line(ctx, l, cy, r + 1, cy);
            Line(ctx, l, cy - tick, l, cy + tick);
            Line(ctx, r + 1, cy - tick, r + 1, cy + tick);
            Line(ctx, cx, t, cx, b + 1);
            Line(ctx, cx - tick, t, cx + tick, t);
            Line(ctx, cx - tick, b + 1, cx + tick, b + 1);
        }
        g.Freeze();
        _crosshair.Data = g;
        _crosshair.Visibility = _dragStart == null ? Visibility.Visible : Visibility.Collapsed;

        string gap = $"{r - l + 1} × {b - t + 1}";
        if (_dragStart == null && _box.Visibility != Visibility.Visible) _lastMeasure = gap;
        _magnifier.Update(x, y, $"{x + Shot.X}, {y + Shot.Y}\n间距 {gap}\n拖动测量 · C 复制 · Esc 退出");
        PlaceNear(_magnifier, ToUi(p), 24);
    }

    static void Line(StreamGeometryContext ctx, double x1, double y1, double x2, double y2)
    {
        ctx.BeginFigure(new Point(x1, y1), false, false);
        ctx.LineTo(new Point(x2, y2), true, false);
    }

    /// <summary>Walks from (x, y) in one direction while the color stays the same; returns the last matching coordinate.</summary>
    int Scan(int x, int y, int dx, int dy)
    {
        var origin = Shot.GetColor(x, y);
        while (Shot.Contains(x + dx, y + dy) && Same(Shot.GetColor(x + dx, y + dy), origin))
        {
            x += dx;
            y += dy;
        }
        return dx != 0 ? x : y;
    }

    static bool Same(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= Tolerance && Math.Abs(a.G - b.G) <= Tolerance && Math.Abs(a.B - b.B) <= Tolerance;

    void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C:
                Close();
                ScreenToolService.CopyText(_lastMeasure.Replace(" ", ""));
                break;
            default: e.Handled = NudgeCursor(e.Key); break;
        }
    }
}
