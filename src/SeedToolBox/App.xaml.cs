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
    ToolboxWindow? _toolbox;
    LauncherData? _data;
    ScreenToolService? _screenTools;
    Clips.ClipboardHistory? _clipboard;
    Clips.ClipboardWindow? _clipboardWindow;
    ModuleManager? _modules;
    const string ModuleHotkeysFile = "module-hotkeys";
    Action? _saveModuleHotkeys;
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
        var main = _main;
        var toolbox = _toolbox = new ToolboxWindow(data, main.RequestSave, RunHidden);
        main.ToolboxRequested += () => toolbox.ShowAndActivate();
        main.ToolSearch = toolbox.SearchTools;
        main.AppHotkeyOwner = v => _hotkeys.FirstOrDefault(h => h.Get() == v)?.Label;

        try { AutoStart.Refresh(); }
        catch (Exception ex) { Log.Error("Failed to refresh autostart entry", ex); }

        _tray = new TrayIcon();
        _tray.ToggleWindowRequested += _main.ToggleVisibility;
        _tray.ShowWindowRequested += _main.ShowAndActivate;
        _tray.SettingsRequested += () => toolbox.ShowAndActivate("settings");
        _tray.ExitRequested += ExitApp;

        var screen = _screenTools = new ScreenToolService(settings);
        _tray.AddCommand("screenshot", "截图", AfterTrayMenu(screen.Screenshot));
        _tray.AddCommand("ocr", "识别文字", AfterTrayMenu(screen.RecognizeText));
        _tray.AddCommand("record", "录屏", AfterTrayMenu(screen.Record));
        toolbox.AddTool("screenshot", "\uE7A8", "截图", screen.Screenshot);
        toolbox.AddTool("ocr", "\uE8D2", "识字", screen.RecognizeText);
        toolbox.AddTool("qr", "\uED14", "扫码", screen.RecognizeQrCodes);
        toolbox.AddTool("qrscreen", "\uE740", "全屏扫码", screen.ScanQrCodes);
        toolbox.AddTool("qrgen", "\uE72D", "生成二维码", screen.GenerateQrCode, hide: false);
        toolbox.AddTool("color", "\uEF3C", "取色", screen.PickColor);
        toolbox.AddTool("ruler", "\uED5E", "标尺", screen.Ruler);
        toolbox.AddTool("record", "\uE7C8", "录屏", screen.Record);
        var clipboard = _clipboard = new Clips.ClipboardHistory(settings);
        Action showClipboard = () => (_clipboardWindow ??= new Clips.ClipboardWindow(clipboard)).ShowAtCursor();
        _tray.AddCommand("clipboard", "剪贴板历史", showClipboard);
        _tray.AddCommand("toolbox", "工具箱", () => toolbox.ShowAndActivate());
        toolbox.AddPage("clipboard", "\uE77F", "剪贴板", () => new Clips.ClipboardPage(clipboard));
        var settingsOptions = new SettingsPage.Options
        {
            SizeLocked = () => data.Window.SizeLocked,
            SetSizeLocked = _main.SetSizeLocked,
            AutoStart = () => AutoStart.IsEnabled,
            SetAutoStart = SetAutoStart,
            Hotkeys = () => _hotkeys.Select(h => (h.Label, h.Get())).ToList(),
            SetHotkey = SetHotkey,
            Record = screen.Settings.Record,
            SaveRecord = screen.SaveSettings,
            Clipboard = clipboard.Settings,
            SaveClipboard = clipboard.SaveSettings,
            Backup = BackupData,
            Restore = RestoreData,
            OpenData = () => ProcessLauncher.OpenLocation(AppPaths.Data),
            OpenApp = () => ProcessLauncher.OpenLocation(ProcessLauncher.ExePath),
        };
        toolbox.AddFooterPage("settings", "\uE713", "设置", () => new SettingsPage(settingsOptions));

        _hotkeys.Add(new HotkeyBinding(TrayIcon.ShowWindowCommand, "呼出主窗口", () => data.Hotkey, v => data.Hotkey = v, main.ToggleFromHotkey));
        _hotkeys.Add(new HotkeyBinding("screenshot", "截图", () => screen.Settings.ScreenshotHotkey, v => screen.Settings.ScreenshotHotkey = v, () => RunHidden(screen.Screenshot)));
        _hotkeys.Add(new HotkeyBinding("color", "取色", () => screen.Settings.ColorPickerHotkey, v => screen.Settings.ColorPickerHotkey = v, () => RunHidden(screen.PickColor)));
        _hotkeys.Add(new HotkeyBinding("ruler", "屏幕标尺", () => screen.Settings.RulerHotkey, v => screen.Settings.RulerHotkey = v, () => RunHidden(screen.Ruler)));
        _hotkeys.Add(new HotkeyBinding("record", "录屏", () => screen.Settings.RecordHotkey, v => screen.Settings.RecordHotkey = v, () => RunHidden(screen.Record)));
        _hotkeys.Add(new HotkeyBinding("ocr", "识别文字", () => screen.Settings.OcrHotkey, v => screen.Settings.OcrHotkey = v, () => RunHidden(screen.RecognizeText)));
        _hotkeys.Add(new HotkeyBinding("qr", "识别二维码", () => screen.Settings.QrHotkey, v => screen.Settings.QrHotkey = v, () => RunHidden(screen.RecognizeQrCodes)));
        _hotkeys.Add(new HotkeyBinding("clipboard", "剪贴板历史", () => clipboard.Settings.Hotkey, v => clipboard.Settings.Hotkey = v, showClipboard));

        var taken = new List<string>();
        foreach (var binding in _hotkeys)
        {
            if (!binding.Register(binding.Get())) taken.Add($"{binding.Label} {binding.Get()}");
            _tray.SetShortcutText(binding.Command, binding.Get());
            toolbox.SetToolHotkey(binding.Command, binding.Get());
        }
        if (taken.Count > 0)
            _tray.ShowMessage($"热键已被其他程序占用：{string.Join("、", taken)}。可在「设置」页中更换");
        if (main.RegisterItemHotkeys() is { Count: > 0 } itemTaken)
            _tray.ShowMessage($"启动项快捷键已被占用：{string.Join("、", itemTaken)}");

        var moduleHotkeys = settings.Load<Dictionary<string, string>>(ModuleHotkeysFile);
        _saveModuleHotkeys = () => settings.Save(ModuleHotkeysFile, moduleHotkeys);
        _modules = new ModuleManager(new AppHost(_tray, settings, Dispatcher, new AppHost.Callbacks
        {
            AddPage = toolbox.AddGroupPage,
            AddSettingsSection = (title, create) => settingsOptions.Extra.Add((title, create)),
            AddHotkey = (id, label, fallback, pressed) =>
            {
                var binding = new HotkeyBinding("module:" + id, label,
                    () => moduleHotkeys.TryGetValue(id, out var v) ? v : fallback, v => moduleHotkeys[id] = v, pressed);
                _hotkeys.Add(binding);
                if (!binding.Register(binding.Get()) && binding.Get().Length > 0)
                    _tray.ShowMessage($"热键已被其他程序占用：{label} {binding.Get()}。可在「设置」页中更换");
            },
        }));
        _modules.LoadAll();

        try { DataBackup.AutoBackup(); }
        catch (Exception ex) { Log.Error("Automatic backup failed", ex); }

        if (!e.Args.Contains(AutoStart.BackgroundArg))
            _main.Show();
    }

    /// <summary>Starts a screen tool, first hiding the app's windows so they stay out of the capture.</summary>
    void RunHidden(Action action)
    {
        bool hid = false;
        foreach (var window in new Window?[] { _main, _toolbox })
        {
            if (window is not { IsVisible: true } || window.WindowState == WindowState.Minimized) continue;
            window.Hide();
            hid = true;
        }
        if (!hid) { action(); return; }
        // Give the windows time to disappear from the screen
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>Waits for the tray menu to close so it isn't in the screenshot.</summary>
    Action AfterTrayMenu(Action action) => () =>
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    };

    /// <summary>Changes one hotkey, keeping the old one if the new one can't be registered.</summary>
    string? SetHotkey(int index, string value)
    {
        var binding = _hotkeys[index];
        var clash = value.Length > 0 ? _hotkeys.FirstOrDefault(h => h != binding && h.Get() == value) : null;
        if (clash != null) return $"{value} 已用于「{clash.Label}」";
        if (_main!.ItemHotkeyOwner(value) is { } item) return $"{value} 已用于启动项「{item}」";
        var old = binding.Get();
        if (!binding.Register(value))
        {
            binding.Register(old);
            return $"{value} 已被其他程序占用，未更改";
        }
        binding.Set(value);
        _tray!.SetShortcutText(binding.Command, value);
        _toolbox!.SetToolHotkey(binding.Command, value);
        _main!.RequestSave();
        _screenTools!.SaveSettings();
        _clipboard?.SaveSettings();
        _saveModuleHotkeys?.Invoke();
        return null;
    }

    void BackupData()
    {
        _main!.PrepareSave();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "备份数据",
            FileName = $"SeedToolBox备份_{DateTime.Now:yyyyMMdd_HHmmss}",
            Filter = "备份文件|*.zip",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            DataBackup.Create(dialog.FileName);
            _tray!.ShowMessage("备份完成");
        }
        catch (Exception ex)
        {
            Log.Error($"Backup to {dialog.FileName} failed", ex);
            MessageBox.Show($"备份失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void RestoreData()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "恢复数据",
            Filter = "备份文件|*.zip",
            InitialDirectory = System.IO.Directory.Exists(DataBackup.Folder) ? DataBackup.Folder : "",
        };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("恢复后当前的启动项和设置会被备份中的内容替换，程序将自动重启。\n当前数据会先另存一份到 Data\\Backups。是否继续？",
                "恢复数据", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        try
        {
            _main!.PrepareSave();
            System.IO.Directory.CreateDirectory(DataBackup.Folder);
            DataBackup.Create(System.IO.Path.Combine(DataBackup.Folder, $"恢复前_{DateTime.Now:yyyyMMdd_HHmmss}.zip"));
            DataBackup.Restore(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error($"Restore from {dialog.FileName} failed", ex);
            MessageBox.Show($"恢复失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Nothing may be saved from here on, or the restored files would be overwritten with the old data
        Log.Info($"Restored from {dialog.FileName}, restarting");
        _main.PrepareExit(false);
        _toolbox?.PrepareExit();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
        System.Diagnostics.Process.Start(ProcessLauncher.ExePath);
        Shutdown();
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
        _toolbox?.PrepareExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _modules?.ShutdownAll();
        foreach (var binding in _hotkeys) binding.Hotkey.Dispose();
        _clipboard?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        Log.Info("Exited");
        base.OnExit(e);
    }
}
