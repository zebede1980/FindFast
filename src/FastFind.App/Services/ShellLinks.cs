using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FastFind.App.Services;

/// <summary>Reads Windows shortcuts so a shortcut to a folder can be browsed in place instead of being handed to Explorer.</summary>
public static class ShellLinks
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        // The remaining methods are never called; only GetPath's slot matters.
    }

    /// <summary>The folder a .lnk shortcut points at, or null if the file is not a shortcut to an existing folder.</summary>
    public static string? FolderTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);
            var buffer = new StringBuilder(1024);
            if (((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) != 0) return null;
            string target = buffer.ToString();
            return target.Length > 0 && Directory.Exists(target) ? target : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }
}
