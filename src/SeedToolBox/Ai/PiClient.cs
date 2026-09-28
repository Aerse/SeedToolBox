using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Ai;

/// <summary>
/// One pi process in RPC mode (JSON lines over stdin/stdout). Tools, extensions, skills and context files are all
/// turned off, so pi only answers and never touches files or runs commands.
/// Events are raised on the thread that created the client.
/// </summary>
sealed class PiClient : IDisposable
{
    public const string SystemPrompt =
        "你是 SeedToolBox 桌面工具箱里的助手。直接给出结果，不要寒暄，不要解释你要做什么。" +
        "除非用户要求，否则不要用 Markdown 代码块包裹整个回答。";

    readonly Process _process;
    readonly SynchronizationContext _context;
    readonly Dictionary<string, TaskCompletionSource<JObject>> _pending = new();
    readonly StringBuilder _stderr = new();
    int _nextId;
    bool _disposed;

    /// <summary>A chunk of the answer being streamed.</summary>
    public event Action<string>? TextDelta;
    /// <summary>A chunk of the model's reasoning, if it shows any.</summary>
    public event Action<string>? ThinkingDelta;
    /// <summary>The answer is finished; the argument is an error message, or null on success.</summary>
    public event Action<string?>? Finished;
    /// <summary>The process ended on its own; the argument is its last error output.</summary>
    public event Action<string>? Exited;
    /// <summary>A tool started: call id, tool name, arguments.</summary>
    public event Action<string, string, JObject>? ToolStarted;
    /// <summary>A tool finished: call id, result text, whether it failed.</summary>
    public event Action<string, string, bool>? ToolEnded;
    /// <summary>An extension asks the user something (select / confirm / input / editor); answer with <see cref="RespondUi"/>.</summary>
    public event Action<JObject>? UiRequest;
    /// <summary>A notification from an extension.</summary>
    public event Action<string>? Notice;

