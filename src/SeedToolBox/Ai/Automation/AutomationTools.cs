using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;
using SeedToolBox.Reminders;
using SeedToolBox.ScreenTools;
using SeedToolBox.SystemTools;

namespace SeedToolBox.Ai.Automation;

sealed class ToolReply
{
    public string Text { get; init; } = "";
    public byte[]? Png { get; init; }
}

/// <summary>One tool the model can call. <see cref="Confirm"/> describes the change for the user, or returns null when nothing changes.</summary>
sealed class AutomationTool
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Guidelines { get; init; } = Array.Empty<string>();
    public JObject Parameters { get; init; } = new();
    public Func<JObject, AiService, string?>? Confirm { get; init; }
    /// <summary>"本次对话都允许" is offered; off for deleting, killing and commands, which are asked about every time.</summary>
    public bool SessionAllow { get; init; } = true;
    public Func<JObject, AiService, Task<ToolReply>> Run { get; init; } = (_, _) => Task.FromResult(new ToolReply());

    public JObject Manifest() => new()
    {
        ["name"] = Name,
        ["label"] = Label,
        ["description"] = Description,
        ["guidelines"] = new JArray(Guidelines),
        ["parameters"] = Parameters,
    };
}

/// <summary>What the automation mode can do. Everything runs on the UI thread unless it moves heavy work off it.</summary>
static class AutomationTools
{
    public static List<AutomationTool> All(AiSettings settings)
    {
        var list = new List<AutomationTool>
        {
            // Reading
            new()
            {
                Name = "list_folder", Label = "列出文件夹",
                Description = "列出文件夹里的文件和子文件夹（名称、大小、修改时间）。可以用 pattern 过滤，例如 *.pdf。",
                Parameters = Params(("path", "string", "文件夹完整路径，支持 %USERPROFILE% 这类环境变量", true), ("pattern", "string", "通配符过滤，默认 *", false), ("recursive", "boolean", "是否包含子文件夹，默认否", false)),
                Run = (a, _) => Task.Run(() => ListFolder(Str(a, "path"), Str(a, "pattern", "*"), Bool(a, "recursive"))),
            },
            new()
            {
                Name = "search_files", Label = "搜索文件",
                Description = "按文件名在整台电脑上搜索文件（装了 Everything 就用 Everything，否则用 Windows 搜索），最多返回 30 个。",
                Parameters = Params(("query", "string", "文件名关键词，可以用空格分隔多个词", true)),
                Run = (a, _) => Task.Run(() =>
                {
                    var found = FileSearch.Find(Str(a, "query"));
                    return Reply(found.Count == 0 ? "没有找到" : $"（{FileSearch.Engine}）\n" + string.Join("\n", found));
                }),
            },
            new()
            {
                Name = "read_text_file", Label = "读取文本文件",
                Description = "读取文本文件的内容（自动识别 UTF-8 / GBK），默认最多 20000 个字符。",
                Parameters = Params(("path", "string", "文件完整路径", true), ("max_chars", "integer", "最多读取的字符数", false)),
                Run = (a, _) => Task.Run(() => ReadText(Str(a, "path"), Int(a, "max_chars", 20000))),
            },
            new()
            {
                Name = "list_processes", Label = "查看进程",
                Description = "列出正在运行的程序：进程名、PID、内存占用、窗口标题，按内存从大到小。",
                Parameters = Params(("filter", "string", "只看名称或标题包含这个词的进程", false)),
                Run = (a, _) => Task.Run(() => Processes(Str(a, "filter", ""))),
            },
            new()
            {
                Name = "list_ports", Label = "查看端口",
                Description = "列出正在监听的 TCP/UDP 端口和占用它们的进程。",
                Parameters = Params(("port", "integer", "只看这个端口", false)),
                Run = (a, _) => Task.Run(() => Ports(Int(a, "port", 0))),
            },
            new()
            {
                Name = "system_status", Label = "系统状态",
                Description = "当前 CPU、内存、显卡占用和网速，各磁盘剩余空间，系统版本和开机时长。",
                Parameters = Params(),
                Run = (_, _) => SystemStatus(),
            },
            new()
            {
                Name = "read_clipboard", Label = "读取剪贴板",
                Description = "读取剪贴板里的文字（或复制的文件列表）。",
                Parameters = Params(),
                Run = (_, _) => Task.FromResult(ReadClipboard()),
            },
            new()
            {
                Name = "list_audio_devices", Label = "查看声音设备",
                Description = "列出输出和输入设备（标出默认设备和音量）、正在出声的程序，以及能调亮度的屏幕。",
                Parameters = Params(),
                Run = (_, _) => Task.Run(AudioStatus),
            },
            new()
            {
                Name = "list_reminders", Label = "查看提醒",
                Description = "列出所有还没到时间的提醒。",
                Parameters = Params(),
                Run = (_, s) => Task.FromResult(Reply(s.Reminders == null || s.Reminders.Items.Count == 0 ? "没有提醒"
                    : string.Join("\n", s.Reminders.Items.Select(r => $"{r.Due:yyyy-MM-dd HH:mm} {r.RepeatText} {r.Text}")))),
            },
            new()
            {
                Name = "recognize_text", Label = "识别文字",
                Description = "识别图片文件里的文字（OCR）。不传 path 时识别当前整个屏幕（需要用户同意截屏）。",
                Parameters = Params(("path", "string", "图片文件路径；留空表示当前屏幕", false), ("table", "boolean", "按表格识别，返回制表符分隔的行", false)),
                Confirm = (a, _) => Str(a, "path", "").Length == 0 ? "截取当前整个屏幕并识别上面的文字" : null,
                Run = (a, _) => Recognize(Str(a, "path", ""), Bool(a, "table")),
            },
            new()
            {
                Name = "screenshot", Label = "截屏",
                Description = "截取当前整个屏幕，把图片交给你查看。",
                Parameters = Params(),
                Confirm = (_, _) => "截取当前整个屏幕发给模型（屏幕上的所有内容都会被看到）",
                Run = (_, _) => Task.FromResult(new ToolReply { Text = "这是当前屏幕", Png = ScreenPng() }),
            },

            // Changing: each one confirmed by the user
            new()
            {
                Name = "move_files", Label = "移动 / 重命名文件",
                Description = "移动或重命名文件和文件夹，可以一次处理很多个。目标文件夹不存在会自动创建；目标已存在时这一项会跳过。",
                Guidelines = new[] { "Use move_files with all items in one call instead of one call per file, so the user confirms once." },
                Parameters = PairParams("from", "to", "原路径", "新路径（完整路径，不是文件夹）"),
                Confirm = (a, s) => DescribePairs(a, s, "移动", "from", "to"),
                Run = (a, s) => Task.Run(() => MoveFiles(a, s)),
            },
            new()
            {
                Name = "copy_files", Label = "复制文件",
                Description = "复制文件或文件夹，可以一次处理很多个。目标已存在时这一项会跳过。",
                Parameters = PairParams("from", "to", "原路径", "复制到的完整路径"),
                Confirm = (a, s) => DescribePairs(a, s, "复制", "from", "to"),
                Run = (a, s) => Task.Run(() => CopyFiles(a, s)),
            },
            new()
            {
                Name = "delete_files", Label = "删除文件",
                Description = "把文件或文件夹移到回收站（不会彻底删除）。",
                Parameters = Params(("paths", "string[]", "要删除的完整路径", true)),
                Confirm = (a, s) => "移到回收站：\n" + string.Join("\n", Strings(a, "paths").Select(p => FileActions.Writable(s.Settings, p))),
                SessionAllow = false,
                Run = (a, s) => Task.Run(() => DeleteFiles(a, s)),
            },
            new()
            {
                Name = "create_folder", Label = "新建文件夹",
                Description = "新建文件夹（可以多级）。",
                Parameters = Params(("path", "string", "文件夹完整路径", true)),
                Confirm = (a, s) => "新建文件夹：" + FileActions.Writable(s.Settings, Str(a, "path")),
                Run = (a, s) => Task.FromResult(CreateFolder(a, s)),
            },
            new()
            {
                Name = "write_text_file", Label = "写入文本文件",
                Description = "新建或覆盖文本文件（UTF-8）。覆盖前会备份原文件，可以撤销。",
                Parameters = Params(("path", "string", "文件完整路径", true), ("content", "string", "文件内容", true), ("overwrite", "boolean", "文件已存在时是否覆盖，默认否", false)),
                Confirm = (a, s) =>
                {
                    var path = FileActions.Writable(s.Settings, Str(a, "path"));
                    var content = Str(a, "content", "");
                    return $"{(File.Exists(path) ? "覆盖" : "新建")}文件：{path}\n\n{(content.Length > 1200 ? content.Substring(0, 1200) + "…" : content)}";
                },
                Run = (a, s) => Task.Run(() => WriteText(a, s)),
            },
            new()
            {
                Name = "set_clipboard", Label = "写入剪贴板",
                Description = "把文字放到剪贴板上。",
                Parameters = Params(("text", "string", "文字", true)),
                Confirm = (a, _) => "复制到剪贴板：\n" + Clip(Str(a, "text", ""), 800),
                Run = (a, _) => { ScreenToolService.CopyText(Str(a, "text", "")); return Task.FromResult(Reply("已复制到剪贴板")); },
            },
            new()
            {
                Name = "kill_process", Label = "结束进程",
                Description = "结束一个进程（未保存的数据会丢失）。先用 list_processes 查到 PID。",
                Parameters = Params(("pid", "integer", "进程 PID", true)),
                Confirm = (a, _) => DescribeProcess(Int(a, "pid", 0)),
                SessionAllow = false,
                Run = (a, _) => Task.Run(() => Kill(Int(a, "pid", 0))),
            },
            new()
            {
                Name = "set_audio_output", Label = "切换输出设备",
                Description = "把默认的声音输出设备换成名称包含 name 的设备。",
                Parameters = Params(("name", "string", "设备名称里的一部分，例如 耳机、Realtek", true)),
                Confirm = (a, _) => "把默认输出设备切换到：" + FindOutput(Str(a, "name")).Name,
                Run = (a, _) => { var d = FindOutput(Str(a, "name")); AudioDevices.SetDefault(d.Id); return Task.FromResult(Reply("已切换到 " + d.Name)); },
            },
            new()
            {
                Name = "set_volume", Label = "调音量",
                Description = "设置默认输出设备的音量或静音；传 app 时只调这个程序的音量。",
                Parameters = Params(("percent", "integer", "音量 0–100；不传则不改", false), ("mute", "boolean", "静音或取消静音；不传则不改", false), ("app", "string", "程序名称里的一部分，例如 Chrome", false)),
                Confirm = (a, _) => DescribeVolume(a),
                Run = (a, _) => Task.FromResult(SetVolume(a)),
            },
            new()
            {
                Name = "set_brightness", Label = "调亮度",
                Description = "设置所有能调的屏幕的亮度（外接显示器需要支持 DDC/CI）。",
                Parameters = Params(("percent", "integer", "亮度 0–100", true)),
                Confirm = (a, _) => $"把屏幕亮度调到 {Math.Max(0, Math.Min(100, Int(a, "percent", 50)))}%",
                Run = (a, _) => Task.Run(() =>
                {
                    var displays = Brightness.Displays();
                    if (displays.Count == 0) throw new ToolException("没有能调亮度的屏幕");
                    foreach (var d in displays) Brightness.Set(d, Int(a, "percent", 50));
                    return Reply($"已把 {displays.Count} 块屏幕调到 {displays[0].Brightness}%");
                }),
            },
            new()
            {
                Name = "add_reminder", Label = "添加提醒",
                Description = "添加一个到时间弹出的提醒。time 用 yyyy-MM-dd HH:mm；也可以不传 time，直接在 text 里写中文，例如「明天下午3点 开会」「每天9点 吃药」「半小时后 关火」。",
                Parameters = Params(("text", "string", "提醒内容（不传 time 时包含时间）", true), ("time", "string", "yyyy-MM-dd HH:mm", false), ("repeat", "string", "none / daily / weekdays / weekly", false)),
                Confirm = (a, _) => { var r = ParseReminder(a); return $"添加提醒：{r.DueText(DateTime.Now)} {r.Text}"; },
                Run = (a, s) => Task.FromResult(AddReminder(a, s)),
            },
            new()
            {
                Name = "add_note", Label = "写笔记",
                Description = "在 SeedToolBox 的快速笔记里新建一条笔记。",
                Parameters = Params(("text", "string", "笔记内容，第一行是标题", true)),
                Confirm = (a, _) => "新建笔记：\n" + Clip(Str(a, "text", ""), 800),
                Run = (a, s) => Task.FromResult(AddNote(a, s)),
            },
            new()
            {
                Name = "open", Label = "打开",
                Description = "用默认程序打开文件、文件夹或网址，或者启动程序。",
                Parameters = Params(("target", "string", "文件路径、文件夹、网址或程序路径", true), ("arguments", "string", "启动程序时的参数", false)),
                Confirm = (a, _) => "打开：" + Str(a, "target") + (Str(a, "arguments", "").Length > 0 ? " " + Str(a, "arguments", "") : ""),
                Run = (a, _) => Task.FromResult(Open(Str(a, "target"), Str(a, "arguments", ""))),
            },
        };
        if (settings.AllowCommands)
            list.Add(new()
            {
                Name = "run_command", Label = "运行命令",
                Description = "在 PowerShell 里运行一条命令并返回输出（最长 2 分钟）。工作目录是第一个允许的文件夹。",
                Guidelines = new[] { "Prefer the dedicated SeedToolBox tools over run_command; use run_command only when no other tool can do it." },
                Parameters = Params(("command", "string", "PowerShell 命令", true)),
                Confirm = (a, _) => "运行 PowerShell 命令：\n\n" + Str(a, "command"),
                SessionAllow = false,
                Run = (a, s) => Task.Run(() => RunCommand(Str(a, "command"), s.Settings)),
            });
        return list;
    }

