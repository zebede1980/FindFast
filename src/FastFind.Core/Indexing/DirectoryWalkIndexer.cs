using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace FastFind.Core.Indexing;

/// <summary>
/// Fallback indexer used when the process is not elevated or the volume is not NTFS.
/// Walks the directory tree in parallel using the low-allocation FileSystemEnumerator API.
/// </summary>
public static class DirectoryWalkIndexer
{
    private readonly record struct RawEntry(string Directory, string Name, FileAttributes Attributes, long Size, long Modified, long Created);

    public static VolumeIndex Build(string root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        root = root.TrimEnd('\\') + "\\";
        var index = VolumeIndex.CreateEmpty(root, IndexMode.DirectoryWalk, 1 << 16);
        long processed = 0;

        // Top level first (sequential), then each top-level directory in parallel.
        var topLevel = new List<RawEntry>();
        EnumerateDirectory(root, recurse: false, topLevel, ct);

        var dirIndexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Norm(root)] = index.RootIndex };
        var subTrees = new List<string>();
        foreach (var e in topLevel)
        {
            int i = AddEntry(index, dirIndexByPath, e);
            if (i >= 0 && (e.Attributes & FileAttributes.Directory) != 0 && (e.Attributes & FileAttributes.ReparsePoint) == 0)
                subTrees.Add(VolumeIndex.Combine(root, e.Name));
        }
        processed += topLevel.Count;

        var buckets = new ConcurrentQueue<List<RawEntry>>();
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
        Parallel.ForEach(subTrees, options, dir =>
        {
            var list = new List<RawEntry>(4096);
            EnumerateDirectory(dir, recurse: true, list, ct);
            buckets.Enqueue(list);
            long done = Interlocked.Add(ref processed, list.Count);
            progress?.Report(new IndexProgress(root, "Scanning folders", done, 0));
        });

        progress?.Report(new IndexProgress(root, "Building index", processed, processed));
        foreach (var list in buckets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var e in list) AddEntry(index, dirIndexByPath, e);
        }
        index.BuiltAtUtc = DateTime.UtcNow;
        return index;
    }

    private static int AddEntry(VolumeIndex index, Dictionary<string, int> dirIndexByPath, in RawEntry e)
    {
        if (!dirIndexByPath.TryGetValue(Norm(e.Directory), out int parent)) return -1;
        int i = index.Add(0, e.Name, parent, e.Attributes, e.Size, e.Modified, e.Created);
        if ((e.Attributes & FileAttributes.Directory) != 0)
            dirIndexByPath[Norm(VolumeIndex.Combine(e.Directory, e.Name))] = i;
        return i;
    }

    /// <summary>Dictionary key form without a trailing backslash, so "C:\" and "C:" agree.</summary>
    private static string Norm(string path) => path.Length > 1 && path[^1] == '\\' ? path.TrimEnd('\\') : path;

    private static void EnumerateDirectory(string directory, bool recurse, List<RawEntry> output, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = 256,
        };
        try
        {
            var enumerable = new FileSystemEnumerable<RawEntry>(directory, Transform, options)
            {
                // Do not descend into junctions/symlinks: they create loops and duplicate whole trees.
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            int n = 0;
            foreach (var e in enumerable)
            {
                output.Add(e);
                if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* directory vanished or inaccessible mid-scan */ }
    }

    [ThreadStatic] private static string? _lastDirectory;

    private static RawEntry Transform(ref FileSystemEntry entry)
    {
        // Entries of one directory arrive consecutively; reuse the previous directory string when it matches.
        string dir;
        if (_lastDirectory != null && entry.Directory.Equals(_lastDirectory, StringComparison.Ordinal)) dir = _lastDirectory;
        else _lastDirectory = dir = entry.Directory.ToString();
        bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
        return new RawEntry(dir, entry.FileName.ToString(), entry.Attributes, isDir ? 0 : entry.Length,
            entry.LastWriteTimeUtc.ToFileTime(), entry.CreationTimeUtc.ToFileTime());
    }
}
