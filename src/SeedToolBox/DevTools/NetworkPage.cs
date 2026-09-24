using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

sealed class PortEntry
{
    public string Protocol { get; set; } = "";
    public string Local { get; set; } = "";
    public int Port { get; set; }
    public string Remote { get; set; } = "";
    public string State { get; set; } = "";
    public int Pid { get; set; }
    public string Process { get; set; } = "";
    public string? ProcessPath { get; set; }
}

/// <summary>Ports and the processes holding them, network adapters, ping and TCP port tests.</summary>
sealed partial class NetworkPage : DockPanel
{
    readonly ObservableCollection<PortEntry> _ports = new();
    readonly ListView _portList = new();
    readonly TextBox _filter = Ui.Field(220);
    readonly CheckBox _listenOnly = new() { Content = "只看监听端口", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _autoRefresh = new() { Content = "自动刷新（3 秒）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly System.Windows.Threading.DispatcherTimer _portTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _refreshing;
    readonly TextBlock _portStatus = Ui.Status();
    List<PortEntry> _allPorts = new();

    readonly TextBox _adapters = Ui.Area();

    readonly TextBox _host = Ui.Field(240);
    readonly TextBox _pingCount = Ui.Field(50);
    readonly TextBox _testPort = Ui.Field(70);
    readonly TextBox _log = Ui.Area();
    bool _pinging;

    public NetworkPage()
    {
        var header = Ui.Header("端口 / 网络", "端口占用与结束进程，网卡与 IP，Ping、路由追踪、DNS 查询、端口扫描、子网计算与 HTTP 请求测试");
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "端口占用", Content = BuildPorts() });
        tabs.Items.Add(new TabItem { Header = "网卡与 IP", Content = BuildAdapters() });
        tabs.Items.Add(new TabItem { Header = "Ping / 端口测试", Content = BuildPing() });
        tabs.Items.Add(new TabItem { Header = "DNS 查询", Content = BuildDns() });
        tabs.Items.Add(new TabItem { Header = "端口扫描", Content = BuildScan() });
        tabs.Items.Add(new TabItem { Header = "子网计算", Content = BuildSubnet() });
        tabs.Items.Add(new TabItem { Header = "HTTP 请求", Content = BuildHttp() });
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(tabs);
        Loaded += (_, _) =>
        {
            if (_allPorts.Count == 0) RefreshPorts();
            if (_autoRefresh.IsChecked == true) _portTimer.Start();
        };
        Unloaded += (_, _) => _portTimer.Stop();
    }

    // Ports

