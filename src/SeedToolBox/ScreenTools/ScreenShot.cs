using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>A frozen copy of the whole virtual screen in physical pixels.</summary>
public sealed class ScreenShot
{
    /// <summary>Virtual screen origin; can be negative when a monitor sits left of/above the primary one.</summary>
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public BitmapSource Image { get; }

    readonly byte[] _pixels;
    readonly int _stride;
    BitmapSource? _mosaic;

    ScreenShot(int x, int y, int width, int height, byte[] pixels, int stride)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        _pixels = pixels;
        _stride = stride;
        Image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        Image.Freeze();
    }

    public static ScreenShot Capture()
    {
        // Physical pixels, since the app is per-monitor DPI aware
        var bounds = WinForms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var target = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                // CAPTUREBLT includes layered (translucent) windows; Graphics.CopyFromScreen rejects the flag
                if (!BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(target);
            }
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var pixels = new byte[data.Stride * bounds.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new ScreenShot(bounds.X, bounds.Y, bounds.Width, bounds.Height, pixels, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Pixel color at image coordinates (not screen coordinates).</summary>
    public System.Windows.Media.Color GetColor(int x, int y)
    {
        int i = y * _stride + x * 4;
        return System.Windows.Media.Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    /// <summary>The screenshot pixelated into blocks, used by the mosaic brush.</summary>
    public BitmapSource Mosaic(int cell)
    {
        if (_mosaic != null) return _mosaic;

        var result = new byte[_pixels.Length];
        for (int by = 0; by < Height; by += cell)
        {
            int cy = System.Math.Min(by + cell / 2, Height - 1);
            for (int bx = 0; bx < Width; bx += cell)
            {
                int cx = System.Math.Min(bx + cell / 2, Width - 1);
                int src = cy * _stride + cx * 4;
                for (int y = by; y < System.Math.Min(by + cell, Height); y++)
                {
                    for (int x = bx; x < System.Math.Min(bx + cell, Width); x++)
                    {
                        int dst = y * _stride + x * 4;
                        result[dst] = _pixels[src];
                        result[dst + 1] = _pixels[src + 1];
                        result[dst + 2] = _pixels[src + 2];
                    }
                }
            }
        }
        _mosaic = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, result, _stride);
        _mosaic.Freeze();
        return _mosaic;
    }

    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
}
