using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Text and files to and from Base64, Base32 and Base58.</summary>
sealed class Base64TextPage : DockPanel
{
    const long MaxFile = 20L * 1024 * 1024;

    readonly TextBox _text = Ui.Area(wrap: true);
    readonly TextBox _base64 = Ui.Area(wrap: true);
    readonly ComboBox _format = new() { Width = 90, ItemsSource = new[] { "Base64", "Base32", "Base58" }, SelectedIndex = 0 };
    readonly ComboBox _encoding = new() { Width = 110, ItemsSource = new[] { "UTF-8", "GBK", "UTF-16" }, SelectedIndex = 0 };
    readonly CheckBox _urlSafe = new() { Content = "URL 安全（-_ 无填充）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _hex = new() { Content = "十六进制视图", ToolTip = "解码结果按十六进制显示；解码出的不是文本时会自动切换", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    public Base64TextPage()
    {
        var header = Ui.Header("Base64 编解码", "文本或文件与 Base64 / Base32 / Base58 互转，二进制内容以十六进制显示");
        _format.SelectionChanged += (_, _) => _urlSafe.IsEnabled = _format.SelectedIndex == 0;
        var toolbar = Ui.Row(
            Ui.Label("格式"), _format, Ui.Label("", 16),
            Ui.Label("字符编码"), _encoding, Ui.Label("", 16), _urlSafe, _hex,
            Ui.Button("编码 →", Encode, accent: true),
            Ui.Button("← 解码", Decode),
            Ui.Button("清空", () => { _text.Clear(); _base64.Clear(); _status.Text = ""; }));

        Button Copy(TextBox box)
        {
            var b = Ui.Button("复制", () => { if (box.Text.Length > 0) ScreenToolService.CopyText(box.Text); });
            b.Margin = new Thickness(0);
            return b;
        }

        Ui.FileDrop(_text, files => Ui.LoadText(_text, files[0], _status));
        Ui.FileDrop(_base64, files => EncodeFile(files[0]));
        var fileIn = Ui.Button("文件 → 编码…", PickFile);
        var fileOut = Ui.Button("解码另存为文件…", SaveFile);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { fileIn, fileOut, Copy(_base64) } };
        var body = Ui.Columns(Ui.Titled("文本", _text, Copy(_text)), Ui.Titled("编码结果（可拖入任意文件）", _base64, right));
        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);
    }

    Encoding TextEncoding => _encoding.SelectedIndex switch
    {
        1 => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        2 => new UnicodeEncoding(false, false, true),
        _ => new UTF8Encoding(false, true),
    };

    string ToText(byte[] bytes) => _format.SelectedIndex switch
    {
        1 => ToBase32(bytes),
        2 => ToBase58(bytes),
        _ => _urlSafe.IsChecked == true ? Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') : Convert.ToBase64String(bytes),
    };

    byte[] FromText(string text) => _format.SelectedIndex switch
    {
        1 => FromBase32(text),
        2 => FromBase58(text),
        _ => FromBase64(text),
    };

    string FormatName => (string)_format.SelectedItem;

    void Encode()
    {
        try
        {
            var bytes = TextEncoding.GetBytes(_text.Text);
            var s = ToText(bytes);
            _base64.Text = s;
            Ui.SetStatus(_status, $"{bytes.Length:N0} 字节 → {s.Length:N0} 字符");
        }
        catch (EncoderFallbackException) { Ui.SetStatus(_status, $"文本里有 {_encoding.SelectedItem} 无法表示的字符", true); }
    }

    void Decode()
    {
        byte[] bytes;
        try { bytes = FromText(_base64.Text); }
        catch (FormatException) { Ui.SetStatus(_status, $"不是有效的 {FormatName}", true); return; }
        if (_hex.IsChecked != true)
        {
            try
            {
                var text = TextEncoding.GetString(bytes);
                if (!LooksBinary(text))
                {
                    _text.Text = text;
                    Ui.SetStatus(_status, $"解码得到 {bytes.Length:N0} 字节");
                    return;
                }
            }
            catch (DecoderFallbackException) { }
        }
        _text.Text = HexDump(bytes);
        Ui.SetStatus(_status, $"解码得到 {bytes.Length:N0} 字节{(_hex.IsChecked == true ? "" : "，不是有效文本，已按十六进制显示")}");
    }

    static bool LooksBinary(string text) => text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t' or '\f'));

    void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) EncodeFile(dialog.FileName);
    }

    void EncodeFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFile) { Ui.SetStatus(_status, "文件超过 20 MB，太大了", true); return; }
            var bytes = File.ReadAllBytes(path);
            _base64.Text = ToText(bytes);
            Ui.SetStatus(_status, $"{Path.GetFileName(path)}：{bytes.Length:N0} 字节 → {_base64.Text.Length:N0} 字符");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "读取失败：" + ex.Message, true); }
    }

    void SaveFile()
    {
        byte[] bytes;
        try { bytes = FromText(_base64.Text); }
        catch (FormatException) { Ui.SetStatus(_status, $"不是有效的 {FormatName}", true); return; }
        if (bytes.Length == 0) { Ui.SetStatus(_status, "没有可保存的内容", true); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "decoded" + GuessExtension(bytes), Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
            Ui.SetStatus(_status, $"已保存 {bytes.Length:N0} 字节到 {dialog.FileName}");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "保存失败：" + ex.Message, true); }
    }

    static string GuessExtension(byte[] b)
    {
        bool Starts(params byte[] magic) => b.Length >= magic.Length && magic.Select((m, i) => b[i] == m).All(x => x);
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Starts(0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Starts(0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (Starts(0x25, 0x50, 0x44, 0x46)) return ".pdf";
        if (Starts(0x50, 0x4B, 0x03, 0x04)) return ".zip";
        if (Starts(0x1F, 0x8B)) return ".gz";
        if (Starts(0x42, 0x4D)) return ".bmp";
        if (Starts(0x52, 0x49, 0x46, 0x46) && b.Length > 11 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        return ".bin";
    }

    /// <summary>Offset, 16 hex bytes and their ASCII, like xxd.</summary>
    static string HexDump(byte[] bytes)
    {
        const int limit = 64 * 1024;
        var sb = new StringBuilder();
        int length = Math.Min(bytes.Length, limit);
        for (int row = 0; row < length; row += 16)
        {
            sb.Append(row.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < length) sb.Append(bytes[row + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < length; i++)
            {
                byte c = bytes[row + i];
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            sb.Append('\n');
        }
        if (bytes.Length > limit) sb.Append($"…（只显示前 {limit / 1024} KB，共 {bytes.Length:N0} 字节）\n");
        return sb.ToString();
    }

    /// <summary>Accepts standard or URL-safe Base64, with or without padding, whitespace or a data: prefix.</summary>
    public static byte[] FromBase64(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = s.IndexOf(',');
            if (comma >= 0) s = s.Substring(comma + 1);
        }
        var sb = new StringBuilder(s.Length + 3);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c == '-' ? '+' : c == '_' ? '/' : c);
        }
        while (sb.Length % 4 != 0) sb.Append('=');
        return Convert.FromBase64String(sb.ToString());
    }

    // Base32 (RFC 4648)

    const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    static string ToBase32(byte[] bytes)
    {
        var sb = new StringBuilder((bytes.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        while (sb.Length % 8 != 0) sb.Append('=');
        return sb.ToString();
    }

    static byte[] FromBase32(string text)
    {
        var output = new MemoryStream();
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw == '=' || raw == '-') continue;
            int value = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (value < 0) throw new FormatException();
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.WriteByte((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    // Base58 (Bitcoin alphabet)

    const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    static string ToBase58(byte[] bytes)
    {
        // Big-endian, unsigned: the trailing zero byte keeps BigInteger positive
        var value = new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray());
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, Base58Alphabet[(int)(value % 58)]);
            value /= 58;
        }
        foreach (var b in bytes)
        {
            if (b != 0) break;
            sb.Insert(0, '1');
        }
        return sb.ToString();
    }

    static byte[] FromBase58(string text)
    {
        var s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        BigInteger value = 0;
        foreach (var c in s)
        {
            int digit = Base58Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException();
            value = value * 58 + digit;
        }
        var body = value.ToByteArray().Reverse().SkipWhile(b => b == 0);
        int zeros = s.TakeWhile(c => c == '1').Count();
        return Enumerable.Repeat((byte)0, zeros).Concat(body).ToArray();
    }
}