    FrameworkElement BuildPorts()
    {
        _filter.ToolTip = "端口号、进程名或 PID";
        _filter.TextChanged += (_, _) => ApplyFilter();
        _listenOnly.Click += (_, _) => ApplyFilter();
        _portTimer.Tick += (_, _) => { if (!_refreshing) RefreshPorts(); };
        _autoRefresh.Click += (_, _) => { if (_autoRefresh.IsChecked == true) _portTimer.Start(); else _portTimer.Stop(); };
        var toolbar = Ui.Row(Ui.Label("筛选"), _filter, Ui.Label("", 12), _listenOnly, _autoRefresh, Ui.Button("刷新", RefreshPorts), Ui.Button("结束进程", KillSelected), Ui.Button("打开位置", () =>
        {
            if (_portList.SelectedItem is PortEntry { ProcessPath: { } path }) Launcher.ProcessLauncher.OpenLocation(path);
        }), ListTools.ExportButton(_portList, _portStatus, "端口占用.csv"));
        toolbar.Margin = new Thickness(0, 10, 0, 10);

        _portList.ItemsSource = _ports;
        _portList.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        void Column(string header, string path, double width) => view.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) });
        Column("协议", nameof(PortEntry.Protocol), 56);
        Column("本地地址", nameof(PortEntry.Local), 170);
        Column("端口", nameof(PortEntry.Port), 64);
        Column("远程地址", nameof(PortEntry.Remote), 170);
        Column("状态", nameof(PortEntry.State), 100);
        Column("PID", nameof(PortEntry.Pid), 64);
        Column("进程", nameof(PortEntry.Process), 150);
        _portList.View = view;
        ListTools.Sortable(_portList);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("结束进程", KillSelected));
        menu.Items.Add(MenuItem("打开文件位置", () => { if (_portList.SelectedItem is PortEntry { ProcessPath: { } path }) Launcher.ProcessLauncher.OpenLocation(path); }));
        menu.Items.Add(MenuItem("复制本行", () =>
        {
            if (_portList.SelectedItem is PortEntry p) ScreenToolService.CopyText($"{p.Protocol}\t{p.Local}:{p.Port}\t{p.Remote}\t{p.State}\t{p.Pid}\t{p.Process}");
        }));
        _portList.ContextMenu = menu;

        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        SetDock(toolbar, Dock.Top);
        SetDock(_portStatus, Dock.Bottom);
        panel.Children.Add(toolbar);
        panel.Children.Add(_portStatus);
        panel.Children.Add(_portList);
        return panel;
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    async void RefreshPorts()
    {
        if (_autoRefresh.IsChecked != true) Ui.SetStatus(_portStatus, "读取中…");
        _refreshing = true;
        try
        {
            _allPorts = await Task.Run(PortTable.Read);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_portStatus, "读取端口失败：" + ex.Message, true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    void ApplyFilter()
    {
        var q = _filter.Text.Trim();
        bool listen = _listenOnly.IsChecked == true;
        var list = _allPorts
            .Where(p => !listen || p.State == "LISTEN" || p.Protocol.StartsWith("UDP"))
            .Where(p => q.Length == 0 || p.Port.ToString() == q || p.Pid.ToString() == q || p.Process.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Remote.Contains(q))
            .OrderBy(p => p.Port).ThenBy(p => p.Protocol)
            .ToList();
        // Keep the selected row across refreshes
        var selected = _portList.SelectedItem as PortEntry;
        _ports.Clear();
        foreach (var p in list) _ports.Add(p);
        if (selected != null)
            _portList.SelectedItem = list.FirstOrDefault(p => p.Protocol == selected.Protocol && p.Port == selected.Port && p.Pid == selected.Pid && p.Local == selected.Local && p.Remote == selected.Remote);
        Ui.SetStatus(_portStatus, $"共 {list.Count} 条（总计 {_allPorts.Count}）  右键可结束进程；系统进程需要以管理员身份运行才能结束");
    }

    void KillSelected()
    {
        if (_portList.SelectedItem is not PortEntry entry) { Ui.SetStatus(_portStatus, "先选中一行", true); return; }
        if (entry.Pid <= 4) { Ui.SetStatus(_portStatus, "这是系统进程，不能结束", true); return; }
        if (entry.Pid == Process.GetCurrentProcess().Id) { Ui.SetStatus(_portStatus, "这是 SeedToolBox 自己", true); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"结束进程 {entry.Process}（PID {entry.Pid}）？\n未保存的数据会丢失。", "结束进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            process.Kill();
            process.WaitForExit(3000);
            Ui.SetStatus(_portStatus, $"已结束 {entry.Process}（PID {entry.Pid}）");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            Ui.SetStatus(_portStatus, "结束失败：" + (ex is Win32Exception { NativeErrorCode: 5 } ? "拒绝访问，请以管理员身份运行" : ex.Message), true);
            return;
        }
        RefreshPorts();
    }

    // Adapters

    FrameworkElement BuildAdapters()
    {
        _adapters.IsReadOnly = true;
        var toolbar = Ui.Row(Ui.Button("刷新", RefreshAdapters), Ui.Button("复制", () => { if (_adapters.Text.Length > 0) ScreenToolService.CopyText(_adapters.Text); }), Ui.Button("查询公网 IP", QueryPublicIp));
        toolbar.Margin = new Thickness(0, 10, 0, 10);
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        panel.Children.Add(_adapters);
        RefreshAdapters();
        return panel;
    }

    void RefreshAdapters()
    {
        try { _adapters.Text = Adapters.Describe(); }
        catch (NetworkInformationException ex) { _adapters.Text = "读取失败：" + ex.Message; }
    }

    async void QueryPublicIp()
    {
        var before = _adapters.Text;
        _adapters.Text = "正在查询公网 IP…\r\n\r\n" + before;
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var ip = (await client.GetStringAsync("https://api.ipify.org")).Trim();
            _adapters.Text = $"公网 IP：{ip}\r\n\r\n" + before;
        }
        catch (Exception ex)
        {
            _adapters.Text = "公网 IP 查询失败：" + ex.Message + "\r\n\r\n" + before;
        }
    }

    // Ping

    FrameworkElement BuildPing()
    {
        _host.Text = "baidu.com";
        _pingCount.Text = "4";
        _testPort.Text = "443";
        _log.IsReadOnly = true;
        var ping = Ui.Row(Ui.Label("主机"), _host, Ui.Label("", 12), Ui.Label("次数"), _pingCount, Ui.Label("", 12), Ui.Button("Ping", Ping, accent: true), Ui.Label("", 12), Ui.Label("端口"), _testPort, Ui.Label("", 8), Ui.Button("测试 TCP 端口", TestPort), Ui.Button("路由追踪", Traceroute), Ui.Button("清空", () => _log.Clear()));
        ping.Margin = new Thickness(0, 10, 0, 10);
        _host.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Ping(); };
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        SetDock(ping, Dock.Top);
        panel.Children.Add(ping);
        panel.Children.Add(_log);
        return panel;
    }

    void Log(string line)
    {
        _log.AppendText(line + "\r\n");
        _log.ScrollToEnd();
    }

    async void Ping()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0 || _pinging) return;
        if (!int.TryParse(_pingCount.Text, out var count) || count < 1 || count > 100) { Log("次数应为 1–100"); return; }
        _pinging = true;
        Log($"Ping {host}：");
        var times = new List<long>();
        int lost = 0;
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, 3000);
                    if (reply.Status == IPStatus.Success)
                    {
                        times.Add(reply.RoundtripTime);
                        Log($"  来自 {reply.Address}：时间 {reply.RoundtripTime} ms  TTL {reply.Options?.Ttl.ToString() ?? "-"}");
                    }
                    else
                    {
                        lost++;
                        Log($"  {StatusText(reply.Status)}");
                    }
                }
                catch (PingException ex)
                {
                    Log("  失败：" + (ex.InnerException?.Message ?? ex.Message));
                    lost = count - times.Count;
                    break;
                }
                if (i + 1 < count) await Task.Delay(1000);
            }
        }
        finally
        {
            _pinging = false;
        }
        var summary = $"  已发送 {count}，已接收 {times.Count}，丢失 {lost}（{lost * 100 / count}% 丢失）";
        if (times.Count > 0) summary += $"，最短 {times.Min()} ms，最长 {times.Max()} ms，平均 {times.Average():0} ms";
        Log(summary + "\r\n");
    }

    static string StatusText(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "请求超时",
        IPStatus.DestinationHostUnreachable => "目标主机不可达",
        IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
        IPStatus.TtlExpired => "TTL 过期",
        _ => status.ToString(),
    };

    async void TestPort()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) return;
        if (!int.TryParse(_testPort.Text, out var port) || port < 1 || port > 65535) { Log("端口应为 1–65535"); return; }
        var watch = Stopwatch.StartNew();
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(5000)) != connect) { Log($"TCP {host}:{port}  ✗ 超时（5 秒）"); return; }
            await connect;
            Log($"TCP {host}:{port}  ✓ 可连接，用时 {watch.ElapsedMilliseconds} ms（{client.Client.RemoteEndPoint}）");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            Log($"TCP {host}:{port}  ✗ {(ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? "连接被拒绝（端口未开放）" : ex.Message)}");
        }
    }
}

