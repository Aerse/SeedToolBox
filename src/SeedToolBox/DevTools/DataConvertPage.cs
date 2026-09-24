using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.ScreenTools;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace SeedToolBox.DevTools;

/// <summary>Converts between JSON, XML, YAML, CSV, TOML and INI, and generates types and schemas from JSON.</summary>
sealed class DataConvertPage : DockPanel
{
    static readonly string[] Sources = { "JSON", "XML", "YAML", "CSV", "TOML", "INI" };
    static readonly string[] Targets = { "JSON", "XML", "YAML", "CSV", "TOML", "INI", "C# 类", "TypeScript", "Java 类", "Go 结构体", "JSON Schema" };

    readonly TextBox _input = Ui.Area();
    readonly TextBox _output = Ui.Area();
    readonly AsyncToken _busy = new();
    readonly ComboBox _from = new() { Width = 100, ItemsSource = Sources, SelectedIndex = 0 };
    readonly ComboBox _to = new() { Width = 120, ItemsSource = Targets, SelectedIndex = 2 };
    readonly ComboBox _delimiter = new() { Width = 90, ItemsSource = new[] { "自动", "逗号 ,", "Tab", "分号 ;" }, SelectedIndex = 0 };
    readonly CheckBox _infer = Check("推断数字和布尔", "读取 CSV / INI 时把数字、true/false 转成对应类型");
    readonly CheckBox _flatten = Check("展平嵌套（a.b）", "写 CSV 时把嵌套对象展开成 a.b 列；读 CSV 时把 a.b 列还原成嵌套对象");
    readonly CheckBox _record = Check("record", "生成 C# record 而不是 class");
    readonly CheckBox _nullable = Check("可空", "生成可空类型（string?、int?）");
    readonly TextBlock _status = Ui.Status();

