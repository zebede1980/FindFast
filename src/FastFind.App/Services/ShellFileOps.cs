using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FastFind.App.Services;

/// <summary>Shell file operations (recycle, move, copy) with the standard Windows progress and conflict dialogs.</summary>
public static class ShellFileOps
{
    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_MULTIDESTFILES = 0x0001, FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMMKDIR = 0x0200, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    private static string DoubleNull(IEnumerable<string> paths) => string.Join('\0', paths) + "\0\0";

    private static int Run(uint op, IReadOnlyList<string> from, string? to, ushort flags, Window? owner)
    {
        if (from.Count == 0) return 0;
        var info = new SHFILEOPSTRUCT
        {
            hwnd = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero,
            wFunc = op,
            pFrom = DoubleNull(from),
            pTo = to != null ? to + "\0\0" : null,
            fFlags = flags,
        };
        int result = SHFileOperationW(ref info);
        if (result != 0) Logger.Warn($"SHFileOperation {op} returned 0x{result:X}");
        return result;
    }

    /// <summary>Sends files to the Recycle Bin (Explorer shows its own confirmation when something cannot be recycled).</summary>
    public static int Recycle(IReadOnlyList<string> paths, Window? owner)
        => Run(FO_DELETE, paths, null, FOF_ALLOWUNDO | FOF_WANTNUKEWARNING, owner);

    public static int MoveTo(IReadOnlyList<string> paths, string folder, Window? owner)
        => Run(FO_MOVE, paths, folder, FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR, owner);

    public static int CopyTo(IReadOnlyList<string> paths, string folder, Window? owner)
        => Run(FO_COPY, paths, folder, FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR, owner);

    /// <summary>Renames in place. Returns an error message or null.</summary>
    public static string? Rename(string path, string newName)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir == null) return "Cannot rename a drive root";
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Length == 0) return "Invalid file name";
            string target = Path.Combine(dir, newName);
            if (string.Equals(path, target, StringComparison.Ordinal)) return null;
            bool caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) return $"{newName} already exists";
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
