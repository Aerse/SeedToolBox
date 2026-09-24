using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    static readonly string[] SettingsFiles = { "clipboard", "screentools", "module-hotkeys", "ai" };
    const string LauncherFile = "launcher";
    const long MaxImageBytes = 8L * 1024 * 1024;

    static string Folder => Path.Combine(AppPaths.Data, "Sync");
    static string PendingFolder => Path.Combine(Folder, PendingFolderName);

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
        clipboard.Entries.CollectionChanged += (_, _) => SyncSoon();
        clipboard.EntryChanged += SyncSoon;
        Reschedule();
        if (Ready) SyncSoon();
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
            var root = Settings.Url.Trim().TrimEnd('/') + "/" + Settings.Folder.Trim().Trim('/');
            using var dav = new WebDavClient(root, Settings.User.Trim(), SyncCrypto.Unprotect(Settings.Password));
            await dav.EnsureFolderAsync();
            var state = _stateStore.Load<SyncState>("state");
            var done = new List<string>();

            if (Settings.Notes && await SyncNotes(dav, state)) done.Add("笔记");
            if (Settings.Clipboard && await SyncClipboard(dav, state)) done.Add("剪贴板");
            int pending = await SyncSettingsFiles(dav, state);
            _stateStore.Save("state", state);

            Settings.LastSync = DateTime.Now;
            _store.Save(SyncSettings.SettingsName, Settings);
            if (pending > 0) RestartNeeded = true;
            Finish($"已同步 {DateTime.Now:HH:mm}" + (done.Count > 0 ? $"，更新了{string.Join("、", done)}" : "")
                + (RestartNeeded ? "；云端的设置已下载，重启程序后生效" : ""), false);
            return true;
        }
        catch (Exception ex) when (ex is SyncException or IOException or System.Net.Http.HttpRequestException or TaskCanceledException or JsonException or CryptographicException or UnauthorizedAccessException)
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
        System.Net.Http.HttpRequestException { InnerException: System.Net.WebException web } => web.Message,
        _ => ex.Message,
    };

    // —— Encrypted JSON documents on the server

    async Task<T?> Download<T>(WebDavClient dav, string name) where T : class
    {
        var data = await dav.GetAsync(name);
        if (data == null) return null;
        return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(_crypto!.Decrypt(data)), Json);
    }

    Task Upload<T>(WebDavClient dav, string name, T value) =>
        dav.PutAsync(name, _crypto!.Encrypt(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Json))));

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

    async Task<bool> SyncNotes(WebDavClient dav, SyncState state)
    {
        const string name = "notes.bin";
        _notes.Flush();
        var remote = await Download<ListDoc<Note>>(dav, name);
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
        if (Serialize(doc) != remoteText) await Upload(dav, name, doc);
        state.KnownNotes = new HashSet<string>(_notes.Notes.Select(n => n.Id));
        return changed;
    }

    // —— Clipboard

    async Task<bool> SyncClipboard(WebDavClient dav, SyncState state)
    {
        const string name = "clipboard.bin";
        bool images = Settings.ClipboardImages;
        var local = _clipboard.Entries.Where(e => images || !e.IsImage).ToList();
        var remote = await Download<ListDoc<ClipboardEntry>>(dav, name);
        var remoteText = Serialize(remote);
        // Keep the server's image entries as they are while this computer doesn't sync images
        var serverImages = images ? new List<ClipboardEntry>() : remote?.Items.Where(e => e.Image != null).ToList() ?? new();
        if (remote != null && !images) remote.Items.RemoveAll(e => e.Image != null);
        // Images not synced stay out of the known list, so they don't look deleted once image sync is turned on
        var known = new HashSet<string>(state.KnownClips.Where(id => !_clipboard.Entries.Any(e => e.Id == id && e.IsImage && !images)));
        var (add, update, remove, deleted) = Merge(local, remote, known, e => e.Id, e => e.Time);

        // Fetch pictures first, so an entry is only added once its file is here
        var fetched = new List<ClipboardEntry>();
        foreach (var entry in add)
        {
            if (entry.Image == null) { fetched.Add(entry); continue; }
            var data = await dav.GetAsync(ImagePath(entry.Hash));
            if (data == null) continue;
            Directory.CreateDirectory(ClipboardHistory.Folder);
            entry.Image = entry.Id + ".png";
            File.WriteAllBytes(Path.Combine(ClipboardHistory.Folder, entry.Image), _crypto!.Decrypt(data));
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
                await dav.PutAsync(ImagePath(entry.Hash), _crypto!.Encrypt(File.ReadAllBytes(path)));
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
        doc.Items.AddRange(serverImages.Where(e => !deleted.ContainsKey(e.Id)));
        if (Serialize(doc) != remoteText) await Upload(dav, name, doc);
        state.KnownClips = new HashSet<string>(synced.Select(e => e.Id));
        return changed;
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
        return json.ToString(Formatting.None);
    }

    static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Returns how many files were downloaded to be applied on the next start.</summary>
    async Task<int> SyncSettingsFiles(WebDavClient dav, SyncState state)
    {
        var names = (Settings.Settings ? SettingsFiles : new string[0]).Concat(Settings.Launcher ? new[] { LauncherFile } : new string[0]).ToList();
        if (names.Count == 0) return 0;
        const string name = "settings.bin";
        if (Settings.Launcher) _flushLauncher();
        var remote = await Download<Dictionary<string, SettingsFile>>(dav, name);
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
            var localTime = File.GetLastWriteTime(Path.Combine(AppPaths.Data, file + ".json"));
            // A computer syncing for the first time takes what is already there instead of pushing its defaults
            bool takeRemote = remoteChanged && (firstTime || !localChanged || theirs!.Updated > localTime);
            if (takeRemote)
            {
                Directory.CreateDirectory(PendingFolder);
                File.WriteAllText(Path.Combine(PendingFolder, file + ".json"), theirs!.Json, new UTF8Encoding(false));
                state.SettingsLocal[file] = state.SettingsRemote[file] = remoteHash;
                pending++;
            }
            else if (local != null && localChanged)
            {
                doc[file] = new SettingsFile { Json = local, Updated = localTime };
                state.SettingsLocal[file] = state.SettingsRemote[file] = localHash;
            }
        }
        if (Serialize(doc) != Serialize(remote)) await Upload(dav, name, doc);
        return pending;
    }

    /// <summary>
    /// Moves settings downloaded by the last sync into place. Runs at startup before anything reads them.
    /// </summary>
    public static void ApplyPending()
    {
        if (!Directory.Exists(PendingFolder)) return;
        foreach (var path in Directory.GetFiles(PendingFolder, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(AppPaths.Data, name + ".json");
            try
            {
                var json = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (name == LauncherFile && json is JObject o && File.Exists(target) && JToken.Parse(File.ReadAllText(target, Encoding.UTF8)) is JObject current && current["Window"] is { } window)
                    o["Window"] = window;
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, json.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
                File.Delete(path);
                Log.Info($"Applied synced {name}.json");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Error($"Failed to apply synced {name}.json", ex);
            }
        }
    }
}
