using SeedToolBox.Core.Modules;
using SeedToolBox.Launcher;

namespace SeedToolBox.Modules;

/// <summary>Tray submenu with shortcuts to common Windows tools.</summary>
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
    }

    static void Add(IAppHost host, string text, string file) =>
        host.AddTrayMenuItem(text, () => ProcessLauncher.Start(file, displayName: text), Submenu);

    public void Shutdown() { }
}
