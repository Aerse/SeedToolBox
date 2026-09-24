using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Recording;

sealed class RecordOptions
{
    public int Fps { get; set; } = 30;
    public int Quality { get; set; } = 1;
    public bool SystemAudio { get; set; }
    public bool Microphone { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool ClickEffect { get; set; } = true;
    /// <summary>Draw recent key presses near the bottom of the video.</summary>
    public bool ShowKeys { get; set; }
    /// <summary>Set by the session when <see cref="ShowKeys"/> is on.</summary>
    public KeyWatcher? Keys { get; set; }
    /// <summary>Monitor scale factor, so cursor effects keep their apparent size.</summary>
    public double Scale { get; set; } = 1;
}

/// <summary>
/// Records a screen rectangle (physical pixels, even size) to an MP4 on a background thread.
/// The timeline excludes paused time; audio sources are mixed against that same clock.
/// </summary>
sealed class Recorder
{
    const long Second = 10_000_000;
    // Audio is written this far behind the clock so late packets still make it in
    const long AudioLag = Second / 10;

    readonly Rectangle _region;
    readonly RecordOptions _options;
    readonly string _path;
    readonly Stopwatch _clock = new();
    readonly object _lock = new();
    readonly TaskCompletionSource<Exception?> _done = new();
    readonly ManualResetEventSlim _go = new();
    readonly List<AudioSource> _sources = new();
    readonly List<Click> _clicks = new();
    long _pausedTicks, _pauseStart;
    volatile bool _stop;
    bool _paused;
    bool _leftDown, _rightDown;

    public Recorder(Rectangle region, RecordOptions options, string path)
    {
        _region = region;
        _options = options;
        _path = path;
    }

    /// <summary>Completes when the file is finalized; the result is the error that stopped recording, if any.</summary>
    public Task<Exception?> Completion => _done.Task;

    public string Path => _path;

    /// <summary>Names of audio sources that could not be opened.</summary>
    public List<string> AudioErrors { get; } = new();

    /// <summary>Opens the encoder and devices in the background; nothing is recorded until <see cref="Begin"/>.</summary>
    public void Prepare()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Recorder" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Begin() => _go.Set();

    public void Stop()
    {
        _stop = true;
        _go.Set();
    }

    public bool Paused
    {
        get { lock (_lock) return _paused; }
        set
        {
            lock (_lock)
            {
                if (_paused == value) return;
                _paused = value;
                if (value) _pauseStart = _clock.ElapsedTicks;
                else _pausedTicks += _clock.ElapsedTicks - _pauseStart;
            }
        }
    }

    /// <summary>Recorded time in 100 ns units, excluding pauses.</summary>
    public long Elapsed
    {
        get
        {
            lock (_lock)
            {
                long ticks = (_paused ? _pauseStart : _clock.ElapsedTicks) - _pausedTicks;
                return (long)(ticks * (double)Second / Stopwatch.Frequency);
            }
        }
    }

    void Run()
    {
        Exception? error = null;
        Mp4Writer? writer = null;
        timeBeginPeriod(1);
        try
        {
            if (_options.SystemAudio) OpenAudio(true, "系统声音");
            if (_options.Microphone) OpenAudio(false, "麦克风");

            int fps = _options.Fps;
            writer = Mp4Writer.Create(_path, _region.Width, _region.Height, fps,
                Mp4Writer.Bitrate(_region.Width, _region.Height, fps, _options.Quality), _sources.Count > 0);
            using var frame = new FrameGrabber(_region, _options);

            _go.Wait();
            _clock.Start();
            foreach (var source in _sources) source.Capture.Start();

            long period = Second / fps;
            long index = 0;
            long audioWritten = 0;
            var mix = new short[0];
            while (!_stop)
            {
                long target = index * Second / fps;
                long now;
                // Short sleeps so brief clicks between frames are still seen
                while ((now = Elapsed) < target && !_stop)
                {
                    PollButtons(now);
                    Thread.Sleep(Math.Max(1, Math.Min(5, (int)((target - now) / 10_000))));
                }
                if (_stop) break;

                PollButtons(now);
                frame.Grab(Clicks(now), now);
                writer.WriteFrame(frame.Bits, frame.Stride, target, period);
                // Skip frames the capture couldn't keep up with instead of falling behind
                index = Math.Max(index + 1, Elapsed * fps / Second);

                if (writer.HasAudio) audioWritten = PumpAudio(writer, Elapsed - AudioLag, audioWritten, ref mix);
            }
            if (writer.HasAudio) PumpAudio(writer, Elapsed, audioWritten, ref mix);
        }
        catch (Exception ex)
        {
            Log.Error("Recording failed", ex);
            error = ex;
        }
        finally
        {
            foreach (var source in _sources) source.Capture.Dispose();
            try { writer?.Finish(); }
            catch (Exception ex) { error ??= ex; Log.Error("Failed to finalize recording", ex); }
            writer?.Dispose();
            timeEndPeriod(1);
            _done.TrySetResult(error);
        }
    }

