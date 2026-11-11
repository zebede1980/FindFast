using System.IO;
using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Native;

#pragma warning disable WPF0001 // ThemeMode is marked experimental but shipped in .NET 9+.

namespace FastFind.App;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();

    /// <summary>The file index, wherever it lives (the elevated index service, or this process).</summary>
    public static IndexHost Index { get; private set; } = null!;

    /// <summary>Registered window message broadcast by a second launch to make the running instance show itself.</summary>
    public static uint ShowMessage { get; private set; }
    private static Mutex? _instanceMutex;

    /// <summary>Where a second launch leaves the folder it was asked to open, for the running instance to pick up.</summary>
    private static readonly string OpenRequestPath = Path.Combine(Logger.Directory, "open-request.txt");

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.ToList();
        // Started by an instance that is restarting itself: let it finish saving and leave before taking over from it.
        if (TakeWaitForPid(args) is int previous) WaitForExit(previous);

        Settings = Settings.Load();
        ShowMessage = RegisterWindowMessageW("FastFind.ShowWindow.v1");

        // "FastFind.exe C:\some\folder" opens that folder (or shows that file selected in its folder).
        string? openPath = args.Select(PathUtil.Normalize).FirstOrDefault(p => p.Length > 0);

        if (Settings.SingleInstance)
        {
            bool first;
            try { _instanceMutex = new Mutex(true, @"Local\FastFind.SingleInstance", out first); }
            catch (Exception) { first = false; } // created by another account's elevated instance: treat as running
            if (!first)
            {
                if (openPath != null)
                {
                    try
                    {
                        Directory.CreateDirectory(Logger.Directory);
                        File.WriteAllText(OpenRequestPath, openPath);
                    }
                    catch (Exception ex) { Logger.Warn($"Could not pass '{openPath}' to the running instance: {ex.Message}"); }
                }
                // This launch was started by the user, so it holds the right to take the foreground; pass that on so the
                // running window can actually come to the front instead of just flashing in the taskbar.
                AllowSetForegroundWindow(-1 /* ASFW_ANY */);
                PostMessageW(new IntPtr(0xFFFF) /* HWND_BROADCAST */, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }
        }
        ApplyTheme(Settings.Theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Logger.Error($"Unhandled: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => { Logger.Error($"Unobserved task: {args.Exception}"); args.SetObserved(); };

        Logger.Info("FastFind starting");
        Index = new IndexHost(IndexSettingsFrom(Settings));
        Index.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (openPath != null) window.ViewModel.OpenPath(openPath, newTab: true);

        _runtimeGuard = new RuntimeGuard();
        _runtimeGuard.Replaced += missing =>
        {
            Logger.Warn($"The .NET runtime FastFind started on has been removed ({missing} is gone); a restart is needed");
            Dispatcher.BeginInvoke(() => OfferRestart(afterError: false));
        };
        RuntimeGuard.Prepare(OfferRestart, AskToRestart, Restart);
        try { System.Runtime.InteropServices.Marshal.Prelink(typeof(App).GetMethod(nameof(MessageBoxW), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!); }
        catch (Exception) { }
        _runtimeGuard.Start();
    }

    // ---- surviving a .NET update ----
    // An update can delete the runtime this process is running on. What is already loaded keeps working; anything not
    // yet used (a right-click menu, say) then fails until FastFind is restarted. RuntimeGuard notices; this offers the restart.

    private const string WaitForArgument = "--wait-for";
    private RuntimeGuard? _runtimeGuard;
    private bool _restartPromptOpen;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Removes "--wait-for &lt;pid&gt;" from the arguments and returns the process id it named, if any.</summary>
    public static int? TakeWaitForPid(List<string> args)
    {
        int at = args.FindIndex(a => string.Equals(a, WaitForArgument, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return null;
        int? pid = at + 1 < args.Count && int.TryParse(args[at + 1], out int value) && value > 0 ? value : null;
        args.RemoveRange(at, Math.Min(2, args.Count - at));
        return pid;
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.WaitForExit(15_000);
        }
        catch (Exception) { /* already gone */ }
    }

    /// <summary>Asks whether to restart now. While the window is hidden in the tray the question waits until it is next shown.</summary>
    private void OfferRestart(bool afterError)
    {
        if (_restartPromptOpen) return;
        var window = MainWindow;
        if (!afterError && window is { IsVisible: false })
        {
            DependencyPropertyChangedEventHandler? shown = null;
            shown = (_, change) =>
            {
                if (!(bool)change.NewValue) return;
                window.IsVisibleChanged -= shown;
                Dispatcher.BeginInvoke(() => OfferRestart(afterError: false));
            };
            window.IsVisibleChanged += shown;
            return;
        }
        _restartPromptOpen = true;
        try
        {
            if (AskToRestart(window, afterError)) Restart();
        }
        finally { _restartPromptOpen = false; }
    }

    private static bool AskToRestart(Window? window, bool afterError)
    {
        IntPtr owner = IntPtr.Zero;
        try { if (window is { IsVisible: true }) owner = new System.Windows.Interop.WindowInteropHelper(window).Handle; } catch (Exception) { }
        string text = (afterError ? "That did not work because .NET was updated while FastFind was running." : ".NET was updated while FastFind was running.")
            + " Until FastFind is restarted, parts of it that have not been used yet (right-click menus, for one) will fail."
            + "\n\nRestart FastFind now? Your tabs and layout are kept."
            + "\n\nChoose No if a copy or move is still running, and restart when it has finished.";
        const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30;
        const int IDYES = 6;
        // The Windows message box directly: it needs nothing that is not already loaded.
        return MessageBoxW(owner, text, "FastFind", MB_YESNO | MB_ICONWARNING) == IDYES;
    }

    private void Restart()
    {
        Logger.Info("Restarting after a .NET runtime update");
        string? exe = Environment.ProcessPath;
        string? error = "the program path is unknown";
        if (exe == null || !RuntimeGuard.Relaunch(exe, $"{WaitForArgument} {Environment.ProcessId}", out error))
        {
            Logger.Error($"Could not restart: {error}");
            MessageBoxW(IntPtr.Zero, "FastFind could not restart itself. Please close it (Ctrl+Q) and open it again.", "FastFind", 0x30);
            return;
        }
        // The new copy waits for this one to go. Close normally so the session is saved; if that fails, go anyway.
        try
        {
            if (MainWindow is MainWindow window) window.ExitApplication();
            else Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Error($"Clean exit failed during restart: {ex.Message}");
            Environment.Exit(0);
        }
        Task.Delay(5000).ContinueWith(_ => Environment.Exit(0));
    }

    /// <summary>The part of the user's settings that decides what gets indexed.</summary>
    public static IndexSettings IndexSettingsFrom(Settings s) => new()
    {
        IncludeRemovable = s.IncludeRemovable,
        IncludeNetwork = s.IncludeNetwork,
        ExcludedRoots = s.ExcludedRoots.ToList(),
        NetworkLocations = s.NetworkLocations.ToList(),
        NetworkRescanHours = s.NetworkRescanHours,
    };

    /// <summary>The folder a second launch asked for, if any. Reading it consumes the request.</summary>
    public static string? TakeOpenRequest()
    {
        try
        {
            if (!File.Exists(OpenRequestPath)) return null;
            // Ignore a request left behind by a launch that never reached us.
            bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(OpenRequestPath) < TimeSpan.FromSeconds(30);
            string path = File.ReadAllText(OpenRequestPath).Trim();
            File.Delete(OpenRequestPath);
            return fresh && path.Length > 0 ? path : null;
        }
        catch (Exception) { return null; }
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Dark" => ThemeMode.Dark,
            "Light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };
        // Row tints are made here as frozen brushes: a brush that refers to a theme colour dynamically cannot be used in a style trigger.
        try
        {
            var accent = SystemColors.AccentColor;
            Current.Resources["RowSelectedBrush"] = Tint(accent, 0x55);
        }
        catch (Exception ex) { Logger.Warn($"Accent colour unavailable: {ex.Message}"); }
    }

    private static System.Windows.Media.SolidColorBrush Tint(System.Windows.Media.Color colour, byte alpha)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error($"UI exception: {e.Exception}");
        e.Handled = true;
        // If the runtime has been removed underneath us, that is the real cause, whatever the error says.
        if (_runtimeGuard?.MissingFile() != null)
        {
            OfferRestart(afterError: true);
            return;
        }
        // Some errors (a file that failed to load, for one) carry no message; never show an empty box.
        string message = string.IsNullOrWhiteSpace(e.Exception.Message) ? e.Exception.GetType().Name : e.Exception.Message;
        if (e.Exception is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            message += "\n\nA part of FastFind could not be loaded. Restarting FastFind usually fixes this. Details are in fastfind.log.";
        MessageBox.Show(message, "FastFind", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runtimeGuard?.Dispose();
        try { Index?.Dispose(); } catch (Exception ex) { Logger.Warn(ex.Message); }
        Settings.Save();
        base.OnExit(e);
    }
}
