using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.DevTools.Api;

/// <summary>Postman collections and environments (v2.0 and v2.1), cURL commands and AI-written requests.</summary>
static class ApiImport
{
    const string Schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    // Postman collections

    public static bool IsPostmanCollection(JObject o) => o["info"] is JObject && o["item"] is JArray;
    public static bool IsPostmanEnvironment(JObject o) => o["values"] is JArray && o["info"] == null;

    public static ApiCollection ReadCollection(JObject o)
    {
        var c = new ApiCollection
        {
            Name = (string?)o["info"]?["name"] ?? "导入的集合",
            Description = Text(o["info"]?["description"]),
            Auth = ReadAuth(o["auth"]) ?? new ApiAuth { Type = AuthTypes.None },
        };
        ReadEvents(o["event"], c);
        foreach (var v in o["variable"] as JArray ?? new JArray())
            c.Variables.Add(new KeyValue { Key = (string?)v["key"] ?? "", Value = Text(v["value"]), Enabled = v["disabled"]?.Value<bool>() != true });
        ReadItems(o["item"] as JArray, c);
        return c;
    }

    static void ReadItems(JArray? items, ApiFolder into)
    {
        foreach (var item in items?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            if (item["item"] is JArray children)
            {
                var f = new ApiFolder { Name = (string?)item["name"] ?? "文件夹", Description = Text(item["description"]) };
                f.Auth = ReadAuth(item["auth"]) ?? new ApiAuth();
                ReadEvents(item["event"], f);
                ReadItems(children, f);
                into.Folders.Add(f);
            }
            else into.Requests.Add(ReadRequest(item));
        }
    }

    static ApiRequest ReadRequest(JObject item)
    {
        var r = new ApiRequest { Name = (string?)item["name"] ?? "请求" };
        var q = item["request"];
        if (q?.Type == JTokenType.String) { r.Url = (string)q!; q = null; }
        if (q is JObject req)
        {
            r.Method = ((string?)req["method"] ?? "GET").ToUpperInvariant();
            r.Url = req["url"] is JObject u ? (string?)u["raw"] ?? "" : Text(req["url"]);
            if (req["url"] is JObject url && url["query"] is JArray query)
            {
                // Disabled query rows only live in the table; enabled ones are part of the raw URL.
                foreach (var p in query.Where(p => p["disabled"]?.Value<bool>() == true))
                    r.Params.Add(new KeyValue { Enabled = false, Key = (string?)p["key"] ?? "", Value = Text(p["value"]), Description = Text(p["description"]) });
            }
            r.Params.InsertRange(0, UrlParams.Parse(r.Url));
            r.Headers = Rows(req["header"]);
            r.Description = Text(req["description"]);
            r.Auth = ReadAuth(req["auth"]) ?? new ApiAuth();
            if (req["body"] is JObject body) r.Body = ReadBody(body);
        }
        ReadEvents(item["event"], r);
        foreach (var e in item["response"] as JArray ?? new JArray())
            r.Examples.Add(new ApiExample
            {
                Name = (string?)e["name"] ?? "示例",
                Status = (int?)e["code"] ?? 0,
                StatusText = (string?)e["status"] ?? "",
                Headers = Rows(e["header"]),
                Body = Text(e["body"]),
            });
        return r;
    }

    static ApiBody ReadBody(JObject body)
    {
        var b = new ApiBody { Mode = (string?)body["mode"] ?? BodyModes.None };
        switch (b.Mode)
        {
            case BodyModes.Raw:
                b.Raw = Text(body["raw"]);
                b.Language = (string?)body["options"]?["raw"]?["language"] ?? (b.Raw.TrimStart().StartsWith("{") || b.Raw.TrimStart().StartsWith("[") ? "json" : "text");
                break;
            case BodyModes.UrlEncoded: b.UrlEncoded = Rows(body["urlencoded"]); break;
            case BodyModes.FormData:
                foreach (var f in body["formdata"] as JArray ?? new JArray())
                {
                    bool file = (string?)f["type"] == "file";
                    b.FormData.Add(new KeyValue
                    {
                        Key = (string?)f["key"] ?? "",
                        Value = file ? (f["src"] is JArray a ? (string?)a.FirstOrDefault() ?? "" : Text(f["src"])) : Text(f["value"]),
                        Type = file ? "file" : "text",
                        Enabled = f["disabled"]?.Value<bool>() != true,
                        Description = Text(f["description"]),
                    });
                }
                break;
            case BodyModes.Binary: b.File = Text(body["file"]?["src"]); break;
            case "graphql":
                b.Mode = BodyModes.Raw;
                b.Language = "json";
                b.Raw = new JObject { ["query"] = body["graphql"]?["query"], ["variables"] = TryParse(Text(body["graphql"]?["variables"])) }.ToString(Formatting.Indented);
                break;
            default: b.Mode = BodyModes.None; break;
        }
        return b;
    }

