using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;

namespace SeedToolBox.Ai;

/// <summary>Download sources for Node and npm packages; the Chinese mirrors come first.</summary>
static class PiMirrors
{
    public sealed record Mirror(string Key, string Name, string Node, string Registry);

    public static readonly Mirror[] All =
    {
        new("npmmirror", "npmmirror（阿里）", "https://npmmirror.com/mirrors/node/", "https://registry.npmmirror.com/"),
        new("huawei", "华为云", "https://mirrors.huaweicloud.com/nodejs/", "https://mirrors.huaweicloud.com/repository/npm/"),
        new("tencent", "腾讯云", "https://mirrors.cloud.tencent.com/nodejs-release/", "https://mirrors.cloud.tencent.com/npm/"),
        new("official", "官方（nodejs.org / npmjs.org）", "https://nodejs.org/dist/", "https://registry.npmjs.org/"),
    };

    /// <summary>The chosen source first, then (when allowed) the others as fallbacks.</summary>
    public static List<Mirror> Order(AiSettings settings)
    {
        var list = new List<Mirror>();
        if (settings.Mirror == "custom" && settings.CustomNodeMirror.Length > 0 && settings.CustomRegistry.Length > 0)
            list.Add(new Mirror("custom", "自定义", Slash(settings.CustomNodeMirror), Slash(settings.CustomRegistry)));
        var chosen = All.FirstOrDefault(m => m.Key == settings.Mirror);
        if (chosen != null) list.Add(chosen);
        if (settings.FallbackMirrors || list.Count == 0) list.AddRange(All.Where(m => m != chosen));
        return list;
    }

    static string Slash(string url) => url.Trim().TrimEnd('/') + "/";
}

/// <summary>Where pi and its Node live, and how to start it.</summary>
static class PiRuntime
{
    public const string Package = "@earendil-works/pi-coding-agent";

    /// <summary>Outside Data so the daily backup doesn't zip hundreds of MB of node_modules.</summary>
    public static string Root => Path.Combine(AppPaths.Base, "Runtime", "pi");
    public static string NodeDir => Path.Combine(Root, "node");
    public static string AppDir => Path.Combine(Root, "app");
    public static string BuiltinNode => Path.Combine(NodeDir, "node.exe");
    static string BuiltinPackage => Path.Combine(AppDir, "node_modules", "@earendil-works", "pi-coding-agent");
    public static string OwnConfig => Path.Combine(AppPaths.Data, "PiAgent");
    public static string SharedConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");

    public static string ConfigDir(AiSettings s) => s.ShareConfig ? SharedConfig : OwnConfig;

    public sealed record Install(string Node, string Cli, string Version, bool Builtin);

    public static Install? Builtin() => FromPackage(BuiltinNode, BuiltinPackage, true);

    /// <summary>A pi installed with the user's own npm (npm i -g).</summary>
    public static Install? System()
    {
        var node = FindOnPath("node.exe");
        if (node == null) return null;
        var global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
        return FromPackage(node, Path.Combine(global, "@earendil-works", "pi-coding-agent"), false)
            ?? FromPackage(node, Path.Combine(global, "@mariozechner", "pi-coding-agent"), false);
    }

    public static Install? Find(AiSettings s) => s.Source == "system" ? System() ?? Builtin() : Builtin() ?? System();

    static Install? FromPackage(string node, string package, bool builtin)
    {
        try
        {
            var manifest = Path.Combine(package, "package.json");
            if (!File.Exists(node) || !File.Exists(manifest)) return null;
            var json = JObject.Parse(File.ReadAllText(manifest));
            var bin = json["bin"] is JObject bins ? (string?)bins["pi"] : (string?)json["bin"];
            if (bin == null) return null;
            var cli = Path.GetFullPath(Path.Combine(package, bin));
            return File.Exists(cli) ? new Install(node, cli, (string?)json["version"] ?? "?", builtin) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? FindOnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var path = Path.Combine(dir.Trim(), file);
                if (dir.Length > 0 && File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>A process running pi with our config directory; not started.</summary>
    public static ProcessStartInfo StartInfo(Install install, AiSettings s, IEnumerable<string> args, bool console = false)
    {
        Directory.CreateDirectory(ConfigDir(s));
        var work = Path.Combine(Root, "work");
        Directory.CreateDirectory(work);
        var info = new ProcessStartInfo(install.Node, Quote(install.Cli) + " " + string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = !console,
            // An empty folder, so nothing in the user's projects gets read as context
            WorkingDirectory = work,
        };
        info.EnvironmentVariables["PI_CODING_AGENT_DIR"] = ConfigDir(s);
        info.EnvironmentVariables["PI_TELEMETRY"] = "0";
        info.EnvironmentVariables["PATH"] = Path.GetDirectoryName(install.Node) + ";" + Environment.GetEnvironmentVariable("PATH");
        return info;
    }

    /// <summary>Quotes one argument the way the C runtime splits a command line.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            // Backslashes are only special right before a quote
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Opens pi's own terminal UI, e.g. for /login with a subscription.</summary>
    public static void OpenTerminal(Install install, AiSettings s)
    {
        var info = StartInfo(install, s, Array.Empty<string>(), console: true);
        // cmd keeps the window open if pi exits with an error
        var inner = Quote(install.Node) + " " + Quote(install.Cli);
        info.FileName = "cmd.exe";
        info.Arguments = "/k \"" + inner + "\"";
        info.UseShellExecute = false;
        Process.Start(info);
    }
}
