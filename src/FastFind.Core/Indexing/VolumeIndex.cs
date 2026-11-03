using System.Collections.Concurrent;

namespace FastFind.Core.Indexing;

/// <summary>
/// In-memory index of one volume. Names live in a flat string array (scanned linearly by the search engine,
/// which is how Everything does it too); parent links let full paths be rebuilt on demand.
/// Readers must hold <see cref="Lock"/> in read mode; mutators hold it in write mode.
/// </summary>
public sealed class VolumeIndex
{
    private string?[] _names;
    private EntryMeta[] _meta;
    private string?[] _dirPaths;
    private int _count;
    private int _live;
    private readonly Dictionary<ulong, int> _byKey;
    private int[]? _nameOrder;
    private ulong _nextSyntheticKey = 1UL << 62;

    public string Root { get; }
    public IndexMode Mode { get; internal set; }
    public int RootIndex { get; }
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);
    public DateTime BuiltAtUtc { get; internal set; } = DateTime.UtcNow;

    internal VolumeIndex(string root, string?[] names, EntryMeta[] meta, int count, Dictionary<ulong, int> byKey, int rootIndex, IndexMode mode)
    {
        Root = root;
        _names = names;
        _meta = meta;
        _count = count;
        _byKey = byKey;
        RootIndex = rootIndex;
        Mode = mode;
        _dirPaths = new string?[names.Length];
        _live = 0;
        for (int i = 0; i < count; i++) if (names[i] != null) _live++;
    }

    public static VolumeIndex CreateEmpty(string root, IndexMode mode, int capacity = 1024)
    {
        var names = new string?[Math.Max(capacity, 16)];
        var meta = new EntryMeta[names.Length];
        names[0] = root;
        meta[0] = new EntryMeta { Key = 0, Parent = -1, Attributes = FileAttributes.Directory };
        return new VolumeIndex(root, names, meta, 1, new Dictionary<ulong, int>(), 0, mode);
    }

    /// <summary>Total slots used, including tombstones. Only valid under the read lock.</summary>
    public int Count => _count;

    /// <summary>Number of live entries (excludes tombstones and the root entry).</summary>
    public int LiveCount => Math.Max(0, _live - 1);

    /// <summary>Raw name array; a null slot is a deleted entry. Only valid under the read lock.</summary>
    public string?[] NamesArray => _names;

    /// <summary>Raw metadata array. Only valid under the read lock.</summary>
    public EntryMeta[] MetaArray => _meta;

    public string? GetName(int i) => (uint)i < (uint)_count ? _names[i] : null;
    public ref EntryMeta GetMeta(int i) => ref _meta[i];
    public bool IsLive(int i) => (uint)i < (uint)_count && _names[i] != null;
    public bool IsDirectory(int i) => (_meta[i].Attributes & FileAttributes.Directory) != 0;

    /// <summary>Full path of a directory entry (cached). Root returns e.g. "C:\".</summary>
    public string GetDirectoryPath(int i)
    {
        var cached = _dirPaths[i];
        if (cached != null) return cached;
        if (i == RootIndex) return _dirPaths[i] = Root;
        int parent = _meta[i].Parent;
        string parentPath = parent < 0 ? Root : GetDirectoryPath(parent);
        string path = Combine(parentPath, _names[i] ?? "?");
        _dirPaths[i] = path;
        return path;
    }

    /// <summary>Full path of the directory that contains entry i.</summary>
    public string GetParentPath(int i)
    {
        int parent = _meta[i].Parent;
        return parent < 0 ? "" : GetDirectoryPath(parent);
    }

    public string GetFullPath(int i)
    {
        if (i == RootIndex) return Root;
        return Combine(GetParentPath(i), _names[i] ?? "");
    }

    public static string Combine(string dir, string name)
        => dir.Length == 0 ? name : dir[^1] == '\\' ? dir + name : dir + "\\" + name;

    public bool TryGetIndex(ulong key, out int index) => _byKey.TryGetValue(key, out index);

    private Dictionary<ChildKey, int>? _children;

    private readonly record struct ChildKey(int Parent, string Name);

    private sealed class ChildKeyComparer : IEqualityComparer<ChildKey>
    {
        public static readonly ChildKeyComparer Instance = new();
        public bool Equals(ChildKey a, ChildKey b) => a.Parent == b.Parent && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ChildKey k) => HashCode.Combine(k.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
    }

    /// <summary>
    /// Builds a (parent, name) lookup so path-based updates from FileSystemWatcher are O(1) instead of a scan per event.
    /// Only needed for directory-walk indexes; MFT indexes are keyed by file reference. Caller holds the write lock.
    /// </summary>
    public void EnableChildLookup()
    {
        if (_children != null) return;
        var map = new Dictionary<ChildKey, int>(_live, ChildKeyComparer.Instance);
        for (int i = 0; i < _count; i++)
        {
            if (_names[i] is { } n && i != RootIndex) map[new ChildKey(_meta[i].Parent, n)] = i;
        }
        _children = map;
    }

    /// <summary>Finds a direct child by name.</summary>
    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        if (_children != null)
            return _children.TryGetValue(new ChildKey(parent, name.ToString()), out int idx) ? idx : -1;
        var names = _names;
        var meta = _meta;
        for (int i = 0; i < _count; i++)
        {
            if (meta[i].Parent == parent && names[i] is { } n && name.Equals(n, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Resolves an absolute directory path to an entry index, or -1.</summary>
    public int FindDirectory(string fullPath)
    {
        if (!fullPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return -1;
        int idx = RootIndex;
        var rest = fullPath.AsSpan(Root.Length).Trim('\\');
        while (!rest.IsEmpty)
        {
            int slash = rest.IndexOf('\\');
            var part = slash < 0 ? rest : rest[..slash];
            idx = FindChild(idx, part);
            if (idx < 0) return -1;
            rest = slash < 0 ? ReadOnlySpan<char>.Empty : rest[(slash + 1)..];
        }
        return idx;
    }

    // ---- mutators: caller must hold the write lock ----

    public int Add(ulong key, string name, int parent, FileAttributes attributes, long size, long modifiedFileTime, long createdFileTime)
    {
        if (_count == _names.Length) Grow();
        if (key == 0) key = _nextSyntheticKey++;
        int i = _count++;
        _names[i] = name;
        _meta[i] = new EntryMeta
        {
            Key = key, Parent = parent, Attributes = attributes, Size = size,
            ModifiedFileTime = modifiedFileTime, CreatedFileTime = createdFileTime,
        };
        _byKey[key] = i;
        _children?.TryAdd(new ChildKey(parent, name), i);
        _live++;
        _nameOrder = null;
        return i;
    }

    public void Remove(int i)
    {
        if ((uint)i >= (uint)_count || _names[i] == null || i == RootIndex) return;
        _byKey.Remove(_meta[i].Key);
        _children?.Remove(new ChildKey(_meta[i].Parent, _names[i]!));
        _names[i] = null;
        _dirPaths[i] = null;
        _live--;
        _nameOrder = null;
    }

    /// <summary>Removes a directory and everything beneath it (used when a watcher only reports the top-level delete).</summary>
    public void RemoveSubtree(int i)
    {
        if ((uint)i >= (uint)_count || _names[i] == null) return;
        if (!IsDirectory(i)) { Remove(i); return; }
        var removedDirs = new HashSet<int> { i };
        Remove(i);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int j = 0; j < _count; j++)
            {
                if (_names[j] == null) continue;
                if (removedDirs.Contains(_meta[j].Parent))
                {
                    if (IsDirectory(j)) removedDirs.Add(j);
                    Remove(j);
                    changed = true;
                }
            }
        }
    }

    public void Rename(int i, string newName, int newParent)
    {
        if ((uint)i >= (uint)_count || _names[i] == null) return;
        _children?.Remove(new ChildKey(_meta[i].Parent, _names[i]!));
        _names[i] = newName;
        _meta[i].Parent = newParent;
        _children?.TryAdd(new ChildKey(newParent, newName), i);
        if (IsDirectory(i)) Array.Clear(_dirPaths, 0, _count);
        _nameOrder = null;
    }

    public void Update(int i, long size, long modifiedFileTime, long createdFileTime, FileAttributes attributes)
    {
        if ((uint)i >= (uint)_count || _names[i] == null) return;
        ref var m = ref _meta[i];
        m.Size = size;
        if (modifiedFileTime != 0) m.ModifiedFileTime = modifiedFileTime;
        if (createdFileTime != 0) m.CreatedFileTime = createdFileTime;
        m.Attributes = attributes;
    }

    public void UpdateAttributes(int i, FileAttributes attributes)
    {
        if ((uint)i >= (uint)_count || _names[i] == null) return;
        _meta[i].Attributes = attributes;
    }

    private void Grow()
    {
        int newSize = Math.Max(1024, _names.Length * 2);
        Array.Resize(ref _names, newSize);
        Array.Resize(ref _meta, newSize);
        Array.Resize(ref _dirPaths, newSize);
    }

    // ---- name ordering ----

    /// <summary>Indices of live entries sorted by name (case-insensitive). Built lazily; invalidated by any mutation.</summary>
    public int[] EnsureNameOrder()
    {
        var order = _nameOrder;
        if (order != null) return order;
        order = BuildNameOrder();
        _nameOrder = order;
        return order;
    }

    public bool IsNameOrderReady => _nameOrder != null;

    private int[] BuildNameOrder()
    {
        var names = _names;
        int count = _count;
        var idx = new int[_live];
        int n = 0;
        for (int i = 0; i < count; i++) if (names[i] != null) idx[n++] = i;
        ParallelSort.Sort(idx, (a, b) => string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase));
        return idx;
    }
}

