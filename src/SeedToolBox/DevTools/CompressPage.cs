using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using NUglify;
using NUglify.JavaScript;
using SeedToolBox.Core;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

sealed class CompressItem : ObservableObject
{
    string _state = "等待";
    string _after = "";
    string _ratio = "";
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Before { get; set; } = "";
    public string After { get => _after; set => Set(ref _after, value); }
    public string Ratio { get => _ratio; set => Set(ref _ratio, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public string? Output { get; set; }
}

/// <summary>Minifies JS/CSS/HTML/SVG/JSON, shrinks PNGs to an 8-bit palette, re-compresses JPEGs, and packs or extracts zip files.</summary>
sealed class CompressPage : DockPanel
{
    static readonly string[] Extensions = { ".js", ".css", ".html", ".htm", ".svg", ".json", ".png", ".jpg", ".jpeg" };

    readonly ObservableCollection<CompressItem> _items = new();
    readonly ListView _list = new();
    readonly RadioButton _suffix = new() { Content = "另存为 .min 文件", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly RadioButton _overwrite = new() { Content = "覆盖原文件", Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _keepComments = new() { Content = "保留 /*! */ 版权注释", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBox _jpegQuality = Ui.Field(50);
    readonly TextBlock _status = Ui.Status();
    readonly Button _start;
    CancellationTokenSource? _cancel;

    readonly ObservableCollection<string> _zipItems = new();
    readonly ListBox _zipList = new() { SelectionMode = SelectionMode.Extended };
    readonly ComboBox _zipLevel = new() { Width = 100, ItemsSource = new[] { "标准压缩", "最快", "仅存储" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 16, 0) };
    bool _zipping;

    public CompressPage()
    {
        var header = Ui.Header("文件压缩", "JS / CSS / HTML / SVG / JSON 去除空白与注释，PNG 转为 8 位调色板，JPG 按质量重新压缩（仅在变小时保留）；也可打包或解压 ZIP");
        _start = Ui.Button("开始压缩", Start, accent: true);
        _jpegQuality.Text = "80";
        var toolbar = Ui.Row(
            Ui.Button("添加文件", PickFiles), _suffix, _overwrite, _keepComments,
            Ui.Label("JPG 质量"), _jpegQuality, Ui.Label("", 16), _start,
            Ui.Button("清空列表", () => { if (_cancel == null) { _items.Clear(); _status.Text = ""; } }));

        _list.ItemsSource = _items;
        _list.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "文件", Width = 300, DisplayMemberBinding = new Binding(nameof(CompressItem.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "原大小", Width = 90, DisplayMemberBinding = new Binding(nameof(CompressItem.Before)) });
        view.Columns.Add(new GridViewColumn { Header = "压缩后", Width = 90, DisplayMemberBinding = new Binding(nameof(CompressItem.After)) });
        view.Columns.Add(new GridViewColumn { Header = "节省", Width = 70, DisplayMemberBinding = new Binding(nameof(CompressItem.Ratio)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 200, DisplayMemberBinding = new Binding(nameof(CompressItem.State)) });
        _list.View = view;
        Ui.FileDrop(_list, AddPaths);
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is CompressItem item) ProcessLauncher.OpenLocation(item.Output ?? item.Path);
        };
        _list.ToolTip = "双击打开所在位置";

        var hint = new TextBlock
        {
            Text = "把文件或文件夹拖到这里",
            Foreground = Views.DialogWindow.HintBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _items.CollectionChanged += (_, _) => hint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var optimize = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        SetDock(toolbar, Dock.Top);
        optimize.Children.Add(toolbar);
        optimize.Children.Add(new Grid { Children = { _list, hint } });

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "压缩优化", Content = optimize });
        tabs.Items.Add(new TabItem { Header = "ZIP 打包 / 解压", Content = ZipPanel() });

        SetDock(header, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(_status);
        Children.Add(tabs);
    }

    FrameworkElement ZipPanel()
    {
        var toolbar = Ui.Row(
            Ui.Button("添加文件", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddZipPaths(dialog.FileNames);
            }),
            Ui.Button("添加文件夹", () =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddZipPaths(new[] { dialog.SelectedPath });
            }),
            Ui.Button("移除选中", () => { foreach (var p in _zipList.SelectedItems.Cast<string>().ToList()) _zipItems.Remove(p); }),
            Ui.Button("清空", () => _zipItems.Clear()),
            Ui.Label("", 8), Ui.Label("压缩级别"), _zipLevel,
            Ui.Button("打包为 ZIP…", Pack, accent: true),
            Ui.Button("解压 ZIP…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "ZIP 文件|*.zip|所有文件|*.*" };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) Extract(dialog.FileNames);
            }));
        _zipList.ItemsSource = _zipItems;
        _zipList.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["ControlBorderBrush"];
        _zipList.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) foreach (var p in _zipList.SelectedItems.Cast<string>().ToList()) _zipItems.Remove(p); };
        Ui.FileDrop(_zipList, AddZipPaths);
        var hint = new TextBlock
        {
            Text = "把要打包的文件或文件夹拖到这里；解压请点「解压 ZIP…」",
            Foreground = Views.DialogWindow.HintBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _zipItems.CollectionChanged += (_, _) => hint.Visibility = _zipItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var panel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        panel.Children.Add(new Grid { Children = { _zipList, hint } });
        return panel;
    }

