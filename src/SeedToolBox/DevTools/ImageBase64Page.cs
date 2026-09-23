using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Images to data URIs and back.</summary>
sealed class ImageBase64Page : DockPanel
{
    readonly Image _preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    readonly TextBlock _placeholder = new()
    {
        Text = "拖入图片、Ctrl+V 粘贴，或点击「选择图片」",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBox _base64 = Ui.Area(wrap: true);
    readonly CheckBox _dataUri = new() { Content = "带 data: 前缀", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    byte[]? _bytes;
    string _mime = "image/png";

    public ImageBase64Page()
    {
        _placeholder.Foreground = Views.DialogWindow.HintBrush;
        var header = Ui.Header("图片 Base64", "图片转 Base64 / Data URI，或把 Base64 还原为图片");
        var toolbar = Ui.Row(
            Ui.Button("选择图片", Pick, accent: true),
            Ui.Button("粘贴图片", Paste),
            _dataUri,
            Ui.Button("Base64 → 图片", Decode),
            Ui.Button("保存图片", Save),
            Ui.Button("清空", Clear));
        _dataUri.Click += (_, _) => { if (_bytes != null) ShowBase64(); };

        var drop = new Border
        {
            Background = (Brush)Application.Current.Resources["WindowBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            AllowDrop = true,
            Padding = new Thickness(8),
            Child = new Grid { Children = { _placeholder, _preview } },
        };
        drop.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        drop.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadFile(files[0]);
        };

        var copy = Ui.Button("复制", () => { if (_base64.Text.Length > 0) ScreenToolService.CopyText(_base64.Text); });
        copy.Margin = new Thickness(0);
        var body = Ui.Columns(Ui.Titled("图片", drop), Ui.Titled("Base64", _base64, copy));

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);

        Focusable = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
                && !_base64.IsKeyboardFocusWithin && Clipboard.ContainsImage())
            {
                Paste();
                e.Handled = true;
            }
        };
    }

    void Pick()
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.svg|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadFile(dialog.FileName);
    }

    void LoadFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var mime = Mime(bytes) ?? Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };
            SetImage(bytes, mime);
            ShowBase64();
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    void Paste()
    {
        if (!Clipboard.ContainsImage()) { Ui.SetStatus(_status, "剪贴板里没有图片", true); return; }
        var image = Clipboard.GetImage();
        if (image == null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        SetImage(ms.ToArray(), "image/png");
        ShowBase64();
    }

    void Decode()
    {
        var text = _base64.Text.Trim();
        if (text.Length == 0) { Ui.SetStatus(_status, "请先粘贴 Base64", true); return; }
        try
        {
            var bytes = Base64TextPage.FromBase64(text);
            SetImage(bytes, Mime(bytes) ?? "application/octet-stream");
        }
        catch (FormatException)
        {
            Ui.SetStatus(_status, "不是有效的 Base64", true);
        }
    }

    void SetImage(byte[] bytes, string mime)
    {
        _bytes = bytes;
        _mime = mime;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            _preview.Source = bitmap;
            _placeholder.Visibility = Visibility.Collapsed;
            Ui.SetStatus(_status, $"{mime}，{bitmap.PixelWidth}×{bitmap.PixelHeight}，{Ui.FormatSize(bytes.Length)}");
        }
        catch (Exception)
        {
            // SVG and WebP (without the codec) cannot be previewed but still convert
            _preview.Source = null;
            _placeholder.Visibility = Visibility.Visible;
            Ui.SetStatus(_status, $"{mime}，{Ui.FormatSize(bytes.Length)}（无法预览）");
        }
    }

    void ShowBase64()
    {
        if (_bytes == null) return;
        var s = Convert.ToBase64String(_bytes);
        _base64.Text = _dataUri.IsChecked == true ? $"data:{_mime};base64,{s}" : s;
    }

    void Save()
    {
        if (_bytes == null) { Ui.SetStatus(_status, "没有可保存的图片", true); return; }
        var ext = _mime switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            "image/x-icon" => ".ico",
            "image/svg+xml" => ".svg",
            "image/png" => ".png",
            _ => ".bin",
        };
        var dialog = new SaveFileDialog { FileName = "image" + ext, Filter = $"*{ext}|*{ext}|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _bytes);
            Ui.SetStatus(_status, "已保存到 " + dialog.FileName);
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
        }
    }

    void Clear()
    {
        _bytes = null;
        _preview.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        _base64.Clear();
        _status.Text = "";
    }

    static string? Mime(byte[] b)
    {
        bool Starts(params byte[] sig)
        {
            if (b.Length < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (b[i] != sig[i]) return false;
            return true;
        }
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (Starts(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (Starts(0x47, 0x49, 0x46, 0x38)) return "image/gif";
        if (Starts(0x42, 0x4D)) return "image/bmp";
        if (Starts(0x00, 0x00, 0x01, 0x00)) return "image/x-icon";
        if (b.Length > 12 && Starts(0x52, 0x49, 0x46, 0x46) && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 256)).TrimStart();
        if (head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg"))) return "image/svg+xml";
        return null;
    }
}
