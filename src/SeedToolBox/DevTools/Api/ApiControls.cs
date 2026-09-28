using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools.Api;

static class ApiUi
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    public static Brush MethodBrush(string method) => method switch
    {
        "GET" => Frozen(0x0C, 0x8A, 0x4B),
        "POST" => Frozen(0xC7, 0x7C, 0x00),
        "PUT" => Frozen(0x1F, 0x6F, 0xD1),
        "PATCH" => Frozen(0x8E, 0x44, 0xAD),
        "DELETE" => Frozen(0xD1, 0x34, 0x38),
        _ => Frozen(0x6B, 0x6B, 0x6B),
    };

    public static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush StatusBrush(int status) => status switch
    {
        >= 200 and < 300 => Frozen(0x0C, 0x8A, 0x4B),
        >= 300 and < 400 => Frozen(0x1F, 0x6F, 0xD1),
        _ => Frozen(0xD1, 0x34, 0x38),
    };

    public static string MethodShort(string method) => method switch { "DELETE" => "DEL", "OPTIONS" => "OPT", "PATCH" => "PATCH", _ => method };

    /// <summary>Small borderless icon button.</summary>
    public static Button Icon(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
            ToolTip = tip,
            Padding = new Thickness(6, 4, 6, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static ComboBox Combo(double width, IEnumerable<(string Id, string Label)> items, string selected, Action<string> changed)
    {
        var combo = new ComboBox { Width = width, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, label) in items)
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            combo.Items.Add(item);
            if (id == selected) combo.SelectedItem = item;
        }
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string id }) changed(id); };
        return combo;
    }

    public static readonly string[] CommonHeaders =
    {
        "Accept", "Accept-Encoding", "Accept-Language", "Authorization", "Cache-Control", "Connection", "Content-Type", "Content-Length",
        "Cookie", "Host", "If-Match", "If-Modified-Since", "If-None-Match", "Origin", "Pragma", "Range", "Referer", "User-Agent",
        "X-Api-Key", "X-Request-Id", "X-Requested-With", "X-Forwarded-For",
    };
}

/// <summary>An editable key/value table with enable boxes and an always-empty last row, over a model list it edits in place.</summary>
sealed class KeyValueEditor : DockPanel
{
    readonly StackPanel _rows = new();
    readonly TextBox _bulk = Ui.Area();
    readonly ScrollViewer _scroll;
    readonly bool _descriptions, _files, _secrets;
    readonly string[]? _suggestions;
    readonly string _keyHint, _valueHint;
    List<KeyValue> _list = new();
    bool _bulkMode;
    public event Action? Changed;
    public bool ReadOnly { get; set; }

    public KeyValueEditor(bool descriptions = true, bool files = false, bool secrets = false, string[]? suggestions = null, string keyHint = "键", string valueHint = "值")
    {
        _descriptions = descriptions;
        _files = files;
        _secrets = secrets;
        _suggestions = suggestions;
        _keyHint = keyHint;
        _valueHint = valueHint;

        var bulkToggle = new TextBlock { Text = "批量编辑", Foreground = ApiUi.Res("AccentBrush"), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 4) };
        bulkToggle.MouseLeftButtonUp += (_, _) =>
        {
            SetBulk(!_bulkMode);
            bulkToggle.Text = _bulkMode ? "表格编辑" : "批量编辑";
        };
        SetDock(bulkToggle, Dock.Top);
        Children.Add(bulkToggle);

