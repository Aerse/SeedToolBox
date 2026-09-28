using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.DevTools;

sealed class ApiRequest
{
    public string Name { get; set; } = "新请求";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string Headers { get; set; } = "";
    public string Body { get; set; } = "";
}

sealed class ApiCollection
{
    public string Name { get; set; } = "新集合";
    public List<ApiRequest> Requests { get; set; } = new();
}

sealed class ApiEnvironment
{
    public string Name { get; set; } = "默认";
    /// <summary>One "name=value" per line.</summary>
    public string Variables { get; set; } = "";
}

sealed class ApiData
{
    public List<ApiCollection> Collections { get; set; } = new();
    public List<ApiEnvironment> Environments { get; set; } = new();
    public string ActiveEnvironment { get; set; } = "";
}

/// <summary>Saved HTTP requests in collections, with {{variables}} from environments and AI-written requests.</summary>
sealed class ApiPage : DockPanel
{
    static readonly string FilePath = Path.Combine(AppPaths.Data, "api.json");
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };

    readonly Func<string, Task<string>>? _ai;
    readonly ApiData _data;
    readonly TreeView _tree = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly ComboBox _method = new() { Width = 90, Margin = new Thickness(0, 0, 8, 0), };
    readonly TextBox _url = Ui.Field();
    readonly TextBox _headers = Ui.Area();
    readonly TextBox _body = Ui.Area();
    readonly TextBox _response = Ui.Area();
    readonly TextBox _variables = Ui.Area();
    readonly ComboBox _env = new() { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _prompt = Ui.Field();
    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    ApiRequest? _current;
    CancellationTokenSource? _cancel;
    bool _loading;

    /// <param name="ai">Answers a prompt with the assistant; null when AI isn't available.</param>
    public ApiPage(Func<string, Task<string>>? ai)
    {
        _ai = ai;
        _data = Load();
        if (_data.Environments.Count == 0) _data.Environments.Add(new ApiEnvironment { Variables = "baseUrl=https://httpbin.org" });
        if (_data.Collections.Count == 0)
            _data.Collections.Add(new ApiCollection
            {
                Name = "示例",
                Requests = { new ApiRequest { Name = "示例", Url = "{{baseUrl}}/get?t={{$timestamp}}", Headers = "Accept: application/json" } },
            });

        var header = Ui.Header("API 请求", "把请求存进集合反复用；URL、请求头和请求体里的 {{变量}} 从当前环境取值，也可以让 AI 按描述写请求");
        SetDock(header, Dock.Top);
        Children.Add(header);
        SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        // Left: collections
        var left = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 12, 0) };
        var leftButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var b in new[]
                 {
                     Ui.Button("新集合", NewCollection), Ui.Button("新请求", NewRequest), Ui.Button("重命名", Rename),
                     Ui.Button("复制", Duplicate), Ui.Button("删除", Delete), Ui.Button("导入 cURL", ImportCurl),
                 })
        {
            b.Margin = new Thickness(0, 0, 6, 6);
            leftButtons.Children.Add(b);
        }
        SetDock(leftButtons, Dock.Top);
        left.Children.Add(leftButtons);
        left.Children.Add(_tree);
        _tree.SelectedItemChanged += (_, _) => { if ((_tree.SelectedItem as TreeViewItem)?.Tag is ApiRequest r) Show(r); };
        SetDock(left, Dock.Left);
        Children.Add(left);

        // Right: editor
        var right = new DockPanel();
        foreach (var m in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }) _method.Items.Add(m);
        _url.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = Send(); };
        var send = Ui.Button("发送", () => _ = Send(), accent: true);
        var sendRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var sendButtons = Ui.Row(send, Ui.Button("取消", () => _cancel?.Cancel()), Ui.Button("复制为 cURL", CopyCurl));
        sendButtons.Margin = new Thickness(8, 0, 0, 0);
        SetDock(sendButtons, Dock.Right);
        SetDock(_method, Dock.Left);
        sendRow.Children.Add(sendButtons);
        sendRow.Children.Add(_method);
        sendRow.Children.Add(_url);
        SetDock(sendRow, Dock.Top);

        var envRow = Ui.Row(Ui.Label("环境"), _env, Ui.Button("新建环境", NewEnvironment), Ui.Button("删除环境", DeleteEnvironment));
        envRow.Margin = new Thickness(0, 0, 0, 8);
        SetDock(envRow, Dock.Top);

        var aiRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var aiButton = Ui.Button("AI 生成", () => _ = Generate());
        aiButton.Margin = new Thickness(8, 0, 0, 0);
        SetDock(aiButton, Dock.Right);
        var aiLabel = Ui.Label("让 AI 写");
        SetDock(aiLabel, Dock.Left);
        _prompt.ToolTip = "比如：用 POST 给 {{baseUrl}}/post 发一个创建用户的 JSON，带 Bearer token";
        _prompt.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = Generate(); };
        aiRow.Children.Add(aiButton);
        aiRow.Children.Add(aiLabel);
        aiRow.Children.Add(_prompt);
        SetDock(aiRow, Dock.Top);

        right.Children.Add(sendRow);
        right.Children.Add(envRow);
        right.Children.Add(aiRow);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "请求头", Content = _headers });
        tabs.Items.Add(new TabItem { Header = "请求体", Content = _body });
        tabs.Items.Add(new TabItem { Header = "环境变量", Content = Ui.Titled("每行 名称=值；在请求里写 {{名称}}。内置 {{$timestamp}} {{$uuid}} {{$isoDate}}", _variables) });
        _response.IsReadOnly = true;
        var responseActions = Ui.Row(Ui.CopyButton(() => _response.Text, "复制响应"));
        right.Children.Add(Ui.Columns(tabs, Ui.Titled("响应", _response, responseActions)));
        Children.Add(right);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        foreach (var box in new[] { _url, _headers, _body }) box.TextChanged += (_, _) => Edited();
        _method.SelectionChanged += (_, _) => { Edited(); if (_current != null && !_loading) RenameNode(); };
        _variables.TextChanged += (_, _) => { if (!_loading && _env.SelectedItem is ComboBoxItem { Tag: ApiEnvironment e }) { e.Variables = _variables.Text; _saveTimer.Stop(); _saveTimer.Start(); } };
        _env.SelectionChanged += (_, _) => ShowEnvironment();
        Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); } };

        BuildTree(_data.Collections[0].Requests.FirstOrDefault());
        BuildEnvironments();
    }

    // Storage

    static ApiData Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<ApiData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Error("Reading API requests failed", ex); }
        return new();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(_data, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
        }
    }

    void Edited()
    {
        if (_loading || _current == null) return;
        _current.Method = _method.SelectedItem as string ?? "GET";
        _current.Url = _url.Text;
        _current.Headers = _headers.Text;
        _current.Body = _body.Text;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // Tree

    void BuildTree(object? select)
    {
        _tree.Items.Clear();
        foreach (var c in _data.Collections)
        {
            var node = new TreeViewItem { Header = "📁 " + c.Name, Tag = c, IsExpanded = true };
            foreach (var r in c.Requests)
            {
                var item = new TreeViewItem { Header = $"{r.Method,-6} {r.Name}", Tag = r, FontFamily = Ui.Mono };
                node.Items.Add(item);
                if (r == select) item.IsSelected = true;
            }
            if (c == select) node.IsSelected = true;
            _tree.Items.Add(node);
        }
        if (select == null) Show(null);
    }

    void Show(ApiRequest? r)
    {
        _current = r;
        _loading = true;
        var method = r?.Method ?? "GET";
        if (!_method.Items.Contains(method)) _method.Items.Add(method);
        _method.SelectedItem = method;
        _url.Text = r?.Url ?? "";
        _headers.Text = r?.Headers ?? "";
        _body.Text = r?.Body ?? "";
        _loading = false;
        foreach (var box in new Control[] { _method, _url, _headers, _body }) box.IsEnabled = r != null;
    }

    void RenameNode()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: ApiRequest r } item) item.Header = $"{r.Method,-6} {r.Name}";
    }

    object? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag;

    ApiCollection? SelectedCollection => Selected switch
    {
        ApiCollection c => c,
        ApiRequest r => _data.Collections.FirstOrDefault(c => c.Requests.Contains(r)),
        _ => _data.Collections.FirstOrDefault(),
    };

    string? Ask(string title, string initial) => Ai.AiService.AskText(Window.GetWindow(this), title, "名称", initial);

    void NewCollection()
    {
        var name = Ask("新集合", "新集合");
        if (string.IsNullOrWhiteSpace(name)) return;
        var c = new ApiCollection { Name = name!.Trim() };
        _data.Collections.Add(c);
        Save();
        BuildTree(c);
    }

    ApiRequest AddRequest(ApiRequest r)
    {
        var c = SelectedCollection;
        if (c == null) { c = new ApiCollection(); _data.Collections.Add(c); }
        c.Requests.Add(r);
        Save();
        BuildTree(r);
        return r;
    }

    void NewRequest() => AddRequest(new ApiRequest { Url = "{{baseUrl}}/" });

    void Rename()
    {
        switch (Selected)
        {
            case ApiCollection c when Ask("重命名集合", c.Name) is { } n && n.Trim().Length > 0: c.Name = n.Trim(); break;
            case ApiRequest r when Ask("重命名请求", r.Name) is { } n && n.Trim().Length > 0: r.Name = n.Trim(); break;
            default: return;
        }
        Save();
        BuildTree(Selected);
    }

    void Duplicate()
    {
        if (Selected is not ApiRequest r) { Ui.SetStatus(_status, "先选一个请求", true); return; }
        var copy = JsonConvert.DeserializeObject<ApiRequest>(JsonConvert.SerializeObject(r))!;
        copy.Name += " 副本";
        AddRequest(copy);
    }

    void Delete()
    {
        var owner = Window.GetWindow(this);
        switch (Selected)
        {
            case ApiCollection c:
                if (MessageBox.Show(owner, $"删除集合“{c.Name}”和里面的 {c.Requests.Count} 个请求？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _data.Collections.Remove(c);
                break;
            case ApiRequest r:
                if (MessageBox.Show(owner, $"删除请求“{r.Name}”？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var c in _data.Collections) c.Requests.Remove(r);
                break;
            default: return;
        }
        Save();
        BuildTree(null);
    }

    // Environments

    void BuildEnvironments()
    {
        _env.Items.Clear();
        foreach (var e in _data.Environments)
        {
            var item = new ComboBoxItem { Content = e.Name, Tag = e };
            _env.Items.Add(item);
            if (e.Name == _data.ActiveEnvironment) _env.SelectedItem = item;
        }
        if (_env.SelectedItem == null && _env.Items.Count > 0) _env.SelectedIndex = 0;
    }

    ApiEnvironment? Environment => (_env.SelectedItem as ComboBoxItem)?.Tag as ApiEnvironment;

    void ShowEnvironment()
    {
        _loading = true;
        _variables.Text = Environment?.Variables ?? "";
        _variables.IsEnabled = Environment != null;
        _loading = false;
        if (Environment != null && _data.ActiveEnvironment != Environment.Name) { _data.ActiveEnvironment = Environment.Name; Save(); }
    }

    void NewEnvironment()
    {
        var name = Ask("新建环境", "测试");
        if (string.IsNullOrWhiteSpace(name)) return;
        _data.Environments.Add(new ApiEnvironment { Name = name!.Trim(), Variables = Environment?.Variables ?? "" });
        _data.ActiveEnvironment = name.Trim();
        Save();
        BuildEnvironments();
    }

    void DeleteEnvironment()
    {
        if (Environment is not { } e || _data.Environments.Count <= 1) { Ui.SetStatus(_status, "至少要留一个环境", true); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"删除环境“{e.Name}”？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _data.Environments.Remove(e);
        Save();
        BuildEnvironments();
    }

    /// <summary>Replaces {{name}} with the environment's value; unknown names are listed in <paramref name="missing"/>.</summary>
    public static string Expand(string text, string variables, ISet<string> missing)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in variables.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && !line.TrimStart().StartsWith("#")) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return Regex.Replace(text, @"\{\{\s*([^{}\s]+)\s*\}\}", m =>
        {
            var name = m.Groups[1].Value;
            switch (name)
            {
                case "$timestamp": return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                case "$uuid": return Guid.NewGuid().ToString();
                case "$isoDate": return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            if (values.TryGetValue(name, out var v)) return v;
            missing.Add(name);
            return m.Value;
        });
    }

    // Sending

    async Task Send()
    {
        if (_cancel != null) return;
        var vars = Environment?.Variables ?? "";
        var missing = new SortedSet<string>();
        var method = _method.SelectedItem as string ?? "GET";
        var url = Expand(_url.Text.Trim(), vars, missing);
        var headers = Expand(_headers.Text, vars, missing);
        var body = Expand(_body.Text, vars, missing);
        if (missing.Count > 0) { Ui.SetStatus(_status, "环境里没有这些变量：" + string.Join("、", missing), true); return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Ui.SetStatus(_status, "URL 应以 http:// 或 https:// 开头：" + url, true);
            return;
        }

        HttpRequestMessage request;
        try { request = Build(method, uri, headers, body); }
        catch (FormatException ex) { Ui.SetStatus(_status, ex.Message, true); return; }

        var cts = _cancel = new CancellationTokenSource();
        Ui.SetStatus(_status, $"{method} {uri} …");
        var watch = Stopwatch.StartNew();
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                watch.Stop();
                var sb = new StringBuilder();
                sb.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}");
                foreach (var h in response.Headers) sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                foreach (var h in response.Content.Headers) sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
                sb.AppendLine();
                sb.Append(Pretty(Decode(bytes, response.Content.Headers.ContentType?.CharSet)));
                _response.Text = sb.ToString();
                Ui.SetStatus(_status, $"{(int)response.StatusCode} {response.ReasonPhrase}  {watch.ElapsedMilliseconds} ms，{Ui.FormatSize(bytes.Length)}", (int)response.StatusCode >= 400);
            }
        }
        catch (TaskCanceledException)
        {
            Ui.SetStatus(_status, cts.IsCancellationRequested ? "已取消" : "请求超时（60 秒）", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Ui.SetStatus(_status, "请求失败：" + (ex.InnerException?.Message ?? ex.Message), true);
        }
        finally
        {
            _cancel = null;
        }
    }

    static HttpRequestMessage Build(string method, Uri uri, string headers, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var contentHeaders = new List<(string, string)>();
        foreach (var line in headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith("#")) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) { request.Dispose(); throw new FormatException("请求头格式不正确：" + line); }
            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (!request.Headers.TryAddWithoutValidation(name, value)) contentHeaders.Add((name, value));
        }
        if (body.Length > 0 || contentHeaders.Count > 0)
        {
            request.Content = new StringContent(body, new UTF8Encoding(false));
            request.Content.Headers.ContentType = null;
            foreach (var (name, value) in contentHeaders) request.Content.Headers.TryAddWithoutValidation(name, value);
            if (request.Content.Headers.ContentType == null && body.Length > 0)
            {
                var t = body.TrimStart();
                request.Content.Headers.TryAddWithoutValidation("Content-Type", t.StartsWith("{") || t.StartsWith("[") ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
            }
        }
        return request;
    }

    static string Decode(byte[] bytes, string? charset)
    {
        const int Max = 2 * 1024 * 1024;
        bool truncated = bytes.Length > Max;
        if (truncated) Array.Resize(ref bytes, Max);
        string text;
        try { text = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')).GetString(bytes) : TextFiles.Decode(bytes, out _); }
        catch (ArgumentException) { text = TextFiles.Decode(bytes, out _); }
        return truncated ? text + "\r\n\r\n（内容超过 2 MB，已截断）" : text;
    }

    static string Pretty(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("{") && !t.StartsWith("[")) return text;
        try { return JToken.Parse(text).ToString(Formatting.Indented); }
        catch (JsonException) { return text; }
    }

    // cURL

    void CopyCurl()
    {
        var missing = new HashSet<string>();
        var vars = Environment?.Variables ?? "";
        var sb = new StringBuilder("curl");
        var method = _method.SelectedItem as string ?? "GET";
        if (method != "GET") sb.Append(" -X ").Append(method);
        sb.Append(' ').Append(Quote(Expand(_url.Text.Trim(), vars, missing)));
        foreach (var line in Expand(_headers.Text, vars, missing).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            if (!line.TrimStart().StartsWith("#")) sb.Append(" -H ").Append(Quote(line.Trim()));
        var body = Expand(_body.Text, vars, missing);
        if (body.Length > 0) sb.Append(" --data-raw ").Append(Quote(body));
        Clipboard.SetText(sb.ToString());
        Ui.SetStatus(_status, missing.Count > 0 ? "已复制，但这些变量没有值：" + string.Join("、", missing) : "已复制 cURL 命令", missing.Count > 0);
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    void ImportCurl()
    {
        var text = Ai.AiService.AskText(Window.GetWindow(this), "导入 cURL", "粘贴一条 curl 命令（浏览器开发者工具里“复制为 cURL”）", Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("curl") ? Clipboard.GetText() : "");
        if (string.IsNullOrWhiteSpace(text)) return;
        var r = ParseCurl(text!);
        if (r == null) { Ui.SetStatus(_status, "看不懂这条命令，要以 curl 开头并带 URL", true); return; }
        AddRequest(r);
        Ui.SetStatus(_status, "已导入");
    }

    /// <summary>The common curl options: -X, -H, -d and friends, -u, and the URL.</summary>
    public static ApiRequest? ParseCurl(string command)
    {
        var args = Tokenize(command.Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("^\r\n", " ").Replace("^\n", " "));
        if (args.Count == 0 || !args[0].Equals("curl", StringComparison.OrdinalIgnoreCase) && !args[0].EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var r = new ApiRequest { Method = "" };
        var headers = new List<string>();
        var body = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : "";
            switch (a)
            {
                case "-X": case "--request": r.Method = Next().ToUpperInvariant(); break;
                case "-H": case "--header": headers.Add(Next()); break;
                case "-d": case "--data": case "--data-raw": case "--data-binary": case "--data-ascii": case "--data-urlencode": body.Add(Next()); break;
                case "--json": body.Add(Next()); headers.Add("Content-Type: application/json"); break;
                case "-u": case "--user": headers.Add("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Next()))); break;
                case "-A": case "--user-agent": headers.Add("User-Agent: " + Next()); break;
                case "-b": case "--cookie": headers.Add("Cookie: " + Next()); break;
                case "-e": case "--referer": headers.Add("Referer: " + Next()); break;
                case "--url": r.Url = Next(); break;
                case "-I": case "--head": r.Method = "HEAD"; break;
                default:
                    if (a.StartsWith("-")) { if (a is "-o" or "--output" or "-m" or "--max-time" or "--connect-timeout" or "-x" or "--proxy") Next(); }
                    else if (r.Url.Length == 0) r.Url = a;
                    break;
            }
        }
        if (r.Url.Length == 0) return null;
        r.Body = string.Join("&", body);
        r.Headers = string.Join("\r\n", headers);
        if (r.Method.Length == 0) r.Method = body.Count > 0 ? "POST" : "GET";
        r.Name = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.AbsolutePath : r.Url;
        return r;
    }

    /// <summary>Splits like a shell: 'single', "double" with \" escapes, and $'...' from Chrome's bash copy.</summary>
    static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { if (any) { list.Add(sb.ToString()); sb.Clear(); any = false; } continue; }
            any = true;
            if (c == '$' && i + 1 < s.Length && s[i + 1] == '\'') continue;
            if (c == '\'')
            {
                int end = s.IndexOf('\'', i + 1);
                if (end < 0) end = s.Length;
                sb.Append(s, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                for (i++; i < s.Length && s[i] != '"'; i++)
                {
                    if ((s[i] == '\\' || s[i] == '^') && i + 1 < s.Length && s[i + 1] == '"') i++;
                    sb.Append(s[i]);
                }
            }
            else if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
            else sb.Append(c);
        }
        if (any) list.Add(sb.ToString());
        return list;
    }

    // AI

    async Task Generate()
    {
        var ask = _prompt.Text.Trim();
        if (ask.Length == 0) { Ui.SetStatus(_status, "先写上想要什么请求", true); return; }
        if (_ai == null) { Ui.SetStatus(_status, "AI 助手还没有开启，去设置里开启", true); return; }
        Ui.SetStatus(_status, "AI 正在写请求…");
        var names = string.Join("、", (Environment?.Variables ?? "").Split('\n').Select(l => l.Split('=')[0].Trim()).Where(n => n.Length > 0));
        var prompt = "按下面的描述写一个 HTTP 请求，只回复一个 JSON 对象，不要代码块，不要解释。格式：" +
                     "{\"name\":\"简短中文名\",\"method\":\"GET\",\"url\":\"...\",\"headers\":\"每行 名称: 值\",\"body\":\"请求体原文，没有就空字符串\"}。" +
                     (names.Length > 0 ? $"可以用 {{{{变量}}}} 引用环境变量，现有变量：{names}。" : "") +
                     (_current != null && _current.Url.Length > 0 ? $"\n当前请求供参考：{_current.Method} {_current.Url}\n{_current.Headers}\n{_current.Body}" : "") +
                     "\n描述：" + ask;
        try
        {
            var answer = await _ai(prompt);
            var r = ParseAi(answer);
            if (r == null) { Ui.SetStatus(_status, "AI 的回复不是请求格式：" + answer.Trim(), true); return; }
            AddRequest(r);
            _prompt.Clear();
            Ui.SetStatus(_status, "AI 写好了“" + r.Name + "”，检查一下再发送");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Ui.SetStatus(_status, "AI 出错：" + ex.Message, true);
        }
    }

    public static ApiRequest? ParseAi(string answer)
    {
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var o = JObject.Parse(answer.Substring(start, end - start + 1));
            var url = (string?)o["url"] ?? "";
            if (url.Length == 0) return null;
            var headers = o["headers"] is JObject h ? string.Join("\r\n", h.Properties().Select(p => $"{p.Name}: {p.Value}")) : (string?)o["headers"] ?? "";
            var body = o["body"] is JObject or JArray ? o["body"]!.ToString(Formatting.Indented) : (string?)o["body"] ?? "";
            var method = ((string?)o["method"] ?? "GET").ToUpperInvariant();
            return new ApiRequest
            {
                Name = (string?)o["name"] is { Length: > 0 } n ? n : method + " " + url,
                Method = method,
                Url = url,
                Headers = headers.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                Body = body.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
        }
        catch (JsonException) { return null; }
    }
}
