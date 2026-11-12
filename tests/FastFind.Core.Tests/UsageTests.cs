using FastFind.Core.Api;
using FastFind.Core.Indexing;
using FastFind.Core.Usage;
using Xunit;

namespace FastFind.Core.Tests;

public class UsageTests
{
    /// <summary>
    /// C:\
    ///   Users\joe\Documents\{a.txt 100, b.txt 250}
    ///   Users\joe\photo.jpg 1000
    ///   Windows\notepad.exe 5000
    ///   empty\
    /// </summary>
    private static SizeTree Sample()
    {
        var b = new SizeTree.Builder(@"C:\");
        int users = b.AddFolder(SizeTree.Root, "Users");
        int joe = b.AddFolder(users, "joe");
        int docs = b.AddFolder(joe, "Documents");
        b.AddFile(docs, "a.txt", 100);
        b.AddFile(docs, "b.txt", 250);
        b.AddFile(joe, "photo.jpg", 1000);
        int windows = b.AddFolder(SizeTree.Root, "Windows");
        b.AddFile(windows, "notepad.exe", 5000);
        b.AddFolder(SizeTree.Root, "empty");
        return b.Build();
    }

    private static string[] ChildNames(SizeTree tree, int node) => tree.Children(node).ToArray().Select(tree.Name).ToArray();

    [Fact]
    public void SizesAndFileCountsAddUpThroughEveryFolder()
    {
        var tree = Sample();
        Assert.Equal(6350, tree.Size(SizeTree.Root));
        Assert.Equal(4, tree.FileCount(SizeTree.Root));
        int users = tree.Find(@"C:\Users");
        Assert.Equal(1350, tree.Size(users));
        Assert.Equal(3, tree.FileCount(users));
        Assert.Equal(350, tree.Size(tree.Find(@"C:\Users\joe\Documents")));
        Assert.Equal(0, tree.Size(tree.Find(@"C:\empty")));
        Assert.True(tree.IsDirectory(users));
        Assert.False(tree.IsDirectory(tree.Find(@"C:\Windows\notepad.exe")));
    }

    [Fact]
    public void ChildrenAreLargestFirst()
    {
        var tree = Sample();
        Assert.Equal(new[] { "Windows", "Users", "empty" }, ChildNames(tree, SizeTree.Root));
        Assert.Equal(new[] { "photo.jpg", "Documents" }, ChildNames(tree, tree.Find(@"C:\Users\joe")));
        Assert.Equal(new[] { "b.txt", "a.txt" }, ChildNames(tree, tree.Find(@"C:\Users\joe\Documents")));
    }

    [Fact]
    public void PathsRoundTripAndAreCaseInsensitive()
    {
        var tree = Sample();
        int photo = tree.Find(@"c:\users\JOE\Photo.jpg");
        Assert.True(photo > 0);
        Assert.Equal(@"C:\Users\joe\photo.jpg", tree.GetPath(photo));
        Assert.Equal(@"C:\", tree.GetPath(SizeTree.Root));
        Assert.Equal(SizeTree.Root, tree.Find(@"C:\"));
        Assert.Equal(-1, tree.Find(@"C:\Users\nobody"));
        Assert.Equal(-1, tree.Find(@"D:\Users"));
        Assert.Equal(3, tree.Depth(photo));
        Assert.True(tree.IsSameOrInside(photo, tree.Find(@"C:\Users")));
        Assert.False(tree.IsSameOrInside(photo, tree.Find(@"C:\Windows")));
    }

    [Fact]
    public void ATreeOfASubfolderBuildsItsPathsFromThatFolder()
    {
        var b = new SizeTree.Builder(@"C:\Users\joe");
        int docs = b.AddFolder(SizeTree.Root, "Documents");
        b.AddFile(docs, "a.txt", 5);
        var tree = b.Build();
        Assert.Equal(@"C:\Users\joe\Documents\a.txt", tree.GetPath(tree.Find(@"C:\Users\joe\Documents\a.txt")));
        Assert.Equal(-1, tree.Find(@"C:\Users\joey\Documents"));
    }

    [Fact]
    public void RemovingAnItemShrinksEveryFolderAboveItAndReorders()
    {
        var tree = Sample();
        int version = tree.Version;
        int notepad = tree.Find(@"C:\Windows\notepad.exe");
        tree.Remove(notepad);
        Assert.True(tree.IsRemoved(notepad));
        Assert.True(tree.Version > version);
        Assert.Equal(1350, tree.Size(SizeTree.Root));
        Assert.Equal(3, tree.FileCount(SizeTree.Root));
        Assert.Equal(0, tree.Size(tree.Find(@"C:\Windows")));
        Assert.Equal(-1, tree.Find(@"C:\Windows\notepad.exe"));
        // Windows was the biggest folder; emptied, it drops below Users (ties are in name order).
        Assert.Equal(new[] { "Users", "empty", "Windows" }, ChildNames(tree, SizeTree.Root));

        // A whole folder goes with everything in it.
        tree.Remove(tree.Find(@"C:\Users\joe\Documents"));
        Assert.Equal(1000, tree.Size(SizeTree.Root));
        Assert.Equal(1, tree.FileCount(SizeTree.Root));
        Assert.Equal(new[] { "photo.jpg" }, ChildNames(tree, tree.Find(@"C:\Users\joe")));

        // The root stays, and removing twice changes nothing.
        tree.Remove(SizeTree.Root);
        tree.Remove(notepad);
        Assert.Equal(1000, tree.Size(SizeTree.Root));
    }

    [Fact]
    public void ExtensionsAreCountedAndGroupedIntoKinds()
    {
        var tree = Sample();
        var stats = tree.ExtensionStats();
        Assert.Equal(new[] { "exe", "jpg", "txt" }, stats.Select(s => s.Extension).ToArray());
        Assert.Equal(350, stats[2].Bytes);
        Assert.Equal(2, stats[2].Files);
        Assert.Equal(FileCategory.Applications, stats[0].Category);
        Assert.Equal(FileCategory.Images, stats[1].Category);
        Assert.Equal(FileCategory.Documents, stats[2].Category);
        Assert.Equal("txt", tree.Extension(tree.Find(@"C:\Users\joe\Documents\a.txt")));
        Assert.Equal("", tree.Extension(tree.Find(@"C:\Users")));

        // Limited to one folder.
        var inDocs = tree.ExtensionStats(tree.Find(@"C:\Users\joe\Documents"));
        Assert.Single(inDocs);
        Assert.Equal(stats[2].Id, inDocs[0].Id);
    }

    [Theory]
    [InlineData("DLL", FileCategory.System)]
    [InlineData("mp4", FileCategory.Media)]
    [InlineData("flac", FileCategory.Media)]
    [InlineData("zip", FileCategory.Archives)]
    [InlineData("vhdx", FileCategory.Data)]
    [InlineData("cs", FileCategory.Code)]
    [InlineData("ts", FileCategory.Code)]
    [InlineData("docx", FileCategory.Documents)]
    [InlineData("png", FileCategory.Images)]
    [InlineData("exe", FileCategory.Applications)]
    [InlineData("whatever", FileCategory.Other)]
    [InlineData("", FileCategory.Other)]
    public void ExtensionsMapToKinds(string extension, FileCategory expected)
        => Assert.Equal(expected, FileCategories.Of(extension));

    [Fact]
    public void OddNamesHaveNoExtension()
    {
        var b = new SizeTree.Builder(@"C:\");
        b.AddFile(SizeTree.Root, "README", 1);
        b.AddFile(SizeTree.Root, "trailing.", 1);
        b.AddFile(SizeTree.Root, "report.final version 2", 1);
        b.AddFile(SizeTree.Root, "Photo.JPG", 1);
        b.AddFile(SizeTree.Root, "other.jpg", 1);
        var tree = b.Build();
        Assert.Equal("", tree.Extension(tree.Find(@"C:\README")));
        Assert.Equal("", tree.Extension(tree.Find(@"C:\trailing.")));
        Assert.Equal("", tree.Extension(tree.Find(@"C:\report.final version 2")));
        // Case does not make a second extension.
        Assert.Equal("jpg", tree.Extension(tree.Find(@"C:\Photo.JPG")));
        Assert.Equal(tree.ExtensionId(tree.Find(@"C:\Photo.JPG")), tree.ExtensionId(tree.Find(@"C:\other.jpg")));
    }

    [Fact]
    public void OnlineOnlyFilesAreListedButTakeNoSpace()
    {
        var b = new SizeTree.Builder(@"C:\");
        b.AddEntry("local.mp4", @"C:\", 1000, 0, FileAttributes.Archive);
        b.AddEntry("cloud.mp4", @"C:\", 5000, 0, FileAttributes.Archive | FileAttributes.Offline);
        b.AddEntry("recall.mp4", @"C:\", 7000, 0, (FileAttributes)0x00400000);
        var tree = b.Build();
        Assert.Equal(1000, tree.Size(SizeTree.Root));
        Assert.Equal(3, tree.FileCount(SizeTree.Root));
        Assert.Equal(12000, tree.CloudBytes);
        Assert.Equal(2, tree.CloudFiles);
        Assert.True(tree.IsCloudOnly(tree.Find(@"C:\cloud.mp4")));
        Assert.False(tree.IsCloudOnly(tree.Find(@"C:\local.mp4")));
        Assert.Equal(0, tree.Size(tree.Find(@"C:\cloud.mp4")));
    }

    [Fact]
    public void EntriesKnownOnlyByPathCanArriveInAnyOrder()
    {
        var b = new SizeTree.Builder(@"C:\data");
        long stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
        // A file deep down arrives before any of the folders above it.
        Assert.True(b.AddEntry("deep.bin", @"C:\data\one\two", 300, 0, FileAttributes.Normal));
        Assert.True(b.AddEntry("top.bin", @"C:\data", 50, 0, FileAttributes.Normal));
        // The folders' own rows turn up afterwards and must not create duplicates.
        Assert.True(b.AddEntry("two", @"C:\data\one", 0, stamp, FileAttributes.Directory));
        Assert.True(b.AddEntry("one", @"C:\DATA", 0, stamp, FileAttributes.Directory));
        // Rows from outside the folder, or with no name (deleted since the search), are ignored.
        Assert.False(b.AddEntry("stray.bin", @"C:\elsewhere", 999, 0, FileAttributes.Normal));
        Assert.False(b.AddEntry("stray.bin", @"C:\database", 999, 0, FileAttributes.Normal));
        Assert.False(b.AddEntry(null, @"C:\data", 999, 0, FileAttributes.Normal));
        var tree = b.Build();

        Assert.Equal(350, tree.Size(SizeTree.Root));
        Assert.Equal(new[] { "one", "top.bin" }, ChildNames(tree, SizeTree.Root));
        int two = tree.Find(@"C:\data\one\two");
        Assert.Equal(300, tree.Size(two));
        Assert.Equal(stamp, tree.ModifiedFileTime(two));
        Assert.Equal(1, tree.ChildCount(tree.Find(@"C:\data\one")));
        Assert.Equal(5, tree.Count); // root, one, two and the two files: nothing doubled
    }

    [Fact]
    public async Task ReadsAFolderFromTheIndex()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        int users = idx.Add(0, "Users", idx.RootIndex, FileAttributes.Directory, 0, 1, 1);
        int joe = idx.Add(0, "joe", users, FileAttributes.Directory, 0, 1, 1);
        idx.Add(0, "photo.jpg", joe, FileAttributes.Normal, 1000, 1, 1);
        idx.Add(0, "cloud.mp4", joe, FileAttributes.Offline, 9000, 1, 1);
        int docs = idx.Add(0, "Documents", joe, FileAttributes.Directory, 0, 1, 1);
        idx.Add(0, "a.txt", docs, FileAttributes.Normal, 100, 1, 1);
        int windows = idx.Add(0, "Windows", idx.RootIndex, FileAttributes.Directory, 0, 1, 1);
        idx.Add(0, "notepad.exe", windows, FileAttributes.Normal, 5000, 1, 1);
        // Folder sizes held in the index must not be counted on top of the files that make them up.
        idx.UpdateFolderSizes();
        var service = new FakeIndexService(idx);

        var reports = new List<SizeTreeProgress>();
        var whole = await SizeTreeLoader.FromIndexAsync(service, @"C:\", new SyncProgress(reports));
        Assert.NotNull(whole);
        Assert.Equal(6100, whole!.Size(SizeTree.Root));
        Assert.Equal(new[] { "Windows", "Users" }, ChildNames(whole, SizeTree.Root));
        Assert.Equal(9000, whole.CloudBytes);
        Assert.NotEmpty(reports);
        Assert.Equal(reports[^1].Total, reports[^1].Done);

        var part = await SizeTreeLoader.FromIndexAsync(service, @"C:\Users\joe");
        Assert.NotNull(part);
        Assert.Equal(1100, part!.Size(SizeTree.Root));
        Assert.Equal(new[] { "photo.jpg", "Documents", "cloud.mp4" }, ChildNames(part, SizeTree.Root));
        Assert.Equal(@"C:\Users\joe\Documents\a.txt", part.GetPath(part.Find(@"C:\Users\joe\Documents\a.txt")));

        // Nothing indexed there: the caller is told to read the disk instead.
        Assert.Null(await SizeTreeLoader.FromIndexAsync(service, @"C:\nowhere"));
    }

    private sealed class SyncProgress : IProgress<SizeTreeProgress>
    {
        private readonly List<SizeTreeProgress> _reports;
        public SyncProgress(List<SizeTreeProgress> reports) => _reports = reports;
        public void Report(SizeTreeProgress value) => _reports.Add(value);
    }

    [Fact]
    public async Task ReadsTheSameTreeThroughThePipe()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        int data = idx.Add(0, "data", idx.RootIndex, FileAttributes.Directory, 0, 1, 1);
        for (int i = 0; i < 300; i++) idx.Add(0, $"file{i:D3}.bin", data, FileAttributes.Normal, i, 1, 1);
        string name = "FastFind.Test." + Guid.NewGuid().ToString("N");
        using var server = new PipeIndexServer(new FakeIndexService(idx), name);
        server.Start();
        using var client = PipeIndexClient.TryConnect(name, 3000);
        Assert.NotNull(client);

        var tree = await SizeTreeLoader.FromIndexAsync(client!, @"C:\");
        Assert.NotNull(tree);
        Assert.Equal(300 * 299 / 2, tree!.Size(SizeTree.Root));
        Assert.Equal(300, tree.FileCount(tree.Find(@"C:\data")));
        Assert.Equal("file299.bin", tree.Name(tree.Children(tree.Find(@"C:\data"))[0]));
    }

    [Fact]
    public void ReadsAFolderFromDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "FastFind.UsageTests." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub", "inner"));
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            File.WriteAllBytes(Path.Combine(root, "top.bin"), new byte[300]);
            File.WriteAllBytes(Path.Combine(root, "sub", "a.txt"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(root, "sub", "inner", "b.zip"), new byte[2000]);
            File.SetAttributes(Path.Combine(root, "sub", "a.txt"), FileAttributes.Hidden);

            var tree = SizeTreeLoader.FromDisk(root);
            Assert.Equal(3300, tree.Size(SizeTree.Root));
            Assert.Equal(3, tree.FileCount(SizeTree.Root));
            Assert.Equal(new[] { "sub", "top.bin", "empty" }, ChildNames(tree, SizeTree.Root));
            // Hidden files count too.
            Assert.Equal(1000, tree.Size(tree.Find(Path.Combine(root, "sub", "a.txt"))));
            Assert.Equal(2000, tree.Size(tree.Find(Path.Combine(root, "sub", "inner"))));
            Assert.True(tree.ModifiedFileTime(tree.Find(Path.Combine(root, "top.bin"))) > 0);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => SizeTreeLoader.FromDisk(root, null, cancelled.Token));
        }
        finally
        {
            try
            {
                File.SetAttributes(Path.Combine(root, "sub", "a.txt"), FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public void TheLargestFileIsFoundThroughNestedFolders()
    {
        var tree = Sample();
        Assert.Equal("notepad.exe", tree.Name(tree.LargestFile(SizeTree.Root)));
        Assert.Equal("photo.jpg", tree.Name(tree.LargestFile(tree.Find(@"C:\Users"))));
        Assert.Equal(-1, tree.LargestFile(tree.Find(@"C:\empty")));
        int a = tree.Find(@"C:\Users\joe\Documents\a.txt");
        Assert.Equal(a, tree.LargestFile(a));
    }
}