        var header = Row(null);
        SetDock(header, Dock.Top);
        Children.Add(header);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows };
        _bulk.ToolTip = "每行 键: 值；行首加 // 表示停用";
        _bulk.Visibility = Visibility.Collapsed;
        _bulk.TextChanged += (_, _) => { if (_bulkMode) { ParseBulk(); Changed?.Invoke(); } };
        // Table and bulk text share the rest of the space; only one is visible.
        Children.Add(new Grid { Children = { _scroll, _bulk } });
    }

    public List<KeyValue> Items => _list;

    public void Load(List<KeyValue> list)
    {
        _list = list;
        if (_bulkMode) { _bulk.Text = BulkText(); return; }
        _rows.Children.Clear();
        foreach (var kv in list) _rows.Children.Add(Row(kv));
        if (!ReadOnly) _rows.Children.Add(Row(new KeyValue(), trailing: true));
    }

    void SetBulk(bool on)
    {
        if (on) _bulk.Text = BulkText();
        _bulkMode = on;
        _scroll.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _bulk.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) Load(_list);
    }

    /// <summary>Stands for a secret value in bulk text; left as is, the value stays.</summary>
    const string SecretMask = "******";

    string BulkText() => string.Join("\r\n", _list.Where(k => !k.IsEmpty).Select(k => (k.Enabled ? "" : "//") + k.Key + ": " + (_secrets && k.Secret ? SecretMask : k.Value)));

    void ParseBulk()
    {
        var parsed = new List<KeyValue>();
        foreach (var raw in _bulk.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            bool off = line.StartsWith("//");
            if (off) line = line.Substring(2).Trim();
            int colon = line.IndexOf(':');
            var key = colon < 0 ? line : line.Substring(0, colon).Trim();
            var old = _list.FirstOrDefault(k => k.Key == key);
            var value = colon < 0 ? "" : line.Substring(colon + 1).Trim();
            if (_secrets && old is { Secret: true } && value == SecretMask) value = old.Value;
            parsed.Add(new KeyValue
            {
                Enabled = !off, Key = key, Value = value,
                Description = old?.Description ?? "", Type = old?.Type ?? "text", Secret = old?.Secret ?? false,
            });
        }
        _list.Clear();
        _list.AddRange(parsed);
    }

    Grid Row(KeyValue? kv, bool trailing = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        void Col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Col(new GridLength(28));
        Col(new GridLength(1, GridUnitType.Star));
        if (_files) Col(new GridLength(64));
        Col(new GridLength(1.4, GridUnitType.Star));
        if (_descriptions) Col(new GridLength(1, GridUnitType.Star));
        if (_secrets) Col(new GridLength(46));
        Col(new GridLength(30));
        int c = 0;
        void Put(UIElement e) { Grid.SetColumn(e, c++); grid.Children.Add(e); }

        if (kv == null)
        {
            TextBlock H(string t) => new() { Text = t, Foreground = DialogWindow.HintBrush, Margin = new Thickness(6, 0, 0, 4), FontSize = 12 };
            Put(new TextBlock());
            Put(H(_keyHint));
            if (_files) Put(H("类型"));
            Put(H(_valueHint));
            if (_descriptions) Put(H("说明"));
            if (_secrets) Put(H("机密"));
            return grid;
        }

        var enabled = new CheckBox { IsChecked = kv.Enabled, VerticalAlignment = VerticalAlignment.Center, Visibility = trailing ? Visibility.Hidden : Visibility.Visible, IsEnabled = !ReadOnly };
        Put(enabled);

        void Promote()
        {
            if (!trailing) return;
            trailing = false;
            enabled.Visibility = Visibility.Visible;
            _list.Add(kv);
            _rows.Children.Add(Row(new KeyValue(), trailing: true));
        }
        void Edited() { Promote(); Changed?.Invoke(); }

        enabled.Click += (_, _) => { kv.Enabled = enabled.IsChecked == true; Edited(); };

        FrameworkElement key;
        if (_suggestions != null && !ReadOnly)
        {
            var combo = new ComboBox { IsEditable = true, Text = kv.Key, Margin = new Thickness(0, 0, 4, 0), ItemsSource = _suggestions, IsTextSearchEnabled = true, StaysOpenOnEdit = true };
            combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (combo.Text == kv.Key) return;
                kv.Key = combo.Text;
                Edited();
            }));
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s && s != kv.Key) { kv.Key = s; Edited(); } };
            key = combo;
        }
        else key = Box(kv.Key, v => { kv.Key = v; Edited(); });
        Put(key);

        if (_files)
        {
            var type = new ComboBox { Margin = new Thickness(0, 0, 4, 0), IsEnabled = !ReadOnly };
            type.Items.Add("文本");
            type.Items.Add("文件");
            type.SelectedIndex = kv.Type == "file" ? 1 : 0;
            Put(type);
            type.SelectionChanged += (_, _) =>
            {
                kv.Type = type.SelectedIndex == 1 ? "file" : "text";
                Edited();
                ReplaceRow(grid, kv);
            };
        }

        UIElement value;
        if (_files && kv.Type == "file")
        {
            var box = Box(kv.Value, v => { kv.Value = v; Edited(); });
            var pick = new Button { Content = "…", Width = 28, Margin = new Thickness(0, 0, 4, 0), ToolTip = "选择文件" };
            pick.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog();
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) box.Text = dialog.FileName;
            };
            var dock = new DockPanel();
            DockPanel.SetDock(pick, Dock.Right);
            dock.Children.Add(pick);
            dock.Children.Add(box);
            value = dock;
        }
        else if (_secrets && kv.Secret)
        {
            var pass = new PasswordBox { Password = kv.Value, Style = DialogWindow.PasswordBoxStyle, Margin = new Thickness(0, 0, 4, 0), ToolTip = "机密变量，界面上不显示" };
            pass.PasswordChanged += (_, _) => { kv.Value = pass.Password; Edited(); };
            value = pass;
        }
        else value = Box(kv.Value, v => { kv.Value = v; Edited(); });
        Put(value);

        if (_descriptions) Put(Box(kv.Description, v => { kv.Description = v; Edited(); }));

        if (_secrets)
        {
            var secret = new CheckBox { IsChecked = kv.Secret, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "机密：值打上掩码" };
            secret.Click += (_, _) => { kv.Secret = secret.IsChecked == true; Edited(); ReplaceRow(grid, kv); };
            Put(secret);
        }

        if (!ReadOnly)
        {
            var remove = ApiUi.Icon("\uE74D", "删除这一行", () =>
            {
                if (!_list.Contains(kv)) return;
                _list.Remove(kv);
                _rows.Children.Remove(grid);
                Changed?.Invoke();
            });
            Put(remove);
        }
        return grid;
    }

    void ReplaceRow(Grid old, KeyValue kv)
    {
        int index = _rows.Children.IndexOf(old);
        if (index < 0) return;
        _rows.Children.RemoveAt(index);
        _rows.Children.Insert(index, Row(kv, trailing: !_list.Contains(kv)));
    }

    TextBox Box(string text, Action<string> changed)
    {
        var box = new TextBox { Text = text, Style = DialogWindow.TextBoxStyle, Margin = new Thickness(0, 0, 4, 0), IsReadOnly = ReadOnly, FontFamily = Ui.Mono };
        box.TextChanged += (_, _) => { if (!ReadOnly) changed(box.Text); };
        return box;
    }
}

