using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SeedToolBox.Core;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;

namespace SeedToolBox.SystemTools;

enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

sealed class StartupItem
{
    public StartupSource Source { get; set; }
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public RegistryValueKind Kind { get; set; }
    /// <summary>For folder items, the full path of the file.</summary>
    public string? File { get; set; }
    /// <summary>Also disabled in Task Manager (StartupApproved).</summary>
    public bool ApprovedOff { get; set; }

    public string StateText => Enabled ? (ApprovedOff ? "已在任务管理器中禁用" : "启用") : "已禁用";
    public string SourceText => Source switch
    {
        StartupSource.UserRun => "HKCU Run",
        StartupSource.MachineRun => "HKLM Run",
        StartupSource.MachineRun32 => "HKLM Run（32 位）",
        StartupSource.UserFolder => "启动文件夹（当前用户）",
        _ => "启动文件夹（所有用户）",
    };
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
}

/// <summary>Run keys and Startup folders. Disabling moves the entry aside so it can be restored.</summary>
sealed class StartupPage : DockPanel
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string DisabledKey = @"Software\SeedToolBox disabled\Run";
    const string Disabled32Key = @"Software\SeedToolBox disabled\Run32";
    static readonly string DisabledDir = Path.Combine(AppPaths.Data, "startup-disabled");
    static string UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    static string CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    readonly ObservableCollection<StartupItem> _items = new();
    readonly ListView _list = new();
    readonly TextBlock _status = Ui.Status();
    bool _busy;

    public StartupPage()
    {
        var header = Ui.Header("启动项", "开机自动运行的程序（注册表 Run 键和启动文件夹）；禁用时移到备份位置，可随时恢复");
        var toolbar = Ui.Row(Ui.Button("刷新", Load), Ui.Button("禁用", () => Toggle(false)), Ui.Button("启用", () => Toggle(true)),
            Ui.Button("打开文件位置", OpenLocation), Ui.Button("打开启动文件夹", () => ProcessLauncher.Start(UserFolder)),
            ListTools.ExportButton(_list, _status, "启动项.csv"));

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        void Column(string h, string path, double width) => view.Columns.Add(new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new Binding(path) });
        Column("状态", nameof(StartupItem.StateText), 150);
        Column("名称", nameof(StartupItem.Name), 180);
        Column("位置", nameof(StartupItem.SourceText), 170);
        Column("命令", nameof(StartupItem.Command), 460);
        _list.View = view;
        ListTools.Sortable(_list);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is StartupItem i) Toggle(!i.Enabled); };

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(_list);
        Load();
    }

    void Load()
    {
        try
        {
            var items = new List<StartupItem>();
            ReadKey(items, Registry.CurrentUser, RunKey, DisabledKey, StartupSource.UserRun);
            var machine = Elevation.Base(RegistryHive.LocalMachine);
            ReadKey(items, machine, RunKey, DisabledKey, StartupSource.MachineRun);
            if (Environment.Is64BitOperatingSystem) ReadKey(items, machine, Run32Key, Disabled32Key, StartupSource.MachineRun32);
            ReadFolder(items, UserFolder, Path.Combine(DisabledDir, "User"), StartupSource.UserFolder);
            ReadFolder(items, CommonFolder, Path.Combine(DisabledDir, "Common"), StartupSource.CommonFolder);
            ReadApproved(items);
            _items.Clear();
            foreach (var i in items) _items.Add(i);
            Ui.SetStatus(_status, $"共 {items.Count} 项，启用 {items.Count(i => i.Enabled)} 项  HKLM 和所有用户的启动文件夹需要管理员权限；双击切换");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    static void ReadKey(List<StartupItem> items, RegistryKey root, string path, string disabledPath, StartupSource source)
    {
        void Read(string p, bool enabled)
        {
            using var key = root.OpenSubKey(p);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                var kind = key.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                items.Add(new StartupItem { Source = source, Enabled = enabled, Name = name, Kind = kind, Command = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "" });
            }
        }
        Read(path, true);
        Read(disabledPath, false);
    }

    static void ReadFolder(List<StartupItem> items, string folder, string disabled, StartupSource source)
    {
        void Read(string dir, bool enabled)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new StartupItem { Source = source, Enabled = enabled, Name = name, File = file, Command = file });
            }
        }
        Read(folder, true);
        Read(disabled, false);
    }

    /// <summary>Task Manager marks disabled items in StartupApproved with an odd first byte.</summary>
    static void ReadApproved(List<StartupItem> items)
    {
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        var machine = Elevation.Base(RegistryHive.LocalMachine);
        foreach (var item in items.Where(i => i.Enabled))
        {
            var (root, sub) = item.Source switch
            {
                StartupSource.UserRun => (Registry.CurrentUser, "Run"),
                StartupSource.MachineRun => (machine, "Run"),
                StartupSource.MachineRun32 => (machine, "Run32"),
                StartupSource.UserFolder => (Registry.CurrentUser, "StartupFolder"),
                _ => (machine, "StartupFolder"),
            };
            using var key = root.OpenSubKey(Approved + sub);
            if (key?.GetValue(item.Name) is byte[] { Length: > 0 } flags) item.ApprovedOff = (flags[0] & 1) != 0;
        }
    }

    void OpenLocation()
    {
        if (_list.SelectedItem is not StartupItem item) { Ui.SetStatus(_status, "先选中一行", true); return; }
        var path = item.File ?? ExtractPath(item.Command);
        if (path == null) { Ui.SetStatus(_status, "无法从命令中找到程序路径", true); return; }
        ProcessLauncher.OpenLocation(path);
    }

    static string? ExtractPath(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith("\""))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : null;
        }
        // Unquoted: take the longest prefix that is an existing file
        var parts = command.Split(' ');
        for (int n = parts.Length; n > 0; n--)
        {
            var candidate = string.Join(" ", parts.Take(n));
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        return null;
    }

    async void Toggle(bool enable)
    {
        if (_busy) return;
        var selected = _list.SelectedItem as StartupItem;
        if (selected == null) { Ui.SetStatus(_status, "先选中一行", true); return; }
        if (selected.Enabled == enable) { Ui.SetStatus(_status, enable ? "已经是启用状态" : "已经是禁用状态"); return; }
        _busy = true;
        Ui.SetStatus(_status, enable ? "正在启用…" : "正在禁用…");
        try
        {
            var error = await Task.Run(() => Move(selected, enable));
            Load();
            if (error != null) Ui.SetStatus(_status, (enable ? "启用失败：" : "禁用失败：") + error, true);
            else Ui.SetStatus(_status, $"已{(enable ? "启用" : "禁用")} {selected.Name}{(enable && selected.ApprovedOff ? "（它在任务管理器中仍是禁用的）" : "")}");
        }
        finally
        {
            _busy = false;
        }
    }

    static string? Move(StartupItem item, bool enable)
    {
        if (item.File != null) return MoveFile(item, enable);

        var (from, to) = item.Source == StartupSource.MachineRun32
            ? (enable ? Disabled32Key : Run32Key, enable ? Run32Key : Disabled32Key)
            : (enable ? DisabledKey : RunKey, enable ? RunKey : DisabledKey);
        if (item.Source == StartupSource.UserRun)
        {
            try
            {
                using (var target = Registry.CurrentUser.CreateSubKey(to)) target!.SetValue(item.Name, item.Command, item.Kind);
                using (var source = Registry.CurrentUser.OpenSubKey(from, true)) source?.DeleteValue(item.Name, false);
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return ex.Message; }
        }
        // The 32-bit key is addressed through WOW6432Node since the .reg is imported in the 64-bit view
        var reg = new RegFile()
            .Key(@"HKEY_LOCAL_MACHINE\" + to).Set(item.Name, item.Kind, item.Command)
            .Key(@"HKEY_LOCAL_MACHINE\" + from).Delete(item.Name);
        return Elevation.ImportReg(reg);
    }

    static string? MoveFile(StartupItem item, bool enable)
    {
        var folder = item.Source == StartupSource.UserFolder ? UserFolder : CommonFolder;
        var disabled = Path.Combine(DisabledDir, item.Source == StartupSource.UserFolder ? "User" : "Common");
        var target = Path.Combine(enable ? folder : disabled, item.Name);
        if (File.Exists(target)) return "目标位置已有同名文件：" + target;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(item.File!, target);
            return null;
        }
        catch (UnauthorizedAccessException) when (item.NeedsAdmin)
        {
            return Elevation.Cmd($"move /y \"{item.File}\" \"{target}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
