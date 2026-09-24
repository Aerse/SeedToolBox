using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeedToolBox.DevTools;

namespace SeedToolBox.SystemTools;

/// <summary>Keeps the PC (and optionally the display) from sleeping. Shared by the page and the tray entry.</summary>
static class KeepAwake
{
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public static bool Active { get; private set; }
    public static bool Display { get; private set; }
    /// <summary>When it stops by itself; null means indefinitely.</summary>
    public static DateTime? Until { get; private set; }
    /// <summary>The display option used by the tray toggle.</summary>
    public static bool PreferDisplay { get; set; } = true;

    public static event Action? Changed;

    static KeepAwake()
    {
        Timer.Tick += (_, _) =>
        {
            if (Until is { } until && DateTime.Now >= until) Stop();
            else Changed?.Invoke();
        };
    }

    /// <summary>Must be called on the UI thread, whose execution state is what counts.</summary>
    public static void Start(TimeSpan? duration, bool display)
    {
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (display ? ES_DISPLAY_REQUIRED : 0));
        Active = true;
        Display = display;
        Until = duration is { } d ? DateTime.Now + d : null;
        Timer.Start();
        Changed?.Invoke();
    }

    public static void Stop()
    {
        if (Active) SetThreadExecutionState(ES_CONTINUOUS);
        Active = false;
        Until = null;
        Timer.Stop();
        Changed?.Invoke();
    }

    public static void Toggle()
    {
        if (Active) Stop();
        else Start(null, PreferDisplay);
    }

    public static string Describe()
    {
        if (!Active) return "未开启，电脑会按电源设置正常睡眠和关闭屏幕";
        var what = Display ? "保持电脑唤醒并且屏幕常亮" : "保持电脑唤醒（屏幕可以关闭）";
        if (Until is not { } until) return $"已开启：{what}，直到手动停止";
        var left = until - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return $"已开启：{what}，{until:HH:mm:ss} 结束（剩余 {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}）";
    }

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}

sealed class KeepAwakePage : DockPanel
{
    readonly RadioButton _forever = new() { Content = "一直保持，直到手动停止", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
    readonly RadioButton _timed = new() { Content = "保持一段时间：", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _minutes = Ui.Field(60);
    readonly CheckBox _display = new() { Content = "同时保持屏幕常亮", Margin = new Thickness(0, 0, 0, 16) };
    readonly Button _toggle;
    readonly TextBlock _status = Ui.Status();

    public KeepAwakePage()
    {
        var header = Ui.Header("保持唤醒", "阻止电脑自动睡眠，适合下载、编译、演示等场景；托盘菜单也可以一键开关");
        _minutes.Text = "60";
        _display.IsChecked = KeepAwake.PreferDisplay;
        _display.Click += (_, _) => KeepAwake.PreferDisplay = _display.IsChecked == true;
        _minutes.GotFocus += (_, _) => _timed.IsChecked = true;
        _toggle = Ui.Button("开始", Toggle, accent: true);

        var presets = Ui.Row();
        foreach (var m in new[] { 15, 30, 60, 120, 240, 480 })
            presets.Children.Add(Ui.Button(m < 60 ? $"{m} 分钟" : $"{m / 60} 小时", () => { _timed.IsChecked = true; _minutes.Text = m.ToString(); }));

        var body = new StackPanel();
        body.Children.Add(_forever);
        body.Children.Add(Ui.Row(_timed, _minutes, Ui.Label("", 6), Ui.Label("分钟")));
        body.Children.Add(presets);
        body.Children.Add(_display);
        body.Children.Add(Ui.Row(_toggle));
        body.Children.Add(_status);

        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(body);

        Loaded += (_, _) => { KeepAwake.Changed += Update; Update(); };
        Unloaded += (_, _) => KeepAwake.Changed -= Update;
    }

    void Toggle()
    {
        if (KeepAwake.Active) { KeepAwake.Stop(); return; }
        TimeSpan? duration = null;
        if (_timed.IsChecked == true)
        {
            if (!double.TryParse(_minutes.Text, out var m) || m <= 0 || m > 7 * 24 * 60) { Ui.SetStatus(_status, "时长应为 1 分钟到 7 天", true); return; }
            duration = TimeSpan.FromMinutes(m);
        }
        KeepAwake.Start(duration, _display.IsChecked == true);
    }

    void Update()
    {
        _toggle.Content = KeepAwake.Active ? "停止" : "开始";
        Ui.SetStatus(_status, KeepAwake.Describe());
    }
}
