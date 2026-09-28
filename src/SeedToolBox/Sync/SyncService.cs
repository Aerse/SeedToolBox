using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Clips;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.Notes;

namespace SeedToolBox.Sync;

public class SyncSettings
{
    public const string SettingsName = "sync";

    public bool Enabled { get; set; }
    public string Url { get; set; } = "https://dav.jianguoyun.com/dav/";
    /// <summary>The user agreed to send the account over plain http to a server outside the local network.</summary>
    public bool AllowHttp { get; set; }
    public string User { get; set; } = "";
    /// <summary>WebDAV password, protected with DPAPI.</summary>
    public string Password { get; set; } = "";
    /// <summary>Folder under the WebDAV root that holds this app's files.</summary>
    public string Folder { get; set; } = "SeedToolBox";
    /// <summary>Encryption password, protected with DPAPI; the same on every computer.</summary>
    public string Passphrase { get; set; } = "";
    public bool Notes { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool ClipboardImages { get; set; }
    public bool Launcher { get; set; } = true;
    public bool Settings { get; set; } = true;
    /// <summary>Minutes between automatic syncs; 0 syncs only when asked.</summary>
    public int IntervalMinutes { get; set; } = 10;
    public DateTime? LastSync { get; set; }
}

/// <summary>What this computer knew after the last sync, to tell its own changes from the other computers'.</summary>
sealed class SyncState
{
    public HashSet<string> KnownNotes { get; set; } = new();
    public HashSet<string> KnownClips { get; set; } = new();
    /// <summary>Settings file name → hash of the local content after the last sync.</summary>
    public Dictionary<string, string> SettingsLocal { get; set; } = new();
    /// <summary>Settings file name → hash of the server copy after the last sync.</summary>
    public Dictionary<string, string> SettingsRemote { get; set; } = new();
    /// <summary>Content hashes of clipboard images already on the server.</summary>
    public HashSet<string> UploadedImages { get; set; } = new();
    /// <summary>Server file → the highest revision seen there, so an older copy put back on the server is noticed.</summary>
    public Dictionary<string, long> Revisions { get; set; } = new();
    /// <summary>Settings file name → hash of the local content when the server's copy was downloaded for the next start.</summary>
    public Dictionary<string, string> PendingOver { get; set; } = new();
}

/// <summary>A document as downloaded, with what's needed to upload it back without overwriting someone else's copy.</summary>
sealed class Remote<T> where T : class
{
    public T? Value;
    public DavFile File = new(null, null);
    public long Revision;
    /// <summary>Written by an older version; rewritten in the current format.</summary>
    public bool Legacy;
}

sealed class ListDoc<T>
{
    public List<T> Items { get; set; } = new();
    /// <summary>Id → when it was deleted, so a deletion reaches the other computers instead of the item coming back.</summary>
    public Dictionary<string, DateTime> Deleted { get; set; } = new();
}

sealed class SettingsFile
{
    public string Json { get; set; } = "";
    public DateTime Updated { get; set; }
}

/// <summary>
/// Syncs notes, clipboard history, launcher items and settings through a WebDAV folder, everything encrypted
/// with the sync password. Notes and clipboard entries merge item by item; a settings file is taken whole from
/// whichever computer changed it last and applied on the next start, since the settings are live objects.
/// </summary>
sealed class SyncService
{
    const string PendingFolderName = "pending";
    static readonly JsonSerializerSettings Json = new() { ObjectCreationHandling = ObjectCreationHandling.Replace };
    /// <summary>Data files synced as settings, excluding the launcher which has its own switch. AI keys live in PiAgent and never leave the computer.</summary>
    static readonly string[] SettingsFiles = { "clipboard", "screentools", "module-hotkeys", "ai", "terminal" };
    const string LauncherFile = "launcher";
    const long MaxImageBytes = 8L * 1024 * 1024;

    static string Folder => Path.Combine(AppPaths.Data, "Sync");
    static string PendingFolder => Path.Combine(Folder, PendingFolderName);
    /// <summary>Settings that lost to a newer copy, kept in case the other one was wanted.</summary>
    static string ConflictFolder => Path.Combine(Folder, "conflicts");
    static readonly Regex SafeId = new("^[A-Za-z0-9-]{1,64}$");
    static readonly Regex SafeHash = new("^[A-Za-z0-9+/=]{1,64}$");
    /// <summary>Pending settings that <see cref="ApplyPending"/> left alone because they were changed here meanwhile.</summary>
    static readonly List<string> KeptLocal = new();

