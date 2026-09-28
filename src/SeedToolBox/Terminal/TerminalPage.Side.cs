using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Renci.SshNet;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

sealed partial class TerminalPage
{
    GridSplitter _sideSplitter = null!;
    ColumnDefinition _sideColumn = null!;
    FrameworkElement _hosts = null!;
    SftpPanel _sftp = null!;
    MonitorPanel _monitor = null!;
    DockerPanel _docker = null!;
    readonly TransfersPanel _transfersPanel = new();
    SnippetPanel _snippets = null!;
    readonly ContentControl _sideHost = new();
    readonly TextBlock _sideTitle = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly List<(int Index, Button Button, Border Mark)> _railButtons = new();
    int _side = -1;

    const int SideHosts = 0, SideSftp = 1, SideMonitor = 2, SideSnippets = 3, SideHistory = 4, SideDocker = 5, SideTransfers = 6;
    static readonly string[] SideNames = { "主机", "文件", "服务器监控", "命令片段", "历史命令", "Docker", "传输" };

    /// <summary>Makes bash and zsh report their directory (OSC 7) so SFTP can follow cd.</summary>
    const string ShellIntegration =
        " if [ -n \"$ZSH_VERSION\" ]; then __stb_cwd(){ printf '\\033]7;file://%s%s\\007' \"$HOST\" \"$PWD\"; }; precmd_functions+=(__stb_cwd);" +
        " else PROMPT_COMMAND='printf \"\\033]7;file://%s%s\\007\" \"$HOSTNAME\" \"$PWD\"'\"${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; fi\r";

    Button RailButton(string glyph, string tip)
    {
        var b = new Button { Content = Glyph(glyph, 17), ToolTip = tip, Padding = new Thickness(0, 10, 0, 10), Width = 44, Style = FlatButton };
        b.SetResourceReference(Control.ForegroundProperty, "SecondaryTextBrush");
        return b;
    }

