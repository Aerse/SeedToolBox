using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SeedToolBox.Core;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

/// <summary>One address line of the hosts file.</summary>
sealed class HostEntry : ObservableObject
{
    bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    string _ip = "", _hosts = "", _comment = "", _warning = "";
    public string Ip { get => _ip; set => Set(ref _ip, value.Trim()); }
    public string Hosts { get => _hosts; set => Set(ref _hosts, Regex.Replace(value.Trim(), @"\s+", " ")); }
    public string Comment { get => _comment; set => Set(ref _comment, value.Trim()); }
    /// <summary>Duplicate or conflicting host names; not part of the file.</summary>
    public string Warning { get => _warning; set => Set(ref _warning, value); }
    /// <summary>Line index in the file.</summary>
    public int Line { get; set; }
}

/// <summary>Views and edits the system hosts file. Saving copies the file in with an elevated command.</summary>
sealed partial class HostsPage : DockPanel
{
    static readonly string HostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
    static readonly Regex EntryLine = new(@"^(?<off>\s*#\s*)?(?<ip>(?:\d{1,3}\.){3}\d{1,3}|[0-9a-fA-F:]*:[0-9a-fA-F:.%\w]*)\s+(?<hosts>[^#]+?)\s*(?:#\s*(?<comment>.*))?$");

    readonly ObservableCollection<HostEntry> _entries = new();
    readonly ListView _list = new();
    readonly TextBox _raw = Ui.Area();
    readonly TextBox _ip = Ui.Field(120);
    readonly TextBox _host = Ui.Field(200);
    readonly TextBox _comment = Ui.Field(120);
    readonly TextBox _filter = Ui.Field(160);
    readonly RadioButton _listMode = new() { Content = "列表", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly RadioButton _rawMode = new() { Content = "文本", Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = Ui.Status();
    readonly Grid _body = new();

    string[] _lines = Array.Empty<string>();
    Encoding _encoding = new UTF8Encoding(false);
    string _newline = "\r\n";
    bool _dirty;

    public HostsPage()
    {
        var header = Ui.Header("Hosts 管理", HostsPath + "（保存时需要管理员权限，会自动刷新 DNS 缓存）");
        var toolbar = Ui.Row(
            _listMode, _rawMode,
            Ui.Button("保存", Save, accent: true),
            Ui.Button("重新载入", () => { if (ConfirmDiscard()) Load(); }),
            Ui.Button("打开所在文件夹", () => ProcessLauncher.OpenLocation(HostsPath)),
            Ui.Button("从备份恢复…", RestoreBackup),
            Ui.Label("筛选"), _filter);
        var profiles = BuildProfiles();

        var add = Ui.Row(Ui.Label("IP"), _ip, Ui.Label("", 12), Ui.Label("域名"), _host, Ui.Label("", 12), Ui.Label("备注"), _comment, Ui.Label("", 12),
            Ui.Button("添加", Add), Ui.Button("删除选中", Remove));
        _ip.Text = "127.0.0.1";

        BuildList();
        var listPanel = new DockPanel();
        SetDock(add, Dock.Top);
        listPanel.Children.Add(add);
        listPanel.Children.Add(_list);
        _body.Children.Add(listPanel);
        _body.Children.Add(_raw);
        _raw.Visibility = Visibility.Collapsed;
        _raw.TextChanged += (_, _) => { if (_raw.IsKeyboardFocusWithin) _dirty = true; };

        _listMode.Checked += (_, _) => SwitchMode(false);
        _rawMode.Checked += (_, _) => SwitchMode(true);
        _filter.TextChanged += (_, _) => CollectionViewSource.GetDefaultView(_entries).Refresh();

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(profiles, Dock.Top);
        SetDock(_status, Dock.Bottom);
        SetDock(_warnings, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(profiles);
        Children.Add(_status);
        Children.Add(_warnings);
        Children.Add(_body);
        Load();
    }

    void BuildList()
    {
        _list.ItemsSource = _entries;
        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["ControlBorderBrush"];
        CollectionViewSource.GetDefaultView(_entries).Filter = o =>
        {
            var f = _filter.Text.Trim();
            if (f.Length == 0) return true;
            var e = (HostEntry)o;
            return e.Ip.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                || e.Hosts.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                || e.Comment.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
        };

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(HostEntry.Enabled)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "启用", Width = 56, CellTemplate = new DataTemplate { VisualTree = check } });
        view.Columns.Add(new GridViewColumn { Header = "IP", Width = 150, CellTemplate = EditableCell(nameof(HostEntry.Ip)) });
        view.Columns.Add(new GridViewColumn { Header = "域名", Width = 280, CellTemplate = EditableCell(nameof(HostEntry.Hosts)) });
        view.Columns.Add(new GridViewColumn { Header = "备注", Width = 200, CellTemplate = EditableCell(nameof(HostEntry.Comment)) });
        view.Columns.Add(new GridViewColumn { Header = "提示", Width = 220, DisplayMemberBinding = new Binding(nameof(HostEntry.Warning)) });
        _list.View = view;
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ListTools.Sortable(_list, ("启用", nameof(HostEntry.Enabled)), ("IP", nameof(HostEntry.Ip)), ("域名", nameof(HostEntry.Hosts)), ("备注", nameof(HostEntry.Comment)));
    }

    static readonly DependencyProperty ToggleButton_IsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    void Load()
    {
        try
        {
            var text = File.Exists(HostsPath) ? TextFiles.Read(HostsPath, out _encoding) : "";
            _newline = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            _lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            ParseEntries();
            _raw.Text = text;
            _dirty = false;
            Ui.SetStatus(_status, $"已载入 {_entries.Count} 条记录，其中启用 {_entries.Count(e => e.Enabled)} 条");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    void ParseEntries()
    {
        foreach (var e in _entries) e.PropertyChanged -= OnEntryChanged;
        _entries.Clear();
        for (int i = 0; i < _lines.Length; i++)
        {
            var m = EntryLine.Match(_lines[i]);
            if (!m.Success) continue;
            var entry = new HostEntry
            {
                Enabled = !m.Groups["off"].Success,
                Ip = m.Groups["ip"].Value,
                Hosts = m.Groups["hosts"].Value,
                Comment = m.Groups["comment"].Value,
                Line = i,
            };
            entry.PropertyChanged += OnEntryChanged;
            _entries.Add(entry);
        }
        CheckConflicts();
    }

    void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostEntry.Warning)) return;
        var entry = (HostEntry)sender!;
        if (e.PropertyName == nameof(HostEntry.Ip) && !EntryLine.IsMatch(entry.Ip + " x"))
            Ui.SetStatus(_status, $"IP 地址格式不正确：{entry.Ip}，保存后这一行不再被识别为记录", true);
        if (entry.Hosts.Contains('#')) entry.Hosts = entry.Hosts.Replace("#", "");
        CheckConflicts();
        _lines[entry.Line] = Format(entry);
        _dirty = true;
        if (e.PropertyName != nameof(HostEntry.Ip) || EntryLine.IsMatch(entry.Ip + " x")) Ui.SetStatus(_status, "有未保存的修改");
    }

