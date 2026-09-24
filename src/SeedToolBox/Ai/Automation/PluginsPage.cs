using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Ai.Automation;

/// <summary>pi packages (extensions, skills, prompt templates) installed with pi's own package manager.</summary>
sealed class PluginsPage : DockPanel
{
    readonly AiService _service;
    readonly StackPanel _packages = new();
    readonly StackPanel _skills = new();
    readonly TextBox _source = Ui.Field();
    readonly TextBox _output = Ui.Area(wrap: true);
    readonly TextBlock _status = Ui.Status();
    readonly List<Button> _actions = new();
    bool _busy;

    public PluginsPage(AiService service)
    {
        _service = service;
        var header = Ui.Header("AI 插件和技能", "给自动化模式装 pi 插件（扩展、技能、提示模板），走设置里选的 npm 镜像");
        SetDock(header, Dock.Top);
        Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x8A, 0x00)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "插件是别人写的程序，装上后以你的权限运行，能读写文件、联网。只装信得过的来源；插件提供的工具每次调用都会先问你。" +
                       "想接 MCP 服务器可以装 npm:pi-mcp-adapter。",
            },
        });

        _source.ToolTip = "npm:包名、git:github.com/用户/仓库，或本地文件夹";
        var install = Action("安装", () => Install(_source.Text.Trim()), accent: true);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = Ui.Row(install, Action("全部更新", () => _ = RunPi("更新", "update", "--extensions")), Ui.Button("刷新", Refresh));
        buttons.Margin = new Thickness(8, 0, 0, 0);
        SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(_source);
        body.Children.Add(Ui.Label("来源（npm:包名 / git:仓库地址 / 本地路径）"));
        body.Children.Add(row);
        body.Children.Add(_status);

        body.Children.Add(Section("已安装的插件"));
        body.Children.Add(_packages);

        var openSkills = Ui.Button("打开技能文件夹", () => OpenFolder(Path.Combine(PiRuntime.ConfigDir(service.Settings), "skills")));
        var openPrompts = Ui.Button("打开模板文件夹", () => OpenFolder(Path.Combine(PiRuntime.ConfigDir(service.Settings), "prompts")));
        body.Children.Add(Section("技能和提示模板"));
        body.Children.Add(Hint("在 AI 窗口输入 /skill:名字 或 /模板名 直接使用；把含 SKILL.md 的文件夹放进技能文件夹就能加自己的技能。"));
        body.Children.Add(Spaced(Ui.Row(openSkills, openPrompts)));
        body.Children.Add(_skills);

        _output.IsReadOnly = true;
        _output.Height = 140;
        _output.FontFamily = new FontFamily("Consolas");
        _output.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(Section("输出"));
        body.Children.Add(_output);

        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += (_, _) => Refresh();
    }

    Button Action(string text, Action run, bool accent = false)
    {
        var button = Ui.Button(text, run, accent);
        _actions.Add(button);
        return button;
    }

    static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 14, 0, 6) };
    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 0, 0, 6) };
    static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 0, 0, 6); return e; }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", PiRuntime.Quote(dir)) { UseShellExecute = true });
    }

    void Install(string source)
    {
        if (source.Length == 0) { Ui.SetStatus(_status, "先填来源", true); return; }
        if (!source.Contains(':') && !Directory.Exists(source) && !source.StartsWith("."))
            source = "npm:" + source;
        if (MessageBox.Show(Window.GetWindow(this), $"安装 {source}？\n\n插件以你的权限运行，只装信得过的来源。", "安装插件",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = RunPi("安装", "install", source);
    }

    /// <summary>Package sources from pi's settings.json: strings or { source } objects.</summary>
    List<string> Installed()
    {
        try
        {
            var path = Path.Combine(PiRuntime.ConfigDir(_service.Settings), "settings.json");
            if (!File.Exists(path)) return new();
            var json = JObject.Parse(File.ReadAllText(path));
            return (json["packages"] as JArray ?? new JArray())
                .Select(p => p.Type == JTokenType.String ? (string?)p : (string?)p["source"])
                .Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            Ui.SetStatus(_status, "读不了 pi 的设置：" + ex.Message, true);
            return new();
        }
    }

    void Refresh()
    {
        _packages.Children.Clear();
        _actions.RemoveRange(2, _actions.Count - 2); // keep 安装 and 全部更新
        var packages = Installed();
        if (packages.Count == 0) _packages.Children.Add(Hint("还没有装插件"));
        foreach (var package in packages)
        {
            var source = package;
            var update = Action("更新", () => _ = RunPi("更新", "update", source));
            var remove = Action("移除", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), "移除 " + source + "？", "移除插件", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _ = RunPi("移除", "remove", source);
            });
            _packages.Children.Add(Card(source, "", Ui.Row(update, remove)));
        }
        foreach (var b in _actions) b.IsEnabled = !_busy;

        _skills.Children.Clear();
        var config = PiRuntime.ConfigDir(_service.Settings);
        var items = Skills(Path.Combine(AppPaths.Base, "Pi", "skills"), "内置技能")
            .Concat(Skills(Path.Combine(config, "skills"), "技能"))
            .Concat(Prompts(Path.Combine(AppPaths.Base, "Pi", "prompts"), "内置模板"))
            .Concat(Prompts(Path.Combine(config, "prompts"), "模板")).ToList();
        if (items.Count == 0) _skills.Children.Add(Hint("没有技能和模板"));
        foreach (var (name, description, kind) in items)
            _skills.Children.Add(Card(name, description, new TextBlock { Text = kind, Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center }));
    }

    static IEnumerable<(string, string, string)> Skills(string dir, string kind)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in SafeFiles(dir, "SKILL.md"))
        {
            var (name, description) = Frontmatter(file);
            yield return ("/skill:" + (name ?? Path.GetFileName(Path.GetDirectoryName(file))), description ?? "", kind);
        }
    }

    static IEnumerable<(string, string, string)> Prompts(string dir, string kind)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.md"))
            yield return ("/" + Path.GetFileNameWithoutExtension(file), Frontmatter(file).Description ?? "", kind);
    }

    static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    static (string? Name, string? Description) Frontmatter(string file)
    {
        string? name = null, description = null;
        try
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null);
            foreach (var line in lines.Skip(1))
            {
                if (line.Trim() == "---") break;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                var value = line.Substring(colon + 1).Trim().Trim('"');
                switch (line.Substring(0, colon).Trim())
                {
                    case "name": name = value; break;
                    case "description": description = value; break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (name, description);
    }

    static Border Card(string title, string detail, FrameworkElement right)
    {
        right.Margin = new Thickness(8, 0, 0, 0);
        var card = new DockPanel();
        SetDock(right, Dock.Right);
        card.Children.Add(right);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail.Length > 0)
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        card.Children.Add(text);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = card,
        };
    }

    async Task RunPi(string verb, params string[] args)
    {
        if (_busy) return;
        var install = PiRuntime.Find(_service.Settings);
        if (install == null) { Ui.SetStatus(_status, "还没有装 pi，先在 AI 设置里安装", true); return; }
        _busy = true;
        foreach (var b in _actions) b.IsEnabled = false;
        Ui.SetStatus(_status, verb + "中…");
        _output.Clear();
        var info = PiRuntime.StartInfo(install, _service.Settings, args);
        var registry = PiMirrors.Order(_service.Settings).First().Registry;
        info.EnvironmentVariables["npm_config_registry"] = registry;
        info.EnvironmentVariables["npm_config_fund"] = "false";
        info.EnvironmentVariables["npm_config_audit"] = "false";
        info.EnvironmentVariables["NO_COLOR"] = "1";
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardOutputEncoding = info.StandardErrorEncoding = new UTF8Encoding(false);
        int code = -1;
        try
        {
            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            void Append(string? line)
            {
                if (line == null) return;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;?]*[A-Za-z]", "");
                Dispatcher.BeginInvoke(new Action(() => { _output.AppendText(line + "\n"); _output.ScrollToEnd(); }));
            }
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Run(() => { if (!process.WaitForExit(10 * 60 * 1000)) process.Kill(); process.WaitForExit(); });
            code = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("pi " + verb + " failed", ex);
            _output.AppendText(ex.Message + "\n");
        }
        _busy = false;
        Ui.SetStatus(_status, code == 0 ? verb + "完成，新开的自动化对话里生效" : verb + "失败，看下面的输出", code != 0);
        Refresh();
    }
}
