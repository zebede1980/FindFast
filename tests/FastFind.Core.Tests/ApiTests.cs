using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Search;
using Xunit;

namespace FastFind.Core.Tests;

public class ApiTests
{
    private static VolumeIndex BuildSample()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        int users = idx.Add(0, "Users", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        int joe = idx.Add(0, "joe", users, FileAttributes.Directory, 0, now, now);
        int docs = idx.Add(0, "Documents", joe, FileAttributes.Directory, 0, now, now);
        idx.Add(0, "a.txt", docs, FileAttributes.Normal, 100, now, now);
        idx.Add(0, "b.txt", docs, FileAttributes.Normal, 250, now, now);
        idx.Add(0, "photo.jpg", joe, FileAttributes.Normal, 1000, now, now);
        int win = idx.Add(0, "Windows", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        idx.Add(0, "notepad.exe", win, FileAttributes.Normal, 5000, now, now);
        idx.Add(0, "empty", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        idx.Lock.EnterReadLock();
        try { idx.EnsureNameOrder(); } finally { idx.Lock.ExitReadLock(); }
        return idx;
    }

    private static string[] Names(VolumeIndex idx, SearchResult r) => r.Hits.Select(h => idx.GetName(h.Index)!).ToArray();

    [Fact]
    public void FolderSizesAggregateUpTheTree()
    {
        var idx = BuildSample();
        Assert.False(idx.HasFolderSizes);
        idx.UpdateFolderSizes();
        Assert.True(idx.HasFolderSizes);
        Assert.True(idx.FolderSizesCurrent);

        var sizes = LocalIndexService.FolderSizes(new[] { idx }, @"C:\");
        Assert.Equal(1350, sizes["Users"]);
        Assert.Equal(5000, sizes["Windows"]);
        Assert.Equal(0, sizes["empty"]);

        sizes = LocalIndexService.FolderSizes(new[] { idx }, @"C:\Users\joe");
        Assert.Equal(350, sizes["Documents"]);
        Assert.Single(sizes);
    }

    [Fact]
    public void FolderSizesGoStaleOnChangeAndRecompute()
    {
        var idx = BuildSample();
        idx.UpdateFolderSizes();
        idx.Lock.EnterWriteLock();
        try
        {
            int docs = idx.FindDirectory(@"C:\Users\joe\Documents");
            idx.Add(0, "c.txt", docs, FileAttributes.Normal, 50, 0, 0);
        }
        finally { idx.Lock.ExitWriteLock(); }
        Assert.False(idx.FolderSizesCurrent);
        // FolderSizes recomputes on demand.
        Assert.Equal(400, LocalIndexService.FolderSizes(new[] { idx }, @"C:\Users\joe")["Documents"]);
    }

    [Fact]
    public void FolderSizeFunctionMatchesFoldersOnly()
    {
        var idx = BuildSample();
        idx.UpdateFolderSizes();
        var r = SearchEngine.Search(new[] { idx }, QueryParser.Parse("foldersize:>1kb"));
        Assert.Equal(new[] { "joe", "Users", "Windows" }, Names(idx, r));
        // size: still means files only
        r = SearchEngine.Search(new[] { idx }, QueryParser.Parse("size:>1kb"));
        Assert.Equal(new[] { "notepad.exe" }, Names(idx, r));
    }

    [Fact]
    public void FoldersFirstKeepsSortWithinEachGroup()
    {
        var idx = BuildSample();
        var q = QueryParser.Parse("", null, new FilterSet { ScopeFolder = @"C:\Users\joe", ScopeRecursive = false });
        var r = SearchEngine.Search(new[] { idx }, q, SortField.Name, descending: true, foldersFirst: true);
        Assert.Equal(new[] { "Documents", "photo.jpg" }, Names(idx, r));

        q = QueryParser.Parse("", null, new FilterSet { ScopeFolder = @"C:\", ScopeRecursive = false });
        r = SearchEngine.Search(new[] { idx }, q, SortField.Name, descending: false, foldersFirst: true);
        Assert.Equal(new[] { "empty", "Users", "Windows" }, Names(idx, r));
    }

    [Fact]
    public void RelevancePutsShortestNamesFirst()
    {
        var idx = BuildSample();
        var r = SearchEngine.Search(new[] { idx }, QueryParser.Parse("o folder:"), SortField.Relevance);
        Assert.Equal("joe", Names(idx, r)[0]);
    }

    [Fact]
    public void LocalResultSetPagesAndClamps()
    {
        var idx = BuildSample();
        using var set = new LocalResultSet(SearchEngine.Search(new[] { idx }, QueryParser.Parse("ext:txt")));
        Assert.Equal(2, set.Count);
        var rows = set.GetRows(1, 10);
        Assert.Single(rows);
        Assert.Equal("b.txt", rows[0].Name);
        Assert.Equal(@"C:\Users\joe\Documents", rows[0].Directory);
        Assert.Equal(@"C:\Users\joe\Documents\b.txt", rows[0].FullPath);
        Assert.Empty(set.GetRows(5, 10));
    }

    [Fact]
    public void FolderListingReadsLiveFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FastFindTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "one.txt"), "hello");
            File.WriteAllText(Path.Combine(dir, "Two.log"), "hello world");
            File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), "x");

            var listing = FolderListing.Load(dir);
            Assert.Null(listing.Error);
            Assert.Equal(3, listing.Count); // not recursive

            var result = SearchEngine.Search(new[] { listing.Index }, QueryParser.Parse(""), SortField.Name, false, default, foldersFirst: true);
            using var set = new LocalResultSet(result);
            var rows = set.GetRows(0, 10);
            Assert.Equal(new[] { "sub", "one.txt", "Two.log" }, rows.Select(r => r.Name).ToArray());
            Assert.True(rows[0].IsDirectory);
            Assert.Equal(dir, rows[1].Directory);
            Assert.Equal(5, rows[1].Size);

            // The ordinary query language filters the live listing.
            result = SearchEngine.Search(new[] { listing.Index }, QueryParser.Parse("ext:log"));
            Assert.Equal(1, result.Count);

            Assert.True(listing.ApplyFolderSizes(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["SUB"] = 1234 }));
            Assert.False(listing.ApplyFolderSizes(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["SUB"] = 1234 }));
            using var sized = new LocalResultSet(SearchEngine.Search(new[] { listing.Index }, QueryParser.Parse("folder:")));
            Assert.Equal(1234, sized.GetRows(0, 1)[0].Size);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FolderListingReportsMissingFolder()
    {
        var listing = FolderListing.Load(Path.Combine(Path.GetTempPath(), "FastFindTests_missing_" + Guid.NewGuid().ToString("N")));
        Assert.NotNull(listing.Error);
        Assert.Equal(0, listing.Count);
    }

    [Theory]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"c:\Users\", @"c:\Users")]
    [InlineData(@"C:/Users/joe", @"C:\Users\joe")]
    [InlineData(@"""C:\Program Files""", @"C:\Program Files")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\server\share\x\", @"\\server\share\x")]
    [InlineData(@"relative\path", "")]
    [InlineData("", "")]
    public void NormalizePaths(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

    [Fact]
    public void ParentAndSegments()
    {
        Assert.Equal(@"C:\Users", PathUtil.Parent(@"C:\Users\joe"));
        Assert.Equal(@"C:\", PathUtil.Parent(@"C:\Users"));
        Assert.Null(PathUtil.Parent(@"C:\"));
        Assert.Null(PathUtil.Parent(@"\\server\share"));
        Assert.Equal(@"\\server\share", PathUtil.Parent(@"\\server\share\x"));

        Assert.Equal("joe", PathUtil.LeafName(@"C:\Users\joe"));
        Assert.Equal("C:", PathUtil.LeafName(@"C:\"));
        Assert.Equal("share", PathUtil.LeafName(@"\\server\share"));

        var segs = PathUtil.Segments(@"C:\Users\joe");
        Assert.Equal(new[] { ("C:", @"C:\"), ("Users", @"C:\Users"), ("joe", @"C:\Users\joe") }, segs.ToArray());
        segs = PathUtil.Segments(@"\\server\share\x");
        Assert.Equal(new[] { (@"\\server\share", @"\\server\share"), ("x", @"\\server\share\x") }, segs.ToArray());

        Assert.True(PathUtil.IsSameOrInside(@"C:\a\b", @"C:\a"));
        Assert.True(PathUtil.IsSameOrInside(@"C:\a", @"C:\a\"));
        Assert.False(PathUtil.IsSameOrInside(@"C:\ab", @"C:\a"));
        Assert.True(PathUtil.SameVolume(@"C:\x", @"c:\y\z"));
        Assert.False(PathUtil.SameVolume(@"C:\x", @"D:\x"));
    }

    [Fact]
    public void VolumeInfoCovers()
    {
        var v = new VolumeInfo { Root = @"C:\", Status = VolumeStatus.Ready };
        Assert.True(v.Covers(@"C:\"));
        Assert.True(v.Covers(@"c:\Users"));
        Assert.False(v.Covers(@"D:\Users"));
        Assert.False(new VolumeInfo { Root = @"C:\", Status = VolumeStatus.Indexing }.Covers(@"C:\Users"));
        var share = new VolumeInfo { Root = @"\\nas\share\", Status = VolumeStatus.Ready };
        Assert.True(share.Covers(@"\\nas\share"));
        Assert.True(share.Covers(@"\\nas\share\x"));
        Assert.False(share.Covers(@"\\nas\other"));
    }
}

/// <summary>An index service over a fixed in-memory index, for exercising the pipe without touching real drives.</summary>
internal sealed class FakeIndexService : IIndexService
{
    private readonly VolumeIndex _index;
    public FakeIndexService(VolumeIndex index) => _index = index;

    public bool IsRemote => false;
    public bool IsElevated => false;
    public IndexSettings? LastSettings;
    public string? LastRebuild = "(none)";
    public TimeSpan SearchDelay;

    public IReadOnlyList<VolumeInfo> Volumes => new[]
    {
        new VolumeInfo { Root = _index.Root, Label = "Test", Format = "NTFS", Status = VolumeStatus.Ready, Mode = IndexMode.DirectoryWalk, Entries = _index.LiveCount },
    };

    public event Action? VolumesChanged;
    public event Action? IndexChanged;
    public void RaiseVolumesChanged() => VolumesChanged?.Invoke();
    public void RaiseIndexChanged() => IndexChanged?.Invoke();

    public Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default) => Task.Run<IResultSet>(async () =>
    {
        if (SearchDelay > TimeSpan.Zero) await Task.Delay(SearchDelay, ct);
        return new LocalResultSet(SearchEngine.Search(new[] { _index }, request.ToQuery(), request.Sort, request.Descending, ct, request.FoldersFirst));
    }, ct);

    public Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, long>>(LocalIndexService.FolderSizes(new[] { _index }, folder));

    public void Configure(IndexSettings settings) => LastSettings = settings;
    public void Rebuild(string? root) => LastRebuild = root;
    public void RefreshVolumes() { }
    public void Dispose() { }
}

public class PipeTests
{
    private static VolumeIndex BuildSample(int extraFiles = 0)
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        int data = idx.Add(0, "data", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        idx.Add(0, "report.pdf", data, FileAttributes.Normal, 2048, now, now);
        idx.Add(0, "notes.txt", data, FileAttributes.Normal, 10, now, now);
        for (int i = 0; i < extraFiles; i++) idx.Add(0, $"file{i:D5}.bin", data, FileAttributes.Normal, i, now, now);
        idx.Lock.EnterReadLock();
        try { idx.EnsureNameOrder(); } finally { idx.Lock.ExitReadLock(); }
        return idx;
    }

    private static (FakeIndexService Service, PipeIndexServer Server, PipeIndexClient Client) Connect(int extraFiles = 0)
    {
        string name = "FastFind.Tests." + Guid.NewGuid().ToString("N");
        var service = new FakeIndexService(BuildSample(extraFiles));
        var server = new PipeIndexServer(service, name, PipeProtocol.CreateSecurity(PipeProtocol.CurrentUserSid()));
        server.Start();
        var client = PipeIndexClient.TryConnect(name, 5000);
        Assert.NotNull(client);
        return (service, server, client!);
    }

    [Fact]
    public async Task SearchAndPageRowsOverThePipe()
    {
        var (_, server, client) = Connect(extraFiles: 1000);
        using (server)
        using (client)
        {
            Assert.True(client.IsRemote);
            Assert.Single(client.Volumes);
            Assert.Equal(@"C:\", client.Volumes[0].Root);
            Assert.Equal(VolumeStatus.Ready, client.Volumes[0].Status);

            using var all = await client.SearchAsync(new SearchRequest { Text = "", Sort = SortField.Name });
            Assert.Equal(1003, all.Count);

            using var set = await client.SearchAsync(new SearchRequest { Text = "file", Extensions = new[] { "bin" }, Sort = SortField.Size, Descending = true });
            Assert.Equal(1000, set.Count);
            Assert.Null(set.Error);
            var page = set.GetRows(0, 100);
            Assert.Equal(100, page.Length);
            Assert.Equal("file00999.bin", page[0].Name);
            Assert.Equal(@"C:\data", page[0].Directory);
            Assert.Equal(999, page[0].Size);
            Assert.Equal(@"C:\data\file00999.bin", page[0].FullPath);
            var tail = set.GetRows(990, 100);
            Assert.Equal(10, tail.Length);
            Assert.Equal("file00000.bin", tail[^1].Name);
        }
    }

    [Fact]
    public async Task ReleasedResultReturnsPlaceholdersInsteadOfThrowing()
    {
        var (_, server, client) = Connect();
        using (server)
        using (client)
        {
            var set = await client.SearchAsync(new SearchRequest { Text = "report" });
            Assert.Equal(1, set.Count);
            set.Dispose();
            var rows = set.GetRows(0, 1);
            Assert.Single(rows);
            Assert.True(rows[0].IsDeleted);
        }
    }

    [Fact]
    public async Task FolderSizesSettingsAndRebuildCrossThePipe()
    {
        var (service, server, client) = Connect();
        using (server)
        using (client)
        {
            var sizes = await client.GetFolderSizesAsync(@"C:\");
            Assert.Equal(2058, sizes["data"]);

            IndexSettings? configured = null;
            server.Configured += s => configured = s;
            client.Configure(new IndexSettings { IncludeRemovable = true, NetworkLocations = { @"\\nas\share\" }, ExcludedRoots = { @"D:\" }, NetworkRescanHours = 2.5 });
            client.Rebuild(@"C:\");
            // Posts are one-way; a round trip afterwards proves the server has processed them (requests are handled in order).
            await client.GetFolderSizesAsync(@"C:\");
            await WaitFor(() => service.LastRebuild == @"C:\" && configured != null);
            Assert.True(service.LastSettings!.IncludeRemovable);
            Assert.Equal(new[] { @"\\nas\share\" }, service.LastSettings.NetworkLocations);
            Assert.Equal(new[] { @"D:\" }, service.LastSettings.ExcludedRoots);
            Assert.Equal(2.5, service.LastSettings.NetworkRescanHours);

            client.Rebuild(null);
            await client.GetFolderSizesAsync(@"C:\");
            await WaitFor(() => service.LastRebuild == null);
        }
    }

    [Fact]
    public async Task EventsReachTheClient()
    {
        var (service, server, client) = Connect();
        using (server)
        using (client)
        {
            int volumes = 0, index = 0;
            client.VolumesChanged += () => Interlocked.Increment(ref volumes);
            client.IndexChanged += () => Interlocked.Increment(ref index);
            service.RaiseVolumesChanged();
            service.RaiseIndexChanged();
            await WaitFor(() => volumes > 0 && index > 0);
        }
    }

    [Fact]
    public async Task CancelledSearchThrowsAndConnectionSurvives()
    {
        var (service, server, client) = Connect();
        using (server)
        using (client)
        {
            service.SearchDelay = TimeSpan.FromSeconds(5);
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync(new SearchRequest { Text = "x" }, cts.Token));
            service.SearchDelay = TimeSpan.Zero;
            using var set = await client.SearchAsync(new SearchRequest { Text = "notes" });
            Assert.Equal(1, set.Count);
        }
    }

    [Fact]
    public async Task ClientNoticesServerGoingAway()
    {
        var (_, server, client) = Connect();
        using (client)
        {
            bool disconnected = false;
            client.Disconnected += () => disconnected = true;
            server.Dispose();
            await WaitFor(() => disconnected);
            Assert.False(client.IsConnected);
            await Assert.ThrowsAsync<IOException>(() => client.SearchAsync(new SearchRequest { Text = "x" }));
        }
    }

    [Fact]
    public void ConnectFailsQuietlyWhenNoServer()
        => Assert.Null(PipeIndexClient.TryConnect("FastFind.Tests.Nobody." + Guid.NewGuid().ToString("N"), 100));

    [Fact]
    public void SecondServerOnSameNameIsRefused()
    {
        string name = "FastFind.Tests." + Guid.NewGuid().ToString("N");
        var security = PipeProtocol.CreateSecurity(PipeProtocol.CurrentUserSid());
        using var first = new PipeIndexServer(new FakeIndexService(BuildSample()), name, security);
        first.Start();
        using var second = new PipeIndexServer(new FakeIndexService(BuildSample()), name, PipeProtocol.CreateSecurity(PipeProtocol.CurrentUserSid()));
        Assert.ThrowsAny<Exception>(() => second.Start());
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }
}

public class NameOrderTests
{
    private static string[] Search(VolumeIndex idx, string query = "")
    {
        var r = SearchEngine.Search(new[] { idx }, QueryParser.Parse(query));
        return r.Hits.Select(h => idx.GetName(h.Index)!).ToArray();
    }

    private static void Write(VolumeIndex idx, Action change)
    {
        idx.Lock.EnterWriteLock();
        try { change(); } finally { idx.Lock.ExitWriteLock(); }
    }

    [Fact]
    public void AddsRenamesAndDeletesAreMergedIntoTheSortedOrder()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        int delta = 0, kilo = 0;
        Write(idx, () =>
        {
            idx.Add(0, "bravo.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            delta = idx.Add(0, "delta.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            idx.Add(0, "foxtrot.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            kilo = idx.Add(0, "kilo.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
        });
        Assert.Equal(new[] { "bravo.txt", "delta.txt", "foxtrot.txt", "kilo.txt" }, Search(idx));
        Assert.True(idx.IsNameOrderReady);

        // Changes after the order exists: one at the front, one in the middle, one at the end, a rename and a delete.
        Write(idx, () =>
        {
            idx.Add(0, "alpha.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            idx.Add(0, "Echo.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            idx.Add(0, "zulu.txt", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            idx.Rename(delta, "yankee.txt", idx.RootIndex);
            idx.Remove(kilo);
        });
        Assert.True(idx.IsNameOrderReady); // not thrown away: merged on the next search
        Assert.Equal(new[] { "alpha.txt", "bravo.txt", "Echo.txt", "foxtrot.txt", "yankee.txt", "zulu.txt" }, Search(idx));

        // An entry added and removed again before any search must not reappear.
        Write(idx, () =>
        {
            int temp = idx.Add(0, "charlie.tmp", idx.RootIndex, FileAttributes.Normal, 1, 0, 0);
            idx.Remove(temp);
        });
        Assert.Equal(new[] { "alpha.txt", "bravo.txt", "Echo.txt", "foxtrot.txt", "yankee.txt", "zulu.txt" }, Search(idx));
    }

    [Fact]
    public void IncrementalOrderMatchesAFullSortAfterManyRandomChanges()
    {
        var random = new Random(12345);
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        var live = new List<int>();
        Write(idx, () => { for (int i = 0; i < 2000; i++) live.Add(idx.Add(0, $"file{random.Next(100000):D5}.dat", idx.RootIndex, FileAttributes.Normal, 1, 0, 0)); });
        Search(idx);

        for (int round = 0; round < 20; round++)
        {
            Write(idx, () =>
            {
                for (int n = 0; n < 50; n++)
                {
                    switch (random.Next(3))
                    {
                        case 0: live.Add(idx.Add(0, $"file{random.Next(100000):D5}.dat", idx.RootIndex, FileAttributes.Normal, 1, 0, 0)); break;
                        case 1 when live.Count > 0: { int at = random.Next(live.Count); idx.Remove(live[at]); live.RemoveAt(at); break; }
                        case 2 when live.Count > 0: idx.Rename(live[random.Next(live.Count)], $"file{random.Next(100000):D5}.dat", idx.RootIndex); break;
                    }
                }
            });
            var names = Search(idx);
            var expected = live.Select(i => idx.GetName(i)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(expected, names);
        }
    }
}

public class ServiceInstallerTests
{
    private static readonly System.Xml.Linq.XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskForTheSameUserRunsAtTheirLogonWithHighestPrivileges()
    {
        string sid = PipeProtocol.CurrentUserSid();
        var xml = System.Xml.Linq.XDocument.Parse(ServiceInstaller.BuildTaskXml(@"C:\Program Files\Fast & Find\FastFind.Service.exe", sid, runAsSystem: false));
        var task = xml.Root!;
        Assert.Equal("HighestAvailable", task.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "RunLevel")!.Value);
        Assert.Equal(sid, task.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", task.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "LogonType")!.Value);
        Assert.NotNull(task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger"));
        // Task Scheduler's defaults would kill the index after three days and stop it on battery.
        Assert.Equal("PT0S", task.Element(Ns + "Settings")!.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", task.Element(Ns + "Settings")!.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        // Paths with special characters survive as text.
        Assert.Equal(@"C:\Program Files\Fast & Find\FastFind.Service.exe", task.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
        Assert.Equal($"run --user {sid}", task.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void TaskInstalledByAnotherAccountRunsAsSystemAtBoot()
    {
        var xml = System.Xml.Linq.XDocument.Parse(ServiceInstaller.BuildTaskXml(@"C:\FastFind\FastFind.Service.exe", "S-1-5-21-1-2-3-1001", runAsSystem: true));
        var principal = xml.Root!.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("S-1-5-18", principal.Element(Ns + "UserId")!.Value);
        Assert.Null(principal.Element(Ns + "LogonType"));
        Assert.NotNull(xml.Root.Element(Ns + "Triggers")!.Element(Ns + "BootTrigger"));
        // It still serves the user it was installed for.
        Assert.Equal("run --user S-1-5-21-1-2-3-1001", xml.Root.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void TaskNameIsPerUser()
        => Assert.NotEqual(ServiceInstaller.TaskName("S-1-5-21-1-2-3-1001"), ServiceInstaller.TaskName("S-1-5-21-1-2-3-1002"));
}
