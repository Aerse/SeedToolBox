using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace SeedToolBox.DevTools.Api;

/// <summary>Turns a prepared request into code for curl, JavaScript, Python and C#.</summary>
static class ApiCodeGen
{
    public static readonly string[] Languages = { "cURL (bash)", "cURL (cmd)", "JavaScript fetch", "JavaScript axios", "Python requests", "C# HttpClient", "HTTP" };

    public static string Generate(string language, PreparedRequest p) => language switch
    {
        "cURL (cmd)" => Curl(p, windows: true),
        "JavaScript fetch" => Fetch(p),
        "JavaScript axios" => Axios(p),
        "Python requests" => Python(p),
        "C# HttpClient" => CSharp(p),
        "HTTP" => Http(p),
        _ => Curl(p, windows: false),
    };

    /// <summary>Headers worth writing out: without the defaults every client adds by itself.</summary>
    static IEnumerable<KeyValuePair<string, string>> Headers(PreparedRequest p, bool dropFormType = true) =>
        p.Headers.Where(h => !(h.Key == "User-Agent" && h.Value == "SeedToolBox") && !(h.Key == "Accept" && h.Value == "*/*")
                             && !(dropFormType && p.Body.Mode == BodyModes.FormData && h.Key.Equals("Content-Type", System.StringComparison.OrdinalIgnoreCase)));

    static string Js(string s) => JsonConvert.ToString(s);

