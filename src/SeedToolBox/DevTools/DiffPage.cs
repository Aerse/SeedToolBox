using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeedToolBox.DevTools;

/// <summary>Line-by-line comparison of two texts, shown as a coloured unified or side-by-side diff.</summary>
sealed class DiffPage : DockPanel
{
    readonly TextBox _left = Ui.Area();
    readonly TextBox _right = Ui.Area();
    readonly AsyncToken _busy = new();
    readonly RichTextBox _result = ResultBox();
    readonly RichTextBox _sideLeft = ResultBox();
    readonly RichTextBox _sideRight = ResultBox();
    readonly Grid _side;
    readonly CheckBox _trim = new() { Content = "忽略行首尾空白", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _case = new() { Content = "忽略大小写", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _onlyChanges = new() { Content = "只显示差异", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _sideBySide = new() { Content = "左右并排", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock _status = Ui.Status();
    readonly List<TextElement> _changes = new();
    DiffResult? _last;
    int _currentChange = -1;
    bool _syncing;

    static readonly Brush Added = Frozen(Color.FromArgb(0x40, 0x2E, 0xC2, 0x5A));
    static readonly Brush Removed = Frozen(Color.FromArgb(0x40, 0xF0, 0x4A, 0x4A));
    static readonly Brush AddedStrong = Frozen(Color.FromArgb(0x90, 0x2E, 0xC2, 0x5A));
    static readonly Brush RemovedStrong = Frozen(Color.FromArgb(0x90, 0xF0, 0x4A, 0x4A));
    static readonly Brush Filler = Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80));

    public DiffPage()
    {
        var header = Ui.Header("文本对比", "逐行比较两段文本，红色为删除，绿色为新增，行内改动加深显示");
        var toolbar = Ui.Row(Ui.Button("比较", Compare, accent: true), Ui.Button("交换", () => (_left.Text, _right.Text) = (_right.Text, _left.Text)), _trim, _case, _onlyChanges, _sideBySide);
        var patch = Ui.SaveButton(Patch, _status, "changes.patch");
        patch.Content = "导出补丁…";
        var nav = Ui.Row(Ui.Button("上一处", () => Navigate(-1)), Ui.Button("下一处", () => Navigate(1)), patch);
        nav.Margin = new Thickness(0);
        foreach (var box in new[] { _left, _right }) AllowFileDrop(box);
        foreach (var check in new[] { _trim, _case, _onlyChanges, _sideBySide }) check.Click += (_, _) => Compare();

        _side = Ui.Columns(_sideLeft, _sideRight);
        _side.Visibility = Visibility.Collapsed;
        SyncScroll(_sideLeft, _sideRight);
        SyncScroll(_sideRight, _sideLeft);

        var inputs = Ui.Columns(Ui.Titled("原文本（可拖入文件）", _left), Ui.Titled("新文本（可拖入文件）", _right));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        var result = Ui.Titled("对比结果", new Grid { Children = { _result, _side } }, nav);
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

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static RichTextBox ResultBox()
    {
        var box = new RichTextBox { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.Style = null;
        box.BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"];
        box.Document.PagePadding = new Thickness(4);
        box.Document.PageWidth = 4000;
        return box;
    }

    void SyncScroll(RichTextBox from, RichTextBox to) => from.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToVerticalOffset(e.VerticalOffset);
        to.ScrollToHorizontalOffset(e.HorizontalOffset);
        _syncing = false;
    }));

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
        Ui.RunAsync(_busy, () =>
        {
            var diff = LineDiff.Compute(a, b, key);
            return new DiffResult(a, b, diff, WordDiff.Pairs(diff));
        }, Show, ex => Ui.SetStatus(_status, ex.Message, true));
    }

    void Show(DiffResult r)
    {
        _last = r;
        _changes.Clear();
        _currentChange = -1;
        bool side = _sideBySide.IsChecked == true;
        _result.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
        _side.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        if (side) ShowSideBySide(r);
        else ShowUnified(r);

        int plus = r.Diff.Count(d => d.Kind == '+'), minus = r.Diff.Count(d => d.Kind == '-');
        Ui.SetStatus(_status, plus + minus == 0 ? "两段文本相同" : $"新增 {plus} 行，删除 {minus} 行，共 {_changes.Count} 处改动");
    }

    /// <summary>Whether "only changes" hides line <paramref name="i"/>: an unchanged line not next to a change.</summary>
    bool Hidden(List<DiffLine> diff, int i) =>
        _onlyChanges.IsChecked == true && diff[i].Kind == ' '
        && !((i > 0 && diff[i - 1].Kind != ' ') || (i + 1 < diff.Count && diff[i + 1].Kind != ' '));

    static void AddGap(Paragraph paragraph)
    {
        if (paragraph.Inlines.LastInline is Run { Text: "  ⋯\n" }) return;
        paragraph.Inlines.Add(new Run("  ⋯\n") { Foreground = Views.DialogWindow.HintBrush });
    }

