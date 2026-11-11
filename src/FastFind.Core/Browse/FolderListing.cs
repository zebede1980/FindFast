using System.IO.Enumeration;
using FastFind.Core.Indexing;

namespace FastFind.Core.Browse;

/// <summary>
/// The live contents of one folder, read straight from the file system (never from the index, which can lag).
/// The entries are held in a one-level <see cref="VolumeIndex"/> so the normal search engine can filter, sort and
/// highlight them exactly as it does for index-wide searches.
/// </summary>
public sealed class FolderListing
{
    private readonly record struct Raw(string Name, FileAttributes Attributes, long Size, long Modified, long Created);

    public string Folder { get; }
    public VolumeIndex Index { get; }
    public int Count => Index.LiveCount;
    /// <summary>Why the folder could not be read, or null.</summary>
    public string? Error { get; }
    public DateTime LoadedAtUtc { get; } = DateTime.UtcNow;

    private FolderListing(string folder, VolumeIndex index, string? error)
    {
        Folder = folder;
        Index = index;
        Error = error;
    }

    /// <summary>Reads <paramref name="folder"/>. Never throws for an unreadable folder: the listing comes back empty with <see cref="Error"/> set.</summary>
    public static FolderListing Load(string folder, CancellationToken ct = default)
    {
        var index = VolumeIndex.CreateEmpty(folder, IndexMode.DirectoryWalk, 256);
        string? error = null;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                // Report an unreadable folder instead of silently showing it as empty.
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };
            var entries = new FileSystemEnumerable<Raw>(folder, Transform, options);
            int n = 0;
            foreach (var e in entries)
            {
                index.Add(0, e.Name, index.RootIndex, e.Attributes, e.Size, e.Modified, e.Created);
                if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { error = "You don't have permission to open this folder."; }
        catch (DirectoryNotFoundException) { error = "This folder no longer exists."; }
        catch (IOException ex) { error = ex.Message; }
        catch (System.Security.SecurityException ex) { error = ex.Message; }

        index.Lock.EnterReadLock();
        try { index.EnsureNameOrder(); } finally { index.Lock.ExitReadLock(); }
        return new FolderListing(folder, index, error);
    }

    private static Raw Transform(ref FileSystemEntry entry)
    {
        bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
        return new Raw(entry.FileName.ToString(), entry.Attributes, isDir ? 0 : entry.Length,
            entry.LastWriteTimeUtc.ToFileTime(), entry.CreationTimeUtc.ToFileTime());
    }

    /// <summary>Writes aggregated folder sizes (from the index) into the listing. Returns true when anything changed.</summary>
    public bool ApplyFolderSizes(IReadOnlyDictionary<string, long> sizes)
    {
        if (sizes.Count == 0) return false;
        bool changed = false;
        Index.Lock.EnterWriteLock();
        try
        {
            var names = Index.NamesArray;
            var meta = Index.MetaArray;
            for (int i = 0; i < Index.Count; i++)
            {
                if (i == Index.RootIndex || names[i] is not { } name || !meta[i].IsDirectory) continue;
                if (sizes.TryGetValue(name, out long size) && meta[i].Size != size)
                {
                    Index.SetSize(i, size);
                    changed = true;
                }
            }
        }
        finally
        {
            Index.Lock.ExitWriteLock();
        }
        return changed;
    }
}

/// <summary>Path helpers shared by the panes: normalising what the user typed and walking up the tree.</summary>
public static class PathUtil
{
    /// <summary>
    /// Canonical form of a folder path: environment variables expanded, forward slashes fixed, no trailing backslash
    /// except on a drive root ("C:\"). Returns "" for input that is not a usable absolute path.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (path == "~" || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        if (path.Length == 2 && path[1] == ':') path += "\\";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "";
            path = Path.GetFullPath(path);
        }
        catch (Exception) { return ""; }
        if (path.Length > 3 && path[^1] == '\\') path = path.TrimEnd('\\');
        return path;
    }

    /// <summary>The containing folder, or null at a drive or share root.</summary>
    public static string? Parent(string path)
    {
        if (path.Length == 0) return null;
        try
        {
            string? parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent)) return null;
            // "\\server" on its own cannot be listed, so a share root has no parent to go up to.
            if (parent.StartsWith(@"\\") && parent.Trim('\\').IndexOf('\\') < 0) return null;
            return Normalize(parent);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Last segment for display: "FastFind" for "C:\src\FastFind", "C:" for a drive root, the share for a UNC root.</summary>
    public static string LeafName(string path)
    {
        if (path.Length == 0) return "";
        string trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed;
        int slash = trimmed.LastIndexOf('\\');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The path split into clickable segments, root first: ("C:", "C:\"), ("src", "C:\src"), ...</summary>
    public static List<(string Name, string Path)> Segments(string path)
    {
        var segments = new List<(string, string)>();
        if (path.Length == 0) return segments;
        string root = Path.GetPathRoot(path) ?? "";
        string rest = path.Length > root.Length ? path[root.Length..] : "";
        string current = Normalize(root);
        segments.Add((root.TrimEnd('\\'), current));
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = VolumeIndex.Combine(current, part);
            segments.Add((part, current));
        }
        return segments;
    }

    public static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies beneath it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string f = folder.TrimEnd('\\');
        string p = path.TrimEnd('\\');
        return p.Length == f.Length
            ? p.Equals(f, StringComparison.OrdinalIgnoreCase)
            : p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p[f.Length] == '\\';
    }
}
