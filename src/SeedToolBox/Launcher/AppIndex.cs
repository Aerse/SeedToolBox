using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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

/// <summary>Shortcuts from the Start Menu and desktops, scanned in the background and again when they change.</summary>
public static class AppIndex
{
    static volatile IReadOnlyList<SystemApp> _apps = Array.Empty<SystemApp>();

    public static IReadOnlyList<SystemApp> Apps => _apps;

    /// <summary>Raised on a worker thread when the scan has finished.</summary>
    public static event Action? Loaded;

    static readonly object ScanLock = new();
    static readonly List<FileSystemWatcher> Watchers = new();
    static Timer? _rescan;

    static readonly Environment.SpecialFolder[] Folders =
    {
        Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
        Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
    };

    public static void StartLoading()
    {
        Load();
        Watch();
    }

    /// <summary>Programs installed or removed later show up without a restart; bursts of changes are scanned once.</summary>
    static void Watch()
    {
        _rescan = new Timer(_ => Load(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var folder in Folders)
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root, "*.lnk") { IncludeSubdirectories = true };
                FileSystemEventHandler changed = (_, _) => _rescan?.Change(3000, Timeout.Infinite);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Changed += changed;
                watcher.Renamed += (_, _) => _rescan?.Change(3000, Timeout.Infinite);
                watcher.EnableRaisingEvents = true;
                Watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Log.Error($"Failed to watch {root}", ex);
            }
        }
    }

    static void Load() => Task.Run(() =>
    {
        try
        {
            lock (ScanLock) _apps = Scan();
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
        // The same shortcut often sits in the Start Menu and on the desktop: keep one per name and target
        var byKey = new Dictionary<string, SystemApp>(StringComparer.OrdinalIgnoreCase);
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null) shell = Activator.CreateInstance(type);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException) { }
        try
        {
            foreach (var folder in Folders)
            {
                var root = Environment.GetFolderPath(folder);
                if (root.Length == 0 || !Directory.Exists(root)) continue;
                foreach (var file in Shortcuts(root))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    var target = shell != null ? Target(shell, file) : null;
                    if (IsUninstaller(name, target)) continue;
                    var key = name + "|" + (target ?? "");
                    if (!byKey.ContainsKey(key)) byKey[key] = new SystemApp(name, file);
                }
            }
        }
        finally
        {
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
        return byKey.Values.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>"Uninstall Foo", "卸载 Foo" or a shortcut to unins000.exe; "Revo Uninstaller" and the like stay.</summary>
    static bool IsUninstaller(string name, string? target)
    {
        name = name.Trim();
        if (name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("卸载")) return true;
        if (name.EndsWith("uninstall", StringComparison.OrdinalIgnoreCase) || name.EndsWith("卸载")) return true;
        if (target == null) return false;
        var exe = System.IO.Path.GetFileNameWithoutExtension(target);
        return exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !exe.StartsWith("uninstaller", StringComparison.OrdinalIgnoreCase);
    }

    static string? Target(object shell, string lnk)
    {
        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var target = shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
        }
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
