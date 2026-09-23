using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SeedToolBox.Core;
using SeedToolBox.Core.Native;
using SeedToolBox.Core.Services;
using SeedToolBox.Host;
using SeedToolBox.Launcher;

namespace SeedToolBox;

public partial class App : Application
{
    const string MutexName = "SeedToolBox.SingleInstance";
    const string ShowEventName = "SeedToolBox.ShowWindow";

    Mutex? _mutex;
    TrayIcon? _tray;
    MainWindow? _main;
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

        _main = new MainWindow(data, settings);

        _tray = new TrayIcon(data.Window.SizeLocked);
        _tray.ToggleWindowRequested += _main.ToggleVisibility;
        _tray.ShowWindowRequested += _main.ShowAndActivate;
        _tray.OpenAppLocationRequested += () => ProcessLauncher.OpenLocation(ProcessLauncher.ExePath);
        _tray.LockSizeChanged += _main.SetSizeLocked;
        _tray.ExitRequested += ExitApp;

        _modules = new ModuleManager(new AppHost(_tray, settings, Dispatcher));
        _modules.LoadAll();

        _main.Show();
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
        _tray?.Dispose();
        _mutex?.Dispose();
        Log.Info("Exited");
        base.OnExit(e);
    }
}
