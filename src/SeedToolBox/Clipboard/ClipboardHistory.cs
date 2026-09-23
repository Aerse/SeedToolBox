using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.Launcher;

namespace SeedToolBox.Clips;

public class ClipboardSettings
{
    public const string SettingsName = "clipboard";

    public string Hotkey { get; set; } = "Ctrl+Alt+H";
    /// <summary>Records clipboard changes; turned off from the history window.</summary>
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 300;
    /// <summary>Pastes into the previous window after choosing an entry, instead of only copying.</summary>
    public bool AutoPaste { get; set; } = true;
}

public sealed class ClipboardEntry : ObservableObject
{
    bool _pinned;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Text content, or null for an image.</summary>
    public string? Text { get; set; }
    /// <summary>PNG file name under the history folder.</summary>
    public string? Image { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    /// <summary>Content hash used to move a repeated copy to the top instead of adding it again.</summary>
    public string Hash { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    [JsonIgnore] public bool IsImage => Image != null;
}

/// <summary>
/// Records what is copied, text and images, via AddClipboardFormatListener.
/// History is kept in Data\Clipboard, which is left out of backups since it may contain passwords.
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const long MaxImageBytes = 40L * 1024 * 1024;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    public static string Folder { get; } = Path.Combine(AppPaths.Data, "Clipboard");
    static string HistoryPath => Path.Combine(Folder, "history.json");

    readonly ISettingsStore _store;
    readonly HwndSource _source;
    readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClipboardSettings Settings { get; }
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    public ClipboardHistory(ISettingsStore store)
    {
        _store = store;
        Settings = store.Load<ClipboardSettings>(ClipboardSettings.SettingsName);
        Load();

        _source = new HwndSource(new HwndSourceParameters("SeedToolBox.Clipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(_source.Handle)) Log.Error($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");

        // The owner often writes several formats in a row; read once it has finished
        _readTimer.Tick += (_, _) => { _readTimer.Stop(); ReadClipboard(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
    }

    public void SaveSettings()
    {
        try { _store.Save(ClipboardSettings.SettingsName, Settings); }
        catch (Exception ex) { Log.Error("Failed to save clipboard settings", ex); }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Settings.Enabled)
        {
            _readTimer.Stop();
            _readTimer.Start();
        }
        return IntPtr.Zero;
    }

    void ReadClipboard()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data == null) return;
            // Password managers mark secrets with these formats so history tools skip them
            var formats = data.GetFormats(false);
            if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore")) return;

            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(text) && text!.Trim().Length > 0) AddText(text);
            }
            else if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddText(string.Join("\r\n", files));
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image != null) AddImage(image);
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            // Another program is holding the clipboard, or the data is in a broken format: skip this copy
            Log.Error("Failed to read the clipboard", ex);
        }
    }

    void AddText(string text)
    {
        var hash = "t" + HashOf(System.Text.Encoding.UTF8.GetBytes(text));
        if (MoveToTop(hash)) return;
        Insert(new ClipboardEntry { Text = text, Hash = hash });
    }

    void AddImage(BitmapSource image)
    {
        if ((long)image.PixelWidth * image.PixelHeight * 4 > MaxImageBytes) return;
        var png = new PngBitmapEncoder();
        // Clipboard DIBs often carry garbage in the alpha channel; drop it
        png.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr32, null, 0)));
        using var ms = new MemoryStream();
        png.Save(ms);
        var bytes = ms.ToArray();
        var hash = "i" + HashOf(bytes);
        if (MoveToTop(hash)) return;

        var entry = new ClipboardEntry { Hash = hash, ImageWidth = image.PixelWidth, ImageHeight = image.PixelHeight };
        entry.Image = entry.Id + ".png";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, entry.Image), bytes);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save clipboard image", ex);
            return;
        }
        Insert(entry);
    }

    bool MoveToTop(string hash)
    {
        var existing = Entries.FirstOrDefault(e => e.Hash == hash);
        if (existing == null) return false;
        existing.Time = DateTime.Now;
        int index = Entries.IndexOf(existing);
        if (index > 0) Entries.Move(index, 0);
        RequestSave();
        return true;
    }

    void Insert(ClipboardEntry entry)
    {
        Entries.Insert(0, entry);
        // Pinned entries never age out
        var extra = Entries.Where(e => !e.Pinned).Skip(Math.Max(10, Settings.MaxItems)).ToList();
        foreach (var old in extra) Remove(old, save: false);
        RequestSave();
    }

    public void Remove(ClipboardEntry entry, bool save = true)
    {
        Entries.Remove(entry);
        if (entry.Image != null)
        {
            try { File.Delete(Path.Combine(Folder, entry.Image)); }
            catch (IOException) { }
        }
        if (save) RequestSave();
    }

    /// <summary>Removes everything except pinned entries.</summary>
    public void Clear()
    {
        foreach (var entry in Entries.Where(e => !e.Pinned).ToList()) Remove(entry, save: false);
        RequestSave();
    }

    public void TogglePin(ClipboardEntry entry)
    {
        entry.Pinned = !entry.Pinned;
        RequestSave();
    }

    public BitmapSource? LoadImage(ClipboardEntry entry, int decodeWidth = 0)
    {
        if (entry.Image == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.Combine(Folder, entry.Image));
            if (decodeWidth > 0 && entry.ImageWidth > decodeWidth) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts the entry back on the clipboard. It moves to the top when the change comes back to us.</summary>
    public bool Copy(ClipboardEntry entry)
    {
        if (entry.Text != null)
        {
            ScreenTools.ScreenToolService.CopyText(entry.Text);
            return true;
        }
        var image = LoadImage(entry);
        if (image == null) return false;
        ScreenTools.ScreenToolService.CopyImage(image);
        return true;
    }

    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Load()
    {
        if (!File.Exists(HistoryPath)) return;
        try
        {
            var entries = JsonConvert.DeserializeObject<List<ClipboardEntry>>(File.ReadAllText(HistoryPath)) ?? new();
            foreach (var entry in entries)
            {
                if (entry.Image != null && !File.Exists(Path.Combine(Folder, entry.Image))) continue;
                Entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read clipboard history", ex);
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = HistoryPath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Entries.ToList()), new System.Text.UTF8Encoding(false));
            if (File.Exists(HistoryPath)) File.Replace(tmp, HistoryPath, null);
            else File.Move(tmp, HistoryPath);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save clipboard history", ex);
        }
    }

    static string HashOf(byte[] bytes)
    {
        using var sha = SHA1.Create();
        return Convert.ToBase64String(sha.ComputeHash(bytes));
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