static class Adapters
{
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"计算机名：{Environment.MachineName}");
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .ThenByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0);
        foreach (var n in interfaces)
        {
            var props = n.GetIPProperties();
            sb.AppendLine();
            sb.AppendLine($"■ {n.Name}（{(n.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接")}）");
            sb.AppendLine($"  描述      {n.Description}");
            sb.AppendLine($"  类型      {n.NetworkInterfaceType}");
            var mac = n.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length > 0) sb.AppendLine($"  MAC       {string.Join("-", mac.Select(b => b.ToString("X2")))}");
            if (n.Speed > 0 && n.OperationalStatus == OperationalStatus.Up) sb.AppendLine($"  速率      {(n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbps" : $"{n.Speed / 1e6:0} Mbps")}");
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                    sb.AppendLine($"  IPv4      {a.Address}/{a.PrefixLength}");
                else if (a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    sb.AppendLine($"  IPv6      {a.Address}");
            }
            foreach (var g in props.GatewayAddresses) sb.AppendLine($"  网关      {g.Address}");
            foreach (var d in props.DnsAddresses) sb.AppendLine($"  DNS       {d}");
            try
            {
                if (props.GetIPv4Properties() is { } v4) sb.AppendLine($"  DHCP      {(v4.IsDhcpEnabled ? "是" : "否")}");
            }
            catch (NetworkInformationException) { }
        }
        return sb.ToString();
    }
}

/// <summary>TCP and UDP endpoints with their owning process, from the IP Helper API.</summary>
static class PortTable
{
    const int AF_INET = 2, AF_INET6 = 23;

