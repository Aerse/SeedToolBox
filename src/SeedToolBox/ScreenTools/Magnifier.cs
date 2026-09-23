using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SeedToolBox.ScreenTools;

/// <summary>Zoomed view of the pixels around the cursor, with a text area underneath.</summary>
public sealed class Magnifier : Border
{
    const int Cells = 17;       // odd, so there is a center pixel
    const double CellSize = 8;

    readonly ImageBrush _brush;
    readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(6, 4, 6, 5), LineHeight = 18 };

    public Magnifier(ScreenShot shot)
    {
        _brush = new ImageBrush(shot.Image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var zoom = new Rectangle { Width = Cells * CellSize, Height = Cells * CellSize, Fill = _brush };
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);

        double center = Cells / 2 * CellSize;
        var crossH = new Rectangle { Width = Cells * CellSize, Height = CellSize, Fill = new SolidColorBrush(Color.FromArgb(50, 30, 144, 255)) };
        var crossV = new Rectangle { Width = CellSize, Height = Cells * CellSize, Fill = crossH.Fill };
        var pixel = new Rectangle { Width = CellSize + 2, Height = CellSize + 2, Stroke = Brushes.Black, StrokeThickness = 1 };
        Canvas.SetTop(crossH, center);
        Canvas.SetLeft(crossV, center);
        Canvas.SetLeft(pixel, center - 1);
        Canvas.SetTop(pixel, center - 1);
        var canvas = new Canvas { Width = zoom.Width, Height = zoom.Height, Children = { zoom, crossH, crossV, pixel } };

        Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32));
        BorderBrush = Brushes.White;
        BorderThickness = new Thickness(1);
        IsHitTestVisible = false;
        Child = new StackPanel { Children = { canvas, _text } };
    }

    public void Update(int x, int y, string text)
    {
        _brush.Viewbox = new Rect(x - Cells / 2, y - Cells / 2, Cells, Cells);
        _text.Text = text;
    }
}

public enum ColorFormat { Hex, Rgb, Hsl }

public static class ColorText
{
    public static ColorFormat Next(ColorFormat format) => (ColorFormat)(((int)format + 1) % 3);

    public static string Format(Color c, ColorFormat format)
    {
        switch (format)
        {
            case ColorFormat.Rgb:
                return $"rgb({c.R}, {c.G}, {c.B})";
            case ColorFormat.Hsl:
                var (h, s, l) = ToHsl(c);
                return $"hsl({h}, {s}%, {l}%)";
            default:
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    static (int H, int S, int L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0, d = max - min;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return ((int)Math.Round(h) % 360, (int)Math.Round(s * 100), (int)Math.Round(l * 100));
    }
}
