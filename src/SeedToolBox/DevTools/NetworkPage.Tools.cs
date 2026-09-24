using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SeedToolBox.DevTools;

/// <summary>Traceroute, DNS lookup, port scan, subnet calculator and HTTP tester.</summary>
sealed partial class NetworkPage
{
    static DockPanel TabPanel(UIElement top, UIElement fill, UIElement? bottom = null)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (top is FrameworkElement f) f.Margin = new Thickness(0, 10, 0, 10);
        SetDock(top, Dock.Top);
        panel.Children.Add(top);
        if (bottom != null)
        {
            SetDock(bottom, Dock.Bottom);
            panel.Children.Add(bottom);
        }
        panel.Children.Add(fill);
        return panel;
    }

    static TextBox ReadOnlyArea()
    {
        var box = Ui.Area();
        box.IsReadOnly = true;
        return box;
    }

    // Traceroute

    async void Traceroute()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0 || _pinging) return;
        _pinging = true;
        try
        {
            IPAddress target;
            try
            {
                target = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                Log($"无法解析 {host}：{ex.Message}");
                return;
            }
            Log($"路由追踪 {host} [{target}]，最多 30 跳：");
            using var ping = new Ping();
            var buffer = new byte[32];
            for (int ttl = 1; ttl <= 30; ttl++)
            {
                var watch = Stopwatch.StartNew();
                PingReply reply;
                try { reply = await ping.SendPingAsync(target, 3000, buffer, new PingOptions(ttl, true)); }
                catch (PingException ex)
                {
                    Log("  失败：" + (ex.InnerException?.Message ?? ex.Message));
                    break;
                }
                watch.Stop();
                if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                {
                    var name = await ReverseName(reply.Address);
                    Log($"  {ttl,2}  {watch.ElapsedMilliseconds,5} ms  {reply.Address}{(name != null ? "  " + name : "")}");
                    if (reply.Status == IPStatus.Success) { Log("  追踪完成\r\n"); return; }
                }
                else
                {
                    Log($"  {ttl,2}      *     {StatusText(reply.Status)}");
                }
            }
            Log("  已达最大跳数\r\n");
        }
        finally
        {
            _pinging = false;
        }
    }

    static async Task<string?> ReverseName(IPAddress address)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) != lookup) return null;
            var name = (await lookup).HostName;
            return name == address.ToString() ? null : name;
        }
        catch (SocketException) { return null; }
    }

    // DNS

    readonly TextBox _dnsName = Ui.Field(260);
    readonly ComboBox _dnsType = new() { Width = 90, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBox _dnsServer = Ui.Field(140);
    readonly TextBox _dnsLog = ReadOnlyArea();
    readonly AsyncToken _dnsToken = new();

    FrameworkElement BuildDns()
    {
        _dnsName.Text = "baidu.com";
        foreach (var t in new[] { "A/AAAA", "MX", "TXT", "CNAME", "NS", "SOA", "PTR", "ANY" }) _dnsType.Items.Add(t);
        _dnsType.SelectedIndex = 0;
        _dnsServer.ToolTip = "留空使用系统 DNS";
        _dnsName.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) LookupDns(); };
        var row = Ui.Row(Ui.Label("域名"), _dnsName, Ui.Label("", 12), Ui.Label("类型"), _dnsType, Ui.Label("DNS 服务器"), _dnsServer, Ui.Label("", 12),
            Ui.Button("查询", LookupDns, accent: true), Ui.Button("清空", () => _dnsLog.Clear()));
        return TabPanel(row, _dnsLog);
    }

    void LookupDns()
    {
        var name = _dnsName.Text.Trim();
        if (name.Length == 0) return;
        var type = (string)_dnsType.SelectedItem;
        var server = _dnsServer.Text.Trim();
        _dnsLog.AppendText($"查询 {name}（{type}）…\r\n");
        Ui.RunAsync(_dnsToken, () => QueryDns(name, type, server), text =>
        {
            _dnsLog.AppendText(text + "\r\n");
            _dnsLog.ScrollToEnd();
        }, ex => _dnsLog.AppendText("查询失败：" + ex.Message + "\r\n\r\n"));
    }

    static string QueryDns(string name, string type, string server)
    {
        var sb = new StringBuilder();
        if (type == "A/AAAA" && server.Length == 0)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var addresses = Dns.GetHostAddresses(name);
                sb.AppendLine($"  用时 {watch.ElapsedMilliseconds} ms，共 {addresses.Length} 个地址");
                foreach (var a in addresses)
                    sb.AppendLine($"  {(a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A   ")}  {a}");
            }
            catch (SocketException ex)
            {
                sb.AppendLine("  解析失败：" + ex.Message);
            }
            return sb.ToString();
        }
        var args = $"-type={(type == "A/AAAA" ? "A" : type)} {name}" + (server.Length > 0 ? " " + server : "");
        return Nslookup(args);
    }

    /// <summary>Runs nslookup and keeps the answer part, dropping the lines about the server that was asked.</summary>
    static string Nslookup(string args)
    {
        var psi = new ProcessStartInfo("nslookup", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        };
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
        var lines = output.Replace("\r\n", "\n").Split('\n').ToList();
        // The first block names the server; the answer follows the first blank line
        int blank = lines.FindIndex(l => l.Trim().Length == 0);
        var answer = (blank >= 0 ? lines.Skip(blank + 1) : lines).Where(l => l.Trim().Length > 0).Select(l => "  " + l.TrimEnd()).ToList();
        var err = errTask.Result.Trim();
        var sb = new StringBuilder();
        foreach (var l in answer) sb.AppendLine(l);
        if (err.Length > 0) sb.AppendLine("  " + err.Replace("\n", "\n  "));
        if (sb.Length == 0) sb.AppendLine("  没有结果");
        return sb.ToString();
    }

    // Port scan

    const int MaxScanPorts = 1024;
    readonly TextBox _scanHost = Ui.Field(200);
    readonly TextBox _scanPorts = Ui.Field(200);
    readonly TextBox _scanTimeout = Ui.Field(60);
    readonly TextBox _scanLog = ReadOnlyArea();
    readonly TextBlock _scanStatus = Ui.Status();
    CancellationTokenSource? _scanCancel;

    FrameworkElement BuildScan()
    {
        _scanHost.Text = "127.0.0.1";
        _scanPorts.Text = "1-1024";
        _scanPorts.ToolTip = "例如 1-1024 或 22,80,443,8000-8100，最多 1024 个端口";
        _scanTimeout.Text = "800";
        var row = Ui.Row(Ui.Label("主机"), _scanHost, Ui.Label("", 12), Ui.Label("端口"), _scanPorts, Ui.Label("", 12), Ui.Label("超时 ms"), _scanTimeout, Ui.Label("", 12),
            Ui.Button("开始扫描", Scan, accent: true), Ui.Button("停止", () => _scanCancel?.Cancel()), Ui.Button("清空", () => _scanLog.Clear()));
        return TabPanel(row, _scanLog, _scanStatus);
    }

    static List<int>? ParsePorts(string text, out string error)
    {
        error = "";
        var ports = new SortedSet<int>();
        foreach (var part in text.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var from) || !int.TryParse(range[range.Length - 1], out var to) || from < 1 || to > 65535 || from > to)
            {
                error = $"端口格式不正确：{part}";
                return null;
            }
            for (int p = from; p <= to; p++)
            {
                ports.Add(p);
                if (ports.Count > MaxScanPorts) { error = $"一次最多扫描 {MaxScanPorts} 个端口"; return null; }
            }
        }
        if (ports.Count == 0) { error = "请输入端口"; return null; }
        return ports.ToList();
    }

    async void Scan()
    {
        if (_scanCancel != null) return;
        var host = _scanHost.Text.Trim();
        if (host.Length == 0) { Ui.SetStatus(_scanStatus, "请输入主机", true); return; }
        var ports = ParsePorts(_scanPorts.Text, out var error);
        if (ports == null) { Ui.SetStatus(_scanStatus, error, true); return; }
        if (!int.TryParse(_scanTimeout.Text, out var timeout) || timeout < 100 || timeout > 10000) { Ui.SetStatus(_scanStatus, "超时应为 100–10000 ms", true); return; }

        IPAddress address;
        try { address = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host)).First(); }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException) { Ui.SetStatus(_scanStatus, $"无法解析 {host}：{ex.Message}", true); return; }

        var cts = _scanCancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var open = new List<int>();
        int done = 0;
        _scanLog.AppendText($"扫描 {host} [{address}] 的 {ports.Count} 个端口…\r\n");
        using var gate = new SemaphoreSlim(64);
        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(true);
            try
            {
                if (await IsOpen(address, port, timeout))
                {
                    open.Add(port);
                    _scanLog.AppendText($"  {port,5}  开放{KnownService(port)}\r\n");
                    _scanLog.ScrollToEnd();
                }
            }
            finally
            {
                gate.Release();
                done++;
                if (done % 16 == 0 || done == ports.Count) Ui.SetStatus(_scanStatus, $"已扫描 {done}/{ports.Count}，开放 {open.Count}");
            }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        finally { _scanCancel = null; }
        var summary = $"{(cts.IsCancellationRequested ? "已停止" : "完成")}：扫描 {done} 个端口，开放 {open.Count} 个，用时 {watch.Elapsed.TotalSeconds:0.0} 秒";
        _scanLog.AppendText("  " + summary + (open.Count > 0 ? "：" + string.Join(", ", open.OrderBy(p => p)) : "") + "\r\n\r\n");
        _scanLog.ScrollToEnd();
        Ui.SetStatus(_scanStatus, summary);
    }

    static async Task<bool> IsOpen(IPAddress address, int port, int timeout)
    {
        using var client = new TcpClient(address.AddressFamily);
        try
        {
            var connect = client.ConnectAsync(address, port);
            if (await Task.WhenAny(connect, Task.Delay(timeout)) != connect)
            {
                _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            await connect;
            return true;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    static string KnownService(int port) => port switch
    {
        21 => "  FTP", 22 => "  SSH", 23 => "  Telnet", 25 => "  SMTP", 53 => "  DNS", 80 => "  HTTP", 110 => "  POP3", 135 => "  RPC",
        139 => "  NetBIOS", 143 => "  IMAP", 443 => "  HTTPS", 445 => "  SMB", 1433 => "  SQL Server", 3306 => "  MySQL", 3389 => "  远程桌面",
        5432 => "  PostgreSQL", 6379 => "  Redis", 8080 => "  HTTP 代理", 27017 => "  MongoDB",
        _ => "",
    };

    // Subnet calculator

    readonly TextBox _subnetInput = Ui.Field(260);
    readonly TextBox _subnetResult = ReadOnlyArea();

    FrameworkElement BuildSubnet()
    {
        _subnetInput.Text = "192.168.1.10/24";
        _subnetInput.ToolTip = "例如 192.168.1.10/24 或 10.0.0.1 255.255.0.0";
        _subnetInput.TextChanged += (_, _) => _subnetResult.Text = Subnet.Describe(_subnetInput.Text);
        var row = Ui.Row(Ui.Label("IP / CIDR"), _subnetInput, Ui.Label("", 12), Ui.CopyButton(() => _subnetResult.Text));
        _subnetResult.Text = Subnet.Describe(_subnetInput.Text);
        return TabPanel(row, _subnetResult);
    }

    // HTTP

    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
    readonly ComboBox _httpMethod = new() { Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEditable = true };
    readonly TextBox _httpUrl = Ui.Field(420);
    readonly TextBox _httpHeaders = Ui.Area();
    readonly TextBox _httpBody = Ui.Area();
    readonly TextBox _httpResponse = ReadOnlyArea();
    readonly TextBlock _httpStatus = Ui.Status();
    CancellationTokenSource? _httpCancel;

    FrameworkElement BuildHttp()
    {
        foreach (var m in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }) _httpMethod.Items.Add(m);
        _httpMethod.Text = "GET";
        _httpUrl.Text = "https://httpbin.org/get";
        _httpHeaders.Text = "User-Agent: SeedToolBox\r\nAccept: */*";
        _httpUrl.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SendHttp(); };
        var row = Ui.Row(_httpMethod, _httpUrl, Ui.Label("", 12), Ui.Button("发送", SendHttp, accent: true), Ui.Button("取消", () => _httpCancel?.Cancel()),
            Ui.CopyButton(() => _httpResponse.Text, "复制响应"));

        var request = new Grid();
        request.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        request.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        request.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var headers = Ui.Titled("请求头（每行 名称: 值）", _httpHeaders);
        var body = Ui.Titled("请求体", _httpBody);
        Grid.SetRow(body, 2);
        request.Children.Add(headers);
        request.Children.Add(body);
        return TabPanel(row, Ui.Columns(request, Ui.Titled("响应", _httpResponse)), _httpStatus);
    }

    async void SendHttp()
    {
        if (_httpCancel != null) return;
        var method = _httpMethod.Text.Trim().ToUpperInvariant();
        if (!Uri.TryCreate(_httpUrl.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Ui.SetStatus(_httpStatus, "URL 应以 http:// 或 https:// 开头", true);
            return;
        }
        if (method.Length == 0) { Ui.SetStatus(_httpStatus, "请输入请求方法", true); return; }

        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var contentHeaders = new List<(string, string)>();
        foreach (var line in _httpHeaders.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) { Ui.SetStatus(_httpStatus, "请求头格式不正确：" + line, true); return; }
            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (!request.Headers.TryAddWithoutValidation(name, value)) contentHeaders.Add((name, value));
        }
        if (_httpBody.Text.Length > 0 || contentHeaders.Count > 0)
        {
            request.Content = new StringContent(_httpBody.Text, new UTF8Encoding(false));
            request.Content.Headers.ContentType = null;
            foreach (var (name, value) in contentHeaders) request.Content.Headers.TryAddWithoutValidation(name, value);
            if (request.Content.Headers.ContentType == null && _httpBody.Text.Length > 0)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", _httpBody.Text.TrimStart().StartsWith("{") || _httpBody.Text.TrimStart().StartsWith("[") ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
        }

        var cts = _httpCancel = new CancellationTokenSource();
        Ui.SetStatus(_httpStatus, $"{method} {uri} …");
        var watch = Stopwatch.StartNew();
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                var headersTime = watch.ElapsedMilliseconds;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                watch.Stop();
                var sb = new StringBuilder();
                sb.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}");
                foreach (var h in response.Headers) sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                foreach (var h in response.Content.Headers) sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                sb.AppendLine();
                sb.Append(DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet));
                _httpResponse.Text = sb.ToString();
                Ui.SetStatus(_httpStatus, $"{(int)response.StatusCode} {response.ReasonPhrase}  首字节 {headersTime} ms，总计 {watch.ElapsedMilliseconds} ms，{Ui.FormatSize(bytes.Length)}", !response.IsSuccessStatusCode && (int)response.StatusCode >= 400);
            }
        }
        catch (TaskCanceledException)
        {
            Ui.SetStatus(_httpStatus, cts.IsCancellationRequested ? "已取消" : "请求超时（60 秒）", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or FormatException)
        {
            Ui.SetStatus(_httpStatus, "请求失败：" + (ex.InnerException?.Message ?? ex.Message), true);
        }
        finally
        {
            _httpCancel = null;
        }
    }

    static string DecodeBody(byte[] bytes, string? charset)
    {
        const int Max = 2 * 1024 * 1024;
        bool truncated = bytes.Length > Max;
        if (truncated) Array.Resize(ref bytes, Max);
        string text;
        try { text = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')).GetString(bytes) : TextFiles.Decode(bytes, out _); }
        catch (ArgumentException) { text = TextFiles.Decode(bytes, out _); }
        return truncated ? text + "\r\n\r\n（内容超过 2 MB，已截断）" : text;
    }
}

