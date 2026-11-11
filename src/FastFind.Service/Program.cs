using System.Text.Json;
using FastFind.Core.Api;
using FastFind.Core.Indexing;
using FastFind.Core.Native;

// FastFind.Service: owns the file index in an elevated background process and serves it over a named pipe, so the
// FastFind window (and anything else) can search instantly without itself running as administrator.
//
//   FastFind.Service.exe run [--user SID] [--data FOLDER]   host the index (what the scheduled task runs)
//   FastFind.Service.exe install --user SID      register + start the scheduled task (needs elevation)
//   FastFind.Service.exe uninstall --user SID    stop + remove the scheduled task (needs elevation)
//
// Exit codes: 0 ok, 1 failed, 2 already running, 3 bad arguments.

namespace FastFind.Service;

internal static class Program
{
    /// <summary>Where the log, settings and index cache live. "--data FOLDER" moves them, so a second copy can run side by side (for testing) without touching the real one's files.</summary>
    private static string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastFind");
    private static string LogPath => Path.Combine(DataDirectory, "service.log");
    private static string SettingsPath => Path.Combine(DataDirectory, "service-index.json");
    private static readonly object LogGate = new();

    private static int Main(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "run";
        string userSid = PipeProtocol.CurrentUserSid();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--user") userSid = args[i + 1];
            else if (args[i] == "--data") DataDirectory = Path.GetFullPath(args[i + 1]);
        }

        try
        {
            switch (command)
            {
                case "run":
                    try { return Run(userSid); }
                    finally
                    {
                        // By now Run has let go of its mutex and pipe, whether it ended cleanly or not, so a fresh copy can take over.
                        if (_restartRequested) RestartSelf(args);
                    }
                case "install":
                {
                    string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ServiceInstaller.ExeName);
                    string? error = ServiceInstaller.Install(exe, userSid);
                    Log(error == null ? $"Installed scheduled task for {userSid}" : $"Install failed: {error}");
                    return error == null ? 0 : 1;
                }
                case "uninstall":
                {
                    string? error = ServiceInstaller.Uninstall(userSid);
                    Log(error == null ? $"Removed scheduled task for {userSid}" : $"Uninstall failed: {error}");
                    return error == null ? 0 : 1;
                }
                default:
                    return 3;
            }
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            return 1;
        }
    }

    private static int Run(string userSid)
    {
        using var mutex = new Mutex(true, @"Global\FastFind.Service." + userSid, out bool first);
        if (!first) return 2;

        Log($"Index service starting for {userSid} (elevated: {Elevation.IsElevated}, pid {Environment.ProcessId})");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"Unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log($"Unobserved task: {e.Exception.Message}"); e.SetObserved(); };

        using var manager = new IndexManager();
        manager.Options.CacheDirectory = Path.Combine(DataDirectory, "service-cache");
        manager.Log += Log;
        LoadSettings()?.ApplyTo(manager.Options);

        var service = new LocalIndexService(manager, ownsManager: false);
        using var server = new PipeIndexServer(service, PipeProtocol.PipeName(userSid), PipeProtocol.CreateSecurity(userSid));
        server.Log += Log;
        server.Configured += SaveSettings;
        server.Start();
        manager.Start();

        using var stop = new ManualResetEventSlim();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { stop.Set(); } catch (ObjectDisposedException) { } };

        // An update can remove the .NET runtime this process started on; it then fails piecemeal. Start again on the new one.
        using var guard = new RuntimeGuard();
        guard.Replaced += missing =>
        {
            Log($"The .NET runtime this service started on has been removed ({missing} is gone); restarting on the current one");
            _restartRequested = true;
            stop.Set();
        };
        guard.Start();

        stop.Wait();
        Log("Index service stopping");
        return 0;
    }

    private static volatile bool _restartRequested;

    /// <summary>Starts a fresh copy of the service (which keeps this one's elevation) and ends this process.</summary>
    private static void RestartSelf(string[] args)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ServiceInstaller.ExeName);
        string arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        if (RuntimeGuard.Relaunch(exe, arguments, out string? error)) Log("Started the replacement service");
        else Log($"Could not start the replacement service: {error}. The FastFind window starts it again when it next needs it.");
        // Leave at once, even if something is still running: the replacement needs the way clear.
        Environment.Exit(0);
    }

    private static IndexSettings? LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<IndexSettings>(File.ReadAllText(SettingsPath)) : null;
        }
        catch (Exception ex)
        {
            Log($"Could not read {SettingsPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Remembers what the UI last asked for, so the right volumes are indexed at the next sign-in before any window opens.</summary>
    private static void SaveSettings(IndexSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"Could not save {SettingsPath}: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
