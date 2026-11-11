using FastFind.Core.Mft;
using FastFind.Core.Native;
using FastFind.Core.Usn;

namespace FastFind.Core.Indexing;

public enum VolumeStatus { Pending, Indexing, Ready, Failed, Removed }

public sealed class VolumeState
{
    public required string Root { get; init; }
    public string Label { get; set; } = "";
    public string Format { get; set; } = "";
    public DriveType DriveType { get; set; }
    public VolumeStatus Status { get; internal set; }
    public IndexMode Mode { get; internal set; }
    public VolumeIndex? Index { get; internal set; }
    public string? Error { get; internal set; }
    public string ProgressText { get; internal set; } = "";
    public long Entries => Index?.LiveCount ?? 0;
    public TimeSpan BuildTime { get; internal set; }
    internal IDisposable? Watcher { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    public bool LiveUpdates => Watcher != null;
    /// <summary>Mapped network drives and UNC shares: scanned on a schedule rather than watched.</summary>
    public bool IsNetwork { get; init; }
    public DateTime? IndexedAtUtc => Index?.BuiltAtUtc;
    public DateTime? NextScanUtc { get; internal set; }
    public bool IsDriveRoot => Root.Length == 3 && Root[1] == ':';
}

public sealed class IndexOptions
{
    public bool IncludeRemovable { get; set; }
    /// <summary>Index mapped network drives (scheduled scans, no live watching).</summary>
    public bool IncludeNetwork { get; set; }
    /// <summary>UNC paths or drive roots to index as network locations on the schedule.</summary>
    public List<string> NetworkLocations { get; } = new();
    /// <summary>How often network locations are rescanned.</summary>
    public TimeSpan NetworkRescanInterval { get; set; } = TimeSpan.FromHours(6);
    /// <summary>Drive roots (e.g. "D:\") never to index.</summary>
    public HashSet<string> ExcludedRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When true the MFT reader is skipped even if elevated (diagnostics).</summary>
    public bool ForceDirectoryWalk { get; set; }
    /// <summary>Folder for directory-walk index snapshots; null disables caching.</summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>
/// Owns one <see cref="VolumeIndex"/> per volume, discovers volumes as they appear (including BitLocker drives
/// unlocked after start-up), picks the fastest indexing strategy available and keeps the indexes live.
/// Network locations are scanned on a schedule and served from cache in between.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly Dictionary<string, VolumeState> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _pollTimer;
    private readonly Timer _changeTimer;
    private readonly Timer _scheduleTimer;
    private readonly Dictionary<string, Timer> _nameOrderTimers = new(StringComparer.OrdinalIgnoreCase);
    private int _changePending;
    private bool _refreshing;

    public IndexOptions Options { get; } = new();
    public bool IsElevated => Elevation.IsElevated;

    /// <summary>Raised on a thread-pool thread whenever the set of volumes or their status changes.</summary>
    public event Action? VolumesChanged;

    /// <summary>Raised (throttled) after live updates modify any index. Consumers should re-run their search.</summary>
    public event Action? IndexChanged;

    public event Action<string>? Log;

