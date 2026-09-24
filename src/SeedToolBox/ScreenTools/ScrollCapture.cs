using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SeedToolBox.Recording;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Joins repeated captures of a region while the user scrolls it by hand. Each frame is matched
/// against the previous one by row hashes to find how far the content moved.
/// </summary>
sealed class ScrollStitcher
{
    readonly int _width, _height, _stride, _hashWidth, _maxRows;
    readonly List<byte[]> _rows = new();
    ulong[]? _previous;

    public ScrollStitcher(int width, int height, double scale)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        // A moving scroll bar thumb on the right would break every match
        _hashWidth = width > 100 ? width - (int)Math.Min(width / 4, 24 * scale) : width;
        // Bounded both in height and in memory (about 250 MB)
        _maxRows = (int)Math.Min(20000, 250L * 1024 * 1024 / _stride);
    }

    public int Height => _rows.Count;
    public bool Full => _rows.Count >= _maxRows;

    public enum Result { First, Same, Added, NoMatch, Full }

    public Result Add(byte[] frame)
    {
        if (Full) return Result.Full;
        var hashes = new ulong[_height];
        for (int y = 0; y < _height; y++) hashes[y] = HashRow(frame, y);
        if (_previous == null)
        {
            for (int y = 0; y < _height; y++) _rows.Add(Row(frame, y));
            _previous = hashes;
            return Result.First;
        }
        var prev = _previous;
        int same = 0;
        while (same < _height && prev[same] == hashes[same]) same++;
        if (same == _height) return Result.Same;

        // Fixed header and footer (toolbars, status bars) stay in place while the content moves
        int limit = _height / 3;
        int top = Math.Min(same, limit);
        int bottom = 0;
        while (bottom < limit && prev[_height - 1 - bottom] == hashes[_height - 1 - bottom]) bottom++;
        int content = _height - top - bottom;
        if (content < 8) return Result.NoMatch;

        int best = 0, bestScore = 0;
        for (int d = 1; d < content - 4; d++)
        {
            int n = content - d, allowed = n / 10, misses = 0, score = 0;
            for (int y = top; y < top + n; y++)
            {
                if (hashes[y] != prev[y + d])
                {
                    if (++misses > allowed) break;
                }
                else if ((hashes[y] & Uniform) == 0) score++;
            }
            // Blank rows match anything, so only rows with content count
            if (misses <= allowed && score >= Math.Max(4, n / 8) && score > bestScore)
            {
                best = d;
                bestScore = score;
            }
        }
        if (best == 0) return Result.NoMatch;

        // The footer of the joined image is replaced by the new rows and this frame's footer
        _rows.RemoveRange(_rows.Count - bottom, bottom);
        for (int y = _height - bottom - best; y < _height && !Full; y++) _rows.Add(Row(frame, y));
        _previous = hashes;
        return Full ? Result.Full : Result.Added;
    }

    const ulong Uniform = 1;

    /// <summary>FNV-1a over the row; the lowest bit marks rows of a single colour.</summary>
    ulong HashRow(byte[] frame, int y)
    {
        int start = y * _stride;
        ulong hash = 14695981039346656037;
        uint first = BitConverter.ToUInt32(frame, start);
        bool uniform = true;
        for (int x = 0; x < _hashWidth; x++)
        {
            int i = start + x * 4;
            uint pixel = (uint)(frame[i] | frame[i + 1] << 8 | frame[i + 2] << 16);
            if (pixel != (first & 0xFFFFFF)) uniform = false;
            hash = (hash ^ pixel) * 1099511628211;
        }
        return uniform ? hash | Uniform : hash & ~Uniform;
    }

    byte[] Row(byte[] frame, int y)
    {
        var row = new byte[_stride];
        Buffer.BlockCopy(frame, y * _stride, row, 0, _stride);
        return row;
    }

    public BitmapSource ToImage()
    {
        var pixels = new byte[_rows.Count * _stride];
        for (int y = 0; y < _rows.Count; y++) Buffer.BlockCopy(_rows[y], 0, pixels, y * _stride, _stride);
        var image = BitmapSource.Create(_width, _rows.Count, 96, 96, PixelFormats.Bgr32, null, pixels, _stride);
        image.Freeze();
        return image;
    }
}