    static JToken TryParse(string s)
    {
        try { return s.Trim().Length > 0 ? JToken.Parse(s) : new JObject(); }
        catch (JsonReaderException) { return s; }
    }

    static ApiAuth? ReadAuth(JToken? token)
    {
        if (token is not JObject a) return null;
        var type = (string?)a["type"] ?? "";
        string Field(string name)
        {
            var t = a[type];
            if (t is JArray list) return Text(list.FirstOrDefault(p => (string?)p["key"] == name)?["value"]);
            return t is JObject o ? Text(o[name]) : "";
        }
        return type switch
        {
            "noauth" => new ApiAuth { Type = AuthTypes.None },
            "bearer" => new ApiAuth { Type = AuthTypes.Bearer, Token = Field("token") },
            "basic" => new ApiAuth { Type = AuthTypes.Basic, Username = Field("username"), Password = Field("password") },
            "apikey" => new ApiAuth { Type = AuthTypes.ApiKey, Key = Field("key"), Value = Field("value"), In = Field("in") == "query" ? "query" : "header" },
            _ => null,
        };
    }

    static void ReadEvents(JToken? events, object target)
    {
        foreach (var e in events as JArray ?? new JArray())
        {
            var exec = e["script"]?["exec"];
            var code = exec is JArray lines ? string.Join("\r\n", lines.Select(l => (string?)l ?? "")) : Text(exec);
            switch ((string?)e["listen"], target)
            {
                case ("prerequest", ApiFolder f): f.PreScript = code; break;
                case ("test", ApiFolder f): f.TestScript = code; break;
                case ("prerequest", ApiRequest r): r.PreScript = code; break;
                case ("test", ApiRequest r): r.TestScript = code; break;
            }
        }
    }

    static List<KeyValue> Rows(JToken? token)
    {
        var list = new List<KeyValue>();
        if (token?.Type == JTokenType.String) return ApiStore.TextHeaders((string)token!);
        foreach (var h in token as JArray ?? new JArray())
            list.Add(new KeyValue { Key = (string?)h["key"] ?? "", Value = Text(h["value"]), Enabled = h["disabled"]?.Value<bool>() != true, Description = Text(h["description"]) });
        return list;
    }

    /// <summary>Strings, or {content} descriptions, or other values as JSON.</summary>
    static string Text(JToken? t) => t switch
    {
        null => "",
        { Type: JTokenType.Null } => "",
        { Type: JTokenType.String } => (string)t!,
        JObject o when o["content"] != null => (string?)o["content"] ?? "",
        JValue v => Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => t.ToString(Formatting.None),
    };

    public static JObject WriteCollection(ApiCollection c)
    {
        var o = new JObject
        {
            ["info"] = new JObject { ["_postman_id"] = ToGuid(c.Id), ["name"] = c.Name, ["description"] = c.Description, ["schema"] = Schema },
            ["item"] = WriteItems(c),
        };
        if (WriteAuth(c.Auth) is { } auth) o["auth"] = auth;
        if (WriteEvents(c.PreScript, c.TestScript) is { } events) o["event"] = events;
        if (c.Variables.Count > 0)
            o["variable"] = new JArray(c.Variables.Select(v => Row(v)));
        return o;
    }

    static string ToGuid(string id) => Guid.TryParseExact(id, "N", out var g) ? g.ToString() : Guid.NewGuid().ToString();

    static JArray WriteItems(ApiFolder f)
    {
        var items = new JArray();
        foreach (var sub in f.Folders)
        {
            var o = new JObject { ["name"] = sub.Name, ["item"] = WriteItems(sub) };
            if (sub.Description.Length > 0) o["description"] = sub.Description;
            if (WriteAuth(sub.Auth) is { } auth) o["auth"] = auth;
            if (WriteEvents(sub.PreScript, sub.TestScript) is { } events) o["event"] = events;
            items.Add(o);
        }
        foreach (var r in f.Requests) items.Add(WriteRequest(r));
        return items;
    }