    static void AddText(Paragraph paragraph, char kind, string text, List<(string Text, bool Changed)>? segments)
    {
        var background = kind == '+' ? Added : kind == '-' ? Removed : null;
        if (segments == null)
        {
            paragraph.Inlines.Add(new Run(text + "\n") { Background = background });
            return;
        }
        var strong = kind == '+' ? AddedStrong : RemovedStrong;
        foreach (var (part, changed) in segments) paragraph.Inlines.Add(new Run(part) { Background = changed ? strong : background });
        paragraph.Inlines.Add(new Run("\n") { Background = background });
    }

    void ShowUnified(DiffResult r)
    {
        var paragraph = new Paragraph { LineHeight = 18 };
        var hint = Views.DialogWindow.HintBrush;
        int width = Math.Max(r.A.Count, r.B.Count).ToString().Length;
        for (int i = 0; i < r.Diff.Count; i++)
        {
            var d = r.Diff[i];
            if (Hidden(r.Diff, i)) { AddGap(paragraph); continue; }
            var numbers = new Run($"{(d.Left > 0 ? d.Left.ToString() : "").PadLeft(width)} {(d.Right > 0 ? d.Right.ToString() : "").PadLeft(width)} ") { Foreground = hint };
            paragraph.Inlines.Add(numbers);
            if (d.Kind != ' ' && (i == 0 || r.Diff[i - 1].Kind == ' ')) _changes.Add(numbers);
            paragraph.Inlines.Add(new Run(d.Kind + " ") { Background = d.Kind == '+' ? Added : d.Kind == '-' ? Removed : null });
            r.Segments.TryGetValue(i, out var segments);
            AddText(paragraph, d.Kind, d.Text, segments);
        }
        _result.Document.Blocks.Clear();
        _result.Document.Blocks.Add(paragraph);
    }

    void ShowSideBySide(DiffResult r)
    {
        var left = new Paragraph { LineHeight = 18 };
        var right = new Paragraph { LineHeight = 18 };
        var hint = Views.DialogWindow.HintBrush;
        int width = Math.Max(r.A.Count, r.B.Count).ToString().Length;
        var diff = r.Diff;
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == ' ')
            {
                if (Hidden(diff, i)) { AddGap(left); AddGap(right); }
                else
                {
                    left.Inlines.Add(new Run(diff[i].Left.ToString().PadLeft(width) + " ") { Foreground = hint });
                    left.Inlines.Add(new Run(r.A[diff[i].Left - 1] + "\n"));
                    right.Inlines.Add(new Run(diff[i].Right.ToString().PadLeft(width) + " ") { Foreground = hint });
                    right.Inlines.Add(new Run(diff[i].Text + "\n"));
                }
                i++;
                continue;
            }
            // A change block: removed lines on the left, added lines on the right, padded to the same height
            var removed = new List<int>();
            var added = new List<int>();
            while (i < diff.Count && diff[i].Kind == '-') removed.Add(i++);
            while (i < diff.Count && diff[i].Kind == '+') added.Add(i++);
            for (int k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                var number = Side(left, diff, r, removed, k, width);
                if (k == 0) _changes.Add(number);
                Side(right, diff, r, added, k, width);
            }
        }
        _sideLeft.Document.Blocks.Clear();
        _sideLeft.Document.Blocks.Add(left);
        _sideRight.Document.Blocks.Clear();
        _sideRight.Document.Blocks.Add(right);
    }

    static Run Side(Paragraph paragraph, List<DiffLine> diff, DiffResult r, List<int> lines, int k, int width)
    {
        if (k >= lines.Count)
        {
            var blank = new Run(new string(' ', width + 1)) { Background = Filler };
            paragraph.Inlines.Add(blank);
            paragraph.Inlines.Add(new Run("\n") { Background = Filler });
            return blank;
        }
        var d = diff[lines[k]];
        var number = new Run(Math.Max(d.Left, d.Right).ToString().PadLeft(width) + " ") { Foreground = Views.DialogWindow.HintBrush };
        paragraph.Inlines.Add(number);
        r.Segments.TryGetValue(lines[k], out var segments);
        AddText(paragraph, d.Kind, d.Text, segments);
        return number;
    }

    void Navigate(int step)
    {
        if (_changes.Count == 0) { Ui.SetStatus(_status, _last == null ? "请先点比较" : "没有差异", _last == null); return; }
        _currentChange = _currentChange < 0 && step < 0 ? _changes.Count - 1 : (_currentChange + step + _changes.Count) % _changes.Count;
        var box = _sideBySide.IsChecked == true ? _sideLeft : _result;
        var target = _changes[_currentChange];
        box.UpdateLayout();
        var rect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty) box.ScrollToVerticalOffset(Math.Max(0, box.VerticalOffset + rect.Top - box.ActualHeight / 3));
        box.ScrollToHorizontalOffset(0);
        box.Selection.Select(target.ContentStart, target.ContentStart);
        Ui.SetStatus(_status, $"第 {_currentChange + 1} / {_changes.Count} 处改动");
    }

    /// <summary>The last comparison as a unified diff with three lines of context.</summary>
    string Patch()
    {
        if (_last == null) return "";
        var diff = _last.Diff;
        if (diff.All(d => d.Kind == ' ')) return "";
        const int context = 3;
        var sb = new StringBuilder("--- a\n+++ b\n");
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == ' ') { i++; continue; }
            int start = Math.Max(0, i - context), end = i;
            // Grow the hunk while the next change is at most 2 * context unchanged lines away
            while (true)
            {
                while (end < diff.Count && diff[end].Kind != ' ') end++;
                int gap = end;
                while (gap < diff.Count && diff[gap].Kind == ' ' && gap - end < 2 * context) gap++;
                if (gap < diff.Count && diff[gap].Kind != ' ') { end = gap; continue; }
                end = Math.Min(diff.Count, end + context);
                break;
            }
            int leftStart = 0, rightStart = 0, leftCount = 0, rightCount = 0;
            for (int k = start; k < end; k++)
            {
                if (diff[k].Kind != '+' && leftCount++ == 0) leftStart = diff[k].Left;
                if (diff[k].Kind != '-' && rightCount++ == 0) rightStart = diff[k].Right;
            }
            // An empty side is numbered by the line before the hunk
            if (leftCount == 0) leftStart = diff.Take(start).Select(d => d.Left).DefaultIfEmpty(0).Max();
            if (rightCount == 0) rightStart = diff.Take(start).Select(d => d.Right).DefaultIfEmpty(0).Max();
            sb.Append($"@@ -{Range(leftStart, leftCount)} +{Range(rightStart, rightCount)} @@\n");
            for (int k = start; k < end; k++)
                sb.Append(diff[k].Kind).Append(diff[k].Kind == '-' ? _last.A[diff[k].Left - 1] : diff[k].Text).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    static string Range(int start, int count) => count == 1 ? start.ToString() : $"{start},{count}";

    static List<string> Lines(string text) =>
        text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
}

