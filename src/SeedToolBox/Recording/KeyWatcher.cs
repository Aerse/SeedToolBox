using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Recording;

/// <summary>
/// Watches key presses with a low-level keyboard hook while recording, so they can be drawn into the video.
/// Read-only: every event is passed on unchanged. Create and dispose on the UI thread, whose message loop runs the hook.
/// </summary>
sealed class KeyWatcher : IDisposable
{
    const long ShowTicks = 1500 * TimeSpan.TicksPerMillisecond;
    const int MaxTyped = 24;

    readonly HookProc _proc;
    readonly object _lock = new();
    IntPtr _hook;
    string _text = "";
    bool _typing;
    long _time;

    public KeyWatcher()
    {
        _proc = OnKey;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Error("Keyboard hook failed", new System.ComponentModel.Win32Exception());
    }

    /// <summary>The keys to show now, or null when nothing was pressed recently.</summary>
    public string? Current
    {
        get
        {
            lock (_lock)
                return _text.Length > 0 && DateTime.UtcNow.Ticks - _time < ShowTicks ? _text : null;
        }
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            try { Add((Keys)Marshal.ReadInt32(lParam)); }
            catch (Exception ex) { Log.Error("Key overlay failed", ex); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Add(Keys key)
    {
        if (IsModifier(key)) return;
        bool ctrl = Down(Keys.ControlKey), alt = Down(Keys.Menu), win = Down(Keys.LWin) || Down(Keys.RWin);
        bool shift = Down(Keys.ShiftKey);
        long now = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            var typed = !ctrl && !alt && !win ? Typed(key, shift) : null;
            if (typed != null)
            {
                // Plain typing runs together, like "hello"
                bool recent = _typing && now - _time < ShowTicks;
                _text = recent ? _text + typed : typed;
                if (_text.Length > MaxTyped) _text = _text.Substring(_text.Length - MaxTyped);
                _typing = true;
            }
            else
            {
                var parts = "";
                if (ctrl) parts += "Ctrl + ";
                if (alt) parts += "Alt + ";
                if (shift) parts += "Shift + ";
                if (win) parts += "Win + ";
                _text = parts + Name(key);
                _typing = false;
            }
            _time = now;
        }
    }

    static bool Down(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    static bool IsModifier(Keys key) => key is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.ControlKey
        or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    static string? Typed(Keys key, bool shift)
    {
        if (key >= Keys.A && key <= Keys.Z) return ((char)((shift ? 'A' : 'a') + (key - Keys.A))).ToString();
        if (!shift && key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return ((char)('0' + (key - Keys.NumPad0))).ToString();
        if (key == Keys.Space) return "␣";
        return null;
    }

    static string Name(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + (key - Keys.NumPad0),
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.Escape => "Esc",
        Keys.Prior => "PgUp",
        Keys.Next => "PgDn",
        Keys.Left => "←",
        Keys.Right => "→",
        Keys.Up => "↑",
        Keys.Down => "↓",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemQuestion => "/",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemPipe => "\\",
        Keys.Oemtilde => "`",
        _ => key.ToString(),
    };

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
