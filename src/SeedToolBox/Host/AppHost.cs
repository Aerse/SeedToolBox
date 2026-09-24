using System;
using System.Windows;
using System.Windows.Threading;
using SeedToolBox.Core.Modules;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Host;

sealed class AppHost : IAppHost
{
    readonly TrayIcon _tray;
    readonly Dispatcher _dispatcher;
    readonly Callbacks _app;

    /// <summary>The parts of the app a module can extend.</summary>
    public sealed class Callbacks
    {
        public Action<string, string, string, string, Func<FrameworkElement>> AddPage { get; set; } = (_, _, _, _, _) => { };
        public Action<string, string, string, Action> AddHotkey { get; set; } = (_, _, _, _) => { };
        public Action<string, Func<FrameworkElement>> AddSettingsSection { get; set; } = (_, _) => { };
    }

    public AppHost(TrayIcon tray, ISettingsStore settings, Dispatcher dispatcher, Callbacks app)
    {
        _tray = tray;
        _dispatcher = dispatcher;
        _app = app;
        Settings = settings;
    }

    public void AddPage(string id, string group, string glyph, string name, Func<object> create) =>
        _app.AddPage(id, group, glyph, name, () => create() as FrameworkElement ?? new System.Windows.Controls.TextBlock { Text = $"「{name}」页面创建失败" });

    public void AddHotkey(string id, string label, string defaultHotkey, Action onPressed) =>
        _app.AddHotkey(id, label, defaultHotkey, () => Guard(label, onPressed));

    public void AddSettingsSection(string title, Func<object> create) =>
        _app.AddSettingsSection(title, () => create() as FrameworkElement ?? new System.Windows.Controls.TextBlock());

    static void Guard(string text, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error($"Module action '{text}' failed", ex);
            MessageBox.Show($"「{text}」执行失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public ISettingsStore Settings { get; }

    public void AddTrayMenuItem(string text, Action onClick, string? submenu = null) =>
        _tray.AddModuleItem(text, () =>
        {
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Log.Error($"Menu action '{text}' failed", ex);
                MessageBox.Show($"「{text}」执行失败：{ex.Message}", "SeedToolBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, submenu);

    public void RunOnUiThread(Action action) => _dispatcher.BeginInvoke(action);
}
