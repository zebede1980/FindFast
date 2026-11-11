using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FastFind.Core.Native;

/// <summary>
/// Notices when the .NET runtime this process started on is removed underneath it. A Windows or Visual Studio update
/// installs the new runtime version and deletes the old one's folder; files the process already has open survive, but
/// anything it has not loaded yet is gone, so the first use of such a part fails with a "file not found" that makes no
/// sense to the user, and keeps failing until the process is restarted. The guard spots the removal within a minute
/// and lets the owner restart onto the new runtime (see <see cref="Relaunch"/>).
/// Everything here sticks to what is already loaded: it has to work at exactly the moment loading more is impossible.
/// </summary>
public sealed class RuntimeGuard : IDisposable
{
    /// <summary>Names one extra file to treat as part of the runtime, so the restart path can be exercised by deleting it.</summary>
    public const string TestFileVariable = "FASTFIND_RUNTIME_GUARD_FILE";

    private readonly string[] _files;
    private readonly TimeSpan _interval, _confirmAfter;
    private Timer? _timer;
    private int _misses, _raised;

    /// <summary>Raised once, on a background thread, with the path of a runtime file that has gone.</summary>
    public event Action<string>? Replaced;

    /// <param name="interval">How often to look.</param>
    /// <param name="confirmAfter">How soon to look again after a miss; only a second miss in a row counts.</param>
    /// <param name="files">The files to watch; the shared framework's by default.</param>
    public RuntimeGuard(TimeSpan? interval = null, TimeSpan? confirmAfter = null, IEnumerable<string>? files = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _confirmAfter = confirmAfter ?? TimeSpan.FromSeconds(5);
        // Only what is there now can go missing later.
        _files = (files ?? RuntimeFiles()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The shared framework's assemblies this process may load: everything on the runtime's assembly list that lives
    /// outside the application's own folder. Empty for a self-contained build, which carries its runtime with it.
    /// </summary>
    public static IEnumerable<string> RuntimeFiles()
    {
        string appFolder = AppContext.BaseDirectory;
        string list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (string path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (!path.StartsWith(appFolder, StringComparison.OrdinalIgnoreCase)) yield return path;
        string? extra = Environment.GetEnvironmentVariable(TestFileVariable);
        if (!string.IsNullOrWhiteSpace(extra)) yield return extra;
    }

    /// <summary>A runtime file that was there at start-up and is not now, or null while the runtime is whole.</summary>
    public string? MissingFile()
    {
        foreach (string file in _files)
            if (!File.Exists(file)) return file;
        return null;
    }

    public void Start()
    {
        MissingFile(); // run the check once now, while everything it needs can still be loaded
        Prepare();
        _timer ??= new Timer(_ => Check(), null, _interval, _interval);
    }

    private void Check()
    {
        try
        {
            string? missing = MissingFile();
            if (missing == null)
            {
                _misses = 0;
                return;
            }
            if (++_misses < 2)
            {
                // One miss could be an installer replacing a file in place; look again shortly before believing it.
                _timer?.Change(_confirmAfter, _interval);
                return;
            }
            if (Interlocked.Exchange(ref _raised, 1) != 0) return;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            Replaced?.Invoke(missing);
        }
        catch (Exception) { /* a failed check is simply repeated at the next tick */ }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Compiles the given methods now. A method is normally compiled on first use, and compiling it loads the assemblies
    /// it refers to: do that while they are still on disk, for anything that must run after the runtime has gone.
    /// </summary>
    public static void Prepare(params Delegate[] handlers)
    {
        foreach (var handler in handlers)
        {
            try { RuntimeHelpers.PrepareMethod(handler.Method.MethodHandle); } catch (Exception) { }
        }
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Marshal.Prelink(typeof(RuntimeGuard).GetMethod(nameof(CreateProcessW), flags)!);
            Marshal.Prelink(typeof(RuntimeGuard).GetMethod(nameof(CloseHandle), flags)!);
            RuntimeHelpers.PrepareMethod(typeof(RuntimeGuard).GetMethod(nameof(Relaunch), flags)!.MethodHandle);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Starts a program without waiting for it; used to start a fresh copy of this one, which then runs on whatever
    /// runtime is installed now. Calls Windows directly rather than going through System.Diagnostics.Process, which
    /// lives in an assembly that may not be loaded yet and, by now, cannot be.
    /// </summary>
    public static bool Relaunch(string exe, string arguments, out string? error)
    {
        error = null;
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        // CreateProcess may write to the command line, so it gets its own buffer.
        IntPtr commandLine = Marshal.StringToHGlobalUni(arguments.Length > 0 ? $"\"{exe}\" {arguments}" : $"\"{exe}\"");
        try
        {
            if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, IntPtr.Zero, ref startup, out var process))
            {
                error = $"Windows error {Marshal.GetLastWin32Error()} starting {exe}";
                return false;
            }
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, IntPtr currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
