using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SeedToolBox.Launcher;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

sealed class HashItem : ObservableObject
{
    string _hash = "", _state = "";
    public string Path { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public string SizeText => Size < 0 ? "" : Ui.FormatSize(Size);
    public string Hash { get => _hash; set => Set(ref _hash, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    /// <summary>Lowercase hex, whatever the display format.</summary>
    public string Hex { get; set; } = "";
}

/// <summary>MD5 / SHA / SHA3 / CRC / xxHash / HMAC of text or files, a check against an expected value, and batch hashing of folders.</summary>
sealed class HashPage : DockPanel
{
    static readonly string[] Names = { "MD5", "SHA1", "SHA256", "SHA384", "SHA512", "SHA3-256", "CRC32", "CRC64", "xxHash64", "HMAC-MD5", "HMAC-SHA1", "HMAC-SHA256", "HMAC-SHA512" };
    static readonly string[] BatchNames = { "SHA256", "MD5", "SHA1", "SHA384", "SHA512", "SHA3-256", "CRC32", "CRC64", "xxHash64" };

    readonly TextBox _input = Ui.Area(wrap: true);
    readonly TextBox[] _results = Names.Select(_ => Ui.Field()).ToArray();
    readonly FrameworkElement[] _rows = new FrameworkElement[Names.Length];
    readonly byte[]?[] _values = new byte[Names.Length][];
    readonly TextBox _expected = Ui.Field(420);
    readonly PasswordBox _key = new() { Width = 300, VerticalContentAlignment = VerticalAlignment.Center };
    readonly CheckBox _upper = new() { Content = "大写", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _base64 = new() { Content = "Base64 输出", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _file = new() { Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _status = Ui.Status();
    string? _path;
    CancellationTokenSource? _cancel;

    readonly ObservableCollection<HashItem> _items = new();
    readonly ListView _list = new();
    readonly TextBox _folder = Ui.Field(300);
    readonly ComboBox _algorithm = new() { Width = 110, ItemsSource = BatchNames, SelectedIndex = 0 };
    readonly CheckBox _recursive = new() { Content = "包含子文件夹", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _batchStatus = Ui.Status();
    readonly List<Button> _batchButtons = new();
    CancellationTokenSource? _batchCancel;
    string _batchAlgorithm = "SHA256";

    public HashPage()
    {
        var header = Ui.Header("哈希 / 校验", "计算文本或文件的 MD5、SHA 系列、SHA3、CRC、xxHash64 与 HMAC，比对期望值，或批量计算、校验整个文件夹");
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "文本 / 文件", Content = SinglePanel() });
        tabs.Items.Add(new TabItem { Header = "批量 / 文件夹", Content = BatchPanel() });
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(tabs);
    }

    FrameworkElement SinglePanel()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var toolbar = Ui.Row(Ui.Button("选择文件", Pick), Ui.Button("清空", Reset), _upper, _base64, _file);
        _upper.Click += (_, _) => { ShowValues(); Compare(); };
        _base64.Click += (_, _) => { ShowValues(); Compare(); };
        // A password box keeps the key out of the saved page state; there is no themed style for it, so borrow the brushes
        var res = Application.Current.Resources;
        _key.Foreground = (Brush)res["TextBrush"];
        _key.CaretBrush = (Brush)res["TextBrush"];
        _key.Background = Brushes.Transparent;
        _key.BorderBrush = (Brush)res["ControlBorderBrush"];
        _key.Padding = new Thickness(6, 5, 6, 5);
        _key.PasswordChanged += (_, _) => Recompute();
        var keyRow = Ui.Row(Ui.Label("HMAC 密钥"), _key, Ui.Label("", 8), new TextBlock { Text = "填写后计算 HMAC（按 UTF-8），密钥不会保存", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center });

        _input.TextChanged += (_, _) =>
        {
            if (_path != null) return;
            ComputeText();
        };
        Ui.FileDrop(_input, files => { if (File.Exists(files[0])) ComputeFile(files[0]); });

        var results = new StackPanel();
        for (int i = 0; i < Names.Length; i++)
        {
            var box = _results[i];
            box.IsReadOnly = true;
            box.FontFamily = Ui.Mono;
            var label = Ui.Label(Names[i]);
            label.Width = 96;
            var copy = Ui.Button("复制", () => { if (box.Text.Length > 0) ScreenToolService.CopyText(box.Text); });
            copy.Margin = new Thickness(8, 0, 0, 0);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(copy);
            row.Children.Add(box);
            if (Names[i].StartsWith("HMAC")) row.Visibility = Visibility.Collapsed;
            _rows[i] = row;
            results.Children.Add(row);
        }
        _expected.FontFamily = Ui.Mono;
        _expected.TextChanged += (_, _) => Compare();
        var label2 = Ui.Label("比对");
        label2.Width = 96;
        var compare = Ui.Row(label2, _expected, Ui.Label("", 8), new TextBlock { Text = "粘贴期望的哈希值（十六进制或 Base64），自动匹配算法", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center });
        compare.Margin = new Thickness(0, 4, 0, 0);
        var scroll = new ScrollViewer { Content = results, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };

        var hint = new TextBlock { Text = "输入文本，或把文件拖到这里", Foreground = Views.DialogWindow.HintBrush, Margin = new Thickness(10, 8, 0, 0), IsHitTestVisible = false };
        _input.TextChanged += (_, _) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(keyRow, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(compare, Dock.Bottom);
        DockPanel.SetDock(scroll, Dock.Bottom);
        panel.Children.Add(toolbar);
        panel.Children.Add(keyRow);
        panel.Children.Add(_status);
        panel.Children.Add(compare);
        panel.Children.Add(scroll);
        panel.Children.Add(new Grid { Children = { _input, hint } });
        return panel;
    }

    FrameworkElement BatchPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var browse = Ui.Button("浏览…", Browse);
        browse.Margin = new Thickness(8, 0, 16, 0);
        var start = Ui.Button("计算文件夹", HashFolder, accent: true);
        var verify = Ui.Button("用校验文件验证…", VerifyChecksumFile);
        _batchButtons.Add(start);
        _batchButtons.Add(verify);
        var row1 = Ui.Row(Ui.Label("文件夹"), _folder, browse, Ui.Label("算法"), _algorithm, Ui.Label("", 12), _recursive);
        var row2 = Ui.Row(start, verify, Ui.Button("停止", () => _batchCancel?.Cancel()), Ui.Label("", 16), Ui.Button("写出校验文件…", WriteChecksumFile), ListTools.ExportButton(_list, _batchStatus, "哈希列表.csv"));
        Ui.FileDrop(_folder, p => { if (Directory.Exists(p[0])) _folder.Text = p[0]; });

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "文件", Width = 300, DisplayMemberBinding = new Binding(nameof(HashItem.Path)) });
        view.Columns.Add(new GridViewColumn { Header = "大小", Width = 90, DisplayMemberBinding = new Binding(nameof(HashItem.SizeText)) });
        view.Columns.Add(new GridViewColumn { Header = "哈希", Width = 420, DisplayMemberBinding = new Binding(nameof(HashItem.Hash)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 90, DisplayMemberBinding = new Binding(nameof(HashItem.State)) });
        _list.View = view;
        ListTools.Sortable(_list);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is HashItem { FullPath.Length: > 0 } item && File.Exists(item.FullPath)) ProcessLauncher.OpenLocation(item.FullPath); };
        _list.ToolTip = "双击打开所在位置";

        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(row2, Dock.Top);
        DockPanel.SetDock(_batchStatus, Dock.Bottom);
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(_batchStatus);
        panel.Children.Add(_list);
        return panel;
    }

    string Format(byte[] value) => _base64.IsChecked == true ? Convert.ToBase64String(value) : _upper.IsChecked == true ? Hashes.Hex(value).ToUpperInvariant() : Hashes.Hex(value);

    void Pick()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ComputeFile(dialog.FileName);
    }