    public static string Curl(PreparedRequest p, bool windows = false)
    {
        string Q(string s) => windows ? "\"" + s.Replace("\"", "\\\"").Replace("%", "%%") + "\"" : "'" + s.Replace("'", "'\\''") + "'";
        var nl = windows ? " ^\r\n  " : " \\\r\n  ";
        var sb = new StringBuilder("curl");
        if (p.Method != "GET" && !(p.Method == "POST" && p.Body.Mode != BodyModes.None)) sb.Append(" -X ").Append(p.Method);
        sb.Append(' ').Append(Q(p.Url));
        if (p.Options.FollowRedirects) sb.Append(" -L");
        if (p.Options.IgnoreSsl) sb.Append(" -k");
        foreach (var h in Headers(p)) sb.Append(nl).Append("-H ").Append(Q(h.Key + ": " + h.Value));
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0: sb.Append(nl).Append("--data-raw ").Append(Q(p.Body.Raw)); break;
            case BodyModes.UrlEncoded:
                foreach (var f in p.Body.UrlEncoded.Active()) sb.Append(nl).Append("--data-urlencode ").Append(Q(f.Key + "=" + f.Value));
                break;
            case BodyModes.FormData:
                foreach (var f in p.Body.FormData.Active()) sb.Append(nl).Append("-F ").Append(Q(f.Key + "=" + (f.Type == "file" ? "@" : "") + f.Value));
                break;
            case BodyModes.Binary when p.Body.File.Length > 0: sb.Append(nl).Append("--data-binary ").Append(Q("@" + p.Body.File)); break;
        }
        return sb.ToString();
    }

    static string Fetch(PreparedRequest p)
    {
        var sb = new StringBuilder();
        var headers = Headers(p).ToList();
        string body = "";
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0: body = Js(p.Body.Raw); break;
            case BodyModes.UrlEncoded:
                sb.AppendLine("const body = new URLSearchParams();");
                foreach (var f in p.Body.UrlEncoded.Active()) sb.AppendLine($"body.append({Js(f.Key)}, {Js(f.Value)});");
                body = "body";
                break;
            case BodyModes.FormData:
                sb.AppendLine("const body = new FormData();");
                foreach (var f in p.Body.FormData.Active())
                    sb.AppendLine(f.Type == "file" ? $"body.append({Js(f.Key)}, fileInput.files[0], {Js(System.IO.Path.GetFileName(f.Value))});" : $"body.append({Js(f.Key)}, {Js(f.Value)});");
                body = "body";
                break;
            case BodyModes.Binary: body = "file"; break;
        }
        sb.AppendLine($"const response = await fetch({Js(p.Url)}, {{");
        sb.AppendLine($"  method: {Js(p.Method)},");
        if (headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            foreach (var h in headers) sb.AppendLine($"    {Js(h.Key)}: {Js(h.Value)},");
            sb.AppendLine("  },");
        }
        if (body.Length > 0) sb.AppendLine($"  body: {body},");
        if (!p.Options.FollowRedirects) sb.AppendLine("  redirect: \"manual\",");
        sb.AppendLine("});");
        sb.Append("console.log(response.status, await response.text());");
        return sb.ToString();
    }

    static string Axios(PreparedRequest p)
    {
        var sb = new StringBuilder("import axios from \"axios\";\r\n");
        string data = "";
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0:
                data = p.Body.Language == "json" && TryJson(p.Body.Raw) is { } json ? json.Replace("\n", "\n  ") : Js(p.Body.Raw);
                break;
            case BodyModes.UrlEncoded:
                sb.AppendLine("const data = new URLSearchParams();");
                foreach (var f in p.Body.UrlEncoded.Active()) sb.AppendLine($"data.append({Js(f.Key)}, {Js(f.Value)});");
                data = "data";
                break;
            case BodyModes.FormData:
                sb.AppendLine("import FormData from \"form-data\";\r\nimport fs from \"fs\";");
                sb.AppendLine("const data = new FormData();");
                foreach (var f in p.Body.FormData.Active())
                    sb.AppendLine(f.Type == "file" ? $"data.append({Js(f.Key)}, fs.createReadStream({Js(f.Value)}));" : $"data.append({Js(f.Key)}, {Js(f.Value)});");
                data = "data";
                break;
        }
        sb.AppendLine("\r\nconst response = await axios({");
        sb.AppendLine($"  method: {Js(p.Method.ToLowerInvariant())},");
        sb.AppendLine($"  url: {Js(p.Url)},");
        var headers = Headers(p).ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            foreach (var h in headers) sb.AppendLine($"    {Js(h.Key)}: {Js(h.Value)},");
            sb.AppendLine("  },");
        }
        if (data.Length > 0) sb.AppendLine($"  data: {data},");
        if (!p.Options.FollowRedirects) sb.AppendLine("  maxRedirects: 0,");
        sb.AppendLine("});");
        sb.Append("console.log(response.status, response.data);");
        return sb.ToString();
    }

    static string? TryJson(string s)
    {
        try { return Newtonsoft.Json.Linq.JToken.Parse(s).ToString(Formatting.Indented).Replace("\r\n", "\n"); }
        catch (JsonReaderException) { return null; }
    }

    static string Py(string s) => JsonConvert.ToString(s);

    static string Python(PreparedRequest p)
    {
        var sb = new StringBuilder("import requests\r\n\r\n");
        sb.AppendLine($"url = {Py(p.Url)}");
        var headers = Headers(p).ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine("headers = {");
            foreach (var h in headers) sb.AppendLine($"    {Py(h.Key)}: {Py(h.Value)},");
            sb.AppendLine("}");
        }
        var args = new List<string> { Py(p.Method), "url" };
        if (headers.Count > 0) args.Add("headers=headers");
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0:
                sb.AppendLine($"data = {Py(p.Body.Raw)}");
                args.Add("data=data.encode(\"utf-8\")");
                break;
            case BodyModes.UrlEncoded:
                sb.AppendLine("data = {");
                foreach (var f in p.Body.UrlEncoded.Active()) sb.AppendLine($"    {Py(f.Key)}: {Py(f.Value)},");
                sb.AppendLine("}");
                args.Add("data=data");
                break;
            case BodyModes.FormData:
                sb.AppendLine("data = {");
                foreach (var f in p.Body.FormData.Active().Where(f => f.Type != "file")) sb.AppendLine($"    {Py(f.Key)}: {Py(f.Value)},");
                sb.AppendLine("}");
                sb.AppendLine("files = {");
                foreach (var f in p.Body.FormData.Active().Where(f => f.Type == "file")) sb.AppendLine($"    {Py(f.Key)}: open({Py(f.Value)}, \"rb\"),");
                sb.AppendLine("}");
                args.Add("data=data");
                args.Add("files=files");
                break;
            case BodyModes.Binary when p.Body.File.Length > 0:
                args.Add($"data=open({Py(p.Body.File)}, \"rb\")");
                break;
        }
        if (!p.Options.FollowRedirects) args.Add("allow_redirects=False");
        if (p.Options.IgnoreSsl) args.Add("verify=False");
        args.Add($"timeout={p.Options.TimeoutSeconds}");
        sb.AppendLine();
        sb.AppendLine($"response = requests.request({string.Join(", ", args)})");
        sb.Append("print(response.status_code, response.text)");
        return sb.ToString();
    }

    static string Cs(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";

    static string CSharp(PreparedRequest p)
    {
        var sb = new StringBuilder();
        var handler = new List<string>();
        if (!p.Options.FollowRedirects) handler.Add("AllowAutoRedirect = false");
        if (p.Options.IgnoreSsl) handler.Add("ServerCertificateCustomValidationCallback = (_, _, _, _) => true");
        sb.AppendLine(handler.Count > 0
            ? $"using var client = new HttpClient(new HttpClientHandler {{ {string.Join(", ", handler)} }});"
            : "using var client = new HttpClient();");
        var method = p.Method switch { "GET" => "HttpMethod.Get", "POST" => "HttpMethod.Post", "PUT" => "HttpMethod.Put", "DELETE" => "HttpMethod.Delete", "HEAD" => "HttpMethod.Head", "OPTIONS" => "HttpMethod.Options", _ => $"new HttpMethod(\"{p.Method}\")" };
        sb.AppendLine($"using var request = new HttpRequestMessage({method}, {Cs(p.Url)});");
        var contentType = p.Header("Content-Type");
        foreach (var h in Headers(p).Where(h => !h.Key.Equals("Content-Type", System.StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({Cs(h.Key)}, {Cs(h.Value)});");
        switch (p.Body.Mode)
        {
            case BodyModes.Raw when p.Body.Raw.Length > 0:
                sb.AppendLine($"request.Content = new StringContent({Cs(p.Body.Raw)});");
                break;
            case BodyModes.UrlEncoded:
                sb.AppendLine("request.Content = new FormUrlEncodedContent(new Dictionary<string, string>");
                sb.AppendLine("{");
                foreach (var f in p.Body.UrlEncoded.Active()) sb.AppendLine($"    [{Cs(f.Key)}] = {Cs(f.Value)},");
                sb.AppendLine("});");
                contentType = null;
                break;
            case BodyModes.FormData:
                sb.AppendLine("var form = new MultipartFormDataContent();");
                foreach (var f in p.Body.FormData.Active())
                    sb.AppendLine(f.Type == "file"
                        ? $"form.Add(new ByteArrayContent(File.ReadAllBytes({Cs(f.Value)})), {Cs(f.Key)}, {Cs(System.IO.Path.GetFileName(f.Value))});"
                        : $"form.Add(new StringContent({Cs(f.Value)}), {Cs(f.Key)});");
                sb.AppendLine("request.Content = form;");
                contentType = null;
                break;
            case BodyModes.Binary when p.Body.File.Length > 0:
                sb.AppendLine($"request.Content = new ByteArrayContent(File.ReadAllBytes({Cs(p.Body.File)}));");
                break;
        }
        if (contentType != null && p.Body.Mode is BodyModes.Raw or BodyModes.Binary)
        {
            sb.AppendLine("request.Content.Headers.Remove(\"Content-Type\");");
            sb.AppendLine($"request.Content.Headers.TryAddWithoutValidation(\"Content-Type\", {Cs(contentType)});");
        }
        sb.AppendLine("using var response = await client.SendAsync(request);");
        sb.Append("Console.WriteLine($\"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}\");");
        return sb.ToString();
    }

    static string Http(PreparedRequest p)
    {
        var sb = new StringBuilder();
        var uri = System.Uri.TryCreate(p.Url, System.UriKind.Absolute, out var u) ? u : null;
        sb.AppendLine($"{p.Method} {(uri != null ? uri.PathAndQuery : p.Url)} HTTP/1.1");
        if (uri != null) sb.AppendLine("Host: " + uri.Authority);
        foreach (var h in Headers(p, dropFormType: false)) sb.AppendLine($"{h.Key}: {h.Value}");
        if (p.BodyText is { Length: > 0 } body) { sb.AppendLine(); sb.Append(body); }
        return sb.ToString();
    }
}
