namespace SeedToolBox.ScreenTools;

public class ScreenToolsSettings
{
    public const string SettingsName = "screentools";

    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+A";
    public string ColorPickerHotkey { get; set; } = "Ctrl+Alt+C";
    public string RulerHotkey { get; set; } = "Ctrl+Alt+R";

    public ColorFormat ColorFormat { get; set; } = ColorFormat.Hex;
    /// <summary>Last folder screenshots were saved to.</summary>
    public string SaveFolder { get; set; } = "";
    /// <summary>Annotation color and size, as indexes into the toolbar choices.</summary>
    public int PenColor { get; set; }
    public int PenSize { get; set; } = 1;
}
