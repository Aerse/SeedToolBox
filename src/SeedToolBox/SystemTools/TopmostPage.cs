using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;

namespace SeedToolBox.SystemTools;

sealed class WindowEntry : ObservableObject
{
    bool _topmost;
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public string Process { get; set; } = "";
    public int Pid { get; set; }
    public bool Topmost { get => _topmost; set { if (Set(ref _topmost, value)) Raise(nameof(TopmostText)); } }
    public string TopmostText => Topmost ? "📌 置顶" : "";
}

/// <summary>Reads and changes the topmost flag of other windows with SetWindowPos only.</summary>
static class Topmost
{
    const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_CHILD = 0x40000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    static readonly HashSet<string> ShellClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>Returns false when Windows refused, typically for windows of elevated programs.</summary>
    public static bool Set(IntPtr hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) && IsTopmost(hwnd) == topmost;

    /// <summary>Toggles the foreground window. Returns a message for the user.</summary>
    public static string ToggleForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || ShellClasses.Contains(ClassName(hwnd))) return "没有可置顶的前台窗口";
        var root = GetAncestor(hwnd, 3); // GA_ROOTOWNER
        if (root != IntPtr.Zero && IsWindowVisible(root)) hwnd = root;
        bool target = !IsTopmost(hwnd);
        var title = Title(hwnd);
        if (!Set(hwnd, target)) return $"无法修改「{title}」，它可能以管理员身份运行";
        return target ? $"已置顶「{title}」" : $"已取消置顶「{title}」";
    }

    public static List<WindowEntry> List()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().Id;
        var result = new List<WindowEntry>();
        var names = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return true;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_TOPMOST) == 0) return true;
            if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0) return true;
            if (ShellClasses.Contains(ClassName(hwnd))) return true;
            var title = Title(hwnd);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!names.TryGetValue((int)pid, out var name))
            {
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; }
                catch (ArgumentException) { name = "?"; }
                names[(int)pid] = name;
            }
            result.Add(new WindowEntry { Handle = hwnd, Title = title, Pid = (int)pid, Process = pid == self ? name + "（本程序）" : name, Topmost = (ex & WS_EX_TOPMOST) != 0 });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0;

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hwnd, int index);
}

sealed class TopmostPage : DockPanel
{
    readonly ObservableCollection<WindowEntry> _windows = new();
    readonly ListView _list = new();
    readonly TextBox _filter = Ui.Field(200);
    readonly CheckBox _onlyTopmost = new() { Content = "只看已置顶", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();
    List<WindowEntry> _all = new();

    public TopmostPage()
    {
        var header = Ui.Header("窗口置顶", "让任意窗口保持在最前面；默认快捷键 Ctrl+Alt+T 切换当前窗口，可在设置中修改");
        _filter.ToolTip = "标题或进程名";
        _filter.TextChanged += (_, _) => ApplyFilter();
        _onlyTopmost.Click += (_, _) => ApplyFilter();
        var toolbar = Ui.Row(Ui.Label("筛选"), _filter, Ui.Label("", 12), _onlyTopmost,
            Ui.Button("刷新", Refresh), Ui.Button("切换置顶", () => Toggle(null), accent: true), Ui.Button("置顶", () => Toggle(true)), Ui.Button("取消置顶", () => Toggle(false)));

        _list.ItemsSource = _windows;
        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "置顶", Width = 80, DisplayMemberBinding = new Binding(nameof(WindowEntry.TopmostText)) });
        view.Columns.Add(new GridViewColumn { Header = "标题", Width = 420, DisplayMemberBinding = new Binding(nameof(WindowEntry.Title)) });
        view.Columns.Add(new GridViewColumn { Header = "进程", Width = 160, DisplayMemberBinding = new Binding(nameof(WindowEntry.Process)) });
        view.Columns.Add(new GridViewColumn { Header = "PID", Width = 70, DisplayMemberBinding = new Binding(nameof(WindowEntry.Pid)) });
        _list.View = view;
        _list.MouseDoubleClick += (_, _) => Toggle(null);
        ListTools.Sortable(_list, ("置顶", nameof(WindowEntry.Topmost)));

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(_list);
        Loaded += (_, _) => Refresh();
    }

    void Refresh()
    {
        _all = Topmost.List();
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = _filter.Text.Trim();
        _windows.Clear();
        foreach (var w in _all.Where(w => (_onlyTopmost.IsChecked != true || w.Topmost)
            && (q.Length == 0 || w.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || w.Process.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)))
            _windows.Add(w);
        Ui.SetStatus(_status, $"共 {_windows.Count} 个窗口，其中置顶 {_windows.Count(w => w.Topmost)} 个  双击切换");
    }

    void Toggle(bool? target)
    {
        var selected = _list.SelectedItems.Cast<WindowEntry>().ToList();
        if (selected.Count == 0) { Ui.SetStatus(_status, "先选中窗口", true); return; }
        int failed = 0;
        foreach (var w in selected)
        {
            bool want = target ?? !w.Topmost;
            if (Topmost.Set(w.Handle, want)) w.Topmost = want;
            else
            {
                failed++;
                w.Topmost = Topmost.IsTopmost(w.Handle);
            }
        }
        if (failed > 0) Ui.SetStatus(_status, $"{failed} 个窗口修改失败：窗口已关闭，或属于以管理员身份运行的程序", true);
        else Ui.SetStatus(_status, $"已修改 {selected.Count} 个窗口");
    }
}
