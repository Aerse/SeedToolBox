using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Views;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>Type or paste text and get a QR code to copy, save or pin.</summary>
sealed class QrGeneratorWindow : Window
{
    static readonly int[] ModuleSizes = { 4, 8, 16 };

    readonly ScreenToolService _service;
    readonly TextBox _input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 14,
    };
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _placeholder = new() { Foreground = DialogWindow.HintBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    readonly ComboBox _size = new() { Width = 140, Items = { "小（每格 4 像素）", "中（每格 8 像素）", "大（每格 16 像素）" }, SelectedIndex = 1 };
    readonly TextBlock _info = new() { Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _copy = new() { Content = "复制图片", MinWidth = 88, IsDefault = true };
    readonly Button _save = new() { Content = "保存…", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _pin = new() { Content = "贴到屏幕", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
    BitmapSource? _image;

    public QrGeneratorWindow(ScreenToolService service, string text)
    {
        _service = service;
        Title = "生成二维码";
        Width = 680;
        Height = 420;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DialogWindow.ApplyTheme(this);
        DialogWindow.StyleMultiline(_input);

        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var label = new TextBlock { Text = "输入文字或网址", Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Top);
        left.Children.Add(label);
        left.Children.Add(_input);

        var previewBox = DialogWindow.Card(new Grid { Margin = new Thickness(8), Children = { _preview, _placeholder } });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        Grid.SetColumn(previewBox, 1);
        grid.Children.Add(previewBox);

        var close = new Button { Content = "关闭", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _copy, _save, _pin, close } };
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "尺寸", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, _size, _info } };
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(sizeRow);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(grid);
        Content = root;

        _input.TextChanged += (_, _) => Update();
        _size.SelectionChanged += (_, _) => Update();
        _copy.Click += (_, _) => { if (_image != null) ScreenToolService.CopyImage(_image); _info.Text = "已复制"; };
        _save.Click += (_, _) => { if (_image != null) _service.SaveImage(_image, this, "保存二维码", "二维码"); };
        _pin.Click += (_, _) => Pin();
        Loaded += (_, _) => { Activate(); _input.Focus(); _input.SelectAll(); };

        _input.Text = text;
        Update();
    }

    void Update()
    {
        var text = _input.Text;
        _image = text.Length > 0 ? QrCodes.Encode(text, ModuleSizes[Math.Max(0, _size.SelectedIndex)]) : null;
        _preview.Source = _image;
        _placeholder.Text = text.Length == 0 ? "二维码会显示在这里" : _image == null ? "文字太长，无法生成二维码" : "";
        _info.Text = _image != null ? $"{_image.PixelWidth}×{_image.PixelHeight}" : "";
        _copy.IsEnabled = _save.IsEnabled = _pin.IsEnabled = _image != null;
    }

    void Pin()
    {
        if (_image == null) return;
        // Centered on the cursor, kept within its screen
        var cursor = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(screen.Right - _image.PixelWidth, cursor.X - _image.PixelWidth / 2));
        int y = Math.Max(screen.Top, Math.Min(screen.Bottom - _image.PixelHeight, cursor.Y - _image.PixelHeight / 2));
        _service.Pin(_image, x, y);
    }
}
