using System;
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

        var text = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (text.Length > 0) text.Append("\r\n");
            OcrWord? previous = null;
            foreach (var word in line.Words)
            {
                // The engine splits Chinese into single characters: Latin words always get a space,
                // anything next to Chinese only when there is a visible gap
                if (previous != null)
                {
                    var a = previous.BoundingRect;
                    var b = word.BoundingRect;
                    bool latin = !IsCjk(previous.Text[previous.Text.Length - 1]) && !IsCjk(word.Text[0]);
                    if (latin || b.X - (a.X + a.Width) > Math.Max(a.Height, b.Height) * 0.4) text.Append(' ');
                }
                text.Append(word.Text);
                previous = word;
            }
        }
        return text.ToString();
    }

    static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯' or >= '가' and <= '힯';
}
