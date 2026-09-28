using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.DevTools.Api;

/// <summary>The variable scopes a request sees, narrowest first: local, data, environment, collection, global.</summary>
sealed class ApiVariables
{
    public static readonly Regex Pattern = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    static readonly Random Rng = new();

    public List<KeyValue> Globals { get; }
    public List<KeyValue> Collection { get; }
    public List<KeyValue> Environment { get; }
    public Dictionary<string, string> Data { get; }
    public Dictionary<string, string> Locals { get; }

    public ApiVariables(List<KeyValue>? globals, List<KeyValue>? collection, List<KeyValue>? environment, Dictionary<string, string>? data = null, Dictionary<string, string>? locals = null)
    {
        Globals = globals ?? new();
        Collection = collection ?? new();
        Environment = environment ?? new();
        Data = data ?? new();
        Locals = locals ?? new();
    }

    public List<KeyValue>? Scope(string name) => name switch
    {
        "globals" => Globals,
        "collection" => Collection,
        "environment" => Environment,
        _ => null,
    };

    string[]? _taken;

    /// <summary>Copies of the variable lists for a script to change off the UI thread; locals and data are shared.</summary>
    public ApiVariables Snapshot()
    {
        var copy = new ApiVariables(Globals.Clone(), Collection.Clone(), Environment.Clone(), Data, Locals);
        copy._taken = copy.Signatures();
        return copy;
    }

    /// <summary>Takes over the lists a script changed in <paramref name="copy"/>.</summary>
    public void Merge(ApiVariables copy)
    {
        if (copy._taken == null) return;
        var now = copy.Signatures();
        if (now[0] != copy._taken[0]) Replace(Globals, copy.Globals);
        if (now[1] != copy._taken[1]) Replace(Collection, copy.Collection);
        if (now[2] != copy._taken[2]) Replace(Environment, copy.Environment);
    }

    string[] Signatures() => new[] { JsonConvert.SerializeObject(Globals), JsonConvert.SerializeObject(Collection), JsonConvert.SerializeObject(Environment) };

    /// <summary>Copies row by row so editors holding the existing rows keep working.</summary>
    static void Replace(List<KeyValue> target, List<KeyValue> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (i >= target.Count) { target.Add(source[i]); continue; }
            var t = target[i];
            var s = source[i];
            t.Enabled = s.Enabled; t.Key = s.Key; t.Value = s.Value; t.Description = s.Description; t.Type = s.Type; t.Secret = s.Secret;
        }
        if (target.Count > source.Count) target.RemoveRange(source.Count, target.Count - source.Count);
    }

    /// <summary>The value and the scope it came from, or null when no scope has it.</summary>
    public (string Value, string Scope)? Find(string name)
    {
        if (Locals.TryGetValue(name, out var v)) return (v, "局部");
        if (Data.TryGetValue(name, out v)) return (v, "数据文件");
        if (Environment.Active().LastOrDefault(k => k.Key == name) is { } e) return (e.Value, "环境");
        if (Collection.Active().LastOrDefault(k => k.Key == name) is { } c) return (c.Value, "集合");
        if (Globals.Active().LastOrDefault(k => k.Key == name) is { } g) return (g.Value, "全局");
        return Dynamic(name) is { } d ? (d, "动态") : null;
    }

    public string? Get(string name) => Find(name)?.Value;

    public static void Set(List<KeyValue> scope, string name, string value)
    {
        var row = scope.LastOrDefault(k => k.Key == name);
        if (row == null) scope.Add(new KeyValue { Key = name, Value = value });
        else { row.Value = value; row.Enabled = true; }
    }

    static string? Dynamic(string name) => name switch
    {
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        "$isoTimestamp" or "$isoDate" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$randomInt" => Rng.Next(0, 1001).ToString(),
        "$randomBoolean" => Rng.Next(2) == 0 ? "false" : "true",
        _ => null,
    };

    /// <summary>Replaces {{name}}, a few levels deep so values may refer to other variables; unknown names stay and are listed in <paramref name="missing"/>.</summary>
    public string Expand(string text, ISet<string>? missing = null)
    {
        if (text.IndexOf("{{", StringComparison.Ordinal) < 0) return text;
        for (int depth = 0; depth < 5; depth++)
        {
            bool changed = false;
            text = Pattern.Replace(text, m =>
            {
                var v = Get(m.Groups[1].Value);
                if (v == null) return m.Value;
                changed = true;
                return v;
            });
            if (!changed) break;
        }
        if (missing != null) foreach (Match m in Pattern.Matches(text)) missing.Add(m.Groups[1].Value);
        return text;
    }
}

