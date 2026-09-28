using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>One upload or download; every file panel adds to the shared <see cref="Transfers.All"/> list.</summary>
sealed class Transfer : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Local = "", Remote = "";
    public bool Upload;
    public long Total;
    public DateTime Started = DateTime.Now;
    public CancellationTokenSource Cancel = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _done, _lastDone;
    double _lastTime, _speed;
    string _state = "等待";

    public long Done
    {
        get => _done;
        set
        {
            _done = value;
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _lastTime >= 0.5) { _speed = (_done - _lastDone) / (now - _lastTime); _lastDone = _done; _lastTime = now; Changed(nameof(Speed)); }
            Changed(nameof(Percent));
            Changed(nameof(Size));
        }
    }

    public double Percent => Total > 0 ? 100.0 * _done / Total : State == "完成" ? 100 : 0;
    public string Direction => Upload ? "↑" : "↓";
    public string Size => $"{Ui.FormatSize(_done)} / {Ui.FormatSize(Total)}";
    public bool Active => _state is "上传中" or "下载中" or "等待" or "压缩中" or "解压中";
    public string Speed => Active && _speed > 0 ? Ui.FormatSize((long)_speed) + "/s" : Active ? "" : Average;
    string Average => _state == "完成" && _clock.Elapsed.TotalSeconds > 0.2 ? "平均 " + Ui.FormatSize((long)(Total / _clock.Elapsed.TotalSeconds)) + "/s" : "";

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            if (!Active) _clock.Stop();
            Changed(nameof(State)); Changed(nameof(Speed)); Changed(nameof(Percent));
            Transfers.OnChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Folders travel as one tar.gz: Windows 10+ ships bsdtar as tar.exe.</summary>
static class Packer
{
    public static readonly string? LocalTar = File.Exists(Path.Combine(Environment.SystemDirectory, "tar.exe")) ? Path.Combine(Environment.SystemDirectory, "tar.exe") : null;

    public static string TempArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SeedToolBox", "pack");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N").Substring(0, 12) + ".tar.gz");
    }

    public static void Delete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>Single-quoted for a POSIX shell.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Error(string output) =>
        string.Join(" ", output.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith("__rc=")).Take(3)) is { Length: > 0 } e ? e : "tar 出错";

    public static System.Threading.Tasks.Task RunTar(CancellationToken cancel, params string[] args) => System.Threading.Tasks.Task.Run(() =>
    {
        var psi = new ProcessStartInfo(LocalTar!, string.Join(" ", args.Select(a => "\"" + a.TrimEnd('\\') + "\"")))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEndAsync();
        while (!p.WaitForExit(200))
            if (cancel.IsCancellationRequested) { try { p.Kill(); } catch (InvalidOperationException) { } cancel.ThrowIfCancellationRequested(); }
        if (p.ExitCode != 0) throw new InvalidOperationException("本地 tar 出错：" + error.Result.Trim());
    });
}

static class Transfers
{
    public static readonly ObservableCollection<Transfer> All = new();
    /// <summary>A transfer started, finished or was removed.</summary>
    public static event Action? Changed;
    /// <summary>Where the last download went; the "open download folder" button starts here.</summary>
    public static string LastFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static void Add(Transfer t)
    {
        if (!t.Upload && t.Local.Length > 0 && !t.Local.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            LastFolder = Path.GetDirectoryName(t.Local) ?? LastFolder;
        All.Insert(0, t);
        OnChanged();
    }

    public static int ActiveCount => All.Count(t => t.Active);

    public static void OnChanged() => Changed?.Invoke();

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Process.Start("explorer.exe", "\"" + folder + "\"");
    }

    public static void Reveal(string file)
    {
        if (File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
        else OpenFolder(Path.GetDirectoryName(file) ?? LastFolder);
    }
}

/// <summary>The 传输 tab: every upload and download, with progress, speed and shortcuts to the files.</summary>
sealed class TransfersPanel : DockPanel
{
    readonly ListView _list = new() { BorderThickness = new Thickness(0), ItemsSource = Transfers.All };
    readonly TextBlock _status = Ui.Status();

