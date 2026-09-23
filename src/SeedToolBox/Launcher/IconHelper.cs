using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Launcher;

public static class IconHelper
{
    static readonly string[] ImageExtensions = { ".ico", ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Icon for an item: custom icon file if set, otherwise the shell icon of its target.</summary>
    public static ImageSource? GetIcon(string path, string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var custom = Environment.ExpandEnvironmentVariables(iconPath!);
            if (ImageExtensions.Contains(Path.GetExtension(custom).ToLowerInvariant()) && File.Exists(custom))
                return LoadImageFile(custom) ?? GetIcon(path);
            if (File.Exists(custom))
                return GetIcon(custom);
        }
        return GetIcon(path);
    }

    /// <summary>Gets the 48px shell icon (falls back to 32px) for a file, folder, shortcut or URL.</summary>
    public static ImageSource? GetIcon(string path)
    {
        uint flags = 0;
        uint attributes = 0;
        string query = Environment.ExpandEnvironmentVariables(path);

        if (IsUrl(path))
        {
            // Use the default browser's icon
            query = ".html";
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }
        else if (File.Exists(query) || Directory.Exists(query))
        {
            // Shell APIs don't accept forward slashes
            query = Path.GetFullPath(query);
        }
        else
        {
            // Missing target: fall back to the icon for its file type
            var ext = Path.GetExtension(query);
            query = ext.Length > 0 ? ext : ".exe";
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        return GetExtraLargeIcon(query, attributes, flags) ?? GetLargeIcon(query, attributes, flags);
    }

    static ImageSource? GetExtraLargeIcon(string query, uint attributes, uint flags)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(query, attributes, ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_SYSICONINDEX) == IntPtr.Zero)
            return null;

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) != 0 || list == null)
            return null;
        if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
            return null;
        return FromHIcon(hIcon);
    }

    static ImageSource? GetLargeIcon(string query, uint attributes, uint flags)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(query, attributes, ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero)
            return null;
        return info.hIcon == IntPtr.Zero ? null : FromHIcon(info.hIcon);
    }

    static ImageSource FromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    static ImageSource? LoadImageFile(string file)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            // .ico files hold several sizes: take the largest
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load icon {file}", ex);
            return null;
        }
    }

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_SYSICONINDEX = 0x4000;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    const int SHIL_EXTRALARGE = 2; // 48x48
    const int ILD_TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // Only the vtable slots up to GetIcon are declared
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
