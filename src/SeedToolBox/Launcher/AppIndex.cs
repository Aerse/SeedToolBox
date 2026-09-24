using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Launcher;

/// <summary>A Start Menu or desktop shortcut found by <see cref="AppIndex"/>.</summary>
public sealed class SystemApp : ObservableObject
{
    ImageSource? _icon;
    bool _isHighlighted;

    public SystemApp(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }
    public string Path { get; }
    public ImageSource? Icon => _icon ??= IconHelper.GetIcon(Path, "");
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }
}

/// <summary>Shortcuts from the Start Menu and desktops, scanned once in the background.</summary>
public static class AppIndex
{
    static volatile IReadOnlyList<SystemApp> _apps = Array.Empty<SystemApp>();

    public static IReadOnlyList<SystemApp> Apps => _apps;

    /// <summary>Raised on a worker thread when the scan has finished.</summary>
    public static event Action? Loaded;

    public static void StartLoading() => Task.Run(() =>
    {
        try
        {
            _apps = Scan();
            Log.Info($"Indexed {_apps.Count} system apps");
            Loaded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to index system apps", ex);
        }
    });

    static List<SystemApp> Scan()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
        };
        var byName = new Dictionary<string, SystemApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            foreach (var file in Shortcuts(root))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("卸载")) continue;
                if (!byName.ContainsKey(name)) byName[name] = new SystemApp(name, file);
            }
        }
        return byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
    }

    static IEnumerable<string> Shortcuts(string dir)
    {
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir, "*.lnk");
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in Shortcuts(d)) yield return f;
    }

    /// <summary>Best matches first, skipping paths in <paramref name="exclude"/>.</summary>
    public static List<SystemApp> Find(string normalizedQuery, ISet<string> exclude, int max)
    {
        if (normalizedQuery.Length == 0) return new List<SystemApp>();
        return Apps
            .Where(a => !exclude.Contains(a.Path))
            .Select(a => (app: a, score: ItemSearch.Match(a.Name, normalizedQuery)))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score)
            .ThenBy(x => x.app.Name.Length)
            .Take(max)
            .Select(x => x.app)
            .ToList();
    }
}
