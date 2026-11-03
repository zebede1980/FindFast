using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace FastFind.App.Services;

/// <summary>Explorer-style actions on files.</summary>
public static class ShellOps
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Open failed for {path}: {ex.Message}"); }
    }

    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Open-with failed for {path}: {ex.Message}"); }
    }

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

    public static void CopyFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var data = new DataObject();
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(paths.ToArray());
        data.SetFileDropList(files);
        data.SetText(string.Join(Environment.NewLine, paths));
        TrySetClipboard(data);
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

    public static void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        ShellExecuteExW(ref info);
    }

    public static void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Elevation cancelled or failed: {ex.Message}");
        }
    }
}
