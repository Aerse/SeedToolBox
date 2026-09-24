using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SeedToolBox.Core;
using SeedToolBox.DevTools;

namespace SeedToolBox.SystemTools;

sealed class EnvVar
{
    public bool System { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Expand { get; set; }
    public string Preview => Value.Length > 200 ? Value.Substring(0, 200) + "…" : Value;
}

/// <summary>User and system environment variables, with PATH-like values edited one entry per line.</summary>
sealed class EnvVarsPage : DockPanel
{
    const string UserKey = "Environment";
    const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    static readonly string BackupDir = Path.Combine(AppPaths.Data, "env-backup");

    readonly ObservableCollection<EnvVar> _user = new(), _system = new();
    readonly ListView _userList = new(), _systemList = new();
    readonly TextBox _filter = Ui.Field(180);
    readonly TextBox _name = Ui.Field(260);
    readonly TextBox _value = Ui.Area();
    readonly CheckBox _asList = new() { Content = "按列表编辑（每行一项，保存时用 ; 连接）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _expand = new() { Content = "可扩展（值里的 %变量% 会被展开）", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _scope = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock _status = Ui.Status();

    EnvVar? _editing;
    bool _editingSystem;
    bool _busy;

    public EnvVarsPage()
    {
        var header = Ui.Header("环境变量", "编辑用户和系统环境变量；系统变量需要管理员权限，每次保存前会备份到 Data\\env-backup");
        _filter.TextChanged += (_, _) =>
        {
            CollectionViewSource.GetDefaultView(_user).Refresh();
            CollectionViewSource.GetDefaultView(_system).Refresh();
        };
        var toolbar = Ui.Row(Ui.Label("筛选"), _filter, Ui.Label("", 12), Ui.Button("刷新", Load),
            Ui.Button("新建用户变量", () => New(false)), Ui.Button("新建系统变量", () => New(true)),
            Ui.Button("打开备份文件夹", () => { Directory.CreateDirectory(BackupDir); Launcher.ProcessLauncher.Start(BackupDir); }));

        var lists = new Grid();
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var userPanel = Ui.Titled("用户变量", BuildList(_userList, _user));
        var systemPanel = Ui.Titled("系统变量", BuildList(_systemList, _system));
        Grid.SetRow(systemPanel, 2);
        lists.Children.Add(userPanel);
        lists.Children.Add(systemPanel);

        _asList.Click += (_, _) => SwitchListMode(_asList.IsChecked == true);
        var editorTop = new StackPanel();
        editorTop.Children.Add(_scope);
        editorTop.Children.Add(Ui.Row(Ui.Label("名称"), _name));
        editorTop.Children.Add(Ui.Row(_asList, _expand));
        var editorButtons = Ui.Row(Ui.Button("保存", Save, accent: true), Ui.Button("删除变量", Delete), Ui.Button("添加文件夹…", AddFolder), Ui.Button("检查路径", CheckPaths));
        editorButtons.Margin = new Thickness(0, 10, 0, 0);
        var editor = new DockPanel();
        SetDock(editorTop, Dock.Top);
        SetDock(editorButtons, Dock.Bottom);
        editor.Children.Add(editorTop);
        editor.Children.Add(editorButtons);
        editor.Children.Add(_value);

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(Ui.Columns(lists, editor));
        ShowEditor(null, false);
        Load();
    }

    ListView BuildList(ListView list, ObservableCollection<EnvVar> items)
    {
        list.ItemsSource = items;
        list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        CollectionViewSource.GetDefaultView(items).Filter = o =>
        {
            var q = _filter.Text.Trim();
            var v = (EnvVar)o;
            return q.Length == 0 || v.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 150, DisplayMemberBinding = new Binding(nameof(EnvVar.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "值", Width = 320, DisplayMemberBinding = new Binding(nameof(EnvVar.Preview)) });
        list.View = view;
        ListTools.Sortable(list, ("值", nameof(EnvVar.Value)));
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not EnvVar v) return;
            (list == _userList ? _systemList : _userList).SelectedItem = null;
            ShowEditor(v, v.System);
        };
        return list;
    }

