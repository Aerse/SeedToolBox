using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>A frozen copy of the whole virtual screen in physical pixels.</summary>
public sealed class ScreenShot : IAnnotationSource
{
    /// <summary>Virtual screen origin; can be negative when a monitor sits left of/above the primary one.</summary>
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public BitmapSource Image { get; }

    readonly byte[] _pixels;
    readonly int _stride;
    readonly PixelBuffer _effects;

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
        _effects = new PixelBuffer(pixels, width, height, stride, PixelFormats.Bgr32);
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

    public BitmapSource Mosaic(int cell) => _effects.Mosaic(cell);

    public BitmapSource Blurred() => _effects.Blurred();

    /// <summary>Copies one screen rectangle (physical pixels) as top-down Bgr32 rows.</summary>
    public static byte[] CaptureRegion(Rectangle region)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var target = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(target, 0, 0, region.Width, region.Height, screen, region.X, region.Y, SRCCOPY | CAPTUREBLT))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(target);
            }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, region.Width, region.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            int stride = region.Width * 4;
            var pixels = new byte[stride * region.Height];
            for (int y = 0; y < region.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
}
