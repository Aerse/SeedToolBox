using System;
using System.Globalization;
using System.Text;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.DevTools;

/// <summary>Decodes a JWT's header and payload and explains its time claims. The signature is not verified.</summary>
sealed class JwtPage : DockPanel
{
    readonly TextBox _input = Ui.Area(wrap: true);
    readonly TextBox _header = Ui.Area();
    readonly TextBox _payload = Ui.Area();
    readonly TextBlock _status = Ui.Status();

    public JwtPage()
    {
        var header = Ui.Header("JWT 解码", "解析 JWT 的头部与载荷，显示签发/过期时间（不校验签名）");
        _header.IsReadOnly = true;
        _payload.IsReadOnly = true;
        _input.Height = 110;
        _input.TextChanged += (_, _) => Decode();

        Ui.FileDrop(_input, files => Ui.LoadText(_input, files[0], _status));
        var top = Ui.Titled("Token", _input);
        top.Margin = new System.Windows.Thickness(0, 0, 0, 12);
        var left = Ui.Titled("头部 Header", _header);
        var body = Ui.Columns(left, Ui.Titled("载荷 Payload", _payload));

        SetDock(header, Dock.Top);
        SetDock(top, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(top);
        Children.Add(_status);
        Children.Add(body);
    }

    void Decode()
    {
        var token = _input.Text.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token.Substring(7).Trim();
        _header.Clear();
        _payload.Clear();
        if (token.Length == 0) { _status.Text = ""; return; }
        try
        {
            var (head, payload, signed) = Jwt.Decode(token);
            _header.Text = head.ToString();
            _payload.Text = payload.ToString();
            Ui.SetStatus(_status, Jwt.Describe(payload, signed), Jwt.IsExpired(payload));
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "无法解析：" + ex.Message, true);
        }
    }
}

static class Jwt
{
    public static (JToken Header, JToken Payload, bool Signed) Decode(string token)
    {
        var parts = token.Split('.');
        if (parts.Length is not (3 or 2)) throw new FormatException("JWT 应由 2 个点分成 3 段");
        return (JToken.Parse(Base64Url(parts[0])), JToken.Parse(Base64Url(parts[1])), parts.Length == 3 && parts[2].Length > 0);
    }

    static string Base64Url(string part)
    {
        var s = part.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    static DateTimeOffset? Time(JToken payload, string claim) =>
        payload[claim] is JValue { Type: JTokenType.Integer or JTokenType.Float } v ? DateTimeOffset.FromUnixTimeSeconds((long)v.Value<double>()).ToLocalTime() : null;

    public static bool IsExpired(JToken payload) => Time(payload, "exp") is { } exp && exp < DateTimeOffset.Now;

    public static string Describe(JToken payload, bool signed)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (Time(payload, "iat") is { } iat) parts.Add("签发 " + iat.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (Time(payload, "nbf") is { } nbf) parts.Add("生效 " + nbf.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (Time(payload, "exp") is { } exp)
        {
            var left = exp - DateTimeOffset.Now;
            parts.Add("过期 " + exp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + (left < TimeSpan.Zero ? "（已过期）" : left.TotalDays >= 1 ? $"（剩余 {(int)left.TotalDays} 天）" : $"（剩余 {left:hh\\:mm\\:ss}）"));
        }
        if (!signed) parts.Add("无签名");
        return parts.Count > 0 ? string.Join("   ", parts) : "解析成功";
    }
}
