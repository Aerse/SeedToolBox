using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SeedToolBox.DevTools.Api;

/// <summary>A row in a params, headers, form or variables table.</summary>
sealed class KeyValue
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"text" or "file" (form-data only).</summary>
    public string Type { get; set; } = "text";
    /// <summary>Variables only: the value is masked in the editor.</summary>
    public bool Secret { get; set; }

    public KeyValue Clone() => (KeyValue)MemberwiseClone();
    [JsonIgnore] public bool IsEmpty => Key.Length == 0 && Value.Length == 0 && Description.Length == 0;
}

static class KeyValues
{
    public static List<KeyValue> Clone(this IEnumerable<KeyValue> list) => list.Select(k => k.Clone()).ToList();
    public static IEnumerable<KeyValue> Active(this IEnumerable<KeyValue> list) => list.Where(k => k.Enabled && k.Key.Length > 0);
}

static class AuthTypes
{
    public const string Inherit = "inherit", None = "noauth", Bearer = "bearer", Basic = "basic", ApiKey = "apikey";
    public static readonly (string Id, string Label)[] All =
    {
        (Inherit, "继承上级"), (None, "无认证"), (Bearer, "Bearer Token"), (Basic, "Basic Auth"), (ApiKey, "API Key"),
    };
}

sealed class ApiAuth
{
    public string Type { get; set; } = AuthTypes.Inherit;
    public string Token { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>API key goes in "header" or "query".</summary>
    public string In { get; set; } = "header";
    public ApiAuth Clone() => (ApiAuth)MemberwiseClone();
}

static class BodyModes
{
    public const string None = "none", Raw = "raw", UrlEncoded = "urlencoded", FormData = "formdata", Binary = "file";
}

sealed class ApiBody
{
    public string Mode { get; set; } = BodyModes.None;
    public string Raw { get; set; } = "";
    /// <summary>json, xml, html, javascript or text: sets the default Content-Type of raw bodies.</summary>
    public string Language { get; set; } = "json";
    public List<KeyValue> UrlEncoded { get; set; } = new();
    public List<KeyValue> FormData { get; set; } = new();
    public string File { get; set; } = "";

    public ApiBody Clone()
    {
        var b = (ApiBody)MemberwiseClone();
        b.UrlEncoded = UrlEncoded.Clone();
        b.FormData = FormData.Clone();
        return b;
    }

    public string ContentType => Language switch
    {
        "json" => "application/json",
        "xml" => "application/xml",
        "html" => "text/html",
        "javascript" => "application/javascript",
        _ => "text/plain",
    };
}

sealed class ApiOptions
{
    public int TimeoutSeconds { get; set; } = 60;
    public bool FollowRedirects { get; set; } = true;
    public bool IgnoreSsl { get; set; }
    /// <summary>Empty uses the system proxy; "direct" skips any proxy; otherwise http://host:port.</summary>
    public string Proxy { get; set; } = "";
    public ApiOptions Clone() => (ApiOptions)MemberwiseClone();
}

/// <summary>A saved response shown under its request.</summary>
sealed class ApiExample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public List<KeyValue> Headers { get; set; } = new();
    public string Body { get; set; } = "";
}

sealed class ApiRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新请求";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<KeyValue> Params { get; set; } = new();
    public List<KeyValue> Headers { get; set; } = new();
    public ApiBody Body { get; set; } = new();
    public ApiAuth Auth { get; set; } = new();
    public string PreScript { get; set; } = "";
    public string TestScript { get; set; } = "";
    public ApiOptions Options { get; set; } = new();
    public string Description { get; set; } = "";
    public List<ApiExample> Examples { get; set; } = new();

    /// <summary>A deep copy; <paramref name="newId"/> for duplicates.</summary>
    public ApiRequest Clone(bool newId = false)
    {
        var r = JsonConvert.DeserializeObject<ApiRequest>(JsonConvert.SerializeObject(this))!;
        if (newId) r.Id = Guid.NewGuid().ToString("N");
        return r;
    }

    /// <summary>Takes over the editable part of <paramref name="source"/>, keeping this request's id and examples.</summary>
    public void Assign(ApiRequest source)
    {
        var c = source.Clone();
        Name = c.Name; Method = c.Method; Url = c.Url; Params = c.Params; Headers = c.Headers; Body = c.Body; Auth = c.Auth;
        PreScript = c.PreScript; TestScript = c.TestScript; Options = c.Options; Description = c.Description;
    }

    /// <summary>The editable part as JSON, to tell whether a tab has unsaved changes.</summary>
    public string Signature() => JsonConvert.SerializeObject(new { Name, Method, Url, Params, Headers, Body, Auth, PreScript, TestScript, Options, Description });
}

/// <summary>A folder, or with <see cref="ApiCollection"/> the top of a tree. Holds auth and scripts its requests inherit.</summary>
class ApiFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新文件夹";
    public string Description { get; set; } = "";
    public List<ApiFolder> Folders { get; set; } = new();
    public List<ApiRequest> Requests { get; set; } = new();
    public ApiAuth Auth { get; set; } = new();
    public string PreScript { get; set; } = "";
    public string TestScript { get; set; } = "";

    public IEnumerable<ApiRequest> AllRequests() => Requests.Concat(Folders.SelectMany(f => f.AllRequests()));
    public IEnumerable<ApiFolder> AllFolders() => Folders.Concat(Folders.SelectMany(f => f.AllFolders()));
}

sealed class ApiCollection : ApiFolder
{
    public ApiCollection() { Name = "新集合"; Auth.Type = AuthTypes.None; }
    public List<KeyValue> Variables { get; set; } = new();
}

sealed class ApiEnvironment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新环境";
    public List<KeyValue> Variables { get; set; } = new();
}

sealed class ApiHistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public ApiRequest Request { get; set; } = new();
    public int Status { get; set; }
    public long Milliseconds { get; set; }
}

sealed class ApiData
{
    public int Version { get; set; } = 2;
    public List<ApiCollection> Collections { get; set; } = new();
    public List<ApiEnvironment> Environments { get; set; } = new();
    public List<KeyValue> Globals { get; set; } = new();
    public string ActiveEnvironment { get; set; } = "";
    public List<ApiHistoryEntry> History { get; set; } = new();
    /// <summary>Open tabs with their unsaved edits.</summary>
    public List<ApiTabState> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    /// <summary>Response below the request (false) or to its right (true).</summary>
    public bool SideBySide { get; set; }
    public double SidebarWidth { get; set; } = 260;
}

sealed class ApiTabState
{
    /// <summary>The saved request the tab edits; empty for a request not saved yet.</summary>
    public string RequestId { get; set; } = "";
    public ApiRequest Draft { get; set; } = new();
}