/// <summary>A request with variables filled in and auth applied, ready to send or turn into code.</summary>
sealed class PreparedRequest
{
    public string Method = "GET";
    public string Url = "";
    public List<KeyValuePair<string, string>> Headers = new();
    /// <summary>Expanded copy of the request body.</summary>
    public ApiBody Body = new();
    public ApiOptions Options = new();

    public string? Header(string name) => Headers.LastOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public bool HasHeader(string name) => Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The body as text for code generation and history; null when there is none.</summary>
    public string? BodyText => Body.Mode switch
    {
        BodyModes.Raw => Body.Raw,
        BodyModes.UrlEncoded => ApiEngine.FormEncode(Body.UrlEncoded.Active()),
        _ => null,
    };
}

sealed class ApiCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "";
    public string Expires { get; set; } = "";
    public bool HttpOnly { get; set; }
    public bool Secure { get; set; }
}

sealed class ApiResponse
{
    public int Status;
    public string Reason = "";
    public string Version = "";
    public string FinalUrl = "";
    public List<KeyValuePair<string, string>> Headers = new();
    public List<ApiCookie> Cookies = new();
    public byte[] Bytes = Array.Empty<byte>();
    public string Text = "";
    public string ContentType = "";
    public long Milliseconds;

    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public bool IsJson => ContentType.Contains("json") || Text.TrimStart().StartsWith("{") || Text.TrimStart().StartsWith("[");
    public bool IsImage => ContentType.StartsWith("image/");
    /// <summary>An image with no text to show; SVG is XML and stays readable.</summary>
    public bool IsBitmap => IsImage && !ContentType.Contains("svg");
    public bool IsHtml => ContentType.Contains("html");

    JToken? _json;
    bool _parsed;

    /// <summary>The body as JSON, parsed once; null when it isn't JSON or is over 20 MB. Sending parses it off the UI thread.</summary>
    public JToken? Json
    {
        get
        {
            if (_parsed) return _json;
            _parsed = true;
            if (IsJson && Text.Length < 20 * 1024 * 1024)
                try { _json = JToken.Parse(Text); } catch (JsonReaderException) { }
            return _json;
        }
    }
}

sealed class TestResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>Everything a request run produced.</summary>
sealed class ExecResult
{
    public PreparedRequest? Request;
    public ApiResponse? Response;
    public List<TestResult> Tests = new();
    public List<string> Console = new();
    public SortedSet<string> Missing = new();
    public string? Error;
    /// <summary>Set by pm.execution.setNextRequest: a name, "" for none, null when not called.</summary>
    public string? NextRequest;
    public bool Skipped;
}

/// <summary>Where a request runs: its collection, folders and the variables it can read and change.</summary>
sealed class ExecContext
{
    public ApiCollection? Collection;
    /// <summary>The collection and folders above the request, outermost first.</summary>
    public List<ApiFolder> Path = new();
    public ApiVariables Variables = new(null, null, null);
    public string EnvironmentName = "";
    public int Iteration;
    public int IterationCount = 1;
}

static class ApiEngine
{
    public const long MaxBody = 50L * 1024 * 1024;
    public static readonly CookieContainer Cookies = new();
    static readonly Dictionary<string, HttpClient> Clients = new();
    static readonly Func<HttpRequestMessage, System.Security.Cryptography.X509Certificates.X509Certificate2, System.Security.Cryptography.X509Certificates.X509Chain, System.Net.Security.SslPolicyErrors, bool> AnyCertificate = (_, _, _, _) => true;

