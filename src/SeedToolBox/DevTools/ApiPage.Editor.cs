using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.DevTools;

sealed partial class ApiPage
{
    readonly TextBox _name = Ui.Field();
    readonly ComboBox _method = new() { Width = 100, VerticalAlignment = VerticalAlignment.Stretch };
    readonly TextBox _url = Ui.Field();
    Button _send = null!;
    readonly TabControl _requestTabs = new();
    readonly KeyValueEditor _params = new(keyHint: "参数", valueHint: "值");
    readonly AuthEditor _auth = new(inherit: true);
    readonly KeyValueEditor _headers = new(suggestions: ApiUi.CommonHeaders, keyHint: "请求头", valueHint: "值");
    readonly ScriptEditor _pre = new("发送前运行的 JavaScript，可以用 pm.environment.set、pm.request.headers.add 等", ScriptEditor.PreSnippets);
    readonly ScriptEditor _tests = new("收到响应后运行的 JavaScript，用 pm.test 和 pm.expect 写断言", ScriptEditor.TestSnippets);
    readonly TextBox _description = Ui.Area(wrap: true);
    readonly WrapPanel _bodyModes = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _language = new() { Width = 110, Margin = new Thickness(12, 0, 0, 0) };
    Button _format = null!;
    readonly ContentControl _bodyHost = new();
    readonly TextBox _raw = Ui.Area();
    readonly KeyValueEditor _urlEncoded = new();
    readonly KeyValueEditor _formData = new(files: true);
    readonly TextBox _binary = Ui.Field();
    readonly TextBox _timeout = Ui.Field(80);
    readonly CheckBox _follow = new() { Content = "自动跟随重定向", Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _ignoreSsl = new() { Content = "不校验 SSL 证书（自签名证书时用）", Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBox _proxy = Ui.Field(320);
    readonly ToolTip _urlTip = new() { Placement = PlacementMode.Mouse };
    DockPanel _requestPanel = null!;
    bool _loading, _syncing;

    ApiRequest Draft => _current!.Draft;

    void BuildEditor()
    {
        var panel = new DockPanel();

        // name row
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var actions = Ui.Row(
            Ui.Button("保存", () => { if (_current != null) SaveTab(_current); }, accent: false),
            Ui.Button("代码", ShowCode),
            Ui.Button("AI 生成", AiGenerate));
        actions.Margin = new Thickness(8, 0, 0, 0);
        ((Button)actions.Children[0]).ToolTip = "Ctrl+S";
        ((Button)actions.Children[1]).ToolTip = "把请求生成 cURL、fetch、Python、C# 等代码";
        ((Button)actions.Children[2]).ToolTip = "用一句话描述请求，让 AI 写出来";
        DockPanel.SetDock(actions, Dock.Right);
        nameRow.Children.Add(actions);
        _name.ToolTip = "请求名称";
        _name.TextChanged += (_, _) => { if (_loading) return; Draft.Name = _name.Text; Touched(); };
        nameRow.Children.Add(_name);
        DockPanel.SetDock(nameRow, Dock.Top);
        panel.Children.Add(nameRow);

        // url row
        var urlRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var m in ApiUi.Methods) _method.Items.Add(m);
        _method.Margin = new Thickness(0, 0, 6, 0);
        _method.FontWeight = FontWeights.SemiBold;
        void MethodChanged()
        {
            if (_loading) return;
            var m = (_method.SelectedItem as string ?? "GET").Trim().ToUpperInvariant();
            if (m.Length == 0 || m == Draft.Method) return;
            Draft.Method = m;
            _method.Foreground = ApiUi.MethodBrush(m);
            Touched();
        }
        _method.SelectionChanged += (_, _) => MethodChanged();
        DockPanel.SetDock(_method, Dock.Left);
        urlRow.Children.Add(_method);
        _send = Ui.Button("发送", SendOrCancel, accent: true);
        _send.MinWidth = 80;
        _send.Margin = new Thickness(6, 0, 0, 0);
        _send.ToolTip = "Ctrl+Enter";
        DockPanel.SetDock(_send, Dock.Right);
        urlRow.Children.Add(_send);
        _url.FontFamily = Ui.Mono;
        _url.Tag = "输入 URL，可以用 {{变量}}";
        _url.TextChanged += (_, _) => UrlChanged();
        _url.MouseMove += UrlHover;
        _url.MouseLeave += (_, _) => _urlTip.IsOpen = false;
        _url.KeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { SendOrCancel(); e.Handled = true; } };
        urlRow.Children.Add(_url);
        DockPanel.SetDock(urlRow, Dock.Top);
        panel.Children.Add(urlRow);

        // tabs
        _params.Changed += ParamsChanged;
        _auth.Changed += () => { if (!_loading) Touched(); };
        _auth.InheritedFrom = DescribeInheritedAuth;
        _headers.Changed += () => { if (!_loading) Touched(); };
        _pre.Box.TextChanged += (_, _) => { if (_loading) return; Draft.PreScript = _pre.Box.Text; Touched(); };
        _tests.Box.TextChanged += (_, _) => { if (_loading) return; Draft.TestScript = _tests.Box.Text; Touched(); };
        _description.Tag = "请求的说明，支持任意文字";
        _description.TextChanged += (_, _) => { if (_loading) return; Draft.Description = _description.Text; Touched(); };
        _requestTabs.Items.Add(new TabItem { Header = "Params", Content = _params });
        _requestTabs.Items.Add(new TabItem { Header = "认证", Content = new ScrollViewer { Content = _auth, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        _requestTabs.Items.Add(new TabItem { Header = "请求头", Content = _headers });
        _requestTabs.Items.Add(new TabItem { Header = "请求体", Content = BuildBody() });
        _requestTabs.Items.Add(new TabItem { Header = "前置脚本", Content = _pre });
        _requestTabs.Items.Add(new TabItem { Header = "测试", Content = _tests });
        _requestTabs.Items.Add(new TabItem { Header = "设置", Content = BuildSettings() });
        _requestTabs.Items.Add(new TabItem { Header = "说明", Content = _description });
        panel.Children.Add(_requestTabs);
        _requestPanel = panel;
    }

    UIElement BuildBody()
    {
        var dock = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var (id, label) in new[] { (BodyModes.None, "none"), (BodyModes.Raw, "raw"), (BodyModes.UrlEncoded, "x-www-form-urlencoded"), (BodyModes.FormData, "form-data"), (BodyModes.Binary, "binary") })
        {
            var radio = new RadioButton { Content = label, Tag = id, GroupName = "apiBodyMode", Margin = new Thickness(0, 3, 14, 3), VerticalAlignment = VerticalAlignment.Center };
            radio.Checked += (_, _) =>
            {
                ShowBodyMode(id);
                if (_loading || Draft.Body.Mode == id) return;
                Draft.Body.Mode = id;
                Touched();
            };
            _bodyModes.Children.Add(radio);
        }
        foreach (var l in new[] { "json", "text", "xml", "html", "javascript" }) _language.Items.Add(l);
        _language.SelectionChanged += (_, _) =>
        {
            if (_loading || _language.SelectedItem is not string l) return;
            Draft.Body.Language = l;
            Touched();
        };
        _format = Ui.Button("格式化", FormatBody);
        _format.Margin = new Thickness(8, 0, 0, 0);
        var right = Ui.Row(_language, _format);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(_bodyModes);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_bodyHost);

        _raw.FontFamily = Ui.Mono;
        _raw.TextChanged += (_, _) => { if (_loading) return; Draft.Body.Raw = _raw.Text; Touched(); };
        _urlEncoded.Changed += () => { if (!_loading) Touched(); };
        _formData.Changed += () => { if (!_loading) Touched(); };
        _binary.TextChanged += (_, _) => { if (_loading) return; Draft.Body.File = _binary.Text; Touched(); };
        return dock;
    }

    void ShowBodyMode(string mode)
    {
        var raw = mode == BodyModes.Raw;
        _language.Visibility = _format.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        _bodyHost.Content = mode switch
        {
            BodyModes.Raw => _raw,
            BodyModes.UrlEncoded => _urlEncoded,
            BodyModes.FormData => _formData,
            BodyModes.Binary => BinaryPanel(),
            _ => new TextBlock { Text = "这个请求没有请求体", Foreground = Views.DialogWindow.HintBrush, Margin = new Thickness(0, 8, 0, 0) },
        };
    }

    UIElement BinaryPanel()
    {
        if (_binary.Parent is Panel old) old.Children.Remove(_binary);
        var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
        var pick = Ui.Button("选择文件…", () =>
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog(Owner) == true) _binary.Text = dialog.FileName;
        });
        pick.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(pick, Dock.Right);
        dock.Children.Add(pick);
        _binary.Tag = "要作为请求体发送的文件";
        dock.Children.Add(_binary);
        return dock;
    }

    void FormatBody()
    {
        var text = _raw.Text;
        if (text.Trim().Length == 0) return;
        try
        {
            if (Draft.Body.Language is "xml" or "html") _raw.Text = XDocument.Parse(text).ToString();
            else _raw.Text = ApiImport.PrettyJson(text);
        }
        catch (System.Xml.XmlException ex) { Ui.SetStatus(_status, "XML 格式不对：" + ex.Message, true); }
    }

    UIElement BuildSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Ui.Row(Ui.Label("超时（秒）"), _timeout));
        panel.Children.Add(_follow);
        panel.Children.Add(_ignoreSsl);
        var proxy = Ui.Row(Ui.Label("代理"), _proxy);
        proxy.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(proxy);
        panel.Children.Add(new TextBlock { Text = "代理写成 http://host:port，留空时用系统代理。", Foreground = Views.DialogWindow.HintBrush, Margin = new Thickness(0, 6, 0, 0) });
        _timeout.TextChanged += (_, _) =>
        {
            if (_loading || !int.TryParse(_timeout.Text.Trim(), out var s) || s <= 0) return;
            Draft.Options.TimeoutSeconds = s;
            Touched();
        };
        _follow.Click += (_, _) => { Draft.Options.FollowRedirects = _follow.IsChecked == true; Touched(); };
        _ignoreSsl.Click += (_, _) => { Draft.Options.IgnoreSsl = _ignoreSsl.IsChecked == true; Touched(); };
        _proxy.TextChanged += (_, _) => { if (_loading) return; Draft.Options.Proxy = _proxy.Text.Trim(); Touched(); };
        return panel;
    }

    void LoadEditor(ApiRequest r)
    {
        _loading = true;
        try
        {
            _name.Text = r.Name;
            if (!_method.Items.Contains(r.Method)) _method.Items.Add(r.Method);
            _method.SelectedItem = r.Method;
            _method.Foreground = ApiUi.MethodBrush(r.Method);
            _url.Text = r.Url;
            _params.Load(r.Params);
            _auth.Load(r.Auth);
            _headers.Load(r.Headers);
            _pre.Box.Text = r.PreScript;
            _tests.Box.Text = r.TestScript;
            _description.Text = r.Description;
            foreach (RadioButton radio in _bodyModes.Children) radio.IsChecked = (string)radio.Tag == r.Body.Mode;
            ShowBodyMode(r.Body.Mode);
            _language.SelectedItem = r.Body.Language;
            _raw.Text = r.Body.Raw;
            _urlEncoded.Load(r.Body.UrlEncoded);
            _formData.Load(r.Body.FormData);
            _binary.Text = r.Body.File;
            _timeout.Text = r.Options.TimeoutSeconds.ToString();
            _follow.IsChecked = r.Options.FollowRedirects;
            _ignoreSsl.IsChecked = r.Options.IgnoreSsl;
            _proxy.Text = r.Options.Proxy;
        }
        finally { _loading = false; }
    }

    // ---------- URL and params ----------

    void UrlChanged()
    {
        if (_loading || _current == null) return;
        Draft.Url = _url.Text;
        if (!_syncing)
        {
            // Keep disabled rows, which aren't in the URL, after the ones parsed from it.
            var parsed = UrlParams.Parse(_url.Text);
            parsed.AddRange(Draft.Params.Where(p => !p.Enabled && !p.IsEmpty));
            Draft.Params = parsed;
            _syncing = true;
            try { _params.Load(Draft.Params); }
            finally { _syncing = false; }
        }
        Touched();
    }

    void ParamsChanged()
    {
        if (_loading || _syncing || _current == null) return;
        _syncing = true;
        try
        {
            Draft.Url = UrlParams.Build(Draft.Url, Draft.Params);
            if (_url.Text != Draft.Url)
            {
                var caret = _url.CaretIndex;
                _url.Text = Draft.Url;
                _url.CaretIndex = Math.Min(caret, _url.Text.Length);
            }
        }
        finally { _syncing = false; }
        Touched();
    }

    /// <summary>Shows the value and scope of the {{variable}} under the mouse.</summary>
    void UrlHover(object sender, MouseEventArgs e)
    {
        var index = _url.GetCharacterIndexFromPoint(e.GetPosition(_url), true);
        var match = index < 0 ? null : ApiVariables.Pattern.Matches(_url.Text).Cast<System.Text.RegularExpressions.Match>().FirstOrDefault(m => index >= m.Index && index < m.Index + m.Length);
        if (match == null || _current == null) { _urlTip.IsOpen = false; return; }
        var name = match.Groups[1].Value;
        var vars = ContextFor(_current).Variables;
        var found = vars.Find(name);
        string text;
        if (found is not { } f) text = $"{name}\n未定义：当前环境、集合和全局变量里都没有";
        else
        {
            var secret = new[] { vars.Environment, vars.Collection, vars.Globals }.Any(list => list != null && list.Any(k => k.Key == name && k.Secret));
            text = $"{name} = {(secret ? "••••••" : f.Value)}\n来自：{f.Scope}";
        }
        _urlTip.Content = text;
        _urlTip.PlacementTarget = _url;
        _urlTip.IsOpen = true;
    }

    string DescribeInheritedAuth()
    {
        if (_current?.Saved == null) return "请求还没保存到集合里，继承时相当于不认证。";
        var path = ApiTree.PathOf(_data, _current.Saved) ?? new List<ApiFolder>();
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i].Auth.Type != AuthTypes.Inherit)
                return $"使用「{path[i].Name}」的认证：{AuthTypes.All.FirstOrDefault(a => a.Id == path[i].Auth.Type).Label}";
        return "上级都没有设置认证。";
    }

    // ---------- send ----------

    void UpdateSendButton()
    {
        var sending = _current?.Cancel != null;
        _send.Content = sending ? "取消" : "发送";
    }

    void SendOrCancel()
    {
        if (_current == null) return;
        if (_current.Cancel != null) { _current.Cancel.Cancel(); return; }
        Send(_current);
    }

    async void Send(Tab tab)
    {
        if (tab.Draft.Url.Trim().Length == 0) { Ui.SetStatus(_status, "先填 URL", true); return; }
        var cts = tab.Cancel = new CancellationTokenSource();
        UpdateSendButton();
        ShowSending();
        Ui.SetStatus(_status, "正在发送…");
        var ctx = ContextFor(tab);
        ExecResult result;
        try { result = await ApiEngine.ExecuteAsync(tab.Draft, ctx, cts.Token); }
        catch (OperationCanceledException) { result = new ExecResult { Error = "已取消" }; }
        catch (Exception ex) { result = new ExecResult { Error = ex.Message }; }
        finally { tab.Cancel = null; cts.Dispose(); }
        tab.Result = result;

        _data.History.Add(new ApiHistoryEntry { Request = tab.Draft.Clone(), Status = result.Response?.Status ?? 0, Milliseconds = result.Response?.Milliseconds ?? 0 });
        Persist();
        RefreshHistory();
        if (_current != tab) return;
        UpdateSendButton();
        ShowResult(result);
        var notes = new List<string>();
        if (result.Missing.Count > 0) notes.Add("未定义的变量：" + string.Join("、", result.Missing));
        if (result.Tests.Count > 0) notes.Add($"测试 {result.Tests.Count(t => t.Passed)}/{result.Tests.Count} 通过");
        if (result.Error != null) Ui.SetStatus(_status, result.Error + (notes.Count > 0 ? "；" + string.Join("；", notes) : ""), true);
        else Ui.SetStatus(_status, notes.Count > 0 ? string.Join("；", notes) : "完成", result.Missing.Count > 0 || result.Tests.Any(t => !t.Passed));
    }

    PreparedRequest? PrepareCurrent()
    {
        if (_current == null) return null;
        var ctx = ContextFor(_current);
        try { return ApiEngine.Prepare(Draft, ApiEngine.ResolveAuth(Draft.Auth, ctx.Path), ctx.Variables, new HashSet<string>()); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            Ui.SetStatus(_status, ex.Message, true);
            return null;
        }
    }

    void ShowCode()
    {
        if (PrepareCurrent() is { } p) ApiDialogs.Code(Owner, p);
    }

    // ---------- AI ----------

    async Task<string?> AskAi(string prompt, string busy)
    {
        if (_ai == null) { Ui.SetStatus(_status, "AI 助手没有开启", true); return null; }
        Ui.SetStatus(_status, busy);
        try { return await _ai(prompt); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Ui.SetStatus(_status, "AI 出错：" + ex.Message, true);
            return null;
        }
    }

    async void AiGenerate()
    {
        var want = Ai.AiService.AskText(Owner, "AI 生成请求", "用一句话描述要发的请求，例如「用 POST 给 httpbin 发一个带 name 和 age 的 JSON，并检查返回 200」", "");
        if (string.IsNullOrWhiteSpace(want)) return;
        var vars = string.Join("、", (ActiveEnvironment?.Variables ?? new List<KeyValue>()).Concat(_data.Globals).Where(v => v.Key.Length > 0).Select(v => "{{" + v.Key + "}}").Distinct());
        var prompt = "根据描述写一个 HTTP 请求。只回答一个 JSON 对象，不要解释，字段：name（简短中文名）、method、url、headers（对象）、body（字符串或 JSON，没有就空字符串）、tests（Postman 风格的 pm.test 测试脚本，可以为空）。"
                     + (vars.Length > 0 ? "\n可以用的变量：" + vars : "") + "\n描述：" + want;
        var answer = await AskAi(prompt, "AI 正在写请求…");
        if (answer == null) return;
        var r = ApiImport.ParseAi(answer);
        if (r == null) { Ui.SetStatus(_status, "AI 的回答里没有可用的请求", true); return; }
        OpenTab(null, r);
        Ui.SetStatus(_status, "AI 写好了，检查后发送");
    }
}
