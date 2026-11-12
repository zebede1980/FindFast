using System.IO;
using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Usage;

namespace FastFind.App.Services;

/// <summary>What a clean-up did.</summary>
/// <param name="Removed">The files and folders that are gone, as nodes of the tree (a folder that went whole stands for everything in it).</param>
/// <param name="Left">Files that stayed: in use, too new, or not allowed.</param>
/// <param name="Refused">Folders that were not touched at all, each with the reason.</param>
public sealed record CleanupOutcome(long BytesFreed, int FilesDeleted, int Left, List<int> Removed, List<string> Refused);

/// <summary>
/// Deletes what a clean-up was approved to delete, and nothing else. It works from the tree that was measured and
/// shown: only files that were counted are deleted, so anything that arrived since is left alone, as is any file in
/// use. Every folder is checked again here, whatever the analysis said: never inside Windows, program folders or
/// shared application data, never one of the user's main folders or anything above them, never through a link.
/// Deletion is permanent (these are caches and rebuildable files; the Recycle Bin would only keep the space taken).
/// </summary>
public static class CleanupRunner
{
    /// <param name="contentsOnly">Empty the folders but leave the folders themselves in place.</param>
    /// <param name="minAgeDays">Leave files created or changed more recently than this (they may belong to a running program).</param>
    public static Task<CleanupOutcome> RunAsync(SizeTree tree, IReadOnlyList<int> nodes, bool contentsOnly, int minAgeDays, UsageEnvironment env,
        IProgress<int>? progress = null, CancellationToken ct = default)
        => Task.Run(() => new Run(tree, env, minAgeDays, progress, ct).Execute(nodes, contentsOnly), ct);

    /// <summary>Why a folder (or file) must not be cleaned, or null when it may be.</summary>
    public static string? Refusal(string path, string treeRoot, UsageEnvironment env)
    {
        if (path.Length == 0 || !PathUtil.IsSameOrInside(path, treeRoot)) return "it is outside the folder that was measured";
        if (env.IsSystemPath(path)) return "it belongs to Windows or to installed programs";
        if (Path.GetPathRoot(path) is { } root && root.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "it is a whole drive";
        // The user's profile, application data and downloads folders themselves, and everything they sit inside.
        foreach (string keep in new[] { env.UserProfile, env.LocalAppData, env.RoamingAppData, env.LocalLowAppData, env.Downloads, env.Windows, env.ProgramFiles, env.ProgramData })
            if (keep.Length > 0 && PathUtil.IsSameOrInside(keep, path)) return "it is one of your main folders";
        if (env.UserProfile.Length > 0)
        {
            foreach (string personal in new[] { "Documents", "Desktop", "Pictures", "Music", "Videos", "OneDrive" })
                if (PathUtil.IsSameOrInside(VolumeIndex.Combine(env.UserProfile, personal), path)) return "it is one of your main folders";
        }
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 && (attributes & FileAttributes.Directory) != 0) return "it is a link to somewhere else";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "it can no longer be found"; }
        return null;
    }

    private sealed class Run
    {
        private readonly SizeTree _tree;
        private readonly UsageEnvironment _env;
        private readonly DateTime? _newest;
        private readonly IProgress<int>? _progress;
        private readonly CancellationToken _ct;
        private readonly List<int> _removed = new();
        private readonly List<string> _refused = new();
        private long _freed;
        private int _deleted, _left, _seen;

        public Run(SizeTree tree, UsageEnvironment env, int minAgeDays, IProgress<int>? progress, CancellationToken ct)
        {
            _tree = tree;
            _env = env;
            _newest = minAgeDays > 0 ? DateTime.UtcNow.AddDays(-minAgeDays) : null;
            _progress = progress;
            _ct = ct;
        }

        public CleanupOutcome Execute(IReadOnlyList<int> nodes, bool contentsOnly)
        {
            foreach (int node in nodes)
            {
                if (_ct.IsCancellationRequested) break;
                if (node <= SizeTree.Root || node >= _tree.Count || _tree.IsRemoved(node)) continue;
                string path = _tree.GetPath(node);
                if (Refusal(path, _tree.RootPath, _env) is { } reason)
                {
                    _refused.Add($"{path}: {reason}");
                    continue;
                }
                Delete(node, path, keep: contentsOnly && _tree.IsDirectory(node));
            }
            _progress?.Report(_seen);
            return new CleanupOutcome(_freed, _deleted, _left, _removed, _refused);
        }

        /// <summary>Deletes a file, or a folder's counted contents and then the folder if that left it empty. True when the item is gone.</summary>
        private bool Delete(int node, string path, bool keep)
        {
            if (!_tree.IsDirectory(node)) return DeleteFile(node, path);

            // Never through a link (a junction or symbolic link): what is behind it lives somewhere else and is not ours to delete.
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { _left++; return false; }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { _removed.Add(node); return true; } // already gone
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _left++; return false; }

            int mark = _removed.Count;
            bool emptied = true;
            foreach (int child in _tree.Children(node).ToArray())
            {
                if (_ct.IsCancellationRequested) return false;
                emptied &= Delete(child, VolumeIndex.Combine(path, _tree.Name(child)), keep: false);
            }
            if (keep || !emptied) return false;
            try
            {
                // Not recursive on purpose: if anything is still in there (files that arrived after measuring), the folder stays.
                Directory.Delete(path, recursive: false);
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            // The folder went whole: it stands for everything that was in it.
            _removed.RemoveRange(mark, _removed.Count - mark);
            _removed.Add(node);
            return true;
        }

        private bool DeleteFile(int node, string path)
        {
            if (++_seen % 250 == 0) _progress?.Report(_seen);
            try
            {
                if (_newest is { } newest)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && (info.LastWriteTimeUtc > newest || info.CreationTimeUtc > newest)) { _left++; return false; }
                }
                try { File.Delete(path); }
                catch (UnauthorizedAccessException)
                {
                    // Read-only files refuse to go; clearing the flag is all it takes.
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
            {
                _left++; // in use, or protected: leave it
                return false;
            }
            _freed += _tree.Size(node);
            _deleted++;
            _removed.Add(node);
            return true;
        }
    }
}