    public IndexManager()
    {
        _pollTimer = new Timer(_ => _ = RefreshVolumesAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _changeTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _changePending, 0) != 0) IndexChanged?.Invoke();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _scheduleTimer = new Timer(_ => RunScheduledScans(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<VolumeState> Volumes
    {
        get { lock (_gate) return _volumes.Values.OrderBy(v => v.Root).ToList(); }
    }

    /// <summary>Indexes that are ready to be searched.</summary>
    public IReadOnlyList<VolumeIndex> ReadyIndexes
    {
        get { lock (_gate) return _volumes.Values.Where(v => v.Status == VolumeStatus.Ready && v.Index != null).OrderBy(v => v.Root).Select(v => v.Index!).ToList(); }
    }

    public long TotalEntries
    {
        get { lock (_gate) return _volumes.Values.Sum(v => v.Entries); }
    }

    /// <summary>Starts discovery and keeps polling so drives that become available later (e.g. BitLocker unlock) get indexed.</summary>
    public void Start(TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        _pollTimer.Change(TimeSpan.Zero, interval);
        _scheduleTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Re-scans the drive list and configured network locations. Safe to call at any time.</summary>
    public async Task RefreshVolumesAsync()
    {
        lock (_gate)
        {
            if (_refreshing) return;
            _refreshing = true;
        }
        try
        {
            var present = new List<VolumeState>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue; // locked BitLocker volumes and empty card readers report not ready
                    bool wanted = d.DriveType switch
                    {
                        DriveType.Fixed => true,
                        DriveType.Removable => Options.IncludeRemovable,
                        DriveType.Network => Options.IncludeNetwork,
                        _ => false,
                    };
                    if (!wanted || Options.ExcludedRoots.Contains(d.Name)) continue;
                    string format = d.DriveFormat; // throws for volumes that are still locked
                    present.Add(new VolumeState
                    {
                        Root = d.Name, Label = SafeLabel(d), Format = format, DriveType = d.DriveType,
                        Status = VolumeStatus.Pending, IsNetwork = d.DriveType == DriveType.Network,
                    });
                }
                catch (Exception) { /* not accessible yet */ }
            }

            // Configured network locations (UNC paths). Reachability checks can block, so run them off-thread with a timeout.
            foreach (var location in Options.NetworkLocations.ToArray())
            {
                string root = NormalizeRoot(location);
                if (root.Length == 0 || present.Any(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) continue;
                bool reachable = await Task.Run(() =>
                {
                    var probe = Task.Run(() => Directory.Exists(root));
                    return probe.Wait(TimeSpan.FromSeconds(4)) && probe.Result;
                });
                if (!reachable) continue;
                present.Add(new VolumeState { Root = root, Label = root.TrimEnd('\\'), Format = "Network", DriveType = DriveType.Network, Status = VolumeStatus.Pending, IsNetwork = true });
            }

            var toStart = new List<VolumeState>();
            bool changed = false;
            lock (_gate)
            {
                foreach (var candidate in present)
                {
                    if (_volumes.ContainsKey(candidate.Root)) continue;
                    _volumes[candidate.Root] = candidate;
                    toStart.Add(candidate);
                    changed = true;
                }
                foreach (var root in _volumes.Keys.ToList())
                {
                    if (present.Any(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase))) continue;
                    var vs = _volumes[root];
                    _volumes.Remove(root);
                    Teardown(vs);
                    vs.Status = VolumeStatus.Removed;
                    Log?.Invoke($"Volume {root} removed");
                    changed = true;
                }
            }
            if (changed) VolumesChanged?.Invoke();
            foreach (var vs in toStart) _ = IndexVolumeAsync(vs, force: false);
        }
        finally
        {
            lock (_gate) _refreshing = false;
        }
    }

    public static string NormalizeRoot(string path)
    {
        path = path.Trim();
        if (path.Length == 0) return "";
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!path.EndsWith('\\')) path += "\\";
        return path;
    }

    /// <summary>Rebuilds one volume now (for network locations this bypasses the schedule).</summary>
    public Task RebuildAsync(string root)
    {
        VolumeState? vs;
        lock (_gate) _volumes.TryGetValue(root, out vs);
        if (vs == null) return Task.CompletedTask;
        Teardown(vs);
        vs.Index = null;
        return IndexVolumeAsync(vs, force: true);
    }

    public Task RebuildAllAsync() => Task.WhenAll(Volumes.Select(v => RebuildAsync(v.Root)));

    /// <summary>Called every minute: rescans network locations whose index is older than the configured interval.</summary>
    private void RunScheduledScans()
    {
        foreach (var vs in Volumes)
        {
            if (!vs.IsNetwork || vs.Status != VolumeStatus.Ready || vs.Index == null) continue;
            if (DateTime.UtcNow - vs.Index.BuiltAtUtc >= Options.NetworkRescanInterval)
            {
                Log?.Invoke($"Scheduled rescan of {vs.Root}");
                _ = RebuildAsync(vs.Root);
            }
        }
    }

    private async Task IndexVolumeAsync(VolumeState vs, bool force)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        vs.Cts = cts;
        var ct = cts.Token;
        vs.Status = VolumeStatus.Indexing;
        vs.Error = null;
        vs.ProgressText = "Starting";
        VolumesChanged?.Invoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = DateTime.MinValue;
        var progress = new Progress<IndexProgress>(p =>
        {
            vs.ProgressText = p.Total > 0 ? $"{p.Phase} {p.Processed * 100 / Math.Max(1, p.Total)}%" : $"{p.Phase} {p.Processed:N0}";
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200) { lastReport = DateTime.UtcNow; VolumesChanged?.Invoke(); }
        });

