using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Ai;

/// <summary>
/// Installs a private Node and pi under Runtime\pi, trying the configured mirrors in order.
/// Nothing outside that folder is touched: no PATH change, no global npm config.
/// </summary>
sealed class PiInstaller
{
    readonly AiSettings _settings;
    readonly IProgress<string> _log;
    readonly IProgress<double?> _progress;
    static readonly HttpClient Http = CreateClient();

    public PiInstaller(AiSettings settings, IProgress<string> log, IProgress<double?> progress)
    {
        _settings = settings;
        _log = log;
        _progress = progress;
    }

    static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SeedToolBox");
        return client;
    }

    /// <summary>Installs or updates to the latest pi; throws with every source's error when all fail.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        var errors = new List<string>();
        foreach (var mirror in PiMirrors.Order(_settings))
        {
            cancel.ThrowIfCancellationRequested();
            _log.Report($"—— 使用下载源：{mirror.Name}");
            try
            {
                var need = await RequiredNodeAsync(mirror, cancel);
                _log.Report($"pi 需要 Node {need} 或更高版本");
                await EnsureNodeAsync(mirror, need, cancel);
                await NpmInstallAsync(mirror, cancel);
                var install = PiRuntime.Builtin() ?? throw new InvalidOperationException("安装完成但没有找到 pi 的入口文件");
                _log.Report($"安装完成：pi {install.Version}");
                return;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"pi install via {mirror.Key} failed", ex);
                var message = ex is TaskCanceledException ? "连接超时" : ex.Message;
                _log.Report($"失败：{message}");
                errors.Add($"{mirror.Name}：{message}");
            }
        }
        throw new InvalidOperationException("所有下载源都失败了：\n" + string.Join("\n", errors));
    }

    async Task<Version> RequiredNodeAsync(PiMirrors.Mirror mirror, CancellationToken cancel)
    {
        var url = mirror.Registry + PiRuntime.Package.Replace("/", "%2F") + "/latest";
        _log.Report($"查询最新版本：{url}");
        var json = JObject.Parse(await GetStringAsync(url, TimeSpan.FromSeconds(20), cancel));
        _log.Report($"最新版本：pi {json["version"]}");
        var engines = (string?)json["engines"]?["node"] ?? "";
        // Only the ">=x.y.z" form is used by pi; anything else falls back to a safe floor
        var match = System.Text.RegularExpressions.Regex.Match(engines, @">=\s*v?(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0, match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0)
            : new Version(22, 19, 0);
    }

    async Task EnsureNodeAsync(PiMirrors.Mirror mirror, Version need, CancellationToken cancel)
    {
        if (NodeVersion(PiRuntime.BuiltinNode) is { } current && current >= need)
        {
            _log.Report($"已有 Node {current}，跳过下载");
            return;
        }

        _log.Report("获取 Node 版本列表…");
        var index = JArray.Parse(await GetStringAsync(mirror.Node + "index.json", TimeSpan.FromSeconds(30), cancel));
        // The newest LTS release that is new enough and has a Windows zip
        var release = index.OfType<JObject>()
            .Where(r => r["lts"] is JValue { Type: JTokenType.String } && (r["files"] as JArray)?.Any(f => (string?)f == "win-x64-zip") == true)
            .Select(r => (Name: (string)r["version"]!, Version: ParseVersion((string)r["version"]!)))
            .Where(r => r.Version != null && r.Version >= need)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
        if (release.Name == null) throw new InvalidOperationException($"下载源里没有 Node {need} 以上的 LTS 版本");

        var file = $"node-{release.Name}-win-x64.zip";
        var baseUrl = $"{mirror.Node}{release.Name}/";
        var sums = await GetStringAsync(baseUrl + "SHASUMS256.txt", TimeSpan.FromSeconds(30), cancel);
        var expected = sums.Split('\n').Select(l => l.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1] == file)?[0]
            ?? throw new InvalidOperationException("校验文件里没有 " + file);

        Directory.CreateDirectory(PiRuntime.Root);
        var zip = Path.Combine(PiRuntime.Root, file + ".download");
        _log.Report($"下载 Node {release.Name}：{baseUrl}{file}");
        await DownloadAsync(baseUrl + file, zip, cancel);

        _log.Report("校验 SHA-256…");
        string actual;
        using (var stream = File.OpenRead(zip))
        using (var sha = SHA256.Create())
            actual = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidOperationException("下载的文件校验不通过，可能已损坏");
        }

        _log.Report("解压…");
        var temp = PiRuntime.NodeDir + ".tmp";
        await Task.Run(() =>
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            ZipFile.ExtractToDirectory(zip, temp);
            // The archive holds a single node-vX-win-x64 folder
            var inner = Directory.GetDirectories(temp).Single();
            if (Directory.Exists(PiRuntime.NodeDir)) Directory.Delete(PiRuntime.NodeDir, true);
            Directory.Move(inner, PiRuntime.NodeDir);
            Directory.Delete(temp, true);
            File.Delete(zip);
        }, cancel);
        _log.Report($"Node {release.Name} 已就绪");
    }

    async Task NpmInstallAsync(PiMirrors.Mirror mirror, CancellationToken cancel)
    {
        Directory.CreateDirectory(PiRuntime.AppDir);
        var manifest = Path.Combine(PiRuntime.AppDir, "package.json");
        if (!File.Exists(manifest)) File.WriteAllText(manifest, "{ \"private\": true }");
        // Our own npmrc, so the user's global npm settings neither apply nor change
        var npmrc = Path.Combine(PiRuntime.AppDir, ".npmrc");
        File.WriteAllText(npmrc, $"registry={mirror.Registry}\nfund=false\naudit=false\nupdate-notifier=false\n");

        var npm = Path.Combine(PiRuntime.NodeDir, "node_modules", "npm", "bin", "npm-cli.js");
        var info = new ProcessStartInfo(PiRuntime.BuiltinNode,
            $"{PiRuntime.Quote(npm)} install {PiRuntime.Package}@latest --registry {mirror.Registry} --no-audit --no-fund --omit=dev")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = PiRuntime.AppDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.EnvironmentVariables["npm_config_userconfig"] = npmrc;
        info.EnvironmentVariables["npm_config_cache"] = Path.Combine(PiRuntime.Root, "npm-cache");
        info.EnvironmentVariables["npm_config_registry"] = mirror.Registry;
        info.EnvironmentVariables["PATH"] = PiRuntime.NodeDir + ";" + Environment.GetEnvironmentVariable("PATH");

        _log.Report($"npm install {PiRuntime.Package}（{mirror.Registry}）");
        _progress.Report(null);
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>();
        var tail = new Queue<string>();
        void Line(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                tail.Enqueue(line!);
                if (tail.Count > 5) tail.Dequeue();
            }
            _log.Report(line!);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancel.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
        {
            var code = await exited.Task;
            cancel.ThrowIfCancellationRequested();
            if (code != 0)
            {
                string last;
                lock (tail) last = string.Join(" / ", tail);
                throw new InvalidOperationException($"npm 退出码 {code}：{last}");
            }
        }
    }

    async Task DownloadAsync(string url, string path, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        using var input = await response.Content.ReadAsStreamAsync();
        using var output = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        var last = DateTime.MinValue;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
        {
            await output.WriteAsync(buffer, 0, read, cancel);
            done += read;
            if (DateTime.Now - last > TimeSpan.FromMilliseconds(200))
            {
                last = DateTime.Now;
                _progress.Report(total > 0 ? (double)done / total.Value : null);
            }
        }
        _progress.Report(1);
    }

    static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        linked.CancelAfter(timeout);
        using var response = await Http.GetAsync(url, linked.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    static Version? ParseVersion(string text) => Version.TryParse(text.TrimStart('v'), out var v) ? v : null;

    /// <summary>The version a node.exe reports, or null if it is missing or won't run.</summary>
    public static Version? NodeVersion(string node)
    {
        if (!File.Exists(node)) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(node, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return ParseVersion(text);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
