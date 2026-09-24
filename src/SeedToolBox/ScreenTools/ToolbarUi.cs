using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SeedToolBox.ScreenTools;

/// <summary>The flat buttons and cards of the capture and annotation toolbars.</summary>
static class ToolbarUi
{
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(30, 144, 255));
    public static readonly Brush ButtonHover = new SolidColorBrush(Color.FromRgb(229, 229, 229));
    public static readonly Brush ButtonSelected = new SolidColorBrush(Color.FromRgb(204, 228, 247));
    static readonly Brush Glyph = new SolidColorBrush(Color.FromRgb(51, 51, 51));
    // Segoe UI Symbol ships with Win7 (with updates) through Win11, so the glyphs render everywhere
    static readonly FontFamily Font = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");

    public static Border Card(UIElement child) => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(3),
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3 },
        Child = child,
    };

    public static Rectangle Divider() => new() { Width = 1, Height = 18, Fill = new SolidColorBrush(Color.FromRgb(221, 221, 221)), Margin = new Thickness(4, 0, 4, 0) };

    /// <summary>Flat button made from a Border, so it never takes keyboard focus from the overlay.</summary>
    public static Border Button(string glyph, string tip, Action onClick, Brush? foreground = null)
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
                FontFamily = Font,
                FontSize = 16,
                Foreground = foreground ?? Glyph,
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

    public static void SetSelected(Border button, bool selected) =>
        button.Background = selected ? ButtonSelected : button.IsMouseOver ? ButtonHover : Brushes.Transparent;

    /// <summary>A small text button, for choices that read better as words.</summary>
    public static Border TextButton(string text, string tip, Action onClick)
    {
        var button = Button(text, tip, onClick);
        button.Width = double.NaN;
        button.Padding = new Thickness(7, 0, 7, 0);
        var block = (TextBlock)button.Child;
        block.FontSize = 12;
        block.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        return button;
    }
}
