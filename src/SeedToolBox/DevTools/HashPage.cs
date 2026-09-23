using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>MD5 / SHA / CRC32 of text or files, with a box to check against an expected value.</summary>
sealed class HashPage : DockPanel
{
    static readonly string[] Names = { "MD5", "SHA1", "SHA256", "SHA512", "CRC32" };

    readonly TextBox _input = Ui.Area(wrap: true);
    readonly TextBox[] _results = Names.Select(_ => Ui.Field()).ToArray();
    readonly TextBox _expected = Ui.Field(420);
    readonly CheckBox _upper = new() { Content = "大写", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _file = new() { Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _status = Ui.Status();
    string? _path;
    CancellationTokenSource? _cancel;

    public HashPage()
    {
        var header = Ui.Header("哈希 / 校验", "计算文本或文件的 MD5、SHA1、SHA256、SHA512、CRC32，并与期望值比对");
        var toolbar = Ui.Row(Ui.Button("选择文件", Pick), Ui.Button("清空", Reset), _upper, _file);
        _upper.Click += (_, _) => { foreach (var r in _results) r.Text = _upper.IsChecked == true ? r.Text.ToUpperInvariant() : r.Text.ToLowerInvariant(); Compare(); };

        _input.TextChanged += (_, _) =>
        {
            if (_path != null) return;
            ComputeText();
        };
        _input.PreviewDragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        _input.PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0])) ComputeFile(files[0]);
        };

        var results = new StackPanel();
        for (int i = 0; i < Names.Length; i++)
        {
            var box = _results[i];
            box.IsReadOnly = true;
            box.FontFamily = Ui.Mono;
            var label = Ui.Label(Names[i]);
            label.Width = 64;
            var copy = Ui.Button("复制", () => { if (box.Text.Length > 0) ScreenToolService.CopyText(box.Text); });
            copy.Margin = new Thickness(8, 0, 0, 0);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(copy);
            row.Children.Add(box);
            results.Children.Add(row);
        }
        _expected.FontFamily = Ui.Mono;
        _expected.TextChanged += (_, _) => Compare();
        var label2 = Ui.Label("比对");
        label2.Width = 64;
        results.Children.Add(Ui.Row(label2, _expected, Ui.Label("", 8), new TextBlock { Text = "粘贴期望的哈希值，自动匹配算法", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center }));
        results.Margin = new Thickness(0, 12, 0, 0);

        var hint = new TextBlock { Text = "输入文本，或把文件拖到这里", Foreground = Views.DialogWindow.HintBrush, Margin = new Thickness(10, 8, 0, 0), IsHitTestVisible = false };
        _input.TextChanged += (_, _) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        SetDock(results, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(results);
        Children.Add(new Grid { Children = { _input, hint } });
    }

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
        foreach (var r in _results) r.Clear();
        _status.Text = "";
    }

    void ComputeText()
    {
        _cancel?.Cancel();
        if (_input.Text.Length == 0) { foreach (var r in _results) r.Clear(); Compare(); return; }
        Show(Hashes.Compute(new MemoryStream(Encoding.UTF8.GetBytes(_input.Text)), null, CancellationToken.None));
        Ui.SetStatus(_status, $"UTF-8 文本，{Encoding.UTF8.GetByteCount(_input.Text)} 字节");
    }

    async void ComputeFile(string path)
    {
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _path = path;
        _input.IsReadOnly = true;
        _input.Text = path;
        _file.Text = "文件模式，点清空返回文本";
        foreach (var r in _results) r.Text = "…";
        long size = new FileInfo(path).Length;
        var progress = new Progress<long>(done => Ui.SetStatus(_status, $"计算中… {done * 100 / Math.Max(1, size)}%"));
        try
        {
            var result = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                return Hashes.Compute(stream, progress, cancel.Token);
            });
            if (cancel.IsCancellationRequested) return;
            Show(result);
            Ui.SetStatus(_status, $"{Path.GetFileName(path)}，{Ui.FormatSize(size)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var r in _results) r.Clear();
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    void Show(string[] hashes)
    {
        for (int i = 0; i < hashes.Length; i++) _results[i].Text = _upper.IsChecked == true ? hashes[i].ToUpperInvariant() : hashes[i];
        Compare();
    }

    void Compare()
    {
        var expected = _expected.Text.Trim().Replace(" ", "");
        foreach (var r in _results) r.ClearValue(Control.ForegroundProperty);
        if (expected.Length == 0) { _expected.ClearValue(Control.ForegroundProperty); return; }
        var match = _results.FirstOrDefault(r => string.Equals(r.Text, expected, StringComparison.OrdinalIgnoreCase));
        var brush = (Brush)Application.Current.Resources[match != null ? "AccentBrush" : "DangerBrush"];
        _expected.Foreground = brush;
        if (match != null)
        {
            match.Foreground = brush;
            Ui.SetStatus(_status, $"✓ 与 {Names[Array.IndexOf(_results, match)]} 一致");
        }
        else Ui.SetStatus(_status, "✗ 与所有结果都不一致", true);
    }
}

static class Hashes
{
    /// <summary>Reads the stream once and feeds every algorithm; returns lowercase hex in <see cref="HashPage"/> order.</summary>
    public static string[] Compute(Stream stream, IProgress<long>? progress, CancellationToken cancel)
    {
        HashAlgorithm[] algorithms = { MD5.Create(), SHA1.Create(), SHA256.Create(), SHA512.Create() };
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var a in algorithms) a.TransformBlock(buffer, 0, read, null, 0);
            crc = Crc32(crc, buffer, read);
            total += read;
            progress?.Report(total);
        }
        var result = algorithms.Select(a =>
        {
            a.TransformFinalBlock(buffer, 0, 0);
            var hex = Hex(a.Hash);
            a.Dispose();
            return hex;
        }).ToList();
        result.Add((crc ^ 0xFFFFFFFF).ToString("x8"));
        return result.ToArray();
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(uint crc, byte[] data, int count)
    {
        for (int i = 0; i < count; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
