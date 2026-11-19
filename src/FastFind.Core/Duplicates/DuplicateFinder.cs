using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FastFind.Core.Usage;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Core.Duplicates;

/// <summary>A file that might have a twin somewhere. All that is known so far is where it is and how big it is.</summary>
public readonly record struct DuplicateCandidate(string Path, long Size);

/// <summary>One copy of a duplicated file, as it was when its content was read.</summary>
public sealed record DuplicateFile(string Path, long Size, DateTime ModifiedUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Files whose content is the same, byte for byte.</summary>
public sealed class DuplicateGroup
{
    public DuplicateGroup(long size, IReadOnlyList<DuplicateFile> files)
    {
        Size = size;
        Files = files;
    }

    /// <summary>The size of each copy.</summary>
    public long Size { get; }
    public IReadOnlyList<DuplicateFile> Files { get; }
    /// <summary>What would be freed by keeping one copy and deleting the rest.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

public enum DuplicateStage
{
    /// <summary>Reading the start and end of every file that shares its size with another.</summary>
    Sampling,
    /// <summary>Reading in full the files whose samples matched.</summary>
    Reading,
}

/// <summary>How far the search has got. The byte counts are only set while <see cref="DuplicateStage.Reading"/>.</summary>
public readonly record struct DuplicateProgress(DuplicateStage Stage, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed class DuplicateResult
{
    /// <summary>The groups, the one wasting the most space first.</summary>
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }
    /// <summary>Files that shared a size with another file and so had their content looked at.</summary>
    public int Compared { get; init; }
    /// <summary>Files that could not be opened or read, or that changed while they were being read. They are left out.</summary>
    public int Unreadable { get; init; }
    /// <summary>Files whose content is not on this computer (online-only OneDrive files). They are never downloaded to compare them.</summary>
    public int OnlineOnly { get; init; }
    /// <summary>Symbolic links, and second names of a file that has several (hard links). Deleting those frees nothing.</summary>
    public int Links { get; init; }
    public long BytesRead { get; init; }

    public long Wasted => Groups.Sum(g => g.Wasted);
    /// <summary>How many files could go while still leaving one copy of each.</summary>
    public int Surplus => Groups.Sum(g => g.Files.Count - 1);
}

/// <summary>
/// Finds files with identical content. Files can only be the same if they are the same size, so the sizes (which the
/// index already has) rule out nearly everything without touching the disk. What is left is narrowed by reading a
/// little from the start and end of each file, and only files that still match are read in full and compared by their
/// SHA-256. Nothing is called a duplicate on the strength of its name, size or date.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>How much is read from each end of a file for the first comparison.</summary>
    public const int SampleBytes = 64 * 1024;
    private const int ReadBuffer = 1024 * 1024;

    private sealed record Probe(string Path, long Length, DateTime ModifiedUtc, (uint Volume, ulong File)? Identity, string Key, bool WholeFileRead);

    private sealed class Counters
    {
        public int Unreadable, OnlineOnly, Links, Done;
        public long Bytes;
    }

    /// <param name="candidates">The files to look through. Each path is considered once however often it is listed.</param>
    /// <param name="minSize">Files smaller than this are ignored. Empty files always are: they are all "the same" and waste nothing.</param>
    /// <param name="parallelism">How many files to read at once; 0 picks a sensible number.</param>
    public static DuplicateResult Find(IEnumerable<DuplicateCandidate> candidates, long minSize = 1, IProgress<DuplicateProgress>? progress = null,
        CancellationToken ct = default, int parallelism = 0)
    {
        minSize = Math.Max(1, minSize);
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism > 0 ? parallelism : Math.Clamp(Environment.ProcessorCount, 1, 4) };
        var counters = new Counters();
        var throttle = Stopwatch.StartNew();
        long lastReport = -1000;

        // 1. Only files that share a size with another file can have a twin.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<string>>();
        foreach (var c in candidates)
        {
            if (c.Size < minSize || c.Path.Length == 0 || !seen.Add(c.Path)) continue;
            if (!bySize.TryGetValue(c.Size, out var list)) bySize[c.Size] = list = new List<string>();
            list.Add(c.Path);
        }
        var suspects = bySize.Values.Where(l => l.Count > 1).SelectMany(l => l).ToArray();
        ct.ThrowIfCancellationRequested();

        // 2. A sample from each end. Small files are read whole here, which settles them.
        var probes = new ConcurrentBag<Probe>();
        Parallel.ForEach(suspects, options, path =>
        {
            if (Sample(path, minSize, counters) is { } probe) probes.Add(probe);
            int done = Interlocked.Increment(ref counters.Done);
            Report(DuplicateStage.Sampling, done, suspects.Length, 0, 0, force: false);
        });
        Report(DuplicateStage.Sampling, suspects.Length, suspects.Length, 0, 0, force: true);

        var settled = new List<List<Probe>>();
        var toRead = new List<List<Probe>>();
        foreach (var group in probes.GroupBy(p => p.Key))
        {
            var files = DistinctFiles(group, counters);
            if (files.Count < 2) continue;
            (files[0].WholeFileRead ? settled : toRead).Add(files);
        }

        // 3. Whatever still matches is read in full.
        var pending = toRead.SelectMany(g => g).ToArray();
        long bytesTotal = pending.Sum(p => p.Length);
        var hashes = new ConcurrentDictionary<Probe, string>();
        counters.Done = 0;
        Parallel.ForEach(pending, options, probe =>
        {
            string? hash = HashWhole(probe, counters, ct, () => Report(DuplicateStage.Reading, counters.Done, pending.Length, Interlocked.Read(ref counters.Bytes), bytesTotal, force: false));
            if (hash != null) hashes[probe] = hash;
            Interlocked.Increment(ref counters.Done);
        });
        if (pending.Length > 0) Report(DuplicateStage.Reading, pending.Length, pending.Length, bytesTotal, bytesTotal, force: true);

        foreach (var group in toRead)
            foreach (var same in group.Where(hashes.ContainsKey).GroupBy(p => hashes[p]))
                if (same.Count() > 1) settled.Add(same.ToList());

        var groups = settled
            .Select(g => new DuplicateGroup(g[0].Length, g.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(p => new DuplicateFile(p.Path, p.Length, p.ModifiedUtc)).ToList()))
            .OrderByDescending(g => g.Wasted).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DuplicateResult
        {
            Groups = groups, Compared = suspects.Length, Unreadable = counters.Unreadable, OnlineOnly = counters.OnlineOnly, Links = counters.Links,
            BytesRead = Interlocked.Read(ref counters.Bytes),
        };

        void Report(DuplicateStage stage, int done, int total, long bytes, long bytesOf, bool force)
        {
            if (progress == null) return;
            long now = throttle.ElapsedMilliseconds;
            if (!force)
            {
                // Often enough to look alive, not so often that the window spends its time redrawing a number.
                long last = Interlocked.Read(ref lastReport);
                if (now - last < 100 || Interlocked.CompareExchange(ref lastReport, now, last) != last) return;
            }
            progress.Report(new DuplicateProgress(stage, done, total, bytes, bytesOf));
        }
    }

    /// <summary>
    /// Two names for one file (hard links) are not duplicates: they share the same space on the disk, and deleting one
    /// frees nothing. Only the first name of each is kept.
    /// </summary>
    private static List<Probe> DistinctFiles(IEnumerable<Probe> group, Counters counters)
    {
        var files = new List<Probe>();
        var identities = new HashSet<(uint, ulong)>();
        foreach (var probe in group.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (probe.Identity is { } id && !identities.Add(id)) { counters.Links++; continue; }
            files.Add(probe);
        }
        return files;
    }

    /// <summary>Opens a file and reads enough of it to tell it from most others of its size. Null when it is to be left out.</summary>
    private static Probe? Sample(string path, long minSize, Counters counters)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return null;
            // Reading an online-only file would download it. Whole libraries would come down just to be compared.
            if (IsOnlineOnly(attributes)) { Interlocked.Increment(ref counters.OnlineOnly); return null; }
            // A symbolic link is another way to reach a file, not a copy of it. (Other reparse points, such as files kept in
            // sync by OneDrive, are ordinary files as far as their content goes.)
            if ((attributes & FileAttributes.ReparsePoint) != 0 && File.ResolveLinkTarget(path, returnFinalTarget: false) != null)
            {
                Interlocked.Increment(ref counters.Links);
                return null;
            }

            using var handle = Open(path);
            long length = RandomAccess.GetLength(handle);
            if (length < minSize) return null; // shrank since it was listed
            var modified = File.GetLastWriteTimeUtc(handle);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool whole = length <= SampleBytes * 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SampleBytes * 2);
            try
            {
                if (whole)
                {
                    if (!ReadExactly(handle, buffer.AsSpan(0, (int)length), 0)) return Unreadable(counters);
                    hash.AppendData(buffer, 0, (int)length);
                    Interlocked.Add(ref counters.Bytes, length);
                }
                else
                {
                    if (!ReadExactly(handle, buffer.AsSpan(0, SampleBytes), 0) || !ReadExactly(handle, buffer.AsSpan(SampleBytes, SampleBytes), length - SampleBytes))
                        return Unreadable(counters);
                    hash.AppendData(buffer, 0, SampleBytes * 2);
                    Interlocked.Add(ref counters.Bytes, SampleBytes * 2);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            // The length is part of the key so that a sample from a long file never matches a short file read whole.
            string key = $"{length}:{Convert.ToHexString(hash.GetHashAndReset())}";
            return new Probe(path, length, modified, Identity(handle), key, whole);
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            return Unreadable(counters);
        }
    }

    /// <summary>SHA-256 of the whole file, or null if it could not be read or was changed in the meantime.</summary>
    private static string? HashWhole(Probe probe, Counters counters, CancellationToken ct, Action tick)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBuffer);
        try
        {
            using var handle = Open(probe.Path);
            if (RandomAccess.GetLength(handle) != probe.Length) return Changed();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < probe.Length)
            {
                ct.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(ReadBuffer, probe.Length - offset)), offset);
                if (read <= 0) return Changed();
                hash.AppendData(buffer, 0, read);
                offset += read;
                Interlocked.Add(ref counters.Bytes, read);
                tick();
            }
            // Written to while it was being read: what was hashed may be neither the old content nor the new.
            if (RandomAccess.GetLength(handle) != probe.Length || File.GetLastWriteTimeUtc(handle) != probe.ModifiedUtc) return Changed();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            return Changed();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        string? Changed()
        {
            Interlocked.Increment(ref counters.Unreadable);
            return null;
        }
    }

    private static Probe? Unreadable(Counters counters)
    {
        Interlocked.Increment(ref counters.Unreadable);
        return null;
    }

    /// <summary>Opened for reading only, and without getting in the way of whatever else has the file open.</summary>
    private static SafeFileHandle Open(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);

    private static bool ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0) return false;
            buffer = buffer[read..];
            offset += read;
        }
        return true;
    }

    private static bool IsFileTrouble(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException;

    /// <summary>True for a file whose content is not on this computer: reading it would fetch it from the cloud.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes)
        => SizeTree.Builder.IsCloudOnly(attributes) || (attributes & (FileAttributes)0x00040000 /* RECALL_ON_OPEN */) != 0;

    // ---- which file a name refers to ----

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>The file behind the name, when it has more than one name. Null for an ordinary file, which cannot be confused with another.</summary>
    private static (uint Volume, ulong File)? Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks < 2) return null;
        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return index == 0 ? null : (info.VolumeSerialNumber, index);
    }
}
