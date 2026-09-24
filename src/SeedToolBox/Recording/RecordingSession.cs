using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Core.Services;
using Rectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Recording;

/// <summary>
/// One recording from countdown to finished file: shows a frame around the region and a control bar,
/// and raises <see cref="Finished"/> on the UI thread.
/// </summary>
sealed class RecordingSession
{
    readonly Rectangle _region;
    readonly RecordOptions _options;
    readonly Recorder _recorder;
    readonly RegionFrame _frame;
    readonly ControlBar _bar;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    CountdownWindow? _countdown;
    bool _started, _stopping;

    /// <summary>The clip (null if cancelled or failed), the error if it failed, and audio sources that could not be recorded.</summary>
    public event Action<RecordedClip?, Exception?, IReadOnlyList<string>>? Finished;

    public RecordingSession(Rectangle region, RecordOptions options)
    {
        _region = region;
        _options = options;
        if (options.ShowKeys) options.Keys = new KeyWatcher();
        var folder = Path.Combine(Path.GetTempPath(), "SeedToolBox");
        Directory.CreateDirectory(folder);
        _recorder = new Recorder(region, options, Path.Combine(folder, $"rec_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mp4"));
        _frame = new RegionFrame(region, options.Scale);
        _bar = new ControlBar(region, options.Scale);
        _bar.PauseClicked += TogglePause;
        _bar.StopClicked += Stop;
        _timer.Tick += (_, _) => _bar.Update(_recorder.Elapsed, _recorder.Paused);
    }

    /// <summary>Folder for recordings waiting in the editor; files left over from a crash are removed.</summary>
    public static void CleanTemp()
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "SeedToolBox");
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder, "rec_*.mp4"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromDays(1))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to clean recording temp files", ex);
        }
    }

    public void Start(bool countdown)
    {
        _recorder.Prepare();
        _frame.Show();
        if (!countdown)
        {
            Begin();
            return;
        }
        _countdown = new CountdownWindow(_region, _options.Scale);
        _countdown.Done += () =>
        {
            _countdown = null;
            Begin();
        };
        _countdown.Show();
    }

    void Begin()
    {
        if (_stopping) return;
        _started = true;
        _recorder.Begin();
        _bar.Show();
        _timer.Start();
    }

    void TogglePause()
    {
        if (_stopping) return;
        _recorder.Paused = !_recorder.Paused;
        _bar.Update(_recorder.Elapsed, _recorder.Paused);
    }

    /// <summary>Stops recording; during the countdown this cancels instead.</summary>
    public async void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        long duration = _recorder.Elapsed;
        _countdown?.Close();
        _recorder.Stop();
        _timer.Stop();
        _bar.ShowSaving();

        var error = await _recorder.Completion;
        _options.Keys?.Dispose();
        // Cancelled in the countdown: finalizing an empty file may fail, which doesn't matter
        if (!_started) error = null;
        _frame.Close();
        _bar.Close();

        RecordedClip? clip = null;
        if (error == null && _started && duration > 0)
            clip = new RecordedClip(_recorder.Path, _region.Width, _region.Height, _options.Fps, duration);
        else
            TryDelete(_recorder.Path);
        Finished?.Invoke(clip, error, _recorder.AudioErrors);
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Error($"Failed to delete {path}", ex); }
    }
}

/// <summary>Helpers for the small always-on-top windows shown while recording, positioned in physical pixels.</summary>
static class OverlayTools
{
    public static Window Create(bool clickThrough, bool excludeFromCapture)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = -32000,
            Top = -32000,
            UseLayoutRounding = true,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (clickThrough) style |= WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
            // Windows 10 2004+: keep the window out of the recording. Only for small windows: GDI
            // capture blacks out the whole excluded window, even its transparent parts
            if (excludeFromCapture) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        };
        return window;
    }

    public static void Place(Window window, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        uint flags = SWP_NOACTIVATE | (width <= 0 ? SWP_NOSIZE : 0);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, flags);
    }

    /// <summary>Physical pixels per DIP for the window's current monitor.</summary>
    public static double ScaleOf(Window window) => VisualTreeHelper.GetDpi(window).DpiScaleX;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

/// <summary>A red frame just outside the recorded region, so it never appears in the video.</summary>
sealed class RegionFrame
{
    readonly Window _window = OverlayTools.Create(true, false);
    readonly Border _border = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(230, 40, 40)) };
    readonly Rectangle _region;
    readonly int _thickness;

    public RegionFrame(Rectangle region, double scale)
    {
        _region = region;
        _thickness = Math.Max(2, (int)Math.Round(2 * scale));
        _window.Content = _border;
        _window.SourceInitialized += (_, _) => Place();
        _window.DpiChanged += (_, _) => _window.Dispatcher.BeginInvoke(new Action(Place));
    }

    void Place()
    {
        int t = _thickness;
        OverlayTools.Place(_window, _region.X - t, _region.Y - t, _region.Width + 2 * t, _region.Height + 2 * t);
        _border.BorderThickness = new Thickness(t / OverlayTools.ScaleOf(_window));
    }

    public void Show() => _window.Show();
    public void Close() => _window.Close();
}