    public DataConvertPage()
    {
        var header = Ui.Header("数据格式互转", "JSON / XML / YAML / CSV / TOML / INI 互相转换，JSON 生成 C# / TypeScript / Java / Go 类型和 JSON Schema");
        var swap = Ui.Button("⇄", Swap);
        swap.MinWidth = 40;
        swap.Margin = new Thickness(8, 0, 8, 0);
        var toolbar = Ui.Row(
            Ui.Label("从"), _from, swap, Ui.Label("到"), _to, Ui.Label("", 16),
            Ui.Button("转换", Convert, accent: true),
            Ui.Button("清空", () => { _input.Clear(); _output.Clear(); _status.Text = ""; }));
        var options = Ui.Row(
            Ui.Label("CSV 分隔符"), _delimiter, Ui.Label("", 16), _infer, _flatten,
            Ui.Label("C#", 8), _record, _nullable);

        Ui.FileDrop(_input, files =>
        {
            if (!Ui.LoadText(_input, files[0], _status)) return;
            var ext = Path.GetExtension(files[0]).ToLowerInvariant();
            int i = ext switch { ".xml" => 1, ".yml" or ".yaml" => 2, ".csv" or ".tsv" => 3, ".toml" => 4, ".ini" or ".properties" or ".cfg" or ".conf" => 5, _ => 0 };
            _from.SelectedIndex = i;
            if (_to.SelectedIndex == i) _to.SelectedIndex = i == 0 ? 2 : 0;
        });

        var format = Ui.Button("整理输入", () => { try { if (_from.SelectedIndex == 0) _input.Text = JToken.Parse(_input.Text).ToString(Newtonsoft.Json.Formatting.Indented); } catch (Exception ex) { Ui.SetStatus(_status, ex.Message, true); } });
        format.Margin = new Thickness(0);
        var copy = Ui.Button("复制结果", () => { if (_output.Text.Length > 0) ScreenToolService.CopyText(_output.Text); });
        copy.Margin = new Thickness(0);
        var body = Ui.Columns(Ui.Titled("输入（可拖入文件）", _input, new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.OpenButton(_input, _status), format } }), Ui.Titled("结果", _output, new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.SaveButton(() => _output.Text, _status), copy } }));
        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(options, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(options);
        Children.Add(_status);
        Children.Add(body);
    }

    static CheckBox Check(string text, string tip) =>
        new() { Content = text, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };

    void Swap()
    {
        if (_to.SelectedIndex >= Sources.Length) return;
        (_from.SelectedIndex, _to.SelectedIndex) = (_to.SelectedIndex, _from.SelectedIndex);
        if (_output.Text.Length > 0) (_input.Text, _output.Text) = (_output.Text, "");
    }

    void Convert()
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        int from = _from.SelectedIndex, to = _to.SelectedIndex;
        char? sep = _delimiter.SelectedIndex switch { 1 => ',', 2 => '\t', 3 => ';', _ => null };
        bool infer = _infer.IsChecked == true, flatten = _flatten.IsChecked == true;
        var cs = new CodeOptions(_record.IsChecked == true, _nullable.IsChecked == true);
        var label = $"{_from.SelectedItem} → {_to.SelectedItem} 完成";
        Ui.SetStatus(_status, "转换中…");
        Ui.RunAsync(_busy, () =>
        {
            var token = from switch
            {
                1 => FromXml(text),
                2 => FromYaml(text),
                3 => FromCsv(text, sep, infer, flatten),
                4 => Toml.Read(text),
                5 => FromIni(text, infer),
                _ => JToken.Parse(text),
            };
            return to switch
            {
                1 => ToXml(token),
                2 => ToYaml(token),
                3 => ToCsv(token, sep ?? ',', flatten),
                4 => Toml.Write(token),
                5 => ToIni(token),
                6 => TypeGenerator.CSharp(token, cs),
                7 => TypeGenerator.TypeScript(token),
                8 => TypeGenerator.Java(token),
                9 => TypeGenerator.Go(token),
                10 => JsonSchema.Generate(token).ToString(Newtonsoft.Json.Formatting.Indented),
                _ => token.ToString(Newtonsoft.Json.Formatting.Indented),
            };
        }, result =>
        {
            _output.Text = result;
            Ui.SetStatus(_status, label);
        }, ex => Ui.SetStatus(_status, "转换失败：" + ex.Message.Replace("\r", " ").Replace("\n", " "), true));
    }

    // XML

    static JToken FromXml(string text)
    {
        var doc = new XmlDocument();
        doc.LoadXml(text);
        var json = JsonConvert.SerializeXmlNode(doc, Newtonsoft.Json.Formatting.None, omitRootObject: false);
        var token = JToken.Parse(json);
        // Drop the <?xml ...?> declaration, which is not data
        if (token is JObject o) o.Remove("?xml");
        return token;
    }

    static string ToXml(JToken token)
    {
        // XML needs a single root element
        JObject root = token is JObject { Count: 1 } single && single.Properties().First().Value is JObject
            ? single
            : new JObject { ["root"] = token is JArray ? new JObject { ["item"] = token } : token };
        var doc = JsonConvert.DeserializeXmlNode(root.ToString(), null, writeArrayAttribute: false)!;
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, Encoding = Encoding.UTF8 }))
            doc.Save(writer);
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    // YAML

    static JToken FromYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count == 0) return JValue.CreateNull();
        return stream.Documents.Count == 1
            ? FromYamlNode(stream.Documents[0].RootNode)
            : new JArray(stream.Documents.Select(d => FromYamlNode(d.RootNode)));
    }

    static JToken FromYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JObject();
                foreach (var pair in map.Children)
                    obj[((YamlScalarNode)pair.Key).Value ?? ""] = FromYamlNode(pair.Value);
                return obj;
            case YamlSequenceNode seq:
                return new JArray(seq.Children.Select(FromYamlNode));
            case YamlScalarNode scalar:
                var value = scalar.Value ?? "";
                // Quoted scalars are always strings; plain ones follow the YAML core schema
                if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain) return new JValue(value);
                if (value is "" or "~" or "null" or "Null" or "NULL") return JValue.CreateNull();
                if (value is "true" or "True" or "TRUE") return new JValue(true);
                if (value is "false" or "False" or "FALSE") return new JValue(false);
                return Number(value) ?? new JValue(value);
            default:
                return JValue.CreateNull();
        }
    }

    static JValue? Number(string value)
    {
        if (Regex.IsMatch(value, @"^[-+]?\d+$") && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return new JValue(l);
        if (Regex.IsMatch(value, @"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new JValue(d);
        return null;
    }

    /// <summary>Turns "12", "3.5", "true" into numbers and bools; leading zeros stay text so IDs and phone numbers survive.</summary>
    static JToken Infer(string value)
    {
        var t = value.Trim();
        if (t.Length == 0) return JValue.CreateNull();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return new JValue(true);
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return new JValue(false);
        if (Regex.IsMatch(t, @"^[-+]?0\d")) return new JValue(value);
        return Number(t) ?? new JValue(value);
    }

    static string ToYaml(JToken token)
    {
        var serializer = new SerializerBuilder().WithQuotingNecessaryStrings().Build();
        return serializer.Serialize(ToPlain(token));
    }

    static object? ToPlain(JToken token) => token switch
    {
        JObject o => o.Properties().ToDictionary(p => p.Name, p => ToPlain(p.Value)),
        JArray a => a.Select(ToPlain).ToList(),
        JValue v => v.Value,
        _ => null,
    };

    // CSV

    static JToken FromCsv(string text, char? sep, bool infer, bool unflatten)
    {
        var rows = ParseCsv(text, sep);
        if (rows.Count == 0) return new JArray();
        var header = rows[0];
        var array = new JArray();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && row[0].Length == 0) continue;
            var obj = new JObject();
            for (int i = 0; i < header.Count; i++)
            {
                var cell = i < row.Count ? row[i] : "";
                JToken value = infer ? Infer(cell) : new JValue(cell);
                if (unflatten && header[i].Contains('.')) SetPath(obj, header[i].Split('.'), value);
                else obj[header[i]] = value;
            }
            array.Add(obj);
        }
        return array;
    }

    static void SetPath(JObject obj, string[] path, JToken value)
    {
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (obj[path[i]] is not JObject child) obj[path[i]] = child = new JObject();
            obj = child;
        }
        obj[path[path.Length - 1]] = value;
    }

    static List<List<string>> ParseCsv(string text, char? chosen)
    {
        // Guess from the first line: tabs (pasted from Excel), then semicolons (European Excel), then commas
        var firstLine = text.Split('\n')[0];
        char sep = chosen ?? (firstLine.Contains('\t') && !firstLine.Contains(',') ? '\t'
            : firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',');
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
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    static string ToCsv(JToken token, char sep, bool flatten)
    {
        var items = token is JArray a ? a.ToList() : new List<JToken> { token };
        if (items.Any(i => i is not JObject)) throw new FormatException("CSV 需要对象数组，例如 [{\"a\":1},{\"a\":2}]");
        var objects = items.Cast<JObject>().Select(o => flatten ? Flatten(o) : o).ToList();
        var columns = objects.SelectMany(o => o.Properties().Select(p => p.Name)).Distinct().ToList();
        var sb = new StringBuilder();
        var separator = sep.ToString();
        sb.AppendLine(string.Join(separator, columns.Select(c => Quote(c, sep))));
        foreach (var o in objects)
        {
            sb.AppendLine(string.Join(separator, columns.Select(c => o[c] switch
            {
                null or { Type: JTokenType.Null } => "",
                JValue { Type: JTokenType.Boolean } b => (bool)b ? "true" : "false",
                JValue v => Quote(System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "", sep),
                var nested => Quote(nested.ToString(Newtonsoft.Json.Formatting.None), sep),
            })));
        }
        return sb.ToString();
    }

    /// <summary>{"a":{"b":1}} becomes {"a.b":1}; arrays stay as they are.</summary>
    static JObject Flatten(JObject obj)
    {
        var result = new JObject();
        void Walk(string prefix, JObject o)
        {
            foreach (var p in o.Properties())
            {
                var key = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                if (p.Value is JObject { Count: > 0 } child) Walk(key, child);
                else result[key] = p.Value;
            }
        }
        Walk("", obj);
        return result;
    }

    static string Quote(string s, char sep = ',') =>
        s.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0 || s.Trim() != s ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // INI / Properties

    static JToken FromIni(string text, bool infer)
    {
        var root = new JObject();
        var section = root;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            // Java .properties continues a line that ends with a backslash
            while (line.EndsWith("\\") && n + 1 < lines.Length) line = line.Substring(0, line.Length - 1) + lines[++n].Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '!') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                var name = line.Substring(1, line.Length - 2).Trim();
                if (root[name] is not JObject existing) root[name] = existing = new JObject();
                section = existing;
                continue;
            }
            int eq = line.IndexOfAny(new[] { '=', ':' });
            var key = (eq < 0 ? line : line.Substring(0, eq)).Trim();
            var value = eq < 0 ? "" : line.Substring(eq + 1).Trim();
            if (value.Length >= 2 && value[0] == value[value.Length - 1] && value[0] is '"' or '\'') value = value.Substring(1, value.Length - 2);
            section[key] = infer ? Infer(value) : new JValue(value);
        }
        return root;
    }

    static string ToIni(JToken token)
    {
        if (token is not JObject root) throw new FormatException("INI 需要 JSON 对象");
        var sb = new StringBuilder();
        foreach (var p in root.Properties().Where(p => p.Value is not JObject))
            sb.Append(p.Name).Append(" = ").Append(IniValue(p.Value)).Append('\n');
        foreach (var p in root.Properties().Where(p => p.Value is JObject))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append('[').Append(p.Name).Append("]\n");
            foreach (var kv in Flatten((JObject)p.Value).Properties())
                sb.Append(kv.Name).Append(" = ").Append(IniValue(kv.Value)).Append('\n');
        }
        return sb.ToString();
    }

    static string IniValue(JToken value) => value switch
    {
        { Type: JTokenType.Null } => "",
        JValue { Type: JTokenType.Boolean } b => (bool)b ? "true" : "false",
        JValue v => System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString(Newtonsoft.Json.Formatting.None),
    };
}

