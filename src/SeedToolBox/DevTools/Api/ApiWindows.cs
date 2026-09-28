using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools.Api;

/// <summary>Finding things in the collection tree.</summary>
static class ApiTree
{
    /// <summary>The collection and folders above <paramref name="node"/>, outermost first; null when it isn't in any collection.</summary>
    public static List<ApiFolder>? PathOf(ApiData data, object node)
    {
        foreach (var c in data.Collections)
        {
            var path = new List<ApiFolder>();
            if (ReferenceEquals(c, node)) return path;
            if (Find(c, node, path)) return path;
        }
        return null;
    }

    static bool Find(ApiFolder folder, object node, List<ApiFolder> path)
    {
        path.Add(folder);
        if (node is ApiRequest r && folder.Requests.Contains(r)) return true;
        foreach (var f in folder.Folders)
        {
            if (ReferenceEquals(f, node)) return true;
            if (Find(f, node, path)) return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    public static ApiFolder? ParentOf(ApiData data, object node) => PathOf(data, node) is { Count: > 0 } p ? p[p.Count - 1] : null;

    public static ApiRequest? FindRequest(ApiData data, string id) => data.Collections.SelectMany(c => c.AllRequests()).FirstOrDefault(r => r.Id == id);

    /// <summary>Requests under <paramref name="root"/> in the order the tree shows them: folders first, then requests.</summary>
    public static IEnumerable<(ApiRequest Request, List<ApiFolder> Path)> Flatten(ApiFolder root, List<ApiFolder> above)
    {
        var path = above.Append(root).ToList();
        foreach (var f in root.Folders)
            foreach (var x in Flatten(f, path)) yield return x;
        foreach (var r in root.Requests) yield return (r, path);
    }

    public static bool Contains(ApiFolder folder, object node) =>
        ReferenceEquals(folder, node) || folder.Requests.Contains(node as ApiRequest) || folder.Folders.Any(f => Contains(f, node));

    public static bool Remove(ApiFolder folder, object node)
    {
        if (node is ApiRequest r && folder.Requests.Remove(r)) return true;
        if (node is ApiFolder f && folder.Folders.Remove(f)) return true;
        return folder.Folders.Any(sub => Remove(sub, node));
    }
}

static class ApiDialogs
{
    public static Window Tool(Window? owner, string title, UIElement content, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height,
            MinWidth = 480,
            MinHeight = 320,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(16), Child = content },
        };
        DialogWindow.ApplyTheme(w);
        return w;
    }

    public static bool Confirm(Window? owner, string text) =>
        MessageBox.Show(owner!, text, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>A read-only text window with a copy button, for code and AI answers.</summary>
    public static void ShowText(Window? owner, string title, string text)
    {
        var box = Ui.Area(wrap: true);
        box.IsReadOnly = true;
        box.Text = text;
        var dock = new DockPanel();
        var buttons = Ui.Row(Ui.CopyButton(() => box.Text, "复制"));
        buttons.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(box);
        Tool(owner, title, dock, 720, 520).Show();
    }

    public static void Code(Window? owner, PreparedRequest p)
    {
        var box = Ui.Area();
        box.IsReadOnly = true;
        var language = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var l in ApiCodeGen.Languages) language.Items.Add(l);
        language.SelectionChanged += (_, _) => box.Text = ApiCodeGen.Generate((string)language.SelectedItem, p);
        language.SelectedIndex = 0;
        var top = Ui.Row(Ui.Label("语言"), language, Ui.CopyButton(() => box.Text, "复制"));
        top.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(box);
        Tool(owner, "生成代码", dock, 760, 520).ShowDialog();
    }
}

/// <summary>Globals and environments, each a table of variables; edits apply straight away.</summary>
sealed class EnvironmentWindow
{
    readonly ApiData _data;
    readonly Action _save;
    readonly ListBox _list = new() { Width = 180, Margin = new Thickness(0, 0, 12, 0) };
    readonly KeyValueEditor _vars = new(descriptions: false, secrets: true, keyHint: "变量", valueHint: "值");
    readonly TextBox _name = Ui.Field();
    readonly TextBlock _status = Ui.Status();
    readonly Window _window;
    bool _loading;

