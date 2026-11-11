using System.Diagnostics;
using System.IO;
using FastFind.Core.Api;
using FastFind.Core.Indexing;
using FastFind.Core.Native;

namespace FastFind.App.Services;

/// <summary>
/// The index as the rest of the app sees it. Behind it sits either the elevated index service (reached over its
/// named pipe) or, when that is not installed, an index built inside this process by walking folders. The source can
/// change while the app runs - the service is installed, removed or restarts - and callers never need to know.
/// </summary>
public sealed class IndexHost : IIndexService
{
    private readonly object _gate = new();
    private readonly string _userSid = PipeProtocol.CurrentUserSid();
    private IIndexService _current = new NoIndex();
    private IndexSettings _settings;
    private bool _disposed;
    private int _connecting;

    public IndexHost(IndexSettings settings) => _settings = settings;

    public event Action? VolumesChanged;
    public event Action? IndexChanged;
    /// <summary>Raised when the index moves between the service and this process.</summary>
    public event Action? SourceChanged;

    public bool IsRemote => _current.IsRemote;
    public bool IsElevated => _current.IsElevated;
    /// <summary>False only during the first moments of start-up, before a source has been chosen.</summary>
    public bool HasSource => _current is not NoIndex;
    public IReadOnlyList<VolumeInfo> Volumes => _current.Volumes;