    public TransfersPanel()
    {
        var bar = new DockPanel { Margin = new Thickness(-4, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ApiUi.Icon("", "打开下载目录", () => Transfers.OpenFolder(Transfers.LastFolder)));
        buttons.Children.Add(ApiUi.Icon("", "全部取消", () => { foreach (var t in Transfers.All.Where(t => t.Active)) t.Cancel.Cancel(); }));
        buttons.Children.Add(ApiUi.Icon("", "清除已结束的", ClearFinished));
        bar.Children.Add(buttons);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        var g = new GridView();
        g.Columns.Add(Col("", nameof(Transfer.Direction), 22));
        g.Columns.Add(Col("文件", nameof(Transfer.Name), 150));
        g.Columns.Add(new GridViewColumn { Header = "进度", Width = 90, CellTemplate = ProgressTemplate() });
        g.Columns.Add(Col("大小", nameof(Transfer.Size), 130));
        g.Columns.Add(Col("速度", nameof(Transfer.Speed), 100));
        g.Columns.Add(Col("状态", nameof(Transfer.State), 80));
        g.Columns.Add(Col("服务器", nameof(Transfer.Server), 110));
        _list.View = g;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Transfer t) OpenItem(t); };
        _list.ContextMenuOpening += (_, e) => { if (_list.SelectedItem is Transfer t) _list.ContextMenu = Menu(t); else e.Handled = true; };
        _list.ContextMenu = new ContextMenu();
        Children.Add(_list);

        Transfers.Changed += () => Dispatcher.BeginInvoke(Update);
        Update();
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    static DataTemplate ProgressTemplate()
    {
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Transfer.Percent)) { Mode = BindingMode.OneWay });
        bar.SetValue(FrameworkElement.HeightProperty, 6.0);
        bar.SetValue(FrameworkElement.WidthProperty, 76.0);
        return new DataTemplate { VisualTree = bar };
    }

    void Update()
    {
        var active = Transfers.ActiveCount;
        Ui.SetStatus(_status, Transfers.All.Count == 0 ? "还没有传输。在「文件」里上传或下载后会显示在这里。"
            : active > 0 ? $"{active} 个进行中 / 共 {Transfers.All.Count} 个" : $"共 {Transfers.All.Count} 个，全部结束 · 双击打开文件");
    }

    static void ClearFinished()
    {
        foreach (var t in Transfers.All.Where(t => !t.Active).ToList()) Transfers.All.Remove(t);
        Transfers.OnChanged();
    }

    static void OpenItem(Transfer t)
    {
        if (t.Upload || t.State != "完成") { Transfers.Reveal(t.Local); return; }
        try { Process.Start(new ProcessStartInfo(t.Local) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { Transfers.Reveal(t.Local); }
    }

    ContextMenu Menu(Transfer t)
    {
        var menu = new ContextMenu();
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        var exists = File.Exists(t.Local);
        Item("打开文件", () => OpenItem(t), exists);
        Item("在文件夹中显示", () => Transfers.Reveal(t.Local), t.Local.Length > 0);
        Item("打开下载目录", () => Transfers.OpenFolder(Transfers.LastFolder));
        menu.Items.Add(new Separator());
        Item("复制本地路径", () => Copy(t.Local), t.Local.Length > 0);
        Item("复制远程路径", () => Copy(t.Remote), t.Remote.Length > 0);
        menu.Items.Add(new Separator());
        Item("取消", () => t.Cancel.Cancel(), t.Active);
        Item("从列表中移除", () => { t.Cancel.Cancel(); Transfers.All.Remove(t); Transfers.OnChanged(); });
        Item("清除已结束的", ClearFinished);
        return menu;
    }

    static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }
}
