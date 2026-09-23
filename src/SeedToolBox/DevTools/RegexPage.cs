using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>.NET regex tester: matches highlighted live, groups listed, and replace preview.</summary>
sealed class RegexPage : DockPanel
{
    const int MaxMatches = 2000;

    readonly TextBox _pattern = Ui.Field();
    readonly TextBox _replace = Ui.Field();
    readonly CheckBox _ignoreCase = Check("忽略大小写"), _multiline = Check("多行 ^$"), _singleline = Check("点匹配换行");
    readonly TextBox _input = Ui.Area(wrap: true);
    readonly RichTextBox _highlight = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 6, 4, 6) };
    readonly TextBox _details = Ui.Area();
    readonly TextBox _preview = Ui.Area(wrap: true);
    readonly TabControl _tabs = new();
    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public RegexPage()
    {
        var header = Ui.Header("正则测试", ".NET 正则表达式，实时高亮匹配、列出分组，并预览替换结果");
        _pattern.FontFamily = Ui.Mono;
        _replace.FontFamily = Ui.Mono;
        _replace.ToolTip = "替换为，可用 $1、${name}、$0";
        _details.IsReadOnly = true;
        _highlight.Style = null;
        _highlight.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _highlight.Document.PagePadding = new Thickness(0);

        var patternRow = Row("正则", _pattern, new StackPanel { Orientation = Orientation.Horizontal, Children = { _ignoreCase, _multiline, _singleline } });
        var replaceRow = Row("替换为", _replace, Ui.Button("复制替换结果", CopyReplaced));
        _pattern.Text = @"(\w+)@(\w+\.\w+)";
        _input.Text = "联系 alice@example.com 或 bob@test.org 获取帮助。";

        _tabs.Items.Add(new TabItem { Header = "高亮", Content = _highlight });
        _tabs.Items.Add(new TabItem { Header = "匹配与分组", Content = _details });
        _tabs.Items.Add(new TabItem { Header = "替换预览", Content = _preview });
        _preview.IsReadOnly = true;

        foreach (var box in new[] { _pattern, _replace, _input }) box.TextChanged += (_, _) => Schedule();
        foreach (var check in new[] { _ignoreCase, _multiline, _singleline }) check.Click += (_, _) => Schedule();
        _timer.Tick += (_, _) => { _timer.Stop(); Run(); };

        Ui.FileDrop(_input, files => Ui.LoadText(_input, files[0], _status));
        var body = Ui.Columns(Ui.Titled("测试文本", _input), _tabs);
        SetDock(header, Dock.Top);
        SetDock(patternRow, Dock.Top);
        SetDock(replaceRow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(patternRow);
        Children.Add(replaceRow);
        Children.Add(_status);
        Children.Add(body);
        Run();
    }

    static CheckBox Check(string text) => new() { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    static DockPanel Row(string label, TextBox box, UIElement right)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var text = Ui.Label(label);
        text.Width = 52;
        DockPanel.SetDock(text, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        if (right is FrameworkElement f) f.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(text);
        row.Children.Add(right);
        row.Children.Add(box);
        return row;
    }

    void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    Regex? Build()
    {
        var options = RegexOptions.None;
        if (_ignoreCase.IsChecked == true) options |= RegexOptions.IgnoreCase;
        if (_multiline.IsChecked == true) options |= RegexOptions.Multiline;
        if (_singleline.IsChecked == true) options |= RegexOptions.Singleline;
        try
        {
            // The timeout keeps catastrophic backtracking from freezing the window
            return new Regex(_pattern.Text, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            Ui.SetStatus(_status, "正则有误：" + ex.Message, true);
            return null;
        }
    }

    void Run()
    {
        var text = _input.Text;
        var paragraph = new Paragraph();
        _highlight.Document.Blocks.Clear();
        _highlight.Document.Blocks.Add(paragraph);
        _details.Clear();
        _preview.Clear();
        if (_pattern.Text.Length == 0)
        {
            paragraph.Inlines.Add(new Run(text));
            _status.Text = "";
            return;
        }
        var regex = Build();
        if (regex == null) { paragraph.Inlines.Add(new Run(text)); return; }

        try
        {
            var matches = new List<Match>();
            for (var m = regex.Match(text); m.Success && matches.Count < MaxMatches; m = m.NextMatch()) matches.Add(m);

            var colors = new[] { Color.FromArgb(0x55, 0xFF, 0xC8, 0x3D), Color.FromArgb(0x50, 0x3D, 0xA5, 0xFF) };
            int pos = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                if (m.Index > pos) paragraph.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                var run = new Run(m.Length == 0 ? "¦" : m.Value) { Background = new SolidColorBrush(colors[i % 2]) };
                if (m.Length == 0) run.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
                paragraph.Inlines.Add(run);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) paragraph.Inlines.Add(new Run(text.Substring(pos)));

            _details.Text = Describe(regex, matches);
            _preview.Text = regex.Replace(text, _replace.Text);
            Ui.SetStatus(_status, matches.Count == 0 ? "没有匹配" : $"{matches.Count}{(matches.Count >= MaxMatches ? "+" : "")} 处匹配，{regex.GetGroupNumbers().Length - 1} 个分组");
        }
        catch (RegexMatchTimeoutException)
        {
            Ui.SetStatus(_status, "匹配超时（超过 1 秒），正则可能存在灾难性回溯", true);
        }
    }

    static string Describe(Regex regex, List<Match> matches)
    {
        var sb = new StringBuilder();
        var names = regex.GetGroupNames();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            sb.AppendLine($"#{i + 1}  [{m.Index}, 长度 {m.Length}]  {Escape(m.Value)}");
            foreach (var name in names.Skip(1))
            {
                var g = m.Groups[name];
                sb.AppendLine($"     {(char.IsDigit(name[0]) ? "$" + name : name)}: {(g.Success ? Escape(g.Value) : "（未匹配）")}");
            }
        }
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    void CopyReplaced()
    {
        var regex = Build();
        if (regex == null) return;
        try { ScreenToolService.CopyText(regex.Replace(_input.Text, _replace.Text)); Ui.SetStatus(_status, "已复制替换结果"); }
        catch (RegexMatchTimeoutException) { Ui.SetStatus(_status, "匹配超时", true); }
    }
}