sealed class DiffResult
{
    public DiffResult(List<string> a, List<string> b, List<DiffLine> diff, Dictionary<int, List<(string Text, bool Changed)>> segments) { A = a; B = b; Diff = diff; Segments = segments; }
    public List<string> A { get; }
    public List<string> B { get; }
    public List<DiffLine> Diff { get; }
    /// <summary>Word-level pieces of changed lines that pair with a line on the other side, keyed by diff index.</summary>
    public Dictionary<int, List<(string Text, bool Changed)>> Segments { get; }
}

static class WordDiff
{
    static readonly Regex Token = new(@"\w+|\s+|.", RegexOptions.Compiled);

    /// <summary>Pairs the removed and added lines of each change block in order and diffs them word by word.</summary>
    public static Dictionary<int, List<(string Text, bool Changed)>> Pairs(List<DiffLine> diff)
    {
        var result = new Dictionary<int, List<(string Text, bool Changed)>>();
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind != '-') { i++; continue; }
            int removed = i;
            while (i < diff.Count && diff[i].Kind == '-') i++;
            int added = i;
            while (i < diff.Count && diff[i].Kind == '+') i++;
            int pairs = Math.Min(added - removed, i - added);
            for (int k = 0; k < pairs; k++)
            {
                string a = diff[removed + k].Text, b = diff[added + k].Text;
                var ta = Token.Matches(a).Cast<Match>().Select(m => m.Value).ToList();
                var tb = Token.Matches(b).Cast<Match>().Select(m => m.Value).ToList();
                if ((long)ta.Count * tb.Count > 250_000) continue;
                var words = LineDiff.Compute(ta, tb, s => s);
                // Lines with little in common read better as whole-line changes
                int same = words.Where(w => w.Kind == ' ').Sum(w => w.Text.Length);
                if (same * 3 < Math.Max(a.Length, b.Length)) continue;
                result[removed + k] = Merge(words.Where(w => w.Kind != '+').Select(w => (w.Text, w.Kind == '-')));
                result[added + k] = Merge(words.Where(w => w.Kind != '-').Select(w => (w.Text, w.Kind == '+')));
            }
        }
        return result;
    }

    static List<(string Text, bool Changed)> Merge(IEnumerable<(string Text, bool Changed)> parts)
    {
        var list = new List<(string Text, bool Changed)>();
        foreach (var p in parts)
        {
            if (list.Count > 0 && list[list.Count - 1].Changed == p.Changed) list[list.Count - 1] = (list[list.Count - 1].Text + p.Text, p.Changed);
            else list.Add(p);
        }
        return list;
    }
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
