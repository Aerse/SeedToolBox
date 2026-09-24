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
}
