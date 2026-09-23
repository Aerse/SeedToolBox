using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

/// <summary>Minifies JS/CSS with NUglify and shrinks PNGs to an 8-bit palette.</summary>
sealed class CompressPage : DockPanel
{
    static readonly string[] Extensions = { ".js", ".css", ".png" };

    readonly ObservableCollection<CompressItem> _items = new();
    readonly ListView _list = new();
    readonly RadioButton _suffix = new() { Content = "另存为 .min 文件", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly RadioButton _overwrite = new() { Content = "覆盖原文件", Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _keepComments = new() { Content = "保留 /*! */ 版权注释", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();
    readonly Button _start;
    CancellationTokenSource? _cancel;

    public CompressPage()
    {
        var header = Ui.Header("文件压缩", "拖入 JS / CSS / PNG 文件或文件夹，JS/CSS 去除空白与注释并混淆，PNG 转为 8 位调色板（仅在变小时保留）");
        _start = Ui.Button("开始压缩", Start, accent: true);
        var toolbar = Ui.Row(
            Ui.Button("添加文件", PickFiles), _suffix, _overwrite, _keepComments, _start,
            Ui.Button("清空列表", () => { if (_cancel == null) { _items.Clear(); _status.Text = ""; } }));

        _list.ItemsSource = _items;
        _list.AllowDrop = true;
        _list.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "文件", Width = 300, DisplayMemberBinding = new Binding(nameof(CompressItem.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "原大小", Width = 90, DisplayMemberBinding = new Binding(nameof(CompressItem.Before)) });
        view.Columns.Add(new GridViewColumn { Header = "压缩后", Width = 90, DisplayMemberBinding = new Binding(nameof(CompressItem.After)) });
        view.Columns.Add(new GridViewColumn { Header = "节省", Width = 70, DisplayMemberBinding = new Binding(nameof(CompressItem.Ratio)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 200, DisplayMemberBinding = new Binding(nameof(CompressItem.State)) });
        _list.View = view;
        _list.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        _list.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
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

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(new Grid { Children = { _list, hint } });
    }

    void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "JS / CSS / PNG|*.js;*.css;*.png" };
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
        Ui.SetStatus(_status, added > 0 ? $"已添加 {added} 个文件，共 {_items.Count} 个" : "没有可压缩的 .js / .css / .png 文件（已跳过 .min 文件）");
    }

    static bool IsMinified(string file) => Path.GetFileNameWithoutExtension(file).EndsWith(".min", StringComparison.OrdinalIgnoreCase);

    async void Start()
    {
        if (_cancel != null) { _cancel.Cancel(); return; }
        var pending = _items.Where(i => i.State == "等待" || i.State.StartsWith("失败")).ToList();
        if (pending.Count == 0) { Ui.SetStatus(_status, _items.Count == 0 ? "请先添加文件" : "列表中的文件都已处理"); return; }
        if (_overwrite.IsChecked == true && MessageBox.Show(Window.GetWindow(this), $"将直接覆盖 {pending.Count} 个原文件，无法撤销。继续？", "文件压缩",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

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
                    var (b, a, output, kept) = await Task.Run(() => Compress(item.Path, overwrite, keepComments), token);
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
    static (long Before, long After, string Output, bool Kept) Compress(string path, bool overwrite, bool keepComments)
    {
        var original = File.ReadAllBytes(path);
        var output = OutputPath(path, overwrite);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] result = ext == ".png" ? CompressPng(original) : MinifyText(original, ext, keepComments);

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
        var settings = new CodeSettings { PreserveImportantComments = keepComments };
        UglifyResult result = ext == ".css"
            ? Uglify.Css(text, new NUglify.Css.CssSettings { CommentMode = keepComments ? NUglify.Css.CssComment.Important : NUglify.Css.CssComment.None }, settings)
            : Uglify.Js(text, settings);
        var error = result.Errors.FirstOrDefault(e => e.IsError);
        if (error != null) throw new InvalidDataException($"第 {error.StartLine} 行：{error.Message}");
        return TextFiles.Encode(result.Code, encoding);
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