/// <summary>3-2-1 in the middle of the region.</summary>
sealed class CountdownWindow
{
    // Gone before recording starts
    readonly Window _window = OverlayTools.Create(true, false);
    readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontSize = 72,
        FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Rectangle _region;
    int _count = 3;

    public event Action? Done;

    public CountdownWindow(Rectangle region, double scale)
    {
        _region = region;
        _window.Width = _window.Height = 150;
        _window.Content = new Border
        {
            Width = 150,
            Height = 150,
            CornerRadius = new CornerRadius(75),
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            Child = _text,
        };
        _text.Text = _count.ToString();
        _window.SourceInitialized += (_, _) => Place(scale);
        _window.DpiChanged += (_, e) => _window.Dispatcher.BeginInvoke(new Action(() => Place(e.NewDpi.DpiScaleX)));
        _timer.Tick += (_, _) =>
        {
            if (--_count > 0)
            {
                _text.Text = _count.ToString();
                return;
            }
            Close();
            Done?.Invoke();
        };
    }

    void Place(double scale)
    {
        int size = (int)Math.Round(150 * scale);
        OverlayTools.Place(_window, _region.X + (_region.Width - size) / 2, _region.Y + (_region.Height - size) / 2, size, size);
    }

    public void Show()
    {
        _window.Show();
        _timer.Start();
    }

    public void Close()
    {
        _timer.Stop();
        _window.Close();
    }
}

/// <summary>Elapsed time, pause/resume and stop, placed outside the region when there is room.</summary>
sealed class ControlBar
{
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(240, 60, 60));
    static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

    readonly Window _window = OverlayTools.Create(false, true);
    readonly Rectangle _region;
    readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 10, Height = 10, Fill = Red, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 14, Width = 64, VerticalAlignment = VerticalAlignment.Center, Text = "00:00" };
    readonly Border _pause, _stop;
    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

    public event Action? PauseClicked;
    public event Action? StopClicked;

    public ControlBar(Rectangle region, double scale)
    {
        _region = region;
        _pause = Button("⏸", "暂停", () => PauseClicked?.Invoke(), Brushes.White);
        _stop = Button("■", "停止录制（再按录屏热键也可停止）", () => StopClicked?.Invoke(), Red);
        _panel.Children.Add(_dot);
        _panel.Children.Add(_time);
        _panel.Children.Add(_pause);
        _panel.Children.Add(_stop);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = _panel,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动可移动",
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            try { _window.DragMove(); } catch (InvalidOperationException) { }
        };
        _window.Content = card;
        _window.SizeToContent = SizeToContent.WidthAndHeight;
        _window.Loaded += (_, _) => Place();
    }

    static Border Button(string glyph, string tip, Action onClick, Brush foreground)
    {
        var button = new Border
        {
            Width = 32,
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"),
                FontSize = 15,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.MouseEnter += (_, _) => button.Background = Hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    void Place()
    {
        double scale = OverlayTools.ScaleOf(_window);
        int width = (int)Math.Ceiling(_window.ActualWidth * scale), height = (int)Math.Ceiling(_window.ActualHeight * scale);
        var screen = WinForms.Screen.FromRectangle(_region).Bounds;
        int gap = (int)(8 * scale);

        int x = Math.Max(screen.Left, Math.Min(screen.Right - width, _region.Right - width));
        int y = _region.Bottom + gap;
        if (y + height > screen.Bottom) y = _region.Top - gap - height;
        // No room outside (e.g. full screen): sit inside the bottom edge
        if (y < screen.Top) y = _region.Bottom - gap - height;
        OverlayTools.Place(_window, x, y, 0, 0);
    }

    public void Update(long elapsed, bool paused)
    {
        var t = TimeSpan.FromTicks(elapsed);
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        ((TextBlock)_pause.Child).Text = paused ? "▶" : "⏸";
        _pause.ToolTip = paused ? "继续" : "暂停";
        // Blink while recording, steady grey while paused
        _dot.Fill = paused ? Brushes.Gray : Red;
        _dot.Opacity = paused || DateTime.Now.Millisecond < 500 ? 1 : 0.3;
    }

    public void ShowSaving()
    {
        _pause.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Collapsed;
        _time.Width = double.NaN;
        _time.Text = "正在保存…";
        _time.Margin = new Thickness(0, 0, 8, 0);
    }

    public void Show() => _window.Show();
    public void Close() => _window.Close();
}
