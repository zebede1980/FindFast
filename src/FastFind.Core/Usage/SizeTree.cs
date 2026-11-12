using FastFind.Core.Browse;
using FastFind.Core.Indexing;

namespace FastFind.Core.Usage;

/// <summary>Space taken by one extension inside a <see cref="SizeTree"/>.</summary>
public readonly record struct ExtensionStat(int Id, string Extension, FileCategory Category, long Bytes, int Files);

/// <summary>
/// A folder and everything beneath it, with the size of every folder added up: the data behind the disk usage map.
/// Nodes are numbered; node 0 is the folder itself. Everything is held in flat arrays rather than one object per file,
/// so a whole drive (a million entries or more) stays compact. Children are kept largest first.
/// Not thread-safe: build it on one thread, then read it from any; <see cref="Remove"/> must not race with readers.
/// </summary>
public sealed class SizeTree
{
    public const int Root = 0;

    private const byte DirectoryFlag = 1, RemovedFlag = 2, CloudFlag = 4;

    private readonly string[] _names;
    private readonly long[] _sizes;
    private readonly long[] _modified;
    private readonly int[] _parents;
    private readonly int[] _childStart;
    private readonly int[] _childCount;
    private readonly int[] _children;
    private readonly int[] _files;
    private readonly ushort[] _extension;
    private readonly byte[] _flags;
    private readonly string[] _extensions;
    private readonly FileCategory[] _extensionCategory;
    private readonly Comparison<int> _bySize;

    internal SizeTree(string rootPath, int count, string[] names, long[] sizes, long[] modified, int[] parents, ushort[] extension, byte[] flags,
        string[] extensions, long cloudBytes, int cloudFiles)
    {
        RootPath = rootPath;
        Count = count;
        _names = names;
        _sizes = sizes;
        _modified = modified;
        _parents = parents;
        _extension = extension;
        _flags = flags;
        _extensions = extensions;
        _extensionCategory = Array.ConvertAll(extensions, e => FileCategories.Of(e));
        CloudBytes = cloudBytes;
        CloudFiles = cloudFiles;
        _bySize = CompareBySize;

        // Children as one shared array, each node owning a slice of it.
        _childStart = new int[count];
        _childCount = new int[count];
        _files = new int[count];
        for (int i = 1; i < count; i++) _childCount[parents[i]]++;
        int next = 0;
        for (int i = 0; i < count; i++) { _childStart[i] = next; next += _childCount[i]; }
        _children = new int[next];
        var fill = new int[count];
        for (int i = 1; i < count; i++)
        {
            int p = parents[i];
            _children[_childStart[p] + fill[p]++] = i;
        }

        // A parent is always created before its children, so one backwards pass adds every size to all its ancestors.
        for (int i = count - 1; i >= 1; i--)
        {
            int p = parents[i];
            _sizes[p] += _sizes[i];
            _files[p] += IsDirectory(i) ? _files[i] : 1;
        }
        for (int i = 0; i < count; i++) if (_childCount[i] > 1) SortChildren(i);
    }

    /// <summary>The folder the tree describes, e.g. "C:\" or "C:\Users\joe".</summary>
    public string RootPath { get; }

    /// <summary>Number of node slots, including any removed since the tree was built.</summary>
    public int Count { get; }

    /// <summary>Bumped whenever a node is removed, so views know to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Total logical size of online-only files (OneDrive placeholders), which take no space on this disk and count as zero in the tree.</summary>
    public long CloudBytes { get; }
    public int CloudFiles { get; }

