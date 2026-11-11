using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastFind.App.Services;

/// <summary>
/// Shows the real Windows context menu for files (or for a folder's background), so everything other programs add
/// there - Send to, 7-Zip, Git, Open with, New - is available. The menu is built by the shell; this class only hosts it.
/// </summary>
public static class ShellContextMenu
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newPidl);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint flags, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    private const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const uint GCS_VERBW = 0x4;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    private const uint FirstCommand = 1, LastCommand = 0x7FFF;

    /// <summary>
    /// Shows the shell menu for the given items at a screen position (device pixels) and runs whatever is picked.
    /// Returns the canonical verb that was chosen ("rename" is left for the caller to carry out), or null if the menu was dismissed.
    /// Only items in the same folder as the first one are included: the shell builds a menu per folder.
    /// </summary>
    public static string? ShowForItems(Window owner, IReadOnlyList<string> paths, Point screenPoint, bool extended)
    {
        if (paths.Count == 0) return null;
        string? parent = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
        var sameFolder = paths.Where(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), parent, StringComparison.OrdinalIgnoreCase)).ToList();

        var pidls = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            foreach (var path in sameFolder)
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero) pidls.Add(pidl);
            if (pidls.Count == 0) return null;

            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidls[0], ref iidFolder, out folderPtr, out _) != 0) return null;
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var children = pidls.Select(ILFindLastID).ToArray();
            var iidMenu = IID_IContextMenu;
            IntPtr hwnd = new WindowInteropHelper(owner).Handle;
            if (folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out menuPtr) != 0) return null;
            return Track(owner, menuPtr, screenPoint, extended, parent, canRename: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shell menu failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            foreach (var pidl in pidls) CoTaskMemFree(pidl);
        }
    }

    /// <summary>Shows the shell's background menu for a folder: New, Paste, Open in Terminal and so on.</summary>
    public static void ShowForFolderBackground(Window owner, string folderPath, Point screenPoint, bool extended)
    {
        IntPtr pidl = IntPtr.Zero, desktopPtr = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
            if (SHGetDesktopFolder(out desktopPtr) != 0) return;
            var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            var iidFolder = IID_IShellFolder;
            if (desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out folderPtr) != 0) return;
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var iidMenu = IID_IContextMenu;
            IntPtr hwnd = new WindowInteropHelper(owner).Handle;
            if (folder.CreateViewObject(hwnd, ref iidMenu, out menuPtr) != 0) return;
            Track(owner, menuPtr, screenPoint, extended, folderPath, canRename: false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shell background menu failed: {ex.Message}");
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (desktopPtr != IntPtr.Zero) Marshal.Release(desktopPtr);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    private static string? Track(Window owner, IntPtr menuPtr, Point screenPoint, bool extended, string? directory, bool canRename)
    {
        object menuObject = Marshal.GetObjectForIUnknown(menuPtr);
        var menu = (IContextMenu)menuObject;
        var menu2 = menuObject as IContextMenu2;
        var menu3 = menuObject as IContextMenu3;
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        IntPtr hmenu = CreatePopupMenu();
        var source = HwndSource.FromHwnd(hwnd);

        // Owner-drawn and lazily filled sub-menus (Send to, Open with, New) need the menu's window messages forwarded.
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
            {
                try
                {
                    if (menu3 != null)
                    {
                        if (menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr result) == 0) { handled = true; return result; }
                    }
                    else if (menu2 != null && msg != WM_MENUCHAR)
                    {
                        if (menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0) { handled = true; return IntPtr.Zero; }
                    }
                }
                catch (Exception) { }
            }
            return IntPtr.Zero;
        };

        try
        {
            uint flags = CMF_NORMAL | CMF_EXPLORE | (canRename ? CMF_CANRENAME : 0) | (extended ? CMF_EXTENDEDVERBS : 0);
            int hr = menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags);
            if (hr < 0) return null;

            source?.AddHook(hook);
            SetForegroundWindow(hwnd);
            uint command = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            source?.RemoveHook(hook);
            if (command < FirstCommand) return null;

            uint offset = command - FirstCommand;
            string? verb = GetVerb(menu, offset);
            if (canRename && string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase)) return "rename";

            IntPtr dirAnsi = IntPtr.Zero, dirWide = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    dirAnsi = Marshal.StringToHGlobalAnsi(directory);
                    dirWide = Marshal.StringToHGlobalUni(directory);
                }
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                            | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
                            | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                    hwnd = hwnd,
                    lpVerb = (IntPtr)offset,
                    lpVerbW = (IntPtr)offset,
                    lpDirectory = dirAnsi,
                    lpDirectoryW = dirWide,
                    nShow = 1,
                    ptInvoke = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y },
                };
                int invoked = menu.InvokeCommand(ref info);
                if (invoked < 0 && invoked != unchecked((int)0x800704C7)) // ERROR_CANCELLED
                    Logger.Warn($"Shell verb '{verb}' failed: 0x{invoked:X8}");
            }
            finally
            {
                if (dirAnsi != IntPtr.Zero) Marshal.FreeHGlobal(dirAnsi);
                if (dirWide != IntPtr.Zero) Marshal.FreeHGlobal(dirWide);
            }
            return verb;
        }
        finally
        {
            source?.RemoveHook(hook);
            DestroyMenu(hmenu);
        }
    }

    private static string? GetVerb(IContextMenu menu, uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 259) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        catch (Exception) { return null; } // some extensions crash on verbs they do not know
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