    public EnvironmentWindow(Window? owner, ApiData data, Action save, ApiEnvironment? select)
    {
        _data = data;
        _save = save;

        var left = new DockPanel();
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Width = 180 };
        foreach (var b in new[] { Ui.Button("新建", Add), Ui.Button("复制", Duplicate), Ui.Button("删除", Delete), Ui.Button("导入…", Import), Ui.Button("导出…", Export) })
        {
            b.MinWidth = 0;
            b.Margin = new Thickness(0, 0, 6, 6);
            buttons.Children.Add(b);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(_list);

        var right = new DockPanel();
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var nameLabel = Ui.Label("名称");
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        nameRow.Children.Add(_name);
        DockPanel.SetDock(nameRow, Dock.Top);
        right.Children.Add(nameRow);
        var hint = new TextBlock
        {
            Text = "请求里用 {{变量}} 引用。取值先看局部变量，再看数据文件、环境、集合，最后是全局。机密变量的值不显示在界面上。",
            Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(hint, Dock.Top);
        right.Children.Add(hint);
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(_vars);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);

        _list.SelectionChanged += (_, _) => Show();
        _vars.Changed += save;
        _name.TextChanged += (_, _) =>
        {
            if (_loading || Current is not ApiEnvironment e || _name.Text.Trim().Length == 0) return;
            if (_data.ActiveEnvironment == e.Name) _data.ActiveEnvironment = _name.Text.Trim();
            e.Name = _name.Text.Trim();
            ((ListBoxItem)_list.SelectedItem).Content = e.Name;
            save();
        };
        _window = ApiDialogs.Tool(owner, "管理环境", root, 900, 560);
        Fill(select);
    }

    public void ShowDialog() => _window.ShowDialog();

    object? Current => (_list.SelectedItem as ListBoxItem)?.Tag;

    void Fill(object? select)
    {
        _list.Items.Clear();
        var globals = new ListBoxItem { Content = "🌐 全局变量", Tag = "globals" };
        _list.Items.Add(globals);
        foreach (var e in _data.Environments)
        {
            var item = new ListBoxItem { Content = e.Name, Tag = e };
            _list.Items.Add(item);
            if (e == select) item.IsSelected = true;
        }
        if (_list.SelectedItem == null) _list.SelectedIndex = select as string == "globals" || _data.Environments.Count == 0 ? 0 : 1;
    }

    void Show()
    {
        _loading = true;
        switch (Current)
        {
            case ApiEnvironment e:
                _name.Text = e.Name;
                _name.IsEnabled = true;
                _vars.Load(e.Variables);
                break;
            case "globals":
                _name.Text = "全局变量（所有集合和环境都能用）";
                _name.IsEnabled = false;
                _vars.Load(_data.Globals);
                break;
        }
        _loading = false;
    }

    void Add()
    {
        var e = new ApiEnvironment { Name = "新环境 " + (_data.Environments.Count + 1) };
        _data.Environments.Add(e);
        _save();
        Fill(e);
    }

    void Duplicate()
    {
        if (Current is not ApiEnvironment e) return;
        var copy = new ApiEnvironment { Name = e.Name + " 副本", Variables = e.Variables.Clone() };
        _data.Environments.Insert(_data.Environments.IndexOf(e) + 1, copy);
        _save();
        Fill(copy);
    }

    void Delete()
    {
        if (Current is not ApiEnvironment e || !ApiDialogs.Confirm(_window, $"删除环境“{e.Name}”？")) return;
        _data.Environments.Remove(e);
        if (_data.ActiveEnvironment == e.Name) _data.ActiveEnvironment = "";
        _save();
        Fill(null);
    }

    void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Postman 环境 (*.json)|*.json|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(_window) != true) return;
        ApiEnvironment? last = null;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(file));
                if (!ApiImport.IsPostmanEnvironment(o)) { Ui.SetStatus(_status, Path.GetFileName(file) + " 不是 Postman 环境文件", true); continue; }
                var env = ApiImport.ReadEnvironment(o);
                if ((string?)o["_postman_variable_scope"] == "globals")
                {
                    foreach (var v in env.Variables) ApiVariables.Set(_data.Globals, v.Key, v.Value);
                    continue;
                }
                _data.Environments.Add(last = env);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Ui.SetStatus(_status, "导入失败：" + ex.Message, true);
            }
        }
        _save();
        Fill(last);
    }

    void Export()
    {
        var (name, vars, scope) = Current is ApiEnvironment e ? (e.Name, e.Variables, "environment") : ("Globals", _data.Globals, "globals");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Postman 环境 (*.json)|*.json", FileName = name + ".postman_environment.json" };
        if (dialog.ShowDialog(_window) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ApiImport.WriteEnvironment(name, vars, scope).ToString(Formatting.Indented), new UTF8Encoding(false));
            Ui.SetStatus(_status, "已导出到 " + dialog.FileName + (vars.Any(v => v.Secret) ? "（机密变量也写进去了，注意保管）" : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.SetStatus(_status, "导出失败：" + ex.Message, true);
        }
    }
}

