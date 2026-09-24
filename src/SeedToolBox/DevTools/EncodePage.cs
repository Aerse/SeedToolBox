using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>URL, Unicode escape, HTML entity, hex, Punycode, Quoted-Printable and ROT13 encoding.</summary>
sealed class EncodePage : DockPanel
{
    static readonly string[] Modes = { "URL 编码", "Unicode (\\uXXXX)", "HTML 实体", "Hex", "HTML 命名实体", "Punycode（国际化域名）", "Quoted-Printable", "ROT13" };

    readonly TextBox _plain = Ui.Area(wrap: true);
    readonly TextBox _encoded = Ui.Area(wrap: true);
    readonly ComboBox _mode = new() { Width = 190, ItemsSource = Modes, SelectedIndex = 0 };
    readonly ComboBox _encoding = new() { Width = 90, ItemsSource = new[] { "UTF-8", "GBK", "UTF-16" }, SelectedIndex = 0, ToolTip = "URL 编码、Hex 和 Quoted-Printable 使用的字符编码" };
    readonly CheckBox _all = new() { Content = "ASCII 字符也编码", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    public EncodePage()
    {
        var header = Ui.Header("编码转换", "URL 编码、Unicode 转义、HTML 实体、十六进制、Punycode、Quoted-Printable、ROT13 互转");
        var toolbar = Ui.Row(
            Ui.Label("方式"), _mode, Ui.Label("", 16),
            Ui.Label("字符编码"), _encoding, Ui.Label("", 16), _all,
            Ui.Button("编码 →", Encode, accent: true),
            Ui.Button("← 解码", Decode),
            Ui.Button("清空", () => { _plain.Clear(); _encoded.Clear(); _status.Text = ""; }));

        Button Copy(TextBox box)
        {
            var b = Ui.Button("复制", () => { if (box.Text.Length > 0) ScreenToolService.CopyText(box.Text); });
            b.Margin = new Thickness(0);
            return b;
        }

        Ui.FileDrop(_plain, files => Ui.LoadText(_plain, files[0], _status));
        var body = Ui.Columns(Ui.Titled("原文", _plain, Copy(_plain)), Ui.Titled("编码结果", _encoded, Copy(_encoded)));
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
        1 => Encoding.GetEncoding(936),
        2 => Encoding.Unicode,
        _ => new UTF8Encoding(false),
    };

    void Encode()
    {
        var text = _plain.Text;
        bool all = _all.IsChecked == true;
        var enc = TextEncoding;
        try
        {
            _encoded.Text = _mode.SelectedIndex switch
            {
                0 => UrlEncode(text, enc, all),
                1 => string.Concat(text.Select(c => all || c > 127 ? "\\u" + ((int)c).ToString("x4") : c.ToString())),
                2 => all ? string.Concat(Runes(text).Select(r => $"&#x{r:X};")) : HtmlEncodeNonAscii(text),
                3 => string.Join(" ", enc.GetBytes(text).Select(b => b.ToString("X2"))),
                4 => HtmlEncodeNamed(text, all),
                5 => PunycodeEncode(text),
                6 => QuotedPrintableEncode(text, enc),
                _ => Rot13(text),
            };
            Ui.SetStatus(_status, $"{text.Length:N0} → {_encoded.Text.Length:N0} 字符");
        }
        catch (ArgumentException ex)
        {
            Ui.SetStatus(_status, "编码失败：" + ex.Message, true);
        }
    }

    void Decode()
    {
        var text = _encoded.Text;
        var enc = TextEncoding;
        try
        {
            _plain.Text = _mode.SelectedIndex switch
            {
                0 => UrlDecode(text, enc),
                1 => UnescapeUnicode(text),
                2 or 4 => WebUtility.HtmlDecode(text),
                3 => HexDecode(text, _encoding.SelectedIndex == 0 ? null : enc),
                5 => PunycodeDecode(text),
                6 => QuotedPrintableDecode(text, enc),
                _ => Rot13(text),
            };
            Ui.SetStatus(_status, $"{text.Length:N0} → {_plain.Text.Length:N0} 字符");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Ui.SetStatus(_status, ex.Message, true);
        }
    }

    static int[] Runes(string text)
    {
        var list = new List<int>();
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

    // URL

    static string UrlEncode(string text, Encoding enc, bool all)
    {
        if (!all && enc is UTF8Encoding) return Uri.EscapeDataString(text);
        var sb = new StringBuilder();
        foreach (var r in Runes(text))
        {
            var s = char.ConvertFromUtf32(r);
            if (!all && r < 128 && (char.IsLetterOrDigit((char)r) || "-._~".IndexOf((char)r) >= 0)) sb.Append(s);
            else foreach (var b in enc.GetBytes(s)) sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Decodes %XX runs as bytes in the chosen encoding, so %C4%E3 works for GBK.</summary>
    static string UrlDecode(string text, Encoding enc)
    {
        var sb = new StringBuilder();
        var bytes = new List<byte>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(byte.Parse(text.Substring(i + 1, 2), NumberStyles.HexNumber));
                i += 2;
                continue;
            }
            if (bytes.Count > 0) { sb.Append(enc.GetString(bytes.ToArray())); bytes.Clear(); }
            sb.Append(text[i] == '+' ? ' ' : text[i]);
        }
        if (bytes.Count > 0) sb.Append(enc.GetString(bytes.ToArray()));
        return sb.ToString();
    }

    // HTML

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

    static readonly Dictionary<int, string> NamedEntities = BuildEntities();

    static Dictionary<int, string> BuildEntities()
    {
        // Latin-1 (160-255) in order, then the common symbols from HTML 4
        var latin1 = ("nbsp iexcl cent pound curren yen brvbar sect uml copy ordf laquo not shy reg macr deg plusmn sup2 sup3 acute micro para middot " +
            "cedil sup1 ordm raquo frac14 frac12 frac34 iquest Agrave Aacute Acirc Atilde Auml Aring AElig Ccedil Egrave Eacute Ecirc Euml Igrave Iacute " +
            "Icirc Iuml ETH Ntilde Ograve Oacute Ocirc Otilde Ouml times Oslash Ugrave Uacute Ucirc Uuml Yacute THORN szlig agrave aacute acirc atilde auml " +
            "aring aelig ccedil egrave eacute ecirc euml igrave iacute icirc iuml eth ntilde ograve oacute ocirc otilde ouml divide oslash ugrave uacute ucirc " +
            "uuml yacute thorn yuml").Split(' ');
        var map = new Dictionary<int, string> { ['"'] = "quot", ['&'] = "amp", ['<'] = "lt", ['>'] = "gt", ['\''] = "apos" };
        for (int i = 0; i < latin1.Length; i++) map[160 + i] = latin1[i];
        var greek = "Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota Kappa Lambda Mu Nu Xi Omicron Pi Rho _ Sigma Tau Upsilon Phi Chi Psi Omega".Split(' ');
        for (int i = 0; i < greek.Length; i++)
        {
            if (greek[i] == "_") continue;
            map[0x391 + i] = greek[i];
            map[0x3B1 + i] = greek[i].ToLowerInvariant();
        }
        map[0x3C2] = "sigmaf";
        var others = new (int, string)[]
        {
            (0x152, "OElig"), (0x153, "oelig"), (0x160, "Scaron"), (0x161, "scaron"), (0x178, "Yuml"), (0x192, "fnof"), (0x2C6, "circ"), (0x2DC, "tilde"),
            (0x2002, "ensp"), (0x2003, "emsp"), (0x2009, "thinsp"), (0x200C, "zwnj"), (0x200D, "zwj"), (0x200E, "lrm"), (0x200F, "rlm"),
            (0x2013, "ndash"), (0x2014, "mdash"), (0x2018, "lsquo"), (0x2019, "rsquo"), (0x201A, "sbquo"), (0x201C, "ldquo"), (0x201D, "rdquo"),
            (0x201E, "bdquo"), (0x2020, "dagger"), (0x2021, "Dagger"), (0x2022, "bull"), (0x2026, "hellip"), (0x2030, "permil"), (0x2032, "prime"),
            (0x2033, "Prime"), (0x2039, "lsaquo"), (0x203A, "rsaquo"), (0x203E, "oline"), (0x2044, "frasl"), (0x20AC, "euro"), (0x2122, "trade"),
            (0x2190, "larr"), (0x2191, "uarr"), (0x2192, "rarr"), (0x2193, "darr"), (0x2194, "harr"), (0x21D0, "lArr"), (0x21D2, "rArr"), (0x21D4, "hArr"),
            (0x2200, "forall"), (0x2202, "part"), (0x2203, "exist"), (0x2205, "empty"), (0x2207, "nabla"), (0x2208, "isin"), (0x2209, "notin"),
            (0x220F, "prod"), (0x2211, "sum"), (0x2212, "minus"), (0x221A, "radic"), (0x221D, "prop"), (0x221E, "infin"), (0x2227, "and"), (0x2228, "or"),
            (0x2229, "cap"), (0x222A, "cup"), (0x222B, "int"), (0x2234, "there4"), (0x223C, "sim"), (0x2245, "cong"), (0x2248, "asymp"), (0x2260, "ne"),
            (0x2261, "equiv"), (0x2264, "le"), (0x2265, "ge"), (0x2282, "sub"), (0x2283, "sup"), (0x2286, "sube"), (0x2287, "supe"), (0x2295, "oplus"),
            (0x22A5, "perp"), (0x25CA, "loz"), (0x2660, "spades"), (0x2663, "clubs"), (0x2665, "hearts"), (0x2666, "diams"),
        };
        foreach (var (code, name) in others) map[code] = name;
        return map;
    }

    /// <summary>Uses names such as &amp;copy; where one exists, and numeric references for the rest of non-ASCII.</summary>
    static string HtmlEncodeNamed(string text, bool all)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var r in Runes(text))
        {
            if (NamedEntities.TryGetValue(r, out var name)) sb.Append('&').Append(name).Append(';');
            else if (r > 127 || (all && r > 32)) sb.Append("&#").Append(r).Append(';');
            else sb.Append((char)r);
        }
        return sb.ToString();
    }

