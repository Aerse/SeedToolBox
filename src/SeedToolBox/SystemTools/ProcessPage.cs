using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.DevTools;
using SeedToolBox.Launcher;

namespace SeedToolBox.SystemTools;

sealed class ProcessEntry
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public long Memory { get; set; }
    public string MemoryText => Ui.FormatSize(Memory);
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
}

sealed class ProcessPage : DockPanel
{
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase) { "csrss", "wininit", "winlogon", "smss", "services", "lsass", "svchost", "dwm", "fontdrvhost", "Registry", "Memory Compression" };

    readonly ObservableCollection<ProcessEntry> _items = new();
    readonly ListView _list = new();
    readonly TextBox _filter = Ui.Field(220);
    readonly CheckBox _autoRefresh = new() { Content = "自动刷新（3 秒）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly TextBlock _status = Ui.Status();
    List<ProcessEntry> _all = new();
    bool _loading;

    public ProcessPage()
    {
        var header = Ui.Header("进程", "查看正在运行的进程和内存占用，结束进程或打开文件位置");
        _filter.ToolTip = "进程名、PID、窗口标题或路径";
        _filter.TextChanged += (_, _) => ApplyFilter();
        _timer.Tick += (_, _) => { if (!_loading) Refresh(); };
        _autoRefresh.Click += (_, _) => { if (_autoRefresh.IsChecked == true) _timer.Start(); else _timer.Stop(); };
        var toolbar = Ui.Row(Ui.Label("筛选"), _filter, Ui.Label("", 12), _autoRefresh, Ui.Button("刷新", Refresh), Ui.Button("结束进程", Kill), Ui.Button("打开位置", OpenLocation),
            ListTools.ExportButton(_list, _status, "进程.csv"));

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        void Column(string h, string path, double width) => view.Columns.Add(new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new Binding(path) });
        Column("名称", nameof(ProcessEntry.Name), 180);
        Column("PID", nameof(ProcessEntry.Pid), 70);
        Column("内存", nameof(ProcessEntry.MemoryText), 90);
        Column("窗口标题", nameof(ProcessEntry.Title), 220);
        Column("路径", nameof(ProcessEntry.Path), 360);
        _list.View = view;
        ListTools.Sortable(_list);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("结束进程", Kill));
        menu.Items.Add(MenuItem("打开文件位置", OpenLocation));
        menu.Items.Add(MenuItem("复制路径", () => { if (_list.SelectedItem is ProcessEntry { Path.Length: > 0 } p) ScreenTools.ScreenToolService.CopyText(p.Path); }));
        _list.ContextMenu = menu;

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(_list);
        Loaded += (_, _) =>
        {
            Refresh();
            if (_autoRefresh.IsChecked == true) _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    async void Refresh()
    {
        _loading = true;
        try
        {
            _all = await Task.Run(Read);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取进程失败：" + ex.Message, true);
        }
        finally
        {
            _loading = false;
        }
    }

    static List<ProcessEntry> Read()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    list.Add(new ProcessEntry
                    {
                        Name = p.ProcessName,
                        Pid = p.Id,
                        Memory = p.WorkingSet64,
                        Title = p.MainWindowTitle,
                        Path = ImagePath(p.Id) ?? "",
                    });
                }
                catch (InvalidOperationException) { }
            }
        }
        return list;
    }

    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    void ApplyFilter()
    {
        var q = _filter.Text.Trim();
        var selected = (_list.SelectedItem as ProcessEntry)?.Pid;
        var list = _all.Where(p => q.Length == 0 || p.Pid.ToString() == q
            || p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || p.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || p.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();
        _items.Clear();
        foreach (var p in list) _items.Add(p);
        if (selected != null) _list.SelectedItem = list.FirstOrDefault(p => p.Pid == selected);
        Ui.SetStatus(_status, $"共 {list.Count} 个进程（总计 {_all.Count}），内存合计 {Ui.FormatSize(list.Sum(p => p.Memory))}  系统进程需要以管理员身份运行才能结束");
    }

    void OpenLocation()
    {
        if (_list.SelectedItem is not ProcessEntry p) { Ui.SetStatus(_status, "先选中一行", true); return; }
        if (p.Path.Length == 0) { Ui.SetStatus(_status, "无法获取这个进程的路径，可能需要管理员权限", true); return; }
        ProcessLauncher.OpenLocation(p.Path);
    }

    void Kill()
    {
        if (_list.SelectedItem is not ProcessEntry entry) { Ui.SetStatus(_status, "先选中一行", true); return; }
        if (entry.Pid <= 4 || Critical.Contains(entry.Name)) { Ui.SetStatus(_status, "这是系统进程，不能结束", true); return; }
        if (entry.Pid == Process.GetCurrentProcess().Id) { Ui.SetStatus(_status, "这是 SeedToolBox 自己", true); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"结束进程 {entry.Name}（PID {entry.Pid}）？\n未保存的数据会丢失。", "结束进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            process.Kill();
            process.WaitForExit(3000);
            Ui.SetStatus(_status, $"已结束 {entry.Name}（PID {entry.Pid}）");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            Ui.SetStatus(_status, "结束失败：" + (ex is Win32Exception { NativeErrorCode: 5 } ? "拒绝访问，请以管理员身份运行" : ex.Message), true);
            return;
        }
        Refresh();
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
