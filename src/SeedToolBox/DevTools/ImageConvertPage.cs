using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

sealed class ImageConvertItem : ObservableObject
{
    string _state = "等待";
    string _result = "";
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Info { get; set; } = "";
    public string Result { get => _result; set => Set(ref _result, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public string? Output { get; set; }
}

/// <summary>Batch converts images between PNG, JPG, BMP, GIF, TIFF and ICO, optionally resizing.</summary>
sealed class ImageConvertPage : DockPanel
{
    static readonly string[] InputExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jfif", ".heic" };
    static readonly string[] Formats = { "PNG", "JPG", "BMP", "GIF", "TIFF", "ICO" };

    readonly ObservableCollection<ImageConvertItem> _items = new();
    readonly ListView _list = new();
    readonly ComboBox _format = new() { Width = 90, ItemsSource = Formats, SelectedIndex = 1 };
    readonly TextBox _quality = Ui.Field(56);
    readonly TextBox _maxSide = Ui.Field(70);
    readonly TextBox _folder = Ui.Field(260);
    readonly TextBlock _status = Ui.Status();
    readonly Button _start;
    bool _running;

    public ImageConvertPage()
    {
        var header = Ui.Header("图片格式转换", "批量转换 PNG / JPG / BMP / GIF / TIFF / ICO，可限制最长边；转 JPG 时透明部分填充白色");
        _quality.Text = "90";
        _maxSide.ToolTip = "留空表示不缩放";
        _folder.ToolTip = "留空表示输出到原图所在文件夹";
        _start = Ui.Button("开始转换", Start, accent: true);
        var toolbar = Ui.Row(
            Ui.Button("添加图片", Pick),
            Ui.Label("转为"), _format, Ui.Label("", 16),
            Ui.Label("JPG 质量"), _quality, Ui.Label("", 16),
            Ui.Label("最长边"), _maxSide, Ui.Label("px", 16),
            _start,
            Ui.Button("清空列表", () => { if (!_running) { _items.Clear(); _status.Text = ""; } }));
        var browse = Ui.Button("浏览…", Browse);
        browse.Margin = new Thickness(8, 0, 0, 0);
        var folderRow = Ui.Row(Ui.Label("输出文件夹"), _folder, browse, new TextBlock { Text = "（留空则放在原图旁边，重名时自动加序号）", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        _format.SelectionChanged += (_, _) => _quality.IsEnabled = _format.SelectedIndex == 1;

        _list.ItemsSource = _items;
        _list.AllowDrop = true;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "文件", Width = 280, DisplayMemberBinding = new Binding(nameof(ImageConvertItem.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "原图", Width = 150, DisplayMemberBinding = new Binding(nameof(ImageConvertItem.Info)) });
        view.Columns.Add(new GridViewColumn { Header = "结果", Width = 150, DisplayMemberBinding = new Binding(nameof(ImageConvertItem.Result)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 200, DisplayMemberBinding = new Binding(nameof(ImageConvertItem.State)) });
        _list.View = view;
        _list.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        _list.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is ImageConvertItem item) ProcessLauncher.OpenLocation(item.Output ?? item.Path);
        };
        _list.ToolTip = "双击打开所在位置";

        var hint = new TextBlock
        {
            Text = "把图片或文件夹拖到这里",
            Foreground = Views.DialogWindow.HintBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _items.CollectionChanged += (_, _) => hint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(folderRow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(folderRow);
        Children.Add(_status);
        Children.Add(new Grid { Children = { _list, hint } });
    }

    void Pick()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "图片|" + string.Join(";", InputExtensions.Select(e => "*" + e)) + "|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dialog.FileNames);
    }

    void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    void AddPaths(IEnumerable<string> paths)
    {
        if (_running) return;
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try { files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)); }
                catch (Exception ex) { Ui.SetStatus(_status, ex.Message, true); }
            }
            else files.Add(path);
        }
        int added = 0;
        foreach (var file in files)
        {
            if (!InputExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            if (_items.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            _items.Add(new ImageConvertItem { Path = file, Info = Ui.FormatSize(new FileInfo(file).Length) });
            added++;
        }
        Ui.SetStatus(_status, added > 0 ? $"已添加 {added} 张图片，共 {_items.Count} 张" : "没有可转换的图片");
    }

    async void Start()
    {
        if (_running) return;
        var pending = _items.Where(i => i.State == "等待" || i.State.StartsWith("失败")).ToList();
        if (pending.Count == 0) { Ui.SetStatus(_status, _items.Count == 0 ? "请先添加图片" : "列表中的图片都已处理"); return; }
        if (!int.TryParse(_quality.Text, out var quality) || quality < 1 || quality > 100) { Ui.SetStatus(_status, "JPG 质量应为 1–100", true); return; }
        int maxSide = 0;
        if (_maxSide.Text.Trim().Length > 0 && (!int.TryParse(_maxSide.Text, out maxSide) || maxSide < 1)) { Ui.SetStatus(_status, "最长边应为正整数，或留空", true); return; }
        var folder = _folder.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { Ui.SetStatus(_status, "输出文件夹无效：" + ex.Message, true); return; }
        }
        var format = (string)_format.SelectedItem;

        _running = true;
        _start.IsEnabled = false;
        int done = 0;
        try
        {
            foreach (var item in pending)
            {
                item.State = "转换中…";
                try
                {
                    var (output, info, result) = await Task.Run(() => ImageConverter.Convert(item.Path, format, quality, maxSide, folder.Length > 0 ? folder : null));
                    item.Output = output;
                    item.Info = info;
                    item.Result = result;
                    item.State = "完成：" + Path.GetFileName(output);
                    done++;
                }
                catch (Exception ex) { item.State = "失败：" + ex.Message; }
            }
        }
        finally
        {
            _running = false;
            _start.IsEnabled = true;
        }
        Ui.SetStatus(_status, $"已转换 {done} / {pending.Count} 张图片");
    }
}

static class ImageConverter
{
    /// <returns>Output path, and "size, dimensions" descriptions of the source and result.</returns>
    public static (string Output, string Info, string Result) Convert(string path, string format, int quality, int maxSide, string? folder)
    {
        var bytes = File.ReadAllBytes(path);
        var source = Load(bytes);
        var info = $"{source.PixelWidth}×{source.PixelHeight}，{Ui.FormatSize(bytes.Length)}";

        BitmapSource image = source;
        if (maxSide > 0 && Math.Max(image.PixelWidth, image.PixelHeight) > maxSide)
            image = Scale(image, (double)maxSide / Math.Max(image.PixelWidth, image.PixelHeight));

        var ext = format switch { "JPG" => ".jpg", "TIFF" => ".tif", _ => "." + format.ToLowerInvariant() };
        var output = OutputPath(path, ext, folder);
        byte[] data = format == "ICO" ? EncodeIco(image) : Encode(image, format, quality);
        File.WriteAllBytes(output, data);
        return (output, info, $"{image.PixelWidth}×{image.PixelHeight}，{Ui.FormatSize(data.Length)}");
    }

