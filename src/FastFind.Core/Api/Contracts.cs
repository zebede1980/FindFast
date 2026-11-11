using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.Core.Api;

/// <summary>One file or folder as shown in a pane. A null <see cref="Name"/> means the entry was deleted after the search ran.</summary>
public readonly record struct FileEntry(string? Name, string Directory, long Size, long ModifiedFileTime, long CreatedFileTime, FileAttributes Attributes)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsDeleted => Name == null;
    public string FullPath => VolumeIndex.Combine(Directory, Name ?? "");
}

/// <summary>Everything needed to run a search, as plain data so it can cross a process boundary.</summary>
public sealed class SearchRequest
{
    public string Text { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool UseRegex { get; set; }
    public bool WholeWord { get; set; }
    public bool MatchPath { get; set; }

    public EntryKind Kind { get; set; } = EntryKind.Any;
    public string[]? Extensions { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? ModifiedFromUtc { get; set; }
    public DateTime? ModifiedToUtcExclusive { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtcExclusive { get; set; }
    /// <summary>Restrict results to this folder (see <see cref="ScopeRecursive"/>).</summary>
    public string? ScopeFolder { get; set; }
    public bool ScopeRecursive { get; set; } = true;
    public bool HideHiddenAndSystem { get; set; }

    public SortField Sort { get; set; } = SortField.Name;
    public bool Descending { get; set; }
    /// <summary>List folders ahead of files, each group in sort order.</summary>
    public bool FoldersFirst { get; set; }

    public QueryOptions ToOptions() => new() { MatchCase = MatchCase, UseRegex = UseRegex, WholeWord = WholeWord, MatchPath = MatchPath };

    public FilterSet ToFilters() => new()
    {
        Kind = Kind,
        Extensions = Extensions is { Length: > 0 } ? Extensions : null,
        MinSize = MinSize,
        MaxSize = MaxSize,
        ModifiedFromUtc = ModifiedFromUtc,
        ModifiedToUtcExclusive = ModifiedToUtcExclusive,
        CreatedFromUtc = CreatedFromUtc,
        CreatedToUtcExclusive = CreatedToUtcExclusive,
        ScopeFolder = ScopeFolder,
        ScopeRecursive = ScopeRecursive,
        HideHiddenAndSystem = HideHiddenAndSystem,
    };

    public SearchQuery ToQuery() => QueryParser.Parse(Text, ToOptions(), ToFilters());

    /// <summary>True when the request would return a location's contents unfiltered.</summary>
    public bool HasCriteria => !string.IsNullOrWhiteSpace(Text) || Kind != EntryKind.Any || Extensions is { Length: > 0 } || MinSize != null || MaxSize != null
        || ModifiedFromUtc != null || ModifiedToUtcExclusive != null || CreatedFromUtc != null || CreatedToUtcExclusive != null || HideHiddenAndSystem;
}

/// <summary>The rows a search produced. Rows are fetched in pages so a view only pays for what it shows.</summary>
public interface IResultSet : IDisposable
{
    int Count { get; }
    TimeSpan Elapsed { get; }
    string? Error { get; }
    /// <summary>Rows [offset, offset + count), clamped to the result. Never throws for a stale or released result: missing rows come back deleted.</summary>
    FileEntry[] GetRows(int offset, int count);
}

/// <summary>Snapshot of one indexed volume's state.</summary>
public sealed class VolumeInfo
{
    public string Root { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public VolumeStatus Status { get; init; }
    public IndexMode Mode { get; init; }
    public long Entries { get; init; }
    public string? Error { get; init; }
    public string ProgressText { get; init; } = "";
    public TimeSpan BuildTime { get; init; }
    public bool LiveUpdates { get; init; }
    public bool IsNetwork { get; init; }
    public DateTime? IndexedAtUtc { get; init; }
    public DateTime? NextScanUtc { get; init; }

    /// <summary>True when <paramref name="path"/> lies on this volume and the volume can be searched.</summary>
    public bool Covers(string path)
        => Status == VolumeStatus.Ready && path.Length >= Root.TrimEnd('\\').Length
           && (path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || string.Equals(path, Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public static VolumeInfo From(VolumeState v) => new()
    {
        Root = v.Root, Label = v.Label, Format = v.Format, Status = v.Status, Mode = v.Mode, Entries = v.Entries, Error = v.Error,
        ProgressText = v.ProgressText, BuildTime = v.BuildTime, LiveUpdates = v.LiveUpdates, IsNetwork = v.IsNetwork,
        IndexedAtUtc = v.IndexedAtUtc, NextScanUtc = v.NextScanUtc,
    };
}

/// <summary>What to index. Sent by the UI so an index hosted in another process follows the user's settings.</summary>
public sealed class IndexSettings
{
    public bool IncludeRemovable { get; set; }
    public bool IncludeNetwork { get; set; }
    public List<string> ExcludedRoots { get; set; } = new();
    public List<string> NetworkLocations { get; set; } = new();
    public double NetworkRescanHours { get; set; } = 6;

    public void ApplyTo(IndexOptions options)
    {
        options.IncludeRemovable = IncludeRemovable;
        options.IncludeNetwork = IncludeNetwork;
        options.ExcludedRoots.Clear();
        foreach (var r in ExcludedRoots) options.ExcludedRoots.Add(r);
        options.NetworkLocations.Clear();
        options.NetworkLocations.AddRange(NetworkLocations);
        options.NetworkRescanInterval = TimeSpan.FromHours(Math.Clamp(NetworkRescanHours, 0.25, 24 * 30));
    }
}

/// <summary>
/// The index as the UI sees it. Implemented in-process by <see cref="LocalIndexService"/> and across a named pipe by
/// <see cref="PipeIndexClient"/>, so the UI does not care which process owns the index.
/// </summary>
public interface IIndexService : IDisposable
{
    /// <summary>True when the index lives in another process.</summary>
    bool IsRemote { get; }
    /// <summary>True when the process that owns the index is elevated, i.e. NTFS volumes are read from the MFT.</summary>
    bool IsElevated { get; }
    IReadOnlyList<VolumeInfo> Volumes { get; }
    /// <summary>Raised on a background thread when volumes appear, disappear or change state.</summary>
    event Action? VolumesChanged;
    /// <summary>Raised (throttled) on a background thread when indexed content changes; consumers re-run their searches.</summary>
    event Action? IndexChanged;

    Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default);
    /// <summary>Total size of each folder directly inside <paramref name="folder"/>, keyed by name. Empty when the folder is not indexed.</summary>
    Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default);
    void Configure(IndexSettings settings);
    /// <summary>Rebuilds one volume, or every volume when <paramref name="root"/> is null.</summary>
    void Rebuild(string? root);
    /// <summary>Looks for volumes that arrived or left (USB, BitLocker unlock, mounted VHD).</summary>
    void RefreshVolumes();
}
