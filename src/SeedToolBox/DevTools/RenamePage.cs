using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json;
using SeedToolBox.Core;
using SeedToolBox.Launcher;

namespace SeedToolBox.DevTools;

sealed class RenameItem : ObservableObject
{
    string _newName = "";
    string _state = "";
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string State { get => _state; set => Set(ref _state, value); }
}

sealed class RenamePreset
{
    public string Find = "", Replace = "", Template = "*", Start = "1", Digits = "2", NewExt = "";
    public bool Regex, WithExt, ExtOnly;
    public int Case;
}

/// <summary>Batch renames files with replace, regex, sequence numbers and case rules, previewed before applying.</summary>
sealed class RenamePage : DockPanel
{
    readonly ObservableCollection<RenameItem> _items = new();
    readonly ListView _list = new();
    readonly TextBox _find = Ui.Field(150);
    readonly TextBox _replace = Ui.Field(150);
    readonly CheckBox _regex = new() { Content = "正则", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _withExt = new() { Content = "含扩展名", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBox _template = Ui.Field(170);
    readonly TextBox _start = Ui.Field(50);
    readonly TextBox _digits = Ui.Field(50);
    readonly ComboBox _case = new() { Width = 120, ItemsSource = new[] { "大小写不变", "全部小写", "全部大写", "首字母大写" }, SelectedIndex = 0 };
    readonly CheckBox _extOnly = new() { Content = "只改扩展名为", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _newExt = Ui.Field(70);
    readonly CheckBox _recursive = new() { Content = "包含子文件夹", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly ComboBox _presets = new() { Width = 150, IsEditable = true, Margin = new Thickness(0, 0, 8, 0) };
    readonly Dictionary<string, (DateTime Modified, DateTime? Taken)> _dates = new(StringComparer.OrdinalIgnoreCase);
    static readonly string PresetFile = Path.Combine(AppPaths.Data, "rename-presets.json");
    readonly ComboBox _sort = new() { Width = 110, ItemsSource = new[] { "按添加顺序", "按名称", "按修改时间" }, SelectedIndex = 0 };
    readonly TextBlock _status = Ui.Status();
    readonly List<(RenameItem Item, string From, string To)> _undo = new();

    public RenamePage()
    {
        var header = Ui.Header("批量重命名", "替换、正则、序号模板与大小写规则，先预览再执行，可撤销上一次");
        _template.Text = "*";
        _template.ToolTip = "* 原文件名，# 序号，{date:yyyyMMdd} 修改日期，{exif:yyyyMMdd_HHmmss} 拍摄日期（无 EXIF 时用修改日期），例如：照片_#  或  {exif}_*";
        _newExt.ToolTip = "新扩展名，如 jpg；留空表示去掉扩展名";
        _start.Text = "1";
        _digits.Text = "2";
        _digits.ToolTip = "序号位数，不足补 0";

        var row1 = Ui.Row(Ui.Button("添加文件", Pick), Ui.Button("添加文件夹", PickFolder), _recursive, Ui.Button("清空", () => { _items.Clear(); _undo.Clear(); _status.Text = ""; }), Ui.Label("", 8), Ui.Label("排序"), _sort, Ui.Label("", 16), Ui.Label("大小写"), _case, Ui.Label("", 16), ListTools.ExportButton(_list, _status, "重命名预览.csv"));
        var row2 = Ui.Row(Ui.Label("查找"), _find, Ui.Label("", 8), Ui.Label("替换为"), _replace, Ui.Label("", 12), _regex, _withExt);
        var row3 = Ui.Row(Ui.Label("命名模板"), _template, Ui.Label("", 8), Ui.Label("起始"), _start, Ui.Label("", 8), Ui.Label("位数"), _digits, Ui.Label("", 16), _extOnly, _newExt);
        var row4 = Ui.Row(Ui.Button("上移", () => Move(-1)), Ui.Button("下移", () => Move(1)), Ui.Label("", 8),
            Ui.Label("预设"), _presets, Ui.Button("保存预设", SavePreset), Ui.Button("载入", LoadPreset), Ui.Button("删除预设", DeletePreset), Ui.Label("", 8),
            Ui.Button("执行重命名", Apply, accent: true), Ui.Button("撤销", Undo));
        LoadPresetNames();

        foreach (var box in new[] { _find, _replace, _template, _start, _digits, _newExt }) box.TextChanged += (_, _) => Preview();
        foreach (var check in new[] { _regex, _withExt, _extOnly }) check.Click += (_, _) => Preview();
        _case.SelectionChanged += (_, _) => Preview();
        _sort.SelectionChanged += (_, _) => Sort();

        _list.ItemsSource = _items;
        _list.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "原文件名", Width = 280, DisplayMemberBinding = new Binding(nameof(RenameItem.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "新文件名", Width = 280, DisplayMemberBinding = new Binding(nameof(RenameItem.NewName)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 170, DisplayMemberBinding = new Binding(nameof(RenameItem.State)) });
        _list.View = view;
        Ui.FileDrop(_list, AddPaths);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is RenameItem item) ProcessLauncher.OpenLocation(item.Path); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Delete) return;
            foreach (var item in _list.SelectedItems.Cast<RenameItem>().ToList()) _items.Remove(item);
            Preview();
        };

        var hint = new TextBlock { Text = "把文件或文件夹拖到这里（文件夹会添加其中的文件）", Foreground = Views.DialogWindow.HintBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _items.CollectionChanged += (_, _) => hint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetDock(header, Dock.Top);
        SetDock(row1, Dock.Top);
        SetDock(row2, Dock.Top);
        SetDock(row3, Dock.Top);
        SetDock(row4, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(row1);
        Children.Add(row2);
        Children.Add(row3);
        Children.Add(row4);
        Children.Add(_status);
        Children.Add(new Grid { Children = { _list, hint } });
    }

    void Pick()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dialog.FileNames);
    }

    void PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddPaths(new[] { dialog.SelectedPath });
    }

    void Move(int delta)
    {
        var selected = _list.SelectedItems.Cast<RenameItem>().OrderBy(i => _items.IndexOf(i)).ToList();
        if (selected.Count == 0) return;
        if (delta > 0) selected.Reverse();
        _sort.SelectedIndex = 0;
        foreach (var item in selected)
        {
            int index = _items.IndexOf(item), target = index + delta;
            if (target < 0 || target >= _items.Count || selected.Contains(_items[target])) continue;
            _items.Move(index, target);
        }
        Preview();
    }

    Dictionary<string, RenamePreset> ReadPresets()
    {
        try
        {
            if (File.Exists(PresetFile))
                return JsonConvert.DeserializeObject<Dictionary<string, RenamePreset>>(File.ReadAllText(PresetFile)) ?? new();
        }
        catch (Exception ex) { Ui.SetStatus(_status, "读取预设失败：" + ex.Message, true); }
        return new();
    }

    bool WritePresets(Dictionary<string, RenamePreset> presets)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(PresetFile, JsonConvert.SerializeObject(presets, Formatting.Indented));
            return true;
        }
        catch (Exception ex) { Ui.SetStatus(_status, "保存预设失败：" + ex.Message, true); return false; }
    }

    void LoadPresetNames()
    {
        var text = _presets.Text;
        _presets.ItemsSource = ReadPresets().Keys.OrderBy(k => k, NaturalComparer.Instance).ToList();
        _presets.Text = text;
    }

    void SavePreset()
    {
        var name = _presets.Text.Trim();
        if (name.Length == 0) { Ui.SetStatus(_status, "请先在预设框里输入名称", true); return; }
        var presets = ReadPresets();
        presets[name] = new RenamePreset
        {
            Find = _find.Text, Replace = _replace.Text, Template = _template.Text, Start = _start.Text, Digits = _digits.Text, NewExt = _newExt.Text,
            Regex = _regex.IsChecked == true, WithExt = _withExt.IsChecked == true, ExtOnly = _extOnly.IsChecked == true, Case = _case.SelectedIndex,
        };
        if (!WritePresets(presets)) return;
        LoadPresetNames();
        _presets.Text = name;
        Ui.SetStatus(_status, $"已保存预设「{name}」");
    }

    void LoadPreset()
    {
        var name = _presets.Text.Trim();
        if (!ReadPresets().TryGetValue(name, out var p)) { Ui.SetStatus(_status, "没有这个预设", true); return; }
        _find.Text = p.Find;
        _replace.Text = p.Replace;
        _template.Text = p.Template;
        _start.Text = p.Start;
        _digits.Text = p.Digits;
        _newExt.Text = p.NewExt;
        _regex.IsChecked = p.Regex;
        _withExt.IsChecked = p.WithExt;
        _extOnly.IsChecked = p.ExtOnly;
        _case.SelectedIndex = p.Case >= 0 && p.Case < _case.Items.Count ? p.Case : 0;
        Preview();
    }

    void DeletePreset()
    {
        var name = _presets.Text.Trim();
        var presets = ReadPresets();
        if (!presets.Remove(name)) { Ui.SetStatus(_status, "没有这个预设", true); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"删除预设「{name}」？", "批量重命名", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!WritePresets(presets)) return;
        _presets.Text = "";
        LoadPresetNames();
        Ui.SetStatus(_status, $"已删除预设「{name}」");
    }

    static readonly Regex DateToken = new(@"\{(date|exif)(?::([^}]*))?\}", RegexOptions.IgnoreCase);

    (DateTime Modified, DateTime? Taken) Dates(string path)
    {
        if (_dates.TryGetValue(path, out var cached)) return cached;
        DateTime modified = DateTime.MinValue;
        DateTime? taken = null;
        try { modified = File.GetLastWriteTime(path); } catch (Exception) { }
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
            if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is System.Windows.Media.Imaging.BitmapMetadata meta && DateTime.TryParse(meta.DateTaken, out var d)) taken = d;
        }
        catch (Exception) { }
        return _dates[path] = (modified, taken);
    }

    string ExpandDates(string template, string path)
    {
        if (template.IndexOf('{') < 0) return template;
        return DateToken.Replace(template, m =>
        {
            var (modified, taken) = Dates(path);
            var date = m.Groups[1].Value.ToLowerInvariant() == "exif" ? taken ?? modified : modified;
            var format = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : "yyyyMMdd";
            try { return date.ToString(format); }
            catch (FormatException) { return m.Value; }
        });
    }

    void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            IEnumerable<string> files;
            var option = _recursive.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            try { files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", option).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : new[] { path }; }
            catch (Exception ex) { Ui.SetStatus(_status, ex.Message, true); continue; }
            foreach (var file in files)
                if (File.Exists(file) && !_items.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase)))
                    _items.Add(new RenameItem { Path = file });
        }
        Sort();
    }

    void Sort()
    {
        if (_sort.SelectedIndex > 0)
        {
            var sorted = _sort.SelectedIndex == 1
                ? _items.OrderBy(i => i.Name, NaturalComparer.Instance).ToList()
                : _items.OrderBy(i => File.GetLastWriteTime(i.Path)).ToList();
            _items.Clear();
            foreach (var item in sorted) _items.Add(item);
        }
        Preview();
    }

    /// <summary>Computes every new name; returns false when a rule is invalid.</summary>
    bool Preview()
    {
        if (!int.TryParse(_start.Text, out var start)) start = 1;
        if (!int.TryParse(_digits.Text, out var digits) || digits < 1 || digits > 10) digits = 1;
        Regex? regex = null;
        if (_regex.IsChecked == true && _find.Text.Length > 0)
        {
            try { regex = new Regex(_find.Text, RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { Ui.SetStatus(_status, "正则有误：" + ex.Message, true); return false; }
        }
        var template = _template.Text.Length == 0 ? "*" : _template.Text;

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var name = item.Name;
            if (_extOnly.IsChecked == true)
            {
                var newExt = _newExt.Text.Trim().TrimStart('.');
                item.NewName = Path.GetFileNameWithoutExtension(name) + (newExt.Length > 0 ? "." + newExt : "");
                continue;
            }
            string stem = _withExt.IsChecked == true ? name : Path.GetFileNameWithoutExtension(name);
            string ext = _withExt.IsChecked == true ? "" : Path.GetExtension(name);
            if (_find.Text.Length > 0)
            {
                try { stem = regex != null ? regex.Replace(stem, _replace.Text) : stem.Replace(_find.Text, _replace.Text); }
                catch (RegexMatchTimeoutException) { }
            }
            var number = (start + i).ToString().PadLeft(digits, '0');
            stem = ExpandDates(template, item.Path).Replace("#", number).Replace("*", stem);
            var result = stem + ext;
            result = _case.SelectedIndex switch
            {
                1 => result.ToLowerInvariant(),
                2 => result.ToUpperInvariant(),
                3 => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(result).ToLower()) + Path.GetExtension(result),
                _ => result,
            };
            item.NewName = result;
        }

        // Flag bad names and collisions before anything is touched
        var invalid = Path.GetInvalidFileNameChars();
        int conflicts = 0, changes = 0;
        var targets = _items.GroupBy(i => Path.Combine(Path.GetDirectoryName(i.Path)!, i.NewName), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            var target = Path.Combine(Path.GetDirectoryName(item.Path)!, item.NewName);
            if (item.NewName.Trim().Length == 0 || item.NewName.IndexOfAny(invalid) >= 0) { item.State = "✗ 文件名无效"; conflicts++; }
            else if (targets[target] > 1) { item.State = "✗ 与其他新文件名重复"; conflicts++; }
            else if (item.NewName == item.Name) item.State = "不变";
            else if (File.Exists(target) && !sources.Contains(target)) { item.State = "✗ 目标已存在"; conflicts++; }
            else { item.State = "将重命名"; changes++; }
        }
        Ui.SetStatus(_status, _items.Count == 0 ? "" : conflicts > 0 ? $"{conflicts} 个文件有冲突，需先解决" : $"{changes} 个文件将被重命名", conflicts > 0);
        return conflicts == 0;
    }

    void Apply()
    {
        if (!Preview()) return;
        var work = _items.Where(i => i.State == "将重命名").ToList();
        if (work.Count == 0) { Ui.SetStatus(_status, "没有需要重命名的文件"); return; }

        // Two passes through temporary names so swaps like a→b, b→a work
        var temps = new List<(RenameItem Item, string Temp, string Target)>();
        _undo.Clear();
        int failed = 0;
        foreach (var item in work)
        {
            var dir = Path.GetDirectoryName(item.Path)!;
            var temp = Path.Combine(dir, $"~stb{Guid.NewGuid():N}.tmp");
            try
            {
                File.Move(item.Path, temp);
                temps.Add((item, temp, Path.Combine(dir, item.NewName)));
            }
            catch (Exception ex) { item.State = "✗ " + ex.Message; failed++; }
        }
        foreach (var (item, temp, target) in temps)
        {
            try
            {
                File.Move(temp, target);
                _undo.Add((item, item.Path, target));
                item.Path = target;
                item.State = "✓ 已重命名";
            }
            catch (Exception ex)
            {
                try { File.Move(temp, item.Path); } catch (IOException) { }
                item.State = "✗ " + ex.Message;
                failed++;
            }
        }
        RefreshNames();
        Ui.SetStatus(_status, $"已重命名 {work.Count - failed} 个文件" + (failed > 0 ? $"，{failed} 个失败" : ""), failed > 0);
    }

    void Undo()
    {
        if (_undo.Count == 0) { Ui.SetStatus(_status, "没有可撤销的操作"); return; }
        var temps = new List<(RenameItem Item, string Temp, string Original)>();
        foreach (var (item, from, to) in _undo)
        {
            var temp = Path.Combine(Path.GetDirectoryName(to)!, $"~stb{Guid.NewGuid():N}.tmp");
            try { File.Move(to, temp); temps.Add((item, temp, from)); }
            catch (IOException) { }
        }
        int done = 0;
        foreach (var (item, temp, original) in temps)
        {
            try
            {
                File.Move(temp, original);
                item.Path = original;
                done++;
            }
            catch (IOException) { item.Path = temp; }
        }
        _undo.Clear();
        RefreshNames();
        Ui.SetStatus(_status, $"已撤销 {done} 个文件的重命名");
    }

    /// <summary>Re-adds the items so the Name column picks up changed paths.</summary>
    void RefreshNames()
    {
        var items = _items.ToList();
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        Preview();
    }
}

/// <summary>Orders "file2" before "file10".</summary>
sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);
}
