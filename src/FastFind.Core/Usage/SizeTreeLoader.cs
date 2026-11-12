using System.IO.Enumeration;
using FastFind.Core.Api;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.Core.Usage;

/// <summary>How far a <see cref="SizeTreeLoader"/> has got. <see cref="Total"/> is 0 when it is not known in advance (a disk scan).</summary>
public readonly record struct SizeTreeProgress(int Done, int Total);

/// <summary>Builds a <see cref="SizeTree"/> for a folder, either from the index (instant) or by reading the disk.</summary>
public static class SizeTreeLoader
{
    private const int PageSize = 50_000;

    /// <summary>
    /// Reads every entry beneath <paramref name="folder"/> from the index. Uses the ordinary search API, so it works
    /// the same whether the index is in this process or behind the service's pipe. Returns null when the index has
    /// nothing for the folder or stops answering part-way, in which case the caller should scan the disk instead.
    /// </summary>
    public static async Task<SizeTree?> FromIndexAsync(IIndexService index, string folder, IProgress<SizeTreeProgress>? progress = null, CancellationToken ct = default)
    {
        // Sorted by path so each folder's entries arrive together: far less to send, and one folder lookup per run of rows.
        var request = new SearchRequest { ScopeFolder = folder, ScopeRecursive = true, Sort = SortField.Path };
        using var set = await index.SearchAsync(request, ct).ConfigureAwait(false);
        if (set.Error != null || set.Count == 0) return null;
        int total = set.Count;
        return await Task.Run(() =>
        {
            var builder = new SizeTree.Builder(folder);
            for (int offset = 0; offset < total; offset += PageSize)
            {
                ct.ThrowIfCancellationRequested();
                var rows = set.GetRows(offset, PageSize);
                int live = 0;
                foreach (var row in rows)
                {
                    if (row.Name == null) continue;
                    live++;
                    builder.AddEntry(row.Name, row.Directory, row.Size, row.ModifiedFileTime, row.Attributes);
                }
                // A page of nothing but gaps means the result has gone (the service restarted, say), not that files were deleted.
                if (live == 0 && rows.Length > 0) return null;
                progress?.Report(new SizeTreeProgress(Math.Min(total, offset + rows.Length), total));
            }
            return builder.Count == 0 ? null : builder.Build();
        }, ct).ConfigureAwait(false);
    }

    private readonly record struct Found(string Name, bool IsDirectory, long Size, long Modified, FileAttributes Attributes);

    /// <summary>
    /// Measures <paramref name="folder"/> by reading it from disk. Slower than the index (minutes for a whole drive) but
    /// works anywhere. Folders that cannot be opened are skipped; junctions and other links are listed but not followed,
    /// so nothing is counted twice.
    /// </summary>
    public static SizeTree FromDisk(string folder, IProgress<SizeTreeProgress>? progress = null, CancellationToken ct = default)
    {
        var builder = new SizeTree.Builder(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<(string Path, int Node)>();
        pending.Push((folder, SizeTree.Root));
        int reported = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, node) = pending.Pop();
            try
            {
                var entries = new FileSystemEnumerable<Found>(path,
                    (ref FileSystemEntry e) => new Found(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.ToFileTime(), e.Attributes), options);
                foreach (var e in entries)
                {
                    if (e.IsDirectory)
                    {
                        int child = builder.AddFolder(node, e.Name, e.Modified);
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push((VolumeIndex.Combine(path, e.Name), child));
                    }
                    else
                    {
                        builder.AddFile(node, e.Name, e.Size, e.Modified, SizeTree.Builder.IsCloudOnly(e.Attributes));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Vanished or locked while we were looking: leave it empty and carry on.
            }
            if (builder.Count - reported >= 5000)
            {
                reported = builder.Count;
                progress?.Report(new SizeTreeProgress(reported, 0));
            }
        }
        progress?.Report(new SizeTreeProgress(builder.Count, 0));
        return builder.Build();
    }
}
