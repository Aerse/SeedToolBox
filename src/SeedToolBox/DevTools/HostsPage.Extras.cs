using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SeedToolBox.Core;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools;

/// <summary>In-place editing, conflict warnings, named profiles and backup restore for the hosts page.</summary>
sealed partial class HostsPage
{
    static readonly string ProfileDir = Path.Combine(AppPaths.Data, "hosts-profiles");
    static readonly string BackupDir = Path.Combine(AppPaths.Data, "hosts-backup");

    readonly TextBlock _warnings = new()
    {
        Foreground = (Brush)Application.Current.Resources["DangerBrush"],
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    readonly ComboBox _profiles = new() { Width = 180, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    static DataTemplate EditableCell(string path)
    {
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        box.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        box.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
        box.SetValue(FrameworkElement.MinWidthProperty, 60.0);
        box.AddHandler(UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((s, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            ((TextBox)s).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }));
        return new DataTemplate { VisualTree = box };
    }

    string CurrentText() => _rawMode.IsChecked == true ? _raw.Text : string.Join(_newline, _lines);

    /// <summary>Replaces the editor content without touching the file; the user saves afterwards.</summary>
    void SetText(string text)
    {
        _lines = text.Replace("\r\n", "\n").Split('\n');
        ParseEntries();
        _raw.Text = string.Join(_newline, _lines);
        _dirty = true;
    }

    /// <summary>Flags host names listed more than once among the enabled entries.</summary>
    void CheckConflicts()
    {
        var byHost = new Dictionary<string, List<HostEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
        {
            e.Warning = "";
            if (!e.Enabled) continue;
            foreach (var host in e.Hosts.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!byHost.TryGetValue(host, out var list)) byHost[host] = list = new List<HostEntry>();
                if (!list.Contains(e)) list.Add(e);
            }
        }
        var problems = new List<string>();
        foreach (var pair in byHost.Where(p => p.Value.Count > 1))
        {
            // The resolver uses the first line that matches, so later ones never apply
            bool conflict = pair.Value.Select(e => e.Ip).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (conflict && pair.Value.Select(e => e.Ip.Contains(':')).Distinct().Count() > 1 && pair.Value.Count == 2) continue; // one IPv4 and one IPv6
            var text = conflict ? $"冲突：{pair.Key} 指向 {string.Join("、", pair.Value.Select(e => e.Ip))}，只有第一条生效" : $"重复：{pair.Key} 出现 {pair.Value.Count} 次";
            problems.Add(text);
            foreach (var e in pair.Value) e.Warning = e.Warning.Length == 0 ? text : e.Warning + "；" + text;
        }
        _warnings.Text = problems.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", problems.Take(8)) + (problems.Count > 8 ? $"\n…另有 {problems.Count - 8} 处" : "");
        _warnings.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Profiles

    FrameworkElement BuildProfiles()
    {
        RefreshProfiles();
        return Ui.Row(Ui.Label("方案"), _profiles,
            Ui.Button("切换到此方案", ApplyProfile),
            Ui.Button("载入编辑", () =>
            {
                if (SelectedProfile() is not { } path || !ConfirmDiscard()) return;
                SetText(TextFiles.Read(path, out _));
                Ui.SetStatus(_status, $"已载入方案「{Path.GetFileNameWithoutExtension(path)}」，保存后生效");
            }),
            Ui.Button("当前内容存为方案…", SaveProfile),
            Ui.Button("删除方案", DeleteProfile));
    }

    void RefreshProfiles(string? select = null)
    {
        _profiles.Items.Clear();
        if (Directory.Exists(ProfileDir))
            foreach (var f in Directory.GetFiles(ProfileDir, "*.txt").OrderBy(f => f, StringComparer.CurrentCulture))
                _profiles.Items.Add(Path.GetFileNameWithoutExtension(f));
        _profiles.SelectedItem = select;
        if (_profiles.SelectedIndex < 0 && _profiles.Items.Count > 0) _profiles.SelectedIndex = 0;
    }

    string? SelectedProfile()
    {
        if (_profiles.SelectedItem is not string name) { Ui.SetStatus(_status, "还没有方案，先把当前内容存为方案", true); return null; }
        var path = Path.Combine(ProfileDir, name + ".txt");
        if (!File.Exists(path)) { Ui.SetStatus(_status, "方案文件不存在", true); RefreshProfiles(); return null; }
        return path;
    }

    void SaveProfile()
    {
        var owner = Window.GetWindow(this);
        var name = InputDialog.Show(owner, "方案名称", _profiles.SelectedItem as string ?? "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (name!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Ui.SetStatus(_status, "方案名称不能包含 \\ / : * ? \" < > |", true); return; }
        var path = Path.Combine(ProfileDir, name + ".txt");
        if (File.Exists(path) && MessageBox.Show(owner, $"覆盖方案「{name}」？", "Hosts 管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Directory.CreateDirectory(ProfileDir);
            File.WriteAllText(path, CurrentText(), new UTF8Encoding(false));
            RefreshProfiles(name);
            Ui.SetStatus(_status, $"已存为方案「{name}」");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "保存方案失败：" + ex.Message, true); }
    }

    void ApplyProfile()
    {
        if (SelectedProfile() is not { } path) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (MessageBox.Show(Window.GetWindow(this), $"用方案「{name}」替换当前 hosts 文件？{(_dirty ? "\n未保存的修改会丢失。" : "")}\n原文件会先备份。", "Hosts 管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { SetText(TextFiles.Read(path, out _)); }
        catch (Exception ex) { Ui.SetStatus(_status, "读取方案失败：" + ex.Message, true); return; }
        Save();
        if (!_dirty) Ui.SetStatus(_status, $"已切换到方案「{name}」并刷新 DNS 缓存");
    }

    void DeleteProfile()
    {
        if (SelectedProfile() is not { } path) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (MessageBox.Show(Window.GetWindow(this), $"删除方案「{name}」？", "Hosts 管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            File.Delete(path);
            RefreshProfiles();
            Ui.SetStatus(_status, $"已删除方案「{name}」");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "删除失败：" + ex.Message, true); }
    }

    // Backups

    void RestoreBackup()
    {
        var files = Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles("hosts-*").OrderByDescending(f => f.Name).ToList() : new List<FileInfo>();
        if (files.Count == 0) { Ui.SetStatus(_status, "还没有备份，保存 hosts 时会自动备份", true); return; }

        var list = new ListBox { Width = 220, Height = 360, BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"] };
        foreach (var f in files) list.Items.Add(new ListBoxItem { Content = $"{f.LastWriteTime:yyyy-MM-dd HH:mm:ss}  {Ui.FormatSize(f.Length)}", Tag = f.FullName });
        var preview = Ui.Area();
        preview.IsReadOnly = true;
        preview.Width = 520;
        preview.Height = 360;
        preview.Margin = new Thickness(12, 0, 0, 0);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string path }) return;
            try { preview.Text = TextFiles.Read(path, out _); }
            catch (Exception ex) { preview.Text = "读取失败：" + ex.Message; }
        };
        list.SelectedIndex = 0;

        var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        body.Children.Add(list);
        body.Children.Add(preview);
        var ok = DialogWindow.OkButton("载入此备份");
        var window = DialogWindow.Create("从备份恢复 hosts", body, ok, DialogWindow.CancelButton());
        window.Owner = Window.GetWindow(this);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) => window.DialogResult = true;
        list.MouseDoubleClick += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true || !ConfirmDiscard()) return;
        SetText(preview.Text);
        Ui.SetStatus(_status, $"已载入备份（{((ListBoxItem)list.SelectedItem).Content}），点「保存」写回 hosts");
    }
}