    // Parameters

    static JObject Params(params (string Name, string Type, string Description, bool Required)[] items)
    {
        var properties = new JObject();
        foreach (var (name, type, description, _) in items)
            properties[name] = type == "string[]"
                ? new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = description }
                : new JObject { ["type"] = type, ["description"] = description };
        return new JObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JArray(items.Where(i => i.Required).Select(i => i.Name)),
        };
    }

    static JObject PairParams(string from, string to, string fromText, string toText) => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["items"] = new JObject
            {
                ["type"] = "array",
                ["description"] = "要处理的文件",
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { [from] = new JObject { ["type"] = "string", ["description"] = fromText }, [to] = new JObject { ["type"] = "string", ["description"] = toText } },
                    ["required"] = new JArray(from, to),
                },
            },
        },
        ["required"] = new JArray("items"),
    };

    static string Str(JObject a, string name, string? fallback = null)
    {
        var value = a[name];
        if (value == null || value.Type == JTokenType.Null) return fallback ?? throw new ToolException("缺少参数 " + name);
        return value.Type == JTokenType.String ? (string)value! : value.ToString();
    }

    static int Int(JObject a, string name, int fallback) =>
        a[name] is { Type: JTokenType.Integer or JTokenType.Float or JTokenType.String } v && double.TryParse(v.ToString(), out var d) ? (int)Math.Round(d) : fallback;

    static bool? OptionalBool(JObject a, string name) => a[name] is { Type: JTokenType.Boolean } v ? (bool)v : a[name] is { Type: JTokenType.String } s ? s.ToString() == "true" : null;
    static bool Bool(JObject a, string name) => OptionalBool(a, name) == true;

    static List<string> Strings(JObject a, string name) =>
        (a[name] as JArray ?? throw new ToolException("缺少参数 " + name)).Select(t => t.ToString()).ToList();

    static List<(string From, string To)> Pairs(JObject a, AiSettings s, string from, string to)
    {
        var items = a["items"] as JArray ?? throw new ToolException("缺少参数 items");
        if (items.Count == 0) throw new ToolException("items 是空的");
        if (items.Count > 2000) throw new ToolException("一次最多处理 2000 个");
        return items.OfType<JObject>().Select(i => (FileActions.Writable(s, Str(i, from)), FileActions.Writable(s, Str(i, to)))).ToList();
    }

    static ToolReply Reply(string text) => new() { Text = text };

    static string Clip(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

    // Files

    static ToolReply ListFolder(string path, string pattern, bool recursive)
    {
        var dir = FileActions.Readable(path);
        if (!Directory.Exists(dir)) throw new ToolException("文件夹不存在：" + dir);
        var sb = new StringBuilder();
        int count = 0, max = 500;
        var info = new DirectoryInfo(dir);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            foreach (var d in info.EnumerateDirectories(recursive ? "*" : pattern, option))
            {
                if (count++ >= max) break;
                sb.AppendLine($"[文件夹] {(recursive ? d.FullName : d.Name)}  {d.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
            foreach (var f in info.EnumerateFiles(pattern, option))
            {
                if (count++ >= max) break;
                sb.AppendLine($"{(recursive ? f.FullName : f.Name)}  {Ui.FormatSize(f.Length)}  {f.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }
        catch (UnauthorizedAccessException ex) { sb.AppendLine("（部分内容没有权限读取：" + ex.Message + "）"); }
        if (count > max) sb.AppendLine($"……只列出前 {max} 项，可以用 pattern 缩小范围");
        return Reply(sb.Length == 0 ? "文件夹是空的" : $"{dir}\n" + sb);
    }

    static ToolReply ReadText(string path, int max)
    {
        var file = FileActions.Readable(path);
        if (!File.Exists(file)) throw new ToolException("文件不存在：" + file);
        if (new FileInfo(file).Length > 20 << 20) throw new ToolException("文件超过 20 MB，太大了");
        var bytes = File.ReadAllBytes(file);
        if (bytes.Take(8000).Contains((byte)0) && !(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)) throw new ToolException("这不是文本文件");
        var text = TextFiles.Decode(bytes, out _);
        max = Math.Max(100, Math.Min(200000, max));
        return Reply(text.Length > max ? text.Substring(0, max) + $"\n……（共 {text.Length} 个字符，只读了前 {max} 个）" : text);
    }

    static string? DescribePairs(JObject a, AiService s, string verb, string from, string to)
    {
        var pairs = Pairs(a, s.Settings, from, to);
        var lines = pairs.Take(40).Select(p => Path.GetDirectoryName(p.From) == Path.GetDirectoryName(p.To)
            ? $"{p.From}  →  {Path.GetFileName(p.To)}"
            : $"{p.From}  →  {p.To}");
        return $"{verb} {pairs.Count} 项：\n" + string.Join("\n", lines) + (pairs.Count > 40 ? $"\n……还有 {pairs.Count - 40} 项" : "");
    }

    static ToolReply MoveFiles(JObject a, AiService s)
    {
        var done = new List<string[]>();
        var problems = new List<string>();
        foreach (var (from, to) in Pairs(a, s.Settings, "from", "to"))
        {
            try
            {
                if (!FileActions.Exists(from)) { problems.Add("不存在：" + from); continue; }
                if (FileActions.Exists(to) && !from.Equals(to, StringComparison.OrdinalIgnoreCase)) { problems.Add("目标已存在，跳过：" + to); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                done.Add(new[] { from, to });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{from}：{ex.Message}"); }
        }
        if (done.Count > 0) s.Operations.Add(new Operation { Kind = OperationKind.Move, Items = done, Summary = $"移动 / 重命名 {done.Count} 项" + Example(done) });
        return Result(done.Count, "移动", problems);
    }

    static ToolReply CopyFiles(JObject a, AiService s)
    {
        var done = new List<string[]>();
        var problems = new List<string>();
        foreach (var (from, to) in Pairs(a, s.Settings, "from", "to"))
        {
            try
            {
                if (FileActions.Exists(to)) { problems.Add("目标已存在，跳过：" + to); continue; }
                if (Directory.Exists(from)) Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(from, to);
                else if (File.Exists(from)) { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to); }
                else { problems.Add("不存在：" + from); continue; }
                done.Add(new[] { to, from });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{from}：{ex.Message}"); }
        }
        if (done.Count > 0) s.Operations.Add(new Operation { Kind = OperationKind.Copy, Items = done, Summary = $"复制 {done.Count} 项" + Example(done.Select(d => new[] { d[1], d[0] }).ToList()) });
        return Result(done.Count, "复制", problems);
    }

    static ToolReply DeleteFiles(JObject a, AiService s)
    {
        var done = new List<string>();
        var problems = new List<string>();
        foreach (var path in Strings(a, "paths").Select(p => FileActions.Writable(s.Settings, p)))
        {
            try { FileActions.Recycle(path); done.Add(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { problems.Add($"{path}：{ex.Message}"); }
        }
        if (done.Count > 0) s.Operations.Add(new Operation { Kind = OperationKind.Other, Items = done.Select(d => new[] { d }).ToList(), Summary = $"移到回收站 {done.Count} 项（在回收站里还原）：{Path.GetFileName(done[0])}{(done.Count > 1 ? " 等" : "")}" });
        return Result(done.Count, "移到回收站", problems);
    }

    static ToolReply CreateFolder(JObject a, AiService s)
    {
        var path = FileActions.Writable(s.Settings, Str(a, "path"));
        if (Directory.Exists(path)) return Reply("文件夹已经存在");
        // Remember the topmost folder that is new, so undo removes the whole new chain
        var top = path;
        while (Path.GetDirectoryName(top) is { } parent && !Directory.Exists(parent)) top = parent;
        Directory.CreateDirectory(path);
        s.Operations.Add(new Operation { Kind = OperationKind.CreateFolder, Items = new() { new[] { top } }, Summary = "新建文件夹 " + path });
        return Reply("已新建 " + path);
    }

    static ToolReply WriteText(JObject a, AiService s)
    {
        var path = FileActions.Writable(s.Settings, Str(a, "path"));
        var exists = File.Exists(path);
        if (exists && !Bool(a, "overwrite")) throw new ToolException("文件已存在；要覆盖请传 overwrite=true");
        string? backup = null;
        if (exists)
        {
            Directory.CreateDirectory(OperationLog.BackupDir);
            backup = Path.Combine(OperationLog.BackupDir, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            File.Copy(path, backup);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Str(a, "content", ""), new UTF8Encoding(false));
        s.Operations.Add(new Operation { Kind = OperationKind.WriteFile, Items = new() { new[] { path } }, Backup = backup, Summary = (exists ? "覆盖 " : "新建 ") + path });
        return Reply((exists ? "已覆盖 " : "已写入 ") + path);
    }

    static string Example(List<string[]> pairs) => $"：{Path.GetFileName(pairs[0][0])} → {pairs[0][1]}{(pairs.Count > 1 ? " 等" : "")}";

    static ToolReply Result(int done, string verb, List<string> problems)
    {
        var text = $"已{verb} {done} 项";
        if (problems.Count > 0) text += $"，{problems.Count} 项没有处理：\n" + string.Join("\n", problems.Take(50));
        if (done == 0 && problems.Count > 0) throw new ToolException(text);
        return Reply(text);
    }

    // System

    static ToolReply Processes(string filter)
    {
        var rows = new List<(long Memory, string Line)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var title = p.MainWindowTitle;
                    if (filter.Length > 0 && p.ProcessName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    rows.Add((p.WorkingSet64, $"{p.ProcessName}  PID {p.Id}  {Ui.FormatSize(p.WorkingSet64)}{(title.Length > 0 ? "  「" + title + "」" : "")}"));
                }
                catch (InvalidOperationException) { } // exited
            }
        }
        var top = rows.OrderByDescending(r => r.Memory).Take(80).Select(r => r.Line).ToList();
        return Reply(top.Count == 0 ? "没有匹配的进程" : $"共 {rows.Count} 个，按内存排序：\n" + string.Join("\n", top));
    }

    static ToolReply Ports(int port)
    {
        var entries = PortTable.Read().Where(e => (e.State == "LISTEN" || e.Protocol.StartsWith("UDP")) && (port == 0 || e.Port == port))
            .GroupBy(e => (e.Protocol, e.Port, e.Pid)).Select(g => g.First()).OrderBy(e => e.Port).Take(200).ToList();
        return Reply(entries.Count == 0 ? "没有找到" : string.Join("\n", entries.Select(e => $"{e.Protocol} {e.Local}  {e.Process}（PID {e.Pid}）")));
    }

    static async Task<ToolReply> SystemStatus()
    {
        MonitorSample? sample = null;
        void Handler(MonitorSample s) => sample = s;
        // Two samples are needed for CPU and network rates
        SystemMonitor.Subscribe(Handler);
        try { await Task.Delay(2200); }
        finally { SystemMonitor.Unsubscribe(Handler); }
        var sb = new StringBuilder();
        if (sample != null)
        {
            sb.AppendLine($"CPU {sample.Cpu:0}%  内存 {sample.Memory:0}%（{SystemMonitor.Gb(sample.MemoryUsed)} / {SystemMonitor.Gb(sample.MemoryTotal)}）" + (sample.Gpu >= 0 ? $"  显卡 {sample.Gpu:0}%" : ""));
            sb.AppendLine($"网速 ↓{SystemMonitor.Rate(sample.Down)} ↑{SystemMonitor.Rate(sample.Up)}");
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            sb.AppendLine($"{d.Name} {d.VolumeLabel}  剩余 {Ui.FormatSize(d.AvailableFreeSpace)} / {Ui.FormatSize(d.TotalSize)}");
        sb.AppendLine($"系统 {Environment.OSVersion.VersionString}，{Environment.ProcessorCount} 个逻辑处理器");
        var up = TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
        sb.AppendLine($"已开机 {(int)up.TotalDays} 天 {up.Hours} 小时 {up.Minutes} 分钟");
        return Reply(sb.ToString());
    }

    static ToolReply ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsText()) return Reply(Clip(Clipboard.GetText(), 50000));
            if (Clipboard.ContainsFileDropList()) return Reply("复制的文件：\n" + string.Join("\n", Clipboard.GetFileDropList().Cast<string>()));
            if (Clipboard.ContainsImage()) return Reply("剪贴板里是一张图片");
        }
        catch (COMException ex) { throw new ToolException("剪贴板正被别的程序占用：" + ex.Message); }
        return Reply("剪贴板是空的");
    }

    static ToolReply AudioStatus()
    {
        var sb = new StringBuilder("输出设备：\n");
        foreach (var d in AudioDevices.Outputs()) sb.AppendLine($"  {d.Name}{(d.IsDefault ? $"（默认，音量 {AudioDevices.GetVolume(d.Id) * 100:0}%{(AudioDevices.GetMute(d.Id) ? "，静音" : "")}）" : "")}");
        sb.AppendLine("输入设备：");
        foreach (var d in AudioDevices.Inputs()) sb.AppendLine($"  {d.Name}{(d.IsDefault ? $"（默认{(AudioDevices.GetMute(d.Id, true) ? "，静音" : "")}）" : "")}");
        var sessions = AudioDevices.Sessions();
        if (sessions.Count > 0)
        {
            sb.AppendLine("正在出声的程序：");
            foreach (var s in sessions) sb.AppendLine($"  {s.Name} {s.Volume * 100:0}%{(s.Muted ? " 静音" : "")}");
        }
        var displays = Brightness.Displays();
        sb.AppendLine(displays.Count == 0 ? "没有能调亮度的屏幕" : "屏幕亮度：" + string.Join("，", displays.Select(d => $"{d.Name} {d.Brightness}%")));
        return Reply(sb.ToString());
    }

    static AudioDevice FindOutput(string name)
    {
        var outputs = AudioDevices.Outputs();
        return outputs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? outputs.FirstOrDefault(d => d.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new ToolException($"没有名称包含「{name}」的输出设备。现有：{string.Join("；", outputs.Select(d => d.Name))}");
    }

    static string DescribeVolume(JObject a)
    {
        var app = Str(a, "app", "");
        var parts = new List<string>();
        if (a["percent"] != null) parts.Add($"音量调到 {Math.Max(0, Math.Min(100, Int(a, "percent", 50)))}%");
        if (OptionalBool(a, "mute") is { } mute) parts.Add(mute ? "静音" : "取消静音");
        if (parts.Count == 0) throw new ToolException("percent 和 mute 至少要传一个");
        return (app.Length > 0 ? FindSession(app).Name : "默认输出设备") + "：" + string.Join("，", parts);
    }

    static AudioSession FindSession(string app)
    {
        var sessions = AudioDevices.Sessions();
        return sessions.FirstOrDefault(s => s.Name.IndexOf(app, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new ToolException($"没有找到正在出声的「{app}」。现在出声的：{string.Join("；", sessions.Select(s => s.Name))}");
    }

    static ToolReply SetVolume(JObject a)
    {
        var app = Str(a, "app", "");
        float? level = a["percent"] != null ? Math.Max(0, Math.Min(100, Int(a, "percent", 50))) / 100f : null;
        var mute = OptionalBool(a, "mute");
        if (app.Length > 0)
        {
            var session = FindSession(app);
            if (level is { } l) AudioDevices.SetSessionVolume(session, l);
            if (mute is { } m) AudioDevices.SetSessionMute(session, m);
        }
        else
        {
            if (level is { } l) AudioDevices.SetVolume(null, l);
            if (mute is { } m) AudioDevices.SetMute(null, m);
        }
        return Reply("已设置");
    }

    static string DescribeProcess(int pid)
    {
        if (pid <= 4) throw new ToolException("这是系统进程，不能结束");
        if (pid == Process.GetCurrentProcess().Id) throw new ToolException("这是 SeedToolBox 自己，不能结束");
        try
        {
            using var p = Process.GetProcessById(pid);
            return $"结束进程 {p.ProcessName}（PID {pid}）{(p.MainWindowTitle.Length > 0 ? "「" + p.MainWindowTitle + "」" : "")}\n未保存的数据会丢失。";
        }
        catch (ArgumentException) { throw new ToolException($"PID {pid} 不存在"); }
    }

    static ToolReply Kill(int pid)
    {
        DescribeProcess(pid);
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill();
            p.WaitForExit(3000);
            return Reply($"已结束 {name}（PID {pid}）");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new ToolException("结束失败：" + (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 } ? "拒绝访问，需要管理员权限" : ex.Message));
        }
    }

    // Screen

    static byte[] ScreenPng()
    {
        var shot = ScreenShot.Capture();
        BitmapSource image = shot.Image;
        // Models downscale anyway; keep the upload small
        double scale = Math.Min(1, 1920.0 / image.PixelWidth);
        if (scale < 1)
        {
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            image.Freeze();
        }
        return Png(image);
    }

    static async Task<ToolReply> Recognize(string path, bool table)
    {
        BitmapSource image;
        if (path.Length == 0) image = ScreenShot.Capture().Image;
        else
        {
            var file = FileActions.Readable(path);
            if (!File.Exists(file)) throw new ToolException("文件不存在：" + file);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        if (!TextRecognizer.IsAvailable) throw new ToolException("这台电脑没有可用的文字识别");
        if (table)
        {
            var rows = await TextRecognizer.RecognizeTableAsync(image);
            return Reply(rows.Count == 0 ? "没有识别到表格" : string.Join("\n", rows.Select(r => string.Join("\t", r))));
        }
        var text = await TextRecognizer.RecognizeAsync(image);
        return Reply(text.Trim().Length == 0 ? "没有识别到文字" : text);
    }

    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // SeedToolBox

    static Reminder ParseReminder(JObject a)
    {
        var text = Str(a, "text").Trim();
        var time = Str(a, "time", "");
        Reminder reminder;
        if (time.Length > 0)
        {
            if (!DateTime.TryParse(time, out var due)) throw new ToolException("time 格式不对，要 yyyy-MM-dd HH:mm");
            reminder = new Reminder { Text = text, Due = due };
            reminder.Repeat = Str(a, "repeat", "none").ToLowerInvariant() switch
            {
                "daily" => ReminderRepeat.Daily,
                "weekdays" => ReminderRepeat.Weekdays,
                "weekly" => ReminderRepeat.Weekly,
                _ => ReminderRepeat.None,
            };
        }
        else if (!ReminderParser.TryParse(text, DateTime.Now, out reminder))
            throw new ToolException("看不懂提醒时间，请传 time（yyyy-MM-dd HH:mm）");
        if (reminder.Text.Trim().Length == 0) throw new ToolException("提醒内容是空的");
        if (reminder.Due <= DateTime.Now && reminder.Repeat == ReminderRepeat.None) throw new ToolException("这个时间已经过去了");
        if (reminder.Due <= DateTime.Now) reminder.Due = Reminder.Next(reminder.Due, reminder.Repeat, DateTime.Now);
        return reminder;
    }

    static ToolReply AddReminder(JObject a, AiService s)
    {
        if (s.Reminders == null) throw new ToolException("提醒功能不可用");
        var reminder = ParseReminder(a);
        s.Reminders.Add(reminder);
        s.Operations.Add(new Operation { Kind = OperationKind.Reminder, Target = reminder.Id, Summary = $"添加提醒：{reminder.Due:M月d日 HH:mm} {reminder.Text}" });
        return Reply("已添加提醒：" + reminder.DueText(DateTime.Now) + " " + reminder.Text);
    }

    static ToolReply AddNote(JObject a, AiService s)
    {
        if (s.Notes == null) throw new ToolException("笔记功能不可用");
        var note = s.Notes.Add();
        note.Text = Str(a, "text");
        s.Notes.RequestSave();
        s.Operations.Add(new Operation { Kind = OperationKind.Note, Target = note.Id, Summary = "新建笔记：" + note.Title });
        return Reply("已新建笔记「" + note.Title + "」");
    }

    static ToolReply Open(string target, string arguments)
    {
        target = target.Trim().Trim('"');
        bool url = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!url)
        {
            var path = FileActions.Readable(target);
            if (!FileActions.Exists(path) && !target.Contains(":") && !target.Contains("\\")) path = target; // a program on PATH, like notepad
            else if (!FileActions.Exists(path)) throw new ToolException("找不到：" + path);
            target = path;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ToolException("打不开：" + ex.Message);
        }
        return Reply("已打开 " + target);
    }

    static ToolReply RunCommand(string command, AiSettings settings)
    {
        var work = FileActions.Folders(settings).Select(FileActions.Normalize).FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // -EncodedCommand avoids every quoting problem
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + command));
        var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = work,
        };
        using var p = Process.Start(info)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool exited = p.WaitForExit(120000);
        if (!exited) { try { p.Kill(); } catch (InvalidOperationException) { } }
        else p.WaitForExit();
        string text;
        lock (output) text = output.ToString();
        text = Clip(text, 30000);
        return Reply((exited ? $"退出码 {p.ExitCode}" : "超过 2 分钟，已结束") + "\n" + text);
    }
}
