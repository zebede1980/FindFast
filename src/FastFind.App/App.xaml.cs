using System.IO;
using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Indexing;

#pragma warning disable WPF0001 // ThemeMode is marked experimental but shipped in .NET 9+.

namespace FastFind.App;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();
    public static IndexManager IndexManager { get; private set; } = null!;

    /// <summary>Registered window message broadcast by a second launch to make the running instance show itself.</summary>
    public static uint ShowMessage { get; private set; }
    private static Mutex? _instanceMutex;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = Settings.Load();
        ShowMessage = RegisterWindowMessageW("FastFind.ShowWindow.v1");

        if (Settings.SingleInstance)
        {
            bool first;
            try { _instanceMutex = new Mutex(true, @"Local\FastFind.SingleInstance", out first); }
            catch (Exception) { first = false; } // created by another account's elevated instance: treat as running
            if (!first)
            {
                PostMessageW(new IntPtr(0xFFFF) /* HWND_BROADCAST */, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }
        }
        ApplyTheme(Settings.Theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Logger.Error($"Unhandled: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => { Logger.Error($"Unobserved task: {args.Exception}"); args.SetObserved(); };

        IndexManager = new IndexManager();
        IndexManager.Options.IncludeRemovable = Settings.IncludeRemovable;
        IndexManager.Options.IncludeNetwork = Settings.IncludeNetwork;
        foreach (var r in Settings.ExcludedRoots) IndexManager.Options.ExcludedRoots.Add(r);
        IndexManager.Options.NetworkLocations.AddRange(Settings.NetworkLocations);
        IndexManager.Options.NetworkRescanInterval = TimeSpan.FromHours(Math.Clamp(Settings.NetworkRescanHours, 0.25, 24 * 30));
        IndexManager.Options.CacheDirectory = Path.Combine(Logger.Directory, "cache");
        IndexManager.Log += Logger.Info;
        Logger.Info($"FastFind starting (elevated: {IndexManager.IsElevated})");
        IndexManager.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Dark" => ThemeMode.Dark,
            "Light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error($"UI exception: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "FastFind", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { IndexManager.Dispose(); } catch (Exception ex) { Logger.Warn(ex.Message); }
        Settings.Save();
        base.OnExit(e);
    }
}
