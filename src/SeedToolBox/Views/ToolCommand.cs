using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SeedToolBox.Launcher;

namespace SeedToolBox.Views;

/// <summary>A button on the main window's tool bar (screenshot, OCR...).</summary>
public sealed class ToolCommand : ObservableObject
{
    string _toolTip;

    public ToolCommand(string id, string glyph, string label, Action action)
    {
        Id = id;
        Glyph = glyph;
        Label = label;
        Action = action;
        _toolTip = label;
    }

    public string Id { get; }
    /// <summary>Character in Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
    public string Glyph { get; }
    public string Label { get; }
    public Action Action { get; }
    public string ToolTip { get => _toolTip; private set => Set(ref _toolTip, value); }

    public void SetHotkey(string hotkey) => ToolTip = hotkey.Length > 0 ? $"{Label}（{hotkey}）" : Label;
}

static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    /// <summary>Rounded corners on Windows 11 (no effect on older versions).</summary>
    public static void RoundCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int value = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
    }
}
