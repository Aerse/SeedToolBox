using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Text to and from Base64.</summary>
sealed class Base64TextPage : DockPanel
{
    readonly TextBox _text = Ui.Area(wrap: true);
    readonly TextBox _base64 = Ui.Area(wrap: true);
    readonly ComboBox _encoding = new() { Width = 110, ItemsSource = new[] { "UTF-8", "GBK", "UTF-16" }, SelectedIndex = 0 };
    readonly CheckBox _urlSafe = new() { Content = "URL 安全（-_ 无填充）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    public Base64TextPage()
    {
        var header = Ui.Header("Base64 编解码", "文本与 Base64 互转");
        var toolbar = Ui.Row(
            Ui.Label("字符编码"), _encoding, Ui.Label("", 16), _urlSafe,
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
        var body = Ui.Columns(Ui.Titled("文本", _text, Copy(_text)), Ui.Titled("Base64", _base64, Copy(_base64)));
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
        var bytes = TextEncoding.GetBytes(_text.Text);
        var s = Convert.ToBase64String(bytes);
        if (_urlSafe.IsChecked == true) s = s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _base64.Text = s;
        Ui.SetStatus(_status, $"{bytes.Length:N0} 字节 → {s.Length:N0} 字符");
    }

    void Decode()
    {
        try
        {
            var bytes = FromBase64(_base64.Text);
            _text.Text = TextEncoding.GetString(bytes);
            Ui.SetStatus(_status, $"解码得到 {bytes.Length:N0} 字节");
        }
        catch (FormatException)
        {
            Ui.SetStatus(_status, "不是有效的 Base64", true);
        }
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
}
