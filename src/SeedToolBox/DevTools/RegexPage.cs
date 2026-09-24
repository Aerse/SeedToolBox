using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>.NET regex tester: matches and groups highlighted live, groups listed, replace preview, a cheat sheet and saved favourites.</summary>
sealed class RegexPage : DockPanel
{
    const int MaxMatches = 2000;
    static readonly string FavoritesPath = Path.Combine(AppPaths.Data, "regex-favorites.json");

    static readonly (RegexOptions Option, string Text, string Tip)[] OptionList =
    {
        (RegexOptions.IgnoreCase, "忽略大小写", "IgnoreCase"),
        (RegexOptions.Multiline, "多行 ^$", "Multiline：^ 和 $ 匹配每一行的开头结尾"),
        (RegexOptions.Singleline, "点匹配换行", "Singleline：. 也匹配 \\n"),
        (RegexOptions.IgnorePatternWhitespace, "忽略空白", "IgnorePatternWhitespace：忽略正则里的空白，# 之后是注释"),
        (RegexOptions.ExplicitCapture, "仅命名捕获", "ExplicitCapture：只有 (?<name>…) 才捕获，普通括号不捕获"),
        (RegexOptions.RightToLeft, "从右向左", "RightToLeft：从文本末尾开始向前匹配"),
    };

    static readonly (string Pattern, string Description)[] Tokens =
    {
        (".", "任意字符（默认不含换行）"), (@"\d", "数字"), (@"\D", "非数字"), (@"\w", "单词字符（字母、数字、下划线、汉字）"), (@"\W", "非单词字符"),
        (@"\s", "空白"), (@"\S", "非空白"), (@"\b", "单词边界"), ("^", "行首 / 文本开头"), ("$", "行尾 / 文本结尾"), ("[abc]", "字符集合"),
        ("[^abc]", "不在集合中的字符"), ("[a-z]", "字符范围"), ("*", "0 次或多次"), ("+", "1 次或多次"), ("?", "0 或 1 次"), ("{2,5}", "2 到 5 次"),
        ("*?", "尽量少地重复（懒惰）"), ("a|b", "或"), ("(…)", "捕获分组"), ("(?:…)", "不捕获的分组"), ("(?<name>…)", "命名分组"),
        (@"\1", "引用第 1 组"), (@"\k<name>", "引用命名分组"), ("(?=…)", "后面是…（正向先行断言）"), ("(?!…)", "后面不是…"),
        ("(?<=…)", "前面是…（正向后行断言）"), ("(?<!…)", "前面不是…"), ("(?i)", "从此处开始忽略大小写"), (@"\p{IsCJKUnifiedIdeographs}", "汉字"),
    };

