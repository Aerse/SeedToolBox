using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.SystemTools;

public sealed class MonitorSettings
{
    public const string SettingsName = "system-monitor";

    public bool Enabled { get; set; }
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Network { get; set; } = true;
    public bool ClickThrough { get; set; }
    public double Opacity { get; set; } = 0.85;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

/// <summary>One reading; percentages are 0–100, -1 when not available.</summary>
sealed record MonitorSample(double Cpu, double Memory, long MemoryUsed, long MemoryTotal, double Gpu, double Down, double Up);

/// <summary>Reads CPU, memory, GPU and network use once a second on a background thread while anyone listens.</summary>
static class SystemMonitor
{
    static readonly object Lock = new();
    static Timer? _timer;
    static int _listeners;
    static long _idle, _kernel, _user;
    static long _received, _sent;
    static DateTime _netTime;
    static Dictionary<string, CounterSample>? _gpu;
    static bool _gpuUnavailable;

    public static MonitorSample? Last { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public static event Action<MonitorSample>? Sampled;

    public static void Subscribe(Action<MonitorSample> handler)
    {
        lock (Lock)
        {
            Sampled += handler;
            if (_listeners++ == 0) _timer = new Timer(_ => Sample(), null, 0, 1000);
        }
    }

    public static void Unsubscribe(Action<MonitorSample> handler)
    {
        lock (Lock)
        {
            Sampled -= handler;
            if (--_listeners > 0) return;
            _timer?.Dispose();
            _timer = null;
            _gpu = null;
            _gpuValue = 0;
            _idle = _received = _sent = 0;
        }
    }

    static int _sampling, _gpuReading;
    static double _gpuValue;

    static void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        // Reading the GPU counters can take seconds with many processes, so it runs on its own and the last value is shown
        if (Interlocked.Exchange(ref _gpuReading, 1) == 0)
            ThreadPool.QueueUserWorkItem(_ => { try { _gpuValue = Gpu(); } finally { _gpuReading = 0; } });
        try
        {
            var sample = new MonitorSample(Cpu(), 0, 0, 0, _gpuValue, 0, 0);
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                sample = sample with { Memory = mem.dwMemoryLoad, MemoryUsed = (long)(mem.ullTotalPhys - mem.ullAvailPhys), MemoryTotal = (long)mem.ullTotalPhys };
            var (down, up) = Network();
            sample = sample with { Down = down, Up = up };
            Last = sample;
            Sampled?.Invoke(sample);
        }
        catch (Exception ex) { Log.Error("System monitor sample failed", ex); }
        finally { _sampling = 0; }
    }

    static double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        bool first = _idle == 0;
        (_idle, _kernel, _user) = (idle, kernel, user);
        // Kernel time includes idle time
        return first || dk + du == 0 ? 0 : Math.Max(0, Math.Min(100, 100.0 * (dk + du - di) / (dk + du)));
    }

    static (double Down, double Up) Network()
    {
        long received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch (NetworkInformationException) { }
        }
        var now = DateTime.UtcNow;
        double seconds = (now - _netTime).TotalSeconds;
        bool first = _received == 0 && _sent == 0;
        var result = first || seconds <= 0 ? (0, 0) : (Math.Max(0, (received - _received) / seconds), Math.Max(0, (sent - _sent) / seconds));
        (_received, _sent, _netTime) = (received, sent, now);
        return result;
    }

    /// <summary>Like Task Manager: each engine's use summed over processes, and the busiest engine counts.</summary>
    static double Gpu()
    {
        if (_gpuUnavailable) return -1;
        try
        {
            // One read for all instances; a PerformanceCounter per instance is far too slow with hundreds of them
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            var current = new Dictionary<string, CounterSample>();
            var engines = new Dictionary<string, double>();
            if (data != null)
                foreach (InstanceData instance in data.Values)
                {
                    current[instance.InstanceName] = instance.Sample;
                    if (EngineOf(instance.InstanceName) is not { } engine || _gpu == null || !_gpu.TryGetValue(instance.InstanceName, out var previous)) continue;
                    engines.TryGetValue(engine, out var sum);
                    engines[engine] = sum + CounterSample.Calculate(previous, instance.Sample);
                }
            _gpu = current;
            return engines.Count == 0 ? 0 : Math.Min(100, engines.Values.Max());
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("GPU counters are not available", ex);
            _gpuUnavailable = true;
            return -1;
        }
    }

    /// <summary>"pid_12_luid_0x0_0x1_phys_0_eng_3_engtype_3D" → "luid_0x0_0x1_phys_0_eng_3".</summary>
    static string? EngineOf(string instance)
    {
        int luid = instance.IndexOf("luid_", StringComparison.Ordinal), type = instance.IndexOf("_engtype_", StringComparison.Ordinal);
        return luid < 0 || type < luid ? null : instance.Substring(luid, type - luid);
    }

    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond >= 1 << 30 ? $"{bytesPerSecond / (1 << 30):0.0} GB/s"
        : bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / (1 << 20):0.0} MB/s"
        : $"{bytesPerSecond / 1024:0} KB/s";

    public static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>The always-on-top strip of readings. Drag it to move; right-click for options.</summary>
