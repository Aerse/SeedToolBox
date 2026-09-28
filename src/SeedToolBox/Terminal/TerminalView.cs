using System;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeedToolBox.Terminal;

/// <summary>Built-in colour schemes, in xterm.js theme format.</summary>
static class TerminalThemes
{
    static JObject T(string bg, string fg, string cursor, string sel, params string[] ansi)
    {
        var names = new[] { "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite" };
        var o = new JObject { ["background"] = bg, ["foreground"] = fg, ["cursor"] = cursor, ["cursorAccent"] = bg, ["selectionBackground"] = sel };
        for (var i = 0; i < names.Length && i < ansi.Length; i++) o[names[i]] = ansi[i];
        return o;
    }

    public static readonly Dictionary<string, JObject> All = new()
    {
        ["默认深色"] = T("#1e1e1e", "#d4d4d4", "#aeafad", "#264f78",
            "#000000", "#cd3131", "#0dbc79", "#e5e510", "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
            "#666666", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#ffffff"),
        ["默认浅色"] = T("#ffffff", "#333333", "#333333", "#add6ff",
            "#000000", "#cd3131", "#00bc00", "#949800", "#0451a5", "#bc05bc", "#0598bc", "#555555",
            "#666666", "#cd3131", "#14ce14", "#b5ba00", "#0451a5", "#bc05bc", "#0598bc", "#a5a5a5"),
        ["Solarized Dark"] = T("#002b36", "#839496", "#93a1a1", "#073642",
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3"),
        ["Solarized Light"] = T("#fdf6e3", "#657b83", "#586e75", "#eee8d5",
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3"),
        ["Dracula"] = T("#282a36", "#f8f8f2", "#f8f8f2", "#44475a",
            "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
            "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff"),
        ["One Dark"] = T("#282c34", "#abb2bf", "#528bff", "#3e4451",
            "#282c34", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
            "#5c6370", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff"),
        ["Monokai"] = T("#272822", "#f8f8f2", "#f8f8f0", "#49483e",
            "#272822", "#f92672", "#a6e22e", "#f4bf75", "#66d9ef", "#ae81ff", "#a1efe4", "#f8f8f2",
            "#75715e", "#f92672", "#a6e22e", "#f4bf75", "#66d9ef", "#ae81ff", "#a1efe4", "#f9f8f5"),
        ["Nord"] = T("#2e3440", "#d8dee9", "#d8dee9", "#434c5e",
            "#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0",
            "#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4"),
    };

    public static JObject Get(string name) => All.TryGetValue(name, out var t) ? t : All["默认深色"];

    public static Color Background(string name) => (Color)ColorConverter.ConvertFromString((string)Get(name)["background"]!);
}

/// <summary>One xterm.js terminal in a WebView2, wired to an <see cref="ISession"/>.</summary>
sealed partial class TerminalView : Border
{
    const string VirtualHost = "stb-terminal.example";
    static Task<CoreWebView2Environment>? _environment;

    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    readonly List<byte[]> _pending = new();
    readonly DispatcherTimer _flush;
    readonly Dictionary<string, TaskCompletionSource<string>> _textRequests = new();
    ISession? _session;
    bool _ready;
    JObject? _options;
    StreamWriter? _log;

    public int Cols { get; private set; } = 120;
    public int Rows { get; private set; } = 30;
    public string Title { get; private set; } = "";
    /// <summary>Last directory reported by the shell (OSC 7), if any.</summary>
    public string? Directory { get; private set; }
    public ISession? Session => _session;
    public bool IsReady => _ready;

    /// <summary>Text the user typed; the page decides where it goes (broadcast).</summary>
    public event Action<TerminalView, string>? Input;
    public event Action<TerminalView>? Ready, TitleChanged, Focused, Bell, SessionClosed;
    public event Action<TerminalView, string>? CommandRun, LineChanged, DirectoryChanged, Hotkey, Copy, Link;
    public event Action<TerminalView>? PasteRequested;
    public event Action<TerminalView, string>? MenuRequested;
    public event Action<bool>? Found;

    public TerminalView()
    {
        Child = _web;
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _flush.Tick += (_, _) => Flush();
        _web.GotFocus += (_, _) => Focused?.Invoke(this);
        Loaded += async (_, _) => await InitAsync();
    }

    public static bool RuntimeAvailable()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString() != null; }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    async Task InitAsync()
    {
        if (_web.CoreWebView2 != null) return;
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Data, "WebView2"));
            await _web.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Log.Error("Starting WebView2 failed", ex);
            Child = new TextBlock { Text = "无法启动终端：需要 Microsoft Edge WebView2 运行时。\n" + ex.Message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            return;
        }
        var core = _web.CoreWebView2!;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppPaths.Base, "terminal"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) => { e.Handled = true; Link?.Invoke(this, e.Uri); };
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JObject m;
        try { m = JObject.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        switch ((string?)m["t"])
        {
            case "ready":
                Cols = (int?)m["c"] ?? Cols;
                Rows = (int?)m["r"] ?? Rows;
                _ready = true;
                if (_options != null) Post(_options);
                _session?.Resize(Cols, Rows);
                Flush();
                Ready?.Invoke(this);
                break;
            case "input":
                Input?.Invoke(this, (string?)m["d"] ?? "");
                break;
            case "binary":
                // Mouse reports in X10 mode; one byte per char.
                var raw = Convert.FromBase64String((string?)m["d"] ?? "");
                var chars = new char[raw.Length];
                for (var i = 0; i < raw.Length; i++) chars[i] = (char)raw[i];
                Input?.Invoke(this, new string(chars));
                break;
            case "resize":
                Cols = (int?)m["c"] ?? Cols;
                Rows = (int?)m["r"] ?? Rows;
                _session?.Resize(Cols, Rows);
                break;
            case "title": Title = (string?)m["s"] ?? ""; TitleChanged?.Invoke(this); break;
            case "copy": Copy?.Invoke(this, (string?)m["s"] ?? ""); break;
            case "paste": PasteRequested?.Invoke(this); break;
            case "hotkey": Hotkey?.Invoke(this, (string?)m["k"] ?? ""); break;
            case "menu": MenuRequested?.Invoke(this, (string?)m["s"] ?? ""); break;
            case "link": Link?.Invoke(this, (string?)m["url"] ?? ""); break;
            case "bell": Bell?.Invoke(this); break;
            case "cwd": Directory = (string?)m["p"]; DirectoryChanged?.Invoke(this, Directory ?? ""); break;
            case "exec": CommandRun?.Invoke(this, (string?)m["s"] ?? ""); break;
            case "line": LineChanged?.Invoke(this, (string?)m["s"] ?? ""); break;
            case "found": Found?.Invoke((bool?)m["ok"] ?? false); break;
            case "text":
                var id = (string?)m["id"] ?? "";
                if (_textRequests.TryGetValue(id, out var tcs)) { _textRequests.Remove(id); tcs.TrySetResult((string?)m["s"] ?? ""); }
                break;
            case string z when z.StartsWith("z"):
                OnZmodem(z, m);
                break;
        }
    }

    void Post(object message)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(message is JObject j ? j.ToString(Formatting.None) : JsonConvert.SerializeObject(message));
    }