    #region Audio

    sealed class AudioSource
    {
        public AudioSource(AudioCapture capture) => Capture = capture;
        public AudioCapture Capture { get; }
        // Interleaved stereo floats waiting to be mixed
        public readonly List<float> Queue = new();
    }

    void OpenAudio(bool loopback, string name)
    {
        try
        {
            var source = new AudioSource(new AudioCapture(loopback));
            source.Capture.Data += (data, frames) =>
            {
                if (Paused) return;
                lock (source.Queue)
                {
                    source.Queue.AddRange(data);
                    // Never hold more than two seconds, whatever happens to the writer
                    int excess = source.Queue.Count - 2 * Mp4Writer.AudioRate * 2;
                    if (excess > 0) source.Queue.RemoveRange(0, excess);
                }
            };
            _sources.Add(source);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {name}", ex);
            AudioErrors.Add(name);
        }
    }

    /// <summary>Mixes and writes audio up to <paramref name="until"/>; missing data becomes silence.</summary>
    long PumpAudio(Mp4Writer writer, long until, long written, ref short[] mix)
    {
        long targetFrame = until * Mp4Writer.AudioRate / Second;
        int frames = (int)(targetFrame - written);
        if (frames < Mp4Writer.AudioRate / 100) return written;

        int count = frames * 2;
        if (mix.Length < count) mix = new short[count];
        var sum = new float[count];
        foreach (var source in _sources)
        {
            lock (source.Queue)
            {
                // A device clock running fast piles up data; drop the oldest to stay in sync
                int excess = source.Queue.Count - count - Mp4Writer.AudioRate * 2 * 3 / 10;
                if (excess > 0) source.Queue.RemoveRange(0, excess);
                int take = Math.Min(count, source.Queue.Count);
                for (int i = 0; i < take; i++) sum[i] += source.Queue[i];
                source.Queue.RemoveRange(0, take);
            }
        }
        for (int i = 0; i < count; i++)
            mix[i] = (short)(Math.Max(-1f, Math.Min(1f, sum[i])) * 32767);

        writer.WriteAudio(mix, frames, written * Second / Mp4Writer.AudioRate);
        return written + frames;
    }

    #endregion

    #region Clicks

    readonly struct Click
    {
        public Click(Point position, long time, bool right)
        {
            Position = position;
            Time = time;
            Right = right;
        }

        public Point Position { get; }
        public long Time { get; }
        public bool Right { get; }
    }

    void PollButtons(long now)
    {
        if (!_options.ClickEffect) return;
        bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        // Clicks while paused would otherwise pop up at the start of the next segment
        if (((left && !_leftDown) || (right && !_rightDown)) && !Paused)
        {
            GetCursorPos(out var p);
            _clicks.Add(new Click(new Point(p.X, p.Y), now, right && !_rightDown));
        }
        _leftDown = left;
        _rightDown = right;
    }

    IEnumerable<Click> Clicks(long now)
    {
        _clicks.RemoveAll(c => now - c.Time > FrameGrabber.ClickDuration);
        return _clicks;
    }

