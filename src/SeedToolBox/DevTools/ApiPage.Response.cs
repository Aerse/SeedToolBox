using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.DevTools;

sealed partial class ApiPage
{
    const int MaxRawChars = 2 * 1024 * 1024;

    DockPanel _responsePanel = null!;
    readonly GridSplitter _splitter = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    readonly TextBlock _statusCode = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _timing = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TabControl _responseTabs = new();
    readonly TabControl _bodyViews = new() { TabStripPlacement = Dock.Top };
    readonly JsonTree _jsonTree = new();
    readonly TextBox _pretty = Ui.Area();
    readonly TextBox _rawView = Ui.Area();
    readonly ContentControl _prettyHost = new();
    readonly ContentControl _preview = new();
    readonly ListView _responseHeaders = new();
    readonly ListView _cookies = new();
    readonly StackPanel _testList = new();
    readonly TextBox _console = Ui.Area();
    Panel _responseActions = null!;
    TabItem _testsTab = null!, _consoleTab = null!;

    void BuildResponse()
    {
        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var layout = ApiUi.Icon("", "切换上下 / 左右布局", () => { _data.SideBySide = !_data.SideBySide; ApplyLayout(); ScheduleSave(); });
        var actions = Ui.Row(
            Ui.Button("保存到文件", SaveResponse),
            Ui.Button("存为示例", SaveExample),
            Ui.Button("复制", () => { if (_current?.Result?.Response is { } r) Clipboard.SetText(r.Text); }),
            Ui.Button("AI 解释", AiExplain),
            Ui.Button("AI 写测试", AiTests),
            layout);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "响应", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, _statusCode, _timing } };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var child in actions.Children.Cast<UIElement>().ToList()) { actions.Children.Remove(child); ((FrameworkElement)child).Margin = new Thickness(6, 2, 0, 2); wrap.Children.Add(child); }
        header.Children.Add(wrap);
        _responseActions = wrap;
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        foreach (var box in new[] { _pretty, _rawView, _console })
        {
            box.IsReadOnly = true;
            box.FontFamily = Ui.Mono;
        }
        var prettyDock = new DockPanel();
        var treeButtons = Ui.Row(Ui.Button("全部展开", () => _jsonTree.ExpandAll(true)), Ui.Button("全部折叠", () => _jsonTree.ExpandAll(false)));
        treeButtons.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(treeButtons, Dock.Top);
        prettyDock.Children.Add(treeButtons);
        prettyDock.Children.Add(_prettyHost);
        _bodyViews.Items.Add(new TabItem { Header = "Pretty", Content = prettyDock, Tag = treeButtons });
        _bodyViews.Items.Add(new TabItem { Header = "Raw", Content = _rawView });
        _bodyViews.Items.Add(new TabItem { Header = "Preview", Content = _preview });

        _responseHeaders.View = GridOf(("名称", "Key", 200), ("值", "Value", 500));
        _cookies.View = GridOf(("名称", "Name", 140), ("值", "Value", 220), ("域", "Domain", 140), ("路径", "Path", 80), ("过期", "Expires", 140), ("HttpOnly", "HttpOnly", 70), ("Secure", "Secure", 60));
        _responseTabs.Items.Add(new TabItem { Header = "响应体", Content = _bodyViews });
        _responseTabs.Items.Add(new TabItem { Header = "响应头", Content = _responseHeaders });
        _responseTabs.Items.Add(new TabItem { Header = "Cookies", Content = _cookies });
        _testsTab = new TabItem { Header = "测试结果", Content = new ScrollViewer { Content = _testList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        _responseTabs.Items.Add(_testsTab);
        _consoleTab = new TabItem { Header = "控制台", Content = _console };
        _responseTabs.Items.Add(_consoleTab);
        panel.Children.Add(_responseTabs);
        _responsePanel = panel;
    }

    static GridView GridOf(params (string Header, string Path, double Width)[] columns)
    {
        var view = new GridView();
        foreach (var (h, p, w) in columns) view.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(p) });
        return view;
    }

    /// <summary>Request above response, or side by side.</summary>
    void ApplyLayout()
    {
        _split.Children.Clear();
        _split.RowDefinitions.Clear();
        _split.ColumnDefinitions.Clear();
        var side = _data.SideBySide;
        if (side)
        {
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            _splitter.Width = 6; _splitter.Height = double.NaN;
            _splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            _splitter.Height = 6; _splitter.Width = double.NaN;
            _splitter.ResizeDirection = GridResizeDirection.Rows;
        }
        _responsePanel.Margin = side ? new Thickness(6, 0, 0, 0) : new Thickness(0, 6, 0, 0);
        foreach (var (e, i) in new UIElement[] { _requestPanel, _splitter, _responsePanel }.Select((e, i) => (e, i)))
        {
            System.Windows.Controls.Grid.SetRow(e, side ? 0 : i);
            System.Windows.Controls.Grid.SetColumn(e, side ? i : 0);
            _split.Children.Add(e);
        }
    }

    void ShowSending()
    {
        _statusCode.Text = "发送中…";
        _statusCode.Foreground = ApiUi.Res("TextBrush");
        _timing.Text = "";
    }

    void ShowResult(ExecResult? result)
    {
        var r = result?.Response;
        _responseActions.IsEnabled = r != null;
        if (result == null)
        {
            _statusCode.Text = "还没有发送";
            _statusCode.Foreground = Views.DialogWindow.HintBrush;
            _timing.Text = "";
        }
        else if (r == null)
        {
            _statusCode.Text = result.Skipped ? "已跳过" : result.Error ?? "没有响应";
            _statusCode.Foreground = ApiUi.Res("DangerBrush");
            _timing.Text = "";
        }
        else
        {
            _statusCode.Text = $"{r.Status} {r.Reason}";
            _statusCode.Foreground = ApiUi.StatusBrush(r.Status);
            _timing.Text = $"{r.Milliseconds} ms · {Ui.FormatSize(r.Bytes.LongLength)}";
            _timing.ToolTip = r.FinalUrl;
        }

        // body
        var text = r?.Text ?? "";
        _rawView.Text = text.Length > MaxRawChars ? text.Substring(0, MaxRawChars) + "\n…（只显示前 2 MB）" : text;
        var treeButtons = (FrameworkElement)((TabItem)_bodyViews.Items[0]).Tag;
        treeButtons.Visibility = Visibility.Collapsed;
        JToken? json = null;
        if (r != null && r.IsJson && text.Length < 20 * 1024 * 1024)
        {
            try { json = JToken.Parse(text); } catch (JsonReaderException) { }
        }
        if (json != null)
        {
            _jsonTree.Show(json);
            _prettyHost.Content = _jsonTree;
            treeButtons.Visibility = Visibility.Visible;
        }
        else
        {
            _pretty.Text = r != null && (r.ContentType.Contains("xml") || r.IsHtml) ? TryXml(_rawView.Text) : _rawView.Text;
            _prettyHost.Content = _pretty;
        }
        _preview.Content = r == null ? null : Preview(r);

        _responseHeaders.ItemsSource = r?.Headers;
        _cookies.ItemsSource = r?.Cookies;
        ((TabItem)_responseTabs.Items[1]).Header = r != null ? $"响应头 ({r.Headers.Count})" : "响应头";
        ((TabItem)_responseTabs.Items[2]).Header = r != null && r.Cookies.Count > 0 ? $"Cookies ({r.Cookies.Count})" : "Cookies";

        // tests and console
        _testList.Children.Clear();
        var tests = result?.Tests ?? new List<TestResult>();
        foreach (var t in tests)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var badge = new Border
            {
                Background = t.Passed ? ApiUi.Frozen(0x2E, 0x9E, 0x5B) : ApiUi.Res("DangerBrush"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = t.Passed ? "通过" : "失败", Foreground = Brushes.White, FontSize = 11 },
            };
            DockPanel.SetDock(badge, Dock.Left);
            line.Children.Add(badge);
            line.Children.Add(new TextBlock { Text = t.Name + (t.Passed ? "" : "\n" + t.Error), TextWrapping = TextWrapping.Wrap });
            _testList.Children.Add(line);
        }
        if (tests.Count == 0) _testList.Children.Add(new TextBlock { Text = "这次没有运行测试。在请求的「测试」页写 pm.test(...)。", Foreground = Views.DialogWindow.HintBrush });
        _testsTab.Header = tests.Count > 0 ? $"测试结果 ({tests.Count(t => t.Passed)}/{tests.Count})" : "测试结果";
        var console = new List<string>(result?.Console ?? new List<string>());
        if (result?.Error != null) console.Add("错误：" + result.Error);
        _console.Text = string.Join(Environment.NewLine, console);
        _consoleTab.Header = console.Count > 0 ? $"控制台 ({console.Count})" : "控制台";
    }

    static string TryXml(string text)
    {
        try { return System.Xml.Linq.XDocument.Parse(text).ToString(); }
        catch (System.Xml.XmlException) { return text; }
    }

    UIElement Preview(ApiResponse r)
    {
        if (r.IsImage)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(r.Bytes);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Image { Source = image, Stretch = Stretch.None } };
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException) { return new TextBlock { Text = "图片无法显示：" + ex.Message }; }
        }
        if (r.IsHtml)
        {
            var browser = new WebBrowser();
            browser.Navigated += (_, _) => SilenceScriptErrors(browser);
            var html = r.Text;
            var head = "<meta charset=\"utf-8\"><base href=\"" + System.Net.WebUtility.HtmlEncode(r.FinalUrl) + "\">";
            html = Regex.IsMatch(html, "<head[^>]*>", RegexOptions.IgnoreCase) ? Regex.Replace(html, "<head[^>]*>", m => m.Value + head, RegexOptions.IgnoreCase) : head + html;
            browser.NavigateToString(html.Length > 0 ? html : " ");
            return browser;
        }
        return new TextBlock { Text = "Preview 只能显示 HTML 和图片；这次的内容类型是 " + (r.ContentType.Length > 0 ? r.ContentType : "未知"), Foreground = Views.DialogWindow.HintBrush, Margin = new Thickness(0, 8, 0, 0) };
    }

    static void SilenceScriptErrors(WebBrowser browser)
    {
        var field = typeof(WebBrowser).GetField("_axIWebBrowser2", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var com = field?.GetValue(browser);
        com?.GetType().InvokeMember("Silent", System.Reflection.BindingFlags.SetProperty, null, com, new object[] { true });
    }

    void ShowExample(ApiRequest request, ApiExample example)
    {
        var bytes = Encoding.UTF8.GetBytes(example.Body);
        var contentType = example.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var result = new ExecResult
        {
            Response = new ApiResponse
            {
                Status = example.Status, Reason = example.StatusText + "（示例）", Headers = example.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList(),
                Bytes = bytes, Text = example.Body, ContentType = contentType, FinalUrl = request.Url,
            },
        };
        var tab = _tabs.FirstOrDefault(t => t.Saved == request);
        if (tab == null) { OpenTab(request); tab = _current!; }
        else Select(tab);
        tab.Result = result;
        ShowResult(result);
        Ui.SetStatus(_status, "正在查看示例「" + example.Name + "」");
    }

    // ---------- response actions ----------

    void SaveResponse()
    {
        if (_current?.Result?.Response is not { } r) return;
        var ext = r.IsJson ? ".json" : r.IsHtml ? ".html" : r.IsImage ? "." + r.ContentType.Substring(6).Split(';', '+')[0] : r.ContentType.Contains("xml") ? ".xml" : ".txt";
        var dialog = new SaveFileDialog { FileName = "response" + ext, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Owner) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, r.Bytes);
            Ui.SetStatus(_status, "已保存到 " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.SetStatus(_status, "保存失败：" + ex.Message, true); }
    }

    void SaveExample()
    {
        if (_current?.Result?.Response is not { } r) return;
        if (_current.Saved == null) { Ui.SetStatus(_status, "先把请求保存到集合，才能存示例", true); return; }
        var name = Ai.AiService.AskText(Owner, "存为示例", "示例名称", $"{r.Status} {r.Reason}".Trim());
        if (string.IsNullOrWhiteSpace(name)) return;
        _current.Saved.Examples.Add(new ApiExample
        {
            Name = name!.Trim(), Status = r.Status, StatusText = r.Reason, Body = r.Text,
            Headers = r.Headers.Select(h => new KeyValue { Key = h.Key, Value = h.Value }).ToList(),
        });
        _expanded.Add(_current.Saved);
        Persist();
        RefreshTree();
        Ui.SetStatus(_status, "已存为示例");
    }

    /// <summary>The exchange as text for the assistant, with credentials masked and the body cut short.</summary>
    string Exchange(ExecResult result, int bodyLimit)
    {
        static string Mask(string key, string value) =>
            key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || key.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("api-key", StringComparison.OrdinalIgnoreCase) >= 0 ? "***" : value;
        var sb = new StringBuilder();
        var p = result.Request!;
        sb.AppendLine($"{p.Method} {p.Url}");
        foreach (var h in p.Headers) sb.AppendLine($"{h.Key}: {Mask(h.Key, h.Value)}");
        if (p.BodyText is { Length: > 0 } body) sb.AppendLine().AppendLine(body.Length > 2000 ? body.Substring(0, 2000) + "…" : body);
        var r = result.Response!;
        sb.AppendLine().AppendLine($"HTTP {r.Status} {r.Reason}  ({r.Milliseconds} ms)");
        foreach (var h in r.Headers) sb.AppendLine($"{h.Key}: {Mask(h.Key, h.Value)}");
        sb.AppendLine().Append(r.Text.Length > bodyLimit ? r.Text.Substring(0, bodyLimit) + "\n…（后面省略）" : r.Text);
        return sb.ToString();
    }

    async void AiExplain()
    {
        if (_current?.Result is not { Response: not null, Request: not null } result) return;
        var answer = await AskAi("用中文简要解释这次 HTTP 请求和响应：状态码意味着什么，响应里的关键字段，有没有错误或可疑的地方，以及怎么改进。\n\n" + Exchange(result, 6000), "AI 正在分析响应…");
        if (answer == null) return;
        Ui.SetStatus(_status, "");
        ApiDialogs.ShowText(Owner, "AI 解释响应", answer);
    }

    async void AiTests()
    {
        var tab = _current;
        if (tab?.Result is not { Response: not null, Request: not null } result) return;
        var answer = await AskAi("根据这次请求和响应，写 Postman 风格的测试脚本：用 pm.test 和 pm.expect 检查状态码、响应时间、关键字段的存在和类型。只回答 JavaScript 代码，不要解释。\n\n" + Exchange(result, 6000), "AI 正在写测试…");
        if (answer == null || _current != tab) return;
        var code = ApiImport.StripFence(answer);
        tab.Draft.TestScript = tab.Draft.TestScript.Trim().Length > 0 ? tab.Draft.TestScript.TrimEnd() + "\r\n\r\n" + code : code;
        LoadEditor(tab.Draft);
        Touched();
        _requestTabs.SelectedIndex = 5;
        Ui.SetStatus(_status, "测试已加到「测试」页，再发送一次就会运行");
    }
}