/// <summary>Reads and writes the commonly used part of TOML: tables, arrays of tables, dotted keys, inline arrays and tables.</summary>
static class Toml
{
    public static JToken Read(string text)
    {
        var root = new JObject();
        var current = root;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            try
            {
                if (line.StartsWith("[["))
                {
                    var path = Keys(line.Substring(2, line.LastIndexOf("]]") - 2));
                    var parent = Table(root, path.Take(path.Count - 1));
                    if (parent[path[path.Count - 1]] is not JArray array) parent[path[path.Count - 1]] = array = new JArray();
                    current = new JObject();
                    array.Add(current);
                    continue;
                }
                if (line.StartsWith("["))
                {
                    current = Table(root, Keys(line.Substring(1, line.LastIndexOf(']') - 1)));
                    continue;
                }
                int eq = KeyEnd(line);
                if (eq < 0) throw new FormatException("缺少 =");
                var keys = Keys(line.Substring(0, eq));
                var rest = line.Substring(eq + 1).Trim();
                // Values may span lines: multi-line strings and arrays
                while (NeedsMore(rest) && n + 1 < lines.Length) rest += "\n" + (rest.Contains("\"\"\"") || rest.Contains("'''") ? lines[++n] : StripComment(lines[++n]));
                int pos = 0;
                var value = ParseValue(rest, ref pos);
                var target = Table(current, keys.Take(keys.Count - 1));
                target[keys[keys.Count - 1]] = value;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                throw new FormatException($"TOML 第 {n + 1} 行：{ex.Message}");
            }
        }
        return root;
    }

    static bool NeedsMore(string value)
    {
        if (Regex.Matches(value, "\"\"\"").Count % 2 == 1 || Regex.Matches(value, "'''").Count % 2 == 1) return true;
        int depth = 0;
        bool inString = false;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inString) { if (c == '\\' && quote == '"') i++; else if (c == quote) inString = false; }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
        }
        return depth > 0;
    }

    static string StripComment(string line)
    {
        bool inString = false;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString) { if (c == '\\' && quote == '"') i++; else if (c == quote) inString = false; }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '#') return line.Substring(0, i);
        }
        return line;
    }

    static int KeyEnd(string line)
    {
        bool inString = false;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString) { if (c == quote) inString = false; }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '=') return i;
        }
        return -1;
    }

    static List<string> Keys(string text) =>
        Regex.Matches(text, @"""((?:[^""\\]|\\.)*)""|'([^']*)'|([^.\s]+)").Cast<Match>()
            .Select(m => m.Groups[1].Success ? Unescape(m.Groups[1].Value) : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            .ToList();

    static JObject Table(JObject root, IEnumerable<string> path)
    {
        foreach (var key in path)
        {
            var next = root[key];
            if (next is JArray { Count: > 0 } array && array.Last is JObject last) root = last;
            else if (next is JObject obj) root = obj;
            else { var created = new JObject(); root[key] = created; root = created; }
        }
        return root;
    }

    static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            if (char.IsWhiteSpace(s[pos]) || s[pos] == ',') pos++;
            else if (s[pos] == '#') { while (pos < s.Length && s[pos] != '\n') pos++; }
            else break;
        }
    }

    static JToken ParseValue(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        if (pos >= s.Length) throw new FormatException("缺少值");
        if (string.CompareOrdinal(s, pos, "\"\"\"", 0, 3) == 0)
        {
            int end = s.IndexOf("\"\"\"", pos + 3, StringComparison.Ordinal);
            var body = s.Substring(pos + 3, end - pos - 3);
            pos = end + 3;
            if (body.StartsWith("\n")) body = body.Substring(1);
            return new JValue(Unescape(Regex.Replace(body, @"\\\s*\n\s*", "")));
        }
        if (string.CompareOrdinal(s, pos, "'''", 0, 3) == 0)
        {
            int end = s.IndexOf("'''", pos + 3, StringComparison.Ordinal);
            var body = s.Substring(pos + 3, end - pos - 3);
            pos = end + 3;
            return new JValue(body.StartsWith("\n") ? body.Substring(1) : body);
        }
        char c = s[pos];
        if (c == '"')
        {
            var sb = new StringBuilder();
            for (pos++; s[pos] != '"'; pos++)
            {
                if (s[pos] == '\\') { sb.Append(s[pos]); pos++; }
                sb.Append(s[pos]);
            }
            pos++;
            return new JValue(Unescape(sb.ToString()));
        }
        if (c == '\'')
        {
            int end = s.IndexOf('\'', pos + 1);
            var body = s.Substring(pos + 1, end - pos - 1);
            pos = end + 1;
            return new JValue(body);
        }
        if (c == '[')
        {
            var array = new JArray();
            pos++;
            for (SkipSpace(s, ref pos); s[pos] != ']'; SkipSpace(s, ref pos)) array.Add(ParseValue(s, ref pos));
            pos++;
            return array;
        }
        if (c == '{')
        {
            var obj = new JObject();
            pos++;
            for (SkipSpace(s, ref pos); s[pos] != '}'; SkipSpace(s, ref pos))
            {
                int eq = s.IndexOf('=', pos);
                var keys = Keys(s.Substring(pos, eq - pos));
                pos = eq + 1;
                Table(obj, keys.Take(keys.Count - 1))[keys[keys.Count - 1]] = ParseValue(s, ref pos);
            }
            pos++;
            return obj;
        }
        int start = pos;
        while (pos < s.Length && s[pos] is not (',' or ']' or '}' or '\n')) pos++;
        var raw = s.Substring(start, pos - start).Trim();
        if (raw == "true") return new JValue(true);
        if (raw == "false") return new JValue(false);
        var num = raw.Replace("_", "");
        if (num.StartsWith("0x") && long.TryParse(num.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return new JValue(hex);
        if (num.StartsWith("0o")) return new JValue(System.Convert.ToInt64(num.Substring(2), 8));
        if (num.StartsWith("0b")) return new JValue(System.Convert.ToInt64(num.Substring(2), 2));
        if (Regex.IsMatch(num, @"^[-+]?\d+$") && long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return new JValue(l);
        if (num is "inf" or "+inf") return new JValue(double.PositiveInfinity);
        if (num == "-inf") return new JValue(double.NegativeInfinity);
        if (num is "nan" or "+nan" or "-nan") return new JValue(double.NaN);
        if (Regex.IsMatch(num, @"^[-+]?\d+(\.\d+)?([eE][-+]?\d+)?$") && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new JValue(d);
        // Dates and times are kept as their text
        if (Regex.IsMatch(raw, @"^\d{4}-\d{2}-\d{2}|^\d{2}:\d{2}")) return new JValue(raw);
        throw new FormatException("无法识别的值：" + raw);
    }

    static string Unescape(string s) => Regex.Replace(s, @"\\(u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|.)", m =>
    {
        var e = m.Groups[1].Value;
        return e[0] switch
        {
            'n' => "\n", 't' => "\t", 'r' => "\r", 'b' => "\b", 'f' => "\f", '"' => "\"", '\\' => "\\",
            'u' or 'U' when e.Length > 1 => char.ConvertFromUtf32(int.Parse(e.Substring(1), NumberStyles.HexNumber)),
            _ => m.Value,
        };
    });

    public static string Write(JToken token)
    {
        if (token is not JObject root) throw new FormatException("TOML 需要 JSON 对象");
        var sb = new StringBuilder();
        WriteTable(sb, root, "");
        return sb.ToString().TrimStart('\n');
    }

    static bool IsTableArray(JToken value) => value is JArray { Count: > 0 } a && a.All(t => t is JObject);

    static void WriteTable(StringBuilder sb, JObject obj, string prefix)
    {
        foreach (var p in obj.Properties().Where(p => p.Value is not JObject && !IsTableArray(p.Value) && p.Value.Type != JTokenType.Null))
            sb.Append(Key(p.Name)).Append(" = ").Append(Value(p.Value)).Append('\n');
        foreach (var p in obj.Properties())
        {
            var path = prefix.Length == 0 ? Key(p.Name) : prefix + "." + Key(p.Name);
            if (p.Value is JObject child)
            {
                if (child.Properties().Any(c => c.Value is not JObject && !IsTableArray(c.Value)) || child.Count == 0)
                    sb.Append('\n').Append('[').Append(path).Append("]\n");
                WriteTable(sb, child, path);
            }
            else if (IsTableArray(p.Value))
            {
                foreach (JObject item in (JArray)p.Value)
                {
                    sb.Append('\n').Append("[[").Append(path).Append("]]\n");
                    WriteTable(sb, item, path);
                }
            }
        }
    }

    static string Key(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9_-]+$") ? name : JsonConvert.ToString(name);

    static string Value(JToken value) => value switch
    {
        JArray a => "[" + string.Join(", ", a.Where(t => t.Type != JTokenType.Null).Select(Value)) + "]",
        JObject o => "{ " + string.Join(", ", o.Properties().Where(p => p.Value.Type != JTokenType.Null).Select(p => Key(p.Name) + " = " + Value(p.Value))) + " }",
        JValue { Type: JTokenType.Boolean } b => (bool)b ? "true" : "false",
        JValue { Type: JTokenType.Integer } i => System.Convert.ToString(i.Value, CultureInfo.InvariantCulture)!,
        JValue { Type: JTokenType.Float } f => FloatText((double)f),
        JValue { Type: JTokenType.Date } d => ((DateTime)d).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
        JValue v => JsonConvert.ToString(System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? ""),
        _ => "\"\"",
    };

    static string FloatText(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }
}

sealed class CodeOptions
{
    public CodeOptions(bool record, bool nullable) { Record = record; Nullable = nullable; }
    public bool Record { get; }
    public bool Nullable { get; }
}

/// <summary>Builds C#, TypeScript, Java and Go types whose shape matches a JSON sample.</summary>
static class TypeGenerator
{
    enum Kind { Any, Int, Long, Double, Bool, String, Date, Object, Array }

    sealed class Shape
    {
        public Kind Kind;
        public string? Class;
        public Shape? Item;
        public bool Nullable;
    }

    sealed class Field
    {
        public Field(string jsonName, Shape shape) { JsonName = jsonName; Shape = shape; }
        public string JsonName { get; }
        public Shape Shape { get; }
    }

    sealed class ClassDef
    {
        public ClassDef(string name, List<Field> fields) { Name = name; Fields = fields; }
        public string Name { get; }
        public List<Field> Fields { get; }
    }

    static List<ClassDef> Analyze(JToken token)
    {
        var classes = new List<ClassDef>();
        var names = new HashSet<string>();
        var root = token is JArray arr ? Merge(arr) : token as JObject;
        if (root == null) throw new FormatException("需要 JSON 对象或对象数组");
        Build("Root", root, classes, names);
        classes.Reverse();
        return classes;
    }

    /// <summary>Combines every object of an array so optional properties are not missed; missing or null ones are marked.</summary>
    static JObject? Merge(JArray array)
    {
        var objects = array.OfType<JObject>().ToList();
        if (objects.Count == 0) return null;
        var merged = new JObject();
        foreach (var o in objects)
            foreach (var p in o.Properties())
                if (merged[p.Name] == null || merged[p.Name]!.Type == JTokenType.Null) merged[p.Name] = p.Value;
        foreach (var p in merged.Properties())
            if (objects.Any(o => o[p.Name] == null || o[p.Name]!.Type == JTokenType.Null)) p.AddAnnotation(new Optional());
        return merged;
    }

    sealed class Optional { }

    static string Build(string name, JObject obj, List<ClassDef> classes, HashSet<string> names)
    {
        var className = Unique(Pascal(name), names);
        var fields = new List<Field>();
        foreach (var p in obj.Properties())
        {
            var shape = ShapeOf(Pascal(p.Name), p.Value, classes, names);
            if (p.Annotation<Optional>() != null || p.Value.Type == JTokenType.Null) shape.Nullable = true;
            fields.Add(new Field(p.Name, shape));
        }
        classes.Add(new ClassDef(className, fields));
        return className;
    }

    static Shape ShapeOf(string prop, JToken value, List<ClassDef> classes, HashSet<string> names)
    {
        switch (value.Type)
        {
            case JTokenType.Integer:
                return new Shape { Kind = value.Value<long>() is >= int.MinValue and <= int.MaxValue ? Kind.Int : Kind.Long };
            case JTokenType.Float: return new Shape { Kind = Kind.Double };
            case JTokenType.Boolean: return new Shape { Kind = Kind.Bool };
            case JTokenType.String: return new Shape { Kind = Kind.String };
            case JTokenType.Date: return new Shape { Kind = Kind.Date };
            case JTokenType.Object: return new Shape { Kind = Kind.Object, Class = Build(prop, (JObject)value, classes, names) };
            case JTokenType.Array:
                var array = (JArray)value;
                var item = new Shape();
                if (array.Count > 0 && array.All(t => t is JObject)) item = new Shape { Kind = Kind.Object, Class = Build(Singular(prop), Merge(array)!, classes, names) };
                else
                {
                    var kinds = array.Where(t => t.Type != JTokenType.Null).Select(t => ShapeOf(prop, t, classes, names)).ToList();
                    var distinct = kinds.Select(k => k.Kind).Distinct().ToList();
                    if (distinct.Count == 1) item = kinds[0];
                    else if (distinct.Count == 2 && distinct.Contains(Kind.Int) && distinct.Contains(Kind.Double)) item = new Shape { Kind = Kind.Double };
                    else if (distinct.Count == 2 && distinct.Contains(Kind.Int) && distinct.Contains(Kind.Long)) item = new Shape { Kind = Kind.Long };
                }
                return new Shape { Kind = Kind.Array, Item = item };
            default: return new Shape();
        }
    }

    // C#

    public static string CSharp(JToken token, CodeOptions options)
    {
        var sb = new StringBuilder("using System.Collections.Generic;\nusing Newtonsoft.Json;\n");
        if (options.Nullable) sb.Append("\n#nullable enable\n");
        foreach (var c in Analyze(token))
        {
            sb.Append('\n');
            var used = new HashSet<string> { c.Name };
            var props = c.Fields.Select(f => (f.JsonName, Name: Unique(Pascal(f.JsonName), used), Type: CsType(f.Shape, options.Nullable))).ToList();
            if (options.Record)
            {
                sb.Append($"public record {c.Name}(\n");
                sb.Append(string.Join(",\n", props.Select(p => "    " + (p.Name != p.JsonName ? $"[property: JsonProperty(\"{Escape(p.JsonName)}\")] " : "") + $"{p.Type} {p.Name}")));
                sb.Append(");\n");
                continue;
            }
            sb.Append($"public class {c.Name}\n{{\n");
            foreach (var p in props)
            {
                if (p.Name != p.JsonName) sb.Append($"    [JsonProperty(\"{Escape(p.JsonName)}\")]\n");
                sb.Append($"    public {p.Type} {p.Name} {{ get; set; }}");
                if (options.Nullable && !p.Type.EndsWith("?") && !IsValueType(p.Type)) sb.Append(" = default!;");
                sb.Append('\n');
            }
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    static bool IsValueType(string type) => type is "int" or "long" or "double" or "bool" or "System.DateTime";

    static string CsType(Shape s, bool nullable)
    {
        var type = s.Kind switch
        {
            Kind.Int => "int", Kind.Long => "long", Kind.Double => "double", Kind.Bool => "bool", Kind.String => "string",
            Kind.Date => "System.DateTime", Kind.Object => s.Class!,
            Kind.Array => $"List<{CsType(s.Item!, false)}>",
            _ => "object",
        };
        return nullable && (s.Nullable || s.Kind == Kind.Any) ? type + "?" : type;
    }

    // TypeScript

    public static string TypeScript(JToken token)
    {
        var sb = new StringBuilder();
        foreach (var c in Analyze(token))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"export interface {c.Name} {{\n");
            foreach (var f in c.Fields)
            {
                var name = Regex.IsMatch(f.JsonName, @"^[A-Za-z_$][\w$]*$") ? f.JsonName : JsonConvert.ToString(f.JsonName);
                sb.Append($"  {name}{(f.Shape.Nullable ? "?" : "")}: {TsType(f.Shape)};\n");
            }
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    static string TsType(Shape s) => s.Kind switch
    {
        Kind.Int or Kind.Long or Kind.Double => "number",
        Kind.Bool => "boolean",
        Kind.String or Kind.Date => "string",
        Kind.Object => s.Class!,
        Kind.Array => s.Item!.Kind == Kind.Any ? "unknown[]" : TsType(s.Item!) + "[]",
        _ => "unknown",
    };

    // Java

    public static string Java(JToken token)
    {
        var sb = new StringBuilder("import java.util.List;\nimport com.fasterxml.jackson.annotation.JsonProperty;\n");
        foreach (var c in Analyze(token))
        {
            sb.Append($"\npublic class {c.Name} {{\n");
            var used = new HashSet<string>();
            var props = c.Fields.Select(f => (f.JsonName, Name: Unique(Camel(f.JsonName), used), Type: JavaType(f.Shape, false))).ToList();
            foreach (var p in props)
            {
                if (p.Name != p.JsonName) sb.Append($"    @JsonProperty(\"{Escape(p.JsonName)}\")\n");
                sb.Append($"    private {p.Type} {p.Name};\n");
            }
            foreach (var p in props)
            {
                var upper = char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1);
                var getter = p.Type == "boolean" ? "is" : "get";
                sb.Append($"\n    public {p.Type} {getter}{upper}() {{ return {p.Name}; }}\n");
                sb.Append($"    public void set{upper}({p.Type} {p.Name}) {{ this.{p.Name} = {p.Name}; }}\n");
            }
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    static string JavaType(Shape s, bool boxed)
    {
        boxed |= s.Nullable;
        return s.Kind switch
        {
            Kind.Int => boxed ? "Integer" : "int",
            Kind.Long => boxed ? "Long" : "long",
            Kind.Double => boxed ? "Double" : "double",
            Kind.Bool => boxed ? "Boolean" : "boolean",
            Kind.String => "String",
            Kind.Date => "java.time.OffsetDateTime",
            Kind.Object => s.Class!,
            Kind.Array => $"List<{JavaType(s.Item!, true)}>",
            _ => "Object",
        };
    }

    // Go

    public static string Go(JToken token)
    {
        var sb = new StringBuilder();
        var classes = Analyze(token);
        if (classes.Any(c => c.Fields.Any(f => f.Shape.Kind == Kind.Date))) sb.Append("import \"time\"\n\n");
        foreach (var c in classes)
        {
            sb.Append($"type {c.Name} struct {{\n");
            var used = new HashSet<string>();
            var fields = c.Fields.Select(f => (f.JsonName, Name: Unique(Pascal(f.JsonName), used), Type: GoType(f.Shape), f.Shape.Nullable)).ToList();
            int nameWidth = fields.Count == 0 ? 0 : fields.Max(f => f.Name.Length);
            int typeWidth = fields.Count == 0 ? 0 : fields.Max(f => f.Type.Length);
            foreach (var f in fields)
                sb.Append($"\t{f.Name.PadRight(nameWidth)} {f.Type.PadRight(typeWidth)} `json:\"{f.JsonName.Replace("\"", "\\\"")}{(f.Nullable ? ",omitempty" : "")}\"`\n");
            sb.Append("}\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    static string GoType(Shape s)
    {
        var type = s.Kind switch
        {
            Kind.Int => "int", Kind.Long => "int64", Kind.Double => "float64", Kind.Bool => "bool", Kind.String => "string",
            Kind.Date => "time.Time", Kind.Object => s.Class!,
            Kind.Array => "[]" + GoType(s.Item!),
            _ => "interface{}",
        };
        return s.Nullable && s.Kind is not (Kind.Array or Kind.Any) ? "*" + type : type;
    }

    // Naming

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string Pascal(string name)
    {
        var parts = Regex.Split(name, @"[^\p{L}\p{Nd}]+").Where(s => s.Length > 0).ToList();
        var result = string.Concat(parts.Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
        if (result.Length == 0) return "Value";
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    static string Camel(string name)
    {
        var pascal = Pascal(name);
        return pascal[0] == '_' ? pascal : char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }

    static string Singular(string name) =>
        name.EndsWith("ies") && name.Length > 3 ? name.Substring(0, name.Length - 3) + "y"
        : name.EndsWith("ses") || name.EndsWith("xes") ? name.Substring(0, name.Length - 2)
        : name.EndsWith("s") && !name.EndsWith("ss") && name.Length > 1 ? name.Substring(0, name.Length - 1)
        : name + "Item";

    static string Unique(string name, HashSet<string> used)
    {
        var result = name;
        for (int i = 2; !used.Add(result); i++) result = name + i;
        return result;
    }
}

/// <summary>Infers a draft-07 JSON Schema from a sample.</summary>
static class JsonSchema
{
    public static JObject Generate(JToken token)
    {
        var schema = Of(token);
        var result = new JObject { ["$schema"] = "http://json-schema.org/draft-07/schema#" };
        foreach (var p in schema.Properties()) result[p.Name] = p.Value;
        return result;
    }

    static JObject Of(JToken token)
    {
        switch (token)
        {
            case JObject o:
                var props = new JObject();
                foreach (var p in o.Properties()) props[p.Name] = Of(p.Value);
                var required = o.Properties().Where(p => p.Value.Type != JTokenType.Null).Select(p => (JToken)p.Name).ToList();
                var obj = new JObject { ["type"] = "object", ["properties"] = props };
                if (required.Count > 0) obj["required"] = new JArray(required);
                return obj;
            case JArray a:
                var array = new JObject { ["type"] = "array" };
                if (a.Count == 0) return array;
                if (a.All(t => t is JObject)) array["items"] = MergeObjects(a.Cast<JObject>().ToList());
                else
                {
                    var items = a.Select(Of).GroupBy(s => s.ToString()).Select(g => g.First()).ToList();
                    if (items.Count == 2 && items.Any(i => (string?)i["type"] == "integer") && items.Any(i => (string?)i["type"] == "number"))
                        items = new List<JObject> { new() { ["type"] = "number" } };
                    array["items"] = items.Count == 1 ? items[0] : new JObject { ["anyOf"] = new JArray(items) };
                }
                return array;
            default:
                return new JObject
                {
                    ["type"] = token.Type switch
                    {
                        JTokenType.Integer => "integer",
                        JTokenType.Float => "number",
                        JTokenType.Boolean => "boolean",
                        JTokenType.Null => "null",
                        _ => "string",
                    },
                };
        }
    }

    /// <summary>Only properties present and non-null in every object are required.</summary>
    static JObject MergeObjects(List<JObject> objects)
    {
        var merged = new JObject();
        foreach (var o in objects)
            foreach (var p in o.Properties())
                if (merged[p.Name] == null || merged[p.Name]!.Type == JTokenType.Null) merged[p.Name] = p.Value;
        var schema = Of(merged);
        var required = merged.Properties().Select(p => p.Name)
            .Where(n => objects.All(o => o[n] != null && o[n]!.Type != JTokenType.Null)).Select(n => (JToken)n).ToList();
        schema.Remove("required");
        if (required.Count > 0) schema["required"] = new JArray(required);
        return schema;
    }
}
