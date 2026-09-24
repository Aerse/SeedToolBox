using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.Ai;

/// <summary>Reads and writes pi's auth.json and models.json in the config directory, keeping entries we don't know.</summary>
static class PiConfig
{
    /// <summary>Built-in pi providers that take a plain API key; the key is pi's auth.json name.</summary>
    public static readonly (string Key, string Name)[] Providers =
    {
        ("deepseek", "DeepSeek"),
        ("zai-coding-cn", "智谱 GLM Coding Plan（国内）"),
        ("kimi-coding", "Kimi For Coding"),
        ("minimax-cn", "MiniMax（国内）"),
        ("qwen-token-plan-cn", "通义千问 Token Plan（国内）"),
        ("xiaomi-token-plan-cn", "小米 MiMo Token Plan（国内）"),
        ("openrouter", "OpenRouter"),
        ("openai", "OpenAI"),
        ("anthropic", "Anthropic Claude"),
        ("google", "Google Gemini"),
        ("xai", "xAI Grok"),
        ("groq", "Groq"),
        ("mistral", "Mistral"),
    };

    /// <summary>Templates for OpenAI-compatible services pi doesn't know by name.</summary>
    public static readonly (string Name, string Id, string BaseUrl, string Models)[] CustomTemplates =
    {
        ("通义千问（百炼）", "dashscope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus, qwen-max, qwen-vl-max"),
        ("硅基流动", "siliconflow", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3, Qwen/Qwen2.5-72B-Instruct"),
        ("火山方舟（豆包）", "volcengine", "https://ark.cn-beijing.volces.com/api/v3", "填写接入点 ID 或模型名"),
        ("月之暗面 Kimi", "moonshot", "https://api.moonshot.cn/v1", "kimi-latest, moonshot-v1-32k"),
        ("智谱开放平台", "zhipu", "https://open.bigmodel.cn/api/paas/v4", "glm-4-plus, glm-4v-plus"),
        ("Ollama（本机）", "ollama", "http://localhost:11434/v1", "qwen2.5:7b"),
        ("LM Studio（本机）", "lmstudio", "http://localhost:1234/v1", "填写已加载的模型名"),
    };

    static string AuthPath(AiSettings s) => Path.Combine(PiRuntime.ConfigDir(s), "auth.json");
    static string ModelsPath(AiSettings s) => Path.Combine(PiRuntime.ConfigDir(s), "models.json");

    /// <summary>Provider names that have a key or login in auth.json.</summary>
    public static HashSet<string> Authorized(AiSettings s) => new(Read(AuthPath(s)).Properties().Select(p => p.Name));

    /// <summary>The saved API key of a built-in provider, or "" (also for subscription logins).</summary>
    public static string ApiKey(AiSettings s, string provider) =>
        Read(AuthPath(s))[provider] is JObject { } entry && (string?)entry["type"] == "api_key" ? (string?)entry["key"] ?? "" : "";

    public static void SetApiKey(AiSettings s, string provider, string key)
    {
        var auth = Read(AuthPath(s));
        if (key.Length == 0) auth.Remove(provider);
        else auth[provider] = new JObject { ["type"] = "api_key", ["key"] = key };
        Write(AuthPath(s), auth);
    }

    /// <summary>Adds or replaces an OpenAI-compatible provider in models.json.</summary>
    public static void SetCustomProvider(AiSettings s, string id, string baseUrl, string key, IEnumerable<string> models)
    {
        var root = Read(ModelsPath(s));
        if (root["providers"] is not JObject providers) root["providers"] = providers = new JObject();
        providers[id] = new JObject
        {
            ["baseUrl"] = NormalizeBaseUrl(baseUrl),
            ["api"] = "openai-completions",
            // Local servers ignore the key, but pi hides models without one
            ["apiKey"] = key.Length > 0 ? key : "none",
            // Many compatible servers reject the "developer" role and reasoning_effort
            ["compat"] = new JObject { ["supportsDeveloperRole"] = false, ["supportsReasoningEffort"] = false },
            ["models"] = new JArray(models.Select(m => new JObject { ["id"] = m, ["input"] = new JArray("text", "image") })),
        };
        Write(ModelsPath(s), root);
    }

    /// <summary>A custom provider's address, key and model ids, or null if models.json doesn't have it.</summary>
    public static (string BaseUrl, string Key, List<string> Models)? CustomProvider(AiSettings s, string id)
    {
        if ((Read(ModelsPath(s))["providers"] as JObject)?[id] is not JObject p) return null;
        var key = (string?)p["apiKey"] ?? "";
        return ((string?)p["baseUrl"] ?? "", key == "none" ? "" : key,
            (p["models"] as JArray ?? new JArray()).Select(m => (string?)m["id"] ?? "").Where(m => m.Length > 0).ToList());
    }

    public static List<string> CustomProviders(AiSettings s) =>
        (Read(ModelsPath(s))["providers"] as JObject)?.Properties().Select(p => p.Name).ToList() ?? new List<string>();

    public static void RemoveCustomProvider(AiSettings s, string id)
    {
        var root = Read(ModelsPath(s));
        if (root["providers"] is JObject providers && providers.Remove(id)) Write(ModelsPath(s), root);
    }

    /// <summary>
    /// A bare host ("http://host:3000") gets "/v1", which OpenAI-compatible servers expect; a pasted
    /// ".../chat/completions" is cut back to its base, since pi appends that part itself.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/chat/completions".Length);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" ? url + "/v1" : url;
    }

    static JObject Read(string path)
    {
        try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); }
        catch (JsonException) { return new JObject(); }
    }

    static void Write(string path, JObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, value.ToString(Formatting.Indented));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
