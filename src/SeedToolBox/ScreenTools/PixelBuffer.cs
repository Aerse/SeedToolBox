using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeedToolBox.ScreenTools;

/// <summary>An image that annotations are drawn on; the mosaic and blur brushes sample its effect copies.</summary>
interface IAnnotationSource
{
    int Width { get; }
    int Height { get; }
    /// <summary>One pixel per <paramref name="cell"/>×<paramref name="cell"/> block; stretch it over PixelWidth×PixelHeight cells.</summary>
    BitmapSource Mosaic(int cell);
    /// <summary>A heavily blurred copy, possibly smaller than the image; stretch it over the full size.</summary>
    BitmapSource Blurred();
}

/// <summary>32-bit pixels (Bgr32 or Bgra32) with lazily made mosaic and blur copies.</summary>
sealed class PixelBuffer : IAnnotationSource
{
    // Either given up front or copied out of _image the first time an effect needs them
    byte[]? _pixels;
    readonly BitmapSource? _image;
    int _stride;
    PixelFormat _format;
    BitmapSource? _mosaic, _blur;
    int _mosaicCell;

    public PixelBuffer(byte[] pixels, int width, int height, int stride, PixelFormat format)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        _stride = stride;
        _format = format;
    }

    PixelBuffer(BitmapSource image)
    {
        _image = image;
        Width = image.PixelWidth;
        Height = image.PixelHeight;
    }

    /// <summary>Doesn't copy the pixels until mosaic or blur is first used: a long screenshot can be hundreds of MB.</summary>
    public static PixelBuffer From(BitmapSource image) => new(image);

    byte[] Pixels
    {
        get
        {
            if (_pixels == null)
            {
                var source = _image!.Format == PixelFormats.Bgra32 || _image.Format == PixelFormats.Bgr32 ? _image : new FormatConvertedBitmap(_image, PixelFormats.Bgra32, null, 0);
                _format = source.Format;
                _stride = Width * 4;
                _pixels = new byte[_stride * Height];
                source.CopyPixels(_pixels, _stride, 0);
            }
            return _pixels;
        }
    }

    public int Width { get; }
    public int Height { get; }

    public BitmapSource Mosaic(int cell)
    {
        if (_mosaic != null && _mosaicCell == cell) return _mosaic;
        var pixels = Pixels;
        // One pixel per cell, taken from its centre; the brush stretches it back up with nearest-neighbour scaling
        int w = (Width + cell - 1) / cell, h = (Height + cell - 1) / cell;
        var result = new byte[w * h * 4];
        for (int by = 0; by < h; by++)
        {
            int cy = Math.Min(by * cell + cell / 2, Height - 1);
            for (int bx = 0; bx < w; bx++)
            {
                int cx = Math.Min(bx * cell + cell / 2, Width - 1);
                Buffer.BlockCopy(pixels, cy * _stride + cx * 4, result, (by * w + bx) * 4, 4);
            }
        }
        _mosaic = BitmapSource.Create(w, h, 96, 96, _format, null, result, w * 4);
        _mosaic.Freeze();
        _mosaicCell = cell;
        return _mosaic;
    }

    public BitmapSource Blurred()
    {
        if (_blur != null) return _blur;
        var pixels = Pixels;
        // Shrink by averaging, then three box blurs: close to a gaussian, and cheap even for a whole desktop
        const int factor = 4, radius = 3;
        int w = Math.Max(1, (Width + factor - 1) / factor), h = Math.Max(1, (Height + factor - 1) / factor);
        var small = new float[w * h * 4];
        var counts = new int[w * h];
        for (int y = 0; y < Height; y++)
        {
            int row = y * _stride, sy = y / factor * w;
            for (int x = 0; x < Width; x++)
            {
                int s = (sy + x / factor);
                int i = row + x * 4;
                small[s * 4] += pixels[i];
                small[s * 4 + 1] += pixels[i + 1];
                small[s * 4 + 2] += pixels[i + 2];
                small[s * 4 + 3] += pixels[i + 3];
                counts[s]++;
            }
        }
        for (int s = 0; s < counts.Length; s++)
            for (int c = 0; c < 4; c++) small[s * 4 + c] /= Math.Max(1, counts[s]);

        var temp = new float[small.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(small, temp, w, h, radius, true);
            BoxBlur(temp, small, w, h, radius, false);
        }

        var bytes = new byte[w * h * 4];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Max(0, Math.Min(255, small[i] + 0.5f));
        _blur = BitmapSource.Create(w, h, 96, 96, _format, null, bytes, w * 4);
        _blur.Freeze();
        return _blur;
    }

    static void BoxBlur(float[] source, float[] target, int w, int h, int radius, bool horizontal)
    {
        int length = horizontal ? w : h, lines = horizontal ? h : w;
        int step = horizontal ? 4 : w * 4;
        float scale = 1f / (2 * radius + 1);
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                // Running sum with edge pixels repeated
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += source[start + Math.Max(0, Math.Min(length - 1, k)) * step + c];
                for (int i = 0; i < length; i++)
                {
                    target[start + i * step + c] = sum * scale;
                    int add = Math.Min(length - 1, i + radius + 1), remove = Math.Max(0, i - radius);
                    sum += source[start + add * step + c] - source[start + remove * step + c];
                }
            }
        }
    }
}