    /// <summary>Screen capture into a DIB section, with the cursor and click ripples drawn on top.</summary>
    sealed class FrameGrabber : IDisposable
    {
        public const long ClickDuration = Second / 2;

        readonly Rectangle _region;
        readonly RecordOptions _options;
        readonly IntPtr _dc, _bitmap, _old;
        readonly Graphics _graphics;
        IntPtr _lastCursor;
        Point _hotspot;

        public IntPtr Bits { get; }
        public int Stride { get; }

        public FrameGrabber(Rectangle region, RecordOptions options)
        {
            _region = region;
            _options = options;
            var screen = GetDC(IntPtr.Zero);
            _dc = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);

            // Negative height: top-down rows, matching what the encoder expects
            var info = new BITMAPINFOHEADER { Size = 40, Width = region.Width, Height = -region.Height, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Bits = bits;
            Stride = region.Width * 4;
            _old = SelectObject(_dc, _bitmap);
            _graphics = Graphics.FromHdc(_dc);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        }

        public void Grab(IEnumerable<Click> clicks, long now)
        {
            var screen = GetDC(IntPtr.Zero);
            try
            {
                BitBlt(_dc, 0, 0, _region.Width, _region.Height, screen, _region.X, _region.Y, SRCCOPY);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            if (_options.ClickEffect) DrawClicks(clicks, now);
            if (_options.Keys?.Current is { } keys) DrawKeys(keys);
            if (_options.ShowCursor) DrawCursor();
            GdiFlush();
        }

        Font? _keyFont;

        void DrawKeys(string text)
        {
            float size = (float)(18 * _options.Scale);
            _keyFont ??= new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = _graphics.MeasureString(text, _keyFont);
            float padX = size * 0.7f, padY = size * 0.35f;
            float w = measured.Width + padX * 2, h = measured.Height + padY * 2;
            float x = (_region.Width - w) / 2, y = _region.Height - h - size * 1.5f;
            if (y < 0) y = 0;
            using (var path = RoundedRect(x, y, w, h, size * 0.4f))
            using (var fill = new SolidBrush(Color.FromArgb(200, 30, 30, 30)))
                _graphics.FillPath(fill, path);
            _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            _graphics.DrawString(text, _keyFont, Brushes.White, x + padX, y + padY);
            _graphics.Flush();
        }

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        void DrawClicks(IEnumerable<Click> clicks, long now)
        {
            double scale = _options.Scale;
            foreach (var click in clicks)
            {
                double t = Math.Min(1, (now - click.Time) / (double)ClickDuration);
                float radius = (float)((6 + 22 * t) * scale);
                int alpha = (int)(220 * (1 - t));
                var color = click.Right ? Color.FromArgb(alpha, 0, 150, 255) : Color.FromArgb(alpha, 255, 190, 0);
                float x = click.Position.X - _region.X, y = click.Position.Y - _region.Y;
                using (var fill = new SolidBrush(Color.FromArgb(alpha / 3, color)))
                    _graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
                using (var pen = new Pen(color, (float)(2.5 * scale)))
                    _graphics.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
            }
            _graphics.Flush();
        }

        void DrawCursor()
        {
            var info = new CURSORINFO { Size = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero) return;
            if (info.Cursor != _lastCursor)
            {
                _lastCursor = info.Cursor;
                _hotspot = Point.Empty;
                if (GetIconInfo(info.Cursor, out var icon))
                {
                    _hotspot = new Point(icon.XHotspot, icon.YHotspot);
                    if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                    if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
                }
            }
            DrawIconEx(_dc, info.X - _region.X - _hotspot.X, info.Y - _region.Y - _hotspot.Y, info.Cursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _keyFont?.Dispose();
            SelectObject(_dc, _old);
            DeleteObject(_bitmap);
            DeleteDC(_dc);
        }
    }

    #endregion

    #region Native

    const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool IsIcon;
        public int XHotspot, YHotspot;
        public IntPtr Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, int usage, out IntPtr bits, IntPtr section, int offset);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int period);
    [DllImport("winmm.dll")] static extern int timeEndPeriod(int period);

    #endregion
}