    static readonly (string Pattern, string Description)[] Common =
    {
        (@"[\w.+-]+@[\w-]+(\.[\w-]+)+", "邮箱"),
        (@"https?://[^\s/$.?#].[^\s]*", "网址"),
        (@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", "IPv4 地址"),
        (@"1[3-9]\d{9}", "中国大陆手机号"),
        (@"\d{17}[\dXx]", "18 位身份证号"),
        (@"\d{4}-\d{1,2}-\d{1,2}", "日期 2026-09-24"),
        (@"([01]?\d|2[0-3]):[0-5]\d(:[0-5]\d)?", "时间 20:30 / 20:30:15"),
        (@"#(?:[0-9a-fA-F]{3}){1,2}\b", "十六进制颜色"),
        (@"[\u4e00-\u9fa5]+", "连续汉字"),
        (@"-?\d+(\.\d+)?", "整数或小数"),
        (@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "UUID"),
        (@"<(\w+)[^>]*>(.*?)</\1>", "成对的 HTML 标签"),
        (@"^\s*$", "空行（配合多行）"),
        (@"^\s+|\s+$", "首尾空白"),
        (@"[1-9]\d{5}(?!\d)", "邮政编码"),
        (@"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*", "Windows 路径"),
    };

    readonly TextBox _pattern = Ui.Field();
    readonly TextBox _replace = Ui.Field();
    readonly CheckBox[] _options = OptionList.Select(o => Check(o.Text, o.Tip)).ToArray();
    readonly TextBox _input = Ui.Area(wrap: true);
    readonly RichTextBox _highlight = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 6, 4, 6) };
    readonly TextBox _details = Ui.Area();
    readonly TextBox _preview = Ui.Area(wrap: true);
    readonly TabControl _tabs = new();
    readonly ListBox _favorites = new() { FontFamily = Ui.Mono, FontSize = 13 };
    readonly TextBox _favoriteName = Ui.Field(180);
    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    List<Favorite> _saved = new();

    sealed class Favorite
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Replace { get; set; } = "";
        public int Options { get; set; }
    }

    public RegexPage()
    {
        var header = Ui.Header("正则测试", ".NET 正则表达式，实时高亮匹配和分组、列出分组、预览替换，附速查表和收藏");
        _pattern.FontFamily = Ui.Mono;
        _replace.FontFamily = Ui.Mono;
        _replace.ToolTip = "替换为，可用 $1、${name}、$0";
        _details.IsReadOnly = true;
        _highlight.Style = null;
        _highlight.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _highlight.Document.PagePadding = new Thickness(0);

        var optionPanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var check in _options) optionPanel.Children.Add(check);
        var patternRow = Row("正则", _pattern, Ui.Button("收藏", AddFavorite));
        var optionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 0, 0, 10), Children = { optionPanel } };
        var replaceRow = Row("替换为", _replace, Ui.Button("复制替换结果", CopyReplaced));
        _pattern.Text = @"(\w+)@(\w+\.\w+)";
        _input.Text = "联系 alice@example.com 或 bob@test.org 获取帮助。";

        _tabs.Items.Add(new TabItem { Header = "高亮", Content = _highlight });
        _tabs.Items.Add(new TabItem { Header = "匹配与分组", Content = _details });
        _tabs.Items.Add(new TabItem { Header = "替换预览", Content = _preview });
        _tabs.Items.Add(new TabItem { Header = "速查", Content = BuildCheatSheet() });
        _tabs.Items.Add(new TabItem { Header = "收藏", Content = BuildFavorites() });
        _preview.IsReadOnly = true;

        foreach (var box in new[] { _pattern, _replace, _input }) box.TextChanged += (_, _) => Schedule();
        foreach (var check in _options) check.Click += (_, _) => Schedule();
        _timer.Tick += (_, _) => { _timer.Stop(); Run(); };

        Ui.FileDrop(_input, files => Ui.LoadText(_input, files[0], _status));
        var body = Ui.Columns(Ui.Titled("测试文本", _input), _tabs);
        SetDock(header, Dock.Top);
        SetDock(patternRow, Dock.Top);
        SetDock(optionRow, Dock.Top);
        SetDock(replaceRow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(patternRow);
        Children.Add(optionRow);
        Children.Add(replaceRow);
        Children.Add(_status);
        Children.Add(body);
        LoadFavorites();
        Run();
    }

    static CheckBox Check(string text, string tip) => new() { Content = text, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

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

    RegexOptions Options
    {
        get
        {
            var options = RegexOptions.None;
            for (int i = 0; i < _options.Length; i++)
                if (_options[i].IsChecked == true) options |= OptionList[i].Option;
            return options;
        }
        set
        {
            for (int i = 0; i < _options.Length; i++) _options[i].IsChecked = (value & OptionList[i].Option) != 0;
        }
    }

    Regex? Build()
    {
        try
        {
            // The timeout keeps catastrophic backtracking from freezing the window
            return new Regex(_pattern.Text, Options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            Ui.SetStatus(_status, "正则有误：" + ex.Message, true);
            return null;
        }
    }

    static readonly Color[] MatchColors = { Color.FromArgb(0x55, 0xFF, 0xC8, 0x3D), Color.FromArgb(0x50, 0x3D, 0xA5, 0xFF) };

    static readonly Color[] GroupColors =
    {
        Color.FromArgb(0x70, 0x4C, 0xD9, 0x64), Color.FromArgb(0x70, 0xFF, 0x6B, 0x8B), Color.FromArgb(0x70, 0xA8, 0x7B, 0xFF),
        Color.FromArgb(0x70, 0x2E, 0xC4, 0xC4), Color.FromArgb(0x70, 0xFF, 0x9F, 0x40), Color.FromArgb(0x70, 0x9C, 0xCC, 0x3C),
    };

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
            var ordered = regex.RightToLeft ? matches.OrderBy(m => m.Index).ToList() : matches;
            var numbers = regex.GetGroupNumbers().Where(n => n > 0).ToArray();

            int pos = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                if (m.Index < pos) continue;
                if (m.Index > pos) paragraph.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                var matchBrush = new SolidColorBrush(MatchColors[i % 2]);
                if (m.Length == 0)
                {
                    paragraph.Inlines.Add(new Run("¦") { Background = matchBrush, Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
                    continue;
                }
                AddMatch(paragraph, m, numbers, matchBrush);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) paragraph.Inlines.Add(new Run(text.Substring(pos)));

            _details.Text = Describe(regex, matches);
            _preview.Text = regex.Replace(text, _replace.Text);
            Ui.SetStatus(_status, matches.Count == 0 ? "没有匹配" : $"{matches.Count}{(matches.Count >= MaxMatches ? "+" : "")} 处匹配，{numbers.Length} 个分组");
        }
        catch (RegexMatchTimeoutException)
        {
            Ui.SetStatus(_status, "匹配超时（超过 1 秒），正则可能存在灾难性回溯", true);
        }
    }

    /// <summary>Colours each character of a match by the innermost group that captured it.</summary>
    static void AddMatch(Paragraph paragraph, Match m, int[] numbers, Brush matchBrush)
    {
        var owner = new int[m.Length];
        foreach (var n in numbers)
        {
            var g = m.Groups[n];
            if (!g.Success) continue;
            // Groups later in the pattern are nested or to the right, so letting them win shows the innermost
            for (int k = Math.Max(g.Index, m.Index); k < Math.Min(g.Index + g.Length, m.Index + m.Length); k++) owner[k - m.Index] = n;
        }
        int start = 0;
        for (int k = 1; k <= owner.Length; k++)
        {
            if (k < owner.Length && owner[k] == owner[start]) continue;
            var run = new Run(m.Value.Substring(start, k - start));
            if (owner[start] == 0) run.Background = matchBrush;
            else
            {
                run.Background = new SolidColorBrush(GroupColors[(Array.IndexOf(numbers, owner[start])) % GroupColors.Length]);
                run.ToolTip = $"分组 {m.Groups[owner[start]].Name}";
            }
            paragraph.Inlines.Add(run);
            start = k;
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

    // Cheat sheet

    UIElement BuildCheatSheet()
    {
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Add(string caption)
        {
            list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) },
                IsEnabled = false,
            });
        }
        void Entries((string Pattern, string Description)[] entries)
        {
            foreach (var (pattern, description) in entries)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var code = new TextBlock { Text = pattern, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pattern };
                var desc = new TextBlock { Text = description, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"] };
                Grid.SetColumn(desc, 1);
                grid.Children.Add(code);
                grid.Children.Add(desc);
                var item = new ListBoxItem { Content = grid, Cursor = Cursors.Hand };
                item.PreviewMouseLeftButtonUp += (_, _) => Insert(pattern);
                list.Items.Add(item);
            }
        }
        Add("语法（点击插入到光标处）");
        Entries(Tokens);
        Add("常用正则");
        Entries(Common);
        return list;
    }

    void Insert(string snippet)
    {
        // "(…)" style entries put the caret where the content goes
        int hole = snippet.IndexOf('…');
        var text = snippet.Replace("…", "");
        int start = _pattern.SelectionStart;
        _pattern.SelectedText = text;
        _pattern.Focus();
        _pattern.Select(start + (hole >= 0 ? hole : text.Length), 0);
    }

    // Favourites

    UIElement BuildFavorites()
    {
        _favoriteName.ToolTip = "收藏的名称，留空则用正则本身";
        var load = Ui.Button("载入", () => LoadFavorite(_favorites.SelectedIndex));
        var remove = Ui.Button("删除", RemoveFavorite);
        var top = Ui.Row(Ui.Label("名称"), _favoriteName, Ui.Label("", 8), Ui.Button("收藏当前", AddFavorite, accent: true), load, remove);
        _favorites.MouseDoubleClick += (_, _) => LoadFavorite(_favorites.SelectedIndex);
        _favorites.ToolTip = "双击载入";
        var panel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_favorites);
        return panel;
    }

    void LoadFavorites()
    {
        try
        {
            if (File.Exists(FavoritesPath))
                _saved = JsonConvert.DeserializeObject<List<Favorite>>(File.ReadAllText(FavoritesPath)) ?? new();
        }
        catch (Exception ex) { Log.Error("Reading regex favorites failed", ex); }
        RefreshFavorites();
    }

    void SaveFavorites()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FavoritesPath, JsonConvert.SerializeObject(_saved, Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex) { Ui.SetStatus(_status, "保存收藏失败：" + ex.Message, true); }
    }

    void RefreshFavorites()
    {
        _favorites.Items.Clear();
        foreach (var f in _saved) _favorites.Items.Add(f.Name == f.Pattern ? f.Pattern : $"{f.Name}    {f.Pattern}");
    }

    void AddFavorite()
    {
        if (_pattern.Text.Length == 0) { Ui.SetStatus(_status, "正则是空的", true); return; }
        var name = _favoriteName.Text.Trim();
        if (name.Length == 0) name = _pattern.Text;
        var favorite = new Favorite { Name = name, Pattern = _pattern.Text, Replace = _replace.Text, Options = (int)Options };
        int existing = _saved.FindIndex(f => f.Name == name);
        if (existing >= 0) _saved[existing] = favorite;
        else _saved.Add(favorite);
        SaveFavorites();
        RefreshFavorites();
        _favoriteName.Clear();
        Ui.SetStatus(_status, existing >= 0 ? $"已更新收藏“{name}”" : $"已收藏“{name}”");
    }

    void LoadFavorite(int index)
    {
        if (index < 0 || index >= _saved.Count) { Ui.SetStatus(_status, "请先选择一个收藏", true); return; }
        var f = _saved[index];
        Options = (RegexOptions)f.Options;
        _pattern.Text = f.Pattern;
        _replace.Text = f.Replace;
        Run();
        Ui.SetStatus(_status, $"已载入“{f.Name}”");
    }

    void RemoveFavorite()
    {
        int index = _favorites.SelectedIndex;
        if (index < 0 || index >= _saved.Count) { Ui.SetStatus(_status, "请先选择一个收藏", true); return; }
        var name = _saved[index].Name;
        _saved.RemoveAt(index);
        SaveFavorites();
        RefreshFavorites();
        Ui.SetStatus(_status, $"已删除“{name}”");
    }
}
