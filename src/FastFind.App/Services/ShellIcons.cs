using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastFind.App.Services;

/// <summary>Small shell icons, cached per extension (per path for executables and shortcuts).</summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly ConcurrentDictionary<string, ImageSource?> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _folder;

    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "url", "cur" };

    public static ImageSource? GetIcon(string fullPath, bool isDirectory, string extension)
    {
        if (isDirectory) return _folder ??= Load("folder", FILE_ATTRIBUTE_DIRECTORY, useAttributes: true);
        if (PerFileExtensions.Contains(extension))
        {
            if (_byPath.Count > 2000) _byPath.Clear();
            return _byPath.GetOrAdd(fullPath, p => Load(p, FILE_ATTRIBUTE_NORMAL, useAttributes: false) ?? Load("file." + extension, FILE_ATTRIBUTE_NORMAL, true));
        }
        return _byExtension.GetOrAdd(extension, e => Load(e.Length == 0 ? "file" : "file." + e, FILE_ATTRIBUTE_NORMAL, useAttributes: true));
    }

    private static ImageSource? Load(string path, uint attributes, bool useAttributes)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | (useAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        IntPtr result = SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
