using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.ScreenTools;

/// <summary>What happens to every finished screenshot: file names, auto-save and the capture history.</summary>
public sealed partial class ScreenToolService
{
    CaptureHistory? _history;

    public CaptureHistory History => _history ??= new CaptureHistory(() => Settings.HistoryCount);

    /// <summary>The file name template with each {format} replaced by the current time, e.g. 截图_{yyyyMMdd_HHmmss}.</summary>
    public string FileName(string? template = null)
    {
        template ??= Settings.FileNameTemplate;
        if (string.IsNullOrWhiteSpace(template)) template = "截图_{yyyyMMdd_HHmmss}";
        var now = DateTime.Now;
        var name = Regex.Replace(template, @"\{([^{}]+)\}", m =>
        {
            try { return now.ToString(m.Groups[1].Value); }
            catch (FormatException) { return m.Value; }
        });
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length > 0 ? name.Trim() : $"截图_{now:yyyyMMdd_HHmmss}";
    }

    public string AutoSaveFolder => Settings.AutoSaveFolder.Length > 0
        ? Settings.AutoSaveFolder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SeedToolBox");

    /// <summary>Call once per screenshot that leaves the overlay (copied, saved or pinned).</summary>
    /// <param name="region">Screen pixels, remembered for 上次区域.</param>
    internal void OnCaptured(BitmapSource image, System.Drawing.Rectangle? region)
    {
        if (region is { } r) RememberRegion(r);
        if (Settings.HistoryCount > 0) History.Add(image);
        if (Settings.AutoSave)
        {
            var folder = AutoSaveFolder;
            var name = FileName();
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, name + ".png");
                    for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).png");
                    CaptureHistory.WritePng(image, path);
                }
                catch (Exception ex)
                {
                    Log.Error($"Auto-save to {folder} failed", ex);
                }
            });
        }
    }
}

/// <summary>The last screenshots as PNG files in Data\captures, newest first.</summary>
public sealed class CaptureHistory
{
    public static string Folder { get; } = Path.Combine(AppPaths.Data, "captures");

    readonly Func<int> _limit;
    readonly object _lock = new();

    /// <summary>Raised on the UI thread after a capture is added or removed.</summary>
    public event Action? Changed;

    public CaptureHistory(Func<int> limit) => _limit = limit;

    public IReadOnlyList<string> Files()
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return Directory.GetFiles(Folder, "*.png").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to list captures", ex);
            return Array.Empty<string>();
        }
    }

    public void Add(BitmapSource image)
    {
        var dispatcher = Application.Current.Dispatcher;
        // Sortable names; the counter keeps two captures in one millisecond apart
        var name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Folder);
                    var path = Path.Combine(Folder, name + ".png");
                    for (int i = 1; File.Exists(path); i++) path = Path.Combine(Folder, $"{name}_{i}.png");
                    WritePng(image, path);
                    Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add to capture history", ex);
            }
            dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
        });
    }

    void Trim()
    {
        int limit = Math.Max(0, _limit());
        foreach (var old in Files().Skip(limit)) TryDelete(old);
    }

    public void Delete(string path)
    {
        TryDelete(path);
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var file in Files()) TryDelete(file);
        Changed?.Invoke();
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Error($"Failed to delete {path}", ex); }
    }

    public static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Loads a file fully into memory so it can be deleted while shown.</summary>
    public static BitmapSource Load(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        using (var stream = new MemoryStream(File.ReadAllBytes(path)))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }
}
