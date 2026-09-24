using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace SeedToolBox.Sync;

/// <summary>The little of WebDAV that sync needs: GET, PUT, DELETE and MKCOL, with basic authentication.</summary>
sealed class WebDavClient : IDisposable
{
    static readonly HttpMethod MkCol = new("MKCOL");
    readonly HttpClient _http;
    readonly string _root;

    /// <param name="root">Folder URL; created on first use if missing.</param>
    public WebDavClient(string root, string user, string password)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _root = root.TrimEnd('/') + "/";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SeedToolBox");
    }

    string Url(string path) => _root + string.Join("/", path.Split('/')).TrimStart('/');

    /// <summary>The file, or null if it doesn't exist yet.</summary>
    public async Task<byte[]?> GetAsync(string path)
    {
        using var response = await _http.GetAsync(Url(path));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task PutAsync(string path, byte[] data)
    {
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PutAsync(Url(path), content);
        Check(response);
    }

    public async Task DeleteAsync(string path)
    {
        using var response = await _http.DeleteAsync(Url(path));
        if (response.StatusCode != HttpStatusCode.NotFound) Check(response);
    }

    /// <summary>Creates the folder and its parents under the server root; existing ones are fine.</summary>
    public async Task EnsureFolderAsync(string path = "")
    {
        var uri = new Uri(Url(path));
        var parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var current = uri.GetLeftPart(UriPartial.Authority);
        foreach (var part in parts)
        {
            current += "/" + part;
            using var response = await _http.SendAsync(new HttpRequestMessage(MkCol, current + "/"));
            // 405: already exists; 401/403 on a parent the account can't touch (e.g. /dav) is fine too, the next level decides
            if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.Forbidden) continue;
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SyncException("账号或密码不对（坚果云要用「应用密码」，不是登录密码）");
        }
    }

    static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new SyncException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "账号或密码不对（坚果云要用「应用密码」，不是登录密码）",
            HttpStatusCode.Forbidden => "服务器拒绝访问（403），检查文件夹权限",
            HttpStatusCode.Conflict => "上级文件夹不存在（409）",
            (HttpStatusCode)507 => "网盘空间不足",
            (HttpStatusCode)429 or (HttpStatusCode)503 => "请求太频繁，服务器暂时拒绝，稍后再试",
            _ => $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}",
        });
    }

    public void Dispose() => _http.Dispose();
}
