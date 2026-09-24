using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SeedToolBox.Core.Native;
using SeedToolBox.Core.Services;
using SeedToolBox.Recording;

namespace SeedToolBox.ScreenTools;

/// <summary>Entry points for screenshot, color picker, ruler, screen recording, OCR and QR codes, plus shared copy/save/pin helpers.</summary>
public sealed partial class ScreenToolService
{
    readonly ISettingsStore _store;
    readonly DispatcherTimer _trimTimer;
    Window? _active;
    RecordingSession? _recording;

    public ScreenToolsSettings Settings { get; }

    public ScreenToolService(ISettingsStore store)
    {
        _store = store;
        Settings = store.Load<ScreenToolsSettings>(ScreenToolsSettings.SettingsName);
        // A capture holds tens of MB; give it back once the overlay is gone
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); MemoryTrimmer.Trim(); };
    }

    public void SaveSettings()
    {
        try { _store.Save(ScreenToolsSettings.SettingsName, Settings); }
        catch (Exception ex) { Log.Error("Failed to save screen tool settings", ex); }
    }

    public void Screenshot() => Open(shot => new CaptureWindow(shot, WindowFinder.Snapshot(shot), this));

    public void PickColor() => Open(shot => new ColorPickerWindow(shot, this));

    public void Ruler() => Open(shot => new RulerWindow(shot));

    /// <summary>Selects a region and records it; while recording, stops instead.</summary>
    public void Record()
    {
        if (_recording != null)
        {
            _recording.Stop();
            return;
        }
        if (!MF.IsAvailable)
        {
            MessageBox.Show("系统缺少媒体功能（Windows N 版需安装“媒体功能包”），无法录屏", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Open(shot => new CaptureWindow(shot, WindowFinder.Snapshot(shot), this, CaptureMode.Record));
    }

    /// <summary>Selects a region and recognizes its text.</summary>
    public void RecognizeText()
    {
        if (!CheckOcr()) return;
        Open(shot => new CaptureWindow(shot, WindowFinder.Snapshot(shot), this, CaptureMode.Text));
    }

    static bool CheckOcr()
    {
        if (TextRecognizer.IsAvailable) return true;
        MessageBox.Show("文字识别组件加载失败（详见日志）；在 Windows 10 及以上版本可改用系统自带识别，需安装至少一种支持 OCR 的语言（设置 → 时间和语言 → 语言）", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    public async void RecognizeText(BitmapSource image)
    {
        if (!CheckOcr()) return;
        var window = new TextResultWindow("识别文字", image);
        window.Show();
        try
        {
            var text = await TextRecognizer.RecognizeAsync(image);
            window.SetResult(text, "未识别到文字");
        }
        catch (Exception ex)
        {
            Log.Error("OCR failed", ex);
            window.SetError($"识别失败：{ex.Message}");
        }
    }

    /// <summary>Selects a region and reads the QR codes and barcodes in it.</summary>
    public void RecognizeQrCodes() => Open(shot => new CaptureWindow(shot, WindowFinder.Snapshot(shot), this, CaptureMode.QrCode));

    /// <summary>Finds QR codes and barcodes anywhere on the screen.</summary>
    public void ScanQrCodes()
    {
        ScreenShot shot;
        try { shot = ScreenShot.Capture(); }
        catch (Exception ex)
        {
            Log.Error("Screen capture failed", ex);
            MessageBox.Show($"截取屏幕失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DecodeQrCodes(shot.Image, false);
    }

    /// <param name="showImage">Show the image beside the result; off for whole-screen scans.</param>
    public async void DecodeQrCodes(BitmapSource image, bool showImage = true)
    {
        var window = new TextResultWindow("识别二维码", showImage ? image : null);
        window.Show();
        try
        {
            var codes = await System.Threading.Tasks.Task.Run(() => QrCodes.Decode(image));
            window.SetResult(string.Join("\r\n", codes), showImage ? "未找到二维码" : "屏幕上未找到二维码", codes.Where(TextResultWindow.IsLink));
        }
        catch (Exception ex)
        {
            Log.Error("QR decoding failed", ex);
            window.SetError($"识别失败：{ex.Message}");
        }
        finally
        {
            _trimTimer.Start();
        }
    }

    /// <summary>Starts with the clipboard text, if any.</summary>
    public void GenerateQrCode()
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (Exception ex) { Log.Error("Failed to read the clipboard", ex); }
        if (text.Length > 2000) text = "";
        new QrGeneratorWindow(this, text).Show();
    }

    public void RecordSettings(Window? owner = null)
    {
        if (RecordSettingsDialog.Show(Settings.Record, owner)) SaveSettings();
    }

    /// <param name="region">Screen pixels, even size.</param>
    /// <param name="scale">DPI scale of the region's monitor.</param>
    internal void StartRecording(System.Drawing.Rectangle region, double scale)
    {
        RecordingSession.CleanTemp();
        var s = Settings.Record;
        var options = new RecordOptions
        {
            Fps = s.Fps,
            Quality = s.Quality,
            SystemAudio = s.SystemAudio,
            Microphone = s.Microphone,
            ShowCursor = s.ShowCursor,
            ClickEffect = s.ClickEffect,
            Scale = scale,
        };
        try
        {
            var session = new RecordingSession(region, options);
            session.Finished += OnRecordingFinished;
            _recording = session;
            session.Start(s.Countdown);
        }
        catch (Exception ex)
        {
            _recording = null;
            Log.Error("Failed to start recording", ex);
            MessageBox.Show($"无法开始录屏：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnRecordingFinished(RecordedClip? clip, Exception? error, System.Collections.Generic.IReadOnlyList<string> audioErrors)
    {
        _recording = null;
        _trimTimer.Start();
        if (error != null)
        {
            MessageBox.Show($"录屏失败：{error.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (clip == null) return;
        var warning = audioErrors.Count > 0 ? $"未能录制{string.Join("和", audioErrors)}（设备不可用），详见日志" : null;
        var editor = new RecordEditorWindow(clip, Settings.Record, SaveSettings, warning);
        editor.Show();
        editor.Activate();
    }

    void Open(Func<ScreenShot, Window> create)
    {
        if (_active != null)
        {
            _active.Activate();
            return;
        }

        Window window;
        try
        {
            window = create(ScreenShot.Capture());
        }
        catch (Exception ex)
        {
            Log.Error("Screen capture failed", ex);
            MessageBox.Show($"截取屏幕失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _active = window;
        _trimTimer.Stop();
        window.Closed += (_, _) => { _active = null; _trimTimer.Start(); };
        window.Show();
    }

    public void Pin(BitmapSource image, int screenX, int screenY) => new PinWindow(image, screenX, screenY, this).Show();

    public static void CopyText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + " ");
        SetClipboard(CF_UNICODETEXT, bytes);
    }

    public static void CopyImage(BitmapSource image)
    {
        // CF_DIB: BITMAPINFOHEADER followed by bottom-up 32-bit pixels
        var source = image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var dib = new byte[40 + pixels.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, (height - 1 - y) * stride, dib, 40 + y * stride, stride);
        SetClipboard(CF_DIB, dib);
    }

    /// <summary>
    /// Writes one format with the raw Win32 API. WPF's Clipboard retries internally for about a second
    /// per call, which froze the UI when another app held the clipboard; here the total wait is bounded.
    /// </summary>
    static void SetClipboard(uint format, byte[] data)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                    if (memory == IntPtr.Zero) break;
                    Marshal.Copy(data, 0, GlobalLock(memory), data.Length);
                    GlobalUnlock(memory);
                    // On success the clipboard owns the memory
                    if (SetClipboardData(format, memory) != IntPtr.Zero) return;
                    GlobalFree(memory);
                    break;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(30);
        }

        Log.Error($"Clipboard unavailable (error {Marshal.GetLastWin32Error()})");
        // Deferred so an overlay being closed can't hide the message behind it
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            MessageBox.Show("剪贴板被其他程序占用，复制失败", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning)));
    }

    const uint CF_DIB = 8, CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>Asks for a file name and saves the image. Returns false if cancelled or failed.</summary>
    public bool SaveImage(BitmapSource image, Window owner, string title = "保存截图", string name = "截图")
    {
        var folder = Directory.Exists(Settings.SaveFolder) ? Settings.SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = name == "截图" ? FileName() : $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            InitialDirectory = folder,
        };
        if (dialog.ShowDialog(owner) != true) return false;

        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            BitmapEncoder encoder = ext switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            // JPEG/BMP have no alpha channel
            BitmapSource frame = encoder is PngBitmapEncoder ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save {dialog.FileName}", ex);
            MessageBox.Show(owner, $"保存失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        Settings.SaveFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        SaveSettings();
        return true;
    }
}