    void AddZipPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if ((File.Exists(path) || Directory.Exists(path)) && !_zipItems.Contains(path, StringComparer.OrdinalIgnoreCase)) _zipItems.Add(path);
    }

    async void Pack()
    {
        if (_zipping) return;
        if (_zipItems.Count == 0) { Ui.SetStatus(_status, "请先添加要打包的文件或文件夹", true); return; }
        var first = _zipItems[0];
        var name = _zipItems.Count == 1 ? Path.GetFileNameWithoutExtension(first.TrimEnd('\\')) : "archive";
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = name + ".zip", Filter = "ZIP 文件|*.zip", InitialDirectory = Path.GetDirectoryName(first.TrimEnd('\\')) };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = dialog.FileName;
        var level = _zipLevel.SelectedIndex switch { 1 => CompressionLevel.Fastest, 2 => CompressionLevel.NoCompression, _ => CompressionLevel.Optimal };
        var sources = _zipItems.ToList();
        _zipping = true;
        Ui.SetStatus(_status, "打包中…");
        try
        {
            var (count, size) = await Task.Run(() => ZipTools.Pack(sources, target, level));
            Ui.SetStatus(_status, $"已打包 {count} 个文件到 {target}（{Ui.FormatSize(size)}）");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "打包失败：" + ex.Message, true); }
        finally { _zipping = false; }
    }

    async void Extract(string[] zips)
    {
        if (_zipping) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "解压到（会在其中为每个 ZIP 新建同名文件夹）", SelectedPath = Path.GetDirectoryName(zips[0]) };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var folder = dialog.SelectedPath;
        _zipping = true;
        Ui.SetStatus(_status, "解压中…");
        try
        {
            var results = await Task.Run(() => zips.Select(z => ZipTools.Extract(z, folder)).ToList());
            Ui.SetStatus(_status, $"已解压 {results.Sum(r => r.Count)} 个文件到 " + string.Join("；", results.Select(r => r.Folder)));
            if (results.Count == 1) ProcessLauncher.OpenLocation(results[0].Folder);
        }
        catch (Exception ex) { Ui.SetStatus(_status, "解压失败：" + ex.Message, true); }
        finally { _zipping = false; }
    }

    void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "可压缩的文件|" + string.Join(";", Extensions.Select(e => "*" + e)) };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dialog.FileNames);
    }

    void AddPaths(IEnumerable<string> paths)
    {
        if (_cancel != null) return;
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
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!Extensions.Contains(ext) || IsMinified(file) || _items.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            _items.Add(new CompressItem { Path = file, Before = Ui.FormatSize(new FileInfo(file).Length) });
            added++;
        }
        Ui.SetStatus(_status, added > 0 ? $"已添加 {added} 个文件，共 {_items.Count} 个" : "没有可压缩的文件（已跳过 .min 文件）");
    }

    static bool IsMinified(string file) => Path.GetFileNameWithoutExtension(file).EndsWith(".min", StringComparison.OrdinalIgnoreCase);

    async void Start()
    {
        if (_cancel != null) { _cancel.Cancel(); return; }
        var pending = _items.Where(i => i.State == "等待" || i.State.StartsWith("失败")).ToList();
        if (pending.Count == 0) { Ui.SetStatus(_status, _items.Count == 0 ? "请先添加文件" : "列表中的文件都已处理"); return; }
        if (_overwrite.IsChecked == true && MessageBox.Show(Window.GetWindow(this), $"将直接覆盖 {pending.Count} 个原文件，无法撤销。继续？", "文件压缩",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        if (!int.TryParse(_jpegQuality.Text, out var quality) || quality < 1 || quality > 100) { Ui.SetStatus(_status, "JPG 质量应为 1–100", true); return; }
        bool overwrite = _overwrite.IsChecked == true;
        bool keepComments = _keepComments.IsChecked == true;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _start.Content = "停止";
        long before = 0, after = 0;
        int done = 0;
        try
        {
            foreach (var item in pending)
            {
                if (token.IsCancellationRequested) break;
                item.State = "压缩中…";
                try
                {
                    var (b, a, output, kept) = await Task.Run(() => Compress(item.Path, overwrite, keepComments, quality), token);
                    before += b;
                    after += a;
                    item.Output = output;
                    item.After = Ui.FormatSize(a);
                    item.Ratio = b > 0 ? $"{(b - a) * 100.0 / b:0.#}%" : "";
                    item.State = kept ? "完成" : "已是最优，未改动";
                    done++;
                }
                catch (OperationCanceledException) { item.State = "等待"; }
                catch (Exception ex) { item.State = "失败：" + ex.Message; }
            }
        }
        finally
        {
            _cancel = null;
            _start.Content = "开始压缩";
        }
        Ui.SetStatus(_status, $"处理 {done} 个文件：{Ui.FormatSize(before)} → {Ui.FormatSize(after)}"
            + (before > 0 ? $"，节省 {(before - after) * 100.0 / before:0.#}%" : "") + (token.IsCancellationRequested ? "（已停止）" : ""));
    }

    static string OutputPath(string path, bool overwrite) =>
        overwrite ? path : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".min" + Path.GetExtension(path));

    /// <returns>Sizes before and after, where the result went, and whether it was written.</returns>
    static (long Before, long After, string Output, bool Kept) Compress(string path, bool overwrite, bool keepComments, int quality)
    {
        var original = File.ReadAllBytes(path);
        var output = OutputPath(path, overwrite);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] result = ext switch
        {
            ".png" => CompressPng(original),
            ".jpg" or ".jpeg" => CompressJpeg(original, quality),
            _ => MinifyText(original, ext, keepComments),
        };

        if (result.Length >= original.Length)
        {
            // Never make a file bigger; the .min copy still gets written so references resolve
            if (!overwrite) File.WriteAllBytes(output, original);
            return (original.Length, original.Length, output, false);
        }
        File.WriteAllBytes(output, result);
        return (original.Length, result.Length, output, true);
    }

    static byte[] MinifyText(byte[] bytes, string ext, bool keepComments)
    {
        var text = TextFiles.Decode(bytes, out var encoding);
        if (ext == ".json") return TextFiles.Encode(JToken.Parse(text).ToString(Newtonsoft.Json.Formatting.None), encoding);
        if (ext == ".svg") return TextFiles.Encode(MinifySvg(text, keepComments), encoding);
        if (ext is ".html" or ".htm")
        {
            var html = Uglify.Html(text, new NUglify.Html.HtmlSettings { RemoveComments = !keepComments });
            var htmlError = html.Errors.FirstOrDefault(e => e.IsError);
            if (htmlError != null) throw new InvalidDataException($"第 {htmlError.StartLine} 行：{htmlError.Message}");
            return TextFiles.Encode(html.Code, encoding);
        }
        var settings = new CodeSettings { PreserveImportantComments = keepComments };
        UglifyResult result = ext == ".css"
            ? Uglify.Css(text, new NUglify.Css.CssSettings { CommentMode = keepComments ? NUglify.Css.CssComment.Important : NUglify.Css.CssComment.None }, settings)
            : Uglify.Js(text, settings);
        var error = result.Errors.FirstOrDefault(e => e.IsError);
        if (error != null) throw new InvalidDataException($"第 {error.StartLine} 行：{error.Message}");
        return TextFiles.Encode(result.Code, encoding);
    }

    /// <summary>Drops comments and whitespace-only text between elements; text inside elements is kept.</summary>
    static string MinifySvg(string text, bool keepComments)
    {
        var doc = XDocument.Parse(text, LoadOptions.None);
        if (!keepComments) doc.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());
        var declaration = doc.Declaration != null ? doc.Declaration + "" : "";
        return declaration + doc.DocumentType + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    static byte[] CompressJpeg(byte[] bytes, int quality)
    {
        var decoder = new JpegBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        BitmapMetadata? metadata = null;
        try { metadata = (frame.Metadata as BitmapMetadata)?.Clone(); } catch { }
        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, frame.ColorContexts));
        using var output = new MemoryStream();
        try { encoder.Save(output); }
        catch (Exception) when (metadata != null)
        {
            // Some metadata blocks cannot be written back; keep the pixels at least
            output.SetLength(0);
            encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(output);
        }
        return output.ToArray();
    }

    static byte[] CompressPng(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var source = new Bitmap(input);
        using var argb = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb)) g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        using var quantized = new nQuant.WuQuantizer().QuantizeImage(argb, 10, 70);
        using var output = new MemoryStream();
        quantized.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}

