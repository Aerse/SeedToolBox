using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>URL, Unicode escape, HTML entity and hex encoding.</summary>
sealed class EncodePage : DockPanel
{
    static readonly string[] Modes = { "URL 编码", "Unicode (\\uXXXX)", "HTML 实体", "Hex (UTF-8)" };

    readonly TextBox _plain = Ui.Area(wrap: true);
    readonly TextBox _encoded = Ui.Area(wrap: true);
    readonly ComboBox _mode = new() { Width = 170, ItemsSource = Modes, SelectedIndex = 0 };
    readonly CheckBox _all = new() { Content = "ASCII 字符也编码", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    public EncodePage()
    {
        var header = Ui.Header("编码转换", "URL 编码、Unicode 转义、HTML 实体、十六进制互转");
        var toolbar = Ui.Row(
            Ui.Label("方式"), _mode, Ui.Label("", 16), _all,
            Ui.Button("编码 →", Encode, accent: true),
            Ui.Button("← 解码", Decode),
            Ui.Button("清空", () => { _plain.Clear(); _encoded.Clear(); _status.Text = ""; }));

        Button Copy(TextBox box)
        {
            var b = Ui.Button("复制", () => { if (box.Text.Length > 0) ScreenToolService.CopyText(box.Text); });
            b.Margin = new Thickness(0);
            return b;
        }

        var body = Ui.Columns(Ui.Titled("原文", _plain, Copy(_plain)), Ui.Titled("编码结果", _encoded, Copy(_encoded)));
        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);
    }

    void Encode()
    {
        var text = _plain.Text;
        bool all = _all.IsChecked == true;
        _encoded.Text = _mode.SelectedIndex switch
        {
            0 => all ? string.Concat(Encoding.UTF8.GetBytes(text).Select(b => "%" + b.ToString("X2"))) : Uri.EscapeDataString(text),
            1 => string.Concat(text.Select(c => all || c > 127 ? "\\u" + ((int)c).ToString("x4") : c.ToString())),
            2 => all ? string.Concat(Runes(text).Select(r => $"&#x{r:X};")) : HtmlEncodeNonAscii(text),
            _ => string.Join(" ", Encoding.UTF8.GetBytes(text).Select(b => b.ToString("X2"))),
        };
        Ui.SetStatus(_status, $"{text.Length:N0} → {_encoded.Text.Length:N0} 字符");
    }

    void Decode()
    {
        var text = _encoded.Text;
        try
        {
            _plain.Text = _mode.SelectedIndex switch
            {
                0 => Uri.UnescapeDataString(text.Replace('+', ' ')),
                1 => UnescapeUnicode(text),
                2 => WebUtility.HtmlDecode(text),
                _ => HexDecode(text),
            };
            Ui.SetStatus(_status, $"{text.Length:N0} → {_plain.Text.Length:N0} 字符");
        }
        catch (FormatException ex)
        {
            Ui.SetStatus(_status, ex.Message, true);
        }
    }

    static int[] Runes(string text)
    {
        var list = new System.Collections.Generic.List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                list.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else list.Add(text[i]);
        }
        return list.ToArray();
    }

    /// <summary>Escapes the HTML-special characters and anything outside ASCII.</summary>
    static string HtmlEncodeNonAscii(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var r in Runes(text))
        {
            switch (r)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default:
                    if (r > 127) sb.Append("&#").Append(r).Append(';');
                    else sb.Append((char)r);
                    break;
            }
        }
        return sb.ToString();
    }

    static string UnescapeUnicode(string text) =>
        Regex.Replace(text, @"\\u\{([0-9a-fA-F]{1,6})\}|\\u([0-9a-fA-F]{4})|\\x([0-9a-fA-F]{2})|&#x([0-9a-fA-F]+);|&#(\d+);", m =>
        {
            int code = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)
                : m.Groups[2].Success ? int.Parse(m.Groups[2].Value, NumberStyles.HexNumber)
                : m.Groups[3].Success ? int.Parse(m.Groups[3].Value, NumberStyles.HexNumber)
                : m.Groups[4].Success ? int.Parse(m.Groups[4].Value, NumberStyles.HexNumber)
                : int.Parse(m.Groups[5].Value);
            // \uD83D\uDE00 pairs decode one half at a time, which string concatenation joins back up
            return code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32(code);
        });

    static string HexDecode(string text)
    {
        var hex = Regex.Replace(text, @"0x|\\x|[\s,:\-]", "", RegexOptions.IgnoreCase);
        if (hex.Length % 2 != 0 || !Regex.IsMatch(hex, "^[0-9a-fA-F]*$")) throw new FormatException("不是有效的十六进制");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
        return TextFiles.Decode(bytes, out _);
    }
}
