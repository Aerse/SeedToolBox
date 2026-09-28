using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>Numbers read from one round of /proc and friends.</summary>
sealed class ServerSample
{
    public long CpuBusy, CpuTotal;
    public long MemTotal, MemAvailable, SwapTotal, SwapFree;
    public long RxBytes, TxBytes;
    public string Load = "", Uptime = "";
    public List<(string Mount, string Size, string Used, string Percent)> Disks = new();
    public List<(int Pid, string User, string Cpu, string Mem, string Command)> Processes = new();

    public const string Command =
        "head -1 /proc/stat; echo ---; cat /proc/meminfo; echo ---; cat /proc/net/dev; echo ---; " +
        "df -hP -x tmpfs -x devtmpfs -x overlay -x squashfs 2>/dev/null; echo ---; " +
        "ps -eo pid,user,pcpu,pmem,comm --sort=-pcpu 2>/dev/null | head -40; echo ---; cat /proc/loadavg; echo ---; cat /proc/uptime";

    public static ServerSample Parse(string text)
    {
        var s = new ServerSample();
        var parts = text.Replace("\r", "").Split(new[] { "\n---\n" }, StringSplitOptions.None);
        string Part(int i) => i < parts.Length ? parts[i] : "";
        static long L(string v) => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        var cpu = Part(0).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (cpu.Length > 4 && cpu[0] == "cpu")
        {
            var values = cpu.Skip(1).Take(8).Select(L).ToArray();
            s.CpuTotal = values.Sum();
            s.CpuBusy = s.CpuTotal - values[3] - (values.Length > 4 ? values[4] : 0);
        }
        foreach (var line in Part(1).Split('\n'))
        {
            var kv = line.Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (kv.Length < 2) continue;
            var kb = L(kv[1]) * 1024;
            switch (kv[0])
            {
                case "MemTotal": s.MemTotal = kb; break;
                case "MemAvailable": s.MemAvailable = kb; break;
                case "SwapTotal": s.SwapTotal = kb; break;
                case "SwapFree": s.SwapFree = kb; break;
            }
        }
        foreach (var line in Part(2).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line.Substring(0, colon).Trim();
            if (name == "lo") continue;
            var f = line.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            s.RxBytes += L(f[0]);
            s.TxBytes += L(f[8]);
        }
        foreach (var line in Part(3).Split('\n').Skip(1))
        {
            var f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 6) s.Disks.Add((string.Join(" ", f.Skip(5)), f[1], f[2], f[4]));
        }
        foreach (var line in Part(4).Split('\n').Skip(1))
        {
            var f = line.Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 5 && int.TryParse(f[0], out var pid)) s.Processes.Add((pid, f[1], f[2], f[3], f[4].Trim()));
        }
        var load = Part(5).Split(' ');
        if (load.Length >= 3) s.Load = string.Join(" ", load.Take(3));
        if (double.TryParse(Part(6).Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var up))
        {
            var t = TimeSpan.FromSeconds(up);
            s.Uptime = t.Days > 0 ? $"{t.Days} 天 {t.Hours} 小时" : $"{t.Hours} 小时 {t.Minutes} 分";
        }
        return s;
    }
}

/// <summary>Live CPU, memory, network, disks and processes of the active SSH host.</summary>
sealed class MonitorPanel : DockPanel
{
    const int Points = 60;
    readonly Func<Window?> _owner;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly TextBlock _status = Ui.Status();
    readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    readonly Chart _cpu = new("CPU", Color.FromRgb(0x3B, 0x8E, 0xEA), percent: true);
    readonly Chart _mem = new("内存", Color.FromRgb(0x2E, 0xCC, 0x71), percent: true);
    readonly Chart _rx = new("下行", Color.FromRgb(0xE6, 0x7E, 0x22), percent: false);
    readonly Chart _tx = new("上行", Color.FromRgb(0x9B, 0x59, 0xB6), percent: false);
    readonly ListView _disks = new() { BorderThickness = new Thickness(0), MaxHeight = 140 };
    readonly ListView _procs = new() { BorderThickness = new Thickness(0) };
    SshConnection? _connection;
    ServerSample? _last;
    DateTime _lastAt;
    bool _busy;

