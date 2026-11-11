using System.Runtime.InteropServices;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace FastFind.App.Services;

/// <summary>
/// Builds the data object for dragging files out of the window. It is the shell's own object for those files,
/// the very one Explorer drags, so anything that takes a file from Explorer takes it from here: it carries the
/// file list, the shell item IDs and nothing else. A home-made object with only a file list (or worse, a file list
/// plus the paths as text) is refused or misread by some programs; Teams reports the text as a file with no data.
/// </summary>
public static class ShellDataObject
{
    /// <summary>Each file costs a shell lookup, so a huge selection is dragged as a plain file list instead.</summary>
    private const int MaxShellItems = 2000;

    public static DataObject ForDrag(IReadOnlyList<string> paths)
    {
        if (paths.Count <= MaxShellItems)
        {
            try
            {
                if (Create(paths) is { } shell) return new DataObject(shell);
            }
            catch (Exception ex) { Logger.Warn($"Shell data object failed, dragging a plain file list: {ex.Message}"); }
        }
        return new DataObject(DataFormats.FileDrop, paths.ToArray());
    }

    /// <summary>The shell's data object for the files, or null if any of them cannot be found (deleted since it was listed).</summary>
    private static ComDataObject? Create(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var pidls = new List<IntPtr>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
                pidls.Add(pidl);
            }
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out var items) != 0 || items == null) return null;
            try
            {
                Guid handler = BHID_DataObject, iid = IID_IDataObject;
                return items.BindToHandler(IntPtr.Zero, ref handler, ref iid, out var data) == 0 ? data : null;
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally
        {
            foreach (var pidl in pidls) CoTaskMemFree(pidl);
        }
    }

    private static readonly Guid BHID_DataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-c000-000000000046");

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        // Only the first method is used; the rest of the interface is left undeclared.
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out ComDataObject? result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, [In] IntPtr[] pidls, [MarshalAs(UnmanagedType.Interface)] out IShellItemArray? items);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
