using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// Snapshot of window/control rectangles for hover-to-select in the screenshot overlay.
/// Rects are in screenshot image coordinates.
/// </summary>
public sealed class WindowFinder
{
    sealed class TopWindow
    {
        public IntPtr Handle;
        public Int32Rect Bounds;
        public List<Int32Rect>? Children;
    }

    readonly List<TopWindow> _windows = new();
    readonly int _originX, _originY;

    WindowFinder(int originX, int originY)
    {
        _originX = originX;
        _originY = originY;
    }

    /// <summary>Call before the overlay is shown so it isn't part of the snapshot.</summary>
    public static WindowFinder Snapshot(ScreenShot shot)
    {
        var finder = new WindowFinder(shot.X, shot.Y);
        // EnumWindows walks top-level windows in Z order, topmost first
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd) && finder.FrameBounds(hwnd) is { } bounds)
                finder._windows.Add(new TopWindow { Handle = hwnd, Bounds = bounds });
            return true;
        }, IntPtr.Zero);
        return finder;
    }

    /// <summary>Screen bounds of the foreground window, or null for the desktop, the taskbar or this app.</summary>
    public static System.Drawing.Rectangle? ForegroundBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsCandidate(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return null;
        var name = new System.Text.StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        var bounds = new WindowFinder(0, 0).FrameBounds(hwnd);
        return bounds is { } b ? new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height) : null;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);

    /// <summary>The smallest known control or window under the point, or null.</summary>
    public Int32Rect? Hit(int x, int y)
    {
        var top = _windows.FirstOrDefault(w => Contains(w.Bounds, x, y));
        if (top == null) return null;

        top.Children ??= ChildRects(top);
        Int32Rect best = top.Bounds;
        foreach (var child in top.Children)
        {
            if (Contains(child, x, y) && Area(child) < Area(best)) best = child;
        }
        return best;
    }

    List<Int32Rect> ChildRects(TopWindow top)
    {
        var list = new List<Int32Rect>();
        EnumChildWindows(top.Handle, (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var r) && ToImage(r) is { } rect)
            {
                // Children may extend past their scrolled/clipped parent
                if (Intersect(rect, top.Bounds) is { } clipped) list.Add(clipped);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        // UWP apps keep invisible "cloaked" windows around
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        long exStyle = GetExStyle(hwnd);
        return (exStyle & WS_EX_TRANSPARENT) == 0;
    }

    Int32Rect? FrameBounds(IntPtr hwnd)
    {
        // The extended frame excludes the invisible resize borders of Win10/11 windows
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out r))
            return null;
        return ToImage(r);
    }

    Int32Rect? ToImage(RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 4 || h < 4) return null;
        return new Int32Rect(r.Left - _originX, r.Top - _originY, w, h);
    }

    static bool Contains(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    static long Area(Int32Rect r) => (long)r.Width * r.Height;

    static Int32Rect? Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right - left >= 4 && bottom - top >= 4 ? new Int32Rect(left, top, right - left, bottom - top) : null;
    }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int DWMWA_CLOAKED = 14;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    // 32-bit user32 has no GetWindowLongPtr export
    static long GetExStyle(IntPtr hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong(hwnd, GWL_EXSTYLE);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
