using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeedToolBox.ScreenTools;

/// <summary>A screenshot pinned on top of other windows. Wheel zooms, Ctrl+wheel changes opacity.</summary>
sealed class PinWindow : Window
{
    static readonly List<PinWindow> All = new();
    static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(30, 144, 255));
    static readonly Brush InactiveBorder = new SolidColorBrush(Color.FromRgb(144, 144, 144));

    readonly BitmapSource _image;
    readonly ScreenToolService _service;
    readonly Image _view;
    readonly Border _frame;
    readonly ScaleTransform _scale = new();
    readonly int _screenX, _screenY;
    double _dpi = 1;
    double _zoom = 1;

    public PinWindow(BitmapSource image, int screenX, int screenY, ScreenToolService service)
    {
        _image = image;
        _service = service;
        _screenX = screenX;
        _screenY = screenY;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // needed for Opacity
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        Title = "贴图";

        _view = new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, LayoutTransform = _scale };
        _frame = new Border { BorderThickness = new Thickness(1), BorderBrush = ActiveBorder, Child = _view };
        Content = _frame;
        ContextMenu = BuildMenu();
        ToolTip = "拖动移动 · 滚轮缩放 · Ctrl+滚轮调透明度 · 双击或 Esc 关闭";

        SourceInitialized += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyZoom();
            // Line the image up exactly with where it was captured, outside the 1 DIP border
            int border = (int)Math.Round(_dpi);
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, _screenX - border, _screenY - border, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        };
        DpiChanged += (_, e) => { _dpi = e.NewDpi.DpiScaleX; ApplyZoom(); };
        Activated += (_, _) => _frame.BorderBrush = ActiveBorder;
        Deactivated += (_, _) => _frame.BorderBrush = InactiveBorder;
        Closed += (_, _) => All.Remove(this);
        All.Add(this);
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("复制", "Ctrl+C", () => ScreenToolService.CopyImage(_image)));
        menu.Items.Add(Item("保存...", "Ctrl+S", () => _service.SaveImage(_image, this)));
        menu.Items.Add(Item("原始大小", "1", () => SetZoom(1)));
        menu.Items.Add(Item("识别文字", null, () => _service.RecognizeText(_image)));
        menu.Items.Add(Item("识别二维码", null, () => _service.DecodeQrCodes(_image)));

        var opacity = new MenuItem { Header = "透明度" };
        foreach (var percent in new[] { 100, 80, 60, 40 })
            opacity.Items.Add(Item($"{percent}%", null, () => Opacity = percent / 100.0));
        menu.Items.Add(opacity);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("关闭", "Esc", Close));
        menu.Items.Add(Item("关闭全部贴图", null, () => { foreach (var pin in All.ToList()) pin.Close(); }));
        return menu;
    }

    static MenuItem Item(string header, string? gesture, Action onClick)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => onClick();
        return item;
    }

    void SetZoom(double zoom)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var before);
        _zoom = Math.Max(0.1, Math.Min(8, zoom));
        ApplyZoom();

        // Keep the center fixed: let SizeToContent resize now, then shift by half the size change
        UpdateLayout();
        GetWindowRect(hwnd, out var after);
        int dx = ((after.Right - after.Left) - (before.Right - before.Left)) / 2;
        int dy = ((after.Bottom - after.Top) - (before.Bottom - before.Top)) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, before.Left - dx, before.Top - dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void ApplyZoom()
    {
        // 1 image pixel = 1 screen pixel at 100%
        _scale.ScaleX = _scale.ScaleY = _zoom / _dpi;
        RenderOptions.SetBitmapScalingMode(_view, _zoom < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) Close();
        else DragMove();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Opacity = Math.Max(0.2, Math.Min(1, Opacity + (e.Delta > 0 ? 0.1 : -0.1)));
        else
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) Close();
        else if (ctrl && e.Key == Key.C) ScreenToolService.CopyImage(_image);
        else if (ctrl && e.Key == Key.S) _service.SaveImage(_image, this);
        else if (e.Key is Key.D1 or Key.NumPad1) SetZoom(1);
    }

    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