    void Reset()
    {
        _cancel?.Cancel();
        _path = null;
        _file.Text = "";
        _input.IsReadOnly = false;
        _input.Clear();
        Array.Clear(_values, 0, _values.Length);
        ShowValues();
        _status.Text = "";
    }

    void Recompute()
    {
        for (int i = 0; i < Names.Length; i++)
            if (Names[i].StartsWith("HMAC")) _rows[i].Visibility = _key.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_path != null) ComputeFile(_path);
        else ComputeText();
    }

    void ComputeText()
    {
        _cancel?.Cancel();
        if (_input.Text.Length == 0) { Array.Clear(_values, 0, _values.Length); ShowValues(); Compare(); return; }
        Show(Hashes.Compute(new MemoryStream(Encoding.UTF8.GetBytes(_input.Text)), Names, Key(), null, CancellationToken.None));
        Ui.SetStatus(_status, $"UTF-8 文本，{Encoding.UTF8.GetByteCount(_input.Text)} 字节");
    }

    byte[]? Key() => _key.Password.Length > 0 ? Encoding.UTF8.GetBytes(_key.Password) : null;

    async void ComputeFile(string path)
    {
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _path = path;
        _input.IsReadOnly = true;
        _input.Text = path;
        _file.Text = "文件模式，点清空返回文本";
        foreach (var r in _results) r.Text = "…";
        var key = Key();
        try
        {
            long size = new FileInfo(path).Length;
            var progress = new Progress<long>(done => Ui.SetStatus(_status, $"计算中… {done * 100 / Math.Max(1, size)}%"));
            var result = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                return Hashes.Compute(stream, Names, key, progress, cancel.Token);
            });
            if (cancel.IsCancellationRequested) return;
            Show(result);
            Ui.SetStatus(_status, $"{Path.GetFileName(path)}，{Ui.FormatSize(size)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Array.Clear(_values, 0, _values.Length);
            ShowValues();
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    void Show(byte[]?[] hashes)
    {
        hashes.CopyTo(_values, 0);
        ShowValues();
        Compare();
    }

    void ShowValues()
    {
        for (int i = 0; i < Names.Length; i++) _results[i].Text = _values[i] is { } v ? Format(v) : "";
    }

    void Compare()
    {
        var expected = _expected.Text.Trim().Replace(" ", "");
        foreach (var r in _results) r.ClearValue(Control.ForegroundProperty);
        if (expected.Length == 0) { _expected.ClearValue(Control.ForegroundProperty); return; }
        int match = Array.FindIndex(_values, v => v != null
            && (string.Equals(Hashes.Hex(v), expected, StringComparison.OrdinalIgnoreCase) || Convert.ToBase64String(v) == expected));
        var brush = (Brush)Application.Current.Resources[match >= 0 ? "AccentBrush" : "DangerBrush"];
        _expected.Foreground = brush;
        if (match >= 0)
        {
            _results[match].Foreground = brush;
            Ui.SetStatus(_status, $"✓ 与 {Names[match]} 一致");
        }
        else Ui.SetStatus(_status, "✗ 与所有结果都不一致", true);
    }

    void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    void HashFolder()
    {
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { Ui.SetStatus(_batchStatus, "请选择存在的文件夹", true); return; }
        var algorithm = (string)_algorithm.SelectedItem;
        List<HashItem> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", _recursive.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(f => new HashItem { FullPath = f, Path = Relative(folder, f), Size = new FileInfo(f).Length })
                .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) { Ui.SetStatus(_batchStatus, "读取文件夹失败：" + ex.Message, true); return; }
        _batchAlgorithm = algorithm;
        RunBatch(files, algorithm, null);
    }

    static string Relative(string folder, string path)
    {
        var root = folder.TrimEnd('\\', '/') + "\\";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
    }

    /// <summary>Hashes the items one by one; with <paramref name="expected"/> it also marks each one as matching or not.</summary>
    async void RunBatch(List<HashItem> items, string algorithm, Dictionary<HashItem, string>? expected)
    {
        _batchCancel?.Cancel();
        var cancel = _batchCancel = new CancellationTokenSource();
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        foreach (var b in _batchButtons) b.IsEnabled = false;
        int ok = 0, bad = 0, missing = 0;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Ui.SetStatus(_batchStatus, $"计算中… {i + 1}/{items.Count}  {item.Path}");
                if (!File.Exists(item.FullPath)) { item.State = "缺失"; missing++; continue; }
                try
                {
                    var hash = await Task.Run(() =>
                    {
                        using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                        return Hashes.Compute(stream, new[] { algorithm }, null, null, cancel.Token)[0]!;
                    });
                    item.Hex = Hashes.Hex(hash);
                    item.Hash = Format(hash);
                    if (expected == null) item.State = "完成";
                    else if (string.Equals(item.Hex, expected[item], StringComparison.OrdinalIgnoreCase)) { item.State = "✓ 一致"; ok++; }
                    else { item.State = "✗ 不一致"; bad++; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { item.State = "读取失败"; item.Hash = ex.Message; missing++; }
            }
            Ui.SetStatus(_batchStatus, expected == null
                ? $"已计算 {items.Count} 个文件的 {algorithm}"
                : $"{algorithm} 校验完成：一致 {ok}，不一致 {bad}，缺失或读取失败 {missing}", bad + missing > 0);
        }
        catch (OperationCanceledException) { Ui.SetStatus(_batchStatus, "已停止"); }
        finally { foreach (var b in _batchButtons) b.IsEnabled = true; }
    }

    static readonly Regex GnuLine = new(@"^\\?([0-9a-fA-F]{8,128})\s+[ *]?(.+)$");
    static readonly Regex BsdLine = new(@"^([\w-]+)\s*\((.+)\)\s*=\s*([0-9a-fA-F]+)$");

    void VerifyChecksumFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "校验文件|*.sha256;*.sha1;*.sha384;*.sha512;*.md5;*.txt;*.sum|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string[] lines;
        try { lines = File.ReadAllLines(dialog.FileName); }
        catch (Exception ex) { Ui.SetStatus(_batchStatus, "读取失败：" + ex.Message, true); return; }

        var baseDir = Path.GetDirectoryName(dialog.FileName)!;
        var entries = new List<(string Hash, string File)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            if (GnuLine.Match(line) is { Success: true } g) entries.Add((g.Groups[1].Value, g.Groups[2].Value.Trim()));
            else if (BsdLine.Match(line) is { Success: true } b) entries.Add((b.Groups[3].Value, b.Groups[2].Value.Trim()));
        }
        if (entries.Count == 0) { Ui.SetStatus(_batchStatus, "文件里没有可识别的校验行（hash  文件名）", true); return; }

        var algorithm = AlgorithmFor(Path.GetExtension(dialog.FileName), entries[0].Hash.Length);
        if (algorithm == null) { Ui.SetStatus(_batchStatus, $"无法从 {entries[0].Hash.Length} 位哈希判断算法", true); return; }
        _batchAlgorithm = algorithm;
        _algorithm.SelectedItem = algorithm;
        _folder.Text = baseDir;
        var expected = new Dictionary<HashItem, string>();
        var items = entries.Select(e =>
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, e.File.Replace('/', '\\')));
            var item = new HashItem { Path = e.File, FullPath = full, Size = File.Exists(full) ? new FileInfo(full).Length : -1 };
            expected[item] = e.Hash;
            return item;
        }).ToList();
        RunBatch(items, algorithm, expected);
    }

    static string? AlgorithmFor(string extension, int hexLength) => extension.ToLowerInvariant() switch
    {
        ".md5" => "MD5",
        ".sha1" => "SHA1",
        ".sha256" => "SHA256",
        ".sha384" => "SHA384",
        ".sha512" => "SHA512",
        _ => hexLength switch { 32 => "MD5", 40 => "SHA1", 64 => "SHA256", 96 => "SHA384", 128 => "SHA512", 8 => "CRC32", _ => null },
    };

    void WriteChecksumFile()
    {
        var done = _items.Where(i => i.Hex.Length > 0).ToList();
        if (done.Count == 0) { Ui.SetStatus(_batchStatus, "请先计算文件夹", true); return; }
        var extension = "." + _batchAlgorithm.ToLowerInvariant().Replace("-", "");
        var folder = _folder.Text.Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = (Directory.Exists(folder) ? new DirectoryInfo(folder).Name : "checksums") + extension,
            InitialDirectory = Directory.Exists(folder) ? folder : null,
            Filter = $"校验文件|*{extension}|所有文件|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var sb = new StringBuilder();
        foreach (var item in done)
        {
            sb.Append(item.Hex).Append(" *").Append(item.Path.Replace('\\', '/')).Append('\n');
        }
        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
            Ui.SetStatus(_batchStatus, $"已写出 {done.Count} 行到 {dialog.FileName}");
        }
        catch (Exception ex) { Ui.SetStatus(_batchStatus, "保存失败：" + ex.Message, true); }
    }
}

