using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox.SystemTools;

sealed class LocalService
{
    public string Name { get; set; } = "";
    /// <summary>Run by cmd.exe, so npm, python, dotnet run and batch files all work.</summary>
    public string Command { get; set; } = "";
    public string Folder { get; set; } = "";
    public int Port { get; set; }
    public string Url { get; set; } = "";
    public bool AutoStart { get; set; }
}

/// <summary>A service started from here: its process tree and the last lines it printed.</summary>
sealed class ServiceRun
{
    const int MaxLines = 3000;
    readonly LinkedList<string> _lines = new();
    public Process Process { get; }
    public DateTime Started { get; } = DateTime.Now;
    public event Action<string>? Line;

    public ServiceRun(Process process) => Process = process;

    public bool Running { get { try { return !Process.HasExited; } catch (InvalidOperationException) { return false; } } }

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.AddLast(line);
            if (_lines.Count > MaxLines) _lines.RemoveFirst();
        }
        Line?.Invoke(line);
    }

    public List<string> Lines { get { lock (_lines) return _lines.ToList(); } }
}

/// <summary>The saved services and the ones running; runs outlive the page and are stopped when the app exits.</summary>
static class LocalServices
{
    static readonly string FilePath = Path.Combine(AppPaths.Data, "services.json");
    static readonly Dictionary<LocalService, ServiceRun> Runs = new();
    static List<LocalService>? _items;
    /// <summary>The file exists but couldn't be read (locked): saving would replace it with what little is in memory.</summary>
    static bool _unreadable;

    public static List<LocalService> Items => _items ??= Load();

