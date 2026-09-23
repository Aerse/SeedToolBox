using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SeedToolBox.ScreenTools;

/// <summary>Text recognition with the OCR engine built into Windows 10/11.</summary>
static class TextRecognizer
{
    static bool? _available;

    /// <summary>False on Windows 7/8 or when no OCR language is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available == null)
            {
                try { _available = Environment.OSVersion.Version.Major >= 10 && HasLanguages(); }
                catch (Exception ex)
                {
                    Log.Error("OCR unavailable", ex);
                    _available = false;
                }
            }
            return _available.Value;
        }
    }

    // Separate method: on old Windows the WinRT types fail to load when this is compiled, not when called
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool HasLanguages() => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>Recognized text, lines separated by CRLF; empty if nothing was found.</summary>
    public static Task<string> RecognizeAsync(BitmapSource image) => Recognize(image);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<string> Recognize(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages.First());
        if (engine == null) throw new InvalidOperationException("没有可用的识别语言");

        // Small text recognizes much better enlarged; the engine rejects images over its size limit
        int max = (int)OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double scale = longest * 2 <= Math.Min(max, 1600) ? 2 : Math.Min(1, max / longest);
        BitmapSource source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        if (scale != 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));

        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = MergeLines(result.Lines);
        // Indentation is measured from the leftmost line, in average character widths
        double left = lines.Count > 0 ? lines.Min(l => l[0].BoundingRect.X) : 0;
        double charWidth = lines.Count > 0 ? lines.Average(l => l.Sum(w => w.BoundingRect.Width) / l.Sum(w => w.Text.Length)) : 1;

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length > 0) text.Append("\r\n");
            // Word boxes are tight and their heights vary with the letters, so measure gaps against the tallest
            double lineHeight = line.Max(w => w.BoundingRect.Height);
            var builder = new StringBuilder();
            int indent = (int)Math.Round((line[0].BoundingRect.X - left) / charWidth);
            if (indent >= 2) builder.Append(' ', indent);
            OcrWord? previous = null;
            foreach (var word in line)
            {
                // The engine also splits at punctuation ("Math" ".min") and between Chinese characters,
                // so a space goes in only where there is a visible gap
                if (previous != null)
                {
                    var a = previous.BoundingRect;
                    bool cjk = IsCjk(previous.Text[previous.Text.Length - 1]) || IsCjk(word.Text[0]);
                    if (word.BoundingRect.X - (a.X + a.Width) > lineHeight * (cjk ? 0.6 : 0.2)) builder.Append(' ');
                }
                builder.Append(word.Text);
                previous = word;
            }
            text.Append(ToHalfWidth(builder.ToString()));
        }
        return text.ToString();
    }

    /// <summary>
    /// The engine breaks a line at wide gaps (indentation, spaced-out code) into separate lines;
    /// lines that share most of their height are put back together, left to right.
    /// </summary>
    static List<List<OcrWord>> MergeLines(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<(double Top, double Bottom, List<OcrWord> Words)>();
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            double top = line.Words.Min(w => w.BoundingRect.Y);
            double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            int match = rows.FindIndex(r => Math.Min(r.Bottom, bottom) - Math.Max(r.Top, top) > 0.5 * Math.Min(r.Bottom - r.Top, bottom - top));
            if (match < 0)
            {
                rows.Add((top, bottom, line.Words.ToList()));
                continue;
            }
            var row = rows[match];
            row.Words.AddRange(line.Words);
            rows[match] = (Math.Min(row.Top, top), Math.Max(row.Bottom, bottom), row.Words);
        }
        return rows
            .OrderBy(r => r.Top)
            .Select(r => r.Words.OrderBy(w => w.BoundingRect.X).ToList())
            .ToList();
    }

    /// <summary>
    /// The Chinese model tends to output full-width punctuation even in English or code;
    /// lines without Chinese get ASCII punctuation back.
    /// </summary>
    static string ToHalfWidth(string line)
    {
        // Punctuation blocks aside, anything CJK means a Chinese line
        if (line.Any(c => IsCjk(c) && c is not (>= '　' and <= '〿' or >= '＀' and <= '￯'))) return line;
        var chars = line.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= '！' and <= '～') chars[i] = (char)(c - 0xFEE0);
            else if (c == '　') chars[i] = ' ';
            else if (c is '、' or '。' or '·') chars[i] = '.';
            else if (c is '“' or '”') chars[i] = '"';
            else if (c is '‘' or '’') chars[i] = '\'';
        }
        return new string(chars);
    }

    static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯' or >= '가' and <= '힯';
}