/// <summary>Auth type and its fields, editing an <see cref="ApiAuth"/> in place.</summary>
sealed class AuthEditor : StackPanel
{
    readonly bool _inherit;
    readonly StackPanel _fields = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _note = new() { Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    ComboBox _type = new();
    ApiAuth _auth = new();
    public event Action? Changed;
    /// <summary>Describes the auth an inheriting request ends up with.</summary>
    public Func<string>? InheritedFrom { get; set; }

    public AuthEditor(bool inherit)
    {
        _inherit = inherit;
    }

    public void Load(ApiAuth auth)
    {
        _auth = auth;
        Children.Clear();
        var types = AuthTypes.All.Where(t => _inherit || t.Id != AuthTypes.Inherit);
        if (!_inherit && auth.Type == AuthTypes.Inherit) auth.Type = AuthTypes.None;
        _type = ApiUi.Combo(180, types, auth.Type, id => { _auth.Type = id; Fields(); Changed?.Invoke(); });
        Children.Add(Ui.Row(Ui.Label("类型"), _type));
        Children.Add(_fields);
        Children.Add(_note);
        Fields();
    }

    void Fields()
    {
        _fields.Children.Clear();
        _note.Text = "";
        var a = _auth;
        switch (a.Type)
        {
            case AuthTypes.Inherit:
                _note.Text = "用所在文件夹或集合的认证。" + (InheritedFrom?.Invoke() ?? "");
                break;
            case AuthTypes.None:
                _note.Text = "这个请求不带认证。";
                break;
            case AuthTypes.Bearer:
                Field("Token", a.Token, v => a.Token = v);
                _note.Text = "发送时加上 Authorization: Bearer <Token>，可以写 {{变量}}。";
                break;
            case AuthTypes.Basic:
                Field("用户名", a.Username, v => a.Username = v);
                Field("密码", a.Password, v => a.Password = v, password: true);
                _note.Text = "发送时加上 Authorization: Basic base64(用户名:密码)。";
                break;
            case AuthTypes.ApiKey:
                Field("Key", a.Key, v => a.Key = v);
                Field("Value", a.Value, v => a.Value = v);
                var where = ApiUi.Combo(180, new[] { ("header", "请求头"), ("query", "查询参数") }, a.In, v => { a.In = v; Changed?.Invoke(); });
                _fields.Children.Add(Line("加到", where));
                break;
        }
    }

    void Field(string label, string value, Action<string> set, bool password = false)
    {
        FrameworkElement box;
        if (password)
        {
            var p = new PasswordBox { Password = value, Style = DialogWindow.PasswordBoxStyle };
            p.PasswordChanged += (_, _) => { set(p.Password); Changed?.Invoke(); };
            box = p;
        }
        else
        {
            var t = Ui.Field();
            t.Text = value;
            t.FontFamily = Ui.Mono;
            t.TextChanged += (_, _) => { set(t.Text); Changed?.Invoke(); };
            box = t;
        }
        _fields.Children.Add(Line(label, box));
    }

    static DockPanel Line(string label, FrameworkElement input)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Width = 560 };
        var l = Ui.Label(label);
        l.Width = 70;
        DockPanel.SetDock(l, Dock.Left);
        dock.Children.Add(l);
        if (input is ComboBox) input.HorizontalAlignment = HorizontalAlignment.Left;
        dock.Children.Add(input);
        return dock;
    }
}

