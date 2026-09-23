using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SeedToolBox.Core;
using SeedToolBox.Core.Modules;
using SeedToolBox.Core.Services;
using SeedToolBox.Modules;

namespace SeedToolBox.Host;

/// <summary>Creates built-in and plugin modules and isolates their failures from the app.</summary>
sealed class ModuleManager
{
    readonly IAppHost _host;
    readonly List<IModule> _modules = new();

    public ModuleManager(IAppHost host) => _host = host;

    public void LoadAll()
    {
        var candidates = new List<IModule> { new SystemToolsModule() };
        candidates.AddRange(DiscoverPlugins());

        foreach (var module in candidates)
        {
            try
            {
                module.Initialize(_host);
                _modules.Add(module);
                Log.Info($"Loaded module {module.Id}");
            }
            catch (Exception ex)
            {
                Log.Error($"Module {module.Id} failed to initialize", ex);
            }
        }
    }

    public void ShutdownAll()
    {
        foreach (var module in _modules)
        {
            try { module.Shutdown(); }
            catch (Exception ex) { Log.Error($"Module {module.Id} failed to shut down", ex); }
        }
        _modules.Clear();
    }

    /// <summary>Plugins live in Plugins/&lt;Name&gt;/&lt;Name&gt;.dll alongside their dependencies.</summary>
    static IEnumerable<IModule> DiscoverPlugins()
    {
        if (!Directory.Exists(AppPaths.Plugins)) yield break;

        foreach (var dir in Directory.GetDirectories(AppPaths.Plugins))
        {
            var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (!File.Exists(dll)) continue;

            List<IModule> found;
            try
            {
                found = Assembly.LoadFrom(dll).GetExportedTypes()
                    .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    .Select(t => (IModule)Activator.CreateInstance(t))
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load plugin {dll}", ex);
                continue;
            }
            foreach (var module in found) yield return module;
        }
    }
}
