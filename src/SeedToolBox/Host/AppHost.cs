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

    public AppHost(TrayIcon tray, ISettingsStore settings, Dispatcher dispatcher)
    {
        _tray = tray;
        _dispatcher = dispatcher;
        Settings = settings;
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