        try
        {
            VolumeIndex? index = null;
            bool ntfs = string.Equals(vs.Format, "NTFS", StringComparison.OrdinalIgnoreCase);
            if (ntfs && vs.IsDriveRoot && !vs.IsNetwork && Elevation.IsElevated && !Options.ForceDirectoryWalk)
            {
                try
                {
                    var position = UsnJournalWatcher.CapturePosition(vs.Root[0]);
                    index = await Task.Run(() => (VolumeIndex)MftReader.Read(vs.Root, progress, ct), ct);
                    var watcher = UsnJournalWatcher.Start(index, position.JournalId, position.NextUsn);
                    HookWatcher(watcher, vs);
                    vs.Watcher = watcher;
                    vs.Mode = IndexMode.Mft;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke($"MFT read failed on {vs.Root}, falling back to folder scan: {ex.Message}");
                    index = null;
                }
            }
            if (index == null)
            {
                // Serve the cached snapshot straight away (if any), then rescan in the background and swap it in.
                string? cacheDir = Options.CacheDirectory;
                bool skipRescan = false;
                if (cacheDir != null && vs.Index == null)
                {
                    var cached = await Task.Run(() => IndexCache.TryLoad(cacheDir, vs.Root, TimeSpan.FromDays(30)), ct);
                    if (cached != null)
                    {
                        if (!vs.IsNetwork) AttachWalkWatcher(cached, vs);
                        _ = Task.Run(() => { try { cached.UpdateFolderSizes(); } catch (Exception) { } });
                        cached.Lock.EnterReadLock();
                        try { cached.EnsureNameOrder(); } finally { cached.Lock.ExitReadLock(); }
                        vs.Index = cached;
                        vs.Mode = IndexMode.DirectoryWalk;
                        vs.Status = VolumeStatus.Ready;
                        // Network locations are only rescanned when the cache is older than the schedule interval.
                        skipRescan = vs.IsNetwork && !force && DateTime.UtcNow - cached.BuiltAtUtc < Options.NetworkRescanInterval;
                        vs.ProgressText = skipRescan ? "" : "Cached; refreshing";
                        Log?.Invoke($"{vs.Root}: loaded {cached.LiveCount:N0} cached entries in {sw.Elapsed.TotalSeconds:F1}s" + (skipRescan ? " (next scan on schedule)" : "; rescanning in background"));
                        VolumesChanged?.Invoke();
                        IndexChanged?.Invoke();
                    }
                }
                if (skipRescan)
                {
                    vs.NextScanUtc = vs.Index!.BuiltAtUtc + Options.NetworkRescanInterval;
                    VolumesChanged?.Invoke();
                    return;
                }

                index = await Task.Run(() => (VolumeIndex)DirectoryWalkIndexer.Build(vs.Root, progress, ct), ct);
                var previousWatcher = vs.Watcher;
                if (!vs.IsNetwork) AttachWalkWatcher(index, vs);
                previousWatcher?.Dispose();
                vs.Mode = IndexMode.DirectoryWalk;
                if (cacheDir != null)
                {
                    var toSave = index;
                    _ = Task.Run(() =>
                    {
                        try { IndexCache.Save(toSave, cacheDir); }
                        catch (Exception ex) { Log?.Invoke($"Cache save failed for {vs.Root}: {ex.Message}"); }
                    });
                }
            }

            // Pre-sort names so the first search is instant.
            index.Lock.EnterReadLock();
            try { index.EnsureNameOrder(); } finally { index.Lock.ExitReadLock(); }

            // Folder totals are derived data: compute them off the critical path so the volume is searchable straight away.
            var sized = index;
            _ = Task.Run(() => { try { sized.UpdateFolderSizes(); } catch (Exception) { } });

            bool swapped = vs.Index != null && !ReferenceEquals(vs.Index, index);
            vs.Index = index;
            vs.BuildTime = sw.Elapsed;
            vs.Status = VolumeStatus.Ready;
            vs.ProgressText = "";
            vs.NextScanUtc = vs.IsNetwork ? index.BuiltAtUtc + Options.NetworkRescanInterval : null;
            Log?.Invoke($"{vs.Root} indexed: {index.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s via {vs.Mode}");
            if (swapped)
            {
                // The previous index and the scan's scratch lists are large-object-heap allocations; hand them back promptly.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var gcInfo = GC.GetGCMemoryInfo();
                Log?.Invoke($"Memory after swap: managed heap {gcInfo.HeapSizeBytes / (1024 * 1024)} MB (fragmented {gcInfo.FragmentedBytes / (1024 * 1024)} MB), working set {Environment.WorkingSet / (1024 * 1024)} MB");
            }
        }
        catch (OperationCanceledException)
        {
            vs.Status = VolumeStatus.Pending;
        }
        catch (Exception ex)
        {
            vs.Status = VolumeStatus.Failed;
            vs.Error = ex.Message;
            Log?.Invoke($"Indexing {vs.Root} failed: {ex.Message}");
        }
        VolumesChanged?.Invoke();
        IndexChanged?.Invoke();
    }

    private void HookWatcher(UsnJournalWatcher watcher, VolumeState vs)
    {
        watcher.Changed += OnIndexMutated;
        watcher.RebuildRequired += (vol, reason) => { Log?.Invoke(reason); _ = RebuildAsync(vs.Root); };
        watcher.Log += m => Log?.Invoke(m);
    }

    private void HookWatcher(FileSystemWatcherMonitor monitor, VolumeState vs)
    {
        monitor.Changed += OnIndexMutated;
        monitor.RebuildRequired += (vol, reason) => { Log?.Invoke(reason); _ = RebuildAsync(vs.Root); };
        monitor.Log += m => Log?.Invoke(m);
    }

    private void AttachWalkWatcher(VolumeIndex index, VolumeState vs)
    {
        index.Lock.EnterWriteLock();
        try { index.EnableChildLookup(); } finally { index.Lock.ExitWriteLock(); }
        var monitor = new FileSystemWatcherMonitor(index);
        HookWatcher(monitor, vs);
        try { monitor.Start(); vs.Watcher = monitor; }
        catch (Exception ex) { Log?.Invoke($"Cannot watch {vs.Root} for changes: {ex.Message}"); monitor.Dispose(); vs.Watcher = null; }
    }

    /// <summary>Writes every directory-walk index to the cache directory (called on exit and periodically).</summary>
    public void SaveCaches()
    {
        string? cacheDir = Options.CacheDirectory;
        if (cacheDir == null) return;
        foreach (var vs in Volumes)
        {
            if (vs.Status != VolumeStatus.Ready || vs.Mode != IndexMode.DirectoryWalk || vs.Index == null) continue;
            try { IndexCache.Save(vs.Index, cacheDir); }
            catch (Exception ex) { Log?.Invoke($"Cache save failed for {vs.Root}: {ex.Message}"); }
        }
    }

    private void OnIndexMutated(VolumeIndex index)
    {
        Interlocked.Exchange(ref _changePending, 1);
        _changeTimer.Change(250, Timeout.Infinite);

        // Rebuild the sorted name order in the background once changes settle, so searches stay instant.
        // The timer resolves the volume's *current* index each time so a swapped-out index is not kept alive.
        string root = index.Root;
        lock (_nameOrderTimers)
        {
            if (!_nameOrderTimers.TryGetValue(root, out var t))
            {
                t = new Timer(_ =>
                {
                    VolumeState? vs;
                    lock (_gate) _volumes.TryGetValue(root, out vs);
                    var current = vs?.Index;
                    if (current == null) return;
                    try
                    {
                        current.Lock.EnterReadLock();
                        try { current.EnsureNameOrder(); } finally { current.Lock.ExitReadLock(); }
                        current.UpdateFolderSizes();
                    }
                    catch (Exception) { }
                }, null, Timeout.Infinite, Timeout.Infinite);
                _nameOrderTimers[root] = t;
            }
            t.Change(1000, Timeout.Infinite);
        }
    }

    private static void Teardown(VolumeState vs)
    {
        try { vs.Cts?.Cancel(); } catch (Exception) { }
        try { vs.Watcher?.Dispose(); } catch (Exception) { }
        vs.Watcher = null;
        vs.Index = null;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch (Exception) { return ""; }
    }

    public void Dispose()
    {
        try { SaveCaches(); } catch (Exception) { }
        _cts.Cancel();
        _pollTimer.Dispose();
        _changeTimer.Dispose();
        _scheduleTimer.Dispose();
        lock (_nameOrderTimers) foreach (var t in _nameOrderTimers.Values) t.Dispose();
        lock (_gate) foreach (var vs in _volumes.Values) Teardown(vs);
        _cts.Dispose();
    }
}
