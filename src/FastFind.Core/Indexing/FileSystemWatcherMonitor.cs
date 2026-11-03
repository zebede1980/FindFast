using System.Collections.Concurrent;

namespace FastFind.Core.Indexing;

/// <summary>Keeps a directory-walk index fresh using FileSystemWatcher. Events are coalesced and applied in batches.</summary>
public sealed class FileSystemWatcherMonitor : IDisposable
{
    private readonly VolumeIndex _index;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<FileSystemEventArgs> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public event Action<VolumeIndex>? Changed;
    public event Action<VolumeIndex, string>? RebuildRequired;
    public event Action<string>? Log;

    public FileSystemWatcherMonitor(VolumeIndex index)
    {
        _index = index;
        _watcher = new FileSystemWatcher(index.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                           NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.CreationTime,
        };
        _watcher.Created += (_, e) => _queue.Enqueue(e);
        _watcher.Deleted += (_, e) => _queue.Enqueue(e);
        _watcher.Renamed += (_, e) => _queue.Enqueue(e);
        _watcher.Changed += (_, e) => _queue.Enqueue(e);
        _watcher.Error += (_, e) => RebuildRequired?.Invoke(_index, $"Watcher buffer overflowed on {_index.Root}: {e.GetException().Message}");
        _thread = new Thread(Run) { IsBackground = true, Name = $"FSW {index.Root}" };
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        _thread.Start();
    }

    private void Run()
    {
        var ct = _cts.Token;
        var batch = new List<FileSystemEventArgs>();
        while (!ct.IsCancellationRequested)
        {
            if (ct.WaitHandle.WaitOne(300)) return;
            if (_queue.IsEmpty) continue;
            batch.Clear();
            while (_queue.TryDequeue(out var e) && batch.Count < 5000) batch.Add(e);
            int applied = 0;
            try { applied = Apply(batch); }
            catch (Exception ex) { Log?.Invoke($"Watcher apply failed on {_index.Root}: {ex.Message}"); }
            if (applied > 0) Changed?.Invoke(_index);
        }
    }

    private int Apply(List<FileSystemEventArgs> batch)
    {
        int applied = 0;
        _index.Lock.EnterWriteLock();
        try
        {
            foreach (var e in batch)
            {
                switch (e.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        if (Locate(e.FullPath, out int parent, out int existing) && existing < 0 && Stat(e.FullPath) is { } s)
                        {
                            _index.Add(0, Path.GetFileName(e.FullPath), parent, s.Attributes, s.Size, s.Modified, s.Created);
                            applied++;
                        }
                        break;
                    case WatcherChangeTypes.Deleted:
                        if (Locate(e.FullPath, out _, out existing) && existing >= 0)
                        {
                            _index.RemoveSubtree(existing);
                            applied++;
                        }
                        break;
                    case WatcherChangeTypes.Renamed when e is RenamedEventArgs r:
                        if (Locate(r.OldFullPath, out _, out existing) && existing >= 0 && Locate(r.FullPath, out int newParent, out _))
                        {
                            _index.Rename(existing, Path.GetFileName(r.FullPath), newParent);
                            applied++;
                        }
                        break;
                    case WatcherChangeTypes.Changed:
                        if (Locate(e.FullPath, out _, out existing) && existing >= 0 && !_index.IsDirectory(existing) && Stat(e.FullPath) is { } u)
                        {
                            _index.Update(existing, u.Size, u.Modified, u.Created, u.Attributes);
                            applied++;
                        }
                        break;
                }
            }
        }
        finally
        {
            _index.Lock.ExitWriteLock();
        }
        return applied;
    }

    private bool Locate(string fullPath, out int parent, out int existing)
    {
        existing = -1;
        string? dir = Path.GetDirectoryName(fullPath);
        parent = dir == null ? -1 : _index.FindDirectory(dir);
        if (parent < 0) return false;
        existing = _index.FindChild(parent, Path.GetFileName(fullPath));
        return true;
    }

    private static (FileAttributes Attributes, long Size, long Modified, long Created)? Stat(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) return (fi.Attributes, fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc(), fi.CreationTimeUtc.ToFileTimeUtc());
            var di = new DirectoryInfo(path);
            if (di.Exists) return (di.Attributes, 0, di.LastWriteTimeUtc.ToFileTimeUtc(), di.CreationTimeUtc.ToFileTimeUtc());
        }
        catch (Exception) { }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        if (_thread.IsAlive) _thread.Join(2000);
        _cts.Dispose();
    }
}