    // Punycode

    static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    /// <summary>Converts each host name found in the text, so a whole URL works as well as a bare domain.</summary>
    static string PunycodeEncode(string text) =>
        Regex.Replace(text, @"[^\s/:?#@\[\]]+", m => m.Value.Any(c => c > 127) ? Idn.GetAscii(m.Value) : m.Value);

    static string PunycodeDecode(string text) =>
        Regex.Replace(text, @"[^\s/:?#@\[\]]*xn--[^\s/:?#@\[\]]*", m => Idn.GetUnicode(m.Value), RegexOptions.IgnoreCase);

    // Quoted-Printable

    static string QuotedPrintableEncode(string text, Encoding enc)
    {
        var sb = new StringBuilder();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            var bytes = enc.GetBytes(lines[l]);
            int column = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                bool last = i == bytes.Length - 1;
                // Trailing spaces and tabs must be encoded, others only when not printable
                var piece = (b >= 33 && b <= 126 && b != '=') || ((b == ' ' || b == '\t') && !last) ? ((char)b).ToString() : "=" + b.ToString("X2");
                if (column + piece.Length > 75) { sb.Append("=\r\n"); column = 0; }
                sb.Append(piece);
                column += piece.Length;
            }
            if (l < lines.Length - 1) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    static string QuotedPrintableDecode(string text, Encoding enc)
    {
        var s = Regex.Replace(text, @"=\r?\n", "");
        var bytes = new MemoryStream();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.WriteByte(byte.Parse(s.Substring(i + 1, 2), NumberStyles.HexNumber));
                i += 2;
            }
            else if (s[i] == '\n') { bytes.WriteByte((byte)'\n'); }
            else if (s[i] > 127) { var b = enc.GetBytes(s[i].ToString()); bytes.Write(b, 0, b.Length); }
            else bytes.WriteByte((byte)s[i]);
        }
        return enc.GetString(bytes.ToArray());
    }

    static string Rot13(string text) => new(text.Select(c =>
        c >= 'a' && c <= 'z' ? (char)('a' + (c - 'a' + 13) % 26)
        : c >= 'A' && c <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26)
        : c).ToArray());

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

    /// <summary>Decodes with <paramref name="enc"/>, or detects UTF-8/GBK when it is null.</summary>
    static string HexDecode(string text, Encoding? enc)
    {
        var hex = Regex.Replace(text, @"0x|\\x|[\s,:\-]", "", RegexOptions.IgnoreCase);
        if (hex.Length % 2 != 0 || !Regex.IsMatch(hex, "^[0-9a-fA-F]*$")) throw new FormatException("不是有效的十六进制");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
        return enc == null ? TextFiles.Decode(bytes, out _) : enc.GetString(bytes);
    }
}
