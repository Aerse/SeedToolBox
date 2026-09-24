using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Ai.Automation;

public enum ConfirmChoice { Deny, Allow, AllowSession }

/// <summary>
/// Serves the tools to the pi extension over a named pipe: one connection per call, one JSON line each way.
/// Calls run on the UI thread; changes are confirmed by the user first.
/// </summary>
sealed class ToolServer : IDisposable
{
    readonly AiService _service;
    readonly Dispatcher _dispatcher;
    readonly Dictionary<string, AutomationTool> _tools;
    readonly HashSet<string> _sessionAllowed = new();
    readonly CancellationTokenSource _stop = new();

    public string PipeName { get; } = "SeedToolBox-ai-" + Guid.NewGuid().ToString("N");
    public string Token { get; } = NewToken();
    public string ManifestPath { get; }

    /// <summary>Shows the change to the user; set by the window.</summary>
    public Func<string, string, bool, Task<ConfirmChoice>> Confirm { get; set; } = (_, _, _) => Task.FromResult(ConfirmChoice.Deny);

    public ToolServer(AiService service)
    {
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tools = AutomationTools.All(service.Settings).ToDictionary(t => t.Name);
        var dir = Path.Combine(Path.GetTempPath(), "SeedToolBox");
        Directory.CreateDirectory(dir);
        ManifestPath = Path.Combine(dir, PipeName + ".json");
        File.WriteAllText(ManifestPath, new JArray(_tools.Values.Select(t => t.Manifest())).ToString(Formatting.None), new UTF8Encoding(false));
        new Thread(AcceptLoop) { IsBackground = true, Name = "ai tools" }.Start();
    }

    static string NewToken()
    {
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Forgets "allow for this conversation".</summary>
    public void ResetSession() => _sessionAllowed.Clear();

    public string Label(string tool) => _tools.TryGetValue(tool, out var t) ? t.Label : tool;

    void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(_stop.Token).Wait();
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or IOException)
            {
                if (_stop.IsCancellationRequested) return;
                Log.Error("AI tool pipe failed", ex);
                Thread.Sleep(500);
                continue;
            }
            _ = Serve(pipe);
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            JObject reply;
            try
            {
                var line = await ReadLine(pipe);
                var request = JObject.Parse(line);
                if ((string?)request["token"] != Token) throw new ToolException("拒绝访问");
                var name = (string?)request["tool"] ?? "";
                var args = request["args"] as JObject ?? new JObject();
                reply = await await _dispatcher.InvokeAsync(() => Run(name, args));
            }
            catch (Exception ex) when (ex is JsonException or ToolException or IOException)
            {
                reply = new JObject { ["error"] = ex.Message };
            }
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(reply.ToString(Formatting.None));
                await pipe.WriteAsync(bytes, 0, bytes.Length);
                await pipe.FlushAsync();
                pipe.WaitForPipeDrain();
            }
            catch (IOException) { } // the call was cancelled
        }
    }

    static async Task<string> ReadLine(Stream stream)
    {
        var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;
            int end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            data.Write(buffer, 0, end < 0 ? read : end);
            if (end >= 0) break;
            if (data.Length > 16 << 20) throw new IOException("请求太大");
        }
        return Encoding.UTF8.GetString(data.ToArray());
    }

    async Task<JObject> Run(string name, JObject args)
    {
        if (!_tools.TryGetValue(name, out var tool)) return new JObject { ["error"] = "没有这个工具：" + name };
        try
        {
            var change = tool.Confirm?.Invoke(args, _service);
            if (change != null && !_sessionAllowed.Contains(name))
            {
                var choice = await Confirm(tool.Label, change, tool.SessionAllow);
                if (choice == ConfirmChoice.Deny) return new JObject { ["error"] = "用户拒绝了这个操作。不要换别的方式重试，先问用户想怎么做。" };
                if (choice == ConfirmChoice.AllowSession && tool.SessionAllow) _sessionAllowed.Add(name);
            }
            var result = await tool.Run(args, _service);
            var reply = new JObject { ["text"] = result.Text };
            if (result.Png != null) reply["image"] = Convert.ToBase64String(result.Png);
            return reply;
        }
        catch (ToolException ex) { return new JObject { ["error"] = ex.Message }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Log.Error("AI tool " + name + " failed", ex);
            return new JObject { ["error"] = ex.Message };
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { File.Delete(ManifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
