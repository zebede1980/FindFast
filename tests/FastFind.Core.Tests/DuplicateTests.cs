using System.Runtime.InteropServices;
using FastFind.Core.Duplicates;
using FastFind.Core.Indexing;
using FastFind.Core.Usage;
using Xunit;

namespace FastFind.Core.Tests;

/// <summary>The duplicate finder, run for real against files in a temporary folder.</summary>
public sealed class DuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFind.Dupes." + Guid.NewGuid().ToString("N"));
    private static readonly UsageEnvironment NoSystemFolders = new();

    public DuplicateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, byte[] content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private DuplicateResult Find(long minSize = 1)
        => DuplicateFinder.Find(DuplicateCandidates.FromDisk(_root, minSize, NoSystemFolders).Files, minSize);

    private static string[] Names(DuplicateGroup group) => group.Files.Select(f => f.Name).ToArray();

    [Fact]
    public void FilesWithTheSameContentAreGroupedWhateverTheyAreCalled()
    {
        var big = Bytes(300_000, 1);
        Write(@"a\report.bin", big);
        Write(@"b\copy of report.bin", big);
        Write(@"b\deep\third.dat", big);
        var small = Bytes(500, 2);
        Write(@"a\note.txt", small);
        Write(@"c\note (1).txt", small);
        Write(@"c\different.txt", Bytes(500, 3));   // same size as the notes, other content
        Write(@"c\alone.bin", Bytes(1234, 4));      // nothing else is this size

        var result = Find();

        Assert.Equal(2, result.Groups.Count);
        // The group wasting the most comes first.
        Assert.Equal(300_000, result.Groups[0].Size);
        Assert.Equal(new[] { "report.bin", "copy of report.bin", "third.dat" }, Names(result.Groups[0]));
        Assert.Equal(600_000, result.Groups[0].Wasted);
        Assert.Equal(new[] { "note.txt", "note (1).txt" }, Names(result.Groups[1]));
        Assert.Equal(600_500, result.Wasted);
        Assert.Equal(3, result.Surplus);
        Assert.Equal(6, result.Compared); // alone.bin was never opened
        Assert.Equal(0, result.Unreadable);
    }

    [Fact]
    public void FilesThatDifferOnlyInTheMiddleAreNotDuplicates()
    {
        // Same size, same first and last 64 KB: only reading the whole file tells them apart.
        var one = Bytes(400_000, 5);
        var two = (byte[])one.Clone();
        two[200_000] ^= 0xFF;
        Write("one.bin", one);
        Write("two.bin", two);
        Write("twin of one.bin", one);

        var result = Find();

        var group = Assert.Single(result.Groups);
        Assert.Equal(new[] { "one.bin", "twin of one.bin" }, Names(group));
    }

    [Fact]
    public void EmptyAndSmallFilesAreLeftOutWhenAsked()
    {
        Write("empty1.txt", Array.Empty<byte>());
        Write("empty2.txt", Array.Empty<byte>());
        var small = Bytes(2000, 6);
        Write("small1.bin", small);
        Write("small2.bin", small);
        var large = Bytes(150_000, 7);
        Write("large1.bin", large);
        Write("large2.bin", large);

        // Empty files are all alike and waste nothing: never reported.
        Assert.Equal(new long[] { 150_000, 2000 }, Find().Groups.Select(g => g.Size).ToArray());
        Assert.Equal(new long[] { 150_000 }, Find(minSize: 100_000).Groups.Select(g => g.Size).ToArray());
    }

    [Fact]
    public void AFileListedTwiceIsNotItsOwnDuplicate()
    {
        string path = Write("only.bin", Bytes(5000, 8));
        var result = DuplicateFinder.Find(new[] { new DuplicateCandidate(path, 5000), new DuplicateCandidate(path.ToUpperInvariant(), 5000) });
        Assert.Empty(result.Groups);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);

    [Fact]
    public void TwoNamesForOneFileAreNotDuplicates()
    {
        var content = Bytes(200_000, 9);
        string original = Write("original.bin", content);
        string link = Path.Combine(_root, "second name.bin");
        Assert.True(CreateHardLinkW(link, original, IntPtr.Zero), "hard link could not be made");

        // Deleting either name would free nothing.
        var linked = Find();
        Assert.Empty(linked.Groups);
        Assert.Equal(1, linked.Links);

        // A real copy is a duplicate of the file, counted once.
        Write("real copy.bin", content);
        var group = Assert.Single(Find().Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains("real copy.bin", Names(group));
    }

    [Fact]
    public void FilesThatCannotBeReadAreLeftOutAndCounted()
    {
        var content = Bytes(100_000, 10);
        Write("free1.bin", content);
        Write("free2.bin", content);
        string locked = Write("locked.bin", content);
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = Find();
            var group = Assert.Single(result.Groups);
            Assert.Equal(new[] { "free1.bin", "free2.bin" }, Names(group));
            Assert.Equal(1, result.Unreadable);
        }
    }

    [Fact]
    public void ProgressEndsWithEverythingDoneAndCancellingStopsTheSearch()
    {
        var content = Bytes(300_000, 11);
        for (int i = 0; i < 6; i++) Write($"f{i}.bin", content);
        var reports = new List<DuplicateProgress>();
        var result = DuplicateFinder.Find(DuplicateCandidates.FromDisk(_root, 1, NoSystemFolders).Files, 1, new SyncProgress(reports));

        Assert.Equal(6, Assert.Single(result.Groups).Files.Count);
        var sampled = reports.Last(r => r.Stage == DuplicateStage.Sampling);
        Assert.Equal((6, 6), (sampled.FilesDone, sampled.FilesTotal));
        var read = reports[^1];
        Assert.Equal(DuplicateStage.Reading, read.Stage);
        Assert.Equal((1_800_000L, 1_800_000L), (read.BytesDone, read.BytesTotal));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateFinder.Find(DuplicateCandidates.FromDisk(_root, 1, NoSystemFolders).Files, 1, null, cancelled.Token));
    }

    private sealed class SyncProgress : IProgress<DuplicateProgress>
    {
        private readonly List<DuplicateProgress> _reports;
        public SyncProgress(List<DuplicateProgress> reports) => _reports = reports;
        public void Report(DuplicateProgress value) { lock (_reports) _reports.Add(value); }
    }

    // ---- which files are compared ----

    [Fact]
    public void ReadingTheDiskLeavesOutSystemFoldersAndCountsThem()
    {
        var content = Bytes(3000, 12);
        Write(@"Windows\System32\a.dll", content);
        Write(@"Windows\WinSxS\a.dll", content);
        Write(@"Users\joe\a.dll", content);
        Write(@"Users\joe\tiny.txt", Bytes(10, 13));
        var env = new UsageEnvironment { Windows = Path.Combine(_root, "Windows") };

        var set = DuplicateCandidates.FromDisk(_root, 1000, env);

        Assert.Equal(new[] { Path.Combine(_root, @"Users\joe\a.dll") }, set.Files.Select(f => f.Path).ToArray());
        Assert.Equal(3, set.Considered);
        Assert.Equal(2, set.Protected);
        // With nothing protected, the same folder gives one group of three.
        Assert.Equal(3, Assert.Single(Find(1000).Groups).Files.Count);
    }

    [Fact]
    public void TheRecycleBinAndRestorePointsAreProtectedOnEveryDrive()
    {
        var env = new UsageEnvironment { Windows = @"C:\Windows", ProgramFiles = @"C:\Program Files" };
        Assert.True(DuplicateCandidates.IsProtected(@"D:\$RECYCLE.BIN\S-1-5-21\$R1234.zip", env));
        Assert.True(DuplicateCandidates.IsProtected(@"E:\System Volume Information", env));
        Assert.True(DuplicateCandidates.IsProtected(@"C:\Windows\System32", env));
        Assert.True(DuplicateCandidates.IsProtected(@"C:\Program Files\App\bin", env));
        Assert.False(DuplicateCandidates.IsProtected(@"D:\Photos\$RECYCLE.BIN", env));
        Assert.False(DuplicateCandidates.IsProtected(@"C:\Users\joe\Windows", env));
        Assert.False(DuplicateCandidates.IsProtected(@"C:\", env));
    }

    [Fact]
    public async Task TheIndexGivesOnlyFilesThatShareASize()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        int data = idx.Add(0, "data", idx.RootIndex, FileAttributes.Directory, 0, 1, 1);
        int sub = idx.Add(0, "sub", data, FileAttributes.Directory, 0, 1, 1);
        idx.Add(0, "a.bin", data, FileAttributes.Normal, 5000, 1, 1);
        idx.Add(0, "b.bin", sub, FileAttributes.Normal, 5000, 1, 1);
        idx.Add(0, "c.bin", sub, FileAttributes.Normal, 7000, 1, 1);          // the only one of its size
        idx.Add(0, "cloud.bin", sub, FileAttributes.Offline, 5000, 1, 1);     // not on this computer
        idx.Add(0, "x.txt", data, FileAttributes.Normal, 50, 1, 1);           // too small
        idx.Add(0, "y.txt", data, FileAttributes.Normal, 50, 1, 1);
        int windows = idx.Add(0, "Windows", idx.RootIndex, FileAttributes.Directory, 0, 1, 1);
        idx.Add(0, "w1.dll", windows, FileAttributes.Normal, 5000, 1, 1);
        idx.Add(0, "elsewhere.bin", idx.RootIndex, FileAttributes.Normal, 5000, 1, 1);
        idx.UpdateFolderSizes();
        var service = new FakeIndexService(idx);
        var env = new UsageEnvironment { Windows = @"C:\Windows" };

        var inData = await DuplicateCandidates.FromIndexAsync(service, @"C:\data", 1000, env);
        Assert.NotNull(inData);
        Assert.Equal(new[] { @"C:\data\a.bin", @"C:\data\sub\b.bin" }, inData!.Files.Select(f => f.Path).Order().ToArray());
        Assert.Equal(1, inData.OnlineOnly);
        Assert.Equal(0, inData.Protected);

        var wholeDrive = await DuplicateCandidates.FromIndexAsync(service, @"C:\", 1000, env);
        Assert.Equal(new[] { @"C:\data\a.bin", @"C:\data\sub\b.bin", @"C:\elsewhere.bin" }, wholeDrive!.Files.Select(f => f.Path).Order().ToArray());
        Assert.Equal(1, wholeDrive.Protected);

        var small = await DuplicateCandidates.FromIndexAsync(service, @"C:\data", 1, env);
        Assert.Equal(4, small!.Files.Count);

        // Indexed, but nothing that big: an empty answer, not "read the disk instead".
        var none = await DuplicateCandidates.FromIndexAsync(service, @"C:\data", 1_000_000, env);
        Assert.NotNull(none);
        Assert.Empty(none!.Files);
        // Nothing indexed there at all: the caller is told to read the disk.
        Assert.Null(await DuplicateCandidates.FromIndexAsync(service, @"C:\nowhere", 1000, env));
    }

    // ---- deleting ----

    [Fact]
    public void ADeleteIsCheckedAgainAndNeverTakesTheLastGoodCopy()
    {
        var content = Bytes(50_000, 14);
        string keep = Write(@"g1\keep.bin", content), spare = Write(@"g1\spare.bin", content), edited = Write(@"g1\edited.bin", content);
        var other = Bytes(60_000, 15);
        string keep2 = Write(@"g2\keep.bin", other), spare2 = Write(@"g2\spare.bin", other);
        var result = Find();
        Assert.Equal(2, result.Groups.Count);

        // Since the comparison: one ticked file was edited, and the copy that was to be kept from the other group has changed.
        File.WriteAllBytes(edited, Bytes(50_000, 16));
        File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(1)); // the clock is too coarse to be sure a rewrite this quick shows
        File.AppendAllText(keep2, "changed");

        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { spare, edited, spare2 };
        var plan = DuplicateDeletePlan.For(result.Groups, ticked);

        Assert.Equal(new[] { spare }, plan.Delete.ToArray());
        Assert.Equal(2, plan.Left.Count);
        Assert.Contains(plan.Left, l => l.StartsWith(edited) && l.Contains("changed"));
        Assert.Contains(plan.Left, l => l.StartsWith(spare2) && l.Contains("no unchanged copy"));
        // Both changed files are reported, the ticked one and the one that was being kept, so the list can drop them.
        Assert.Equal(new[] { edited, keep2 }, plan.Changed.Order().ToArray());
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void TickingEveryCopyDeletesNone()
    {
        var content = Bytes(20_000, 17);
        string a = Write("a.bin", content), b = Write("b.bin", content);
        var result = Find();
        var plan = DuplicateDeletePlan.For(result.Groups, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b });
        Assert.Empty(plan.Delete);
        Assert.Equal(2, plan.Left.Count);
    }
}