    static HttpClient Client(ApiOptions o)
    {
        var key = $"{o.FollowRedirects}|{o.IgnoreSsl}|{o.Proxy.Trim()}";
        lock (Clients)
        {
            if (Clients.TryGetValue(key, out var client)) return client;
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = o.FollowRedirects,
                MaxAutomaticRedirections = 10,
                UseCookies = true,
                CookieContainer = Cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            if (o.IgnoreSsl) handler.ServerCertificateCustomValidationCallback = AnyCertificate;
            var proxy = o.Proxy.Trim();
            if (proxy.Equals("direct", StringComparison.OrdinalIgnoreCase)) handler.UseProxy = false;
            else if (proxy.Length > 0)
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxy.Contains("://") ? proxy : "http://" + proxy);
            }
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            Clients[key] = client;
            return client;
        }
    }

    /// <summary>The request's own auth, or the nearest folder's or collection's when it inherits.</summary>
    public static ApiAuth ResolveAuth(ApiAuth own, IReadOnlyList<ApiFolder> path)
    {
        if (own.Type != AuthTypes.Inherit) return own;
        for (int i = path.Count - 1; i >= 0; i--)
            if (path[i].Auth.Type != AuthTypes.Inherit) return path[i].Auth;
        return new ApiAuth { Type = AuthTypes.None };
    }

    public static string FormEncode(IEnumerable<KeyValue> fields) =>
        string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));

    static readonly Regex MethodToken = new(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.Compiled);

    /// <summary>Fills in variables, applies auth and default headers.</summary>
    public static PreparedRequest Prepare(ApiRequest r, ApiAuth auth, ApiVariables vars, ISet<string> missing)
    {
        string X(string s) => vars.Expand(s, missing);
        var p = new PreparedRequest { Method = (r.Method.Trim().Length > 0 ? r.Method.Trim() : "GET").ToUpperInvariant(), Options = r.Options.Clone() };
        if (!MethodToken.IsMatch(p.Method)) throw new InvalidOperationException("请求方法不对：" + p.Method);
        p.Options.Proxy = X(p.Options.Proxy);

        var url = X(r.Url.Trim());
        if (url.Length > 0 && !Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*://")) url = "http://" + url;

        foreach (var h in r.Headers.Active()) p.Headers.Add(new(X(h.Key), X(h.Value)));

        switch (auth.Type)
        {
            case AuthTypes.Bearer when auth.Token.Length > 0 && !p.HasHeader("Authorization"):
                p.Headers.Add(new("Authorization", "Bearer " + X(auth.Token)));
                break;
            case AuthTypes.Basic when !p.HasHeader("Authorization"):
                p.Headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(X(auth.Username) + ":" + X(auth.Password)))));
                break;
            case AuthTypes.ApiKey when auth.Key.Length > 0:
                if (auth.In == "query") url = UrlParams.Append(url, X(auth.Key), X(auth.Value));
                else if (!p.HasHeader(X(auth.Key))) p.Headers.Add(new(X(auth.Key), X(auth.Value)));
                break;
        }
        p.Url = url;

        var b = r.Body.Clone();
        b.Raw = X(b.Raw);
        b.File = X(b.File);
        foreach (var f in b.UrlEncoded.Concat(b.FormData)) { f.Key = X(f.Key); f.Value = X(f.Value); }
        p.Body = b;
        if (!p.HasHeader("Content-Type"))
        {
            if (b.Mode == BodyModes.Raw && b.Raw.Length > 0) p.Headers.Add(new("Content-Type", b.ContentType));
            else if (b.Mode == BodyModes.UrlEncoded) p.Headers.Add(new("Content-Type", "application/x-www-form-urlencoded"));
        }
        if (!p.HasHeader("User-Agent")) p.Headers.Add(new("User-Agent", "SeedToolBox"));
        if (!p.HasHeader("Accept")) p.Headers.Add(new("Accept", "*/*"));
        return p;
    }

    static HttpRequestMessage Build(PreparedRequest p, Uri uri)
    {
        var message = new HttpRequestMessage(new HttpMethod(p.Method), uri);
        HttpContent? content = null;
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0:
                content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(p.Body.Raw));
                break;
            case BodyModes.UrlEncoded:
                content = new ByteArrayContent(Encoding.ASCII.GetBytes(FormEncode(p.Body.UrlEncoded.Active())));
                break;
            case BodyModes.FormData:
                var multi = new MultipartFormDataContent("----SeedToolBox" + Guid.NewGuid().ToString("N"));
                foreach (var f in p.Body.FormData.Active())
                {
                    if (f.Type == "file")
                    {
                        if (!File.Exists(f.Value)) throw new FileNotFoundException("找不到表单里的文件：" + f.Value);
                        var part = new ByteArrayContent(File.ReadAllBytes(f.Value));
                        part.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(f.Value));
                        multi.Add(part, Quoted(f.Key), Quoted(System.IO.Path.GetFileName(f.Value)));
                    }
                    else multi.Add(new StringContent(f.Value, new UTF8Encoding(false)), Quoted(f.Key));
                }
                content = multi;
                break;
            case BodyModes.Binary when p.Body.File.Length > 0:
                if (!File.Exists(p.Body.File)) throw new FileNotFoundException("找不到要发送的文件：" + p.Body.File);
                content = new ByteArrayContent(File.ReadAllBytes(p.Body.File));
                if (!p.HasHeader("Content-Type")) content.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(p.Body.File));
                break;
        }
        foreach (var h in p.Headers)
        {
            if (message.Headers.TryAddWithoutValidation(h.Key, h.Value)) continue;
            if (content == null) content = new ByteArrayContent(Array.Empty<byte>());
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && content is MultipartFormDataContent) continue;
            content.Headers.Remove(h.Key);
            content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        message.Content = content;
        return message;
    }

    static string Quoted(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static string MimeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".txt" or ".log" or ".csv" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    public static async Task<ApiResponse> SendAsync(PreparedRequest p, CancellationToken cancel)
    {
        if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("URL 不对，应以 http:// 或 https:// 开头：" + p.Url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (p.Options.TimeoutSeconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(p.Options.TimeoutSeconds));
        var watch = Stopwatch.StartNew();
        try
        {
            using var message = Build(p, uri);
            using var response = await Client(p.Options).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadAsync(response.Content, timeout.Token).ConfigureAwait(false);
            watch.Stop();
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var r = new ApiResponse
            {
                Status = (int)response.StatusCode,
                Reason = response.ReasonPhrase ?? "",
                Version = response.Version.ToString(),
                FinalUrl = finalUri.ToString(),
                Bytes = bytes,
                ContentType = response.Content.Headers.ContentType?.MediaType ?? "",
                Milliseconds = watch.ElapsedMilliseconds,
            };
            foreach (var h in response.Headers.Concat(response.Content.Headers))
                foreach (var v in h.Value) r.Headers.Add(new(h.Key, v));
            r.Text = r.IsBitmap ? "" : Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            _ = r.Json;
            foreach (Cookie c in Cookies.GetCookies(finalUri))
                r.Cookies.Add(new ApiCookie
                {
                    Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, HttpOnly = c.HttpOnly, Secure = c.Secure,
                    Expires = c.Expires == DateTime.MinValue ? "会话" : c.Expires.ToString("yyyy-MM-dd HH:mm:ss"),
                });
            return r;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"请求超时（{p.Options.TimeoutSeconds} 秒）");
        }
    }

    static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancel)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, n);
            if (memory.Length > MaxBody) throw new InvalidOperationException("响应超过 50 MB，已停止接收");
        }
        return memory.ToArray();
    }

    static string Decode(byte[] bytes, string? charset)
    {
        try { return charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')).GetString(bytes) : TextFiles.Decode(bytes, out _); }
        catch (ArgumentException) { return TextFiles.Decode(bytes, out _); }
    }

    /// <summary>Pre-request scripts, variables, auth, sending and tests: one request the way Postman runs it.</summary>
    public static async Task<ExecResult> ExecuteAsync(ApiRequest request, ExecContext ctx, CancellationToken cancel)
    {
        var result = new ExecResult();
        var work = request.Clone();
        var info = new ScriptInfo(work.Name, ctx.Iteration, ctx.IterationCount, ctx.EnvironmentName);

        var pre = ctx.Path.Select(f => f.PreScript).Append(work.PreScript).Where(s => s.Trim().Length > 0).ToList();
        foreach (var script in pre)
        {
            if (!await RunScript(script, "prerequest", ctx.Variables, work, null, null, info, result, cancel)) return result;
            if (result.Skipped) return result;
        }

        PreparedRequest prepared;
        try
        {
            var auth = ResolveAuth(work.Auth, ctx.Path);
            prepared = result.Request = Prepare(work, auth, ctx.Variables, result.Missing);
            result.Response = await SendAsync(prepared, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException or IOException or WebException or ArgumentException or UriFormatException or ProtocolViolationException)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            result.Error = ex is HttpRequestException ? "请求失败：" + inner.Message : ex.Message;
            return result;
        }

        var tests = ctx.Path.Select(f => f.TestScript).Append(work.TestScript).Where(s => s.Trim().Length > 0).ToList();
        foreach (var script in tests)
            if (!await RunScript(script, "test", ctx.Variables, work, prepared, result.Response, info, result, cancel)) break;
        return result;
    }

    /// <summary>
    /// Runs a script on the thread pool against copies of the variable lists, which the UI may be saving or editing meanwhile;
    /// the changes are copied back once the script is done, on the caller's thread.
    /// </summary>
    static async Task<bool> RunScript(string script, string phase, ApiVariables vars, ApiRequest work, PreparedRequest? prepared, ApiResponse? response, ScriptInfo info, ExecResult result, CancellationToken cancel)
    {
        var copy = vars.Snapshot();
        var scriptResult = new ExecResult();
        var host = new ScriptHost(phase, copy, work, prepared, response, info, scriptResult);
        var ok = await Task.Run(() => ApiScript.Run(script, host, cancel), cancel);
        vars.Merge(copy);
        result.Console.AddRange(scriptResult.Console);
        result.Tests.AddRange(scriptResult.Tests);
        if (scriptResult.Error != null) result.Error = scriptResult.Error;
        if (scriptResult.NextRequest != null) result.NextRequest = scriptResult.NextRequest;
        if (scriptResult.Skipped) result.Skipped = true;
        return ok;
    }
}

