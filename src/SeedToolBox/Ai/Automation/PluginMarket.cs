using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.Ai.Automation;

/// <summary>Finds pi packages on npm: the gallery at pi.dev lists the ones tagged pi-package.</summary>
static class PluginMarket
{
    public sealed record Package(string Name, string Version, string Description, string Author, long WeeklyDownloads, DateTime? Updated, string Homepage);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>npmjs.org first (it has download counts and keyword search), then npmmirror for when that's blocked.</summary>
    public static async Task<List<Package>> SearchAsync(string text)
    {
        text = text.Trim();
        var sources = new[]
        {
            $"https://registry.npmjs.org/-/v1/search?text={Uri.EscapeDataString(("keywords:pi-package " + text).Trim())}&size=40&popularity=1.0",
            $"https://registry.npmmirror.com/-/v1/search?text={Uri.EscapeDataString(("pi-package " + text).Trim())}&size=40",
        };
        Exception? last = null;
        foreach (var url in sources)
        {
            try
            {
                var json = JObject.Parse(await Http.GetStringAsync(url));
                var list = (json["objects"] as JArray ?? new JArray()).OfType<JObject>().Select(TryParse)
                    .Where(p => p != null).Select(p => p!).ToList();
                if (list.Count > 0 || url == sources[sources.Length - 1]) return list;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                last = ex;
            }
        }
        throw new HttpRequestException("连不上 npm：" + last?.Message, last);
    }

    /// <summary>An entry of an unexpected shape (like a plain string author) is skipped instead of failing the search.</summary>
    static Package? TryParse(JObject item)
    {
        try { return Parse(item); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException) { return null; }
    }

    static Package? Parse(JObject item)
    {
        if (item["package"] is not JObject p) return null;
        // npmmirror's plain text search also matches packages that only mention the word
        if (p["keywords"] is JArray keywords && !keywords.Any(k => (string?)k == "pi-package")) return null;
        var name = (string?)p["name"];
        if (string.IsNullOrEmpty(name)) return null;
        var author = (string?)p["publisher"]?["username"] ?? (string?)p["author"]?["name"] ?? (string?)p["maintainers"]?.FirstOrDefault()?["username"] ?? "";
        var date = (string?)item["updated"] ?? (string?)p["date"];
        return new Package(name!, (string?)p["version"] ?? "", (string?)p["description"] ?? "", author,
            (long?)item["downloads"]?["weekly"] ?? 0,
            DateTime.TryParse(date, out var d) ? d : null,
            (string?)p["links"]?["homepage"] ?? (string?)p["links"]?["npm"] ?? "https://www.npmjs.com/package/" + name);
    }
}