    void Load()
    {
        try
        {
            Fill(_user, Read(false));
            Fill(_system, Read(true));
            Ui.SetStatus(_status, $"用户变量 {_user.Count} 个，系统变量 {_system.Count} 个{(Elevation.IsAdmin ? "" : "  修改系统变量时会请求管理员权限")}");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    static void Fill(ObservableCollection<EnvVar> target, List<EnvVar> items)
    {
        target.Clear();
        foreach (var v in items.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)) target.Add(v);
    }

    static RegistryKey? Open(bool system, bool write = false) =>
        system ? Elevation.Base(RegistryHive.LocalMachine).OpenSubKey(SystemKey, write) : Registry.CurrentUser.OpenSubKey(UserKey, write);

    static List<EnvVar> Read(bool system)
    {
        var list = new List<EnvVar>();
        using var key = Open(system);
        if (key == null) return list;
        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0) continue;
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames);
            list.Add(new EnvVar { System = system, Name = name, Value = value is string[] multi ? string.Join(";", multi) : Convert.ToString(value) ?? "", Expand = kind == RegistryValueKind.ExpandString });
        }
        return list;
    }

    static bool LooksLikeList(EnvVar v) =>
        v.Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase) || v.Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
        || (v.Value.Contains(';') && v.Value.Split(';').Count(s => s.Contains('\\')) >= 2);

    void ShowEditor(EnvVar? v, bool system)
    {
        _editing = v;
        _editingSystem = system;
        _scope.Text = v == null ? $"新建{(system ? "系统" : "用户")}变量" : $"{(system ? "系统" : "用户")}变量：{v.Name}";
        _name.Text = v?.Name ?? "";
        _expand.IsChecked = v?.Expand ?? false;
        bool asList = v != null && LooksLikeList(v);
        _asList.IsChecked = asList;
        _value.TextWrapping = asList ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _value.Text = asList ? string.Join("\r\n", v!.Value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) : v?.Value ?? "";
    }

    void New(bool system)
    {
        _userList.SelectedItem = null;
        _systemList.SelectedItem = null;
        ShowEditor(null, system);
        _name.Focus();
    }

    void SwitchListMode(bool asList)
    {
        _value.TextWrapping = asList ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _value.Text = asList
            ? string.Join("\r\n", _value.Text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()))
            : EditedValue(true);
    }

    string EditedValue(bool list) => list
        ? string.Join(";", _value.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Select(s => s.Trim()).Where(s => s.Length > 0))
        : _value.Text.Replace("\r\n", "").Replace("\n", "");

    void AddFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (_asList.IsChecked != true)
        {
            _asList.IsChecked = true;
            SwitchListMode(true);
        }
        _value.Text = _value.Text.TrimEnd() + (_value.Text.Trim().Length > 0 ? "\r\n" : "") + dialog.SelectedPath;
    }

    void CheckPaths()
    {
        var entries = EditedValue(_asList.IsChecked == true).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var missing = entries.Where(e => e.Contains('\\') && !Directory.Exists(Environment.ExpandEnvironmentVariables(e)) && !File.Exists(Environment.ExpandEnvironmentVariables(e))).ToList();
        var duplicates = entries.GroupBy(e => Environment.ExpandEnvironmentVariables(e).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (missing.Count == 0 && duplicates.Count == 0) { Ui.SetStatus(_status, $"共 {entries.Length} 项，路径都存在且没有重复"); return; }
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"不存在 {missing.Count} 项：" + string.Join("；", missing.Take(3)) + (missing.Count > 3 ? "…" : ""));
        if (duplicates.Count > 0) parts.Add($"重复 {duplicates.Count} 项：" + string.Join("；", duplicates.Take(3)) + (duplicates.Count > 3 ? "…" : ""));
        Ui.SetStatus(_status, string.Join("  ", parts), true);
    }

    async void Save()
    {
        if (_busy) return;
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(new[] { '=', '\0' }) >= 0) { Ui.SetStatus(_status, "变量名不能为空，也不能包含 =", true); return; }
        var value = EditedValue(_asList.IsChecked == true);
        if (value.Length > 32767) { Ui.SetStatus(_status, "值太长（最多 32767 个字符）", true); return; }
        bool expand = _expand.IsChecked == true || value.Contains('%') && _editing == null;
        bool system = _editingSystem;
        var existing = (system ? _system : _user).FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (_editing == null && existing != null && MessageBox.Show(Window.GetWindow(this), $"{name} 已存在，覆盖？", "环境变量", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var renamedFrom = _editing != null && !_editing.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? _editing.Name : null;
        await Apply(system, $"已保存 {name}", reg =>
        {
            if (renamedFrom != null) reg.Delete(renamedFrom);
            reg.Set(name, expand ? RegistryValueKind.ExpandString : RegistryValueKind.String, value);
        }, () =>
        {
            if (renamedFrom != null) Environment.SetEnvironmentVariable(renamedFrom, null, EnvironmentVariableTarget.User);
            if (expand)
            {
                using var key = Open(false, true) ?? Registry.CurrentUser.CreateSubKey(UserKey);
                key!.SetValue(name, value, RegistryValueKind.ExpandString);
            }
            else Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        });
        Select(system, name);
    }

    async void Delete()
    {
        if (_busy || _editing == null) { Ui.SetStatus(_status, "先选中要删除的变量", true); return; }
        var v = _editing;
        if (MessageBox.Show(Window.GetWindow(this), $"删除{(v.System ? "系统" : "用户")}变量 {v.Name}？\n删除前会备份。", "环境变量", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await Apply(v.System, $"已删除 {v.Name}", reg => reg.Delete(v.Name), () => Environment.SetEnvironmentVariable(v.Name, null, EnvironmentVariableTarget.User));
        ShowEditor(null, v.System);
    }

    /// <summary>Backs up the scope, applies the change and notifies running programs.</summary>
    async Task Apply(bool system, string done, Action<RegFile> systemChange, Action userChange)
    {
        _busy = true;
        Ui.SetStatus(_status, "正在保存…");
        try
        {
            var backup = Backup(system);
            string? error = null;
            if (system)
            {
                var reg = new RegFile().Key(@"HKEY_LOCAL_MACHINE\" + SystemKey);
                systemChange(reg);
                error = await Task.Run(() => Elevation.ImportReg(reg));
                if (error == null) await Task.Run(Elevation.BroadcastEnvironmentChange);
            }
            else
            {
                await Task.Run(() => { userChange(); Elevation.BroadcastEnvironmentChange(); });
            }
            Load();
            if (error != null) Ui.SetStatus(_status, "保存失败：" + error, true);
            else Ui.SetStatus(_status, $"{done}，已通知其他程序（新打开的程序才会读到新值）。备份：{Path.GetFileName(backup)}");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "保存失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Writes the whole scope as a .reg file that can be double-clicked to restore it.</summary>
    static string Backup(bool system)
    {
        Directory.CreateDirectory(BackupDir);
        var keyPath = system ? @"HKEY_LOCAL_MACHINE\" + SystemKey : @"HKEY_CURRENT_USER\" + UserKey;
        var reg = new RegFile().Key(keyPath);
        using (var key = Open(system))
        {
            if (key != null)
                foreach (var name in key.GetValueNames())
                    reg.Set(name, key.GetValueKind(name), key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        var path = Path.Combine(BackupDir, $"{(system ? "system" : "user")}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        File.WriteAllText(path, reg.ToString(), Encoding.Unicode);
        foreach (var old in new DirectoryInfo(BackupDir).GetFiles((system ? "system" : "user") + "-*.reg").OrderByDescending(f => f.Name).Skip(30)) old.Delete();
        return path;
    }

    void Select(bool system, string name)
    {
        var list = system ? _systemList : _userList;
        var item = (system ? _system : _user).FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        list.SelectedItem = item;
        list.ScrollIntoView(item);
    }
}
