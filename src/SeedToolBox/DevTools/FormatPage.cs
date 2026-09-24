using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUglify;
using NUglify.Css;
using NUglify.JavaScript;
using SeedToolBox.ScreenTools;
using YamlDotNet.RepresentationModel;
using Formatting = Newtonsoft.Json.Formatting;

namespace SeedToolBox.DevTools;

/// <summary>Beautifies or minifies JSON, JavaScript, CSS, XML, HTML, SQL and YAML.</summary>
sealed class FormatPage : DockPanel
{
    readonly TextBox _input = Ui.Area();
    readonly TextBox _output = Ui.Area();
    readonly ComboBox _language = new() { Width = 120, ItemsSource = new[] { "JSON", "JavaScript", "CSS", "XML", "HTML", "SQL", "YAML" }, SelectedIndex = 0 };
    readonly ComboBox _indent = new() { Width = 100, ItemsSource = new[] { "2 空格", "4 空格", "Tab" }, SelectedIndex = 1 };
    readonly TextBlock _status = Ui.Status();
    readonly AsyncToken _busy = new();
    readonly CheckBox _sortKeys = new() { Content = "排序键", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _treeView = new() { Content = "树视图", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBox _path = Ui.Field(220);
    readonly TreeView _tree = new() { FontFamily = Ui.Mono, FontSize = 13, Visibility = Visibility.Collapsed };
    readonly StackPanel _jsonRow;

    public FormatPage()
    {
        var header = Ui.Header("代码格式化", "JSON / JavaScript / CSS / XML / HTML / SQL / YAML 的格式化、压缩与校验");
        var toolbar = Ui.Row(
            Ui.Label("语言"), _language, Ui.Label("", 16),
            Ui.Label("缩进"), _indent, Ui.Label("", 16),
            Ui.Button("格式化", Beautify, accent: true),
            Ui.Button("压缩", Minify),
            Ui.Button("校验", Validate),
            Ui.Button("清空", () => { _input.Clear(); _output.Clear(); _tree.Items.Clear(); _status.Text = ""; }));

        _path.ToolTip = "JSONPath，例如 $.store.book[*].author 或 $..price";
        _path.Margin = new Thickness(0, 0, 8, 0);
        _jsonRow = Ui.Row(
            _sortKeys, _treeView,
            Ui.Label("JSONPath"), _path,
            Ui.Button("查询", Query), Ui.Label("", 8),
            Ui.Button("转义为字符串", Escape),
            Ui.Button("反转义字符串", Unescape));
        _treeView.Click += (_, _) => ShowTree();
        _language.SelectionChanged += (_, _) =>
        {
            _jsonRow.Visibility = Language == "JSON" ? Visibility.Visible : Visibility.Collapsed;
            ShowTree();
        };

        Ui.FileDrop(_input, files => Load(files[0]));

        var copy = Ui.CopyButton(() => _output.Text);
        copy.Margin = new Thickness(0);
        var swap = Ui.Button("结果放回输入", () => _input.Text = _output.Text);
        var save = Ui.SaveButton(() => _output.Text, _status, "result" + Extension);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { swap, save, copy } };

        var result = new Grid { Children = { _output, _tree } };
        var body = Ui.Columns(Ui.Titled("输入（可拖入文件）", _input, Ui.OpenButton(_input, _status, PickLanguage)), Ui.Titled("结果", result, buttons));
        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_jsonRow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_jsonRow);
        Children.Add(_status);
        Children.Add(body);
    }

    string Language => (string)_language.SelectedItem;

    string Extension => Language switch
    {
        "JavaScript" => ".js", "CSS" => ".css", "XML" => ".xml", "HTML" => ".html", "SQL" => ".sql", "YAML" => ".yaml", _ => ".json",
    };

    void Load(string path)
    {
        if (Ui.LoadText(_input, path, _status)) PickLanguage(path);
    }

