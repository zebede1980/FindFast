using System.IO.Enumeration;
using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.Core.Duplicates;

/// <summary>The files in a folder that are worth comparing, and what was left out before any comparing began.</summary>
public sealed class DuplicateCandidateSet
{
    public List<DuplicateCandidate> Files { get; } = new();
    /// <summary>Files of the asked-for size that were looked at.</summary>
    public int Considered { get; set; }
    /// <summary>Files left out because they belong to Windows or to installed programs.</summary>
    public int Protected { get; set; }
    /// <summary>Files left out because their content is not on this computer.</summary>
    public int OnlineOnly { get; set; }
}

/// <summary>
/// Lists the files under a folder that could have duplicates, from the index (instant) or by reading the disk.
/// Windows, installed programs and shared application data are left out: they are full of identical files that are
/// meant to be there, and deleting one to save space breaks the program it belongs to.
/// </summary>
public static class DuplicateCandidates
{
    private const int PageSize = 50_000;
    private static readonly string[] SystemFolderNames = { "$RECYCLE.BIN", "System Volume Information" };

    /// <summary>True for a path whose files are never offered as duplicates.</summary>
    public static bool IsProtected(string path, UsageEnvironment env)
    {
        if (env.IsSystemPath(path)) return true;
        // The Recycle Bin and restore points, on any drive: \$RECYCLE.BIN\... straight under the root.
        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length == 0 || path.Length <= root.Length) return false;
        var rest = path.AsSpan(root.Length);
        int slash = rest.IndexOf('\\');
        var first = slash < 0 ? rest : rest[..slash];
        foreach (string name in SystemFolderNames)
            if (first.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Reads the candidates from the index. Uses the ordinary search API, so it works the same whether the index is in
    /// this process or behind the service's pipe. Files that are the only one of their size are dropped as they go by.
    /// Returns null when the index has nothing for the folder or stops answering part-way, in which case the caller
    /// should read the disk instead.
    /// </summary>
    public static async Task<DuplicateCandidateSet?> FromIndexAsync(IIndexService index, string folder, long minSize, UsageEnvironment env,
        IProgress<SizeTreeProgress>? progress = null, CancellationToken ct = default)
    {
        // Largest first, so files of one size arrive together and a size seen once can be forgotten at once.
        var request = new SearchRequest
        {
            ScopeFolder = folder, ScopeRecursive = true, Kind = EntryKind.File, MinSize = Math.Max(1, minSize), Sort = SortField.Size, Descending = true,
        };
        using var set = await index.SearchAsync(request, ct).ConfigureAwait(false);
        if (set.Error != null) return null;
        int total = set.Count;
        if (total == 0)
        {
            // Nothing that size, or nothing known about the folder at all? Only the first means there is nothing to find.
            using var any = await index.SearchAsync(new SearchRequest { ScopeFolder = folder, ScopeRecursive = true }, ct).ConfigureAwait(false);
            return any.Error == null && any.Count > 0 ? new DuplicateCandidateSet() : null;
        }
        return await Task.Run(() =>
        {
            var result = new DuplicateCandidateSet();
            var run = new List<DuplicateCandidate>();
            long runSize = -1;
            for (int offset = 0; offset < total; offset += PageSize)
            {
                ct.ThrowIfCancellationRequested();
                var rows = set.GetRows(offset, PageSize);
                int live = 0;
                foreach (var row in rows)
                {
                    if (row.Name == null) continue;
                    live++;
                    if (row.IsDirectory || row.Size < Math.Max(1, minSize)) continue;
                    result.Considered++;
                    if (IsProtected(row.Directory, env)) { result.Protected++; continue; }
                    if (DuplicateFinder.IsOnlineOnly(row.Attributes)) { result.OnlineOnly++; continue; }
                    if (row.Size != runSize)
                    {
                        if (run.Count > 1) result.Files.AddRange(run);
                        run.Clear();
                        runSize = row.Size;
                    }
                    run.Add(new DuplicateCandidate(row.FullPath, row.Size));
                }
                // A page of nothing but gaps means the result has gone (the service restarted, say), not that files were deleted.
                if (live == 0 && rows.Length > 0) return null;
                progress?.Report(new SizeTreeProgress(Math.Min(total, offset + rows.Length), total));
            }
            if (run.Count > 1) result.Files.AddRange(run);
            return result;
        }, ct).ConfigureAwait(false);
    }

    private readonly record struct Found(string Name, bool IsDirectory, long Size, FileAttributes Attributes);

    /// <summary>
    /// Lists the candidates by reading the folder from disk. Slower than the index but works anywhere. Folders that
    /// cannot be opened are skipped, and junctions and other links are not followed, so no file is met twice.
    /// </summary>
    public static DuplicateCandidateSet FromDisk(string folder, long minSize, UsageEnvironment env, IProgress<SizeTreeProgress>? progress = null, CancellationToken ct = default)
    {
        minSize = Math.Max(1, minSize);
        var result = new DuplicateCandidateSet();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(folder);
        int seen = 0, reported = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string path = pending.Pop();
            bool isProtected = IsProtected(path, env);
            try
            {
                var entries = new FileSystemEnumerable<Found>(path,
                    (ref FileSystemEntry e) => new Found(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes), options);
                foreach (var e in entries)
                {
                    seen++;
                    if (e.IsDirectory)
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(VolumeIndex.Combine(path, e.Name));
                        continue;
                    }
                    if (e.Size < minSize) continue;
                    result.Considered++;
                    if (isProtected) result.Protected++;
                    else if (DuplicateFinder.IsOnlineOnly(e.Attributes)) result.OnlineOnly++;
                    else result.Files.Add(new DuplicateCandidate(VolumeIndex.Combine(path, e.Name), e.Size));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Vanished or locked while we were looking: carry on without it.
            }
            if (seen - reported >= 5000)
            {
                reported = seen;
                progress?.Report(new SizeTreeProgress(seen, 0));
            }
        }
        progress?.Report(new SizeTreeProgress(seen, 0));
        return result;
    }
}

