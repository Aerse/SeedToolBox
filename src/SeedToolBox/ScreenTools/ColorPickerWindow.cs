using System;
using System.Windows;
using System.Windows.Input;

namespace SeedToolBox.ScreenTools;

/// <summary>Magnifier over a frozen screen; click copies the pixel color.</summary>
sealed class ColorPickerWindow : OverlayWindow
{
    readonly ScreenToolService _service;
    readonly Magnifier _magnifier;
    System.Windows.Media.Color _color;

    public ColorPickerWindow(ScreenShot shot, ScreenToolService service) : base(shot)
    {
        _service = service;
        _magnifier = new Magnifier(shot);
        Ui.Children.Add(_magnifier);

        MouseMove += (_, e) => UpdateAt(PixelOf(e));
        Loaded += (_, _) => UpdateAt(Mouse.GetPosition(Surface));
        MouseLeftButtonUp += (_, _) => Copy();
        MouseRightButtonUp += (_, _) => Close();
        PreviewKeyDown += OnKey;
    }

    void UpdateAt(Point pixel)
    {
        int x = Math.Max(0, Math.Min(Shot.Width - 1, (int)pixel.X));
        int y = Math.Max(0, Math.Min(Shot.Height - 1, (int)pixel.Y));
        _color = Shot.GetColor(x, y);
        _magnifier.Update(x, y,
            $"{x + Shot.X}, {y + Shot.Y}\n{ColorText.Format(_color, _service.Settings.ColorFormat)}\nTab 切换格式 · 单击复制 · Esc 退出");
        PlaceNear(_magnifier, ToUi(pixel));
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Tab:
            case Key.Space:
                _service.Settings.ColorFormat = ColorText.Next(_service.Settings.ColorFormat);
                _service.SaveSettings();
                UpdateAt(Mouse.GetPosition(Surface));
                break;
            case Key.Enter:
            case Key.C: Copy(); break;
            default: e.Handled = NudgeCursor(e.Key); break;
        }
    }

    void Copy()
    {
        var text = ColorText.Format(_color, _service.Settings.ColorFormat);
        Close();
        ScreenToolService.CopyText(text);
    }
}