/// <summary>IPv4 subnet arithmetic.</summary>
static class Subnet
{
    public static string Describe(string input)
    {
        var parts = input.Trim().Split(new[] { '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (!TryParse(parts[0], out var ip)) return "IP 地址格式不正确（只支持 IPv4）";
        int prefix = 32;
        if (parts.Length > 1)
        {
            if (parts[1].Contains('.'))
            {
                if (!TryParse(parts[1], out var m) || !IsMask(m)) return "子网掩码不正确";
                prefix = CountBits(m);
            }
            else if (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > 32) return "前缀长度应为 0–32";
        }
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint network = ip & mask;
        uint broadcast = network | ~mask;
        long total = 1L << (32 - prefix);
        string first, last;
        long hosts;
        if (prefix >= 31)
        {
            first = Format(network);
            last = Format(broadcast);
            hosts = total;
        }
        else
        {
            first = Format(network + 1);
            last = Format(broadcast - 1);
            hosts = total - 2;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"IP 地址       {Format(ip)}");
        sb.AppendLine($"CIDR          {Format(network)}/{prefix}");
        sb.AppendLine($"子网掩码      {Format(mask)}");
        sb.AppendLine($"通配符掩码    {Format(~mask)}");
        sb.AppendLine($"网络地址      {Format(network)}");
        sb.AppendLine($"广播地址      {(prefix >= 31 ? "（无）" : Format(broadcast))}");
        sb.AppendLine($"可用主机范围  {first} – {last}");
        sb.AppendLine($"可用主机数    {hosts:N0}");
        sb.AppendLine($"地址总数      {total:N0}");
        sb.AppendLine($"地址类别      {Kind(ip)}");
        sb.AppendLine($"二进制掩码    {Binary(mask)}");
        sb.AppendLine($"二进制 IP     {Binary(ip)}");
        return sb.ToString();
    }

    static bool TryParse(string s, out uint value)
    {
        value = 0;
        var octets = s.Split('.');
        if (octets.Length != 4) return false;
        foreach (var o in octets)
        {
            if (!byte.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return false;
            value = (value << 8) | b;
        }
        return true;
    }

    static bool IsMask(uint m) => (~m & (~m + 1)) == 0;

    static int CountBits(uint m)
    {
        int n = 0;
        for (; m != 0; m <<= 1) n++;
        return n;
    }

    static string Format(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    static string Binary(uint v) => string.Join(".", Enumerable.Range(0, 4).Select(i => Convert.ToString((v >> (24 - i * 8)) & 255, 2).PadLeft(8, '0')));

    static string Kind(uint ip)
    {
        byte a = (byte)(ip >> 24), b = (byte)(ip >> 16);
        if (a == 10 || (a == 172 && b >= 16 && b < 32) || (a == 192 && b == 168)) return "私有地址";
        if (a == 127) return "回环地址";
        if (a == 169 && b == 254) return "链路本地地址";
        if (a == 100 && b >= 64 && b < 128) return "运营商级 NAT（CGNAT）";
        if (a >= 224 && a < 240) return "组播地址";
        if (a >= 240) return "保留地址";
        return "公网地址";
    }
}
