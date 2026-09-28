using SeedToolBox.Core.Modules;
using SeedToolBox.Launcher;
using SeedToolBox.SystemTools;

namespace SeedToolBox.Modules;

/// <summary>System tool pages in the toolbox, plus a tray submenu with shortcuts to common Windows tools.</summary>
sealed class SystemToolsModule : IModule
{
    const string Submenu = "系统工具";

    public string Id => "system-tools";
    public string Name => "系统工具";

    public void Initialize(IAppHost host)
    {
        Add(host, "任务管理器", "taskmgr.exe");
        Add(host, "控制面板", "control.exe");
        Add(host, "设备管理器", "devmgmt.msc");
        Add(host, "服务", "services.msc");
        Add(host, "注册表编辑器", "regedit.exe");
        Add(host, "命令提示符", "cmd.exe");

        host.AddPage("keep-awake", Submenu, "\uE7E8", "保持唤醒", () => new KeepAwakePage());
        host.AddPage("topmost", Submenu, "\uE718", "窗口置顶", () => new TopmostPage());
        host.AddPage("env-vars", Submenu, "\uE943", "环境变量", () => new EnvVarsPage());
        host.AddPage("startup", Submenu, "\uE768", "启动项", () => new StartupPage());
        host.AddPage("processes", Submenu, "\uE9D9", "进程", () => new ProcessPage());
        host.AddPage("services", Submenu, "\uE912", "本地服务", () => new ServicesPage());
        LocalServices.StartAutoStart();

        host.AddTrayMenuItem("保持唤醒（开/关）", KeepAwake.Toggle);
        host.AddLauncherCommand("保持唤醒（开/关）", KeepAwake.Toggle);
        foreach (var (id, name) in new[] { ("keep-awake", "保持唤醒"), ("topmost", "窗口置顶"), ("env-vars", "环境变量"), ("startup", "启动项"), ("processes", "进程") })
            host.AddLauncherCommand("打开" + name + "页面", () => host.OpenPage(id));
        host.AddHotkey("topmost", "切换当前窗口置顶", "", () => Topmost.ToggleForeground());

        _monitor = new MonitorService(host.Settings, () => host.OpenPage("monitor"));
        host.AddPage("monitor", Submenu, "\uE9D2", "系统监控", () => new MonitorPage(_monitor));
        host.AddTrayMenuItem("系统监控悬浮条（开/关）", _monitor.Toggle);
        host.AddTrayMenuItem("系统监控鼠标穿透（开/关）", _monitor.ToggleClickThrough);
        host.AddLauncherCommand("系统监控悬浮条（开/关）", _monitor.Toggle);
        host.AddLauncherCommand("打开系统监控页面", () => host.OpenPage("monitor"));
        host.AddHotkey("monitor", "显示/隐藏系统监控", "", _monitor.Toggle);
        host.AddHotkey("monitor-through", "切换系统监控鼠标穿透", "", _monitor.ToggleClickThrough);

        host.AddPage("sound", Submenu, "\uE767", "声音与亮度", () => new SoundPage());
        host.AddLauncherCommand("打开声音与亮度页面", () => host.OpenPage("sound"));
        host.AddLauncherCommand("切换到下一个输出设备", SoundHotkeys.NextOutput);
        host.AddLauncherCommand("麦克风静音（开/关）", SoundHotkeys.ToggleMicrophone);
        host.AddHotkey("next-output", "切换到下一个输出设备", "", SoundHotkeys.NextOutput);
        host.AddHotkey("mic-mute", "麦克风静音（开/关）", "", SoundHotkeys.ToggleMicrophone);
        host.AddHotkey("brightness-up", "亮度 +10", "", () => SoundHotkeys.ChangeBrightness(10));
        host.AddHotkey("brightness-down", "亮度 -10", "", () => SoundHotkeys.ChangeBrightness(-10));
    }

    static void Add(IAppHost host, string text, string file) =>
        host.AddTrayMenuItem(text, () => ProcessLauncher.Start(file, displayName: text), Submenu);

    MonitorService? _monitor;

    public void Shutdown()
    {
        KeepAwake.Stop();
        _monitor?.Shutdown();
    }
}
