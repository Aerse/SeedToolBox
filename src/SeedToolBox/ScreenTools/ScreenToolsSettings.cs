namespace SeedToolBox.ScreenTools;

public class ScreenToolsSettings
{
    public const string SettingsName = "screentools";

    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+A";
    public string ColorPickerHotkey { get; set; } = "Ctrl+Alt+C";
    public string RulerHotkey { get; set; } = "Ctrl+Alt+R";
    public string RecordHotkey { get; set; } = "Ctrl+Alt+V";

    public ColorFormat ColorFormat { get; set; } = ColorFormat.Hex;
    /// <summary>Last folder screenshots were saved to.</summary>
    public string SaveFolder { get; set; } = "";
    /// <summary>Annotation color and size, as indexes into the toolbar choices.</summary>
    public int PenColor { get; set; }
    public int PenSize { get; set; } = 1;

    public RecordSettings Record { get; set; } = new();
}

public class RecordSettings
{
    public int Fps { get; set; } = 30;
    /// <summary>0 = low, 1 = medium, 2 = high.</summary>
    public int Quality { get; set; } = 1;
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool ClickEffect { get; set; } = true;
    public bool Countdown { get; set; } = true;
    public int GifFps { get; set; } = 15;
    /// <summary>GIF size in percent of the recording.</summary>
    public int GifScale { get; set; } = 100;
    /// <summary>Last folder recordings were saved to.</summary>
    public string SaveFolder { get; set; } = "";
}