    PiClient(Process process)
    {
        _process = process;
        _context = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public const string AutomationPrompt =
        "你是 SeedToolBox 桌面工具箱里的自动化助手，运行在用户的 Windows 电脑上，可以用工具帮用户查看和整理文件、查看系统状态、" +
        "调音量亮度、加提醒和笔记等。先用只读工具弄清楚情况，再动手；批量操作一次调用传入所有项目。" +
        "改动类工具会先请用户确认，用户拒绝后不要换别的办法绕过去。只能改动用户允许的文件夹，被拒绝时告诉用户去 AI 设置里添加。" +
        "完成后用一两句话说明做了什么。直接给出结果，不要寒暄。";

    public static PiClient Start(AiSettings settings) => Launch(settings, new List<string>
    {
        "--mode", "rpc", "--no-session",
        "--no-tools", "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-context-files", "--no-themes", "--no-approve",
        "--system-prompt", SystemPrompt,
    }, null);

    /// <summary>
    /// pi with only the SeedToolBox tools (served by <paramref name="server"/>) and, when allowed, the installed
    /// plugins and skills; pi's own file and shell tools stay off.
    /// </summary>
    public static PiClient StartAutomation(AiSettings settings, Automation.ToolServer server)
    {
        var args = new List<string>
        {
            "--mode", "rpc", "--no-session", "--no-builtin-tools", "--no-context-files", "--no-themes",
            "-e", Path.Combine(AppPaths.Base, "Pi", "seedtoolbox.ts"),
        };
        if (!settings.LoadPlugins) args.AddRange(new[] { "--no-extensions", "--no-skills", "--no-prompt-templates" });
        var skills = Path.Combine(AppPaths.Base, "Pi", "skills");
        if (Directory.Exists(skills)) args.AddRange(new[] { "--skill", skills });
        var prompts = Path.Combine(AppPaths.Base, "Pi", "prompts");
        if (Directory.Exists(prompts)) args.AddRange(new[] { "--prompt-template", prompts });
        args.AddRange(new[] { "--system-prompt", AutomationPrompt + "\n\n今天是 " + DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm") + "。允许改动的文件夹：" + string.Join("；", Automation.FileActions.Folders(settings)) });
        return Launch(settings, args, new Dictionary<string, string>
        {
            ["STB_PIPE"] = @"\\.\pipe\" + server.PipeName,
            ["STB_TOKEN"] = server.Token,
            ["STB_TOOLS"] = server.ManifestPath,
            ["STB_TRUST_PLUGINS"] = settings.TrustPlugins ? "1" : "",
        });
    }

    static PiClient Launch(AiSettings settings, List<string> args, Dictionary<string, string>? env)
    {
        var install = PiRuntime.Find(settings) ?? throw new InvalidOperationException("还没有安装 pi，请先在设置里安装");
        if (settings.Model.Length > 0) args.AddRange(new[] { "--model", settings.Model });
        if (settings.Thinking.Length > 0) args.AddRange(new[] { "--thinking", settings.Thinking });
        var info = PiRuntime.StartInfo(install, settings, args);
        if (env != null) foreach (var pair in env) info.EnvironmentVariables[pair.Key] = pair.Value;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardErrorEncoding = Encoding.UTF8;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var client = new PiClient(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (client._stderr)
            {
                client._stderr.AppendLine(e.Data);
                // Only the tail matters for error messages
                if (client._stderr.Length > 4000) client._stderr.Remove(0, client._stderr.Length - 4000);
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        new Thread(client.ReadLoop) { IsBackground = true, Name = "pi rpc" }.Start();
        return client;
    }

    public string ErrorOutput
    {
        get { lock (_stderr) return _stderr.ToString().Trim(); }
    }

    /// <summary>Sends a prompt; the answer arrives through the events.</summary>
    public Task<JObject> PromptAsync(string message, IReadOnlyCollection<byte[]>? pngs = null)
    {
        var command = new JObject { ["type"] = "prompt", ["message"] = message };
        if (pngs is { Count: > 0 })
            command["images"] = new JArray(pngs.Select(png => new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }));
        return SendAsync(command);
    }

    public Task<JObject> AbortAsync() => SendAsync(new JObject { ["type"] = "abort" });

    /// <summary>Forgets the conversation so far.</summary>
    public Task<JObject> NewSessionAsync() => SendAsync(new JObject { ["type"] = "new_session" });

    /// <summary>Models that have credentials set up, as "provider/id" with their display names.</summary>
    public async Task<List<(string Id, string Name)>> GetModelsAsync()
    {
        var response = await SendAsync(new JObject { ["type"] = "get_available_models" });
        return (response["data"]?["models"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(m => ($"{m["provider"]}/{m["id"]}", $"{m["name"] ?? m["id"]}（{m["provider"]}）"))
            .ToList();
    }

    public async Task<string> GetModelAsync()
    {
        var response = await SendAsync(new JObject { ["type"] = "get_state" });
        var model = response["data"]?["model"];
        return model == null || model.Type == JTokenType.Null ? "" : $"{model["provider"]}/{model["id"]}";
    }

    /// <summary>Skills and prompt templates pi found, as (name, description, source).</summary>
    public async Task<List<(string Name, string Description, string Source)>> GetCommandsAsync()
    {
        var response = await SendAsync(new JObject { ["type"] = "get_commands" });
        return (response["data"]?["commands"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(c => ((string?)c["name"] ?? "", (string?)c["description"] ?? "", (string?)c["source"] ?? ""))
            .ToList();
    }

    /// <summary>Answers an extension_ui_request: a value, confirmed, or cancelled when both are null.</summary>
    public void RespondUi(string id, string? value = null, bool? confirmed = null)
    {
        var response = new JObject { ["type"] = "extension_ui_response", ["id"] = id };
        if (value != null) response["value"] = value;
        else if (confirmed != null) response["confirmed"] = confirmed.Value;
        else response["cancelled"] = true;
        try { Write(response); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    void Write(JObject record)
    {
        // Strict JSONL: one record per line, \n only, UTF-8 without BOM
        var bytes = new UTF8Encoding(false).GetBytes(record.ToString(Formatting.None) + "\n");
        var stdin = _process.StandardInput.BaseStream;
        lock (stdin)
        {
            stdin.Write(bytes, 0, bytes.Length);
            stdin.Flush();
        }
    }

    /// <summary>Resolves with the command's response; faults when pi reports failure.</summary>
    Task<JObject> SendAsync(JObject command)
    {
        var id = "c" + Interlocked.Increment(ref _nextId);
        command["id"] = id;
        var done = new TaskCompletionSource<JObject>();
        lock (_pending) _pending[id] = done;
        try
        {
            Write(command);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_pending) _pending.Remove(id);
            done.TrySetException(new InvalidOperationException("pi 已退出：" + ErrorOutput, ex));
        }
        return done.Task;
    }

    void ReadLoop()
    {
        var stdout = _process.StandardOutput.BaseStream;
        var line = new MemoryStream();
        var buffer = new byte[16384];
        try
        {
            int read;
            while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    // Split on \n only: U+2028/2029 are valid inside JSON strings
                    if (buffer[i] != (byte)'\n') { line.WriteByte(buffer[i]); continue; }
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    line.SetLength(0);
                    if (text.Length == 0) continue;
                    // A record with unexpected field types must not take the whole app down with this thread
                    try { Handle(text); }
                    catch (Exception ex) { Log.Error("pi: bad record " + (text.Length > 500 ? text.Substring(0, 500) : text), ex); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        // Whatever was waiting will never get an answer
        List<TaskCompletionSource<JObject>> waiting;
        lock (_pending)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        try { _process.WaitForExit(2000); } catch (InvalidOperationException) { }
        var error = ErrorOutput;
        foreach (var w in waiting) w.TrySetException(new InvalidOperationException("pi 已退出" + (error.Length > 0 ? "：" + error : "")));
        if (!_disposed) Post(() => Exited?.Invoke(error));
    }

    void Handle(string text)
    {
        JObject message;
        try { message = JObject.Parse(text); }
        catch (JsonException)
        {
            Log.Info("pi: " + text);
            return;
        }
        switch ((string?)message["type"])
        {
            case "response":
                var id = (string?)message["id"];
                TaskCompletionSource<JObject>? waiting = null;
                if (id != null) lock (_pending) { if (_pending.TryGetValue(id, out waiting)) _pending.Remove(id); }
                if (waiting == null) break;
                if ((bool?)message["success"] == true) waiting.TrySetResult(message);
                else waiting.TrySetException(new InvalidOperationException((string?)message["error"] ?? "pi 拒绝了请求"));
                break;
            case "message_update":
                var update = message["assistantMessageEvent"];
                var delta = (string?)update?["delta"];
                if (delta == null) break;
                switch ((string?)update!["type"])
                {
                    case "text_delta": Post(() => TextDelta?.Invoke(delta)); break;
                    case "thinking_delta": Post(() => ThinkingDelta?.Invoke(delta)); break;
                }
                break;
            case "message_end":
                // Provider errors (bad key, no quota) end the assistant message with stopReason "error"
                if (message["message"] is JObject { } m && (string?)m["role"] == "assistant" && (string?)m["stopReason"] == "error")
                    _lastError = (string?)m["errorMessage"] ?? (string?)m["error"] ?? "模型返回了错误";
                break;
            case "agent_settled":
                var error = _lastError;
                _lastError = null;
                Post(() => Finished?.Invoke(error));
                break;
            case "tool_execution_start":
                var startId = (string?)message["toolCallId"] ?? "";
                var toolName = (string?)message["toolName"] ?? "";
                var toolArgs = message["args"] as JObject ?? new JObject();
                Post(() => ToolStarted?.Invoke(startId, toolName, toolArgs));
                break;
            case "tool_execution_end":
                var endId = (string?)message["toolCallId"] ?? "";
                var output = string.Join("\n", (message["result"]?["content"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
                var failed = (bool?)message["isError"] == true;
                Post(() => ToolEnded?.Invoke(endId, output, failed));
                break;
            case "extension_ui_request":
                var method = (string?)message["method"];
                if (method is "select" or "confirm" or "input" or "editor") Post(() => UiRequest?.Invoke(message));
                else if (method == "notify" && (string?)message["message"] is { } notice) Post(() => Notice?.Invoke(notice));
                break;
            case "extension_error":
                Log.Info("pi extension error: " + text);
                break;
        }
    }

    string? _lastError;

    void Post(Action action) => _context.Post(_ => { if (!_disposed) action(); }, null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Extensions and tools pi started go too
            ProcessTree.Kill(_process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _process.Dispose();
    }
}