    public static List<PortEntry> Read()
    {
        var entries = new List<PortEntry>();
        ReadTcp(AF_INET, entries);
        ReadTcp(AF_INET6, entries);
        ReadUdp(AF_INET, entries);
        ReadUdp(AF_INET6, entries);

        var names = new Dictionary<int, (string Name, string? Path)>();
        foreach (var e in entries)
        {
            if (!names.TryGetValue(e.Pid, out var info))
            {
                info = e.Pid switch { 0 => ("System Idle", null), 4 => ("System", null), _ => ProcessInfo(e.Pid) };
                names[e.Pid] = info;
            }
            e.Process = info.Name;
            e.ProcessPath = info.Path;
        }
        return entries;
    }

    static (string, string?) ProcessInfo(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            return (p.ProcessName, path);
        }
        catch (ArgumentException)
        {
            return ("（已退出）", null);
        }
    }

    static readonly string[] TcpStates = { "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

    static void ReadTcp(int family, List<PortEntry> entries)
    {
        // TCP_TABLE_OWNER_PID_ALL
        var buffer = Query((IntPtr b, ref int size) => GetExtendedTcpTable(b, ref size, false, family, 5, 0));
        if (buffer == null) return;
        int count = BitConverter.ToInt32(buffer, 0);
        bool v6 = family == AF_INET6;
        // MIB_TCPROW_OWNER_PID is 24 bytes; MIB_TCP6ROW_OWNER_PID is 56
        int rowSize = v6 ? 56 : 24;
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * rowSize;
            if (o + rowSize > buffer.Length) break;
            if (!v6)
            {
                int state = BitConverter.ToInt32(buffer, o);
                entries.Add(new PortEntry
                {
                    Protocol = "TCP",
                    State = state < TcpStates.Length ? TcpStates[state] : state.ToString(),
                    Local = new IPAddress(BitConverter.ToUInt32(buffer, o + 4)).ToString(),
                    Port = Port(buffer, o + 8),
                    Remote = state == 2 ? "" : $"{new IPAddress(BitConverter.ToUInt32(buffer, o + 12))}:{Port(buffer, o + 16)}",
                    Pid = BitConverter.ToInt32(buffer, o + 20),
                });
            }
            else
            {
                int state = BitConverter.ToInt32(buffer, o + 48);
                entries.Add(new PortEntry
                {
                    Protocol = "TCPv6",
                    Local = V6(buffer, o, BitConverter.ToUInt32(buffer, o + 16)),
                    Port = Port(buffer, o + 20),
                    Remote = state == 2 ? "" : $"[{V6(buffer, o + 24, BitConverter.ToUInt32(buffer, o + 40))}]:{Port(buffer, o + 44)}",
                    State = state < TcpStates.Length ? TcpStates[state] : state.ToString(),
                    Pid = BitConverter.ToInt32(buffer, o + 52),
                });
            }
        }
    }

    static void ReadUdp(int family, List<PortEntry> entries)
    {
        // UDP_TABLE_OWNER_PID
        var buffer = Query((IntPtr b, ref int size) => GetExtendedUdpTable(b, ref size, false, family, 1, 0));
        if (buffer == null) return;
        int count = BitConverter.ToInt32(buffer, 0);
        bool v6 = family == AF_INET6;
        // MIB_UDPROW_OWNER_PID is 12 bytes; MIB_UDP6ROW_OWNER_PID is 28
        int rowSize = v6 ? 28 : 12;
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * rowSize;
            if (o + rowSize > buffer.Length) break;
            entries.Add(v6
                ? new PortEntry { Protocol = "UDPv6", Local = V6(buffer, o, BitConverter.ToUInt32(buffer, o + 16)), Port = Port(buffer, o + 20), Pid = BitConverter.ToInt32(buffer, o + 24) }
                : new PortEntry { Protocol = "UDP", Local = new IPAddress(BitConverter.ToUInt32(buffer, o)).ToString(), Port = Port(buffer, o + 4), Pid = BitConverter.ToInt32(buffer, o + 8) });
        }
    }

    delegate uint TableCall(IntPtr buffer, ref int size);

    static byte[]? Query(TableCall call)
    {
        int size = 0;
        call(IntPtr.Zero, ref size);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            // The table can grow between the size query and the read; pad and retry
            size += 4096;
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                uint result = call(ptr, ref size);
                if (result == 0)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, size);
                    return bytes;
                }
                if (result != 122) return null; // ERROR_INSUFFICIENT_BUFFER
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        return null;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    static int Port(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

    static string V6(byte[] b, int offset, uint scope)
    {
        var bytes = new byte[16];
        Buffer.BlockCopy(b, offset, bytes, 0, 16);
        return new IPAddress(bytes, scope).ToString();
    }

    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
