using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeedToolBox.Views;

/// <summary>Semi-transparent snapshot of the dragged element that follows the cursor.</summary>
public sealed class DragAdorner : Adorner
{
    readonly ImageSource _image;
    readonly Size _size;
    readonly Vector _grabOffset;
    Point _position;
    bool _visible = true;

    public DragAdorner(UIElement adornedElement, FrameworkElement source, Point grabPoint) : base(adornedElement)
    {
        _size = new Size(source.ActualWidth, source.ActualHeight);
        _image = Snapshot(source);
        _grabOffset = (Vector)grabPoint;
        IsHitTestVisible = false;
    }

    public void MoveTo(Point position)
    {
        _position = position;
        _visible = true;
        InvalidateVisual();
    }

    public void Hide()
    {
        _visible = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_visible) return;
        dc.PushOpacity(0.8);
        dc.DrawImage(_image, new Rect(_position - _grabOffset, _size));
        dc.Pop();
    }

    static ImageSource Snapshot(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Render through a brush so the element's position in its parent doesn't matter
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
