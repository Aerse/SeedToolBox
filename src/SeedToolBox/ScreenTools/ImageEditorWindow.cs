using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Views;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Annotates an existing image (a scrolling capture, a history entry or a pin) in a normal window,
/// one image pixel per screen pixel.
/// </summary>
sealed class ImageEditorWindow : Window
{
    readonly ScreenToolService _service;
    readonly BitmapSource _image;
    readonly AnnotationLayer _layer;
    readonly Grid _canvas;
    readonly ScaleTransform _unscale = new();
    readonly Border _styleCard;
    readonly Action<BitmapSource>? _done;
    bool _dragging, _captured;

    /// <param name="done">When set, a 完成 button hands the annotated image back instead of the usual outputs.</param>
    public ImageEditorWindow(BitmapSource image, ScreenToolService service, Action<BitmapSource>? done = null)
    {
        _service = service;
        _image = image;
        _done = done;
        Title = done != null ? "标注贴图" : $"编辑图片 - {image.PixelWidth} × {image.PixelHeight}";
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));

        double scale = 1;
        _layer = new AnnotationLayer(PixelBuffer.From(image), service, () => scale);
        _layer.ToolChanged += UpdateStyleCard;

        var picture = new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        _canvas = new Grid
        {
            Width = image.PixelWidth,
            Height = image.PixelHeight,
            LayoutTransform = _unscale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Background = Brushes.White,
            Children = { picture, _layer.Content, _layer.Chrome },
        };
        _canvas.PreviewMouseLeftButtonDown += OnMouseDown;
        _canvas.PreviewMouseMove += OnMouseMove;
        _canvas.PreviewMouseLeftButtonUp += OnMouseUp;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        _layer.AddToolButtons(tools);
        tools.Children.Add(ToolbarUi.Divider());
        if (done != null)
        {
            tools.Children.Add(ToolbarUi.Button("✕", "放弃 (Esc)", Close));
            tools.Children.Add(ToolbarUi.Button("✓", "完成", Finish, ToolbarUi.Accent));
        }
        else
        {
            tools.Children.Add(ToolbarUi.Button("文", "识别文字", () => _service.RecognizeText(Render())));
            tools.Children.Add(ToolbarUi.Button("码", "识别二维码", () => _service.DecodeQrCodes(Render())));
            tools.Children.Add(ToolbarUi.Divider());
            tools.Children.Add(ToolbarUi.Button("📌", "贴到屏幕", PinImage));
            tools.Children.Add(ToolbarUi.Button("💾", "保存 (Ctrl+S)", Save));
            tools.Children.Add(ToolbarUi.Button("✓", "复制到剪贴板 (Ctrl+C)", Copy, ToolbarUi.Accent));
        }
        var toolCard = ToolbarUi.Card(tools);
        toolCard.Effect = null;
        _styleCard = ToolbarUi.Card(_layer.StyleBar);
        _styleCard.Effect = null;
        _styleCard.Margin = new Thickness(6, 0, 0, 0);
        _styleCard.Visibility = Visibility.Collapsed;
        var bar = new WrapPanel { Margin = new Thickness(8), Children = { toolCard, _styleCard } };
        DockPanel.SetDock(bar, Dock.Top);

        Content = new DockPanel
        {
            Children =
            {
                bar,
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(8, 0, 8, 8),
                    Focusable = false,
                    Content = _canvas,
                },
            },
        };

        void UpdateScale()
        {
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _unscale.ScaleX = _unscale.ScaleY = 1 / scale;
        }
        SourceInitialized += (_, _) =>
        {
            UpdateScale();
            var area = SystemParameters.WorkArea;
            Width = Math.Max(640, Math.Min(area.Width * 0.9, image.PixelWidth / scale + 60));
            Height = Math.Max(420, Math.Min(area.Height * 0.9, image.PixelHeight / scale + 130));
        };
        DpiChanged += (_, _) => UpdateScale();
        PreviewKeyDown += OnKey;
    }

    void UpdateStyleCard() => _styleCard.Visibility = _layer.ShowsStyle ? Visibility.Visible : Visibility.Collapsed;

    Point PixelOf(MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        return new Point(Math.Max(0, Math.Min(_image.PixelWidth - 1, p.X)), Math.Max(0, Math.Min(_image.PixelHeight - 1, p.Y)));
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_layer.EditingText is { IsMouseOver: true }) return;
        if (_layer.Tool == AnnotationTool.None) return;
        e.Handled = true;
        Focus();
        if (_layer.MouseDown(PixelOf(e)))
        {
            _dragging = true;
            _canvas.CaptureMouse();
        }
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = PixelOf(e);
        if (_dragging) _layer.MouseMove(p);
        else _canvas.Cursor = _layer.Tool == AnnotationTool.None ? Cursors.Arrow : _layer.CursorAt(p);
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _layer.MouseUp();
        _canvas.ReleaseMouseCapture();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_layer.EditingText is { IsKeyboardFocused: true })
        {
            _layer.HandleTextKey(e);
            return;
        }
        if (_layer.HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape)
        {
            if (_layer.Tool != AnnotationTool.None) _layer.SetTool(AnnotationTool.None);
            else if (_done != null) Close();
            else return;
        }
        else if (ctrl && e.Key == Key.C && _done == null) Copy();
        else if (ctrl && e.Key == Key.S && _done == null) Save();
        else if (e.Key == Key.Enter && _done != null) Finish();
        else return;
        e.Handled = true;
    }

    BitmapSource Render() => _layer.Render(_image, new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight));

    void Captured(BitmapSource image)
    {
        if (_captured) return;
        _captured = true;
        _service.OnCaptured(image, null);
    }

    void Copy()
    {
        var image = Render();
        Captured(image);
        ScreenToolService.CopyImage(image);
        Title = $"已复制 - {_image.PixelWidth} × {_image.PixelHeight}";
    }

    void Save()
    {
        var image = Render();
        if (_service.SaveImage(image, this)) Captured(image);
    }

    void PinImage()
    {
        var image = Render();
        Captured(image);
        var p = System.Windows.Forms.Cursor.Position;
        _service.Pin(image, p.X - Math.Min(image.PixelWidth, 600) / 2, p.Y - Math.Min(image.PixelHeight, 400) / 2);
    }

    void Finish()
    {
        var image = Render();
        Close();
        _done?.Invoke(image);
    }
}