    static BitmapSource Load(byte[] bytes)
    {
        // Pick the largest frame, which matters for .ico files
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    static BitmapSource Scale(BitmapSource image, double factor)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform(factor, factor));
        // TransformedBitmap is lazy; copy it so encoding does not rescale per frame
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }

    static string OutputPath(string path, string ext, string? folder)
    {
        var dir = folder ?? Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var output = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(output); i++) output = Path.Combine(dir, $"{name} ({i}){ext}");
        return output;
    }

    static byte[] Encode(BitmapSource image, string format, int quality)
    {
        BitmapEncoder encoder = format switch
        {
            "JPG" => new JpegBitmapEncoder { QualityLevel = quality },
            "BMP" => new BmpBitmapEncoder(),
            "GIF" => new GifBitmapEncoder(),
            "TIFF" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };
        // JPG and BMP have no alpha channel: blend onto white instead of letting transparent pixels go black
        if (format is "JPG" or "BMP") image = FlattenOnWhite(image);
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        image.CopyPixels(pixels, stride, 0);
        var rgb = new byte[w * 3 * h];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            int a = pixels[i + 3];
            for (int c = 0; c < 3; c++) rgb[j + c] = (byte)((pixels[i + c] * a + 255 * (255 - a) + 127) / 255);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        result.Freeze();
        return result;
    }

    /// <summary>Icon with PNG-compressed frames from 16 px up to 256 px, the image centred on a square.</summary>
    static byte[] EncodeIco(BitmapSource image)
    {
        int side = Math.Max(image.PixelWidth, image.PixelHeight);
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 }.Where(s => s <= Math.Max(side, 16)).ToList();
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            double factor = (double)size / side;
            var scaled = Math.Abs(factor - 1) < 1e-6 ? image : Scale(image, factor);
            var square = Pad(scaled, size);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(square));
            using var ms = new MemoryStream();
            png.Save(ms);
            frames.Add(ms.ToArray());
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
        return output.ToArray();
    }

    static BitmapSource Pad(BitmapSource image, int size)
    {
        int w = Math.Min(image.PixelWidth, size), h = Math.Min(image.PixelHeight, size);
        var src = new byte[image.PixelWidth * 4 * image.PixelHeight];
        image.CopyPixels(src, image.PixelWidth * 4, 0);
        var dst = new byte[size * 4 * size];
        int left = (size - w) / 2, top = (size - h) / 2;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * image.PixelWidth * 4, dst, ((top + y) * size + left) * 4, w * 4);
        var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, dst, size * 4);
        result.Freeze();
        return result;
    }
}