    /// <summary>Number of running transfers on the rail button.</summary>
    static FrameworkElement TransferBadge()
    {
        var text = new TextBlock { FontSize = 9, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var badge = new Border
        {
            Background = Brand, CornerRadius = new CornerRadius(7), MinWidth = 14, Height = 14, Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 5, 0),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = text,
        };
        void Update()
        {
            var n = Transfers.ActiveCount;
            text.Text = n > 99 ? "99+" : n.ToString();
            badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Transfers.Changed += () => badge.Dispatcher.BeginInvoke(Update);
        return badge;
    }

    FrameworkElement BuildRail()
    {
        var rail = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Add(int index, string glyph)
        {
            var b = RailButton(glyph, SideNames[index]);
            b.Click += (_, _) => ToggleSide(index);
            var mark = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = Brand, Visibility = Visibility.Hidden, Margin = new Thickness(0, 6, 0, 6) };
            var cell = new Grid();
            cell.Children.Add(b);
            cell.Children.Add(mark);
            rail.Children.Add(cell);
            _railButtons.Add((index, b, mark));
            if (index == SideTransfers) cell.Children.Add(TransferBadge());
        }
        Add(SideHosts, "\uE7F4");
        Add(SideSftp, "\uE8B7");
        Add(SideMonitor, "\uE9D9");
        Add(SideDocker, "\uE7B8");
        Add(SideTransfers, "\uE896");
        Add(SideSnippets, "\uE943");
        Add(SideHistory, "\uE81C");
        var more = RailButton("\uE712", "更多");
        more.Click += (_, _) => ShowMenu(ToolsMenu(), more);
        rail.Children.Add(more);
        var border = new Border { Child = rail, BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        return border;
    }

    FrameworkElement BuildSidePanel()
    {
        _hosts = BuildSidebar();
        _sftp = new SftpPanel(() => Owner, dir => ActivePane?.View.Send(" cd " + Quote(dir) + "\r"));
        _sftp.FollowEnabled += () =>
        {
            if (ActivePane is { Connection: not null, Open: true } p && p.View.Directory == null) p.View.Send(ShellIntegration);
        };
        _sftp.FavoritesChanged += ScheduleSave;
        _monitor = new MonitorPanel(() => Owner);
        _docker = new DockerPanel(() => Owner, RunInNewTerminal);
        _snippets = new SnippetPanel(_data, () => Owner, SendToTerminal, ScheduleSave);

        var panel = new DockPanel { Margin = new Thickness(10, 8, 8, 6) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Docked(ApiUi.Icon("\uE711", "收起", () => ShowSide(-1)), Dock.Right));
        head.Children.Add(_sideTitle);
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(_sideHost);
        var border = new Border { Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "WindowBrush");
        return border;
    }

    static T Docked<T>(T element, Dock dock) where T : UIElement
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    void ToggleSide(int index) => ShowSide(_side == index ? -1 : index);

    void ShowSide(int index)
    {
        if (_side >= 0 && _sideColumn.ActualWidth > 0) _data.PanelWidth = _sideColumn.ActualWidth;
        _side = index;
        foreach (var (i, button, mark) in _railButtons)
        {
            mark.Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
            if (i == index) button.Foreground = Brand;
            else button.SetResourceReference(Control.ForegroundProperty, "SecondaryTextBrush");
        }
        if (index < 0)
        {
            _sideColumn.MinWidth = 0;
            _sideColumn.Width = new GridLength(0);
            _sideSplitter.Visibility = Visibility.Collapsed;
            _sideHost.Content = null;
        }
        else
        {
            _sideColumn.MinWidth = 220;
            _sideColumn.Width = new GridLength(Math.Max(260, _data.PanelWidth));
            _sideSplitter.Visibility = Visibility.Visible;
            _sideTitle.Text = SideNames[index];
            if (index is SideSnippets or SideHistory) _snippets.ShowTab(index == SideHistory);
            _sideHost.Content = index switch { SideHosts => _hosts, SideSftp => _sftp, SideMonitor => _monitor, SideDocker => _docker, SideTransfers => _transfersPanel, _ => _snippets };
        }
        BindSidePanel();
    }

    void BindSidePanel()
    {
        var pane = ActivePane;
        var connection = pane?.Connection is { IsConnected: true } c ? c : null;
        _snippets.SetHistoryKey(pane?.HistoryKey ?? "local");
        _monitor.Bind(_side == SideMonitor ? connection : null);
        _docker.Bind(_side == SideDocker ? connection : null);
        _sftp.Favorites = pane?.Host?.Favorites;
        if (_side == SideSftp) { _sftp.ServerName = pane?.Host?.Title ?? ""; _sftp.Bind(connection, pane?.View.Directory); }
    }

    void OnDirectoryChanged(Pane pane, string dir)
    {
        pane.Directory = dir;
        if (_side == SideSftp && pane.Connection != null) _sftp.Follow(dir);
    }

    void OnHistoryChanged()
    {
        if (_side is SideSnippets or SideHistory) _snippets.Refresh();
    }

    void SaveSnippet(string text) => _snippets.Edit(null, text.Trim());

    /// <summary>Sends text to the active pane, or every open pane of its tab.</summary>
    void SendToTerminal(string text, bool run, bool all)
    {
        var pane = ActivePane;
        if (pane == null) { Ui.SetStatus(_status, "先打开一个终端", true); return; }
        var body = text.Replace("\r\n", "\n").Replace("\n", "\r") + (run ? "\r" : "");
        foreach (var p in all ? pane.Tab.Panes : new List<Pane> { pane })
            if (p.Open) p.View.Send(body);
        pane.View.FocusTerminal();
    }

    // ---------- tunnels ----------

    sealed class ActiveTunnel
    {
        public TunnelSpec Spec = null!;
        public ForwardedPort Port = null!;
        public string? Error;
    }

    readonly Dictionary<Session, List<ActiveTunnel>> _tunnels = new();

    int ActiveTunnelCount(Session s) => _tunnels.TryGetValue(s, out var l) ? l.Count(t => t.Port.IsStarted) : 0;

    void StartAutoTunnels(Session s, Pane pane)
    {
        if (s.Host is not { IsSsh: true }) return;
        foreach (var spec in s.Host.Tunnels.Where(t => t.AutoStart))
        {
            var error = StartTunnel(s, spec);
            if (error != null) pane.View.WriteText($"\x1b[33m端口转发 {spec.Describe} 启动失败：{error}\x1b[0m\r\n");
        }
    }

    /// <summary>Returns an error message, or null when the tunnel is running.</summary>
    string? StartTunnel(Session s, TunnelSpec spec)
    {
        if (s.Connection is not { IsConnected: true } connection) return "没有连接";
        if (!_tunnels.TryGetValue(s, out var list)) _tunnels[s] = list = new List<ActiveTunnel>();
        if (list.Any(t => t.Spec.Id == spec.Id && t.Port.IsStarted)) return null;
        list.RemoveAll(t => t.Spec.Id == spec.Id);
        ForwardedPort port = spec.Kind switch
        {
            "D" => new ForwardedPortDynamic(spec.BindHost, (uint)spec.BindPort),
            "R" => new ForwardedPortRemote(spec.BindHost, (uint)spec.BindPort, spec.TargetHost, (uint)spec.TargetPort),
            _ => new ForwardedPortLocal(spec.BindHost, (uint)spec.BindPort, spec.TargetHost, (uint)spec.TargetPort),
        };
        var active = new ActiveTunnel { Spec = spec, Port = port };
        port.Exception += (_, e) => { active.Error = e.Exception.Message; Log.Info("Tunnel " + spec.Describe + ": " + e.Exception.Message); };
        try
        {
            connection.Client.AddForwardedPort(port);
            port.Start();
            list.Add(active);
            UpdateStatus();
            return null;
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
        {
            try { connection.Client.RemoveForwardedPort(port); } catch (Exception inner) when (inner is InvalidOperationException or Renci.SshNet.Common.SshException) { }
            port.Dispose();
            return ex.Message;
        }
    }

    void StopTunnel(Session s, ActiveTunnel t)
    {
        try
        {
            if (t.Port.IsStarted) t.Port.Stop();
            s.Connection?.Client.RemoveForwardedPort(t.Port);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Renci.SshNet.Common.SshException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        t.Port.Dispose();
        if (_tunnels.TryGetValue(s, out var list)) list.Remove(t);
        UpdateStatus();
    }

    void StopTunnels(Session s)
    {
        if (!_tunnels.TryGetValue(s, out var list)) return;
        foreach (var t in list.ToList()) StopTunnel(s, t);
        _tunnels.Remove(s);
    }

    void ShowTunnels()
    {
        var pane = ActivePane;
        if (pane?.Host is not { IsSsh: true }) { Ui.SetStatus(_status, "端口转发需要先选中一个 SSH 会话", true); return; }
        var host = pane.Host;
        var session = pane.Tab.Session;
        var list = new ListView { BorderThickness = new Thickness(0) };
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "状态", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("Item1") });
        grid.Columns.Add(new GridViewColumn { Header = "转发", Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding("Item2") });
        grid.Columns.Add(new GridViewColumn { Header = "自动", Width = 50, DisplayMemberBinding = new System.Windows.Data.Binding("Item3") });
        list.View = grid;
        var status = Ui.Status();
        TunnelSpec? Selected() => list.SelectedItem is ValueTuple<string, string, string, TunnelSpec> row ? row.Item4 : null;
        ActiveTunnel? Running(TunnelSpec s) => _tunnels.TryGetValue(session, out var l) ? l.FirstOrDefault(t => t.Spec.Id == s.Id && t.Port.IsStarted) : null;
        void Refresh()
        {
            var selected = Selected();
            var rows = host.Tunnels.Select(s => (Running(s) != null ? "运行中" : "已停止", (s.Name.Length > 0 ? s.Name + "：" : "") + s.Describe, s.AutoStart ? "是" : "", s)).ToList();
            list.ItemsSource = rows;
            if (selected != null) list.SelectedItem = rows.FirstOrDefault(r => r.Item4 == selected);
            if (rows.Count == 0) Ui.SetStatus(status, "还没有转发规则。点「添加」新建：本地转发把本机端口接到服务器能访问的地址，远程转发反过来，动态转发是一个 SOCKS5 代理。");
        }
        void Toggle()
        {
            if (Selected() is not { } s) return;
            if (Running(s) is { } t) { StopTunnel(session, t); Ui.SetStatus(status, "已停止 " + s.Describe); }
            else
            {
                var error = StartTunnel(session, s);
                Ui.SetStatus(status, error == null ? "已启动 " + s.Describe : "启动失败：" + error, error != null);
            }
            Refresh();
        }
        var window = (Window)null!;
        var buttons = Ui.Row(
            Ui.Button("启动 / 停止", Toggle, accent: true),
            Ui.Button("添加…", () =>
            {
                if (TunnelList.EditSpec(window, new TunnelSpec()) is not { } s) return;
                host.Tunnels.Add(s);
                ScheduleSave();
                Refresh();
            }),
            Ui.Button("编辑…", () =>
            {
                if (Selected() is not { } s || TunnelList.EditSpec(window, s) is not { } edited) return;
                if (Running(s) is { } t) StopTunnel(session, t);
                host.Tunnels[host.Tunnels.IndexOf(s)] = edited;
                ScheduleSave();
                Refresh();
            }),
            Ui.Button("删除", () =>
            {
                if (Selected() is not { } s) return;
                if (Running(s) is { } t) StopTunnel(session, t);
                host.Tunnels.Remove(s);
                ScheduleSave();
                Refresh();
            }));
        list.MouseDoubleClick += (_, _) => Toggle();
        var dock = new DockPanel();
        dock.Children.Add(Docked(new TextBlock { Text = $"{host.Title} 的端口转发（规则保存在主机设置里，勾选「自动」的会在连接后自动启动）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        dock.Children.Add(Docked(status, Dock.Bottom));
        dock.Children.Add(Docked(buttons, Dock.Bottom));
        dock.Children.Add(list);
        window = ApiDialogs.Tool(Owner, "端口转发 - " + host.Title, dock, 560, 380);
        Refresh();
        window.ShowDialog();
        RefreshTree();
    }

    // ---------- AI ----------

    readonly Dictionary<SshConnection, string> _systemInfo = new();

    async Task<string> EnvironmentOf(Pane? pane)
    {
        if (pane?.Connection is { IsConnected: true } c)
        {
            if (!_systemInfo.TryGetValue(c, out var info))
            {
                try { info = await Task.Run(c.SystemInfo); }
                catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or ObjectDisposedException) { info = ""; }
                _systemInfo[c] = info;
            }
            return "远程 Linux/Unix 服务器（SSH）" + (info.Length > 0 ? "：\n" + info : "");
        }
        return "本机 Windows，shell 是 " + (pane?.Shell?.Name ?? "PowerShell");
    }

    bool AiReady()
    {
        if (_ai != null) return true;
        Ui.SetStatus(_status, "AI 助手没有开启，请先在 AI 设置里配置", true);
        return false;
    }

    static string StripFence(string text)
    {
        var m = Regex.Match(text, "```[a-zA-Z0-9_-]*\\s*\\n([\\s\\S]*?)```");
        return (m.Success ? m.Groups[1].Value : text).Trim();
    }

    void AiGenerate()
    {
        if (!AiReady()) return;
        var pane = ActivePane;
        var ask = Ui.Area(wrap: true);
        ask.Height = 70;
        var result = Ui.Area();
        result.Height = 110;
        result.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ApiUi.Res("HintTextBrush"), Margin = new Thickness(0, 6, 0, 0) };
        var status = Ui.Status();
        var window = (Window)null!;
        async void Generate()
        {
            var question = ask.Text.Trim();
            if (question.Length == 0) return;
            Ui.SetStatus(status, "正在生成…");
            try
            {
                var env = await EnvironmentOf(pane);
                var prompt = "你是终端命令助手。根据用户的描述写出一条（必要时用 && 连接或多行）可以直接执行的命令。\n" +
                             "运行环境：" + env + "\n" + (pane?.View.Directory is { } d ? "当前目录：" + d + "\n" : "") +
                             "只输出一个代码块放命令，代码块后面用一两句中文说明命令做什么；如果命令有风险（删除、覆盖、重启等），在说明里用「注意：」开头提醒。\n\n用户的描述：" + question;
                var answer = await _ai!(prompt);
                result.Text = StripFence(answer);
                var fence = answer.LastIndexOf("```", StringComparison.Ordinal);
                note.Text = fence >= 0 ? answer.Substring(fence + 3).Trim() : "";
                Ui.SetStatus(status, "请检查命令后再执行");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                Ui.SetStatus(status, "生成失败：" + ex.Message, true);
            }
        }
        var body = new DockPanel();
        body.Children.Add(Docked(new TextBlock { Text = "用一句话描述你想做什么，例如「找出当前目录下最大的 10 个文件」。生成的命令不会自动执行。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }, Dock.Top));
        body.Children.Add(Docked(ask, Dock.Top));
        body.Children.Add(Docked(Ui.Row(Ui.Button("生成", Generate, accent: true)), Dock.Top));
        body.Children.Add(Docked(status, Dock.Bottom));
        body.Children.Add(Docked(Ui.Row(
            Ui.Button("插入到终端", () => { if (result.Text.Trim().Length > 0) { SendToTerminal(result.Text.Trim(), false, false); window.Close(); } }),
            Ui.Button("执行", () => { if (result.Text.Trim().Length > 0) { SendToTerminal(result.Text.Trim(), true, false); window.Close(); } }),
            Ui.Button("存为片段…", () => { if (result.Text.Trim().Length > 0) SaveSnippet(result.Text); }),
            Ui.CopyButton(() => result.Text, "复制")), Dock.Bottom));
        body.Children.Add(Docked(note, Dock.Bottom));
        var label = new TextBlock { Text = "命令（可以修改）：", Margin = new Thickness(0, 8, 0, 4) };
        body.Children.Add(Docked(label, Dock.Top));
        body.Children.Add(result);
        ask.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == 0) { e.Handled = true; Generate(); } };
        window = ApiDialogs.Tool(Owner, "AI 生成命令", body, 580, 440);
        window.Loaded += (_, _) => ask.Focus();
        window.ShowDialog();
    }

    async void AiExplain(Pane pane, string selection)
    {
        if (!AiReady()) return;
        var text = selection.Trim().Length > 0 ? selection : await pane.View.GetTextAsync(80);
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 12000) text = text.Substring(text.Length - 12000);
        Ui.SetStatus(_status, "AI 正在分析…");
        try
        {
            var env = await EnvironmentOf(pane);
            var answer = await _ai!("下面是终端里的一段内容（运行环境：" + env + "）。请用中文解释它是什么意思；如果里面有报错，说明原因并给出修复方法和可以执行的命令。\n\n```\n" + text + "\n```");
            Ui.SetStatus(_status, "");
            UpdateStatus();
            ApiDialogs.ShowText(Owner, "AI 解释", answer.Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            Ui.SetStatus(_status, "AI 解释失败：" + ex.Message, true);
        }
    }
}
