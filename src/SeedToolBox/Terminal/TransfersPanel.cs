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
    /// <summary>Starts the same transfer again (a new entry); null when it cannot be repeated.</summary>
    public Action? Retry;
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
            if (now - _lastTime >= 0.5) { _speed = (_done - _lastDone) / (now - _lastTime); _lastDone = _done; _lastTime = now; Changed(nameof(Speed)); Changed(nameof(Detail)); }
            Changed(nameof(Percent));
            Changed(nameof(Size));
            Changed(nameof(Detail));
        }
    }

    public double Percent => Total > 0 ? 100.0 * _done / Total : State == "完成" ? 100 : 0;
    public string Direction => Upload ? "↑" : "↓";
    public string Title => Direction + " " + Name;
    public string Detail => string.Join(" · ", new[] { Size, Speed, Server }.Where(x => x.Length > 0));
    /// <summary>Red for failures, green when done, the dim text colour otherwise.</summary>
    public object? StateBrush => _state.StartsWith("失败") ? System.Windows.Media.Brushes.IndianRed
        : _state == "完成" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0xBF, 0x6E))
        : Application.Current.TryFindResource("SecondaryTextBrush");
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
            Changed(nameof(State)); Changed(nameof(Speed)); Changed(nameof(Percent)); Changed(nameof(Detail)); Changed(nameof(StateBrush));
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

    /// <summary>Quoted for the Windows command line; trailing backslashes are doubled so they don't escape the closing quote, and C:\ stays the root.</summary>
    static string Arg(string a) => "\"" + a + new string('\\', a.Length - a.TrimEnd('\\').Length) + "\"";

    public static System.Threading.Tasks.Task RunTar(CancellationToken cancel, params string[] args) => System.Threading.Tasks.Task.Run(() =>
    {
        var psi = new ProcessStartInfo(LocalTar!, string.Join(" ", args.Select(Arg)))
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
    /// <summary>Our own scratch folder (edit copies, archives); downloads there are not "real" downloads.</summary>
    public static readonly string Cache = Path.Combine(Path.GetTempPath(), "SeedToolBox");
    public static readonly ObservableCollection<Transfer> All = new();
    /// <summary>A transfer started, finished or was removed.</summary>
    public static event Action? Changed;
    /// <summary>Where the last download went; the "open download folder" button starts here.</summary>
    public static string LastFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static void Add(Transfer t)
    {
        if (!t.Upload && t.Local.Length > 0 && !t.Local.StartsWith(Transfers.Cache, StringComparison.OrdinalIgnoreCase))
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
    readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, ItemsSource = Transfers.All, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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

        _list.ItemTemplate = CardTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Transfer t) OpenItem(t); };
        _list.ContextMenuOpening += (_, e) => { if (_list.SelectedItem is Transfer t) _list.ContextMenu = Menu(t); else e.Handled = true; };
        _list.ContextMenu = new ContextMenu();
        Children.Add(_list);

        // Subscribed only while shown, so the static event doesn't keep a closed page alive.
        Action changed = () => Dispatcher.BeginInvoke(Update);
        Loaded += (_, _) => { Transfers.Changed -= changed; Transfers.Changed += changed; Update(); };
        Unloaded += (_, _) => Transfers.Changed -= changed;
        Update();
    }

    /// <summary>Name and state, a full-width bar, then size · speed · server in small print; fits the narrow side panel.</summary>
    static DataTemplate CardTemplate()
    {
        FrameworkElementFactory Text(string path, double size, bool dim)
        {
            var t = new FrameworkElementFactory(typeof(TextBlock));
            t.SetBinding(TextBlock.TextProperty, new Binding(path));
            t.SetValue(TextBlock.FontSizeProperty, size);
            t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            if (dim) t.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            return t;
        }
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 5, 2, 6));
        var top = new FrameworkElementFactory(typeof(DockPanel));
        var state = Text(nameof(Transfer.State), 11.5, true);
        state.SetValue(DockPanel.DockProperty, Dock.Right);
        state.SetValue(FrameworkElement.MaxWidthProperty, 170.0);
        state.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        state.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Transfer.StateBrush)));
        state.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Transfer.State)));
        top.AppendChild(state);
        top.AppendChild(Text(nameof(Transfer.Title), 13, false));
        root.AppendChild(top);
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Transfer.Percent)) { Mode = BindingMode.OneWay });
        bar.SetValue(FrameworkElement.HeightProperty, 4.0);
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 3));
        root.AppendChild(bar);
        root.AppendChild(Text(nameof(Transfer.Detail), 11, true));
        return new DataTemplate { VisualTree = root };
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
        Item("重试", () => { Transfers.All.Remove(t); t.Retry!(); }, t.Retry != null && !t.Active && t.State != "完成");
        Item("取消", () => t.Cancel.Cancel(), t.Active);
        Item("从列表中移除", () => { t.Cancel.Cancel(); Transfers.All.Remove(t); Transfers.OnChanged(); });
        Item("清除已结束的", ClearFinished);
        return menu;
    }

    static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }
}
