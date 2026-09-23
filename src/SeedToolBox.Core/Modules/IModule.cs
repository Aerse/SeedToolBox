namespace SeedToolBox.Core.Modules;

/// <summary>
/// A feature that plugs into the app. Built-in modules live in the main project;
/// external ones are loaded from Plugins/&lt;Name&gt;/&lt;Name&gt;.dll.
/// Implementations need a public parameterless constructor.
/// </summary>
public interface IModule
{
    /// <summary>Stable identifier, also used as the settings file name.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Called once on the UI thread after the main window is created.</summary>
    void Initialize(IAppHost host);

    /// <summary>Called on exit. Release native resources, stop background work.</summary>
    void Shutdown();
}