    public string Name(int node) => _names[node];
    public long Size(int node) => _sizes[node];
    public int Parent(int node) => _parents[node];
    public bool IsDirectory(int node) => (_flags[node] & DirectoryFlag) != 0;
    public bool IsRemoved(int node) => (_flags[node] & RemovedFlag) != 0;
    /// <summary>True for an online-only file: listed, but holding no data on this disk.</summary>
    public bool IsCloudOnly(int node) => (_flags[node] & CloudFlag) != 0;
    public int ChildCount(int node) => _childCount[node];
    /// <summary>Children of a folder, largest first.</summary>
    public ReadOnlySpan<int> Children(int node) => _children.AsSpan(_childStart[node], _childCount[node]);
    /// <summary>Number of files beneath a folder, at any depth (0 for a file).</summary>
    public int FileCount(int node) => _files[node];
    /// <summary>Last write time as a FILETIME, or 0 when unknown.</summary>
    public long ModifiedFileTime(int node) => _modified[node];
    /// <summary>Lower-case extension without the dot; empty for folders and files without one.</summary>
    public string Extension(int node) => _extensions[_extension[node]];
    public int ExtensionId(int node) => _extension[node];
    public int ExtensionCount => _extensions.Length;
    public string ExtensionName(int id) => _extensions[id];
    public FileCategory CategoryOfExtension(int id) => _extensionCategory[id];
    public FileCategory Category(int node) => _extensionCategory[_extension[node]];

    public string GetPath(int node)
    {
        if (node == Root) return RootPath;
        return VolumeIndex.Combine(GetPath(_parents[node]), _names[node]);
    }