/// <summary>Auth, scripts and variables of a folder or collection, inherited by the requests in it.</summary>
static class FolderWindow
{
    public static void Show(Window? owner, ApiFolder folder, Action save)
    {
        var tabs = new TabControl();
        var name = Ui.Field();
        name.Text = folder.Name;
        name.TextChanged += (_, _) => { if (name.Text.Trim().Length > 0) { folder.Name = name.Text.Trim(); save(); } };

        var auth = new AuthEditor(inherit: folder is not ApiCollection);
        auth.Load(folder.Auth);
        auth.Changed += save;
        tabs.Items.Add(new TabItem { Header = "认证", Content = new Border { Padding = new Thickness(8), Child = auth } });

        var pre = new ScriptEditor("在这里的每个请求发送前先运行（先外层后里层，最后是请求自己的脚本）。", ScriptEditor.PreSnippets);
        pre.Box.Text = folder.PreScript;
        pre.Box.TextChanged += (_, _) => { folder.PreScript = pre.Box.Text; save(); };
        tabs.Items.Add(new TabItem { Header = "前置脚本", Content = new Border { Padding = new Thickness(8), Child = pre } });

        var test = new ScriptEditor("在这里的每个请求收到响应后运行。", ScriptEditor.TestSnippets);
        test.Box.Text = folder.TestScript;
        test.Box.TextChanged += (_, _) => { folder.TestScript = test.Box.Text; save(); };
        tabs.Items.Add(new TabItem { Header = "测试脚本", Content = new Border { Padding = new Thickness(8), Child = test } });

        if (folder is ApiCollection c)
        {
            var vars = new KeyValueEditor(descriptions: false, secrets: true, keyHint: "变量", valueHint: "值");
            vars.Load(c.Variables);
            vars.Changed += save;
            tabs.Items.Add(new TabItem { Header = "集合变量", Content = new Border { Padding = new Thickness(8), Child = vars } });
        }

        var desc = Ui.Area(wrap: true);
        desc.Text = folder.Description;
        desc.TextChanged += (_, _) => { folder.Description = desc.Text; save(); };
        tabs.Items.Add(new TabItem { Header = "说明", Content = new Border { Padding = new Thickness(8), Child = desc } });

        var root = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var label = Ui.Label(folder is ApiCollection ? "集合名称" : "文件夹名称");
        DockPanel.SetDock(label, Dock.Left);
        top.Children.Add(label);
        top.Children.Add(name);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(tabs);
        ApiDialogs.Tool(owner, (folder is ApiCollection ? "集合设置 - " : "文件夹设置 - ") + folder.Name, root, 820, 560).ShowDialog();
    }
}

/// <summary>Runs every request in a collection or folder, a number of times or once per data file row, and shows the test results.</summary>
sealed class RunnerWindow
{
    readonly ApiData _data;
    readonly ApiFolder _root;
    readonly List<ApiFolder> _above;
    readonly Action _save;
    readonly Window _window;
    readonly TextBox _iterations = Ui.Field(60);
    readonly TextBox _delay = Ui.Field(70);
    readonly TextBox _dataFile = Ui.Field();
    readonly CheckBox _stopOnError = new() { Content = "出错就停止", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _keepVars = new() { Content = "保留脚本改的变量", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly TreeView _results = new() { FontFamily = Ui.Mono };
    readonly TextBlock _summary = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) };
    readonly Button _run;
    readonly Button _stop;
    CancellationTokenSource? _cancel;

    public RunnerWindow(Window? owner, ApiData data, ApiFolder root, Action save)
    {
        _data = data;
        _root = root;
        _above = ApiTree.PathOf(data, root) ?? new();
        _save = save;
        _iterations.Text = "1";
        _delay.Text = "0";
        _dataFile.ToolTip = "CSV（第一行是列名）或 JSON 数组；每一行跑一轮，列名当变量用";
        var pick = Ui.Button("选择…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "数据文件 (*.csv;*.json)|*.csv;*.json|所有文件|*.*" };
            if (dialog.ShowDialog(_window) == true) _dataFile.Text = dialog.FileName;
        });
        _run = Ui.Button("运行", () => _ = Run(), accent: true);
        _stop = Ui.Button("停止", () => _cancel?.Cancel());
        _stop.IsEnabled = false;