    static JObject WriteRequest(ApiRequest r)
    {
        var url = new JObject { ["raw"] = r.Url };
        var split = SplitUrl(r.Url);
        if (split.Protocol != null) url["protocol"] = split.Protocol;
        if (split.Host.Count > 0) url["host"] = new JArray(split.Host);
        if (split.Port != null) url["port"] = split.Port;
        if (split.Path.Count > 0) url["path"] = new JArray(split.Path);
        var query = UrlParams.Parse(r.Url).Concat(r.Params.Where(p => !p.Enabled)).ToList();
        if (query.Count > 0) url["query"] = new JArray(query.Select(p => Row(p)));

        var req = new JObject { ["method"] = r.Method, ["header"] = new JArray(r.Headers.Where(h => !h.IsEmpty).Select(h => Row(h))), ["url"] = url };
        if (r.Description.Length > 0) req["description"] = r.Description;
        if (WriteAuth(r.Auth) is { } auth) req["auth"] = auth;
        switch (r.Body.Mode)
        {
            case BodyModes.Raw:
                req["body"] = new JObject { ["mode"] = "raw", ["raw"] = r.Body.Raw, ["options"] = new JObject { ["raw"] = new JObject { ["language"] = r.Body.Language } } };
                break;
            case BodyModes.UrlEncoded:
                req["body"] = new JObject { ["mode"] = "urlencoded", ["urlencoded"] = new JArray(r.Body.UrlEncoded.Where(k => !k.IsEmpty).Select(k => Row(k))) };
                break;
            case BodyModes.FormData:
                req["body"] = new JObject
                {
                    ["mode"] = "formdata",
                    ["formdata"] = new JArray(r.Body.FormData.Where(k => !k.IsEmpty).Select(k =>
                    {
                        var row = new JObject { ["key"] = k.Key, ["type"] = k.Type };
                        if (k.Type == "file") row["src"] = k.Value; else row["value"] = k.Value;
                        if (!k.Enabled) row["disabled"] = true;
                        return row;
                    })),
                };
                break;
            case BodyModes.Binary:
                req["body"] = new JObject { ["mode"] = "file", ["file"] = new JObject { ["src"] = r.Body.File } };
                break;
        }
        var item = new JObject { ["name"] = r.Name, ["request"] = req };
        if (WriteEvents(r.PreScript, r.TestScript) is { } events) item["event"] = events;
        item["response"] = new JArray(r.Examples.Select(e => new JObject
        {
            ["name"] = e.Name, ["code"] = e.Status, ["status"] = e.StatusText,
            ["header"] = new JArray(e.Headers.Select(h => Row(h))), ["body"] = e.Body,
            ["originalRequest"] = req.DeepClone(),
        }));
        return item;
    }

    static JObject Row(KeyValue k)
    {
        var o = new JObject { ["key"] = k.Key, ["value"] = k.Value };
        if (k.Description.Length > 0) o["description"] = k.Description;
        if (!k.Enabled) o["disabled"] = true;
        return o;
    }