    static List<LocalService> Load()
    {
        if (!File.Exists(FilePath)) return new();
        string text;
        try { text = File.ReadAllText(FilePath, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading services failed", ex);
            _unreadable = true;
            return new();
        }
        if (Parse(text) is { } items) return items;
        // Broken (e.g. cut off by a power loss): keep it aside before anything is saved over it, then try the last good copy
        try { File.Copy(FilePath, FilePath + ".broken", true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Backing up services.json failed", ex); _unreadable = true; }
        try
        {
            if (File.Exists(FilePath + ".bak") && Parse(File.ReadAllText(FilePath + ".bak", Encoding.UTF8)) is { } backup) return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Reading services.json.bak failed", ex); }
        return new();
    }

    static List<LocalService>? Parse(string text)
    {
        try { return JsonConvert.DeserializeObject<List<LocalService>>(text); }
        catch (JsonException ex) { Log.Error("services.json is broken", ex); return null; }
    }

    public static void Save()
    {
        if (_unreadable) throw new IOException("services.json 读取失败，为免覆盖原有内容，这次不保存；重启程序再试");
        Directory.CreateDirectory(AppPaths.Data);
        // Written aside and swapped in, so a crash mid-write leaves the old file (and the one before it as .bak)
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(Items, Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
        else File.Move(tmp, FilePath);
    }

    public static ServiceRun? Run(LocalService s) => Runs.TryGetValue(s, out var r) ? r : null;

    public static bool IsRunning(LocalService s) => Run(s)?.Running == true;

    public static ServiceRun Start(LocalService s)
    {
        if (Run(s) is { Running: true } running) return running;
        var folder = s.Folder.Length > 0 ? System.Environment.ExpandEnvironmentVariables(s.Folder) : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("文件夹不存在：" + folder);
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + s.Command + "\"")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        info.EnvironmentVariables["FORCE_COLOR"] = "0";
        info.EnvironmentVariables["NO_COLOR"] = "1";
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var run = new ServiceRun(process);
        void Add(string? line)
        {
            if (line != null) run.Add(System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;?]*[A-Za-z]", ""));
        }
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.Exited += (_, _) => { try { run.Add($"—— 已退出，代码 {process.ExitCode} ——"); } catch (InvalidOperationException) { } };
        run.Add($"—— {DateTime.Now:HH:mm:ss} 在 {folder} 运行：{s.Command} ——");
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Runs[s] = run;
        return run;
    }

    /// <summary>Kills the whole tree we started (cmd, then node/python under it).</summary>
    public static void Stop(LocalService s)
    {
        if (Run(s) is not { Running: true } run) return;
        KillTree(run.Process.Id);
        run.Process.WaitForExit(3000);
    }

    static void KillTree(int pid)
    {
        using var kill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F") { UseShellExecute = false, CreateNoWindow = true });
        kill?.WaitForExit(5000);
    }

    public static void StopAll()
    {
        foreach (var run in Runs.Values.Where(r => r.Running).ToList())
        {
            try { KillTree(run.Process.Id); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Error("Stopping a service failed", ex); }
        }
    }

    public static void StartAutoStart()
    {
        foreach (var s in Items.Where(s => s.AutoStart))
        {
            try { Start(s); }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException) { Log.Error("Auto-starting " + s.Name + " failed", ex); }
        }
    }

    public static HashSet<int> ListeningPorts()
    {
        try { return new HashSet<int>(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port)); }
        catch (NetworkInformationException) { return new(); }
    }
}

sealed class ServiceRow : INotifyPropertyChanged
{
    public LocalService Service { get; }
    public ServiceRow(LocalService s) => Service = s;
    public string Name => Service.Name;
    public string Command => Service.Command;
    public string PortText => Service.Port > 0 ? Service.Port.ToString() : "";
    public string State { get; private set; } = "";

    public void Update(HashSet<int> ports)
    {
        var run = LocalServices.Run(Service);
        var state = run?.Running == true
            ? "● 运行中 " + FormatUptime(DateTime.Now - run.Started) + (Service.Port > 0 && !ports.Contains(Service.Port) ? "（端口未监听）" : "")
            : Service.Port > 0 && ports.Contains(Service.Port) ? "● 端口已被占用（不是从这里启动的）" : "○ 已停止";
        if (state == State) return;
        State = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }

    static string FormatUptime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分" : t.TotalMinutes >= 1 ? $"{t.Minutes} 分" : $"{t.Seconds} 秒";

    public event PropertyChangedEventHandler? PropertyChanged;
}

sealed class DockerContainer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public string Ports { get; set; } = "";
    public bool Running => State == "running";
    public string StateText => (Running ? "● " : "○ ") + Status;
}

/// <summary>Start and stop the local dev services you use often, watch their output, and manage Docker containers.</summary>
sealed class ServicesPage : DockPanel
{
    readonly ObservableCollection<ServiceRow> _rows = new();
    readonly ListView _list = new();
    readonly TextBox _log = Ui.Area();
    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    ServiceRun? _shown;
    const int LogLines = 3000;
    /// <summary>Lines printed since the last refresh, added to the box in one go.</summary>
    readonly List<string> _newLines = new();
    /// <summary>Length of each line in the box, to cut the oldest ones without reading the whole text back.</summary>
    readonly Queue<int> _logLengths = new();
    readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly WrapPanel _toolbar = new() { Margin = new Thickness(0, 10, 0, 6) };

    readonly ObservableCollection<DockerContainer> _containers = new();
    readonly ListView _dockerList = new();
    readonly TextBox _dockerLog = Ui.Area();
    readonly TextBlock _dockerStatus = Ui.Status();
    bool _dockerBusy;

