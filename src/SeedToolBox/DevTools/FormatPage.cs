using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUglify;
using NUglify.Css;
using NUglify.JavaScript;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Beautifies or minifies JSON, JavaScript and CSS.</summary>
sealed class FormatPage : DockPanel
{
    readonly TextBox _input = Ui.Area();
    readonly TextBox _output = Ui.Area();
    readonly ComboBox _language = new() { Width = 120, ItemsSource = new[] { "JSON", "JavaScript", "CSS" }, SelectedIndex = 0 };
    readonly ComboBox _indent = new() { Width = 100, ItemsSource = new[] { "2 空格", "4 空格", "Tab" }, SelectedIndex = 1 };
    readonly TextBlock _status = Ui.Status();

    public FormatPage()
    {
        var header = Ui.Header("代码格式化", "JSON / JavaScript / CSS 的格式化、压缩与校验");
        var toolbar = Ui.Row(
            Ui.Label("语言"), _language, Ui.Label("", 16),
            Ui.Label("缩进"), _indent, Ui.Label("", 16),
            Ui.Button("格式化", Beautify, accent: true),
            Ui.Button("压缩", Minify),
            Ui.Button("校验", Validate),
            Ui.Button("清空", () => { _input.Clear(); _output.Clear(); _status.Text = ""; }));

        _input.PreviewDragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        _input.PreviewDrop += OnDrop;

        var copy = Ui.Button("复制结果", () => { if (_output.Text.Length > 0) ScreenToolService.CopyText(_output.Text); });
        copy.Margin = new Thickness(0);
        var swap = Ui.Button("结果放回输入", () => _input.Text = _output.Text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { swap, copy } };

        var body = Ui.Columns(Ui.Titled("输入（可拖入文件）", _input), Ui.Titled("结果", _output, buttons));
        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(body);
    }

    string Language => (string)_language.SelectedItem;

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var path = files[0];
        try
        {
            _input.Text = TextFiles.Read(path, out _);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            _language.SelectedItem = ext switch { ".js" or ".mjs" or ".cjs" or ".ts" => "JavaScript", ".css" => "CSS", _ => "JSON" };
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, "读取失败：" + ex.Message, true);
        }
    }

    void Beautify() => Run(true);
    void Minify() => Run(false);

    void Run(bool beautify)
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        try
        {
            _output.Text = Language switch
            {
                "JSON" => FormatJson(text, beautify),
                "JavaScript" => FormatJs(text, beautify),
                _ => FormatCss(text, beautify),
            };
            Ui.SetStatus(_status, $"完成：{text.Length:N0} → {_output.Text.Length:N0} 字符");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, ex.Message, true);
        }
    }

    void Validate()
    {
        var text = _input.Text;
        if (text.Trim().Length == 0) { Ui.SetStatus(_status, "请先输入内容", true); return; }
        try
        {
            if (Language == "JSON") ParseJson(text);
            else if (Language == "JavaScript") Check(Uglify.Js(text));
            else Check(Uglify.Css(text));
            Ui.SetStatus(_status, $"{Language} 语法正确");
        }
        catch (Exception ex)
        {
            Ui.SetStatus(_status, ex.Message, true);
        }
    }

    string IndentText => _indent.SelectedIndex switch { 0 => "  ", 2 => "\t", _ => "    " };

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

    string FormatJson(string text, bool beautify)
    {
        var token = ParseJson(text);
        if (!beautify) return token.ToString(Formatting.None);
        using var writer = new StringWriter();
        using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented };
        if (_indent.SelectedIndex == 2) { json.IndentChar = '\t'; json.Indentation = 1; }
        else json.Indentation = _indent.SelectedIndex == 0 ? 2 : 4;
        token.WriteTo(json);
        json.Flush();
        return writer.ToString();
    }

    string FormatJs(string text, bool beautify)
    {
        var settings = new CodeSettings();
        if (beautify)
        {
            settings.MinifyCode = false;
            settings.OutputMode = OutputMode.MultipleLines;
            settings.IndentSize = IndentText == "\t" ? 4 : IndentText.Length;
            settings.PreserveImportantComments = true;
            settings.BlocksStartOnSameLine = BlockStart.SameLine;
            settings.TermSemicolons = true;
        }
        var result = Uglify.Js(text, settings);
        Check(result);
        return beautify && IndentText == "\t" ? Retab(result.Code, 4) : result.Code;
    }

    string FormatCss(string text, bool beautify)
    {
        var settings = new CssSettings();
        var code = new CodeSettings();
        if (beautify)
        {
            settings.OutputMode = OutputMode.MultipleLines;
            settings.IndentSize = IndentText == "\t" ? 4 : IndentText.Length;
            settings.CommentMode = CssComment.All;
            settings.BlocksStartOnSameLine = BlockStart.SameLine;
            settings.TermSemicolons = true;
            code.MinifyCode = false;
        }
        var result = Uglify.Css(text, settings, code);
        Check(result);
        return beautify && IndentText == "\t" ? Retab(result.Code, 4) : result.Code;
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