/// <summary>A script box with a list of snippets that insert at the caret.</summary>
sealed class ScriptEditor : DockPanel
{
    public readonly TextBox Box = Ui.Area();

    public static readonly (string Name, string Code)[] PreSnippets =
    {
        ("设置环境变量", "pm.environment.set(\"name\", \"value\");"),
        ("设置集合变量", "pm.collectionVariables.set(\"name\", \"value\");"),
        ("设置全局变量", "pm.globals.set(\"name\", \"value\");"),
        ("设置局部变量", "pm.variables.set(\"name\", \"value\");"),
        ("读取变量", "const value = pm.variables.get(\"name\");"),
        ("加请求头", "pm.request.headers.upsert({ key: \"X-Request-Id\", value: pm.variables.replaceIn(\"{{$guid}}\") });"),
        ("时间戳变量", "pm.variables.set(\"ts\", Date.now().toString());"),
        ("输出日志", "console.log(pm.request.url.toString());"),
    };

    public static readonly (string Name, string Code)[] TestSnippets =
    {
        ("状态码是 200", "pm.test(\"状态码是 200\", function () {\r\n    pm.response.to.have.status(200);\r\n});"),
        ("响应时间小于 500ms", "pm.test(\"响应时间小于 500ms\", function () {\r\n    pm.expect(pm.response.responseTime).to.be.below(500);\r\n});"),
        ("响应体包含文字", "pm.test(\"响应体包含文字\", function () {\r\n    pm.expect(pm.response.text()).to.include(\"文字\");\r\n});"),
        ("JSON 字段等于", "pm.test(\"JSON 字段检查\", function () {\r\n    const json = pm.response.json();\r\n    pm.expect(json.id).to.eql(1);\r\n});"),
        ("有响应头", "pm.test(\"有 Content-Type 响应头\", function () {\r\n    pm.response.to.have.header(\"Content-Type\");\r\n});"),
        ("存下 token", "const json = pm.response.json();\r\npm.environment.set(\"token\", json.token);"),
        ("状态码是 2xx", "pm.test(\"请求成功\", function () {\r\n    pm.response.to.be.success;\r\n});"),
        ("输出响应", "console.log(pm.response.json());"),
    };