    readonly ISettingsStore _store;
    readonly JsonSettingsStore _stateStore = new(Folder);
    readonly NoteStore _notes;
    readonly ClipboardHistory _clipboard;
    readonly Action _flushLauncher;
    readonly DispatcherTimer _interval = new();
    readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromSeconds(20) };
    bool _running, _applying;
    SyncCrypto? _crypto;
    string _cryptoFor = "";
    /// <summary>Clipboard entries dropped by the size limit or expiry since the last sync: not deletions to pass on.</summary>
    readonly HashSet<string> _trimmed = new();
    int _conflicts;

    public SyncSettings Settings { get; }
    public string LastResult { get; private set; } = "";
    public bool LastFailed { get; private set; }
    /// <summary>Settings downloaded and waiting for a restart.</summary>
    public bool RestartNeeded { get; private set; }
    public bool Running => _running;

    /// <summary>A sync started or finished.</summary>
    public event Action? StateChanged;

    public SyncService(ISettingsStore store, NoteStore notes, ClipboardHistory clipboard, Action flushLauncher)
    {
        _store = store;
        _notes = notes;
        _clipboard = clipboard;
        _flushLauncher = flushLauncher;
        Settings = store.Load<SyncSettings>(SyncSettings.SettingsName);
        RestartNeeded = Directory.Exists(PendingFolder) && Directory.EnumerateFiles(PendingFolder).Any();

        _interval.Tick += (_, _) => _ = SyncAsync();
        _soon.Tick += (_, _) => { _soon.Stop(); _ = SyncAsync(); };
        notes.Changed += SyncSoon;
        clipboard.Entries.CollectionChanged += (_, e) => { NoteTrimmed(e); SyncSoon(); };
        clipboard.EntryChanged += SyncSoon;
        Reschedule();
        if (KeptLocal.Count > 0)
            LastResult = $"云端下载的设置（{string.Join("、", KeptLocal)}）在本机又改过，保留了本机的改动，下次同步时上传；下载的那份存在 Data\\Sync\\conflicts";
        if (Ready) SyncSoon();
    }