static class ZipTools
{
    public static (int Count, long Size) Pack(IList<string> sources, string target, CompressionLevel level)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"stb{Guid.NewGuid():N}.zip");
        int count = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var source in sources)
            {
                if (File.Exists(source))
                {
                    Add(zip, source, Path.GetFileName(source));
                    continue;
                }
                var root = source.TrimEnd('\\');
                var baseName = Path.GetFileName(root);
                var parent = Path.GetDirectoryName(root) ?? root;
                bool any = false;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                    Add(zip, file, file.Substring(parent.Length).TrimStart('\\'));
                    any = true;
                }
                // Keep empty folders
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
                    zip.CreateEntry(dir.Substring(parent.Length).TrimStart('\\').Replace('\\', '/') + "/");
                if (!any && !Directory.EnumerateFileSystemEntries(root).Any()) zip.CreateEntry((baseName.Length > 0 ? baseName : "root") + "/");
            }
        }
        if (File.Exists(target)) File.Delete(target);
        File.Move(temp, target);
        return (count, new FileInfo(target).Length);

        void Add(ZipArchive zip, string file, string entryName)
        {
            entryName = entryName.Replace('\\', '/');
            var unique = entryName;
            for (int i = 1; !used.Add(unique); i++)
                unique = Path.GetDirectoryName(entryName)!.Replace('\\', '/').TrimEnd('/') is { Length: > 0 } dir
                    ? $"{dir}/{Path.GetFileNameWithoutExtension(entryName)} ({i}){Path.GetExtension(entryName)}"
                    : $"{Path.GetFileNameWithoutExtension(entryName)} ({i}){Path.GetExtension(entryName)}";
            zip.CreateEntryFromFile(file, unique, level);
            count++;
        }
    }

    /// <summary>Extracts into a new folder named after the zip, refusing entries that would escape it.</summary>
    public static (int Count, string Folder) Extract(string zipPath, string parent)
    {
        var name = Path.GetFileNameWithoutExtension(zipPath);
        var folder = Path.Combine(parent, name);
        for (int i = 1; Directory.Exists(folder) || File.Exists(folder); i++) folder = Path.Combine(parent, $"{name} ({i})");
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        int count = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(folder, entry.FullName.Replace('/', '\\')));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("压缩包包含不安全的路径：" + entry.FullName);
            if (entry.FullName.EndsWith("/") || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: false);
            count++;
        }
        return (count, folder);
    }
}
