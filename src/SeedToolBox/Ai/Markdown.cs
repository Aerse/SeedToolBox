using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeedToolBox.Ai;

/// <summary>
/// Turns the Markdown models usually answer with into FlowDocument blocks: headings, paragraphs, lists, quotes,
/// code blocks, tables, rules and the common inline marks. Anything unusual stays as plain text.
/// </summary>
static class Markdown
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    static readonly Regex Ordered = new(@"^(\s*)(\d+)[.)]\s+(.*)$");
    static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$");
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
    static readonly Regex Inline = new(
        @"(?<code>`+)(?<codetext>.+?)\k<code>|\*\*(?<bold>.+?)\*\*|__(?<bold2>.+?)__|~~(?<strike>.+?)~~|\[(?<link>[^\]]+)\]\((?<url>[^)\s]+)\)|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<url2>https?://[^\s<>()（）]+)");

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static IEnumerable<Block> Render(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        var blocks = new List<Block>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                AddInlines(p.Inlines, paragraph[i].Trim());
            }
            blocks.Add(p);
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                var fence = trimmed.Substring(0, 3);
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence); i++) code.Add(lines[i]);
                blocks.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            Match m;
            if ((m = Heading.Match(line)).Success)
            {
                FlushParagraph();
                int level = m.Groups[1].Length;
                var h = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = level switch { 1 => 20, 2 => 18, 3 => 16, _ => 15 }, Margin = new Thickness(0, 6, 0, 6) };
                AddInlines(h.Inlines, m.Groups[2].Value);
                blocks.Add(h);
                continue;
            }
            if (Rule.IsMatch(line) && paragraph.Count == 0)
            {
                blocks.Add(new Paragraph { BorderBrush = Res("CardBorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 10), FontSize = 2 });
                continue;
            }
            if (trimmed.StartsWith(">"))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith(">"); i++) quote.Add(lines[i].TrimStart().Substring(1).TrimStart());
                i--;
                var section = new Section { BorderBrush = Res("ControlBorderBrush"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Foreground = Res("SecondaryTextBrush"), Margin = new Thickness(0, 0, 0, 8) };
                section.Blocks.AddRange(Render(string.Join("\n", quote)).ToList());
                blocks.Add(section);
                continue;
            }
            if (Bullet.IsMatch(line) || Ordered.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(ListBlock(lines, ref i));
                continue;
            }
            if (line.Contains("|") && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Length && lines[i].Contains("|") && lines[i].Trim().Length > 0; i++) rows.Add(lines[i]);
                i--;
                blocks.Add(TableBlock(rows));
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        return blocks;
    }

    static Block CodeBlock(string code) => new Paragraph(new Run(code))
    {
        FontFamily = Mono,
        FontSize = 13,
        Background = Res("HoverBrush"),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>A run of list items starting at line i; deeper indentation makes nested lists.</summary>
    static List ListBlock(string[] lines, ref int i)
    {
        int Indent(string l) => l.Length - l.TrimStart().Length;
        bool IsItem(string l) => Bullet.IsMatch(l) || Ordered.IsMatch(l);
        int indent = Indent(lines[i]);
        var first = Ordered.Match(lines[i]);
        var list = new List
        {
            MarkerStyle = first.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(22, 0, 0, 0),
        };
        if (first.Success && int.TryParse(first.Groups[2].Value, out var start) && start > 1) list.StartIndex = start;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                // A blank line ends the list unless another item follows
                if (i + 1 < lines.Length && IsItem(lines[i + 1]) && Indent(lines[i + 1]) >= indent) continue;
                break;
            }
            if (IsItem(line) && Indent(line) > indent && list.ListItems.LastListItem is { } parent)
            {
                parent.Blocks.Add(ListBlock(lines, ref i));
                continue;
            }
            // A different kind of item at the same level starts a new list
            if (!IsItem(line) || Indent(line) < indent || Ordered.IsMatch(line) != first.Success)
            {
                // A wrapped line belongs to the item above
                if (!IsItem(line) && Indent(line) > indent && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
                {
                    last.Inlines.Add(new LineBreak());
                    AddInlines(last.Inlines, line.Trim());
                    continue;
                }
                break;
            }
            var m = Bullet.Match(line);
            var text = m.Success ? m.Groups[2].Value : Ordered.Match(line).Groups[3].Value;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            AddInlines(p.Inlines, text);
            list.ListItems.Add(new ListItem(p));
        }
        // Leave i on the last line used, like the other blocks, so the caller's loop moves on to the next one
        i--;
        return list;
    }

    static Block TableBlock(List<string> rows)
    {
        string[] Cells(string row)
        {
            var t = row.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(c => c.Trim()).ToArray();
        }
        var cells = rows.Select(Cells).ToList();
        int columns = cells.Max(c => c.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderBrush = Res("CardBorderBrush"), BorderThickness = new Thickness(1, 1, 0, 0) };
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < cells.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) { row.FontWeight = FontWeights.SemiBold; row.Background = Res("HoverBrush"); }
            for (int c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, c < cells[r].Length ? cells[r][c] : "");
                row.Cells.Add(new TableCell(p) { Padding = new Thickness(6, 3, 6, 3), BorderBrush = Res("CardBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    static void AddInlines(InlineCollection inlines, string text)
    {
        int at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text.Substring(at, m.Index - at)));
            at = m.Index + m.Length;
            if (m.Groups["codetext"].Success)
                inlines.Add(new Run(m.Groups["codetext"].Value.Trim()) { FontFamily = Mono, Background = Res("HoverBrush") });
            else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value);
                inlines.Add(bold);
            }
            else if (m.Groups["strike"].Success)
                inlines.Add(new Run(m.Groups["strike"].Value) { TextDecorations = TextDecorations.Strikethrough });
            else if (m.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups["italic"].Value);
                inlines.Add(italic);
            }
            else
            {
                var url = m.Groups["url"].Success ? m.Groups["url"].Value : m.Groups["url2"].Value;
                var label = m.Groups["link"].Success ? m.Groups["link"].Value : url;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    var link = new Hyperlink(new Run(label)) { Foreground = Res("AccentBrush"), ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
                    link.Click += (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception) { }
                    };
                    inlines.Add(link);
                }
                else inlines.Add(new Run(m.Value));
            }
        }
        if (at < text.Length) inlines.Add(new Run(text.Substring(at)));
    }
}