sealed class MonitorOverlay : Window
{
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    readonly MonitorSettings _settings;
    readonly Action _save;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    readonly Border _card;

    public MonitorOverlay(MonitorSettings settings, Action save, Action openPage)
    {
        _settings = settings;
        _save = save;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "系统监控";
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Child = _row,
            Cursor = Cursors.SizeAll,
        };
        Content = _card;
        ApplyOpacity();

        var menu = new ContextMenu();
        var through = new MenuItem { Header = "鼠标穿透（从托盘或设置页恢复）" };
        through.Click += (_, _) => { _settings.ClickThrough = true; ApplyClickThrough(); _save(); };
        var open = new MenuItem { Header = "设置…" };
        open.Click += (_, _) => openPage();
        var close = new MenuItem { Header = "关闭悬浮条" };
        close.Click += (_, _) => { _settings.Enabled = false; _save(); Close(); };
        menu.Items.Add(through);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        ContextMenu = menu;

        MouseLeftButtonDown += (_, _) =>
        {
            DragMove();
            _settings.Left = Left;
            _settings.Top = Top;
            _save();
        };
        SourceInitialized += (_, _) => ApplyClickThrough();
        Loaded += (_, _) => Place();
        Update(SystemMonitor.Last);
        SystemMonitor.Subscribe(OnSample);
        Closed += (_, _) => SystemMonitor.Unsubscribe(OnSample);
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        if (_settings.Left is { } left && _settings.Top is { } top
            && left > SystemParameters.VirtualScreenLeft - 20 && top > SystemParameters.VirtualScreenTop - 20
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20)
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = area.Right - ActualWidth - 16;
            Top = area.Top + 16;
        }
    }

    public void ApplyOpacity() => _card.Opacity = Math.Max(0.3, Math.Min(1, _settings.Opacity));

    public void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        style = _settings.ClickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    void OnSample(MonitorSample sample) => Dispatcher.BeginInvoke(new Action(() => Update(sample)));

    public void Update(MonitorSample? s)
    {
        _row.Children.Clear();
        void Add(string label, string value, Brush brush)
        {
            if (_row.Children.Count > 0) _row.Children.Add(new Border { Width = 12 });
            _row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            _row.Children.Add(new TextBlock { Text = value, Foreground = brush, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        if (_settings.Cpu) Add("CPU", s == null ? "--" : $"{s.Cpu:0}%", Load(s?.Cpu ?? 0));
        if (_settings.Memory) Add("内存", s == null ? "--" : $"{s.Memory:0}%", Load(s?.Memory ?? 0));
        if (_settings.Gpu && (s == null || s.Gpu >= 0)) Add("GPU", s == null ? "--" : $"{s.Gpu:0}%", Load(s?.Gpu ?? 0));
        if (_settings.Network)
        {
            Add("↓", s == null ? "--" : SystemMonitor.Rate(s.Down), Brushes.White);
            Add("↑", s == null ? "--" : SystemMonitor.Rate(s.Up), Brushes.White);
        }
        if (_row.Children.Count == 0) Add("系统监控", "未选择项目", Brushes.White);
    }

    static readonly Brush Normal = Freeze(Color.FromRgb(108, 203, 95)), Busy = Freeze(Color.FromRgb(252, 200, 60)), Full = Freeze(Color.FromRgb(255, 107, 107));
    static Brush Load(double percent) => percent >= 90 ? Full : percent >= 70 ? Busy : Normal;
    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

/// <summary>Turns the overlay on and off; shared by the page, the tray and the hotkey.</summary>
sealed class MonitorService
{
    readonly ISettingsStore _store;
    readonly Action _openPage;
    MonitorOverlay? _overlay;

    public MonitorSettings Settings { get; }
    public event Action? Changed;

    public MonitorService(ISettingsStore store, Action openPage)
    {
        _store = store;
        _openPage = openPage;
        Settings = store.Load<MonitorSettings>(MonitorSettings.SettingsName);
        if (Settings.Enabled) Show();
    }

    public void Save()
    {
        _store.Save(MonitorSettings.SettingsName, Settings);
        Changed?.Invoke();
    }

    public void Toggle() => SetEnabled(!Settings.Enabled);

    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        if (enabled) Show();
        else _overlay?.Close();
        Save();
    }

    public void ToggleClickThrough()
    {
        Settings.ClickThrough = !Settings.ClickThrough;
        _overlay?.ApplyClickThrough();
        Save();
    }

    /// <summary>Redraws after the shown items or the opacity changed.</summary>
    public void Refresh()
    {
        _overlay?.ApplyOpacity();
        _overlay?.ApplyClickThrough();
        _overlay?.Update(SystemMonitor.Last);
    }

    void Show()
    {
        if (_overlay != null) return;
        _overlay = new MonitorOverlay(Settings, Save, _openPage);
        _overlay.Closed += (_, _) => { _overlay = null; if (Settings.Enabled) { Settings.Enabled = false; Save(); } };
        _overlay.Show();
    }

    public void Shutdown()
    {
        var enabled = Settings.Enabled;
        _overlay?.Close();
        // Closing on exit is not the user turning it off
        if (enabled != Settings.Enabled) { Settings.Enabled = enabled; _store.Save(MonitorSettings.SettingsName, Settings); }
    }
}

sealed class MonitorPage : DockPanel
{
    readonly MonitorService _service;
    readonly TextBlock _cpu = Big(), _memory = Big(), _gpu = Big(), _down = Big(), _up = Big();
    readonly TextBlock _memoryDetail = Small(), _gpuDetail = Small();

    public MonitorPage(MonitorService service)
    {
        _service = service;
        var s = service.Settings;
        var header = Ui.Header("系统监控", "在桌面上显示一条总在最前的悬浮条：CPU、内存、显卡占用和网速；可拖动，右键有选项");

        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        cards.Children.Add(Card("CPU", _cpu, Small()));
        cards.Children.Add(Card("内存", _memory, _memoryDetail));
        cards.Children.Add(Card("显卡", _gpu, _gpuDetail));
        cards.Children.Add(Card("下载", _down, Small()));
        cards.Children.Add(Card("上传", _up, Small()));

        CheckBox Option(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 16, 8) };
            box.Click += (_, _) => { set(box.IsChecked == true); _service.Save(); _service.Refresh(); };
            return box;
        }
        var enabled = new CheckBox { Content = "显示悬浮条", IsChecked = s.Enabled, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
        enabled.Click += (_, _) => _service.SetEnabled(enabled.IsChecked == true);
        var through = Option("鼠标穿透（点击会落到下面的窗口，不能拖动）", s.ClickThrough, v => s.ClickThrough = v);
        void Sync() { enabled.IsChecked = s.Enabled; through.IsChecked = s.ClickThrough; }

        var what = new WrapPanel
        {
            Children =
            {
                Option("CPU", s.Cpu, v => s.Cpu = v),
                Option("内存", s.Memory, v => s.Memory = v),
                Option("显卡", s.Gpu, v => s.Gpu = v),
                Option("网速", s.Network, v => s.Network = v),
            },
        };
        var opacity = new Slider { Minimum = 0.3, Maximum = 1, Value = s.Opacity, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        opacity.ValueChanged += (_, _) => { s.Opacity = opacity.Value; _service.Refresh(); };
        opacity.LostMouseCapture += (_, _) => _service.Save();

        var body = new StackPanel
        {
            Children =
            {
                cards,
                enabled,
                Ui.Row(Ui.Label("显示内容", 12), what),
                Ui.Row(Ui.Label("不透明度", 12), opacity),
                through,
                new TextBlock { Text = "托盘菜单「系统工具」里也能开关悬浮条和鼠标穿透；还可以在设置页给「显示/隐藏系统监控」设快捷键", Foreground = DialogWindow.HintBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
            },
        };
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Loaded += (_, _) => { SystemMonitor.Subscribe(OnSample); _service.Changed += Sync; Update(SystemMonitor.Last); };
        Unloaded += (_, _) => { SystemMonitor.Unsubscribe(OnSample); _service.Changed -= Sync; };
    }

    static TextBlock Big() => new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Text = "--" };
    static TextBlock Small() => new() { FontSize = 11, Foreground = DialogWindow.HintBrush };

    static Border Card(string title, TextBlock value, TextBlock detail) => new()
    {
        Width = 132,
        Margin = new Thickness(0, 0, 8, 8),
        Padding = new Thickness(12, 10, 12, 10),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"], FontSize = 12 },
                value,
                detail,
            },
        },
    };

    void OnSample(MonitorSample sample) => Dispatcher.BeginInvoke(new Action(() => Update(sample)));

    void Update(MonitorSample? s)
    {
        if (s == null) return;
        _cpu.Text = $"{s.Cpu:0}%";
        _memory.Text = $"{s.Memory:0}%";
        _memoryDetail.Text = $"{SystemMonitor.Gb(s.MemoryUsed)} / {SystemMonitor.Gb(s.MemoryTotal)}";
        _gpu.Text = s.Gpu < 0 ? "--" : $"{s.Gpu:0}%";
        _gpuDetail.Text = s.Gpu < 0 ? "这台电脑读不到显卡占用" : "最忙的引擎";
        _down.Text = SystemMonitor.Rate(s.Down);
        _up.Text = SystemMonitor.Rate(s.Up);
    }
}