/// <summary>Keeps the Params table and the URL's query string in step.</summary>
static class UrlParams
{
    /// <summary>The query of <paramref name="url"/> as rows, without decoding so {{variables}} survive.</summary>
    public static List<KeyValue> Parse(string url)
    {
        var list = new List<KeyValue>();
        int q = url.IndexOf('?');
        if (q < 0) return list;
        var query = url.Substring(q + 1);
        int hash = query.IndexOf('#');
        if (hash >= 0) query = query.Substring(0, hash);
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            list.Add(eq < 0 ? new KeyValue { Key = part } : new KeyValue { Key = part.Substring(0, eq), Value = part.Substring(eq + 1) });
        }
        return list;
    }

    /// <summary><paramref name="url"/> with its query rebuilt from the enabled rows.</summary>
    public static string Build(string url, IEnumerable<KeyValue> rows)
    {
        int q = url.IndexOf('?');
        var hashIndex = url.IndexOf('#', q < 0 ? 0 : q);
        var fragment = hashIndex >= 0 ? url.Substring(hashIndex) : "";
        var baseUrl = q >= 0 ? url.Substring(0, q) : hashIndex >= 0 ? url.Substring(0, hashIndex) : url;
        var query = string.Join("&", rows.Where(r => r.Enabled && (r.Key.Length > 0 || r.Value.Length > 0)).Select(r => r.Value.Length > 0 ? r.Key + "=" + r.Value : r.Key));
        return baseUrl + (query.Length > 0 ? "?" + query : "") + fragment;
    }

    public static string Append(string url, string key, string value)
    {
        var pair = Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
        int hash = url.IndexOf('#');
        var fragment = hash >= 0 ? url.Substring(hash) : "";
        if (hash >= 0) url = url.Substring(0, hash);
        return url + (url.Contains("?") ? "&" : "?") + pair + fragment;
    }
}

