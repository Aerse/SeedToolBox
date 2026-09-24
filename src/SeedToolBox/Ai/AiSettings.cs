using System.Collections.Generic;

namespace SeedToolBox.Ai;

/// <summary>Saved as Data\ai.json.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    /// <summary>"builtin" for the copy installed by the app, "system" for a pi found on PATH.</summary>
    public string Source { get; set; } = "builtin";
    /// <summary>Key into <see cref="PiMirrors.All"/>, or "custom".</summary>
    public string Mirror { get; set; } = "npmmirror";
    public string CustomNodeMirror { get; set; } = "";
    public string CustomRegistry { get; set; } = "";
    /// <summary>Try the other download sources when the chosen one fails.</summary>
    public bool FallbackMirrors { get; set; } = true;
    /// <summary>Use ~/.pi/agent (logins and models of a pi the user already set up) instead of Data\PiAgent.</summary>
    public bool ShareConfig { get; set; }
    /// <summary>"provider/id"; empty lets pi pick its default.</summary>
    public string Model { get; set; } = "";
    public string Thinking { get; set; } = "off";
    /// <summary>Copies the selection in the foreground window and opens the assistant with it.</summary>
    public string SelectionHotkey { get; set; } = "";
    public string ScreenshotHotkey { get; set; } = "";

    /// <summary>Folders the automation mode may change files in; empty means Desktop, Documents and Downloads.</summary>
    public List<string> AutomationFolders { get; set; } = new();
    /// <summary>Lets the automation mode run PowerShell commands, each one confirmed.</summary>
    public bool AllowCommands { get; set; }
    /// <summary>Installed pi packages (plugins) are loaded in automation mode.</summary>
    public bool LoadPlugins { get; set; } = true;
    /// <summary>Opens new windows in automation mode.</summary>
    public bool AutomationDefault { get; set; }
    public List<QuickTask> QuickTasks { get; set; } = new();
}

/// <summary>A saved request the automation mode can run again with one click.</summary>
public sealed class QuickTask
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>When it runs on its own, in the words the user typed; empty when not scheduled.</summary>
    public string Schedule { get; set; } = "";
}
