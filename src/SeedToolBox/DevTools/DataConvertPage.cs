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

/// <summary>Converts between JSON, XML, YAML and CSV, and generates C# classes from JSON.</summary>
sealed class DataConvertPage : DockPanel
{
    static readonly string[] Sources = { "JSON", "XML", "YAML", "CSV" };
    static readonly string[] Targets = { "JSON", "XML", "YAML", "CSV", "C# 类" };

    readonly TextBox _input = Ui.Area();
    readonly TextBox _output = Ui.Area();
    readonly AsyncToken _busy = new();
    readonly ComboBox _from = new() { Width = 100, ItemsSource = Sources, SelectedIndex = 0 };
    readonly ComboBox _to = new() { Width = 100, ItemsSource = Targets, SelectedIndex = 2 };
    readonly TextBlock _status = Ui.Status();

    public DataConvertPage()
    {
        var header = Ui.Header("数据格式互转", "JSON / XML / YAML / CSV 互相转换，JSON 生成 C# 类");
        var swap = Ui.Button("⇄", Swap);
        swap.MinWidth = 40;
        swap.Margin = new Thickness(8, 0, 8, 0);
        var toolbar = Ui.Row(
            Ui.Label("从"), _from, swap, Ui.Label("到"), _to, Ui.Label("", 16),
            Ui.Button("转换", Convert, accent: true),
            Ui.Button("清空", () => { _input.Clear(); _output.Clear(); _status.Text = ""; }));

        Ui.FileDrop(_input, files =>
        {
            if (!Ui.LoadText(_input, files[0], _status)) return;
            var ext = Path.GetExtension(files[0]).ToLowerInvariant();
            int i = ext switch { ".xml" => 1, ".yml" or ".yaml" => 2, ".csv" => 3, _ => 0 };
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
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);
    }

    void Swap()
    {
        if (_to.SelectedIndex > 3) return;
        (_from.SelectedIndex, _to.SelectedIndex) = (_to.SelectedIndex, _from.SelectedIndex);
        if (_output.Text.Length > 0) (_input.Text, _output.Text) = (_output.Text, "");
    }

    void Convert()
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        int from = _from.SelectedIndex, to = _to.SelectedIndex;
        var label = $"{_from.SelectedItem} → {_to.SelectedItem} 完成";
        Ui.SetStatus(_status, "转换中…");
        Ui.RunAsync(_busy, () =>
        {
            var token = from switch
            {
                1 => FromXml(text),
                2 => FromYaml(text),
                3 => FromCsv(text),
                _ => JToken.Parse(text),
            };
            return to switch
            {
                1 => ToXml(token),
                2 => ToYaml(token),
                3 => ToCsv(token),
                4 => CSharpGenerator.Generate(token),
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
                if (Regex.IsMatch(value, @"^[-+]?\d+$") && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return new JValue(l);
                if (Regex.IsMatch(value, @"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new JValue(d);
                return new JValue(value);
            default:
                return JValue.CreateNull();
        }
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

    static JToken FromCsv(string text)
    {
        var rows = ParseCsv(text);
        if (rows.Count == 0) return new JArray();
        var header = rows[0];
        var array = new JArray();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && row[0].Length == 0) continue;
            var obj = new JObject();
            for (int i = 0; i < header.Count; i++) obj[header[i]] = i < row.Count ? row[i] : "";
            array.Add(obj);
        }
        return array;
    }

    static List<List<string>> ParseCsv(string text)
    {
        // Tab-separated when the first line has tabs but no commas (pasted from Excel)
        var firstLine = text.Split('\n')[0];
        char sep = firstLine.Contains('\t') && !firstLine.Contains(',') ? '\t' : ',';
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

    static string ToCsv(JToken token)
    {
        var items = token is JArray a ? a.ToList() : new List<JToken> { token };
        if (items.Any(i => i is not JObject)) throw new FormatException("CSV 需要对象数组，例如 [{\"a\":1},{\"a\":2}]");
        var columns = items.Cast<JObject>().SelectMany(o => o.Properties().Select(p => p.Name)).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(Quote)));
        foreach (JObject o in items)
        {
            sb.AppendLine(string.Join(",", columns.Select(c => o[c] switch
            {
                null or { Type: JTokenType.Null } => "",
                JValue { Type: JTokenType.Boolean } b => (bool)b ? "true" : "false",
                JValue v => Quote(System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? ""),
                var nested => Quote(nested.ToString(Newtonsoft.Json.Formatting.None)),
            })));
        }
        return sb.ToString();
    }

    static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.Trim() != s ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Builds C# classes whose shape matches a JSON sample.</summary>
static class CSharpGenerator
{
    public static string Generate(JToken token)
    {
        var classes = new List<string>();
        var names = new HashSet<string>();
        var root = token is JArray arr ? Merge(arr) : token as JObject;
        if (root == null) throw new FormatException("需要 JSON 对象或对象数组");
        Build("Root", root, classes, names);
        classes.Reverse();
        return "using System.Collections.Generic;\nusing Newtonsoft.Json;\n\n" + string.Join("\n", classes);
    }

    /// <summary>Combines every object of an array so optional properties are not missed.</summary>
    static JObject? Merge(JArray array)
    {
        var objects = array.OfType<JObject>().ToList();
        if (objects.Count == 0) return null;
        var merged = new JObject();
        foreach (var o in objects)
            foreach (var p in o.Properties())
                if (merged[p.Name] == null || merged[p.Name]!.Type == JTokenType.Null) merged[p.Name] = p.Value;
        return merged;
    }

    static string Build(string name, JObject obj, List<string> classes, HashSet<string> names)
    {
        var className = Unique(name, names);
        var sb = new StringBuilder();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        var used = new HashSet<string> { className };
        foreach (var p in obj.Properties())
        {
            var prop = Unique(Pascal(p.Name), used);
            var type = TypeOf(prop, p.Value, classes, names);
            if (prop != p.Name) sb.AppendLine($"    [JsonProperty(\"{p.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\")]");
            sb.AppendLine($"    public {type} {prop} {{ get; set; }}");
        }
        sb.AppendLine("}");
        classes.Add(sb.ToString());
        return className;
    }

    static string TypeOf(string prop, JToken value, List<string> classes, HashSet<string> names)
    {
        switch (value.Type)
        {
            case JTokenType.Integer:
                return value.Value<long>() is >= int.MinValue and <= int.MaxValue ? "int" : "long";
            case JTokenType.Float: return "double";
            case JTokenType.Boolean: return "bool";
            case JTokenType.String: return "string";
            case JTokenType.Date: return "System.DateTime";
            case JTokenType.Object: return Build(prop, (JObject)value, classes, names);
            case JTokenType.Array:
                var array = (JArray)value;
                if (array.Count == 0) return "List<object>";
                if (array.All(t => t is JObject)) return $"List<{Build(Singular(prop), Merge(array)!, classes, names)}>";
                var types = array.Where(t => t.Type != JTokenType.Null).Select(t => TypeOf(prop, t, classes, names)).Distinct().ToList();
                if (types.Count == 2 && types.Contains("int") && types.Contains("double")) return "List<double>";
                if (types.Count == 2 && types.Contains("int") && types.Contains("long")) return "List<long>";
                return types.Count == 1 ? $"List<{types[0]}>" : "List<object>";
            default: return "object";
        }
    }

    static string Pascal(string name)
    {
        var parts = Regex.Split(name, @"[^\p{L}\p{Nd}]+").Where(s => s.Length > 0).ToList();
        var result = string.Concat(parts.Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
        if (result.Length == 0) return "Value";
        return char.IsDigit(result[0]) ? "_" + result : result;
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
