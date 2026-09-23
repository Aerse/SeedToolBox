using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Full-screen window over all monitors showing a frozen screenshot.
/// <see cref="Surface"/> is laid out in screenshot pixels; <see cref="Ui"/> is in normal DIPs for toolbars and labels.
/// </summary>
public abstract class OverlayWindow : Window
{
    protected ScreenShot Shot { get; }
    protected Canvas Surface { get; }
    protected Canvas Ui { get; }

    /// <summary>Physical pixels per DIP.</summary>
    protected double Scale { get; private set; } = 1;

    readonly ScaleTransform _surfaceScale = new();

    protected OverlayWindow(ScreenShot shot)
    {
        Shot = shot;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        var background = new Image { Source = shot.Image, Width = shot.Width, Height = shot.Height };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);
        Surface = new Canvas { Width = shot.Width, Height = shot.Height, ClipToBounds = true, LayoutTransform = _surfaceScale };
        Surface.Children.Add(background);
        Ui = new Canvas();

        var root = new Grid();
        root.Children.Add(Surface);
        root.Children.Add(Ui);
        Content = root;

        SourceInitialized += (_, _) => { UpdateScale(VisualTreeHelper.GetDpi(this).DpiScaleX); CoverScreen(); };
        DpiChanged += (_, e) =>
        {
            UpdateScale(e.NewDpi.DpiScaleX);
            // WPF resizes the window for the new DPI; put it back over the whole screen
            Dispatcher.BeginInvoke(new Action(CoverScreen));
        };
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    void UpdateScale(double scale)
    {
        Scale = scale;
        _surfaceScale.ScaleX = _surfaceScale.ScaleY = 1 / scale;
        OnScaleChanged();
    }

    protected virtual void OnScaleChanged() { }

    void CoverScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, Shot.X, Shot.Y, Shot.Width, Shot.Height, SWP_NOACTIVATE);
    }

    /// <summary>Screenshot pixel → DIP position in <see cref="Ui"/>.</summary>
    protected Point ToUi(Point pixel) => new(pixel.X / Scale, pixel.Y / Scale);

    protected Point PixelOf(MouseEventArgs e) => e.GetPosition(Surface);

    /// <summary>Moves the real cursor by one pixel, for precise positioning with the arrow keys.</summary>
    protected static bool NudgeCursor(Key key)
    {
        int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
        int dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
        if (dx == 0 && dy == 0) return false;
        GetCursorPos(out var p);
        SetCursorPos(p.X + dx, p.Y + dy);
        return true;
    }

    /// <summary>Places a UI element next to the cursor, flipping sides near the screen edge.</summary>
    protected void PlaceNear(FrameworkElement element, Point uiPoint, double offset = 18)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;
        double x = uiPoint.X + offset, y = uiPoint.Y + offset;
        if (x + size.Width > ActualWidth) x = uiPoint.X - offset - size.Width;
        if (y + size.Height > ActualHeight) y = uiPoint.Y - offset - size.Height;
        Canvas.SetLeft(element, Math.Max(0, x));
        Canvas.SetTop(element, Math.Max(0, y));
    }

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
}