/// <summary>Frames the region, captures it a few times a second, and shows progress with 完成 and 取消.</summary>
sealed class ScrollCaptureSession
{
    readonly System.Drawing.Rectangle _region;
    readonly RegionFrame _frame;
    readonly Window _bar = OverlayTools.Create(false, true);
    readonly TextBlock _status = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), MinWidth = 220 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly ScrollStitcher _stitcher;
    bool _ended;

    /// <summary>The joined image, or null when cancelled.</summary>
    public event Action<BitmapSource?>? Finished;

    public ScrollCaptureSession(System.Drawing.Rectangle region, double scale)
    {
        _region = region;
        _frame = new RegionFrame(region, scale);
        _stitcher = new ScrollStitcher(region.Width, region.Height, scale);
        _status.Text = "请用鼠标滚轮慢慢向下滚动区域内的内容";

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { _status } };
        panel.Children.Add(BarButton("完成", () => End(true), ToolbarUi.Accent));
        panel.Children.Add(BarButton("取消", () => End(false), new SolidColorBrush(Color.FromRgb(90, 90, 90))));
        _bar.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = panel,
        };
        _bar.SizeToContent = SizeToContent.WidthAndHeight;
        _bar.Loaded += (_, _) => PlaceBar();
        _timer.Tick += (_, _) => Tick();
    }

    static Border BarButton(string text, Action onClick, Brush background)
    {
        var button = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 5, 14, 6),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13 },
        };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    void PlaceBar()
    {
        double scale = OverlayTools.ScaleOf(_bar);
        int width = (int)Math.Ceiling(_bar.ActualWidth * scale), height = (int)Math.Ceiling(_bar.ActualHeight * scale);
        var screen = WinForms.Screen.FromRectangle(_region).Bounds;
        int gap = (int)(8 * scale);
        int x = Math.Max(screen.Left, Math.Min(screen.Right - width, _region.Right - width));
        int y = _region.Bottom + gap;
        if (y + height > screen.Bottom) y = _region.Top - gap - height;
        // No room outside: inside the bottom edge. It is excluded from capture on Windows 10 2004+
        if (y < screen.Top) y = _region.Bottom - gap - height;
        OverlayTools.Place(_bar, x, y, 0, 0);
    }

    public void Start()
    {
        _frame.Show();
        _bar.Show();
        Tick();
        _timer.Start();
    }

    void Tick()
    {
        if (_ended) return;
        byte[] frame;
        try { frame = ScreenShot.CaptureRegion(_region); }
        catch (Exception ex)
        {
            SeedToolBox.Core.Services.Log.Error("Scrolling capture failed", ex);
            _status.Text = "截取失败";
            return;
        }
        switch (_stitcher.Add(frame))
        {
            case ScrollStitcher.Result.Added:
            case ScrollStitcher.Result.First:
                _status.Text = $"已拼接 {_stitcher.Height} 像素高，继续滚动或点完成";
                break;
            case ScrollStitcher.Result.NoMatch:
                _status.Text = $"已拼接 {_stitcher.Height} 像素高；滚得太快，请往回滚一点";
                break;
            case ScrollStitcher.Result.Full:
                _status.Text = $"已达到最大高度 {_stitcher.Height} 像素，请点完成";
                _timer.Stop();
                break;
        }
    }

    void End(bool keep)
    {
        if (_ended) return;
        _ended = true;
        _timer.Stop();
        _frame.Close();
        _bar.Close();
        Finished?.Invoke(keep && _stitcher.Height > 0 ? _stitcher.ToImage() : null);
    }
}
