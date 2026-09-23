using System.Windows;
using SeedToolBox.Services;

namespace SeedToolBox;

public partial class App : Application
{
    const string MutexName = "SeedToolBox.SingleInstance";
    const string ShowEventName = "SeedToolBox.ShowWindow";

    Mutex? _mutex;
    TrayIcon? _tray;
    MainWindow? _main;

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
        ListenForShowRequests();

        base.OnStartup(e);

        var data = DataStore.Load();
        _main = new MainWindow(data);

        _tray = new TrayIcon(data.Window.SizeLocked);
        _tray.ToggleWindowRequested += _main.ToggleVisibility;
        _tray.ShowWindowRequested += _main.ShowAndActivate;
        _tray.OpenAppLocationRequested += () => Launcher.OpenLocation(Environment.ProcessPath!);
        _tray.LockSizeChanged += _main.SetSizeLocked;
        _tray.ExitRequested += ExitApp;

        _main.Show();
    }

    void ListenForShowRequests()
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (evt.WaitOne())
                Dispatcher.BeginInvoke(() => _main?.ShowAndActivate());
        }) { IsBackground = true }.Start();
    }

    void ExitApp()
    {
        _main?.PrepareExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
