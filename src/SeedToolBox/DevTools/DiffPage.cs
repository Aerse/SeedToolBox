using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeedToolBox.DevTools;

/// <summary>Line-by-line comparison of two texts, shown as a coloured unified diff.</summary>
sealed class DiffPage : DockPanel
{
    readonly TextBox _left = Ui.Area();
    readonly TextBox _right = Ui.Area();
    readonly AsyncToken _busy = new();
    readonly RichTextBox _result = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly CheckBox _trim = new() { Content = "忽略行首尾空白", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _case = new() { Content = "忽略大小写", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _onlyChanges = new() { Content = "只显示差异", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();

    public DiffPage()
    {
        var header = Ui.Header("文本对比", "逐行比较两段文本，红色为删除，绿色为新增");
        var toolbar = Ui.Row(Ui.Button("比较", Compare, accent: true), Ui.Button("交换", () => (_left.Text, _right.Text) = (_right.Text, _left.Text)), _trim, _case, _onlyChanges);
        foreach (var box in new[] { _left, _right }) AllowFileDrop(box);
        foreach (var check in new[] { _trim, _case, _onlyChanges }) check.Click += (_, _) => Compare();

        _result.Style = null;
        _result.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _result.Document.PagePadding = new Thickness(4);
        _result.Document.PageWidth = 4000;

        var inputs = Ui.Columns(Ui.Titled("原文本（可拖入文件）", _left), Ui.Titled("新文本（可拖入文件）", _right));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        var result = Ui.Titled("对比结果", _result);
        Grid.SetRow(result, 2);
        grid.Children.Add(inputs);
        grid.Children.Add(result);

        SetDock(header, Dock.Top);
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(toolbar);
        Children.Add(_status);
        Children.Add(grid);
    }

    void AllowFileDrop(TextBox box) => Ui.FileDrop(box, files => Ui.LoadText(box, files[0], _status));

    void Compare()
    {
        var a = Lines(_left.Text);
        var b = Lines(_right.Text);
        bool trim = _trim.IsChecked == true, ignoreCase = _case.IsChecked == true;
        Func<string, string> key = s =>
        {
            if (trim) s = s.Trim();
            return ignoreCase ? s.ToLowerInvariant() : s;
        };
        Ui.SetStatus(_status, "对比中…");
        Ui.RunAsync(_busy, () => LineDiff.Compute(a, b, key), diff => Show(a, b, diff), ex => Ui.SetStatus(_status, ex.Message, true));
    }

    void Show(List<string> a, List<string> b, List<DiffLine> diff)
    {
        var paragraph = new Paragraph { LineHeight = 18 };
        var added = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0xC2, 0x5A));
        var removed = new SolidColorBrush(Color.FromArgb(0x40, 0xF0, 0x4A, 0x4A));
        var hint = Views.DialogWindow.HintBrush;
        int width = Math.Max(a.Count, b.Count).ToString().Length;
        bool onlyChanges = _onlyChanges.IsChecked == true;
        for (int i = 0; i < diff.Count; i++)
        {
            var d = diff[i];
            if (onlyChanges && d.Kind == ' ')
            {
                // Keep one line of context around each change
                bool near = (i > 0 && diff[i - 1].Kind != ' ') || (i + 1 < diff.Count && diff[i + 1].Kind != ' ');
                if (!near)
                {
                    if (i > 0 && paragraph.Inlines.LastInline is Run { Text: "  ⋯\n" }) continue;
                    paragraph.Inlines.Add(new Run("  ⋯\n") { Foreground = hint });
                    continue;
                }
            }
            var numbers = $"{(d.Left > 0 ? d.Left.ToString() : "").PadLeft(width)} {(d.Right > 0 ? d.Right.ToString() : "").PadLeft(width)} ";
            paragraph.Inlines.Add(new Run(numbers) { Foreground = hint });
            var run = new Run($"{d.Kind} {d.Text}\n");
            if (d.Kind == '+') run.Background = added;
            else if (d.Kind == '-') run.Background = removed;
            paragraph.Inlines.Add(run);
        }
        _result.Document.Blocks.Clear();
        _result.Document.Blocks.Add(paragraph);

        int plus = diff.Count(d => d.Kind == '+'), minus = diff.Count(d => d.Kind == '-');
        Ui.SetStatus(_status, plus + minus == 0 ? "两段文本相同" : $"新增 {plus} 行，删除 {minus} 行");
    }

    static List<string> Lines(string text) =>
        text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
}

readonly struct DiffLine
{
    public DiffLine(char kind, string text, int left, int right) { Kind = kind; Text = text; Left = left; Right = right; }
    /// <summary>' ' unchanged, '-' only in the left text, '+' only in the right text.</summary>
    public char Kind { get; }
    public string Text { get; }
    /// <summary>1-based line numbers, 0 when the line is not on that side.</summary>
    public int Left { get; }
    public int Right { get; }
}

static class LineDiff
{
    const long MaxCells = 40_000_000;

    /// <summary>Longest common subsequence diff after trimming the shared head and tail.</summary>
    public static List<DiffLine> Compute(IList<string> a, IList<string> b, Func<string, string> key)
    {
        var ka = a.Select(key).ToArray();
        var kb = b.Select(key).ToArray();
        int start = 0;
        while (start < ka.Length && start < kb.Length && ka[start] == kb[start]) start++;
        int endA = ka.Length, endB = kb.Length;
        while (endA > start && endB > start && ka[endA - 1] == kb[endB - 1]) { endA--; endB--; }

        int n = endA - start, m = endB - start;
        if ((long)(n + 1) * (m + 1) > MaxCells) throw new InvalidOperationException("差异部分太大（超过约 6000×6000 行），无法比较");

        // lcs[i, j] = LCS length of a[start+i..endA) and b[start+j..endB)
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = ka[start + i] == kb[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        for (int k = 0; k < start; k++) result.Add(new DiffLine(' ', b[k], k + 1, k + 1));
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && ka[start + x] == kb[start + y])
            {
                result.Add(new DiffLine(' ', b[start + y], start + x + 1, start + y + 1));
                x++;
                y++;
            }
            else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                result.Add(new DiffLine('-', a[start + x], start + x + 1, 0));
                x++;
            }
            else
            {
                result.Add(new DiffLine('+', b[start + y], 0, start + y + 1));
                y++;
            }
        }
        for (int k = 0; k < ka.Length - endA; k++) result.Add(new DiffLine(' ', b[endB + k], endA + k + 1, endB + k + 1));
        return result;
    }
}
