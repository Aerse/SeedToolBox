using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SeedToolBox.Host;

/// <summary>System-wide hotkey, received by a hidden message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int HotkeyId = 1;
    const uint MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly HwndSource _source;
    bool _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("SeedToolBox.Hotkey") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Replaces the current hotkey. Returns false if another program already owns the combination.</summary>
    public bool Register(Hotkey? hotkey)
    {
        Unregister();
        if (hotkey is not { } key) return true;

        // WPF ModifierKeys values match the Win32 MOD_* flags
        _registered = RegisterHotKey(_source.Handle, HotkeyId, (uint)key.Modifiers | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key.Key));
        return _registered;
    }

    void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
