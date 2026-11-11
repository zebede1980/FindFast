using System.Collections.Concurrent;
using System.Diagnostics;
using FastFind.Core.Indexing;

namespace FastFind.Core.Search;

public readonly record struct Hit(int Volume, int Index);

public enum SortField
{
    Name, Path, Size, Modified, Created, Extension,
    /// <summary>Shortest names first, newest first within a length: puts exact and near-exact matches at the top.</summary>
    Relevance,
}

public sealed class SearchResult
{
    public required VolumeIndex[] Volumes { get; init; }
    public required Hit[] Hits { get; init; }
    public int Count => Hits.Length;
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public SortField SortField { get; init; }
    public bool Descending { get; init; }

    public static readonly SearchResult Empty = new() { Volumes = Array.Empty<VolumeIndex>(), Hits = Array.Empty<Hit>() };
}

/// <summary>
/// Linear parallel scan over every indexed name. With ~1M entries this takes a few milliseconds,
/// so no inverted index is needed; the trick is keeping the data compact and the hot loop tight.
/// </summary>
public static class SearchEngine
{
    private const int Partition = 32 * 1024;

    public static SearchResult Search(IReadOnlyList<VolumeIndex> volumes, SearchQuery query, SortField sort = SortField.Name, bool descending = false, CancellationToken ct = default, bool foldersFirst = false)
    {
        var sw = Stopwatch.StartNew();
        var vols = volumes.ToArray();
        foreach (var v in vols) v.Lock.EnterReadLock();
        try
        {
            var perVolume = new int[vols.Length][];
            for (int vi = 0; vi < vols.Length; vi++)
            {
                ct.ThrowIfCancellationRequested();
                perVolume[vi] = ScanVolume(vols[vi], query, ct);
            }

            Hit[] hits = sort == SortField.Name
                ? MergeByName(vols, perVolume, ct)
                : Concat(perVolume);

            if (sort != SortField.Name) SortHits(vols, hits, sort, ct);
            if (descending) Array.Reverse(hits);
            if (foldersFirst) hits = FoldersFirst(vols, hits);

            return new SearchResult { Volumes = vols, Hits = hits, Elapsed = sw.Elapsed, Error = query.Error, SortField = sort, Descending = descending };
        }
        finally
        {
            foreach (var v in vols) v.Lock.ExitReadLock();
        }
    }

    private static int[] ScanVolume(VolumeIndex volume, SearchQuery query, CancellationToken ct)
    {
        int[] order = volume.EnsureNameOrder();
        var names = volume.NamesArray;
        int root = volume.RootIndex;
        int total = order.Length;
        if (total == 0) return Array.Empty<int>();

        var buckets = new ConcurrentBag<(int Start, List<int> Items)>();
        var options = new ParallelOptions { CancellationToken = ct };
        Parallel.ForEach(Partitioner.Create(0, total, Partition), options, range =>
        {
            var list = new List<int>();
            for (int pos = range.Item1; pos < range.Item2; pos++)
            {
                int idx = order[pos];
                var name = names[idx];
                if (name == null || idx == root) continue;
                if (query.Matches(volume, idx, name)) list.Add(idx);
            }
            if (list.Count > 0) buckets.Add((range.Item1, list));
        });

        var sorted = buckets.OrderBy(b => b.Start).ToArray();
        int count = 0;
        foreach (var b in sorted) count += b.Items.Count;
        var result = new int[count];
        int o = 0;
        foreach (var b in sorted)
        {
            b.Items.CopyTo(result, o);
            o += b.Items.Count;
        }
        return result;
    }

    private static Hit[] Concat(int[][] perVolume)
    {
        int total = 0;
        foreach (var p in perVolume) total += p.Length;
        var hits = new Hit[total];
        int o = 0;
        for (int vi = 0; vi < perVolume.Length; vi++)
            foreach (int i in perVolume[vi]) hits[o++] = new Hit(vi, i);
        return hits;
    }