static class Hashes
{
    static HashAlgorithm? Create(string name, byte[]? key) => name switch
    {
        "MD5" => MD5.Create(),
        "SHA1" => SHA1.Create(),
        "SHA256" => SHA256.Create(),
        "SHA384" => SHA384.Create(),
        "SHA512" => SHA512.Create(),
        "SHA3-256" => new Sha3_256(),
        "CRC32" => new Crc32Hash(),
        "CRC64" => new Crc64Hash(),
        "xxHash64" => new XxHash64(),
        "HMAC-MD5" => key == null ? null : new HMACMD5(key),
        "HMAC-SHA1" => key == null ? null : new HMACSHA1(key),
        "HMAC-SHA256" => key == null ? null : new HMACSHA256(key),
        "HMAC-SHA512" => key == null ? null : new HMACSHA512(key),
        _ => throw new ArgumentException(name),
    };

    /// <summary>Reads the stream once and feeds every algorithm; HMAC entries are null without a key.</summary>
    public static byte[]?[] Compute(Stream stream, IList<string> names, byte[]? key, IProgress<long>? progress, CancellationToken cancel)
    {
        var algorithms = names.Select(n => Create(n, key)).ToArray();
        try
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (var a in algorithms) a?.TransformBlock(buffer, 0, read, null, 0);
                total += read;
                progress?.Report(total);
            }
            return algorithms.Select(a =>
            {
                if (a == null) return null;
                a.TransformFinalBlock(buffer, 0, 0);
                return a.Hash;
            }).ToArray();
        }
        finally
        {
            foreach (var a in algorithms) a?.Dispose();
        }
    }

    public static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
