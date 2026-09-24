using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    public string Hotkey { get; set; } = "Alt+Shift+V";
    /// <summary>Records clipboard changes; false while recording is paused.</summary>
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 300;
    /// <summary>Pastes into the previous window after choosing an entry, instead of only copying.</summary>
    public bool AutoPaste { get; set; } = true;
    /// <summary>Unpinned entries older than this many days are deleted; 0 keeps them forever.</summary>
    public int ExpireDays { get; set; }
    /// <summary>Process names whose copies are never recorded, separated by commas or new lines.</summary>
    public string IgnoredProcesses { get; set; } = "KeePass.exe, KeePassXC.exe, 1Password.exe, Bitwarden.exe";
}

public sealed class ClipboardEntry : ObservableObject
{
    bool _pinned;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Text content (for files, the paths one per line), or null for an image.</summary>
    public string? Text { get; set; }
    /// <summary>PNG file name under the history folder.</summary>
    public string? Image { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    /// <summary>Copied files, put back as a file drop list.</summary>
    public List<string>? Files { get; set; }
    /// <summary>CF_HTML kept alongside Text.</summary>
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>Content hash used to move a repeated copy to the top instead of adding it again.</summary>
    public string Hash { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    [JsonIgnore] public bool IsImage => Image != null;
    [JsonIgnore] public bool IsFiles => Files is { Count: > 0 };
    [JsonIgnore] public bool IsRich => Html != null || Rtf != null;

    public bool ShouldSerializeTags() => Tags.Count > 0;
}

/// <summary>
/// Records what is copied (text, rich text, files and images) via AddClipboardFormatListener.
/// History is kept in Data\Clipboard, which is left out of backups since it may contain passwords.
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const long MaxImageBytes = 40L * 1024 * 1024;
    const int MaxRichChars = 4 * 1024 * 1024;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    public static string Folder { get; } = Path.Combine(AppPaths.Data, "Clipboard");
    static string HistoryPath => Path.Combine(Folder, "history.json");

    readonly ISettingsStore _store;
    readonly HwndSource _source;
    readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _expireTimer = new() { Interval = TimeSpan.FromHours(1) };
    bool _lastEnabled;

    public ClipboardSettings Settings { get; }
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    /// <summary>Raised when recording is paused or resumed, wherever the setting was changed.</summary>
    public event Action? RecordingChanged;
    /// <summary>Raised when an entry's text or tags change without the collection changing.</summary>
    public event Action? EntryChanged;

    public ClipboardHistory(ISettingsStore store)
    {
        _store = store;
        Settings = store.Load<ClipboardSettings>(ClipboardSettings.SettingsName);
        _lastEnabled = Settings.Enabled;
        Load();
        Expire();

        _source = new HwndSource(new HwndSourceParameters("SeedToolBox.Clipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(_source.Handle)) Log.Error($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");

        // The owner often writes several formats in a row; read once it has finished
        _readTimer.Tick += (_, _) => { _readTimer.Stop(); ReadClipboard(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _expireTimer.Tick += (_, _) => Expire();
        _expireTimer.Start();
    }

    /// <summary>False while recording is paused.</summary>
    public bool Recording
    {
        get => Settings.Enabled;
        set
        {
            Settings.Enabled = value;
            SaveSettings();
        }
    }

    public void SaveSettings()
    {
        try { _store.Save(ClipboardSettings.SettingsName, Settings); }
        catch (Exception ex) { Log.Error("Failed to save clipboard settings", ex); }
        if (_lastEnabled != Settings.Enabled)
        {
            _lastEnabled = Settings.Enabled;
            RecordingChanged?.Invoke();
        }
        Expire();
    }

    /// <summary>Deletes unpinned entries older than the ExpireDays setting.</summary>
    public void Expire()
    {
        if (Settings.ExpireDays <= 0) return;
        var limit = DateTime.Now.AddDays(-Settings.ExpireDays);
        var old = Entries.Where(e => !e.Pinned && e.Time < limit).ToList();
        if (old.Count == 0) return;
        foreach (var entry in old) Remove(entry, save: false);
        RequestSave();
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
            if (formats.Contains("CanIncludeInClipboardHistory") && IsZeroDword(data.GetData("CanIncludeInClipboardHistory"))) return;
            if (IsOwnerIgnored()) return;

            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddFiles(files);
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(text) && text!.Trim().Length > 0)
                    AddText(text, ReadRich(data, formats, DataFormats.Html), ReadRich(data, formats, DataFormats.Rtf));
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

    static string? ReadRich(IDataObject data, string[] formats, string format)
    {
        if (!formats.Contains(format)) return null;
        try { return data.GetData(format) is string { Length: > 0 and <= MaxRichChars } s ? s : null; }
        catch (Exception ex) when (ex is COMException or ExternalException) { return null; }
    }

    static bool IsZeroDword(object? value) => value switch
    {
        MemoryStream ms => ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0,
        byte[] b => b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0,
        int i => i == 0,
        _ => false,
    };

    /// <summary>True when the program that put the data on the clipboard is on the ignore list.</summary>
    bool IsOwnerIgnored()
    {
        var names = Settings.IgnoredProcesses.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return false;
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, name + ".exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    static string TextHash(string text) => "t" + HashOf(Encoding.UTF8.GetBytes(text));

    void AddText(string text, string? html, string? rtf)
    {
        var hash = TextHash(text);
        var existing = Entries.FirstOrDefault(e => e.Hash == hash);
        if (existing != null)
        {
            // The same text copied again from a rich source picks up its formatting
            if (html != null) existing.Html = html;
            if (rtf != null) existing.Rtf = rtf;
        }
        if (MoveToTop(hash)) return;
        Insert(new ClipboardEntry { Text = text, Hash = hash, Html = html, Rtf = rtf });
    }

    void AddFiles(string[] files)
    {
        var text = string.Join("\r\n", files);
        var hash = "f" + HashOf(Encoding.UTF8.GetBytes(text));
        if (MoveToTop(hash)) return;
        Insert(new ClipboardEntry { Text = text, Files = files.ToList(), Hash = hash });
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

    /// <summary>Entry just put back on the clipboard by <see cref="Copy"/>; it keeps its place when the change comes back.</summary>
    string? _copiedHash;

    bool MoveToTop(string hash)
    {
        if (hash == _copiedHash)
        {
            _copiedHash = null;
            if (Entries.Any(e => e.Hash == hash)) return true;
        }
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

    /// <summary>Replaces the text of an entry; its rich formats and file list no longer match and are dropped.</summary>
    public void UpdateText(ClipboardEntry entry, string text)
    {
        if (entry.IsImage || text == entry.Text) return;
        entry.Text = text;
        entry.Html = entry.Rtf = null;
        entry.Files = null;
        entry.Hash = TextHash(text);
        // Fold in an entry that already has this text
        foreach (var dup in Entries.Where(e => e != entry && e.Hash == entry.Hash).ToList())
        {
            entry.Pinned |= dup.Pinned;
            foreach (var tag in dup.Tags) if (!entry.Tags.Contains(tag)) entry.Tags.Add(tag);
            Remove(dup, save: false);
        }
        RequestSave();
        EntryChanged?.Invoke();
    }

    public void SetTags(ClipboardEntry entry, IEnumerable<string> tags)
    {
        entry.Tags = tags.Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RequestSave();
        EntryChanged?.Invoke();
    }

    public IReadOnlyList<string> AllTags() =>
        Entries.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCulture).ToList();

    /// <summary>Writes the text entries to a .json file, or as plain text for any other extension. Returns the count.</summary>
    public int Export(string path)
    {
        var entries = Entries.Where(e => e.Text != null).ToList();
        string content;
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            content = JsonConvert.SerializeObject(entries.Select(e => new
            {
                e.Time,
                Kind = e.IsFiles ? "files" : "text",
                e.Text,
                e.Files,
                e.Pinned,
                Tags = e.Tags.Count > 0 ? e.Tags : null,
            }), new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore });
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                sb.Append("===== ").Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                if (e.Pinned) sb.Append(" [置顶]");
                if (e.Tags.Count > 0) sb.Append(" #").Append(string.Join(" #", e.Tags));
                sb.Append(" =====\r\n").Append(e.Text).Append("\r\n\r\n");
            }
            content = sb.ToString();
        }
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return entries.Count;
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

    /// <summary>
    /// Puts the entry back on the clipboard with all its formats, or only its text when plainText is set.
    /// It keeps its place in the list.
    /// </summary>
    public bool Copy(ClipboardEntry entry, bool plainText = false)
    {
        _copiedHash = entry.Hash;
        if (entry.IsFiles && !plainText)
        {
            var list = new StringCollection();
            list.AddRange(entry.Files!.ToArray());
            var data = new DataObject();
            data.SetFileDropList(list);
            data.SetData(DataFormats.UnicodeText, entry.Text ?? string.Join("\r\n", entry.Files));
            if (SetDataObject(data)) return true;
        }
        if (entry.Text != null)
        {
            if (entry.IsRich && !plainText)
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, entry.Text);
                if (entry.Html != null) data.SetData(DataFormats.Html, entry.Html);
                if (entry.Rtf != null) data.SetData(DataFormats.Rtf, entry.Rtf);
                if (SetDataObject(data)) return true;
            }
            ScreenTools.ScreenToolService.CopyText(entry.Text);
            return true;
        }
        if (plainText) return false;
        var image = LoadImage(entry);
        if (image == null) return false;
        ScreenTools.ScreenToolService.CopyImage(image);
        return true;
    }

    /// <summary>Copies the text of several entries joined by new lines; images are skipped.</summary>
    public bool CopyMerged(IEnumerable<ClipboardEntry> entries)
    {
        var texts = entries.Where(e => e.Text != null).Select(e => e.Text!.TrimEnd('\r', '\n')).ToList();
        if (texts.Count == 0) return false;
        ScreenTools.ScreenToolService.CopyText(string.Join("\r\n", texts));
        return true;
    }

    static bool SetDataObject(DataObject data)
    {
        // Bounded retries: another program may briefly hold the clipboard open
        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
        return false;
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
                entry.Tags ??= new();
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
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Entries.ToList()), new UTF8Encoding(false));
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
        _expireTimer.Stop();
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
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
