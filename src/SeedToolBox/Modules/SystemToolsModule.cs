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

        host.AddTrayMenuItem("保持唤醒（开/关）", KeepAwake.Toggle);
        host.AddLauncherCommand("保持唤醒（开/关）", KeepAwake.Toggle);
        foreach (var (id, name) in new[] { ("keep-awake", "保持唤醒"), ("topmost", "窗口置顶"), ("env-vars", "环境变量"), ("startup", "启动项"), ("processes", "进程") })
            host.AddLauncherCommand("打开" + name + "页面", () => host.OpenPage(id));
        host.AddHotkey("topmost", "切换当前窗口置顶", "", () => Topmost.ToggleForeground());
    }

    static void Add(IAppHost host, string text, string file) =>
        host.AddTrayMenuItem(text, () => ProcessLauncher.Start(file, displayName: text), Submenu);

    public void Shutdown() => KeepAwake.Stop();
}
