using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>Docker containers and images on the connected server, read with the docker CLI over an exec channel.</summary>
sealed class DockerPanel : DockPanel
{
    public sealed class Container
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        public string State { get; set; } = "";
        public string Status { get; set; } = "";
        public string Ports { get; set; } = "";
        public string Cpu { get; set; } = "";
        public string Memory { get; set; } = "";
        public bool Running => State == "running";
        public string Mark => Running ? "●" : State == "paused" ? "‖" : "○";
    }

    public sealed class DockerImage
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Size { get; set; } = "";
        public string Created { get; set; } = "";
    }

    readonly Func<Window?> _owner;
    /// <summary>Opens a new terminal on the same connection and runs the command in it (title, command).</summary>
    readonly Action<string, string> _runInTerminal;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly TextBlock _status = Ui.Status();
    readonly ListView _containers = new() { BorderThickness = new Thickness(0) };
    readonly ListView _images = new() { BorderThickness = new Thickness(0) };
    readonly CheckBox _all = new() { Content = "显示已停止的", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    SshConnection? _connection;
    string _docker = "docker";
    bool _busy;

    public DockerPanel(Func<Window?> owner, Action<string, string> runInTerminal)
    {
        _owner = owner;
        _runInTerminal = runInTerminal;
        _timer.Tick += async (_, _) => await RefreshAsync(quiet: true);
        IsVisibleChanged += (_, _) => Restart();
        _all.Click += async (_, _) => await RefreshAsync(quiet: false);

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(Docked(ApiUi.Icon("", "刷新", () => _ = RefreshAsync(quiet: false)), Dock.Right));
        bar.Children.Add(Docked(ApiUi.Icon("", "拉取镜像", Pull), Dock.Right));
        bar.Children.Add(_all);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        var cg = new GridView();
        cg.Columns.Add(Col("", nameof(Container.Mark), 22));
        cg.Columns.Add(Col("名称", nameof(Container.Name), 120));
        cg.Columns.Add(Col("镜像", nameof(Container.Image), 120));
        cg.Columns.Add(Col("状态", nameof(Container.Status), 110));
        cg.Columns.Add(Col("CPU", nameof(Container.Cpu), 55));
        cg.Columns.Add(Col("内存", nameof(Container.Memory), 110));
        cg.Columns.Add(Col("端口", nameof(Container.Ports), 160));
        _containers.View = cg;
        _containers.MouseDoubleClick += (_, _) => { if (_containers.SelectedItem is Container c && c.Running) Exec(c); };
        _containers.ContextMenuOpening += (_, e) => { if (_containers.SelectedItem is not Container) e.Handled = true; else _containers.ContextMenu = ContainerMenu((Container)_containers.SelectedItem); };
        _containers.ContextMenu = new ContextMenu();

        var ig = new GridView();
        ig.Columns.Add(Col("镜像", nameof(DockerImage.Name), 200));
        ig.Columns.Add(Col("ID", nameof(DockerImage.Id), 100));
        ig.Columns.Add(Col("大小", nameof(DockerImage.Size), 70));
        ig.Columns.Add(Col("创建", nameof(DockerImage.Created), 100));
        _images.View = ig;
        _images.ContextMenuOpening += (_, e) => { if (_images.SelectedItem is not DockerImage) e.Handled = true; else _images.ContextMenu = ImageMenu((DockerImage)_images.SelectedItem); };
        _images.ContextMenu = new ContextMenu();

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "容器", Content = _containers });
        tabs.Items.Add(new TabItem { Header = "镜像", Content = _images });
        Children.Add(tabs);
        Ui.SetStatus(_status, "没有连接。选中一个 SSH 标签页后这里会列出服务器上的 Docker 容器。");
    }

    static T Docked<T>(T element, Dock dock) where T : UIElement { DockPanel.SetDock(element, dock); return element; }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

    public void Bind(SshConnection? connection)
    {
        if (connection == _connection) return;
        _connection = connection;
        _docker = "docker";
        _containers.ItemsSource = null;
        _images.ItemsSource = null;
        if (connection == null) Ui.SetStatus(_status, "没有连接。选中一个 SSH 标签页后这里会列出服务器上的 Docker 容器。");
        Restart();
    }

    void Restart()
    {
        _timer.Stop();
        if (IsVisible && _connection != null) { _timer.Start(); _ = RefreshAsync(quiet: false); }
    }

    /// <summary>Runs docker with the given arguments; falls back to sudo -n when the socket needs root.</summary>
    string Docker(SshConnection c, string args, int timeout = 20)
    {
        var output = c.Run(_docker + " " + args + " 2>&1", timeout);
        if (_docker == "docker" && output.Contains("permission denied") && output.Contains("docker"))
        {
            var retry = c.Run("sudo -n docker " + args + " 2>&1", timeout);
            if (!retry.Contains("a password is required") && !retry.Contains("sudo:")) { _docker = "sudo -n docker"; return retry; }
        }
        return output;
    }

    async Task RefreshAsync(bool quiet)
    {
        var c = _connection;
        if (_busy || c == null) return;
        if (!c.IsConnected) { Ui.SetStatus(_status, "连接已断开"); return; }
        _busy = true;
        if (!quiet) Ui.SetStatus(_status, "正在读取…");
        try
        {
            var all = _all.IsChecked == true;
            var (ps, images, stats) = await Task.Run(() =>
            {
                var p = Docker(c, "ps " + (all ? "-a " : "") + "--no-trunc --format '{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}\t{{.Ports}}'");
                if (IsMissing(p)) return (p, "", "");
                var i = Docker(c, "images --format '{{.ID}}\t{{.Repository}}:{{.Tag}}\t{{.Size}}\t{{.CreatedSince}}'");
                var s = Docker(c, "stats --no-stream --no-trunc --format '{{.ID}}\t{{.CPUPerc}}\t{{.MemUsage}}'", 30);
                return (p, i, s);
            });
            if (c != _connection) return;
            if (IsMissing(ps)) { Ui.SetStatus(_status, "这台服务器上没有 docker 命令", true); _timer.Stop(); return; }
            if (ps.Contains("permission denied")) { Ui.SetStatus(_status, "没有权限访问 Docker：把当前用户加入 docker 组，或者允许免密 sudo", true); _timer.Stop(); return; }
            if (ps.StartsWith("Cannot connect") || ps.StartsWith("error", StringComparison.OrdinalIgnoreCase)) { Ui.SetStatus(_status, ps.Trim(), true); return; }

            var usage = Rows(stats).Where(r => r.Length >= 3).ToDictionary(r => r[0], r => (r[1], r[2]));
            var containers = Rows(ps).Where(r => r.Length >= 5).Select(r =>
            {
                usage.TryGetValue(r[0], out var u);
                return new Container
                {
                    Id = r[0].Length > 12 ? r[0].Substring(0, 12) : r[0], Name = r[1], Image = r[2].StartsWith("sha256:") && r[2].Length > 19 ? r[2].Substring(7, 12) : r[2], State = r[3], Status = r[4],
                    Ports = r.Length > 5 ? Ports(r[5]) : "", Cpu = u.Item1 ?? "", Memory = u.Item2 ?? "",
                };
            }).OrderBy(x => x.Running ? 0 : 1).ThenBy(x => x.Name).ToList();
            var imageList = Rows(images).Where(r => r.Length >= 4).Select(r => new DockerImage { Id = r[0].Replace("sha256:", "").Substring(0, Math.Min(12, r[0].Replace("sha256:", "").Length)), Name = r[1], Size = r[2], Created = r[3] }).ToList();

            var selected = (_containers.SelectedItem as Container)?.Id;
            _containers.ItemsSource = containers;
            if (selected != null) _containers.SelectedItem = containers.FirstOrDefault(x => x.Id == selected);
            var selectedImage = (_images.SelectedItem as DockerImage)?.Id;
            _images.ItemsSource = imageList;
            if (selectedImage != null) _images.SelectedItem = imageList.FirstOrDefault(x => x.Id == selectedImage);
            Ui.SetStatus(_status, $"{containers.Count(x => x.Running)} 个运行中 / {containers.Count} 个容器 · {imageList.Count} 个镜像 · {DateTime.Now:HH:mm:ss}" + (_docker != "docker" ? " · 通过 sudo" : ""));
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or TimeoutException)
        {
            if (c == _connection) Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
        finally { _busy = false; }
    }

    static bool IsMissing(string output) => output.Contains("command not found") || output.Contains("docker: not found");

    static IEnumerable<string[]> Rows(string text) =>
        text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && l.Contains('\t')).Select(l => l.Split('\t'));

    /// <summary>"0.0.0.0:8080->80/tcp, :::8080->80/tcp" shortened to "8080→80".</summary>
    static string Ports(string ports) =>
        string.Join(", ", ports.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Replace("0.0.0.0:", "").Replace(":::", "").Replace("[::]:", "").Replace("->", "→").Replace("/tcp", ""))
            .Distinct());

    ContextMenu ContainerMenu(Container c)
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("进入容器（新终端）", () => Exec(c), c.Running);
        Item("查看日志（新终端）", () => _runInTerminal("日志 " + c.Name, $"{_docker} logs -f --tail 300 {c.Id}"));
        Item("查看详情（新终端）", () => _runInTerminal("详情 " + c.Name, $"{_docker} inspect {c.Id} | less"));
        menu.Items.Add(new Separator());
        Item("启动", () => Act(c, "start"), !c.Running);
        Item("停止", () => Act(c, "stop"), c.Running);
        Item("重启", () => Act(c, "restart"));
        Item(c.State == "paused" ? "恢复" : "暂停", () => Act(c, c.State == "paused" ? "unpause" : "pause"), c.Running || c.State == "paused");
        menu.Items.Add(new Separator());
        Item("删除容器…", () => Act(c, "rm", confirm: $"删除容器 {c.Name} 吗？" + (c.Running ? "它正在运行，会被强制停止。" : "")));
        Item("复制 ID", () => Copy(c.Id));
        Item("复制名称", () => Copy(c.Name));
        return menu;
    }

    ContextMenu ImageMenu(DockerImage i)
    {
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("用这个镜像运行（新终端）", () => _runInTerminal("运行 " + i.Name, $"{_docker} run --rm -it {(i.Name.EndsWith(":<none>") ? i.Id : i.Name)} sh"));
        Item("查看构建历史（新终端）", () => _runInTerminal("历史 " + i.Name, $"{_docker} history {i.Id} | less"));
        Item("删除镜像…", async () =>
        {
            if (!ApiDialogs.Confirm(_owner(), $"删除镜像 {i.Name} 吗？")) return;
            await Command($"rmi {i.Id}", "已删除镜像 " + i.Name);
        });
        Item("复制名称", () => Copy(i.Name));
        return menu;
    }

    static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }

    void Exec(Container c) =>
        _runInTerminal(c.Name, $"{_docker} exec -it {c.Id} sh -c 'if command -v bash >/dev/null; then exec bash; else exec sh; fi'");

    void Pull()
    {
        if (_connection == null) return;
        var name = TerminalDialogs.Ask(_owner(), "拉取镜像", "镜像名称，例如 nginx:latest", false);
        if (string.IsNullOrWhiteSpace(name)) return;
        _runInTerminal("拉取 " + name!.Trim(), $"{_docker} pull {name.Trim()}");
    }

    async void Act(Container c, string verb, string? confirm = null)
    {
        if (confirm != null && !ApiDialogs.Confirm(_owner(), confirm)) return;
        await Command(verb + (verb == "rm" ? " -f " : " ") + c.Id, $"{c.Name}：{verb} 完成");
    }

    async Task Command(string args, string done)
    {
        var c = _connection;
        if (c == null) return;
        Ui.SetStatus(_status, "正在执行 docker " + args.Split(' ')[0] + "…");
        try
        {
            var output = await Task.Run(() => Docker(c, args, 60));
            var failed = output.Contains("Error") || output.Contains("error");
            Ui.SetStatus(_status, failed ? output.Trim() : done, failed);
            await RefreshAsync(quiet: true);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or TimeoutException)
        {
            Ui.SetStatus(_status, "执行失败：" + ex.Message, true);
        }
    }
}
