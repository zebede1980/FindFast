using FastFind.Core.Indexing;
using FastFind.Core.Native;
using FastFind.Core.Search;

namespace FastFind.Core.Api;

/// <summary>Rows of a <see cref="SearchResult"/> read straight from the in-memory index.</summary>
public sealed class LocalResultSet : IResultSet
{
    private readonly SearchResult _result;

    public LocalResultSet(SearchResult result) => _result = result;

    public SearchResult Result => _result;
    public int Count => _result.Count;
    public TimeSpan Elapsed => _result.Elapsed;
    public string? Error => _result.Error;

    public FileEntry[] GetRows(int offset, int count)
    {
        if (offset < 0) offset = 0;
        count = Math.Max(0, Math.Min(count, _result.Count - offset));
        var rows = new FileEntry[count];
        if (count == 0) return rows;
        var vols = _result.Volumes;
        foreach (var v in vols) v.Lock.EnterReadLock();
        try
        {
            for (int n = 0; n < count; n++)
            {
                var hit = _result.Hits[offset + n];
                var vol = vols[hit.Volume];
                string? name = vol.GetName(hit.Index);
                if (name == null) { rows[n] = new FileEntry(null, "", 0, 0, 0, 0); continue; }
                ref var m = ref vol.GetMeta(hit.Index);
                rows[n] = new FileEntry(name, DisplayDirectory(vol.GetParentPath(hit.Index)), m.Size, m.ModifiedFileTime, m.CreatedFileTime, m.Attributes);
            }
        }
        finally
        {
            foreach (var v in vols) v.Lock.ExitReadLock();
        }
        return rows;
    }

    /// <summary>"C:\" stays as it is; any other folder loses a trailing backslash so paths read the same whichever index produced them.</summary>
    public static string DisplayDirectory(string dir)
        => dir.Length > 3 && dir[^1] == '\\' ? dir.TrimEnd('\\') : dir;

    public void Dispose() { }
}

/// <summary>The index hosted in this process: an <see cref="IndexManager"/> plus the search engine.</summary>
public sealed class LocalIndexService : IIndexService
{
    private readonly bool _ownsManager;

    public IndexManager Manager { get; }

    public LocalIndexService(IndexManager manager, bool ownsManager = true)
    {
        Manager = manager;
        _ownsManager = ownsManager;
        manager.VolumesChanged += () => VolumesChanged?.Invoke();
        manager.IndexChanged += () => IndexChanged?.Invoke();
    }

    public bool IsRemote => false;
    public bool IsElevated => Elevation.IsElevated;
    public IReadOnlyList<VolumeInfo> Volumes => Manager.Volumes.Select(VolumeInfo.From).ToList();
    public event Action? VolumesChanged;
    public event Action? IndexChanged;

    public Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var volumes = Manager.ReadyIndexes;
        return Task.Run<IResultSet>(() =>
        {
            var query = request.ToQuery();
            return new LocalResultSet(SearchEngine.Search(volumes, query, request.Sort, request.Descending, ct, request.FoldersFirst));
        }, ct);
    }

    public Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default)
    {
        var volumes = Manager.ReadyIndexes;
        return Task.Run<IReadOnlyDictionary<string, long>>(() => FolderSizes(volumes, folder), ct);
    }

    /// <summary>Aggregated size of each sub-folder of <paramref name="folder"/>, from whichever index covers it.</summary>
    public static Dictionary<string, long> FolderSizes(IReadOnlyList<VolumeIndex> volumes, string folder)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Longest root first so a nested network location wins over the drive that contains it.
        foreach (var volume in volumes.OrderByDescending(v => v.Root.Length))
        {
            string root = volume.Root.TrimEnd('\\');
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (folder.Length > root.Length && folder[root.Length] != '\\') continue;
            if (!volume.FolderSizesCurrent) volume.UpdateFolderSizes();
            volume.Lock.EnterReadLock();
            try
            {
                int dir = volume.FindDirectory(folder.Length <= root.Length ? volume.Root : folder);
                if (dir >= 0) volume.GetChildDirectorySizes(dir, sizes);
            }
            finally
            {
                volume.Lock.ExitReadLock();
            }
            break;
        }
        return sizes;
    }

    public void Configure(IndexSettings settings)
    {
        settings.ApplyTo(Manager.Options);
        _ = Manager.RefreshVolumesAsync();
    }

    public void Rebuild(string? root)
    {
        if (root == null) _ = Manager.RebuildAllAsync();
        else _ = Manager.RebuildAsync(root);
    }

    public void RefreshVolumes() => _ = Manager.RefreshVolumesAsync();

    public void Dispose()
    {
        if (_ownsManager) Manager.Dispose();
    }
}