    public Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default) => _current.SearchAsync(request, ct);
    public Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default) => _current.GetFolderSizesAsync(folder, ct);
    public void Rebuild(string? root) => _current.Rebuild(root);
    public void RefreshVolumes() => _current.RefreshVolumes();

    public void Configure(IndexSettings settings)
    {
        _settings = settings;
        _current.Configure(settings);
    }

    public bool ServiceInstalled => ServiceInstaller.IsInstalled(_userSid);
    public bool ServiceAvailable => ServiceInstaller.FindServiceExe() != null;

    /// <summary>Chooses a source in the background: the service if it is (or can be) running, otherwise an in-process index.</summary>
    public void Start() => Task.Run(() => Connect(waitForService: TimeSpan.FromSeconds(6)));

    private void Connect(TimeSpan waitForService)
    {
        if (Interlocked.Exchange(ref _connecting, 1) != 0) return;
        try
        {
            // An elevated window can read the MFT itself; there is nothing to gain from a second process.
            if (Elevation.IsElevated) { UseLocal(); return; }

            string pipe = PipeProtocol.PipeName(_userSid);
            var client = PipeIndexClient.TryConnect(pipe, 250);
            if (client == null && ServiceInstaller.IsInstalled(_userSid))
            {
                Logger.Info("Index service is installed but not running; starting it");
                ServiceInstaller.TryStart(_userSid);
                var sw = Stopwatch.StartNew();
                while (client == null && sw.Elapsed < waitForService && !_disposed)
                {
                    Thread.Sleep(250);
                    client = PipeIndexClient.TryConnect(pipe, 250);
                }
            }
            if (client != null) UseRemote(client);
            else UseLocal();
        }
        catch (Exception ex)
        {
            Logger.Error($"Index start-up failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    private void UseRemote(PipeIndexClient client)
    {
        Logger.Info($"Using the index service (pid {client.ServerProcessId}, elevated: {client.IsElevated})");
        client.Configure(_settings);
        client.Disconnected += () => OnDisconnected(client);
        Swap(client);
    }

    private void UseLocal()
    {
        if (_current is LocalIndexService) return;
        Logger.Info($"Using an in-process index (elevated: {Elevation.IsElevated})");
        var manager = new IndexManager();
        _settings.ApplyTo(manager.Options);
        manager.Options.CacheDirectory = Path.Combine(Logger.Directory, "cache");
        manager.Log += Logger.Info;
        manager.Start();
        Swap(new LocalIndexService(manager));
    }

    private void OnDisconnected(PipeIndexClient client)
    {
        if (_disposed || !ReferenceEquals(_current, client)) return;
        Logger.Warn("Lost the index service; reconnecting");
        Swap(new NoIndex());
        // A service that was restarted (or updated) comes back within a few seconds; one that was removed does not.
        Task.Run(() => Connect(waitForService: TimeSpan.FromSeconds(10)));
    }

    private void Swap(IIndexService next)
    {
        IIndexService previous;
        lock (_gate)
        {
            if (_disposed) { next.Dispose(); return; }
            previous = _current;
            previous.VolumesChanged -= OnVolumesChanged;
            previous.IndexChanged -= OnIndexChanged;
            _current = next;
            next.VolumesChanged += OnVolumesChanged;
            next.IndexChanged += OnIndexChanged;
        }
        try { previous.Dispose(); } catch (Exception ex) { Logger.Warn($"Index source dispose failed: {ex.Message}"); }
        SourceChanged?.Invoke();
        VolumesChanged?.Invoke();
        IndexChanged?.Invoke();
    }

    private void OnVolumesChanged() => VolumesChanged?.Invoke();
    private void OnIndexChanged() => IndexChanged?.Invoke();

    /// <summary>
    /// Installs the index service (one UAC prompt) and switches to it. Returns an error message, or null on success.
    /// Runs the installer elevated and waits for it, so call from a background thread.
    /// </summary>
    public string? InstallService()
    {
        string? exe = ServiceInstaller.FindServiceExe();
        if (exe == null) return $"{ServiceInstaller.ExeName} was not found next to FastFind.exe.";
        string? error = RunElevated(exe, $"install --user {_userSid}");
        if (error != null) return error;

        string pipe = PipeProtocol.PipeName(_userSid);
        var sw = Stopwatch.StartNew();
        PipeIndexClient? client = null;
        while (client == null && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            client = PipeIndexClient.TryConnect(pipe, 250);
            if (client == null) Thread.Sleep(250);
        }
        if (client == null) return "The service was installed but did not start. See service.log next to fastfind.log.";
        UseRemote(client);
        return null;
    }

    /// <summary>Removes the index service (one UAC prompt) and falls back to the in-process index.</summary>
    public string? UninstallService()
    {
        string? exe = ServiceInstaller.FindServiceExe();
        if (exe == null) return $"{ServiceInstaller.ExeName} was not found next to FastFind.exe.";
        string? error = RunElevated(exe, $"uninstall --user {_userSid}");
        if (error != null) return error;
        // Ending the task drops the pipe; OnDisconnected then finds no service and builds the in-process index.
        if (_current is not PipeIndexClient) Task.Run(() => Connect(TimeSpan.Zero));
        return null;
    }

    private static string? RunElevated(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return "Could not start the installer.";
            if (!p.WaitForExit(60_000)) return "The installer did not finish.";
            return p.ExitCode == 0 ? null : "The installer reported a failure. See service.log next to fastfind.log.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Dispose()
    {
        IIndexService current;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
        }
        try { current.Dispose(); } catch (Exception ex) { Logger.Warn(ex.Message); }
    }

    /// <summary>Stands in until a real source is ready: no volumes, empty results.</summary>
    private sealed class NoIndex : IIndexService
    {
        public bool IsRemote => false;
        public bool IsElevated => false;
        public IReadOnlyList<VolumeInfo> Volumes => Array.Empty<VolumeInfo>();
        public event Action? VolumesChanged { add { } remove { } }
        public event Action? IndexChanged { add { } remove { } }
        public Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default) => Task.FromResult<IResultSet>(EmptyResultSet.Instance);
        public Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
        public void Configure(IndexSettings settings) { }
        public void Rebuild(string? root) { }
        public void RefreshVolumes() { }
        public void Dispose() { }
    }
}

public sealed class EmptyResultSet : IResultSet
{
    public static readonly EmptyResultSet Instance = new();
    public int Count => 0;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public string? Error => null;
    public FileEntry[] GetRows(int offset, int count) => Array.Empty<FileEntry>();
    public void Dispose() { }
}
