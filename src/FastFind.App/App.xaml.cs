using System.IO;
using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Api;
using FastFind.Core.Browse;

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
        Settings = Settings.Load();
        ShowMessage = RegisterWindowMessageW("FastFind.ShowWindow.v1");

        // "FastFind.exe C:\some\folder" opens that folder (or shows that file selected in its folder).
        string? openPath = e.Args.Select(PathUtil.Normalize).FirstOrDefault(p => p.Length > 0);

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
        MessageBox.Show(e.Exception.Message, "FastFind", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Index?.Dispose(); } catch (Exception ex) { Logger.Warn(ex.Message); }
        Settings.Save();
        base.OnExit(e);
    }
}
