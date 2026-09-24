using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Recording;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>Screenshots that start from a known region: full screen, active window, last region, and delayed.</summary>
public sealed partial class ScreenToolService
{
    public static readonly int[] Delays = { 3, 5, 10 };
    Window? _countdown;

    /// <summary>Opens the screenshot overlay already editing a region given in screen pixels.</summary>
    void ScreenshotOf(System.Drawing.Rectangle screen) =>
        Open(shot => new CaptureWindow(shot, WindowFinder.Snapshot(shot), this, CaptureMode.Screenshot,
            new Rect(screen.X - shot.X, screen.Y - shot.Y, screen.Width, screen.Height)));

    /// <summary>The monitor under the cursor.</summary>
    public void FullScreen() => ScreenshotOf(WinForms.Screen.FromPoint(WinForms.Cursor.Position).Bounds);

    /// <summary>The foreground window without its invisible borders; falls back to a normal screenshot.</summary>
    public void ActiveWindow()
    {
        if (WindowFinder.ForegroundBounds() is { } bounds) ScreenshotOf(bounds);
        else Screenshot();
    }

    public bool HasLastRegion => Settings.LastRegion is { Length: 4 } r && r[2] > 1 && r[3] > 1;

    public void RepeatLastRegion()
    {
        if (!HasLastRegion)
        {
            Screenshot();
            return;
        }
        var r = Settings.LastRegion!;
        ScreenshotOf(new System.Drawing.Rectangle(r[0], r[1], r[2], r[3]));
    }

    internal void RememberRegion(System.Drawing.Rectangle region)
    {
        Settings.LastRegion = new[] { region.X, region.Y, region.Width, region.Height };
        SaveSettings();
    }

    /// <summary>Counts down, then takes a screenshot, so menus and hover states can be opened first.</summary>
    public void DelayedScreenshot() => DelayedScreenshot(Settings.CaptureDelay);

    public void DelayedScreenshot(int seconds)
    {
        if (_countdown != null || _active != null) return;
        seconds = Math.Max(1, Math.Min(60, seconds));
        var text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 40,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = seconds.ToString(),
        };
        // Click-through and kept out of captures, so the app below stays usable while waiting
        var window = _countdown = OverlayTools.Create(true, true);
        window.Width = window.Height = 90;
        window.Content = new Border
        {
            CornerRadius = new CornerRadius(45),
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    text,
                    new TextBlock { Text = "秒后截图", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        window.SourceInitialized += (_, _) =>
        {
            // Top right of the cursor's monitor, out of the way of what is being prepared
            var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
            double scale = OverlayTools.ScaleOf(window);
            int size = (int)Math.Round(90 * scale), margin = (int)(24 * scale);
            OverlayTools.Place(window, area.Right - size - margin, area.Top + margin, size, size);
        };
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var left = end - DateTime.UtcNow;
            text.Text = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds)).ToString();
            // Gone a moment early: exclusion from capture needs Windows 10 2004+
            if (left.TotalMilliseconds > 150) return;
            timer.Stop();
            window.Close();
            _countdown = null;
            var shoot = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            shoot.Tick += (_, _) => { shoot.Stop(); Screenshot(); };
            shoot.Start();
        };
        window.Show();
        timer.Start();
    }
}