        var count = ApiTree.Flatten(root, _above).Count();
        var env = data.Environments.FirstOrDefault(e => e.Name == data.ActiveEnvironment);
        var info = new TextBlock
        {
            Text = $"运行“{root.Name}”里的 {count} 个请求，环境：{env?.Name ?? "无"}",
            Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 0, 0, 10),
        };
        var row1 = Ui.Row(Ui.Label("轮数"), _iterations, Ui.Label("  每个请求间隔 (ms)"), _delay, _stopOnError, _keepVars);
        row1.Margin = new Thickness(0, 0, 0, 8);
        var row2 = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var dataLabel = Ui.Label("数据文件");
        DockPanel.SetDock(dataLabel, Dock.Left);
        var right = Ui.Row(pick, Ui.Button("清除", () => _dataFile.Clear()), _run, _stop);
        right.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        row2.Children.Add(dataLabel);
        row2.Children.Add(right);
        row2.Children.Add(_dataFile);

        var dock = new DockPanel();
        foreach (var e in new UIElement[] { info, row1, row2, _summary })
        {
            DockPanel.SetDock(e, Dock.Top);
            dock.Children.Add(e);
        }
        dock.Children.Add(_results);
        _window = ApiDialogs.Tool(owner, "运行集合 - " + root.Name, dock, 860, 620);
        _window.Closing += (_, _) => _cancel?.Cancel();
    }

    public void Show() => _window.Show();

    public static List<Dictionary<string, string>> ReadData(string path)
    {
        var text = File.ReadAllText(path);
        var rows = new List<Dictionary<string, string>>();
        if (text.TrimStart().StartsWith("["))
        {
            foreach (var o in JArray.Parse(text).OfType<JObject>())
                rows.Add(o.Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.String ? (string)p.Value! : p.Value.ToString(Formatting.None)));
            return rows;
        }
        var lines = Csv(text);
        if (lines.Count == 0) return rows;
        var header = lines[0];
        foreach (var line in lines.Skip(1))
        {
            if (line.Count == 1 && line[0].Length == 0) continue;
            var row = new Dictionary<string, string>();
            for (int i = 0; i < header.Count; i++) row[header[i].Trim()] = i < line.Count ? line[i] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>CSV with quoted fields, "" escapes and newlines inside quotes.</summary>
    static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else if (!(c == '﻿' && i == 0)) field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    async Task Run()
    {
        if (_cancel != null) return;
        if (!int.TryParse(_iterations.Text.Trim(), out var iterations) || iterations < 1) iterations = 1;
        if (!int.TryParse(_delay.Text.Trim(), out var delay) || delay < 0) delay = 0;
        List<Dictionary<string, string>> rows = new();
        if (_dataFile.Text.Trim().Length > 0)
        {
            try { rows = ReadData(_dataFile.Text.Trim()); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                _summary.Text = "读不了数据文件：" + ex.Message;
                _summary.Foreground = ApiUi.Res("DangerBrush");
                return;
            }
            if (rows.Count > 0) iterations = rows.Count;
        }

        var env = _data.Environments.FirstOrDefault(e => e.Name == _data.ActiveEnvironment);
        var collection = _above.Count > 0 ? _above[0] as ApiCollection : _root as ApiCollection;
        bool keep = _keepVars.IsChecked == true;
        var vars = new ApiVariables(
            keep ? _data.Globals : _data.Globals.Clone(),
            collection == null ? null : keep ? collection.Variables : collection.Variables.Clone(),
            env == null ? null : keep ? env.Variables : env.Variables.Clone());
        var list = ApiTree.Flatten(_root, _above).ToList();

        _results.Items.Clear();
        _summary.Foreground = ApiUi.Res("TextBrush");
        var cancel = _cancel = new CancellationTokenSource();
        _run.IsEnabled = false;
        _stop.IsEnabled = true;
        int sent = 0, failedRequests = 0, passed = 0, failed = 0;
        long totalMs = 0;
        try
        {
            for (int it = 0; it < iterations && !cancel.IsCancellationRequested; it++)
            {
                vars.Data.Clear();
                if (it < rows.Count) foreach (var kv in rows[it]) vars.Data[kv.Key] = kv.Value;
                var iterationNode = new TreeViewItem { Header = $"第 {it + 1} 轮", IsExpanded = true, FontWeight = FontWeights.SemiBold };
                _results.Items.Add(iterationNode);
                int index = 0, guard = 0;
                while (index < list.Count && !cancel.IsCancellationRequested && guard++ < list.Count * 20)
                {
                    var (request, path) = list[index];
                    var ctx = new ExecContext
                    {
                        Collection = collection, Path = path, Variables = vars, EnvironmentName = env?.Name ?? "",
                        Iteration = it, IterationCount = iterations,
                    };
                    ExecResult result;
                    try { result = await ApiEngine.ExecuteAsync(request, ctx, cancel.Token); }
                    catch (OperationCanceledException) { break; }
                    var node = ResultNode(request, result);
                    iterationNode.Items.Add(node);
                    node.BringIntoView();
                    if (!result.Skipped) sent++;
                    if (result.Error != null || result.Tests.Any(t => !t.Passed)) failedRequests++;
                    passed += result.Tests.Count(t => t.Passed);
                    failed += result.Tests.Count(t => !t.Passed);
                    totalMs += result.Response?.Milliseconds ?? 0;
                    _summary.Text = $"已发送 {sent} 个请求，测试通过 {passed}，失败 {failed}";
                    if (_stopOnError.IsChecked == true && (result.Error != null || result.Tests.Any(t => !t.Passed))) { cancel.Cancel(); break; }

                    if (result.NextRequest != null)
                    {
                        if (result.NextRequest.Length == 0) break;
                        int next = list.FindIndex(x => x.Request.Name == result.NextRequest);
                        index = next >= 0 ? next : index + 1;
                    }
                    else index++;
                    if (delay > 0 && index < list.Count) await Task.Delay(delay, cancel.Token).ContinueWith(_ => { });
                }
            }
        }
        finally
        {
            _cancel = null;
            _run.IsEnabled = true;
            _stop.IsEnabled = false;
            if (keep) _save();
        }
        _summary.Text = (cancel.IsCancellationRequested ? "已停止。" : "运行完成。") +
                        $"发送 {sent} 个请求（{failedRequests} 个有问题），测试通过 {passed}，失败 {failed}，平均 {(sent > 0 ? totalMs / sent : 0)} ms";
        _summary.Foreground = failed > 0 || failedRequests > 0 ? ApiUi.Res("DangerBrush") : ApiUi.StatusBrush(200);
    }

    static TreeViewItem ResultNode(ApiRequest request, ExecResult r)
    {
        var header = new TextBlock { FontWeight = FontWeights.Normal };
        bool bad = r.Error != null || r.Tests.Any(t => !t.Passed);
        header.Inlines.Add(new System.Windows.Documents.Run(bad ? "✘ " : "✔ ") { Foreground = bad ? ApiUi.Res("DangerBrush") : ApiUi.StatusBrush(200) });
        header.Inlines.Add(new System.Windows.Documents.Run(ApiUi.MethodShort(request.Method).PadRight(6)) { Foreground = ApiUi.MethodBrush(request.Method), FontWeight = FontWeights.SemiBold });
        header.Inlines.Add(new System.Windows.Documents.Run(request.Name + "  "));
        if (r.Skipped) header.Inlines.Add(new System.Windows.Documents.Run("已跳过") { Foreground = DialogWindow.HintBrush });
        else if (r.Response is { } resp)
        {
            header.Inlines.Add(new System.Windows.Documents.Run($"{resp.Status} {resp.Reason}") { Foreground = ApiUi.StatusBrush(resp.Status) });
            header.Inlines.Add(new System.Windows.Documents.Run($"  {resp.Milliseconds} ms  {Ui.FormatSize(resp.Bytes.Length)}") { Foreground = DialogWindow.HintBrush });
        }
        if (r.Error != null) header.Inlines.Add(new System.Windows.Documents.Run(r.Error) { Foreground = ApiUi.Res("DangerBrush") });
        if (r.Tests.Count > 0) header.Inlines.Add(new System.Windows.Documents.Run($"  测试 {r.Tests.Count(t => t.Passed)}/{r.Tests.Count}") { Foreground = DialogWindow.HintBrush });
        var node = new TreeViewItem { Header = header, IsExpanded = bad, ToolTip = r.Request?.Url };
        foreach (var t in r.Tests)
            node.Items.Add(new TreeViewItem
            {
                Header = (t.Passed ? "✔ " : "✘ ") + t.Name + (t.Passed ? "" : " — " + t.Error),
                Foreground = t.Passed ? ApiUi.StatusBrush(200) : ApiUi.Res("DangerBrush"),
                FontWeight = FontWeights.Normal,
            });
        foreach (var line in r.Console)
            node.Items.Add(new TreeViewItem { Header = "› " + line, Foreground = DialogWindow.HintBrush, FontWeight = FontWeights.Normal });
        return node;
    }
}
