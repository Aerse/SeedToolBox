using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SeedToolBox.Core;
using SeedToolBox.Core.Native;
using SeedToolBox.Core.Services;
using SeedToolBox.Host;
using SeedToolBox.Launcher;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox;

public partial class App : Application
{
    const string MutexName = "SeedToolBox.SingleInstance";
    const string ShowEventName = "SeedToolBox.ShowWindow";

    Mutex? _mutex;
    TrayIcon? _tray;
    MainWindow? _main;
    LauncherData? _data;
    ScreenToolService? _screenTools;
    ModuleManager? _modules;
    readonly List<HotkeyBinding> _hotkeys = new();

    /// <summary>A configurable global hotkey and the tray command it mirrors.</summary>
    sealed class HotkeyBinding
    {
        public HotkeyBinding(string command, string label, Func<string> get, Action<string> set, Action action)
        {
            Command = command;
            Label = label;
            Get = get;
            Set = set;
            Hotkey.Pressed += action;
        }

        public string Command { get; }
        public string Label { get; }
        public Func<string> Get { get; }
        public Action<string> Set { get; }
        public GlobalHotkey Hotkey { get; } = new();

        public bool Register(string text) => Hotkey.Register(Host.Hotkey.TryParse(text, out var key) ? key : null);
    }

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

        _tray = new TrayIcon(data.Window.SizeLocked, AutoStart.IsEnabled);
        _tray.ToggleWindowRequested += _main.ToggleVisibility;
        _tray.ShowWindowRequested += _main.ShowAndActivate;
        _tray.OpenAppLocationRequested += () => ProcessLauncher.OpenLocation(ProcessLauncher.ExePath);
        _tray.LockSizeChanged += _main.SetSizeLocked;
        _tray.AutoStartChanged += SetAutoStart;
        _tray.HotkeyRequested += ChangeHotkeys;
        _tray.ExitRequested += ExitApp;

        var screen = _screenTools = new ScreenToolService(settings);
        _tray.AddCommand("screenshot", "截图", AfterTrayMenu(screen.Screenshot));
        _tray.AddCommand("color", "取色", AfterTrayMenu(screen.PickColor));
        _tray.AddCommand("ruler", "屏幕标尺", AfterTrayMenu(screen.Ruler));
        _tray.AddCommand("record", "录屏", AfterTrayMenu(screen.Record));
        _tray.AddCommand("recordsettings", "录屏设置…", () => screen.RecordSettings());
        _tray.AddCommand("ocr", "识别文字", AfterTrayMenu(screen.RecognizeText));
        _tray.AddCommand("qr", "识别二维码", AfterTrayMenu(screen.RecognizeQrCodes));
        _tray.AddCommand("qrscreen", "识别全屏二维码", AfterTrayMenu(screen.ScanQrCodes));
        _tray.AddCommand("qrgen", "生成二维码…", screen.GenerateQrCode);

        var main = _main;
        _hotkeys.Add(new HotkeyBinding(TrayIcon.ShowWindowCommand, "呼出主窗口", () => data.Hotkey, v => data.Hotkey = v, main.ToggleFromHotkey));
        _hotkeys.Add(new HotkeyBinding("screenshot", "截图", () => screen.Settings.ScreenshotHotkey, v => screen.Settings.ScreenshotHotkey = v, screen.Screenshot));
        _hotkeys.Add(new HotkeyBinding("color", "取色", () => screen.Settings.ColorPickerHotkey, v => screen.Settings.ColorPickerHotkey = v, screen.PickColor));
        _hotkeys.Add(new HotkeyBinding("ruler", "屏幕标尺", () => screen.Settings.RulerHotkey, v => screen.Settings.RulerHotkey = v, screen.Ruler));
        _hotkeys.Add(new HotkeyBinding("record", "录屏", () => screen.Settings.RecordHotkey, v => screen.Settings.RecordHotkey = v, screen.Record));
        _hotkeys.Add(new HotkeyBinding("ocr", "识别文字", () => screen.Settings.OcrHotkey, v => screen.Settings.OcrHotkey = v, screen.RecognizeText));
        _hotkeys.Add(new HotkeyBinding("qr", "识别二维码", () => screen.Settings.QrHotkey, v => screen.Settings.QrHotkey = v, screen.RecognizeQrCodes));

        var taken = new List<string>();
        foreach (var binding in _hotkeys)
        {
            if (!binding.Register(binding.Get())) taken.Add($"{binding.Label} {binding.Get()}");
            _tray.SetShortcutText(binding.Command, binding.Get());
        }
        if (taken.Count > 0)
            _tray.ShowMessage($"热键已被其他程序占用：{string.Join("、", taken)}。可在托盘菜单「热键设置」中更换");

        _modules = new ModuleManager(new AppHost(_tray, settings, Dispatcher));
        _modules.LoadAll();

        if (!e.Args.Contains(AutoStart.BackgroundArg))
            _main.Show();
    }

    /// <summary>Waits for the tray menu to close so it isn't in the screenshot.</summary>
    Action AfterTrayMenu(Action action) => () =>
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    };

    void ChangeHotkeys()
    {
        var values = HotkeysDialog.Show(_hotkeys.Select(h => (h.Label, h.Get())).ToList());
        if (values == null) return;

        var failed = new List<string>();
        for (int i = 0; i < _hotkeys.Count; i++)
        {
            var binding = _hotkeys[i];
            var old = binding.Get();
            if (values[i] == old) continue;
            if (binding.Register(values[i]))
            {
                binding.Set(values[i]);
            }
            else
            {
                binding.Register(old);
                failed.Add($"{binding.Label} {values[i]}");
            }
            _tray!.SetShortcutText(binding.Command, binding.Get());
        }
        _main!.RequestSave();
        _screenTools!.SaveSettings();

        if (failed.Count > 0)
            MessageBox.Show($"以下热键已被其他程序占用，未更改：\n{string.Join("\n", failed)}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        foreach (var binding in _hotkeys) binding.Hotkey.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        Log.Info("Exited");
        base.OnExit(e);
    }
}