    public MonitorPanel(Func<Window?> owner)
    {
        _owner = owner;
        _timer.Tick += async (_, _) => await PollAsync();
        IsVisibleChanged += (_, _) => Restart();

        var top = new StackPanel();
        top.Children.Add(_info);
        var charts = new UniformGrid2(2);
        charts.Add(_cpu); charts.Add(_mem); charts.Add(_rx); charts.Add(_tx);
        top.Children.Add(charts.Grid);
        top.Children.Add(new TextBlock { Text = "磁盘", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        var dg = new GridView();
        dg.Columns.Add(Col("挂载点", "Item1", 130));
        dg.Columns.Add(Col("大小", "Item2", 60));
        dg.Columns.Add(Col("已用", "Item3", 60));
        dg.Columns.Add(Col("使用率", "Item4", 60));
        _disks.View = dg;
        top.Children.Add(_disks);
        top.Children.Add(new TextBlock { Text = "进程（按 CPU 排序，右键可结束）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        var pg = new GridView();
        pg.Columns.Add(Col("PID", "Item1", 60));
        pg.Columns.Add(Col("用户", "Item2", 70));
        pg.Columns.Add(Col("CPU%", "Item3", 50));
        pg.Columns.Add(Col("内存%", "Item4", 50));
        pg.Columns.Add(Col("命令", "Item5", 120));
        _procs.View = pg;
        var menu = new ContextMenu();
        foreach (var (label, signal) in new[] { ("结束进程 (TERM)", "TERM"), ("强制结束 (KILL)", "KILL") })
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => Kill(signal);
            menu.Items.Add(mi);
        }
        _procs.ContextMenu = menu;
        Children.Add(_procs);
        Show("没有连接。选中一个 SSH 标签页后这里会显示服务器状态。");
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

    void Show(string text)
    {
        _info.Text = "";
        Ui.SetStatus(_status, text);
    }

    public void Bind(SshConnection? connection)
    {
        if (connection == _connection) return;
        _connection = connection;
        _last = null;
        foreach (var c in new[] { _cpu, _mem, _rx, _tx }) c.Reset();
        _disks.ItemsSource = null;
        _procs.ItemsSource = null;
        if (connection == null) Show("没有连接。选中一个 SSH 标签页后这里会显示服务器状态。");
        else { _info.Text = connection.Host.Title; Ui.SetStatus(_status, "正在读取…"); }
        Restart();
    }

    void Restart()
    {
        _timer.Stop();
        if (IsVisible && _connection != null) { _timer.Start(); _ = PollAsync(); }
    }

    async Task PollAsync()
    {
        var connection = _connection;
        if (_busy || connection == null) return;
        if (!connection.IsConnected) { Show("连接已断开"); return; }
        _busy = true;
        try
        {
            var text = await Task.Run(() => connection.Run(ServerSample.Command, 8));
            if (connection != _connection) return;
            var s = ServerSample.Parse(text);
            var now = DateTime.UtcNow;
            if (_last != null && s.CpuTotal > _last.CpuTotal)
                _cpu.Add(100.0 * (s.CpuBusy - _last.CpuBusy) / (s.CpuTotal - _last.CpuTotal));
            if (s.MemTotal > 0) _mem.Add(100.0 * (s.MemTotal - s.MemAvailable) / s.MemTotal, $"{Ui.FormatSize(s.MemTotal - s.MemAvailable)} / {Ui.FormatSize(s.MemTotal)}");
            if (_last != null)
            {
                var secs = Math.Max(0.5, (now - _lastAt).TotalSeconds);
                _rx.Add(Math.Max(0, (s.RxBytes - _last.RxBytes) / secs));
                _tx.Add(Math.Max(0, (s.TxBytes - _last.TxBytes) / secs));
            }
            _last = s;
            _lastAt = now;
            _info.Text = $"{connection.Host.Title}    负载 {s.Load}    运行 {s.Uptime}" + (s.SwapTotal > 0 ? $"    交换 {Ui.FormatSize(s.SwapTotal - s.SwapFree)} / {Ui.FormatSize(s.SwapTotal)}" : "");
            _disks.ItemsSource = s.Disks;
            var selected = _procs.SelectedItem;
            _procs.ItemsSource = s.Processes;
            if (selected != null) _procs.SelectedItem = s.Processes.FirstOrDefault(p => p.Pid == ((ValueTuple<int, string, string, string, string>)selected).Item1);
            Ui.SetStatus(_status, $"每 2 秒刷新 · {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or TimeoutException)
        {
            if (connection == _connection) Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
        finally { _busy = false; }
    }

    async void Kill(string signal)
    {
        if (_procs.SelectedItem is not ValueTuple<int, string, string, string, string> p || _connection is not { } c) return;
        if (!ApiDialogs.Confirm(_owner(), $"向进程 {p.Item1}（{p.Item5}）发送 {signal} 信号吗？")) return;
        try
        {
            var result = await Task.Run(() => c.Run($"kill -{signal} {p.Item1} 2>&1", 5));
            Ui.SetStatus(_status, result.Trim().Length > 0 ? result.Trim() : $"已向 {p.Item1} 发送 {signal}", result.Trim().Length > 0);
            await PollAsync();
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or InvalidOperationException or ObjectDisposedException)
        {
            Ui.SetStatus(_status, "结束进程失败：" + ex.Message, true);
        }
    }

    /// <summary>Two-column grid that fills row by row.</summary>
    sealed class UniformGrid2
    {
        public readonly Grid Grid = new();
        readonly int _columns;
        int _count;
        public UniformGrid2(int columns) { _columns = columns; for (var i = 0; i < columns; i++) Grid.ColumnDefinitions.Add(new ColumnDefinition()); }
        public void Add(FrameworkElement e)
        {
            if (_count % _columns == 0) Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, _count / _columns);
            Grid.SetColumn(e, _count % _columns);
            e.Margin = new Thickness(_count % _columns == 0 ? 0 : 3, 3, _count % _columns == 0 ? 3 : 0, 3);
            Grid.Children.Add(e);
            _count++;
        }
    }

    /// <summary>A small sparkline with the latest value as a caption.</summary>
    sealed class Chart : Border
    {
        readonly string _name;
        readonly bool _percent;
        readonly List<double> _values = new();
        readonly Polyline _line;
        readonly Polygon _fill;
        readonly TextBlock _caption = new() { FontSize = 11, Margin = new Thickness(6, 3, 6, 0) };
        readonly Canvas _canvas = new() { Height = 44, ClipToBounds = true };

        public Chart(string name, Color color, bool percent)
        {
            _name = name;
            _percent = percent;
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "CardBrush");
            SetResourceReference(BorderBrushProperty, "CardBorderBrush");
            _line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.5 };
            _fill = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)) };
            _canvas.Children.Add(_fill);
            _canvas.Children.Add(_line);
            _canvas.SizeChanged += (_, _) => Draw();
            var stack = new StackPanel();
            stack.Children.Add(_caption);
            stack.Children.Add(_canvas);
            Child = stack;
            Reset();
        }

        public void Reset()
        {
            _values.Clear();
            _caption.Text = _name + "  —";
            Draw();
        }

        public void Add(double value, string? detail = null)
        {
            _values.Add(value);
            if (_values.Count > Points) _values.RemoveAt(0);
            _caption.Text = _name + "  " + (_percent ? value.ToString("0.0") + "%" : Ui.FormatSize((long)value) + "/s") + (detail != null ? "  " + detail : "");
            Draw();
        }

        void Draw()
        {
            double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
            _line.Points.Clear();
            _fill.Points.Clear();
            if (w <= 0 || _values.Count == 0) return;
            var max = _percent ? 100 : Math.Max(1024, _values.Max() * 1.2);
            var step = w / (Points - 1);
            var x0 = w - step * (_values.Count - 1);
            var points = new PointCollection();
            for (var i = 0; i < _values.Count; i++) points.Add(new Point(x0 + step * i, h - 2 - (h - 4) * Math.Min(1, _values[i] / max)));
            _line.Points = points;
            var fill = new PointCollection(points) { new Point(w, h), new Point(x0, h) };
            _fill.Points = fill;
        }
    }
}
