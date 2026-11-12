using FastFind.Core.Usage;
using Xunit;

namespace FastFind.Core.Tests;

public class UsageAnalyzerTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly UsageEnvironment Env = new()
    {
        UserProfile = @"C:\Users\joe",
        LocalAppData = @"C:\Users\joe\AppData\Local",
        RoamingAppData = @"C:\Users\joe\AppData\Roaming",
        LocalLowAppData = @"C:\Users\joe\AppData\LocalLow",
        Temp = @"C:\Users\joe\AppData\Local\Temp",
        Downloads = @"C:\Users\joe\Downloads",
        ProgramFiles = @"C:\Program Files",
        ProgramFilesX86 = @"C:\Program Files (x86)",
        ProgramData = @"C:\ProgramData",
        Windows = @"C:\Windows",
        NowUtc = Now,
    };

    /// <summary>Builds a tree from full file paths; the folders come into being as needed.</summary>
    private sealed class Disk
    {
        private readonly SizeTree.Builder _builder;
        public Disk(string root = @"C:\") => _builder = new SizeTree.Builder(root);

        public Disk File(string path, long megabytes, int daysOld = 30)
        {
            int slash = path.LastIndexOf('\\');
            string directory = slash == 2 ? path[..3] : path[..slash];
            Assert.True(_builder.AddEntry(path[(slash + 1)..], directory, megabytes * MB, Now.AddDays(-daysOld).ToFileTimeUtc(), FileAttributes.Normal), path);
            return this;
        }

        public SizeTree Build() => _builder.Build();
    }

    private static SizeTree Machine() => new Disk()
        // Chrome: program, a cache, real data and a log
        .File(@"C:\Program Files\Google\Chrome\Application\chrome.exe", 300)
        .File(@"C:\Users\joe\AppData\Local\Google\Chrome\User Data\Default\Cache\data_1", 200)
        .File(@"C:\Users\joe\AppData\Local\Google\Chrome\User Data\Default\History", 10)
        .File(@"C:\Users\joe\AppData\Local\Google\Chrome\User Data\debug.log", 5)
        // VS Code under three different names in three places
        .File(@"C:\Users\joe\AppData\Local\Programs\Microsoft VS Code\Code.exe", 100)
        .File(@"C:\Users\joe\AppData\Roaming\Code\CachedData\abc\chunk", 50)
        .File(@"C:\Users\joe\AppData\Roaming\Code\User\settings.json", 2)
        .File(@"C:\Users\joe\.vscode\extensions\some.ext\extension.js", 8)
        // a cache that belongs to every user of the machine: counted, never offered
        .File(@"C:\ProgramData\Acme\Cache\blob", 20)
        .File(@"C:\Program Files\Docker\docker.exe", 40)
        .File(@"C:\Users\joe\.docker\config.json", 30)
        .File(@"C:\Users\joe\AppData\Local\Packages\Contoso.Notes_8wekyb3d8bbwe\AC\INetCache\page", 3)
        .File(@"C:\Users\joe\AppData\Local\Packages\Contoso.Notes_8wekyb3d8bbwe\LocalState\notes.db", 4)
        // FastFind's own index cache
        .File(@"C:\Users\joe\AppData\Local\FastFind\cache\C.idx", 36)
        // temporary files: one old, one a running program may still want
        .File(@"C:\Users\joe\AppData\Local\Temp\old-install\setup.tmp", 10, daysOld: 10)
        .File(@"C:\Users\joe\AppData\Local\Temp\just-now.tmp", 7, daysOld: 0)
        .File(@"C:\Users\joe\AppData\Local\CrashDumps\app.exe.1234.dmp", 5)
        .File(@"C:\Users\joe\.nuget\packages\newtonsoft.json\13.0.3\lib.dll", 60)
        // a project under git: its node_modules and build output can be made again
        .File(@"C:\src\app\.git\HEAD", 1)
        .File(@"C:\src\app\web\package.json", 1)
        .File(@"C:\src\app\web\node_modules\react\index.js", 80)
        .File(@"C:\src\app\web\node_modules\react\node_modules\inner\package.json", 1)
        .File(@"C:\src\app\api\api.csproj", 1)
        .File(@"C:\src\app\api\bin\Debug\api.dll", 60)
        .File(@"C:\src\app\api\obj\project.assets.json", 5)
        // the same folder names where they are shipped content, not something to rebuild
        .File(@"C:\releases\product-5.8\package.json", 1)
        .File(@"C:\releases\product-5.8\node_modules\lib\index.js", 90)
        .File(@"C:\releases\sdk-sample\sample.csproj", 1)
        .File(@"C:\releases\sdk-sample\bin\sample.exe", 70)
        .File(@"C:\Program Files\Tool\resources\app\package.json", 1)
        .File(@"C:\Program Files\Tool\resources\app\node_modules\dep\index.js", 75)
        .File(@"C:\Users\joe\AppData\Roaming\npm\package.json", 1)
        .File(@"C:\Users\joe\AppData\Roaming\npm\node_modules\tool\cli.js", 65)
        // Windows' own
        .File(@"C:\hiberfil.sys", 500, daysOld: 0)
        .File(@"C:\Windows\SoftwareDistribution\Download\update.cab", 50)
        .File(@"C:\Windows\System32\kernel32.dll", 400, daysOld: 2000)
        .File(@"C:\$Recycle.Bin\S-1-5-21\$R123.zip", 5)
        // the user's own files
        .File(@"C:\Users\joe\Downloads\setup.exe", 30, daysOld: 5)
        .File(@"C:\Users\joe\Downloads\old-report.zip", 40, daysOld: 200)
        .File(@"C:\Users\joe\Downloads\new-report.zip", 40, daysOld: 1)
        .File(@"C:\data\archive-2022.bak", 300, daysOld: 1100)
        .File(@"C:\data\current.bak", 300, daysOld: 10)
        .Build();

    private static AppUsage App(UsageReport report, string name) => report.Apps.Single(a => a.Name == name);
    private static CleanupCandidate? Candidate(UsageReport report, string id) => report.Candidates.SingleOrDefault(c => c.Id == id || c.Id.StartsWith(id + ":"));

    [Fact]
    public void AnApplicationIsGatheredFromEverywhereItKeepsThings()
    {
        var tree = Machine();
        var report = UsageAnalyzer.Analyze(tree, Env);

        var chrome = App(report, "Google Chrome");
        Assert.Equal(300 * MB, chrome.Program);
        Assert.Equal(200 * MB, chrome.Caches);
        Assert.Equal(10 * MB, chrome.Data);
        Assert.Equal(5 * MB, chrome.Logs);
        Assert.Equal(515 * MB, chrome.Total);
        Assert.Equal(200 * MB, chrome.ClearableBytes);
        Assert.Equal(new[] { @"C:\Users\joe\AppData\Local\Google\Chrome\User Data\Default\Cache" }, chrome.ClearableCaches.Select(tree.GetPath).ToArray());
        Assert.Equal(2, chrome.Roots.Count);

        // "Microsoft VS Code", "Code" and ".vscode" are one application.
        var code = App(report, "Visual Studio Code");
        Assert.Equal(100 * MB, code.Program);
        Assert.Equal(50 * MB, code.Caches);
        Assert.Equal(10 * MB, code.Data);
        Assert.Equal(3, code.Roots.Count);

        var docker = App(report, "Docker");
        Assert.Equal(40 * MB, docker.Program);
        Assert.Equal(30 * MB, docker.Data);

        var notes = App(report, "Contoso Notes");
        Assert.Equal(3 * MB, notes.Caches);
        Assert.Equal(4 * MB, notes.Data);

        // A cache shared by all users is reported but is not FastFind's to clear.
        var acme = App(report, "Acme");
        Assert.Equal(20 * MB, acme.Caches);
        Assert.Equal(0, acme.ClearableBytes);

        Assert.DoesNotContain(report.Apps, a => a.Name is "FastFind" or "Temp" or "Packages" or "Programs" or "CrashDumps");
        Assert.Equal(report.Apps.OrderByDescending(a => a.Total).Select(a => a.Name), report.Apps.Select(a => a.Name));
    }

    [Fact]
    public void CachesAndOldTemporaryFilesAreSafeToClear()
    {
        var tree = Machine();
        var report = UsageAnalyzer.Analyze(tree, Env);

        var temp = Candidate(report, "temp")!;
        Assert.Equal(CleanupSafety.Safe, temp.Safety);
        Assert.Equal(CleanupAction.Clear, temp.Action);
        Assert.True(temp.ContentsOnly);
        Assert.Equal(2, temp.MinAgeDays);
        // Only the file that has sat for ten days is counted; the one written today may be in use.
        Assert.Equal(10 * MB, temp.Bytes);
        Assert.Equal(1, temp.Files);

        var caches = Candidate(report, "app-caches")!;
        Assert.Equal(CleanupSafety.Safe, caches.Safety);
        Assert.True(caches.ContentsOnly);
        Assert.Equal((200 + 50 + 3) * MB, caches.Bytes);
        Assert.Equal(3, caches.Nodes.Count);
        Assert.Contains("Google Chrome", caches.Detail);

        var dumps = Candidate(report, "crash-dumps")!;
        Assert.Equal(5 * MB, dumps.Bytes);
        Assert.Equal((10 + 253 + 5) * MB, report.SafeBytes);
    }

    [Fact]
    public void ProjectFoldersAreOnlyOfferedInsideAGitWorkingCopy()
    {
        var tree = Machine();
        var report = UsageAnalyzer.Analyze(tree, Env);

        var modules = Candidate(report, "modules")!;
        Assert.Equal(CleanupSafety.Rebuildable, modules.Safety);
        Assert.Equal(CleanupAction.Clear, modules.Action);
        Assert.False(modules.ContentsOnly);
        Assert.Equal(new[] { @"C:\src\app\web\node_modules" }, modules.Nodes.Select(tree.GetPath).ToArray());
        Assert.Equal(81 * MB, modules.Bytes);

        var build = Candidate(report, "build")!;
        Assert.Equal(new[] { @"C:\src\app\api\bin", @"C:\src\app\api\obj" }, build.Nodes.Select(tree.GetPath).OrderBy(p => p).ToArray());
        Assert.Equal(65 * MB, build.Bytes);

        var nuget = Candidate(report, "nuget")!;
        Assert.Equal(CleanupSafety.Rebuildable, nuget.Safety);
        Assert.Equal(60 * MB, nuget.Bytes);

        // A released package, a vendor's sample, an installed program and a globally installed tool have the same
        // folder names, but nothing says they can be rebuilt: none of them is mentioned anywhere.
        var offered = report.Candidates.SelectMany(c => c.Nodes).Select(tree.GetPath).ToList();
        Assert.DoesNotContain(offered, p => p.StartsWith(@"C:\releases", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offered, p => p.Contains(@"\Tool\"));
        Assert.DoesNotContain(offered, p => p.Contains(@"\Roaming\npm"));
        Assert.Single(report.Candidates, c => c.Id.StartsWith("modules:"));
        Assert.Single(report.Candidates, c => c.Id.StartsWith("build:"));
    }

    [Fact]
    public void WindowsOwnFilesAreLeftToWindowsTools()
    {
        var tree = Machine();
        var report = UsageAnalyzer.Analyze(tree, Env);

        var update = Candidate(report, "windows-update")!;
        Assert.Equal(CleanupSafety.Windows, update.Safety);
        Assert.Equal(CleanupAction.DiskCleanup, update.Action);
        Assert.Equal(50 * MB, update.Bytes);

        var hibernation = Candidate(report, "hibernation")!;
        Assert.Equal(CleanupAction.CopyCommand, hibernation.Action);
        Assert.Equal("powercfg /hibernate off", hibernation.Command);
        Assert.Equal(500 * MB, hibernation.Bytes);

        var bin = Candidate(report, "recycle-bin")!;
        Assert.Equal(CleanupAction.EmptyRecycleBin, bin.Action);
        Assert.Equal(5 * MB, bin.Bytes);

        Assert.Null(Candidate(report, "windows-old"));

        // Whatever FastFind itself would delete lies outside Windows, program folders and shared application data.
        foreach (var candidate in report.Candidates.Where(c => c.Action == CleanupAction.Clear))
            foreach (int node in candidate.Nodes)
                Assert.False(Env.IsSystemPath(tree.GetPath(node)), $"{candidate.Title} would clear {tree.GetPath(node)}");
    }

    [Fact]
    public void TheUsersOwnFilesAreOnlyPointedOut()
    {
        var tree = Machine();
        var report = UsageAnalyzer.Analyze(tree, Env);

        var installers = Candidate(report, "downloads-installers")!;
        Assert.Equal(CleanupAction.Show, installers.Action);
        Assert.Equal(CleanupSafety.Review, installers.Safety);
        Assert.Equal(new[] { @"C:\Users\joe\Downloads\setup.exe" }, installers.Nodes.Select(tree.GetPath).ToArray());

        var stale = Candidate(report, "downloads-old")!;
        Assert.Equal(new[] { @"C:\Users\joe\Downloads\old-report.zip" }, stale.Nodes.Select(tree.GetPath).ToArray());

        var old = Candidate(report, "old-large")!;
        Assert.Equal(CleanupAction.Show, old.Action);
        // The old backup is found; the recent one, and an old file that is part of Windows, are not.
        Assert.Equal(new[] { @"C:\data\archive-2022.bak" }, old.Nodes.Select(tree.GetPath).ToArray());
    }

    [Fact]
    public void AFolderWithNothingOfTheSortReportsNothing()
    {
        var tree = new Disk(@"C:\Users\joe\Documents")
            .File(@"C:\Users\joe\Documents\report.docx", 3)
            .File(@"C:\Users\joe\Documents\Cache\not-an-app-cache.bin", 9)
            .Build();
        var report = UsageAnalyzer.Analyze(tree, Env);
        Assert.Empty(report.Apps);
        Assert.Empty(report.Candidates);
        Assert.Equal(0, report.SafeBytes);
    }

    [Fact]
    public void MeasuringOnlyApplicationDataStillFindsItsCaches()
    {
        var tree = new Disk(@"C:\Users\joe\AppData\Local")
            .File(@"C:\Users\joe\AppData\Local\Vivaldi\User Data\Default\Code Cache\js\x", 70)
            .File(@"C:\Users\joe\AppData\Local\Vivaldi\User Data\Default\Bookmarks", 1)
            .File(@"C:\Users\joe\AppData\Local\Vivaldi\package.json", 1)
            .File(@"C:\Users\joe\AppData\Local\Vivaldi\node_modules\x\y.js", 80)
            .Build();
        var report = UsageAnalyzer.Analyze(tree, Env);
        var vivaldi = App(report, "Vivaldi");
        Assert.Equal(70 * MB, vivaldi.ClearableBytes);
        Assert.Equal(82 * MB, vivaldi.Data);
        Assert.Equal(new[] { "app-caches" }, report.Candidates.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void SystemAndUserPathsAreToldApart()
    {
        Assert.True(Env.IsSystemPath(@"C:\Windows\Temp"));
        Assert.True(Env.IsSystemPath(@"c:\program files\App"));
        Assert.True(Env.IsSystemPath(@"C:\ProgramData"));
        Assert.False(Env.IsSystemPath(@"C:\Windows.old"));
        Assert.False(Env.IsSystemPath(@"C:\Users\joe\AppData\Local\Temp"));
        Assert.True(Env.IsUserAppData(@"C:\Users\joe\AppData\Roaming\Code"));
        Assert.False(Env.IsUserAppData(@"C:\Users\joe\Documents"));
        Assert.False(new UsageEnvironment().IsSystemPath(@"C:\Windows"));
    }

    // ---- SizeTree.RemoveMany ----

    [Fact]
    public void RemovingManyAtOnceMatchesRemovingOneAtATime()
    {
        static SizeTree Build()
        {
            var b = new SizeTree.Builder(@"C:\");
            int a = b.AddFolder(SizeTree.Root, "a");
            for (int i = 0; i < 40; i++) b.AddFile(a, $"f{i:D2}.bin", 100 + i);
            int deep = b.AddFolder(a, "deep");
            b.AddFile(deep, "x.bin", 5000);
            b.AddFile(deep, "y.bin", 10);
            int c = b.AddFolder(SizeTree.Root, "c");
            b.AddFile(c, "big.bin", 3000);
            return b.Build();
        }

        var one = Build();
        var many = Build();
        // Half the files of a, the whole of deep together with one of its own files, and everything in c.
        var victims = new List<int>();
        foreach (int child in one.Children(one.Find(@"C:\a")))
            if (!one.IsDirectory(child) && one.Size(child) % 2 == 0) victims.Add(child);
        victims.Add(one.Find(@"C:\a\deep\y.bin"));
        victims.Add(one.Find(@"C:\a\deep"));
        victims.Add(one.Find(@"C:\c\big.bin"));

        foreach (int node in victims) one.Remove(node);
        int version = many.Version;
        many.RemoveMany(victims);

        Assert.True(many.Version > version);
        Assert.Equal(one.Size(SizeTree.Root), many.Size(SizeTree.Root));
        Assert.Equal(one.FileCount(SizeTree.Root), many.FileCount(SizeTree.Root));
        foreach (string folder in new[] { @"C:\", @"C:\a", @"C:\c" })
        {
            int x = one.Find(folder), y = many.Find(folder);
            Assert.Equal(one.Size(x), many.Size(y));
            Assert.Equal(one.Children(x).ToArray().Select(one.Name), many.Children(y).ToArray().Select(many.Name));
        }
        Assert.Equal(-1, many.Find(@"C:\a\deep"));
        Assert.Equal(0, many.Size(many.Find(@"C:\c")));
        // c emptied, so a (which kept its odd-sized files) now comes first.
        Assert.Equal(new[] { "a", "c" }, many.Children(SizeTree.Root).ToArray().Select(many.Name).ToArray());

        // Nothing changes when there is nothing left to remove.
        version = many.Version;
        many.RemoveMany(victims);
        many.RemoveMany(new[] { SizeTree.Root, -5, 99999 });
        Assert.Equal(version, many.Version);
    }

    [Fact]
    public void EmptyingAHugeFolderIsQuick()
    {
        var b = new SizeTree.Builder(@"C:\");
        int cache = b.AddFolder(SizeTree.Root, "cache");
        for (int i = 0; i < 200_000; i++) b.AddFile(cache, "f" + i, 10);
        var tree = b.Build();
        var all = tree.Children(cache).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        tree.RemoveMany(all);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, tree.Size(SizeTree.Root));
        Assert.Equal(0, tree.ChildCount(cache));
        Assert.Equal(0, tree.FileCount(cache));
    }
}
