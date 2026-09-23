using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SeedToolBox.Core;
using SeedToolBox.Core.Native;
using SeedToolBox.Core.Services;
using SeedToolBox.Host;
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox;

public partial class App : Application
{
    const string MutexName = "SeedToolBox.SingleInstance";
    const string ShowEventName = "SeedToolBox.ShowWindow";

    Mutex? _mutex;
    TrayIcon? _tray;
    MainWindow? _main;
    GlobalHotkey? _hotkey;
    LauncherData? _data;
    ModuleManager? _modules;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: ask the existing instance to show its window
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var evt)) evt.Set();
            Shutdown();
            return;
        }

        Log.Init(AppPaths.Logs);
        Log.Info("Starting");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        NativeLibraries.Init();
        ListenForShowRequests();
        base.OnStartup(e);

        var settings = new JsonSettingsStore(AppPaths.Data);
        var data = settings.Load<LauncherData>(LauncherData.SettingsName);
        if (data.Groups.Count == 0) data.Groups.Add(new ItemGroup { Name = "常用工具" });

        _data = data;
        _main = new MainWindow(data, settings);

        try { AutoStart.Refresh(); }
        catch (Exception ex) { Log.Error("Failed to refresh autostart entry", ex); }

        _tray = new TrayIcon(data.Window.SizeLocked, AutoStart.IsEnabled, data.Hotkey);
        _tray.ToggleWindowRequested += _main.ToggleVisibility;
        _tray.ShowWindowRequested += _main.ShowAndActivate;
        _tray.OpenAppLocationRequested += () => ProcessLauncher.OpenLocation(ProcessLauncher.ExePath);
        _tray.LockSizeChanged += _main.SetSizeLocked;
        _tray.AutoStartChanged += SetAutoStart;
        _tray.HotkeyRequested += ChangeHotkey;
        _tray.ExitRequested += ExitApp;

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += _main.ToggleFromHotkey;
        if (!RegisterHotkey(data.Hotkey))
            _tray.ShowMessage($"呼出热键 {data.Hotkey} 已被其他程序占用，可在托盘菜单中更换");

        _modules = new ModuleManager(new AppHost(_tray, settings, Dispatcher));
        _modules.LoadAll();

        if (!e.Args.Contains(AutoStart.BackgroundArg))
            _main.Show();
    }

    bool RegisterHotkey(string text) =>
        _hotkey!.Register(Hotkey.TryParse(text, out var key) ? key : null);

    void ChangeHotkey()
    {
        var old = _data!.Hotkey;
        if (HotkeyDialog.Show(old) is not { } text || text == old) return;

        if (!RegisterHotkey(text))
        {
            RegisterHotkey(old);
            MessageBox.Show($"热键 {text} 已被其他程序占用，请换一个", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _data.Hotkey = text;
        _tray!.SetHotkeyText(text);
        _main!.RequestSave();
    }

    static void SetAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Set(enabled);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change autostart", ex);
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ListenForShowRequests()
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (evt.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => _main?.ShowAndActivate()));
        }) { IsBackground = true }.Start();
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"发生错误：{e.Exception.Message}\n详情见 Data\\Logs\\app.log", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    void ExitApp()
    {
        _main?.PrepareExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _modules?.ShutdownAll();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        Log.Info("Exited");
        base.OnExit(e);
    }
}
