using System;
using System.IO;
using System.Text;
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

        try
        {
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8), JsonSettings) ?? new T();
        }
        catch (JsonException ex)
        {
            // Keep the broken file so the user's data isn't silently lost
            Log.Error($"Failed to read {path}", ex);
            File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            return new T();
        }
    }

    public void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(value, JsonSettings), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
