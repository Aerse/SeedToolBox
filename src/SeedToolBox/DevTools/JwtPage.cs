using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.DevTools;

/// <summary>Decodes a JWT's header and payload, explains its time claims, verifies HS*/RS* signatures and signs new HS* tokens.</summary>
sealed class JwtPage : DockPanel
{
    readonly TextBox _input = Ui.Area(wrap: true);
    readonly TextBox _key = Ui.Area(wrap: true);
    readonly TextBox _header = Ui.Area();
    readonly TextBox _payload = Ui.Area();
    readonly ComboBox _alg = new() { Width = 90, ItemsSource = new[] { "HS256", "HS384", "HS512" }, SelectedIndex = 0 };
    readonly TextBlock _verify = Ui.Status();
    readonly TextBlock _status = Ui.Status();

    public JwtPage()
    {
        var header = Ui.Header("JWT 解码 / 校验", "解析 JWT 的头部与载荷，显示签发/过期时间，校验 HS*/RS* 签名，也可签发 HS* Token");
        _input.Height = 110;
        _key.Height = 64;
        _input.TextChanged += (_, _) => Decode();
        _key.TextChanged += (_, _) => Verify();

        Ui.FileDrop(_input, files => Ui.LoadText(_input, files[0], _status));
        Ui.FileDrop(_key, files => Ui.LoadText(_key, files[0], _status));
        var top = Ui.Titled("Token", _input);
        top.Margin = new Thickness(0, 0, 0, 12);
        var key = Ui.Titled("密钥：HS* 填共享密钥，RS* 填 PEM 公钥或证书（可拖入文件）", _key);
        key.Margin = new Thickness(0, 0, 0, 12);
        var left = Ui.Titled("头部 Header（可编辑后签名）", _header);
        var body = Ui.Columns(left, Ui.Titled("载荷 Payload（可编辑后签名）", _payload));

        var sign = Ui.Row(Ui.Label("签发"), _alg, Ui.Label("", 8), Ui.Button("用上面的头部、载荷和密钥生成 Token", Sign, accent: true), Ui.CopyButton(() => _input.Text.Trim(), "复制 Token"));
        sign.Margin = new Thickness(0, 12, 0, 0);
        _verify.Margin = new Thickness(0, 10, 0, 0);

        SetDock(header, Dock.Top);
        SetDock(top, Dock.Top);
        SetDock(key, Dock.Top);
        SetDock(_status, Dock.Bottom);
        SetDock(_verify, Dock.Bottom);
        SetDock(sign, Dock.Bottom);
        Children.Add(header);
        Children.Add(top);
        Children.Add(key);
        Children.Add(_status);
        Children.Add(_verify);
        Children.Add(sign);
        Children.Add(body);
    }

    string Token()
    {
        var token = _input.Text.Trim();
        return token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token.Substring(7).Trim() : token;
    }

    void Decode()
    {
        var token = Token();
        _header.Clear();
        _payload.Clear();
        if (token.Length == 0) { _status.Text = ""; _verify.Text = ""; return; }
        try
        {
            var (head, payload, signed) = Jwt.Decode(token);
            _header.Text = head.ToString();
            _payload.Text = payload.ToString();
            Ui.SetStatus(_status, Jwt.Describe(payload, signed), Jwt.IsExpired(payload));
            if (head["alg"]?.ToString() is { } alg && JwtCrypto.IsHmac(alg)) _alg.SelectedItem = alg;
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "无法解析：" + ex.Message, true);
        }
        Verify();
    }

    void Verify()
    {
        var token = Token();
        _verify.Text = "";
        if (token.Length == 0) return;
        string alg;
        try { alg = Jwt.Decode(token).Header["alg"]?.ToString() ?? ""; }
        catch { return; }

        if (alg.Equals("none", StringComparison.OrdinalIgnoreCase) || alg.Length == 0)
        {
            Ui.SetStatus(_verify, "⚠ alg 为 none：Token 没有签名，任何人都能伪造，服务端不应接受", true);
            return;
        }
        if (token.Split('.') is not { Length: 3 } parts || parts[2].Length == 0) { Ui.SetStatus(_verify, $"⚠ 头部声明 {alg}，但 Token 没有签名段", true); return; }
        var key = JwtCrypto.IsRsa(alg) ? _key.Text : _key.Text.TrimEnd('\r', '\n');
        if (key.Trim().Length == 0) { Ui.SetStatus(_verify, $"签名算法 {alg}，填写密钥后自动校验"); return; }
        try
        {
            if (!JwtCrypto.Verify(alg, token, key)) { Ui.SetStatus(_verify, $"✗ {alg} 签名无效", true); return; }
            var weak = JwtCrypto.IsHmac(alg) ? JwtCrypto.WeakSecret(alg, key) : null;
            Ui.SetStatus(_verify, weak == null ? $"✓ {alg} 签名有效" : $"✓ {alg} 签名有效，但⚠ {weak}，容易被暴力破解", weak != null);
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_verify, "无法校验：" + ex.Message, true);
        }
    }

    void Sign()
    {
        var alg = (string)_alg.SelectedItem;
        var secret = _key.Text.TrimEnd('\r', '\n');
        if (secret.Length == 0) { Ui.SetStatus(_verify, "请先在密钥框填写签名用的密钥", true); return; }
        try
        {
            var head = _header.Text.Trim().Length == 0 ? new JObject { ["typ"] = "JWT" } : JObject.Parse(_header.Text);
            head["alg"] = alg;
            var payload = _payload.Text.Trim().Length == 0
                ? new JObject { ["sub"] = "1234567890", ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                : JToken.Parse(_payload.Text);
            var input = JwtCrypto.Base64Url(Encoding.UTF8.GetBytes(head.ToString(Formatting.None))) + "." + JwtCrypto.Base64Url(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            _input.Text = JwtCrypto.SignHmac(alg, input, secret);
        }
        catch (JsonException ex)
        {
            Ui.SetStatus(_verify, "头部或载荷不是有效的 JSON：" + ex.Message, true);
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
