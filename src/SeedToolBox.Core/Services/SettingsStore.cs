using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace SeedToolBox.Core.Services;

public interface ISettingsStore
{
    /// <summary>Loads Data/&lt;name&gt;.json, or a new instance if missing or unreadable.</summary>
    T Load<T>(string name) where T : class, new();

    void Save<T>(string name, T value);
}

public sealed class JsonSettingsStore : ISettingsStore
{
    static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        // Don't append to collections that the constructor already filled
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    readonly string _dir;

    public JsonSettingsStore(string dir) => _dir = dir;

    string PathOf(string name) => Path.Combine(_dir, name + ".json");

    public T Load<T>(string name) where T : class, new()
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return new T();

        string text;
        try
        {
            text = ReadWithRetry(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by a sync client or antivirus: the file itself is fine, so never overwrite it with defaults
            Log.Error($"Failed to read {path}, it won't be saved this session", ex);
            lock (_unreadable) _unreadable.Add(name);
            return new T();
        }
        if (Parse<T>(text, path) is { } value) return value;

        // Broken (e.g. empty after a power cut): fall back to the copy kept by the last save
        var bak = path + BackupSuffix;
        try
        {
            if (File.Exists(bak) && Parse<T>(File.ReadAllText(bak, Encoding.UTF8), bak) is { } backup)
            {
                Log.Info($"Loaded {bak} instead");
                return backup;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to read {bak}", ex);
        }
        return new T();
    }

    const string BackupSuffix = ".bak";

    /// <summary>Names whose file exists but couldn't be read; saving them would replace the real data with defaults.</summary>
    readonly HashSet<string> _unreadable = new();

    static string ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(100); }
        }
    }

    /// <summary>The parsed value, or null if the text is empty or not valid JSON (the file is then kept aside).</summary>
    static T? Parse<T>(string text, string path) where T : class
    {
        try
        {
            if (JsonConvert.DeserializeObject<T>(text, JsonSettings) is { } value) return value;
            Log.Error($"{path} is empty");
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to read {path}", ex);
        }
        // Keep the broken file so the user's data isn't silently lost
        try { File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Failed to keep a copy of {path}", ex); }
        return null;
    }

    public void Save<T>(string name, T value)
    {
        lock (_unreadable)
        {
            if (_unreadable.Contains(name)) { Log.Info($"Not saving {name}.json: it couldn't be read at startup"); return; }
        }
        Directory.CreateDirectory(_dir);
        var path = PathOf(name);
        var tmp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value, JsonSettings));
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            // On disk before the rename, or a power cut can leave an empty file under the real name
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + BackupSuffix);
        else File.Move(tmp, path);
    }
}
