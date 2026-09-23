using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace SeedToolBox.ScreenTools;

/// <summary>QR code (and barcode) reading and QR code generation with ZXing.</summary>
static class QrCodes
{
    /// <summary>Every code found in the image, in reading order. Slow on a whole screen: call off the UI thread.</summary>
    public static IReadOnlyList<string> Decode(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, CharacterSet = "UTF-8" },
        };
        var results = reader.DecodeMultiple(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32));
        if (results == null) return new string[0];
        return results
            .OrderBy(r => r.ResultPoints.Length > 0 ? r.ResultPoints.Min(p => p.Y) : 0)
            .ThenBy(r => r.ResultPoints.Length > 0 ? r.ResultPoints.Min(p => p.X) : 0)
            .Select(r => r.Text)
            .Distinct()
            .ToList();
    }

    /// <summary>A QR code with <paramref name="moduleSize"/> pixels per module, or null if the text is too long.</summary>
    public static BitmapSource? Encode(string text, int moduleSize)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            // Size 1 gives one pixel per module; scaled up here so edges stay sharp
            Options = new QrCodeEncodingOptions { Width = 1, Height = 1, Margin = 2, CharacterSet = "UTF-8", ErrorCorrection = ErrorCorrectionLevel.M },
        };
        ZXing.Rendering.PixelData data;
        try { data = writer.Write(text); }
        catch (WriterException) { return null; }

        int size = data.Width * moduleSize, stride = size * 4;
        var pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int from = ((y / moduleSize) * data.Width + x / moduleSize) * 4;
                int to = y * stride + x * 4;
                pixels[to] = data.Pixels[from];
                pixels[to + 1] = data.Pixels[from + 1];
                pixels[to + 2] = data.Pixels[from + 2];
                pixels[to + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
