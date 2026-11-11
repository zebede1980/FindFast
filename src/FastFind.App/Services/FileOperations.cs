using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastFind.App.Services;

/// <summary>
/// Copy, move, delete and create through the shell's own engine (IFileOperation): the same progress and conflict
/// dialogs as Explorer, Recycle Bin support, and a UAC prompt only when a protected location needs one.
/// Each operation runs on its own STA thread so the window stays responsive and several can run side by side.
/// </summary>
public static class FileOperations
{
    private const uint FOF_RENAMEONCOLLISION = 0x0008;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_WANTNUKEWARNING = 0x4000;
    private const uint FOFX_SHOWELEVATIONPROMPT = 0x00040000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_PRESERVEFILEEXTENSIONS = 0x00100000;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperationCoClass { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destinationFolder);
        void CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destinationFolder);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IntPtr sink);
        void PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    internal static extern IShellItem SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid);

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    internal static IShellItem ShellItem(string path)
    {
        var iid = IID_IShellItem;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
    }

    public enum Kind { Copy, Move, Recycle, DeletePermanently }

    /// <summary>What happened to an operation, reported on the UI thread.</summary>
    public sealed record Outcome(Kind Kind, int Items, bool Aborted, string? Error);

    private static IntPtr OwnerHandle()
    {
        var window = Application.Current?.MainWindow;
        return window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    /// <summary>Copies the items into <paramref name="folder"/>. Copying into the folder an item already lives in makes "name - Copy".</summary>
    public static void Copy(IReadOnlyList<string> paths, string folder, Action<Outcome>? done = null)
        => Run(Kind.Copy, paths, folder, done);

    public static void Move(IReadOnlyList<string> paths, string folder, Action<Outcome>? done = null)
        => Run(Kind.Move, paths, folder, done);

    /// <summary>Sends the items to the Recycle Bin. Windows warns before destroying anything that cannot be recycled.</summary>
    public static void Recycle(IReadOnlyList<string> paths, Action<Outcome>? done = null)
        => Run(Kind.Recycle, paths, null, done);

    /// <summary>Deletes without the Recycle Bin. The caller is responsible for having confirmed this with the user.</summary>
    public static void DeletePermanently(IReadOnlyList<string> paths, Action<Outcome>? done = null)
        => Run(Kind.DeletePermanently, paths, null, done);

    private static void Run(Kind kind, IReadOnlyList<string> paths, string? folder, Action<Outcome>? done)
    {
        if (paths.Count == 0) return;
        var items = paths.ToArray();
        IntPtr owner = OwnerHandle();
        var dispatcher = Application.Current?.Dispatcher;

        var thread = new Thread(() =>
        {
            Outcome outcome;
            try
            {
                bool aborted = Perform(kind, items, folder, owner);
                outcome = new Outcome(kind, items.Length, aborted, null);
            }
            catch (Exception ex)
            {
                // 0x80270000 (COPYENGINE_E_USER_CANCELLED) and friends: the user backed out of a dialog.
                bool cancelled = ex is COMException { HResult: unchecked((int)0x80270000) } or OperationCanceledException;
                if (!cancelled) Logger.Warn($"{kind} failed: {ex.Message}");
                outcome = new Outcome(kind, items.Length, true, cancelled ? null : ex.Message);
            }
            if (done != null) dispatcher?.BeginInvoke(() => done(outcome));
        })
        { IsBackground = true, Name = "FastFind file operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static bool Perform(Kind kind, string[] items, string? folder, IntPtr owner)
    {
        var op = (IFileOperation)new FileOperationCoClass();
        try
        {
            uint flags = FOF_NOCONFIRMMKDIR | FOFX_SHOWELEVATIONPROMPT;
            switch (kind)
            {
                case Kind.Copy:
                    flags |= FOF_ALLOWUNDO | FOFX_ADDUNDORECORD;
                    // A copy that lands beside its source needs a new name rather than an overwrite prompt.
                    if (folder != null && items.Any(p => string.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? ""),
                            Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)))
                        flags |= FOF_RENAMEONCOLLISION | FOFX_PRESERVEFILEEXTENSIONS;
                    break;
                case Kind.Move:
                    flags |= FOF_ALLOWUNDO | FOFX_ADDUNDORECORD;
                    break;
                case Kind.Recycle:
                    // The caller has already asked; NOCONFIRMATION avoids asking twice, WANTNUKEWARNING still warns if an item cannot be recycled.
                    flags |= FOF_ALLOWUNDO | FOFX_ADDUNDORECORD | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING;
                    break;
                case Kind.DeletePermanently:
                    flags |= FOF_NOCONFIRMATION;
                    break;
            }
            op.SetOperationFlags(flags);
            if (owner != IntPtr.Zero) op.SetOwnerWindow(owner);

            IShellItem? destination = folder != null ? ShellItem(folder) : null;
            int queued = 0;
            foreach (var path in items)
            {
                IShellItem item;
                try { item = ShellItem(path); }
                catch (Exception) { continue; } // vanished since it was listed
                switch (kind)
                {
                    case Kind.Copy: op.CopyItem(item, destination!, null, IntPtr.Zero); break;
                    case Kind.Move: op.MoveItem(item, destination!, null, IntPtr.Zero); break;
                    default: op.DeleteItem(item, IntPtr.Zero); break;
                }
                queued++;
            }
            if (queued == 0) return true;
            op.PerformOperations();
            op.GetAnyOperationsAborted(out bool aborted);
            return aborted;
        }
        finally
        {
            Marshal.FinalReleaseComObject(op);
        }
    }

    // ---- creating things ----

    /// <summary>A name not yet used in <paramref name="folder"/>: "New folder", "New folder (2)", ...</summary>
    public static string UniqueName(string folder, string baseName, string extension = "")
    {
        string candidate = baseName + extension;
        for (int n = 2; File.Exists(Path.Combine(folder, candidate)) || Directory.Exists(Path.Combine(folder, candidate)); n++)
            candidate = $"{baseName} ({n}){extension}";
        return candidate;
    }

    /// <summary>Creates a folder. Returns its path, or null with <paramref name="error"/> set.</summary>
    public static string? CreateFolder(string parent, string name, out string? error)
    {
        error = ValidateName(name);
        if (error != null) return null;
        string path = Path.Combine(parent, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) { error = $"'{name}' already exists."; return null; }
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Creates an empty file. Returns its path, or null with <paramref name="error"/> set.</summary>
    public static string? CreateFile(string parent, string name, out string? error)
    {
        error = ValidateName(name);
        if (error != null) return null;
        string path = Path.Combine(parent, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) { error = $"'{name}' already exists."; return null; }
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
            return path;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name cannot contain any of:  \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "A name cannot end with a dot or a space.";
        return null;
    }
}