    /// <summary>Each volume's hits are already in name order; merge them without a full sort.</summary>
    private static Hit[] MergeByName(VolumeIndex[] vols, int[][] perVolume, CancellationToken ct)
    {
        if (vols.Length == 1)
        {
            var single = new Hit[perVolume[0].Length];
            for (int i = 0; i < single.Length; i++) single[i] = new Hit(0, perVolume[0][i]);
            return single;
        }
        int total = 0;
        foreach (var p in perVolume) total += p.Length;
        var hits = new Hit[total];
        var heads = new int[vols.Length];
        for (int o = 0; o < total; o++)
        {
            if ((o & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int best = -1;
            string? bestName = null;
            for (int vi = 0; vi < vols.Length; vi++)
            {
                if (heads[vi] >= perVolume[vi].Length) continue;
                var name = vols[vi].NamesArray[perVolume[vi][heads[vi]]];
                if (best < 0 || string.Compare(name, bestName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    best = vi;
                    bestName = name;
                }
            }
            hits[o] = new Hit(best, perVolume[best][heads[best]++]);
        }
        return hits;
    }

    /// <summary>Stable partition: directories ahead of files, each group keeping its sorted order (Explorer-style listing).</summary>
    private static Hit[] FoldersFirst(VolumeIndex[] vols, Hit[] hits)
    {
        var output = new Hit[hits.Length];
        int o = 0;
        foreach (var h in hits) if (vols[h.Volume].MetaArray[h.Index].IsDirectory) output[o++] = h;
        if (o == 0 || o == hits.Length) return hits;
        foreach (var h in hits) if (!vols[h.Volume].MetaArray[h.Index].IsDirectory) output[o++] = h;
        return output;
    }

    private static void SortHits(VolumeIndex[] vols, Hit[] hits, SortField sort, CancellationToken ct)
    {
        if (hits.Length < 2) return;
        Comparison<Hit> cmp = sort switch
        {
            SortField.Size => (a, b) => vols[a.Volume].MetaArray[a.Index].Size.CompareTo(vols[b.Volume].MetaArray[b.Index].Size),
            SortField.Modified => (a, b) => vols[a.Volume].MetaArray[a.Index].ModifiedFileTime.CompareTo(vols[b.Volume].MetaArray[b.Index].ModifiedFileTime),
            SortField.Created => (a, b) => vols[a.Volume].MetaArray[a.Index].CreatedFileTime.CompareTo(vols[b.Volume].MetaArray[b.Index].CreatedFileTime),
            SortField.Extension => (a, b) => Extension(vols[a.Volume].NamesArray[a.Index]).CompareTo(Extension(vols[b.Volume].NamesArray[b.Index]), StringComparison.OrdinalIgnoreCase),
            SortField.Path => (a, b) => string.Compare(vols[a.Volume].GetParentPath(a.Index), vols[b.Volume].GetParentPath(b.Index), StringComparison.OrdinalIgnoreCase),
            SortField.Relevance => (a, b) =>
            {
                int c = (vols[a.Volume].NamesArray[a.Index]?.Length ?? 0).CompareTo(vols[b.Volume].NamesArray[b.Index]?.Length ?? 0);
                return c != 0 ? c : vols[b.Volume].MetaArray[b.Index].ModifiedFileTime.CompareTo(vols[a.Volume].MetaArray[a.Index].ModifiedFileTime);
            },
            _ => (a, b) => string.Compare(vols[a.Volume].NamesArray[a.Index], vols[b.Volume].NamesArray[b.Index], StringComparison.OrdinalIgnoreCase),
        };
        // Stable tie-break on name keeps the list deterministic.
        Comparison<Hit> withTie = (a, b) =>
        {
            int c = cmp(a, b);
            return c != 0 ? c : string.Compare(vols[a.Volume].NamesArray[a.Index], vols[b.Volume].NamesArray[b.Index], StringComparison.OrdinalIgnoreCase);
        };

        if (sort == SortField.Path)
        {
            // Path comparisons are costly: precompute keys once.
            var keys = new string[hits.Length];
            Parallel.For(0, hits.Length, i => keys[i] = vols[hits[i].Volume].GetParentPath(hits[i].Index));
            var idx = new int[hits.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            ParallelSort.Sort(idx, (a, b) =>
            {
                int c = string.Compare(keys[a], keys[b], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(vols[hits[a].Volume].NamesArray[hits[a].Index], vols[hits[b].Volume].NamesArray[hits[b].Index], StringComparison.OrdinalIgnoreCase);
            });
            var copy = (Hit[])hits.Clone();
            for (int i = 0; i < idx.Length; i++) hits[i] = copy[idx[i]];
            return;
        }
        Array.Sort(hits, withTie);
        ct.ThrowIfCancellationRequested();
    }

    private static ReadOnlySpan<char> Extension(string? name)
    {
        if (name == null) return default;
        int dot = name.LastIndexOf('.');
        return dot < 0 ? default : name.AsSpan(dot + 1);
    }
}
