using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Recording;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>A short message near the bottom of the screen under the cursor; it never takes focus or clicks.</summary>
static class Toast
{
    public static void Show(string text, double seconds = 3)
    {
        var toast = OverlayTools.Create(true, true);
        toast.SizeToContent = SizeToContent.WidthAndHeight;
        toast.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            MaxWidth = 640,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap },
        };
        toast.Loaded += (_, _) =>
        {
            var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
            double scale = OverlayTools.ScaleOf(toast);
            int w = (int)(toast.ActualWidth * scale), h = (int)(toast.ActualHeight * scale);
            OverlayTools.Place(toast, area.Left + (area.Width - w) / 2, area.Bottom - h - (int)(40 * scale), 0, 0);
        };
        toast.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        timer.Start();
    }
}