    static (string? Protocol, List<string> Host, string? Port, List<string> Path) SplitUrl(string raw)
    {
        var s = raw;
        int q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s.Substring(0, q);
        string? protocol = null;
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0) { protocol = s.Substring(0, scheme); s = s.Substring(scheme + 3); }
        int slash = s.IndexOf('/');
        var host = slash >= 0 ? s.Substring(0, slash) : s;
        var path = slash >= 0 ? s.Substring(slash + 1) : "";
        string? port = null;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.Substring(colon + 1).All(char.IsDigit)) { port = host.Substring(colon + 1); host = host.Substring(0, colon); }
        return (protocol, host.Length > 0 ? (host.StartsWith("{{") ? new List<string> { host } : host.Split('.').ToList()) : new(), port,
            path.Length > 0 ? path.Split('/').ToList() : new());
    }

    static JObject? WriteAuth(ApiAuth a)
    {
        JArray Fields(params (string, string)[] f) => new(f.Select(p => new JObject { ["key"] = p.Item1, ["value"] = p.Item2, ["type"] = "string" }));
        return a.Type switch
        {
            AuthTypes.None => new JObject { ["type"] = "noauth" },
            AuthTypes.Bearer => new JObject { ["type"] = "bearer", ["bearer"] = Fields(("token", a.Token)) },
            AuthTypes.Basic => new JObject { ["type"] = "basic", ["basic"] = Fields(("username", a.Username), ("password", a.Password)) },
            AuthTypes.ApiKey => new JObject { ["type"] = "apikey", ["apikey"] = Fields(("key", a.Key), ("value", a.Value), ("in", a.In)) },
            _ => null,
        };
    }

    static JArray? WriteEvents(string pre, string test)
    {
        var list = new JArray();
        JObject Event(string listen, string code) => new()
        {
            ["listen"] = listen,
            ["script"] = new JObject { ["type"] = "text/javascript", ["exec"] = new JArray(code.Replace("\r\n", "\n").Split('\n')) },
        };
        if (pre.Trim().Length > 0) list.Add(Event("prerequest", pre));
        if (test.Trim().Length > 0) list.Add(Event("test", test));
        return list.Count > 0 ? list : null;
    }

    // Postman environments

    public static ApiEnvironment ReadEnvironment(JObject o) => new()
    {
        Name = (string?)o["name"] ?? "导入的环境",
        Variables = (o["values"] as JArray ?? new JArray()).Select(v => new KeyValue
        {
            Key = (string?)v["key"] ?? "",
            Value = Text(v["value"]),
            Enabled = v["enabled"]?.Value<bool>() != false,
            Secret = (string?)v["type"] == "secret",
        }).ToList(),
    };

    public static JObject WriteEnvironment(string name, IEnumerable<KeyValue> variables, string scope = "environment") => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["values"] = new JArray(variables.Where(v => v.Key.Length > 0).Select(v => new JObject
        {
            ["key"] = v.Key, ["value"] = v.Value, ["type"] = v.Secret ? "secret" : "default", ["enabled"] = v.Enabled,
        })),
        ["_postman_variable_scope"] = scope,
        ["_postman_exported_using"] = "SeedToolBox",
    };

    // cURL

    /// <summary>The common curl options: -X, -H, -d and friends, -F, -u, -k, -L, -x and the URL.</summary>
    public static ApiRequest? ParseCurl(string command)
    {
        var args = Tokenize(command.Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("^\r\n", " ").Replace("^\n", " "));
        if (args.Count == 0 || !args[0].Equals("curl", StringComparison.OrdinalIgnoreCase) && !args[0].EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var r = new ApiRequest { Method = "" };
        r.Options.FollowRedirects = false;
        var body = new List<string>();
        bool get = false;
        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : "";
            switch (a)
            {
                case "-X": case "--request": r.Method = Next().ToUpperInvariant(); break;
                case "-H": case "--header":
                    var h = Next();
                    int colon = h.IndexOf(':');
                    if (colon > 0) r.Headers.Add(new KeyValue { Key = h.Substring(0, colon).Trim(), Value = h.Substring(colon + 1).Trim() });
                    break;
                case "-d": case "--data": case "--data-raw": case "--data-binary": case "--data-ascii": case "--data-urlencode": body.Add(Next()); break;
                case "--json":
                    body.Add(Next());
                    r.Headers.Add(new KeyValue { Key = "Content-Type", Value = "application/json" });
                    r.Headers.Add(new KeyValue { Key = "Accept", Value = "application/json" });
                    break;
                case "-F": case "--form": case "--form-string":
                    var f = Next();
                    int eq = f.IndexOf('=');
                    if (eq <= 0) break;
                    r.Body.Mode = BodyModes.FormData;
                    var value = f.Substring(eq + 1);
                    bool file = a != "--form-string" && value.StartsWith("@");
                    if (file) value = value.Substring(1).Split(';')[0].Trim('"');
                    r.Body.FormData.Add(new KeyValue { Key = f.Substring(0, eq), Value = value, Type = file ? "file" : "text" });
                    break;
                case "-u": case "--user":
                    var user = Next();
                    int sep = user.IndexOf(':');
                    r.Auth = new ApiAuth { Type = AuthTypes.Basic, Username = sep >= 0 ? user.Substring(0, sep) : user, Password = sep >= 0 ? user.Substring(sep + 1) : "" };
                    break;
                case "-A": case "--user-agent": r.Headers.Add(new KeyValue { Key = "User-Agent", Value = Next() }); break;
                case "-b": case "--cookie": r.Headers.Add(new KeyValue { Key = "Cookie", Value = Next() }); break;
                case "-e": case "--referer": r.Headers.Add(new KeyValue { Key = "Referer", Value = Next() }); break;
                case "--url": r.Url = Next(); break;
                case "-I": case "--head": r.Method = "HEAD"; break;
                case "-G": case "--get": get = true; break;
                case "-k": case "--insecure": r.Options.IgnoreSsl = true; break;
                case "-L": case "--location": r.Options.FollowRedirects = true; break;
                case "-x": case "--proxy": r.Options.Proxy = Next(); break;
                case "-m": case "--max-time": if (double.TryParse(Next(), out var t)) r.Options.TimeoutSeconds = Math.Max(1, (int)Math.Ceiling(t)); break;
                default:
                    if (a.StartsWith("-")) { if (a is "-o" or "--output" or "--connect-timeout" or "-w" or "--write-out" or "--retry" or "-c" or "--cookie-jar") Next(); }
                    else if (r.Url.Length == 0) r.Url = a;
                    break;
            }
        }
        if (r.Url.Length == 0) return null;
        if (get && body.Count > 0) { r.Url += (r.Url.Contains("?") ? "&" : "?") + string.Join("&", body); body.Clear(); }
        if (body.Count > 0)
        {
            var text = string.Join("&", body);
            var type = r.Headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            var t = text.TrimStart();
            if (type.Contains("x-www-form-urlencoded") || (type.Length == 0 && !t.StartsWith("{") && !t.StartsWith("[") && !t.StartsWith("<") && text.Contains("=")))
            {
                r.Body.Mode = BodyModes.UrlEncoded;
                foreach (var pair in text.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    var k = eq < 0 ? pair : pair.Substring(0, eq);
                    var v = eq < 0 ? "" : pair.Substring(eq + 1);
                    r.Body.UrlEncoded.Add(new KeyValue { Key = Unescape(k), Value = Unescape(v) });
                }
                r.Headers.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("x-www-form-urlencoded"));
            }
            else
            {
                r.Body.Mode = BodyModes.Raw;
                r.Body.Raw = t.StartsWith("{") || t.StartsWith("[") ? PrettyJson(text) : text;
                r.Body.Language = type.Contains("json") || t.StartsWith("{") || t.StartsWith("[") ? "json" : type.Contains("xml") || t.StartsWith("<") ? "xml" : "text";
            }
        }
        if (r.Method.Length == 0) r.Method = body.Count > 0 || r.Body.Mode != BodyModes.None ? "POST" : "GET";
        r.Params = UrlParams.Parse(r.Url);
        r.Name = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.AbsolutePath : r.Url;
        return r;
    }

    static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }

    public static string PrettyJson(string text)
    {
        try { return JToken.Parse(text).ToString(Formatting.Indented); }
        catch (JsonReaderException) { return text; }
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

    /// <summary>A request from the assistant's JSON answer; headers may be an object or "Name: value" lines.</summary>
    public static ApiRequest? ParseAi(string answer)
    {
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var o = JObject.Parse(answer.Substring(start, end - start + 1));
            var url = (string?)o["url"] ?? "";
            if (url.Length == 0) return null;
            var method = ((string?)o["method"] ?? "GET").ToUpperInvariant();
            var r = new ApiRequest
            {
                Name = (string?)o["name"] is { Length: > 0 } n ? n : method + " " + url,
                Method = method,
                Url = url,
                Params = UrlParams.Parse(url),
                Headers = o["headers"] is JObject h
                    ? h.Properties().Select(p => new KeyValue { Key = p.Name, Value = Text(p.Value) }).ToList()
                    : o["headers"] is JArray list ? Rows(list) : ApiStore.TextHeaders(Text(o["headers"])),
            };
            var body = o["body"] is JObject or JArray ? o["body"]!.ToString(Formatting.Indented) : Text(o["body"]);
            if (body.Length > 0)
            {
                var t = body.TrimStart();
                r.Body = new ApiBody { Mode = BodyModes.Raw, Raw = body.Replace("\r\n", "\n").Replace("\n", "\r\n"), Language = t.StartsWith("{") || t.StartsWith("[") ? "json" : t.StartsWith("<") ? "xml" : "text" };
            }
            var tests = Text(o["tests"]);
            if (tests.Length > 0) r.TestScript = tests.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return r;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Script text from an AI answer, without a ``` fence around it.</summary>
    public static string StripFence(string answer)
    {
        var t = answer.Trim();
        int fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0) return t;
        int lineEnd = t.IndexOf('\n', fence);
        int close = lineEnd < 0 ? -1 : t.IndexOf("```", lineEnd, StringComparison.Ordinal);
        if (lineEnd < 0) return t;
        return (close < 0 ? t.Substring(lineEnd + 1) : t.Substring(lineEnd + 1, close - lineEnd - 1)).Trim().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
