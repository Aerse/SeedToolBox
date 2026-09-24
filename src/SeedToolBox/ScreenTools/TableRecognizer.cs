using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SeedToolBox.ScreenTools;

static partial class TextRecognizer
{
    /// <summary>Recognized text arranged in rows and columns; empty if nothing was found.</summary>
    public static async Task<List<string[]>> RecognizeTableAsync(BitmapSource image, string language = "")
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        bool windows = !string.IsNullOrEmpty(language) && WindowsAvailable;
        var pieces = await Task.Run(() => !windows && PaddleOcr.IsAvailable ? PaddleOcr.Recognize(image) : null)
            ?? await RecognizeWithWindows(image, windows ? language : "");
        return Table(pieces);
    }

    /// <summary>
    /// Splits each line into cells at wide gaps, then lines the cells up into columns by their horizontal overlap,
    /// so a cell that spans two words still lands in one column.
    /// </summary>
    static List<string[]> Table(List<(Rect Box, string Text)> pieces)
    {
        var rows = new List<List<(double Left, double Right, string Text)>>();
        foreach (var line in MergeLines(pieces))
        {
            double height = line.Max(w => w.Box.Height);
            var cells = new List<(double Left, double Right, string Text)>();
            (Rect Box, string Text)? previous = null;
            foreach (var word in line)
            {
                var text = word.Text.Trim();
                if (previous is { } p && word.Box.X - p.Box.Right <= height * 1.2 && cells.Count > 0)
                {
                    var last = cells[cells.Count - 1];
                    bool cjk = IsCjk(p.Text[p.Text.Length - 1]) || IsCjk(text[0]);
                    bool space = word.Box.X - p.Box.Right > height * (cjk ? 0.6 : 0.2);
                    cells[cells.Count - 1] = (last.Left, word.Box.Right, last.Text + (space ? " " : "") + text);
                }
                else cells.Add((word.Box.Left, word.Box.Right, text));
                previous = word;
            }
            rows.Add(cells);
        }
        if (rows.Count == 0) return new();

        // Column spans grow as cells join them; overlapping spans are merged until stable
        var columns = new List<(double Left, double Right)>();
        // A title spanning the whole table would join every column, so single-cell lines don't shape them
        var shaping = rows.Where(r => r.Count > 1).ToList();
        if (shaping.Count == 0) shaping = rows;
        foreach (var cell in shaping.SelectMany(r => r).OrderBy(c => c.Left))
        {
            int match = columns.FindIndex(c => Math.Min(c.Right, cell.Right) - Math.Max(c.Left, cell.Left) > 0);
            if (match < 0) columns.Add((cell.Left, cell.Right));
            else columns[match] = (Math.Min(columns[match].Left, cell.Left), Math.Max(columns[match].Right, cell.Right));
        }
        columns = columns.OrderBy(c => c.Left).ToList();
        for (int i = 0; i + 1 < columns.Count;)
        {
            if (columns[i + 1].Left < columns[i].Right)
            {
                columns[i] = (columns[i].Left, Math.Max(columns[i].Right, columns[i + 1].Right));
                columns.RemoveAt(i + 1);
            }
            else i++;
        }

        var table = new List<string[]>();
        foreach (var row in rows)
        {
            var values = new string[columns.Count];
            foreach (var cell in row)
            {
                double middle = (cell.Left + cell.Right) / 2;
                int index = columns.FindIndex(c => middle >= c.Left && middle <= c.Right);
                if (index < 0) index = columns.FindIndex(c => Math.Min(c.Right, cell.Right) - Math.Max(c.Left, cell.Left) > 0);
                if (index < 0) index = columns.FindLastIndex(c => c.Left <= cell.Left);
                if (index < 0) index = 0;
                values[index] = values[index] == null ? cell.Text : values[index] + " " + cell.Text;
            }
            table.Add(values.Select(v => ToHalfWidth(v ?? "")).ToArray());
        }
        return table;
    }

    /// <summary>Tab-separated text, which Excel pastes into cells.</summary>
    public static string ToTsv(IEnumerable<string[]> rows) =>
        string.Join("\r\n", rows.Select(r => string.Join("\t", r.Select(c => c.Replace('\t', ' ')))));

    public static List<string[]> FromTsv(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();

    /// <summary>The table as CF_HTML, which Word and Excel paste with borders.</summary>
    public static string ToClipboardHtml(IEnumerable<string[]> rows)
    {
        var html = new StringBuilder("<table border=\"1\" style=\"border-collapse:collapse\">");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var cell in row) html.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
            html.Append("</tr>");
        }
        html.Append("</table>");

        // Offsets in the header count UTF-8 bytes
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body><!--StartFragment-->", after = "<!--EndFragment--></body></html>";
        int headerLength = string.Format(header, 0, 0, 0, 0).Length;
        int fragment = Encoding.UTF8.GetByteCount(html.ToString());
        int startHtml = headerLength, startFragment = startHtml + before.Length, endFragment = startFragment + fragment, endHtml = endFragment + after.Length;
        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + before + html + after;
    }
}
