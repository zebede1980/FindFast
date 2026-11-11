using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastFind.App.Services;

/// <summary>Explorer-style actions on files.</summary>
public static class ShellOps
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Open failed for {path}: {ex.Message}"); }
    }

    /// <summary>
    /// Shows the "How do you want to open this file?" dialog via the shell "openas" verb.
    /// The old rundll32 OpenAs_RunDLL entry point silently does nothing on current Windows 11 builds.
    /// </summary>
    public static void OpenWith(string path) => InvokeVerb("openas", path);

    public static void ShowInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Explorer failed for {path}: {ex.Message}"); }
    }

    public static void OpenFolder(string directory)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Open folder failed for {directory}: {ex.Message}"); }
    }

    private const string DropEffectFormat = "Preferred DropEffect";

    /// <summary>Puts files on the clipboard the way Explorer does, so they can be pasted here or in any other program.</summary>
    public static void CopyFiles(IReadOnlyList<string> paths, bool cut = false)
    {
        if (paths.Count == 0) return;
        var data = new DataObject();
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(paths.ToArray());
        data.SetFileDropList(files);
        data.SetText(string.Join(Environment.NewLine, paths));
        // 2 = move (cut), 5 = copy | link: tells whoever pastes whether the originals should go.
        data.SetData(DropEffectFormat, new System.IO.MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        TrySetClipboard(data);
    }

    /// <summary>Files waiting on the clipboard, and whether they were cut (so pasting should move them).</summary>
    public static IReadOnlyList<string> ClipboardFiles(out bool cut)
    {
        cut = false;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return Array.Empty<string>();
            var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (Clipboard.GetData(DropEffectFormat) is System.IO.MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                stream.ReadExactly(bytes);
                cut = (BitConverter.ToInt32(bytes) & 2) != 0;
            }
            return paths;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Clipboard read failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch (Exception) { return false; }
    }

    public static void ClearClipboard()
    {
        try { Clipboard.Clear(); } catch (Exception) { }
    }

    /// <summary>Opens a terminal in the folder: Windows Terminal when installed, otherwise PowerShell.</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true, WorkingDirectory = folder, ArgumentList = { "-d", folder } });
        }
        catch (Exception)
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder }); }
            catch (Exception ex) { Logger.Warn($"Terminal failed for {folder}: {ex.Message}"); }
        }
    }

    /// <summary>Hands a location FastFind does not browse itself (Recycle Bin, This PC, shell: paths) to Explorer.</summary>
    public static void OpenInExplorer(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Explorer failed for {target}: {ex.Message}"); }
    }

    public static void CopyText(string text) => TrySetClipboard(text);

    private static void TrySetClipboard(object data)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (data is DataObject d) Clipboard.SetDataObject(d, true);
                else Clipboard.SetText((string)data);
                return;
            }
            catch (COMException) { Thread.Sleep(50); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOASYNC = 0x00000100;

    public static void ShowProperties(string path) => InvokeVerb("properties", path);

    /// <summary>Runs a shell verb on a file, owned by the main window so any dialog comes to the front.</summary>
    private static void InvokeVerb(string verb, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC,
            hwnd = OwnerHandle(),
            lpVerb = verb,
            lpFile = path,
            nShow = 5,
        };
        if (!ShellExecuteExW(ref info))
            Logger.Warn($"Shell verb '{verb}' failed for {path}: Win32 error {Marshal.GetLastWin32Error()}");
    }

    private static IntPtr OwnerHandle()
    {
        try
        {
            var window = Application.Current?.MainWindow;
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }
        catch { return IntPtr.Zero; }
    }
}