    public ServicesPage()
    {
        var header = Ui.Header("本地服务", "把常用的开发服务（npm run dev、python、dotnet run…）存起来一键启停、看输出；也能管理 Docker 容器");
        SetDock(header, Dock.Top);
        Children.Add(header);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "我的服务", Content = BuildServices() });
        var docker = new TabItem { Header = "Docker 容器", Content = BuildDocker() };
        tabs.Items.Add(docker);
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs && tabs.SelectedItem == docker && _containers.Count == 0) _ = RefreshDocker(); };
        Children.Add(tabs);
        _timer.Tick += (_, _) => UpdateStates();
        _logTimer.Tick += (_, _) => FlushLog();
        Loaded += (_, _) => { UpdateStates(); _timer.Start(); _logTimer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); _logTimer.Stop(); };
    }

    // My services

    FrameworkElement BuildServices()
    {
        var toolbar = _toolbar;
        foreach (var b in new[]
                 {
                     Ui.Button("启动", () => _ = Act(StartSelected), accent: true), Ui.Button("停止", () => _ = Act(StopSelected)), Ui.Button("重启", () => _ = Act(Restart)),
                     Ui.Button("打开网址", OpenUrl), Ui.Button("打开文件夹", OpenFolder),
                     Ui.Button("新建", () => Edit(null)), Ui.Button("编辑", () => { if (Selected is { } r) Edit(r.Service); }), Ui.Button("删除", Delete),
                 })
        {
            b.Margin = new Thickness(0, 0, 6, 4);
            toolbar.Children.Add(b);
        }

        _list.ItemsSource = _rows;
        _list.Height = 190;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        void Column(string h, string path, double width) => view.Columns.Add(new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new Binding(path) });
        Column("状态", nameof(ServiceRow.State), 230);
        Column("名称", nameof(ServiceRow.Name), 150);
        Column("端口", nameof(ServiceRow.PortText), 60);
        Column("命令", nameof(ServiceRow.Command), 380);
        _list.View = view;
        _list.SelectionChanged += (_, _) => ShowLog();
        _list.MouseDoubleClick += (_, _) => { if (Selected is { } r && _toolbar.IsEnabled) _ = Act(LocalServices.IsRunning(r.Service) ? StopSelected : StartSelected); };
        foreach (var s in LocalServices.Items) _rows.Add(new ServiceRow(s));

        _log.IsReadOnly = true;
        _log.FontFamily = Ui.Mono;
        var logActions = Ui.Row(Ui.CopyButton(() => _log.Text, "复制输出"), Ui.Button("清空", () => { _log.Clear(); _logLengths.Clear(); }));
        var panel = new DockPanel();
        SetDock(toolbar, Dock.Top);
        SetDock(_list, Dock.Top);
        SetDock(_status, Dock.Bottom);
        panel.Children.Add(toolbar);
        panel.Children.Add(_list);
        panel.Children.Add(_status);
        var log = Ui.Titled("输出", _log, logActions);
        log.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(log);
        Ui.SetStatus(_status, _rows.Count == 0 ? "点“新建”加一个服务，比如 npm run dev" : "双击启动或停止；从这里启动的服务在退出 SeedToolBox 时一起停止");
        return panel;
    }

    ServiceRow? Selected => _list.SelectedItem as ServiceRow;

    /// <summary>Runs an action on the selected service with the buttons off, since stopping waits for the process tree.</summary>
    async Task Act(Func<LocalService, Task> action)
    {
        if (Selected is not { } row) { Ui.SetStatus(_status, "先选一个服务", true); return; }
        _toolbar.IsEnabled = false;
        try { await action(row.Service); }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            Ui.SetStatus(_status, "失败：" + ex.Message, true);
        }
        finally { _toolbar.IsEnabled = true; }
        UpdateStates();
        ShowLog();
    }

    Task StartSelected(LocalService s)
    {
        LocalServices.Start(s);
        Ui.SetStatus(_status, "已启动 " + s.Name);
        return Task.CompletedTask;
    }

    async Task StopSelected(LocalService s)
    {
        if (!LocalServices.IsRunning(s))
        {
            Ui.SetStatus(_status, s.Port > 0 && LocalServices.ListeningPorts().Contains(s.Port) ? "这个端口上的程序不是从这里启动的，去“端口/网络”页结束它" : "没有在运行", true);
            return;
        }
        Ui.SetStatus(_status, "正在停止 " + s.Name + "…");
        await Task.Run(() => LocalServices.Stop(s));
        Ui.SetStatus(_status, "已停止 " + s.Name);
    }

    async Task Restart(LocalService s)
    {
        Ui.SetStatus(_status, "正在重启 " + s.Name + "…");
        await Task.Run(() => LocalServices.Stop(s));
        LocalServices.Start(s);
        Ui.SetStatus(_status, "已重启 " + s.Name);
    }

    void OpenUrl()
    {
        if (Selected is not { } row) return;
        var s = row.Service;
        var url = s.Url.Length > 0 ? s.Url : s.Port > 0 ? $"http://localhost:{s.Port}" : "";
        if (url.Length == 0) { Ui.SetStatus(_status, "这个服务没填网址或端口", true); return; }
        ProcessLauncher.Start(url);
    }

    void OpenFolder()
    {
        if (Selected is not { } row) return;
        var folder = System.Environment.ExpandEnvironmentVariables(row.Service.Folder);
        if (Directory.Exists(folder)) ProcessLauncher.Start(folder);
        else Ui.SetStatus(_status, "文件夹不存在：" + folder, true);
    }

    async void Delete()
    {
        if (Selected is not { } row) return;
        if (MessageBox.Show(Window.GetWindow(this), $"删除服务“{row.Name}”？（只删除这条记录，不删任何文件）", "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _toolbar.IsEnabled = false;
        try { await Task.Run(() => LocalServices.Stop(row.Service)); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Error("Stopping a service failed", ex); }
        finally { _toolbar.IsEnabled = true; }
        LocalServices.Items.Remove(row.Service);
        _rows.Remove(row);
        SaveAll();
    }

    void SaveAll()
    {
        try { LocalServices.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.SetStatus(_status, "保存失败：" + ex.Message, true); }
    }

    void Edit(LocalService? existing)
    {
        var s = existing ?? new LocalService();
        var name = Ui.Field(360);
        var command = Ui.Field(360);
        var folder = Ui.Field(290);
        var port = Ui.Field(100);
        var url = Ui.Field(360);
        var auto = new CheckBox { Content = "SeedToolBox 启动时自动运行", Margin = new Thickness(0, 10, 0, 0) };
        name.Text = s.Name;
        command.Text = s.Command;
        folder.Text = s.Folder;
        port.Text = s.Port > 0 ? s.Port.ToString() : "";
        url.Text = s.Url;
        auto.IsChecked = s.AutoStart;
        command.ToolTip = "比如 npm run dev、python -m http.server 8000、dotnet run、docker compose up";
        var browse = Ui.Button("浏览…", () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = folder.Text };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dialog.SelectedPath;
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        var body = new StackPanel { Margin = new Thickness(16) };
        void Add(string label, UIElement field) { body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) }); body.Children.Add(field); }
        Add("名称", name);
        Add("命令（用 cmd 运行）", command);
        Add("工作文件夹", Ui.Row(folder, browse));
        Add("端口（可选，用来判断是否在运行）", port);
        Add("网址（可选，默认 http://localhost:端口）", url);
        body.Children.Add(auto);
        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create(existing == null ? "新建服务" : "编辑服务", body, ok, DialogWindow.CancelButton());
        window.Owner = Window.GetWindow(this);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) =>
        {
            if (command.Text.Trim().Length == 0) { MessageBox.Show(window, "命令不能为空"); return; }
            if (port.Text.Trim().Length > 0 && (!int.TryParse(port.Text.Trim(), out var p) || p is < 1 or > 65535)) { MessageBox.Show(window, "端口应是 1 到 65535"); return; }
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => name.Focus();
        if (window.ShowDialog() != true) return;
        s.Command = command.Text.Trim();
        s.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : s.Command.Split(' ')[0];
        s.Folder = folder.Text.Trim();
        s.Port = int.TryParse(port.Text.Trim(), out var n) ? n : 0;
        s.Url = url.Text.Trim();
        s.AutoStart = auto.IsChecked == true;
        if (existing == null)
        {
            LocalServices.Items.Add(s);
            var row = new ServiceRow(s);
            _rows.Add(row);
            _list.SelectedItem = row;
        }
        else
        {
            var i = _list.SelectedIndex;
            _rows[i] = new ServiceRow(s);
            _list.SelectedIndex = i;
        }
        SaveAll();
        UpdateStates();
    }

    void UpdateStates()
    {
        var ports = LocalServices.ListeningPorts();
        foreach (var r in _rows) r.Update(ports);
        if (Selected is { } row && LocalServices.Run(row.Service) != _shown) ShowLog();
    }

    void ShowLog()
    {
        if (_shown != null) _shown.Line -= OnLine;
        _shown = Selected is { } row ? LocalServices.Run(row.Service) : null;
        lock (_newLines) _newLines.Clear();
        var lines = _shown?.Lines ?? new List<string>();
        _logLengths.Clear();
        foreach (var line in lines) _logLengths.Enqueue(line.Length);
        _log.Text = string.Join("\r\n", lines);
        _log.ScrollToEnd();
        if (_shown != null) _shown.Line += OnLine;
    }

    // From the process's reader threads; busy services print hundreds of lines a second
    void OnLine(string line)
    {
        lock (_newLines) _newLines.Add(line);
    }

    void FlushLog()
    {
        List<string> lines;
        lock (_newLines)
        {
            if (_newLines.Count == 0) return;
            lines = _newLines.ToList();
            _newLines.Clear();
        }
        _log.AppendText((_logLengths.Count > 0 ? "\r\n" : "") + string.Join("\r\n", lines));
        foreach (var line in lines) _logLengths.Enqueue(line.Length);
        // Over the limit: drop the oldest fifth at once rather than a line at a time
        if (_logLengths.Count > LogLines)
        {
            int cut = 0;
            while (_logLengths.Count > LogLines * 4 / 5) cut += _logLengths.Dequeue() + 2;
            _log.Text = _log.Text.Substring(Math.Min(cut, _log.Text.Length));
        }
        _log.ScrollToEnd();
    }

    // Docker

    FrameworkElement BuildDocker()
    {
        var toolbar = Ui.Row(Ui.Button("刷新", () => _ = RefreshDocker()), Ui.Button("启动", () => _ = Docker("start")), Ui.Button("停止", () => _ = Docker("stop")),
            Ui.Button("重启", () => _ = Docker("restart")), Ui.Button("查看日志", () => _ = DockerLogs()));
        toolbar.Margin = new Thickness(0, 10, 0, 6);
        _dockerList.ItemsSource = _containers;
        _dockerList.Height = 190;
        _dockerList.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        void Column(string h, string path, double width) => view.Columns.Add(new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new Binding(path) });
        Column("状态", nameof(DockerContainer.StateText), 200);
        Column("名称", nameof(DockerContainer.Name), 170);
        Column("镜像", nameof(DockerContainer.Image), 200);
        Column("端口", nameof(DockerContainer.Ports), 260);
        _dockerList.View = view;
        _dockerList.MouseDoubleClick += (_, _) => _ = DockerLogs();
        _dockerLog.IsReadOnly = true;
        _dockerLog.FontFamily = Ui.Mono;
        var panel = new DockPanel();
        SetDock(toolbar, Dock.Top);
        SetDock(_dockerList, Dock.Top);
        SetDock(_dockerStatus, Dock.Bottom);
        panel.Children.Add(toolbar);
        panel.Children.Add(_dockerList);
        panel.Children.Add(_dockerStatus);
        var log = Ui.Titled("日志（最后 300 行）", _dockerLog, Ui.CopyButton(() => _dockerLog.Text, "复制日志"));
        log.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(log);
        return panel;
    }

    /// <summary>Runs the docker CLI; the exit code and everything it printed.</summary>
    static Task<(int Code, string Output)> RunDocker(params string[] args) => Task.Run(() =>
    {
        var info = new ProcessStartInfo("docker", string.Join(" ", args.Select(Arg)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(info)!;
        // Both read in the background, so a docker that hangs with its output open still times out
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(60000))
        {
            try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            return (-1, "docker 超过 60 秒没有响应");
        }
        // Output still in the pipes after exit
        Task.WaitAll(new Task[] { err, output }, 5000);
        return (p.ExitCode, (output.IsCompleted ? output.Result : "") + (err.IsCompleted ? err.Result : ""));
    });

    static string Arg(string a) => a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_.:/{}=".Contains(c)) ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    async Task RefreshDocker()
    {
        if (_dockerBusy) return;
        _dockerBusy = true;
        Ui.SetStatus(_dockerStatus, "正在读取容器…");
        try
        {
            var (code, output) = await RunDocker("ps", "-a", "--format", "{{json .}}");
            if (code != 0) { Ui.SetStatus(_dockerStatus, "docker 出错（Docker Desktop 开着吗？）：" + output.Trim(), true); return; }
            var selected = (_dockerList.SelectedItem as DockerContainer)?.Id;
            _containers.Clear();
            foreach (var line in output.Split('\n').Where(l => l.TrimStart().StartsWith("{")))
            {
                try
                {
                    var o = JObject.Parse(line);
                    _containers.Add(new DockerContainer
                    {
                        Id = (string?)o["ID"] ?? "",
                        Name = (string?)o["Names"] ?? "",
                        Image = (string?)o["Image"] ?? "",
                        State = (string?)o["State"] ?? "",
                        Status = (string?)o["Status"] ?? "",
                        Ports = (string?)o["Ports"] ?? "",
                    });
                }
                catch (JsonException) { }
            }
            _dockerList.SelectedItem = _containers.FirstOrDefault(c => c.Id == selected);
            Ui.SetStatus(_dockerStatus, $"{_containers.Count} 个容器，运行中 {_containers.Count(c => c.Running)} 个；双击看日志");
        }
        catch (Win32Exception)
        {
            Ui.SetStatus(_dockerStatus, "没找到 docker 命令，装了 Docker Desktop 才能用", true);
        }
        finally { _dockerBusy = false; }
    }

    async Task Docker(string verb)
    {
        if (_dockerList.SelectedItem is not DockerContainer c) { Ui.SetStatus(_dockerStatus, "先选一个容器", true); return; }
        if (_dockerBusy) return;
        _dockerBusy = true;
        Ui.SetStatus(_dockerStatus, $"docker {verb} {c.Name} …");
        try
        {
            var (code, output) = await RunDocker(verb, c.Id);
            _dockerBusy = false;
            if (code != 0) { Ui.SetStatus(_dockerStatus, output.Trim(), true); return; }
            await RefreshDocker();
        }
        catch (Win32Exception ex) { Ui.SetStatus(_dockerStatus, ex.Message, true); }
        finally { _dockerBusy = false; }
    }

    async Task DockerLogs()
    {
        if (_dockerList.SelectedItem is not DockerContainer c) { Ui.SetStatus(_dockerStatus, "先选一个容器", true); return; }
        Ui.SetStatus(_dockerStatus, "读取日志…");
        try
        {
            var (_, output) = await RunDocker("logs", "--tail", "300", "--timestamps", c.Id);
            _dockerLog.Text = System.Text.RegularExpressions.Regex.Replace(output, @"\x1b\[[0-9;?]*[A-Za-z]", "");
            _dockerLog.ScrollToEnd();
            Ui.SetStatus(_dockerStatus, c.Name + " 的日志");
        }
        catch (Win32Exception ex) { Ui.SetStatus(_dockerStatus, ex.Message, true); }
    }
}