/// <summary>What a delete of ticked duplicates may go ahead with, and what it must leave.</summary>
/// <param name="Delete">Files that are still what they were when compared, each with an untouched copy staying behind.</param>
/// <param name="Left">Files that will not be deleted, each with the reason.</param>
/// <param name="Changed">Files in the groups looked at, ticked or not, that are no longer what was compared: they are not known duplicates any more.</param>
public sealed record DuplicateDeletePlan(List<string> Delete, List<string> Left, List<string> Changed)
{
    /// <summary>
    /// Checks the ticked files again at the moment of deleting. A file is only deleted if it is still the size and date
    /// it had when its content was read, and if a copy that is being kept is too: otherwise the two are no longer known
    /// to be the same, and deleting one could lose the only good copy.
    /// </summary>
    public static DuplicateDeletePlan For(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> ticked)
    {
        var plan = new DuplicateDeletePlan(new List<string>(), new List<string>(), new List<string>());
        foreach (var group in groups)
        {
            var going = group.Files.Where(f => ticked.Contains(f.Path)).ToList();
            if (going.Count == 0) continue;
            var changed = group.Files.Where(f => !IsUnchanged(f)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan.Changed.AddRange(changed);
            if (!group.Files.Any(f => !ticked.Contains(f.Path) && !changed.Contains(f.Path)))
            {
                foreach (var f in going) plan.Left.Add($"{f.Path}: no unchanged copy would be left");
                continue;
            }
            foreach (var f in going)
            {
                if (!changed.Contains(f.Path)) plan.Delete.Add(f.Path);
                else plan.Left.Add($"{f.Path}: it has changed or gone since it was compared");
            }
        }
        return plan;
    }

    /// <summary>True when the file is still there with the size and date it had when its content was read.</summary>
    public static bool IsUnchanged(DuplicateFile file)
    {
        try
        {
            var info = new FileInfo(file.Path);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.ModifiedUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