    /// <summary>
    /// Entries removed because the history is full or expired stay on the other computers, which may keep more;
    /// only entries the user deleted are passed on as deletions.
    /// </summary>
    void NoteTrimmed(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Remove || e.OldItems == null) return;
        var settings = _clipboard.Settings;
        int unpinned = _clipboard.Entries.Count(x => !x.Pinned);
        var expired = settings.ExpireDays > 0 ? DateTime.Now.AddDays(-settings.ExpireDays) : DateTime.MinValue;
        var trimmed = e.OldItems.OfType<ClipboardEntry>()
            .Where(x => !x.Pinned && (unpinned >= Math.Max(10, settings.MaxItems) || x.Time < expired)).Select(x => x.Id).ToList();
        if (trimmed.Count == 0) return;
        _trimmed.UnionWith(trimmed);
        if (_running) return; // the sync takes them out of its state when it saves
        // Right away, in case the program exits before the next sync
        try
        {
            var state = _stateStore.Load<SyncState>("state");
            state.KnownClips.ExceptWith(_trimmed);
            _stateStore.Save("state", state);
            _trimmed.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Failed to save sync state", ex);
        }
    }

    /// <summary>Why the server address can't be used, or null.</summary>
    public static string? UrlProblem(string url, bool allowHttp)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return "服务器地址要以 https:// 开头，例如 https://dav.jianguoyun.com/dav/";
        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp && !IsLocal(uri))
            return "http:// 地址会明文发送账号密码，请改用 https://（或在设置里重新填写地址并确认使用 http）";
        return null;
    }

    /// <summary>A server on this computer or the local network, where plain http is an accepted risk.</summary>
    public static bool IsLocal(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (IPAddress.TryParse(uri.DnsSafeHost, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }
        var host = uri.DnsSafeHost;
        return !host.Contains(".") || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    public bool Ready => Settings.Enabled && Settings.Url.Trim().Length > 0 && Settings.User.Trim().Length > 0 && Settings.Passphrase.Length > 0;

    public void Save()
    {
        _store.Save(SyncSettings.SettingsName, Settings);
        Reschedule();
    }

    void Reschedule()
    {
        _interval.Stop();
        if (Settings.IntervalMinutes <= 0) return;
        _interval.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.IntervalMinutes));
        _interval.Start();
    }

    /// <summary>Syncs a little after local changes settle.</summary>
    void SyncSoon()
    {
        if (_applying || !Ready || Settings.IntervalMinutes <= 0) return;
        _soon.Stop();
        _soon.Start();
    }

    /// <summary>Runs one sync; returns false and sets <see cref="LastResult"/> on failure.</summary>
    public async Task<bool> SyncAsync(bool manual = false)
    {
        if (_running) return false;
        if (!Ready)
        {
            if (manual) Finish("请先开启同步，并填写地址、账号和同步密码", true);
            return false;
        }
        _running = true;
        _soon.Stop();
        StateChanged?.Invoke();
        try
        {
            var passphrase = SyncCrypto.Unprotect(Settings.Passphrase);
            if (passphrase.Length == 0) throw new SyncException("同步密码无法读取（换了 Windows 账号？），请重新填写");
            if (_crypto == null || _cryptoFor != passphrase) { _crypto = new SyncCrypto(passphrase); _cryptoFor = passphrase; }
            if (UrlProblem(Settings.Url, Settings.AllowHttp) is { } problem) throw new SyncException(problem);
            var root = Settings.Url.Trim().TrimEnd('/') + "/" + Settings.Folder.Trim().Trim('/');
            using var dav = new WebDavClient(root, Settings.User.Trim(), SyncCrypto.Unprotect(Settings.Password));
            await dav.EnsureFolderAsync();
            var state = _stateStore.Load<SyncState>("state");
            var done = new List<string>();
            _conflicts = 0;

            if (Settings.Notes && await Retry(() => SyncNotes(dav, state))) done.Add("笔记");
            if (Settings.Clipboard && await Retry(() => SyncClipboard(dav, state))) done.Add("剪贴板");
            await Retry(() => SyncSettingsFiles(dav, state));
            state.KnownClips.ExceptWith(_trimmed);
            _stateStore.Save("state", state);
            _trimmed.Clear();

            Settings.LastSync = DateTime.Now;
            _store.Save(SyncSettings.SettingsName, Settings);
            RestartNeeded = Directory.Exists(PendingFolder) && Directory.EnumerateFiles(PendingFolder).Any();
            Finish($"已同步 {DateTime.Now:HH:mm}" + (done.Count > 0 ? $"，更新了{string.Join("、", done)}" : "")
                + (RestartNeeded ? "；云端的设置已下载，重启程序后生效" : "")
                + (_conflicts > 0 ? $"；{_conflicts} 个设置两台电脑都改过，没采用的一份存在 Data\\Sync\\conflicts" : ""), false);
            return true;
        }
        catch (Exception ex) when (ex is SyncException or IOException or System.Net.Http.HttpRequestException or TaskCanceledException or JsonException or CryptographicException
            or UnauthorizedAccessException or UriFormatException or InvalidOperationException or ArgumentException)
        {
            if (ex is not SyncException) Log.Error("Sync failed", ex);
            Finish("同步失败：" + Describe(ex), true);
            return false;
        }
        finally
        {
            _running = false;
            StateChanged?.Invoke();
        }
    }

    void Finish(string result, bool failed)
    {
        LastResult = result;
        LastFailed = failed;
        StateChanged?.Invoke();
    }

    static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "连接超时",
        UriFormatException or InvalidOperationException or ArgumentException => "服务器地址不对：" + ex.Message,
        System.Net.Http.HttpRequestException { InnerException: System.Net.WebException web } => web.Message,
        _ => ex.Message,
    };

    // —— Encrypted JSON documents on the server

    async Task<Remote<T>> Download<T>(WebDavClient dav, SyncState state, string name) where T : class
    {
        var remote = new Remote<T> { File = await dav.GetFileAsync(name) };
        if (remote.File.Data == null) return remote;
        var plain = _crypto!.Decrypt(remote.File.Data, name, out remote.Revision, out remote.Legacy);
        state.Revisions.TryGetValue(name, out var seen);
        if (remote.Revision < seen)
            throw new SyncException($"云端的 {name} 比上次同步时还旧，可能被换回了旧版本；确认无误的话换个云端文件夹重新同步");
        state.Revisions[name] = Math.Max(seen, remote.Revision);
        remote.Value = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plain), Json);
        return remote;
    }

    /// <summary>Uploads unless another computer changed the file since it was downloaded; false means merge again.</summary>
    async Task<bool> Upload<T>(WebDavClient dav, SyncState state, string name, Remote<T> read, T value) where T : class
    {
        state.Revisions.TryGetValue(name, out var seen);
        var revision = Math.Max(seen, read.Revision) + 1;
        var data = _crypto!.Encrypt(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Json)), name, revision);
        if (!await dav.PutIfUnchangedAsync(name, data, read.File)) return false;
        state.Revisions[name] = revision;
        return true;
    }

    /// <summary>Runs a download-merge-upload again while other computers upload in between; returns whether anything changed here.</summary>
    static async Task<bool> Retry(Func<Task<(bool Changed, bool Saved)>> run)
    {
        bool changed = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (c, saved) = await run();
            changed |= c;
            if (saved) return changed;
        }
        throw new SyncException("其他电脑也在同步，云端文件一直在变，稍后再试");
    }

    static string Serialize(object? value) => value == null ? "" : JsonConvert.SerializeObject(value, Json);

    /// <summary>
    /// Merges the local list with the server's: the newer copy of an item wins, and items deleted on one side since
    /// the last sync (known then, missing now) are deleted on the other.
    /// </summary>
    static (List<T> Add, List<(T Local, T Remote)> Update, List<T> Remove, Dictionary<string, DateTime> Deleted) Merge<T>(
        IReadOnlyList<T> local, ListDoc<T>? remote, HashSet<string> known, Func<T, string> id, Func<T, DateTime> time)
    {
        var now = DateTime.Now;
        var deleted = new Dictionary<string, DateTime>(remote?.Deleted ?? new());
        var localById = new Dictionary<string, T>();
        foreach (var item in local) localById[id(item)] = item;
        foreach (var gone in known.Where(k => !localById.ContainsKey(k))) deleted[gone] = now;

        var add = new List<T>();
        var update = new List<(T, T)>();
        foreach (var item in remote?.Items ?? new())
        {
            var key = id(item);
            if (deleted.TryGetValue(key, out var at) && at >= time(item)) continue;
            if (!localById.TryGetValue(key, out var mine)) add.Add(item);
            else if (time(item) > time(mine)) update.Add((mine, item));
        }
        var remove = local.Where(i => deleted.TryGetValue(id(i), out var at) && at >= time(i)).ToList();
        // Keep deletions long enough for computers that sync rarely
        foreach (var old in deleted.Where(d => d.Value < now.AddDays(-90)).Select(d => d.Key).ToList()) deleted.Remove(old);
        return (add, update, remove, deleted);
    }

    // —— Notes

    async Task<(bool Changed, bool Saved)> SyncNotes(WebDavClient dav, SyncState state)
    {
        const string name = "notes.bin";
        _notes.Flush();
        var read = await Download<ListDoc<Note>>(dav, state, name);
        var remote = read.Value;
        // Taken before merging: added notes are the downloaded objects and get changed
        var remoteText = Serialize(remote);
        var (add, update, remove, deleted) = Merge(_notes.Notes, remote, state.KnownNotes, n => n.Id, n => n.Updated);
        bool changed = add.Count + update.Count + remove.Count > 0;
        if (changed)
        {
            _applying = true;
            try
            {
                foreach (var note in remove) _notes.Data.Notes.Remove(note);
                foreach (var (mine, theirs) in update)
                {
                    mine.Text = theirs.Text;
                    mine.Color = theirs.Color;
                    mine.Updated = theirs.Updated;
                }
                foreach (var note in add)
                {
                    // Where a note sits on the desktop belongs to the computer it was placed on
                    note.Open = note.Topmost = false;
                    note.Left = note.Top = double.NaN;
                    _notes.Data.Notes.Add(note);
                }
                _notes.RequestSave();
                _notes.Flush();
            }
            finally { _applying = false; }
        }

        var doc = new ListDoc<Note>
        {
            Items = _notes.Notes.Select(n => new Note { Id = n.Id, Text = n.Text, Color = n.Color, Created = n.Created, Updated = n.Updated, Left = double.NaN, Top = double.NaN }).ToList(),
            Deleted = deleted,
        };
        if ((read.Legacy || Serialize(doc) != remoteText) && !await Upload(dav, state, name, read, doc)) return (changed, false);
        state.KnownNotes = new HashSet<string>(_notes.Notes.Select(n => n.Id));
        return (changed, true);
    }

    // —— Clipboard

    async Task<(bool Changed, bool Saved)> SyncClipboard(WebDavClient dav, SyncState state)
    {
        const string name = "clipboard.bin";
        bool images = Settings.ClipboardImages;
        var local = _clipboard.Entries.Where(e => images || !e.IsImage).ToList();
        var read = await Download<ListDoc<ClipboardEntry>>(dav, state, name);
        var remote = read.Value;
        var remoteText = Serialize(remote);
        // Keep the server's image entries as they are while this computer doesn't sync images
        var serverImages = images ? new List<ClipboardEntry>() : remote?.Items.Where(e => e.Image != null).ToList() ?? new();
        if (remote != null && !images) remote.Items.RemoveAll(e => e.Image != null);
        // Images not synced stay out of the known list, so they don't look deleted once image sync is turned on
        var known = new HashSet<string>(state.KnownClips.Where(id => !_trimmed.Contains(id) && !_clipboard.Entries.Any(e => e.Id == id && e.IsImage && !images)));
        var (add, update, remove, deleted) = Merge(local, remote, known, e => e.Id, e => e.Time);
        // Ids and hashes become file and server paths
        add.RemoveAll(e => e.Id == null || !SafeId.IsMatch(e.Id) || e.Hash == null || !SafeHash.IsMatch(e.Hash));
        // Older than what this computer keeps: leave them on the server for computers that keep more,
        // instead of adding them only to trim them again
        int keep = Math.Max(10, _clipboard.Settings.MaxItems);
        var newest = local.Concat(add).Where(e => !e.Pinned).OrderByDescending(e => e.Time).Take(keep).ToList();
        var beyond = add.Where(e => !e.Pinned && newest.Count >= keep && e.Time < newest[newest.Count - 1].Time).ToList();
        add.RemoveAll(beyond.Contains);

        // Fetch pictures first, so an entry is only added once its file is here
        var fetched = new List<ClipboardEntry>();
        foreach (var entry in add)
        {
            if (entry.Image == null) { fetched.Add(entry); continue; }
            var data = await dav.GetAsync(ImagePath(entry.Hash));
            if (data == null) continue;
            Directory.CreateDirectory(ClipboardHistory.Folder);
            entry.Image = entry.Id + ".png";
            File.WriteAllBytes(Path.Combine(ClipboardHistory.Folder, entry.Image), _crypto!.Decrypt(data, ImagePath(entry.Hash)));
            state.UploadedImages.Add(entry.Hash);
            fetched.Add(entry);
        }

        bool changed = fetched.Count + update.Count + remove.Count > 0;
        if (changed)
        {
            _applying = true;
            try
            {
                foreach (var entry in remove)
                {
                    _clipboard.Remove(entry, save: false);
                    if (entry.IsImage) { await dav.DeleteAsync(ImagePath(entry.Hash)); state.UploadedImages.Remove(entry.Hash); }
                }
                foreach (var (mine, theirs) in update)
                {
                    if (mine.Hash != theirs.Hash && !mine.IsImage)
                    {
                        mine.Text = theirs.Text;
                        mine.Files = theirs.Files;
                        mine.Html = mine.Rtf = null;
                        mine.Hash = theirs.Hash;
                    }
                    mine.Pinned = theirs.Pinned;
                    mine.Tags = theirs.Tags ?? new();
                    mine.Time = theirs.Time;
                }
                foreach (var entry in fetched)
                {
                    entry.Tags ??= new();
                    entry.Html = entry.Rtf = null;
                    // The same content copied on two computers: keep one entry
                    if (_clipboard.Entries.FirstOrDefault(e => e.Hash == entry.Hash) is { } same)
                    {
                        if (same.Time >= entry.Time) { deleted[entry.Id] = DateTime.Now; DeleteImageFile(entry); continue; }
                        deleted[same.Id] = DateTime.Now;
                        entry.Pinned |= same.Pinned;
                        _clipboard.Remove(same, save: false);
                    }
                    _clipboard.Entries.Add(entry);
                }
                _clipboard.SortAndTrim();
            }
            finally { _applying = false; }
        }

        var synced = _clipboard.Entries.Where(e => images || !e.IsImage).ToList();
        if (images)
        {
            foreach (var entry in synced.Where(e => e.IsImage && !state.UploadedImages.Contains(e.Hash)).ToList())
            {
                var path = Path.Combine(ClipboardHistory.Folder, entry.Image!);
                if (!File.Exists(path) || new FileInfo(path).Length > MaxImageBytes) { synced.Remove(entry); continue; }
                await dav.PutAsync(ImagePath(entry.Hash), _crypto!.Encrypt(File.ReadAllBytes(path), ImagePath(entry.Hash)));
                state.UploadedImages.Add(entry.Hash);
            }
        }
        var doc = new ListDoc<ClipboardEntry>
        {
            // Rich formats can be megabytes; the text is what matters on the other computer
            Items = synced.Select(e => new ClipboardEntry
            {
                Id = e.Id, Text = e.Text, Image = e.Image, ImageWidth = e.ImageWidth, ImageHeight = e.ImageHeight,
                Files = e.Files, Tags = e.Tags, Hash = e.Hash, Time = e.Time, Pinned = e.Pinned,
            }).ToList(),
            Deleted = deleted,
        };
        doc.Items.AddRange(serverImages.Concat(beyond).Where(e => !deleted.ContainsKey(e.Id)));
        if ((read.Legacy || Serialize(doc) != remoteText) && !await Upload(dav, state, name, read, doc)) return (changed, false);
        state.KnownClips = new HashSet<string>(synced.Select(e => e.Id));
        return (changed, true);
    }

    static string ImagePath(string hash) => "clipboard-images/" + hash + ".bin";

    static void DeleteImageFile(ClipboardEntry entry)
    {
        if (entry.Image == null) return;
        try { File.Delete(Path.Combine(ClipboardHistory.Folder, entry.Image)); }
        catch (IOException) { }
    }

    // —— Settings files

    /// <summary>The file's content as compared and synced; the launcher's window placement stays on each computer.</summary>
    static string? LocalJson(string name)
    {
        var path = Path.Combine(AppPaths.Data, name + ".json");
        if (!File.Exists(path)) return null;
        var json = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (name == LauncherFile && json is JObject o) o.Remove("Window");
        // DPAPI only works on this computer; the whole file is encrypted by the sync anyway
        if (name == "terminal" && json is JObject t) Terminal.Secret.MakePortable(t);
        return json.ToString(Formatting.None);
    }

    static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Changed: files were downloaded to be applied on the next start.</summary>
    async Task<(bool Changed, bool Saved)> SyncSettingsFiles(WebDavClient dav, SyncState state)
    {
        var names = (Settings.Settings ? SettingsFiles : new string[0]).Concat(Settings.Launcher ? new[] { LauncherFile } : new string[0]).ToList();
        if (names.Count == 0) return (false, true);
        const string name = "settings.bin";
        if (Settings.Launcher) _flushLauncher();
        var read = await Download<Dictionary<string, SettingsFile>>(dav, state, name);
        var remote = read.Value;
        var doc = remote != null ? new Dictionary<string, SettingsFile>(remote) : new();
        int pending = 0;
        foreach (var file in names)
        {
            // Downloaded and waiting for a restart: the local file is out of date until then
            if (File.Exists(Path.Combine(PendingFolder, file + ".json"))) continue;
            var local = LocalJson(file);
            doc.TryGetValue(file, out var theirs);
            var localHash = local == null ? "" : Hash(local);
            var remoteHash = theirs == null ? "" : Hash(theirs.Json);
            if (localHash == remoteHash) { state.SettingsLocal[file] = state.SettingsRemote[file] = localHash; continue; }
            bool firstTime = !state.SettingsRemote.ContainsKey(file);
            bool localChanged = !state.SettingsLocal.TryGetValue(file, out var lastLocal) || lastLocal != localHash;
            bool remoteChanged = theirs != null && (firstTime || state.SettingsRemote[file] != remoteHash);
            // UTC, so computers in different time zones compare right
            var localTime = File.GetLastWriteTimeUtc(Path.Combine(AppPaths.Data, file + ".json"));
            // A computer syncing for the first time takes what is already there instead of pushing its defaults
            bool takeRemote = remoteChanged && (firstTime || !localChanged || theirs!.Updated.ToUniversalTime() > localTime);
            if (takeRemote)
            {
                // Changed on both sides: the clocks decide, so keep the losing copy rather than trust them
                if (local != null && localChanged)
                {
                    KeepConflict(file, "local", local);
                    if (!firstTime) _conflicts++;
                }
                Directory.CreateDirectory(PendingFolder);
                File.WriteAllText(Path.Combine(PendingFolder, file + ".json"), theirs!.Json, new UTF8Encoding(false));
                state.PendingOver[file] = localHash;
                state.SettingsLocal[file] = state.SettingsRemote[file] = remoteHash;
                pending++;
            }
            else if (local != null && localChanged)
            {
                if (remoteChanged) { KeepConflict(file, "remote", theirs!.Json); _conflicts++; }
                doc[file] = new SettingsFile { Json = local, Updated = localTime };
                state.SettingsLocal[file] = state.SettingsRemote[file] = localHash;
            }
        }
        if ((read.Legacy || Serialize(doc) != Serialize(remote)) && !await Upload(dav, state, name, read, doc)) return (pending > 0, false);
        return (pending > 0, true);
    }

    /// <summary>Saves a settings file's copy that wasn't used to the conflicts folder.</summary>
    static void KeepConflict(string file, string side, string json)
    {
        Directory.CreateDirectory(ConflictFolder);
        var path = Path.Combine(ConflictFolder, $"{file}-{side}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JToken.Parse(json).ToString(Formatting.Indented), new UTF8Encoding(false));
        Log.Info($"Kept the unused copy of {file}.json in {path}");
    }

    /// <summary>
    /// Moves settings downloaded by the last sync into place. Runs at startup before anything reads them.
    /// A file changed here after the download keeps that newer change; the download goes to the conflicts folder.
    /// </summary>
    public static void ApplyPending()
    {
        if (!Directory.Exists(PendingFolder)) return;
        var store = new JsonSettingsStore(Folder);
        var state = store.Load<SyncState>("state");
        foreach (var path in Directory.GetFiles(PendingFolder, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(AppPaths.Data, name + ".json");
            try
            {
                var local = LocalJson(name);
                if (state.PendingOver.TryGetValue(name, out var before) && (local == null ? "" : Hash(local)) != before)
                {
                    // The next sync sees the local change against the downloaded hash and uploads it
                    KeepConflict(name, "remote", File.ReadAllText(path, Encoding.UTF8));
                    File.Delete(path);
                    state.PendingOver.Remove(name);
                    KeptLocal.Add(name);
                    Log.Info($"Kept local {name}.json, changed after the synced copy was downloaded");
                    continue;
                }
                var json = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (name == LauncherFile && json is JObject o && File.Exists(target) && JToken.Parse(File.ReadAllText(target, Encoding.UTF8)) is JObject current && current["Window"] is { } window)
                    o["Window"] = window;
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, json.ToString(Formatting.Indented), new UTF8Encoding(false));
                // The replaced file stays next to the conflicts, in case the download wasn't wanted
                if (File.Exists(target))
                {
                    Directory.CreateDirectory(ConflictFolder);
                    File.Replace(tmp, target, Path.Combine(ConflictFolder, name + ".before-sync.json"));
                }
                else File.Move(tmp, target);
                File.Delete(path);
                state.PendingOver.Remove(name);
                Log.Info($"Applied synced {name}.json");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Error($"Failed to apply synced {name}.json", ex);
            }
        }
        try { store.Save("state", state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Failed to save sync state", ex); }
    }
}
