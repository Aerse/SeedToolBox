using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeedToolBox.Services;

public static class IconHelper
{
    public static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Gets the 32px shell icon for a file, folder, shortcut or URL.</summary>
    public static ImageSource? GetIcon(string path)
    {
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        uint attributes = 0;
        string query = path;

        if (IsUrl(path))
        {
            // Use the default browser's icon
            query = ".html";
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            // Shell APIs don't accept forward slashes
            query = Path.GetFullPath(path);
        }
        else
        {
            // Missing target: fall back to the icon for its file type
            var ext = Path.GetExtension(path);
            query = ext.Length > 0 ? ext : ".exe";
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        var info = new SHFILEINFO();
        if (SHGetFileInfo(query, attributes, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