    static string Format(HostEntry e) =>
        (e.Enabled ? "" : "# ") + e.Ip + "\t" + e.Hosts + (e.Comment.Length > 0 ? "\t# " + e.Comment : "");

    void SwitchMode(bool raw)
    {
        if (raw)
        {
            _raw.Text = string.Join(_newline, _lines);
        }
        else
        {
            _lines = _raw.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            ParseEntries();
        }
        _raw.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        ((UIElement)_body.Children[0]).Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
    }

    void Add()
    {
        var ip = _ip.Text.Trim();
        var host = _host.Text.Trim();
        if (!EntryLine.IsMatch(ip + " x")) { Ui.SetStatus(_status, "IP 地址格式不正确", true); return; }
        if (host.Length == 0 || host.Contains('#')) { Ui.SetStatus(_status, "请输入域名", true); return; }
        var entry = new HostEntry { Enabled = true, Ip = ip, Hosts = Regex.Replace(host, @"\s+", " "), Comment = _comment.Text.Trim() };
        // Drop trailing blank lines so the new entry sits right after the last one
        var lines = _lines.ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        entry.Line = lines.Count;
        lines.Add(Format(entry));
        lines.Add("");
        _lines = lines.ToArray();
        entry.PropertyChanged += OnEntryChanged;
        _entries.Add(entry);
        CheckConflicts();
        _list.ScrollIntoView(entry);
        _host.Clear();
        _comment.Clear();
        _dirty = true;
        Ui.SetStatus(_status, $"已添加 {entry.Hosts}，保存后生效");
    }

    void Remove()
    {
        var selected = _list.SelectedItems.Cast<HostEntry>().ToList();
        if (selected.Count == 0) { Ui.SetStatus(_status, "请先选择要删除的记录", true); return; }
        var remove = new System.Collections.Generic.HashSet<int>(selected.Select(e => e.Line));
        _lines = _lines.Where((_, i) => !remove.Contains(i)).ToArray();
        ParseEntries();
        _dirty = true;
        Ui.SetStatus(_status, $"已删除 {selected.Count} 条，保存后生效");
    }

    bool ConfirmDiscard() => !_dirty || MessageBox.Show(Window.GetWindow(this), "放弃未保存的修改？", "Hosts 管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    void Save()
    {
        var text = _rawMode.IsChecked == true ? _raw.Text : string.Join(_newline, _lines);
        var temp = Path.Combine(Path.GetTempPath(), "SeedToolBox.hosts");
        try
        {
            // Keep the previous file in case the edit breaks something
            var backups = Path.Combine(AppPaths.Data, "hosts-backup");
            Directory.CreateDirectory(backups);
            if (File.Exists(HostsPath)) File.Copy(HostsPath, Path.Combine(backups, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}"), true);
            foreach (var old in new DirectoryInfo(backups).GetFiles("hosts-*").OrderByDescending(f => f.Name).Skip(20)) old.Delete();

            File.WriteAllBytes(temp, TextFiles.Encode(text, _encoding));
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
            return;
        }

        if (!TryWriteDirect(temp) && !WriteElevated(temp)) return;
        _dirty = false;
        Load();
        Ui.SetStatus(_status, $"已保存并刷新 DNS 缓存（{DateTime.Now:HH:mm:ss}），原文件已备份到 Data\\hosts-backup");
    }

    /// <summary>Works when the app already runs as administrator.</summary>
    static bool TryWriteDirect(string temp)
    {
        try
        {
            File.Copy(temp, HostsPath, true);
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    bool WriteElevated(string temp)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c copy /y \"{temp}\" \"{HostsPath}\" && ipconfig /flushdns")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            if (p.ExitCode != 0) { Ui.SetStatus(_status, $"保存失败（错误码 {p.ExitCode}），文件可能被安全软件锁定", true); return false; }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Ui.SetStatus(_status, "已取消：需要管理员权限才能保存", true);
            return false;
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
            return false;
        }
    }
}