    public int Depth(int node)
    {
        int depth = 0;
        for (int n = node; n > Root; n = _parents[n]) depth++;
        return depth;
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or lies beneath it.</summary>
    public bool IsSameOrInside(int node, int ancestor)
    {
        for (int n = node; n >= 0; n = _parents[n])
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>The node for an absolute path, or -1 when it is not in the tree.</summary>
    public int Find(string path)
    {
        if (!PathUtil.IsSameOrInside(path, RootPath)) return -1;
        int node = Root;
        var rest = path.AsSpan(Math.Min(path.Length, RootPath.TrimEnd('\\').Length)).Trim('\\');
        while (!rest.IsEmpty)
        {
            int slash = rest.IndexOf('\\');
            var part = slash < 0 ? rest : rest[..slash];
            int found = -1;
            foreach (int child in Children(node))
            {
                if (part.Equals(_names[child], StringComparison.OrdinalIgnoreCase)) { found = child; break; }
            }
            if (found < 0) return -1;
            node = found;
            rest = slash < 0 ? default : rest[(slash + 1)..];
        }
        return node;
    }

    /// <summary>The largest file beneath a node (the node itself if it is a file), or -1 for an empty folder.</summary>
    public int LargestFile(int node)
    {
        while (IsDirectory(node))
        {
            // Children are largest first, but the largest child may be a folder full of small files; good enough for a colour.
            int next = -1;
            foreach (int child in Children(node))
            {
                if (_sizes[child] > 0 || !IsDirectory(child)) { next = child; break; }
            }
            if (next < 0) return -1;
            node = next;
        }
        return node;
    }

    /// <summary>
    /// Takes a file or folder out of the tree (after it was deleted on disk): its size leaves every folder above it and
    /// the folders are put back in size order. The root cannot be removed.
    /// </summary>
    public void Remove(int node)
    {
        if (node <= Root || node >= Count || IsRemoved(node)) return;
        int parent = _parents[node];
        var slice = _children.AsSpan(_childStart[parent], _childCount[parent]);
        int at = slice.IndexOf(node);
        if (at < 0) return;
        slice[(at + 1)..].CopyTo(slice[at..]);
        _childCount[parent]--;
        _flags[node] |= RemovedFlag;

        long size = _sizes[node];
        int files = IsDirectory(node) ? _files[node] : 1;
        for (int a = parent; a >= 0; a = _parents[a])
        {
            _sizes[a] -= size;
            _files[a] -= files;
        }
        // Each folder that shrank may now belong further down its parent's list.
        for (int a = parent; a > Root; a = _parents[a]) SortChildren(_parents[a]);
        Version++;
    }

    /// <summary>
    /// Takes many items out at once (after a clean-up). Does the same as calling <see cref="Remove"/> for each, but
    /// tidies each affected folder once rather than once per item, so emptying a folder of 100,000 files is quick.
    /// An item whose folder is also in the list is covered by the folder.
    /// </summary>
    public void RemoveMany(IEnumerable<int> nodes)
    {
        var touched = new HashSet<int>();
        foreach (int node in nodes)
        {
            if (node <= Root || node >= Count || IsRemoved(node)) continue;
            // Something above it already went in this batch: its size has left the tree with that folder.
            bool covered = false;
            for (int a = _parents[node]; a > Root; a = _parents[a])
                if (IsRemoved(a)) { covered = true; break; }
            if (covered) continue;

            _flags[node] |= RemovedFlag;
            long size = _sizes[node];
            int files = IsDirectory(node) ? _files[node] : 1;
            for (int a = _parents[node]; a >= 0; a = _parents[a])
            {
                _sizes[a] -= size;
                _files[a] -= files;
            }
            touched.Add(_parents[node]);
        }
        if (touched.Count == 0) return;

        // Close the gaps the removed items left in their folders' lists.
        foreach (int parent in touched)
        {
            var slice = _children.AsSpan(_childStart[parent], _childCount[parent]);
            int kept = 0;
            foreach (int child in slice)
                if (!IsRemoved(child)) slice[kept++] = child;
            _childCount[parent] = kept;
        }
        // Every folder that shrank may belong further down its own folder's list.
        var resort = new HashSet<int>();
        foreach (int parent in touched)
            for (int a = parent; a > Root; a = _parents[a])
                if (!resort.Add(_parents[a])) break;
        foreach (int folder in resort)
            if (!IsRemoved(folder)) SortChildren(folder);
        Version++;
    }

    private void SortChildren(int node)
        => _children.AsSpan(_childStart[node], _childCount[node]).Sort(_bySize);

    private int CompareBySize(int a, int b)
    {
        int c = _sizes[b].CompareTo(_sizes[a]);
        return c != 0 ? c : string.Compare(_names[a], _names[b], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Space and file count per extension beneath <paramref name="node"/>, largest first.</summary>
    public List<ExtensionStat> ExtensionStats(int node = Root)
    {
        var bytes = new long[_extensions.Length];
        var files = new int[_extensions.Length];
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!IsDirectory(n))
            {
                bytes[_extension[n]] += _sizes[n];
                files[_extension[n]]++;
                continue;
            }
            foreach (int child in Children(n)) stack.Push(child);
        }
        var stats = new List<ExtensionStat>();
        for (int i = 0; i < bytes.Length; i++)
            if (files[i] > 0) stats.Add(new ExtensionStat(i, _extensions[i], _extensionCategory[i], bytes[i], files[i]));
        stats.Sort((a, b) =>
        {
            int c = b.Bytes.CompareTo(a.Bytes);
            return c != 0 ? c : string.CompareOrdinal(a.Extension, b.Extension);
        });
        return stats;
    }

    /// <summary>Collects a tree one entry at a time. A folder must be added before anything inside it.</summary>
    public sealed class Builder
    {
        private readonly string _root;
        private readonly List<string> _names = new();
        private readonly List<long> _sizes = new();
        private readonly List<long> _modified = new();
        private readonly List<int> _parents = new();
        private readonly List<ushort> _extension = new();
        private readonly List<byte> _flags = new();
        private readonly List<string> _extensions = new() { "" };
        private readonly Dictionary<string, ushort> _extensionIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ushort>.AlternateLookup<ReadOnlySpan<char>> _extensionLookup;
        private Dictionary<string, int>? _folders;
        private string? _lastFolder;
        private int _lastFolderNode;
        private long _cloudBytes;
        private int _cloudFiles;

        /// <param name="rootPath">The folder being measured, in normalised form (see <see cref="PathUtil.Normalize"/>).</param>
        public Builder(string rootPath)
        {
            _root = rootPath;
            _extensionLookup = _extensionIds.GetAlternateLookup<ReadOnlySpan<char>>();
            Append(rootPath, 0, 0, -1, 0, DirectoryFlag);
        }

        /// <summary>Entries added so far, not counting the root.</summary>
        public int Count => _names.Count - 1;

        private int Append(string name, long size, long modified, int parent, ushort extension, byte flags)
        {
            _names.Add(name);
            _sizes.Add(size);
            _modified.Add(modified);
            _parents.Add(parent);
            _extension.Add(extension);
            _flags.Add(flags);
            return _names.Count - 1;
        }

        public int AddFolder(int parent, string name, long modifiedFileTime = 0)
            => Append(name, 0, modifiedFileTime, parent, 0, DirectoryFlag);

        /// <param name="cloudOnly">An online-only placeholder: recorded, but counted as taking no space.</param>
        public int AddFile(int parent, string name, long size, long modifiedFileTime = 0, bool cloudOnly = false)
        {
            if (size < 0) size = 0;
            if (cloudOnly)
            {
                _cloudBytes += size;
                _cloudFiles++;
                size = 0;
            }
            return Append(name, size, modifiedFileTime, parent, ExtensionOf(name), cloudOnly ? CloudFlag : (byte)0);
        }

        /// <summary>
        /// Adds an entry known only by its path, as rows from the index are. Rows may arrive in any order: a folder that
        /// has not been seen yet is created on the spot and filled in when its own row turns up. Returns false for a row
        /// that does not lie inside the root.
        /// </summary>
        public bool AddEntry(string? name, string directory, long size, long modifiedFileTime, FileAttributes attributes)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int parent = FolderNode(directory);
            if (parent < 0) return false;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                string path = VolumeIndex.Combine(directory, name);
                if (_folders!.TryGetValue(path, out int existing)) _modified[existing] = modifiedFileTime;
                else _folders[path] = AddFolder(parent, name, modifiedFileTime);
            }
            else
            {
                AddFile(parent, name, size, modifiedFileTime, IsCloudOnly(attributes));
            }
            return true;
        }

        /// <summary>Offline, or flagged to be fetched on access: the file's data is not on this disk.</summary>
        public static bool IsCloudOnly(FileAttributes attributes)
            => (attributes & (FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */)) != 0;

        private int FolderNode(string directory)
        {
            // Rows arrive grouped by folder, so the folder is nearly always the one used last.
            if (ReferenceEquals(directory, _lastFolder) || directory == _lastFolder) return _lastFolderNode;
            _folders ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [_root] = Root };
            int node = EnsureFolder(directory, 0);
            if (node >= 0) { _lastFolder = directory; _lastFolderNode = node; }
            return node;
        }