    public ScriptEditor(string hint, (string Name, string Code)[] snippets)
    {
        var side = new StackPanel { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
        side.Children.Add(new TextBlock { Text = "片段（点击插入）", Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (name, code) in snippets)
        {
            var link = new TextBlock { Text = name, Foreground = ApiUi.Res("AccentBrush"), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            link.MouseLeftButtonUp += (_, _) => Insert(code);
            side.Children.Add(link);
        }
        SetDock(side, Dock.Right);
        Children.Add(side);
        var tip = new TextBlock { Text = hint, Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        SetDock(tip, Dock.Top);
        Children.Add(tip);
        Box.AcceptsTab = true;
        Children.Add(Box);
    }

    void Insert(string code)
    {
        int at = Box.CaretIndex;
        var prefix = at > 0 && Box.Text[at - 1] != '\n' ? "\r\n" : "";
        var text = prefix + code + "\r\n";
        Box.Text = Box.Text.Insert(at, text);
        Box.CaretIndex = at + text.Length;
        Box.Focus();
    }
}

/// <summary>Collapsible, coloured JSON. Children are built when a node opens, so big responses stay quick.</summary>
sealed class JsonTree : TreeView
{
    static readonly Brush KeyBrush = ApiUi.Frozen(0xA3, 0x15, 0x15);
    static readonly Brush StringBrush = ApiUi.Frozen(0x0B, 0x7A, 0x3E);
    static readonly Brush NumberBrush = ApiUi.Frozen(0x1F, 0x5F, 0xC8);
    static readonly Brush LiteralBrush = ApiUi.Frozen(0x8E, 0x44, 0xAD);
    const int Page = 500;
    /// <summary>At most this many nodes are opened by "expand all", so huge responses don't build a huge tree.</summary>
    const int ExpandBudget = 5000;

    public JsonTree()
    {
        FontFamily = Ui.Mono;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        var copy = new MenuItem { Header = "复制值" };
        copy.Click += (_, _) => { if (SelectedItem is TreeViewItem { Tag: JToken t }) Clipboard.SetText(t is JValue v ? Convert.ToString(v.Value) ?? "null" : t.ToString()); };
        var path = new MenuItem { Header = "复制路径" };
        path.Click += (_, _) => { if (SelectedItem is TreeViewItem { Tag: JToken t }) Clipboard.SetText(t.Path.Length > 0 ? t.Path : "$"); };
        ContextMenu = new ContextMenu { Items = { copy, path } };
    }

    public void Show(JToken root)
    {
        Items.Clear();
        var node = Node(null, root);
        node.IsExpanded = true;
        Items.Add(node);
        foreach (var child in node.Items.OfType<TreeViewItem>().Take(50)) child.IsExpanded = child.Tag is JContainer c && c.Count <= 20;
    }

    public void ExpandAll(bool expand)
    {
        int budget = ExpandBudget;
        void Walk(ItemsControl parent, int depth)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>().ToList())
            {
                if (item.Tag is not JContainer c) continue;
                if (expand && !item.IsExpanded)
                {
                    if (budget <= 0) return;
                    budget -= Math.Min(c.Count, Page);
                }
                item.IsExpanded = expand || depth == 0;
                if (expand && depth < 6) Walk(item, depth + 1);
            }
        }
        Walk(this, 0);
    }

    static TreeViewItem Node(string? name, JToken token)
    {
        var header = new TextBlock();
        if (name != null) { header.Inlines.Add(new Run(name) { Foreground = KeyBrush }); header.Inlines.Add(new Run(": ")); }
        var item = new TreeViewItem { Header = header, Tag = token };
        switch (token)
        {
            case JObject o:
                header.Inlines.Add(new Run(o.Count == 0 ? "{}" : "{ " + o.Count + " }") { Foreground = DialogWindow.HintBrush });
                if (o.Count > 0) Lazy(item, () => o.Properties().Select(p => ("\"" + p.Name + "\"", p.Value)).ToList());
                break;
            case JArray a:
                header.Inlines.Add(new Run(a.Count == 0 ? "[]" : "[ " + a.Count + " ]") { Foreground = DialogWindow.HintBrush });
                if (a.Count > 0) Lazy(item, () => a.Select((v, i) => (i.ToString(), v)).ToList());
                break;
            case JValue v:
                header.Inlines.Add(Value(v));
                break;
        }
        return item;
    }

    static Run Value(JValue v) => v.Type switch
    {
        JTokenType.String or JTokenType.Date or JTokenType.Guid or JTokenType.Uri or JTokenType.TimeSpan =>
            new Run(Newtonsoft.Json.JsonConvert.ToString(Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture))) { Foreground = StringBrush },
        JTokenType.Integer or JTokenType.Float => new Run(v.ToString(Newtonsoft.Json.Formatting.None)) { Foreground = NumberBrush },
        _ => new Run(v.ToString(Newtonsoft.Json.Formatting.None)) { Foreground = LiteralBrush },
    };

    static void Lazy(TreeViewItem item, Func<List<(string Name, JToken Token)>> children)
    {
        item.Items.Add(new TreeViewItem { Header = "…" });
        bool built = false;
        item.Expanded += (_, e) =>
        {
            if (built || e.OriginalSource != item) return;
            built = true;
            item.Items.Clear();
            AddPage(item, children(), 0);
        };
    }

    /// <summary>Builds nodes for one page only; the rest wait behind a "show more" row.</summary>
    static void AddPage(TreeViewItem item, List<(string Name, JToken Token)> all, int from)
    {
        for (int i = from; i < all.Count && i < from + Page; i++) item.Items.Add(Node(all[i].Name, all[i].Token));
        int rest = all.Count - from - Page;
        if (rest <= 0) return;
        var more = new TreeViewItem { Header = new TextBlock { Text = $"… 还有 {rest} 项，点击显示", Foreground = ApiUi.Res("AccentBrush"), Cursor = Cursors.Hand } };
        more.Selected += (_, _) =>
        {
            item.Items.Remove(more);
            AddPage(item, all, from + Page);
        };
        item.Items.Add(more);
    }
}