/// <summary>Chunked parallel sort followed by a k-way merge. Faster than Array.Sort for large string comparisons.</summary>
public static class ParallelSort
{
    public static void Sort(int[] items, Comparison<int> comparison)
    {
        int n = items.Length;
        if (n < 50_000)
        {
            Array.Sort(items, comparison);
            return;
        }
        int chunks = Math.Clamp(Environment.ProcessorCount, 2, 16);
        int chunkSize = (n + chunks - 1) / chunks;
        var ranges = new (int Start, int Length)[chunks];
        for (int c = 0; c < chunks; c++)
        {
            int start = c * chunkSize;
            ranges[c] = (start, Math.Max(0, Math.Min(chunkSize, n - start)));
        }
        Parallel.For(0, chunks, c =>
        {
            var (start, len) = ranges[c];
            if (len > 1) Array.Sort(items, start, len, Comparer<int>.Create(comparison));
        });

        // k-way merge into a buffer
        var output = new int[n];
        var heads = new int[chunks];
        for (int c = 0; c < chunks; c++) heads[c] = ranges[c].Start;
        for (int o = 0; o < n; o++)
        {
            int best = -1;
            for (int c = 0; c < chunks; c++)
            {
                var (start, len) = ranges[c];
                if (heads[c] >= start + len) continue;
                if (best < 0 || comparison(items[heads[c]], items[heads[best]]) < 0) best = c;
            }
            output[o] = items[heads[best]++];
        }
        Array.Copy(output, items, n);
    }
}