        private int EnsureFolder(string path, int depth)
        {
            if (_folders!.TryGetValue(path, out int node)) return node;
            if (depth > 512 || path.Length <= _root.TrimEnd('\\').Length || !PathUtil.IsSameOrInside(path, _root)) return -1;
            int slash = path.LastIndexOf('\\');
            if (slash <= 0 || slash == path.Length - 1) return -1;
            string parentPath = path[..slash];
            if (parentPath.Length == 2 && parentPath[1] == ':') parentPath += "\\";
            int parent = EnsureFolder(parentPath, depth + 1);
            if (parent < 0) return -1;
            node = AddFolder(parent, path[(slash + 1)..]);
            _folders[path] = node;
            return node;
        }

        private ushort ExtensionOf(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) return 0;
            var ext = name.AsSpan(dot + 1);
            // Something like "report.final version 2" has no real extension; such tails would only clutter the type list.
            if (ext.Length > 12) return 0;
            foreach (char c in ext)
                if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return 0;
            if (_extensionLookup.TryGetValue(ext, out ushort id)) return id;
            if (_extensions.Count >= ushort.MaxValue) return 0;
            id = (ushort)_extensions.Count;
            string lower = ext.ToString().ToLowerInvariant();
            _extensions.Add(lower);
            _extensionIds[lower] = id;
            return id;
        }

        public SizeTree Build()
            => new(_root, _names.Count, _names.ToArray(), _sizes.ToArray(), _modified.ToArray(), _parents.ToArray(), _extension.ToArray(), _flags.ToArray(),
                _extensions.ToArray(), _cloudBytes, _cloudFiles);
    }
}
