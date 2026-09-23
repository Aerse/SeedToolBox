using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using SeedToolBox.Models;

namespace SeedToolBox.Services;

/// <summary>Stores config as JSON in a Data folder next to the exe (portable).</summary>
public static class DataStore
{
    static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    static readonly string ConfigPath = Path.Combine(DataDir, "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppData Load()
    {
        AppData? data = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(ConfigPath), Options);
            }
            catch (JsonException)
            {
                // Keep the broken file so the user's data isn't silently lost
                File.Copy(ConfigPath, ConfigPath + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            }
        }

        data ??= new AppData();
        if (data.Groups.Count == 0) data.Groups.Add(new ItemGroup { Name = "常用工具" });
        return data;
    }

    public static void Save(AppData data)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
        File.Move(tmp, ConfigPath, true);
    }
}
