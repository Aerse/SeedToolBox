using System.Collections.Generic;

namespace SeedToolBox.ScreenTools;

public class ScreenToolsSettings
{
    public const string SettingsName = "screentools";

    public bool HideWindowsOnCapture { get; set; }
    public string ScreenshotHotkey { get; set; } = "Alt+Shift+A";
    public string ColorPickerHotkey { get; set; } = "";
    public string RulerHotkey { get; set; } = "";
    public string RecordHotkey { get; set; } = "Alt+Shift+E";
    public string OcrHotkey { get; set; } = "";
    public string QrHotkey { get; set; } = "";

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
    /// <summary>Toggles click-through on all pinned screenshots.</summary>
    public string PinClickThroughHotkey { get; set; } = "";

    /// <summary>Default file name; {format} parts are replaced by the current time.</summary>
    public string FileNameTemplate { get; set; } = "截图_{yyyyMMdd_HHmmss}";
    /// <summary>Also write every screenshot to <see cref="AutoSaveFolder"/>.</summary>
    public bool AutoSave { get; set; }
    /// <summary>Empty = Pictures\SeedToolBox.</summary>
    public string AutoSaveFolder { get; set; } = "";
    /// <summary>How many screenshots to keep in Data\captures; 0 turns the history off.</summary>
    public int HistoryCount { get; set; } = 50;

    /// <summary>Recently picked colours as hex, newest first.</summary>
    public List<string> ColorHistory { get; set; } = new();
    public List<string> FavoriteColors { get; set; } = new();

    /// <summary>Windows OCR language tag; empty uses PaddleOCR or the profile language.</summary>
    public string OcrLanguage { get; set; } = "";
    /// <summary>Copy recognized text without showing the result window.</summary>
    public bool OcrCopyDirectly { get; set; }
    /// <summary>Translation page opened by 翻译, with {text} replaced; empty hides the button.</summary>
    public string TranslateUrl { get; set; } = "";

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
    public bool ShowKeys { get; set; }
    public bool Countdown { get; set; } = true;
    public int GifFps { get; set; } = 15;
    /// <summary>GIF size in percent of the recording.</summary>
    public int GifScale { get; set; } = 100;
    /// <summary>Last folder recordings were saved to.</summary>
    public string SaveFolder { get; set; } = "";
}
