namespace SeedToolBox.ScreenTools;

public class ScreenToolsSettings
{
    public const string SettingsName = "screentools";

    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+A";
    public string ColorPickerHotkey { get; set; } = "Ctrl+Alt+C";
    public string RulerHotkey { get; set; } = "Ctrl+Alt+R";
    public string RecordHotkey { get; set; } = "Ctrl+Alt+V";
    public string OcrHotkey { get; set; } = "Ctrl+Alt+O";
    public string QrHotkey { get; set; } = "Ctrl+Alt+Q";

    public ColorFormat ColorFormat { get; set; } = ColorFormat.Hex;
    /// <summary>Last folder screenshots were saved to.</summary>
    public string SaveFolder { get; set; } = "";
    /// <summary>Annotation color and size, as indexes into the toolbar choices.</summary>
    public int PenColor { get; set; }
    public int PenSize { get; set; } = 1;
    /// <summary>Hex colour used when <see cref="PenColor"/> points past the palette.</summary>
    public string CustomColor { get; set; } = "#FF69B4";
    /// <summary>Fill rectangles and ellipses instead of outlining them.</summary>
    public bool FillShapes { get; set; }
    /// <summary>Last captured region in screen pixels (x, y, width, height), for 重复上次区域.</summary>
    public int[]? LastRegion { get; set; }
    /// <summary>Locked selection ratio such as "16:9"; empty for free selection.</summary>
    public string SelectionRatio { get; set; } = "";
    /// <summary>Seconds to wait for delayed screenshots.</summary>
    public int CaptureDelay { get; set; } = 3;
    public string DelayedScreenshotHotkey { get; set; } = "";
    public string FullScreenHotkey { get; set; } = "";
    public string ActiveWindowHotkey { get; set; } = "";
    public string LastRegionHotkey { get; set; } = "";

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
