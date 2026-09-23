using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SeedToolBox.Core.Services;
using SeedToolBox.Launcher;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.Recording;

/// <summary>Preview a finished recording, trim its start and end, and export it as MP4 or GIF.</summary>
sealed class RecordEditorWindow : Window
{
    readonly RecordedClip _clip;
    readonly RecordSettings _settings;
    readonly Action _saveSettings;
    readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    readonly TrimBar _trim;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Button _play = new() { Content = "\uE768", Width = 40, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), ToolTip = "播放 / 暂停 (空格)" };
    readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MinWidth = 150 };
    readonly TextBlock _range = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = DialogWindow.HintBrush };
    readonly RadioButton _mp4 = new() { Content = "MP4 视频", GroupName = "format", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly RadioButton _gif = new() { Content = "GIF 动图", GroupName = "format", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly ComboBox _gifFps = new() { Width = 80, Margin = new Thickness(4, 0, 12, 0) };
    readonly ComboBox _gifScale = new() { Width = 80, Margin = new Thickness(4, 0, 0, 0) };
    readonly StackPanel _gifOptions = new() { Orientation = Orientation.Horizontal };
    readonly ProgressBar _progress = new() { Width = 160, Height = 4, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _openFolder = new() { Content = "打开所在文件夹", Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly Button _save = new() { Content = "导出…", MinWidth = 88, IsDefault = true };
    readonly Button _cancel = new() { Content = "取消导出", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly Panel _controls;

    bool _playing, _saved, _closeAfterExport;
    string? _lastSaved;
    CancellationTokenSource? _export;

    static readonly int[] GifRates = { 5, 10, 15, 20, 25 };
    static readonly int[] GifScales = { 100, 75, 50, 33 };

    public RecordEditorWindow(RecordedClip clip, RecordSettings settings, Action saveSettings, string? warning)
    {
        _clip = clip;
        _settings = settings;
        _saveSettings = saveSettings;
        Title = $"录屏 - {clip.Width}×{clip.Height}，{Format(clip.Duration)}";
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DialogWindow.ApplyTheme(this);

        _trim = new TrimBar(clip.Duration) { Height = 36, Margin = new Thickness(0, 8, 0, 4) };
        _trim.Seeked += SeekTo;
        _trim.RangeChanged += UpdateRange;

        foreach (var fps in GifRates) _gifFps.Items.Add($"{fps} 帧/秒");
        foreach (var scale in GifScales) _gifScale.Items.Add($"{scale}%");
        _gifFps.SelectedIndex = Math.Max(0, Array.IndexOf(GifRates, settings.GifFps));
        _gifScale.SelectedIndex = Math.Max(0, Array.IndexOf(GifScales, settings.GifScale));
        _gifOptions.Children.Add(new TextBlock { Text = "帧率", VerticalAlignment = VerticalAlignment.Center });
        _gifOptions.Children.Add(_gifFps);
        _gifOptions.Children.Add(new TextBlock { Text = "尺寸", VerticalAlignment = VerticalAlignment.Center });
        _gifOptions.Children.Add(_gifScale);
        _mp4.Checked += (_, _) => _gifOptions.IsEnabled = false;
        _gif.Checked += (_, _) => _gifOptions.IsEnabled = true;
        _mp4.IsChecked = true;

        var preview = new Border { Background = Brushes.Black, Child = _media };
        preview.MouseLeftButtonUp += (_, _) => TogglePlay();

        var playRow = new DockPanel();
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_position, Dock.Left);
        playRow.Children.Add(_play);
        playRow.Children.Add(_position);
        _range.HorizontalAlignment = HorizontalAlignment.Right;
        playRow.Children.Add(_range);

        var formatRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        formatRow.Children.Add(new TextBlock { Text = "导出格式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        formatRow.Children.Add(_mp4);
        formatRow.Children.Add(_gif);
        formatRow.Children.Add(_gifOptions);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_save);
        buttons.Children.Add(_cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Left);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_progress);
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(_status);
        statusRow.Children.Add(_openFolder);
        bottom.Children.Add(statusRow);

        var hint = new TextBlock
        {
            Text = "拖动时间轴两端的滑块裁剪首尾，点击时间轴定位预览",
            Foreground = DialogWindow.HintBrush,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var lower = new StackPanel();
        _controls = new StackPanel { Children = { hint, _trim, playRow, formatRow } };
        lower.Children.Add(_controls);
        lower.Children.Add(bottom);
        DockPanel.SetDock(lower, Dock.Bottom);
        root.Children.Add(lower);
        root.Children.Add(preview);
        Content = root;

        if (warning != null) _status.Text = warning;

        _play.Click += (_, _) => TogglePlay();
        _save.Click += (_, _) => Export();
        _cancel.Click += (_, _) => _export?.Cancel();
        _openFolder.Click += (_, _) => { if (_lastSaved != null) ProcessLauncher.OpenLocation(_lastSaved); };
        _timer.Tick += (_, _) => OnTick();
        PreviewKeyDown += OnKey;

        _media.MediaOpened += (_, _) => SeekTo(0);
        _media.MediaFailed += (_, e) =>
        {
            Log.Error("Preview failed", e.ErrorException);
            _status.Text = "无法预览（系统缺少播放组件），仍可裁剪和导出";
        };
        _media.Source = new Uri(clip.Path);
        // Manual behavior: Play/Pause loads the first frame without starting playback
        _media.Play();
        _media.Pause();

        UpdateRange();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _media.Close();
            _media.Source = null;
            RecordingSession.TryDelete(clip.Path);
        };
    }

    static string Format(long time)
    {
        var t = TimeSpan.FromTicks(Math.Max(0, time));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");
    }

    #region Preview

    void TogglePlay()
    {
        if (_export != null) return;
        if (_playing)
        {
            Pause();
            return;
        }
        // Playing from outside the kept range starts at its beginning
        if (_trim.Position < _trim.Start || _trim.Position >= _trim.End - 10_000) SeekTo(_trim.Start);
        _playing = true;
        _media.Play();
        _timer.Start();
        _play.Content = "\uE769";
    }

    void Pause()
    {
        _playing = false;
        _media.Pause();
        _timer.Stop();
        _play.Content = "\uE768";
    }

    void SeekTo(long time)
    {
        _media.Position = TimeSpan.FromTicks(time);
        _trim.Position = time;
        UpdatePosition();
    }

    void OnTick()
    {
        long now = _media.Position.Ticks;
        if (now >= _trim.End)
        {
            Pause();
            now = _trim.End;
        }
        _trim.Position = now;
        UpdatePosition();
    }

    void UpdatePosition() => _position.Text = $"{Format(_trim.Position)} / {Format(_clip.Duration)}";

    void UpdateRange()
    {
        _range.Text = $"保留 {Format(_trim.Start)} – {Format(_trim.End)}（{(_trim.End - _trim.Start) / 1e7:0.0} 秒）";
        if (_playing && (_trim.Position < _trim.Start || _trim.Position > _trim.End)) Pause();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _export == null)
        {
            TogglePlay();
            e.Handled = true;
        }
    }

    #endregion

    #region Export

    async void Export()
    {
        Pause();
        bool gif = _gif.IsChecked == true;
        int gifFps = GifRates[Math.Max(0, _gifFps.SelectedIndex)];
        int gifScale = GifScales[Math.Max(0, _gifScale.SelectedIndex)];

        var folder = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var dialog = new SaveFileDialog
        {
            Title = "导出录屏",
            FileName = $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}",
            Filter = gif ? "GIF 动图|*.gif" : "MP4 视频|*.mp4",
            DefaultExt = gif ? ".gif" : ".mp4",
            InitialDirectory = folder,
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;

        _settings.SaveFolder = Path.GetDirectoryName(path) ?? "";
        if (gif)
        {
            _settings.GifFps = gifFps;
            _settings.GifScale = gifScale;
        }
        _saveSettings();

        long start = _trim.Start, end = _trim.End;
        int quality = _settings.Quality;
        var cancel = new CancellationTokenSource();
        _export = cancel;
        SetExporting(true);
        _status.Text = "正在导出…";
        _openFolder.Visibility = Visibility.Collapsed;

        double shown = 0;
        void Report(double value)
        {
            // Throttle: the encoder reports every frame
            if (value - shown < 0.01 && value < 1) return;
            shown = value;
            Dispatcher.BeginInvoke(new Action(() => _progress.Value = value));
        }

        try
        {
            // Thread-pool threads are MTA, which Media Foundation needs
            await Task.Run(() =>
            {
                if (gif) Exporter.Gif(_clip, path, start, end, gifFps, gifScale / 100.0, Report, cancel.Token);
                else Exporter.Mp4(_clip, path, start, end, quality, Report, cancel.Token);
            });
            _saved = true;
            _lastSaved = path;
            _status.Text = $"已导出：{Path.GetFileName(path)}（{new FileInfo(path).Length / 1024.0 / 1024:0.0} MB）";
            _openFolder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            RecordingSession.TryDelete(path);
            _status.Text = "已取消导出";
        }
        catch (Exception ex)
        {
            Log.Error($"Export to {path} failed", ex);
            RecordingSession.TryDelete(path);
            _status.Text = "导出失败";
            MessageBox.Show(this, $"导出失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _export = null;
            cancel.Dispose();
            SetExporting(false);
        }
        if (_closeAfterExport) Close();
    }

    void SetExporting(bool exporting)
    {
        _controls.IsEnabled = !exporting;
        _save.IsEnabled = !exporting;
        _cancel.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = 0;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_export != null)
        {
            // Let the export unwind first so the files aren't in use
            e.Cancel = true;
            _closeAfterExport = true;
            _export.Cancel();
            return;
        }
        if (_saved || _closeAfterExport) return;
        if (MessageBox.Show(this, "录屏还没有导出，关闭后将丢弃。确定关闭？", "SeedToolBox", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            e.Cancel = true;
    }

    #endregion
}

/// <summary>Timeline with draggable start/end handles and a playhead. Times are in 100 ns units.</summary>
sealed class TrimBar : FrameworkElement
{
    static readonly Brush Track = new SolidColorBrush(Color.FromRgb(225, 225, 225));
    static readonly Brush Kept = new SolidColorBrush(Color.FromRgb(160, 205, 245));
    static readonly Brush HandleBrush = new SolidColorBrush(Color.FromRgb(30, 144, 255));
    static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(230, 40, 40)), 2);
    const double HandleWidth = 10, Min = 1_000_000; // keep at least 0.1 s

    readonly long _duration;
    long _start, _end, _position;
    enum Drag { None, Start, End, Position }
    Drag _drag;

    public event Action<long>? Seeked;
    public event Action? RangeChanged;

    public TrimBar(long duration)
    {
        _duration = Math.Max(1, duration);
        _end = _duration;
        Cursor = Cursors.Hand;
    }

    public long Start => _start;
    public long End => _end;

    public long Position
    {
        get => _position;
        set
        {
            _position = Math.Max(0, Math.Min(_duration, value));
            InvalidateVisual();
        }
    }

    double Usable => Math.Max(1, ActualWidth - 2 * HandleWidth);
    double X(long time) => HandleWidth + time * Usable / _duration;
    long TimeAt(double x) => (long)Math.Max(0, Math.Min(_duration, (x - HandleWidth) * _duration / Usable));

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, top = 6, bottom = h - 6;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        dc.DrawRoundedRectangle(Track, null, new Rect(HandleWidth, top, Usable, bottom - top), 3, 3);
        dc.DrawRectangle(Kept, null, new Rect(X(_start), top, Math.Max(0, X(_end) - X(_start)), bottom - top));
        // Handles sit outside the kept range: left of start, right of end
        dc.DrawRoundedRectangle(HandleBrush, null, new Rect(X(_start) - HandleWidth, 0, HandleWidth, h), 3, 3);
        dc.DrawRoundedRectangle(HandleBrush, null, new Rect(X(_end), 0, HandleWidth, h), 3, 3);
        double p = X(_position);
        dc.DrawLine(PlayheadPen, new Point(p, 0), new Point(p, h));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        double start = X(_start), end = X(_end);
        if (x >= start - HandleWidth - 2 && x <= start + 2) _drag = Drag.Start;
        else if (x >= end - 2 && x <= end + HandleWidth + 2) _drag = Drag.End;
        else _drag = Drag.Position;
        CaptureMouse();
        Update(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (_drag != Drag.None)
        {
            Update(x);
            return;
        }
        double start = X(_start), end = X(_end);
        bool onHandle = (x >= start - HandleWidth - 2 && x <= start + 2) || (x >= end - 2 && x <= end + HandleWidth + 2);
        Cursor = onHandle ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    void Update(double x)
    {
        switch (_drag)
        {
            case Drag.Start:
                // The start handle's right edge marks the time
                _start = Math.Min(TimeAt(x + HandleWidth / 2), _end - (long)Math.Min(Min, _duration));
                _start = Math.Max(0, _start);
                RangeChanged?.Invoke();
                Seeked?.Invoke(_start);
                break;
            case Drag.End:
                _end = Math.Max(TimeAt(x - HandleWidth / 2), _start + (long)Math.Min(Min, _duration));
                _end = Math.Min(_duration, _end);
                RangeChanged?.Invoke();
                // Show the last kept frame, not the one after it
                Seeked?.Invoke(Math.Max(_start, _end - 1));
                break;
            case Drag.Position:
                Seeked?.Invoke(TimeAt(x));
                break;
        }
        InvalidateVisual();
    }
}