    // ---------- session ----------

    public void Attach(ISession session)
    {
        Detach();
        _session = session;
        session.Output += OnOutput;
        session.Closed += OnClosed;
        if (_ready) session.Resize(Cols, Rows);
    }

    public ISession? Detach()
    {
        var s = _session;
        if (s == null) return null;
        s.Output -= OnOutput;
        s.Closed -= OnClosed;
        _session = null;
        return s;
    }

    void OnOutput(byte[] data)
    {
        lock (_pending) _pending.Add(data);
        try { _log?.BaseStream.Write(data, 0, data.Length); } catch (IOException) { } catch (ObjectDisposedException) { }
        Dispatcher.BeginInvoke(() => { if (!_flush.IsEnabled) _flush.Start(); });
    }

    void OnClosed(string? reason) => Dispatcher.BeginInvoke(() =>
    {
        Flush();
        WriteText("\r\n\x1b[90m[" + (reason ?? "会话已结束") + "]\x1b[0m\r\n");
        SessionClosed?.Invoke(this);
    });

    void Flush()
    {
        _flush.Stop();
        if (!_ready) return;
        byte[] all;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            var length = 0;
            foreach (var p in _pending) length += p.Length;
            all = new byte[length];
            var at = 0;
            foreach (var p in _pending) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
            _pending.Clear();
        }
        Post(new { t = "data", d = Convert.ToBase64String(all) });
    }

    /// <summary>Sends keys to the session as if typed here.</summary>
    public void Send(string text) => _session?.Write(text);

    /// <summary>Writes text on the screen only (status messages); the session doesn't see it.</summary>
    public void WriteText(string text)
    {
        if (_ready) Post(new { t = "text", s = text });
        else lock (_pending) _pending.Add(System.Text.Encoding.UTF8.GetBytes(text));
    }

    // ---------- commands for the page ----------

    public void Apply(TerminalSettings s, double zoom = 0)
    {
        var theme = TerminalThemes.Get(s.Theme);
        _options = new JObject
        {
            ["t"] = "opts", ["theme"] = theme, ["fontFamily"] = s.FontFamily, ["fontSize"] = Math.Max(6, s.FontSize + zoom),
            ["cursorStyle"] = s.CursorStyle, ["cursorBlink"] = s.CursorBlink, ["scrollback"] = s.Scrollback, ["lineHeight"] = s.LineHeight,
            ["copyOnSelect"] = s.CopyOnSelect, ["rightClickPaste"] = s.RightClickPaste, ["suggest"] = s.Suggestions, ["background"] = theme["background"],
        };
        Background = new SolidColorBrush(TerminalThemes.Background(s.Theme));
        Post(_options);
    }

    public void Suggest(IEnumerable<(string Text, string Kind)> items)
    {
        var list = new JArray();
        foreach (var (text, kind) in items) list.Add(new JObject { ["text"] = text, ["kind"] = kind });
        Post(new JObject { ["t"] = "suggest", ["items"] = list });
    }

    public void Find(string query, bool back, bool caseSensitive, bool regex) => Post(new { t = "find", q = query, back, caseSensitive, regex });
    public void ClearFind() => Post(new { t = "clearFind" });
    public void FocusTerminal() { _web.Focus(); Post(new { t = "focus" }); }
    public void Clear() => Post(new { t = "clear" });
    public void Reset() => Post(new { t = "reset" });
    public void SelectAll() => Post(new { t = "selectAll" });
    /// <summary>Pastes through xterm.js so bracketed paste mode is honoured.</summary>
    public void Paste(string text) => Post(new { t = "paste", s = text });

    /// <summary>The last <paramref name="lines"/> lines of the screen and scrollback.</summary>
    public Task<string> GetTextAsync(int lines = 200)
    {
        if (!_ready) return Task.FromResult("");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>();
        _textRequests[id] = tcs;
        Post(new { t = "getText", id, lines });
        return tcs.Task;
    }

    public Task CaptureAsync(Stream stream) =>
        _web.CoreWebView2?.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream) ?? Task.CompletedTask;

    // ---------- session log ----------

    public bool IsLogging => _log != null;

    public void StartLog(string path)
    {
        StopLog();
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _log = new StreamWriter(path, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }

    public void StopLog()
    {
        var log = _log;
        _log = null;
        log?.Dispose();
    }

    public void Close()
    {
        StopLog();
        _flush.Stop();
        Detach()?.Dispose();
        _web.Dispose();
    }
}