    void PickLanguage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        _language.SelectedItem = ext switch
        {
            ".js" or ".mjs" or ".cjs" or ".ts" => "JavaScript",
            ".css" => "CSS",
            ".xml" or ".xaml" or ".csproj" or ".config" or ".svg" or ".xsd" => "XML",
            ".html" or ".htm" => "HTML",
            ".sql" => "SQL",
            ".yml" or ".yaml" => "YAML",
            _ => "JSON",
        };
    }

    void Beautify() => Run(true);
    void Minify() => Run(false);

    void Run(bool beautify)
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        var language = Language;
        int indent = _indent.SelectedIndex;
        bool sort = _sortKeys.IsChecked == true;
        Ui.SetStatus(_status, "处理中…");
        Ui.RunAsync(_busy, () => language switch
        {
            "JSON" => FormatJson(text, beautify, indent, sort),
            "JavaScript" => FormatJs(text, beautify, indent),
            "XML" => FormatXml(text, beautify, indent),
            "HTML" => FormatHtml(text, beautify, indent),
            "SQL" => FormatSql(text, beautify, indent),
            "YAML" => FormatYaml(text, beautify),
            _ => FormatCss(text, beautify, indent),
        }, result =>
        {
            _output.Text = result;
            ShowTree();
            Ui.SetStatus(_status, $"完成：{text.Length:N0} → {result.Length:N0} 字符");
        }, ex => Ui.SetStatus(_status, ex.Message, true));
    }

    void Validate()
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        try
        {
            switch (Language)
            {
                case "JSON": ParseJson(text); break;
                case "JavaScript": Check(Uglify.Js(text)); break;
                case "XML": ParseXml(text); break;
                case "HTML": Check(Uglify.Html(text)); break;
                case "YAML": ParseYaml(text); break;
                case "SQL": Ui.SetStatus(_status, "SQL 只做排版，不做语法校验"); return;
                default: Check(Uglify.Css(text)); break;
            }
            Ui.SetStatus(_status, $"{Language} 语法正确");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, ex.Message, true);
        }
    }

    // JSON extras

    void Query()
    {
        var text = _input.Text;
        var path = _path.Text.Trim();
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入 JSON", true); return; }
        if (path.Length == 0) { Ui.SetStatus(_status, "请输入 JSONPath，例如 $..name", true); return; }
        int indent = _indent.SelectedIndex;
        Ui.SetStatus(_status, "查询中…");
        Ui.RunAsync(_busy, () =>
        {
            var matches = ParseJson(text).SelectTokens(path).ToList();
            var result = matches.Count == 1 ? matches[0] : new JArray(matches.Select(m => m.DeepClone()));
            return (Count: matches.Count, Text: FormatJson(result.ToString(Formatting.None), true, indent));
        }, r =>
        {
            _output.Text = r.Text;
            ShowTree();
            Ui.SetStatus(_status, $"匹配 {r.Count} 项");
        }, ex => Ui.SetStatus(_status, "查询失败：" + ex.Message, true));
    }

    void Escape()
    {
        if (_input.Text.Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        _output.Text = JsonConvert.ToString(_input.Text);
        ShowTree();
        Ui.SetStatus(_status, "已转义为 JSON 字符串");
    }

    void Unescape()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        try
        {
            if (!text.StartsWith("\"")) text = "\"" + text + "\"";
            _output.Text = JToken.Parse(text).Value<string>() ?? "";
            ShowTree();
            Ui.SetStatus(_status, "已反转义");
        }
        catch (Exception ex) { Ui.SetStatus(_status, "反转义失败：" + ex.Message, true); }
    }

    void ShowTree()
    {
        bool show = _treeView.IsChecked == true && Language == "JSON";
        _tree.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _output.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;
        _tree.Items.Clear();
        var source = _output.Text.Trim().Length > 0 ? _output.Text : _input.Text;
        if (source.Trim().Length == 0) return;
        try
        {
            var root = TreeNode("$", ParseJson(source));
            root.IsExpanded = true;
            _tree.Items.Add(root);
        }
        catch (Exception ex) { _tree.Items.Add(new TreeViewItem { Header = ex.Message }); }
    }

    static TreeViewItem TreeNode(string name, JToken token)
    {
        var item = new TreeViewItem();
        switch (token)
        {
            case JObject o:
                item.Header = $"{name} {{{o.Count}}}";
                AddLazy(item, () => o.Properties().Select(p => TreeNode(p.Name, p.Value)));
                break;
            case JArray a:
                item.Header = $"{name} [{a.Count}]";
                AddLazy(item, () => a.Select((t, i) => TreeNode($"[{i}]", t)));
                break;
            default:
                item.Header = $"{name}: {token.ToString(Formatting.None)}";
                break;
        }
        return item;
    }

    /// <summary>Builds children on first expand, so huge documents stay responsive.</summary>
    static void AddLazy(TreeViewItem item, Func<IEnumerable<TreeViewItem>> children)
    {
        item.Items.Add("…");
        bool filled = false;
        item.Expanded += (_, e) =>
        {
            if (filled || e.OriginalSource != item) return;
            filled = true;
            item.Items.Clear();
            int n = 0;
            foreach (var child in children())
            {
                if (++n > 5000) { item.Items.Add(new TreeViewItem { Header = "…（只显示前 5000 项）" }); break; }
                item.Items.Add(child);
            }
        };
    }

    static JToken SortKeys(JToken token) => token switch
    {
        JObject o => new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, SortKeys(p.Value)))),
        JArray a => new JArray(a.Select(SortKeys)),
        _ => token,
    };

    static string IndentText(int indent) => indent switch { 0 => "  ", 2 => "\t", _ => "    " };

    static JToken ParseJson(string text)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            var token = JToken.ReadFrom(reader);
            if (reader.Read()) throw new JsonReaderException($"第 {reader.LineNumber} 行第 {reader.LinePosition} 列之后还有多余内容");
            return token;
        }
        catch (JsonReaderException ex)
        {
            throw new FormatException($"JSON 错误（第 {ex.LineNumber} 行，第 {ex.LinePosition} 列）：{ex.Message}");
        }
    }

    static string FormatJson(string text, bool beautify, int indent, bool sort = false)
    {
        var token = ParseJson(text);
        if (sort) token = SortKeys(token);
        if (!beautify) return token.ToString(Formatting.None);
        using var writer = new StringWriter();
        using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented };
        if (indent == 2) { json.IndentChar = '\t'; json.Indentation = 1; }
        else json.Indentation = indent == 0 ? 2 : 4;
        token.WriteTo(json);
        json.Flush();
        return writer.ToString();
    }

    static string FormatJs(string text, bool beautify, int indent)
    {
        var settings = new CodeSettings();
        if (beautify)
        {
            settings.MinifyCode = false;
            settings.OutputMode = OutputMode.MultipleLines;
            settings.IndentSize = IndentText(indent) == "\t" ? 4 : IndentText(indent).Length;
            settings.PreserveImportantComments = true;
            settings.BlocksStartOnSameLine = BlockStart.SameLine;
            settings.TermSemicolons = true;
        }
        var result = Uglify.Js(text, settings);
        Check(result);
        return beautify && IndentText(indent) == "\t" ? Retab(result.Code, 4) : result.Code;
    }

    static string FormatCss(string text, bool beautify, int indent)
    {
        var settings = new CssSettings();
        var code = new CodeSettings();
        if (beautify)
        {
            settings.OutputMode = OutputMode.MultipleLines;
            settings.IndentSize = IndentText(indent) == "\t" ? 4 : IndentText(indent).Length;
            settings.CommentMode = CssComment.All;
            settings.BlocksStartOnSameLine = BlockStart.SameLine;
            settings.TermSemicolons = true;
            code.MinifyCode = false;
        }
        var result = Uglify.Css(text, settings, code);
        Check(result);
        return beautify && IndentText(indent) == "\t" ? Retab(result.Code, 4) : result.Code;
    }

    // XML

    static XDocument ParseXml(string text)
    {
        try { return XDocument.Parse(text, LoadOptions.None); }
        catch (XmlException ex) { throw new FormatException($"XML 错误（第 {ex.LineNumber} 行，第 {ex.LinePosition} 列）：{ex.Message}"); }
    }

    static string FormatXml(string text, bool beautify, int indent)
    {
        var doc = ParseXml(text);
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = beautify,
            IndentChars = IndentText(indent),
            OmitXmlDeclaration = doc.Declaration == null,
            NewLineChars = "\n",
        };
        using (var writer = XmlWriter.Create(sb, settings)) doc.Save(writer);
        var result = sb.ToString();
        // StringBuilder output always claims utf-16; keep what the source declared
        if (doc.Declaration?.Encoding is { Length: > 0 } enc) result = result.Replace("encoding=\"utf-16\"", $"encoding=\"{enc}\"");
        return result;
    }

    // HTML

    static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "!doctype" };
    static readonly HashSet<string> RawTags = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "pre", "textarea" };
    static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
        { "a", "b", "i", "u", "em", "strong", "span", "code", "small", "sub", "sup", "label", "abbr", "kbd", "mark", "s" };

    static string FormatHtml(string text, bool beautify, int indent)
    {
        if (!beautify)
        {
            var min = Uglify.Html(text);
            Check(min);
            return min.Code;
        }
        var unit = IndentText(indent);
        var sb = new StringBuilder();
        int depth = 0;
        var tokens = Regex.Matches(text, @"<!--[\s\S]*?-->|<[^>]+>|[^<]+");
        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i].Value;
            if (!tok.StartsWith("<"))
            {
                var t = Regex.Replace(tok, @"\s+", " ").Trim();
                if (t.Length > 0) Line(sb, unit, depth, t);
                continue;
            }
            var m = Regex.Match(tok, @"^<\s*(/?)\s*([!\w:-]+)");
            var name = m.Success ? m.Groups[2].Value : "";
            bool closing = m.Success && m.Groups[1].Value == "/";
            if (tok.StartsWith("<!--") || !m.Success) { Line(sb, unit, depth, tok); continue; }
            if (closing) { depth = Math.Max(0, depth - 1); Line(sb, unit, depth, tok); continue; }
            bool selfClosing = tok.EndsWith("/>") || VoidTags.Contains(name) || name.StartsWith("!") || name.StartsWith("?");
            if (selfClosing) { Line(sb, unit, depth, tok); continue; }
            if (RawTags.Contains(name))
            {
                // Keep the body of script/style/pre verbatim up to the closing tag
                var end = text.IndexOf("</" + name, tokens[i].Index + tok.Length, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { Line(sb, unit, depth, text.Substring(tokens[i].Index).Trim()); break; }
                var close = text.IndexOf('>', end);
                close = close < 0 ? text.Length - 1 : close;
                var inner = text.Substring(tokens[i].Index + tok.Length, end - tokens[i].Index - tok.Length);
                Line(sb, unit, depth, tok + inner + text.Substring(end, close - end + 1));
                while (i + 1 < tokens.Count && tokens[i + 1].Index <= close) i++;
                continue;
            }
            // Keep short inline elements with plain text on one line: <a href="#">link</a>
            if (InlineTags.Contains(name) && i + 2 < tokens.Count && !tokens[i + 1].Value.StartsWith("<")
                && Regex.IsMatch(tokens[i + 2].Value, $@"^<\s*/\s*{Regex.Escape(name)}\s*>$", RegexOptions.IgnoreCase))
            {
                Line(sb, unit, depth, tok + Regex.Replace(tokens[i + 1].Value, @"\s+", " ").Trim() + tokens[i + 2].Value);
                i += 2;
                continue;
            }
            Line(sb, unit, depth, tok);
            depth++;
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    static void Line(StringBuilder sb, string unit, int depth, string text)
    {
        for (int i = 0; i < depth; i++) sb.Append(unit);
        sb.Append(text).Append('\n');
    }

    // SQL

    static readonly string[] SqlKeywords =
    {
        "select", "from", "where", "and", "or", "not", "in", "is", "null", "like", "between", "exists", "as", "on", "join", "inner", "left", "right", "full",
        "outer", "cross", "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct", "insert", "into", "values", "update", "set",
        "delete", "create", "table", "alter", "drop", "index", "view", "primary", "key", "foreign", "references", "default", "case", "when", "then",
        "else", "end", "asc", "desc", "top", "with", "returning", "count", "sum", "avg", "min", "max", "if", "begin", "commit", "rollback", "truncate",
        "except", "intersect", "over", "partition", "unique", "constraint", "check", "add", "column", "fetch", "next", "rows", "only", "declare", "using",
    };

    // Clauses that start a new line at the statement's level
    static readonly string[] SqlClauses =
    {
        "select", "from", "where", "group by", "order by", "having", "limit", "offset", "union", "union all", "except", "intersect", "insert into",
        "values", "update", "set", "delete from", "delete", "join", "inner join", "left join", "right join", "full join", "left outer join",
        "right outer join", "full outer join", "cross join", "returning", "with",
    };

    static string FormatSql(string text, bool beautify, int indent)
    {
        var keywords = new HashSet<string>(SqlKeywords, StringComparer.OrdinalIgnoreCase);
        var tokens = Regex.Matches(text, @"--[^\n]*|/\*[\s\S]*?\*/|'(?:[^']|'')*'|""(?:[^""]|"""")*""|`[^`]*`|\[[^\]]*\]|\d+(?:\.\d+)?|[\w@#$]+|<=|>=|<>|!=|\|\||::|\S")
            .Cast<Match>().Select(m => m.Value).ToList();
        if (!beautify)
        {
            var parts = tokens.Where(t => !t.StartsWith("--") && !t.StartsWith("/*")).Select(t => keywords.Contains(t) ? t.ToUpperInvariant() : t);
            var min = new StringBuilder();
            foreach (var t in parts)
            {
                if (min.Length > 0 && NeedsSpace(min[min.Length - 1], t)) min.Append(' ');
                min.Append(t);
            }
            return min.ToString();
        }

        var unit = IndentText(indent);
        var sb = new StringBuilder();
        int depth = 0;
        var levels = new Stack<int>();
        bool lineStart = true;
        void NewLine(int extra = 0)
        {
            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            if (sb.Length > 0) sb.Append('\n');
            for (int k = 0; k < depth + extra; k++) sb.Append(unit);
            lineStart = true;
        }
        void Emit(string t)
        {
            if (!lineStart && sb.Length > 0 && NeedsSpace(sb[sb.Length - 1], t)) sb.Append(' ');
            sb.Append(t);
            lineStart = false;
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var lower = t.ToLowerInvariant();
            // Longest clause that starts here, e.g. "left outer join"
            string? clause = null;
            int clauseLength = 0;
            foreach (var c in SqlClauses)
            {
                var words = c.Split(' ');
                if (words.Length <= clauseLength || i + words.Length > tokens.Count) continue;
                bool match = true;
                for (int w = 0; w < words.Length && match; w++) match = tokens[i + w].Equals(words[w], StringComparison.OrdinalIgnoreCase);
                if (match) { clause = c; clauseLength = words.Length; }
            }
            if (clause != null)
            {
                NewLine();
                Emit(clause.ToUpperInvariant());
                i += clauseLength - 1;
                continue;
            }
            if (t.StartsWith("--")) { Emit(t); NewLine(1); continue; }
            if (lower is "and" or "or") { NewLine(1); Emit(t.ToUpperInvariant()); continue; }
            if (t == "(")
            {
                bool sub = i + 1 < tokens.Count && tokens[i + 1].Equals("select", StringComparison.OrdinalIgnoreCase);
                Emit(t);
                levels.Push(sub ? 1 : 0);
                if (sub) depth++;
                continue;
            }
            if (t == ")")
            {
                if (levels.Count > 0 && levels.Pop() == 1) { depth = Math.Max(0, depth - 1); NewLine(); }
                sb.Append(')');
                lineStart = false;
                continue;
            }
            if (t == ",") { sb.Append(','); if (levels.Count == 0 || levels.Peek() == 1) NewLine(1); else sb.Append(' '); lineStart = levels.Count == 0 || levels.Peek() == 1; continue; }
            if (t == ";") { sb.Append(';'); sb.Append('\n'); depth = 0; levels.Clear(); lineStart = true; continue; }
            Emit(keywords.Contains(t) ? t.ToUpperInvariant() : t);
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim() + "\n";
    }

    static bool NeedsSpace(char last, string next) =>
        !(last is '(' or '.' or ' ' or '\n' or '\t') && !(next is ")" or "," or "." or ";") && !(next == "(" && (char.IsLetterOrDigit(last) || last == '_'));

    // YAML

    static YamlStream ParseYaml(string text)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlDotNet.Core.YamlException ex) { throw new FormatException($"YAML 错误（第 {ex.Start.Line} 行，第 {ex.Start.Column} 列）：{ex.Message}"); }
        return stream;
    }

    static string FormatYaml(string text, bool beautify)
    {
        var stream = ParseYaml(text);
        if (!beautify)
            foreach (var doc in stream.Documents)
                foreach (var node in doc.AllNodes)
                {
                    if (node is YamlMappingNode map) map.Style = YamlDotNet.Core.Events.MappingStyle.Flow;
                    else if (node is YamlSequenceNode seq) seq.Style = YamlDotNet.Core.Events.SequenceStyle.Flow;
                }
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        // YamlStream always ends each document with "..."; drop the trailing one
        var result = writer.ToString().Replace("\r\n", "\n").TrimEnd();
        if (result.EndsWith("\n...")) result = result.Substring(0, result.Length - 4).TrimEnd();
        else if (result == "...") result = "";
        return result + "\n";
    }

    static void Check(UglifyResult result)
    {
        var errors = result.Errors.Where(e => e.IsError).ToList();
        if (errors.Count > 0)
            throw new FormatException(string.Join("\n", errors.Take(3).Select(e => $"第 {e.StartLine} 行，第 {e.StartColumn} 列：{e.Message}")));
    }

    static string Retab(string code, int size) => string.Join("\n", code.Split('\n').Select(line =>
    {
        int spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ') spaces++;
        return new string('\t', spaces / size) + line.Substring(spaces - spaces % size);
    }));
}