/// <summary>Data\api.json, with the move from the first version where headers, body and variables were plain text.</summary>
static class ApiStore
{
    public static readonly string FilePath = System.IO.Path.Combine(AppPaths.Data, "api.json");
    public const int MaxHistory = 300;

    public static ApiData Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Reading API requests failed", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
        return new ApiData();
    }

    public static ApiData Parse(string json)
    {
        var root = JObject.Parse(json);
        if ((int?)root["Version"] is null or < 2) Migrate(root);
        Secrets(root, Reveal);
        return root.ToObject<ApiData>() ?? new ApiData();
    }

    // Auth fields, secret variables and credential headers are stored with DPAPI; files from before are read as plain text.
    const string ProtectedPrefix = "dpapi:";
    static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase) { "Authorization", "Proxy-Authorization", "Cookie", "X-Api-Key", "Api-Key" };

    static string Protect(string value) => value.Length == 0 || value.StartsWith(ProtectedPrefix) ? value : ProtectedPrefix + Sync.SyncCrypto.Protect(value);
    static string Reveal(string stored) => stored.StartsWith(ProtectedPrefix) ? Sync.SyncCrypto.Unprotect(stored.Substring(ProtectedPrefix.Length)) : stored;

    /// <summary>Applies <paramref name="convert"/> to every secret string in the file: auth fields anywhere (requests, tabs, history) and secret or credential rows.</summary>
    static void Secrets(JToken token, Func<string, string> convert)
    {
        void Apply(JObject o, string name)
        {
            if (o[name] is JValue { Type: JTokenType.String } v) o[name] = convert((string)v!);
        }
        foreach (var o in (token is JContainer c ? c.DescendantsAndSelf() : new[] { token }).OfType<JObject>().ToList())
        {
            if (o["Auth"] is JObject auth)
            {
                Apply(auth, nameof(ApiAuth.Token));
                Apply(auth, nameof(ApiAuth.Password));
                Apply(auth, nameof(ApiAuth.Value));
            }
            if (o["Key"] is JValue { Type: JTokenType.String } key && o["Secret"] is JValue secret && ((bool?)secret == true || SecretHeaders.Contains((string)key!)))
                Apply(o, nameof(KeyValue.Value));
        }
    }

    static void Migrate(JObject root)
    {
        foreach (var c in root["Collections"] as JArray ?? new JArray())
            foreach (var r in c["Requests"] as JArray ?? new JArray())
            {
                if (r["Headers"]?.Type == JTokenType.String) r["Headers"] = JArray.FromObject(TextHeaders((string)r["Headers"]!));
                if (r["Body"]?.Type == JTokenType.String)
                {
                    var body = (string)r["Body"]!;
                    var t = body.TrimStart();
                    r["Body"] = JObject.FromObject(new ApiBody
                    {
                        Mode = body.Length > 0 ? BodyModes.Raw : BodyModes.None,
                        Raw = body,
                        Language = t.StartsWith("{") || t.StartsWith("[") ? "json" : t.StartsWith("<") ? "xml" : "text",
                    });
                }
                if (r["Url"]?.Type == JTokenType.String) r["Params"] = JArray.FromObject(UrlParams.Parse((string)r["Url"]!));
            }
        var active = (string?)root["ActiveEnvironment"] ?? "";
        foreach (var e in root["Environments"] as JArray ?? new JArray())
        {
            if (e["Variables"]?.Type == JTokenType.String) e["Variables"] = JArray.FromObject(TextVariables((string)e["Variables"]!));
            // v1 picked the active environment by name; v2 by id.
            if (e["Id"] == null) e["Id"] = Guid.NewGuid().ToString("N");
            if ((string?)e["Name"] == active) root["ActiveEnvironment"] = e["Id"];
        }
        root["Version"] = 2;
    }

    /// <summary>"Name: value" lines; lines starting with # become disabled rows.</summary>
    public static List<KeyValue> TextHeaders(string text)
    {
        var list = new List<KeyValue>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            bool off = line.StartsWith("#") || line.StartsWith("//");
            line = line.TrimStart('#', '/').Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            list.Add(new KeyValue { Enabled = !off, Key = line.Substring(0, colon).Trim(), Value = line.Substring(colon + 1).Trim() });
        }
        return list;
    }

    static List<KeyValue> TextVariables(string text)
    {
        var list = new List<KeyValue>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            bool off = line.StartsWith("#");
            line = line.TrimStart('#').Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            list.Add(new KeyValue { Enabled = !off, Key = line.Substring(0, eq).Trim(), Value = line.Substring(eq + 1).Trim() });
        }
        return list;
    }

    public static void Save(ApiData data)
    {
        if (data.History.Count > MaxHistory) data.History.RemoveRange(0, data.History.Count - MaxHistory);
        Directory.CreateDirectory(AppPaths.Data);
        var tmp = FilePath + ".tmp";
        var root = JObject.FromObject(data);
        Secrets(root, Protect);
        File.WriteAllText(tmp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }
}
