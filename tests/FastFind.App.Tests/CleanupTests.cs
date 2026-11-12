using System.Diagnostics;
using System.IO;
using FastFind.App.Services;
using FastFind.App.Treemap;
using FastFind.Core.Usage;
using Xunit;

namespace FastFind.App.Tests;

/// <summary>
/// Clean-ups against a made-up machine in a temporary folder: a user profile with application data and a project,
/// described by an environment that points at them, so the real deletion code runs without touching anything real.
/// </summary>
public sealed class CleanupTests : IDisposable
{
    private readonly string _root;
    private readonly UsageEnvironment _env;

    public CleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FastFind.CleanupTests." + Guid.NewGuid().ToString("N"));
        string me = Path.Combine(_root, "Users", "me");
        _env = new UsageEnvironment
        {
            UserProfile = me,
            LocalAppData = Path.Combine(me, "AppData", "Local"),
            RoamingAppData = Path.Combine(me, "AppData", "Roaming"),
            LocalLowAppData = Path.Combine(me, "AppData", "LocalLow"),
            Temp = Path.Combine(me, "AppData", "Local", "Temp"),
            Downloads = Path.Combine(me, "Downloads"),
            ProgramFiles = Path.Combine(_root, "Program Files"),
            ProgramFilesX86 = Path.Combine(_root, "Program Files (x86)"),
            ProgramData = Path.Combine(_root, "ProgramData"),
            Windows = Path.Combine(_root, "Windows"),
        };
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { }
    }

    private string Write(string relative, int bytes = 1000, int daysOld = 0)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (daysOld > 0)
        {
            var when = DateTime.UtcNow.AddDays(-daysOld);
            File.SetCreationTimeUtc(path, when);
            File.SetLastWriteTimeUtc(path, when);
        }
        return path;
    }

    private string Cache => Path.Combine(_env.LocalAppData, "Browser", "Cache");

    [Fact]
    public async Task EmptyingACacheKeepsTheFolderAndWhateverIsInUseOrArrivedLater()
    {
        for (int i = 0; i < 5; i++) Write($@"Users\me\AppData\Local\Browser\Cache\data_{i}", 1000);
        Write(@"Users\me\AppData\Local\Browser\Cache\js\chunk", 500);
        string readOnly = Write(@"Users\me\AppData\Local\Browser\Cache\locked-down", 200);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string history = Write(@"Users\me\AppData\Local\Browser\History", 300);
        var tree = SizeTreeLoader.FromDisk(_root);
        // Written after the folder was measured: nobody was shown this file, so it is not deleted.
        string late = Write(@"Users\me\AppData\Local\Browser\Cache\arrived-later", 50);

        int cache = tree.Find(Cache);
        CleanupOutcome outcome;
        using (new FileStream(Path.Combine(Cache, "data_2"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = await CleanupRunner.RunAsync(tree, new[] { cache }, contentsOnly: true, minAgeDays: 0, _env);

        Assert.Equal(6, outcome.FilesDeleted); // four data files, the chunk and the read-only one
        Assert.Equal(1, outcome.Left);
        Assert.Equal(4 * 1000 + 500 + 200, outcome.BytesFreed);
        Assert.Empty(outcome.Refused);
        Assert.True(Directory.Exists(Cache));
        Assert.True(File.Exists(Path.Combine(Cache, "data_2")));
        Assert.True(File.Exists(late));
        Assert.True(File.Exists(history));
        Assert.False(File.Exists(readOnly));
        Assert.False(Directory.Exists(Path.Combine(Cache, "js"))); // emptied sub-folders go

        // What went comes off the tree; what stayed is still counted.
        tree.RemoveMany(outcome.Removed);
        Assert.Equal(1000, tree.Size(cache));
        Assert.Equal(1000 + 300, tree.Size(SizeTree.Root));
        Assert.Equal(-1, tree.Find(Path.Combine(Cache, "js")));
    }

    [Fact]
    public async Task TemporaryFilesNewerThanTheLimitAreLeft()
    {
        string old = Write(@"Users\me\AppData\Local\Temp\installer\old.tmp", 2000, daysOld: 10);
        string recent = Write(@"Users\me\AppData\Local\Temp\running.tmp", 700);
        // Old contents but only just copied here: a program may have put it there a moment ago.
        string copied = Write(@"Users\me\AppData\Local\Temp\copied.dll", 400);
        File.SetLastWriteTimeUtc(copied, DateTime.UtcNow.AddDays(-400));
        var tree = SizeTreeLoader.FromDisk(_root);

        var outcome = await CleanupRunner.RunAsync(tree, new[] { tree.Find(_env.Temp) }, contentsOnly: true, minAgeDays: 2, _env);

        Assert.Equal(1, outcome.FilesDeleted);
        Assert.Equal(2, outcome.Left);
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(copied));
        Assert.True(Directory.Exists(_env.Temp));
    }

    [Fact]
    public async Task AFolderIsRemovedWholeUnlessSomethingUnmeasuredIsInIt()
    {
        Write(@"src\app\package.json", 10);
        Write(@"src\app\node_modules\a\index.js", 3000);
        Write(@"src\app\node_modules\b\lib\b.js", 2000);
        Write(@"src\other\node_modules\c\c.js", 1000);
        var tree = SizeTreeLoader.FromDisk(_root);
        string surprise = Write(@"src\other\node_modules\c\added-since.js", 5);

        int whole = tree.Find(Path.Combine(_root, @"src\app\node_modules")), partial = tree.Find(Path.Combine(_root, @"src\other\node_modules"));
        var outcome = await CleanupRunner.RunAsync(tree, new[] { whole, partial }, contentsOnly: false, minAgeDays: 0, _env);

        Assert.Equal(3, outcome.FilesDeleted);
        Assert.False(Directory.Exists(Path.Combine(_root, @"src\app\node_modules")));
        Assert.True(File.Exists(Path.Combine(_root, @"src\app\package.json")));
        // The folder that went whole is reported once, standing for all that was in it.
        Assert.Contains(whole, outcome.Removed);
        Assert.DoesNotContain(tree.Find(Path.Combine(_root, @"src\app\node_modules\a")), outcome.Removed);
        // The other keeps the file nobody measured, and so stays.
        Assert.True(File.Exists(surprise));
        Assert.DoesNotContain(partial, outcome.Removed);

        tree.RemoveMany(outcome.Removed);
        Assert.Equal(10, tree.Size(SizeTree.Root));
    }

    [Fact]
    public async Task NothingIsDeletedThroughALink()
    {
        string precious = Write(@"elsewhere\precious.docx", 900);
        Write(@"Users\me\AppData\Local\Browser\Cache\data_0", 100);
        string link = Path.Combine(Cache, "linked");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(precious)}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        if (!Directory.Exists(link)) return; // junctions not available here: nothing to test

        var tree = SizeTreeLoader.FromDisk(_root);
        // The tree itself does not look through the link.
        Assert.Equal(0, tree.Size(tree.Find(link)));
        var outcome = await CleanupRunner.RunAsync(tree, new[] { tree.Find(Cache) }, contentsOnly: true, minAgeDays: 0, _env);

        Assert.Equal(1, outcome.FilesDeleted);
        Assert.True(File.Exists(precious));
        Assert.True(Directory.Exists(link)); // the link itself is left as it was

        // Nor may a link be the thing to clean.
        Assert.Contains("link", CleanupRunner.Refusal(link, _root, _env));
    }

    [Fact]
    public async Task FoldersThatMustNeverBeCleanedAreRefused()
    {
        Write(@"Users\me\AppData\Local\Browser\Cache\data_0", 100);
        Write(@"Users\me\Documents\letter.docx", 100);
        Write(@"Users\me\Downloads\setup.exe", 100);
        Write(@"Windows\Temp\w.tmp", 100);
        Write(@"Program Files\App\Cache\p.bin", 100);
        Write(@"ProgramData\App\Cache\d.bin", 100);
        var tree = SizeTreeLoader.FromDisk(_root);

        string[] forbidden =
        {
            _env.UserProfile, _env.LocalAppData, Path.Combine(_env.UserProfile, "AppData"), Path.Combine(_env.UserProfile, "Documents"), _env.Downloads,
            Path.Combine(_root, "Users"), Path.Combine(_env.Windows, "Temp"), Path.Combine(_env.ProgramFiles, @"App\Cache"), Path.Combine(_env.ProgramData, @"App\Cache"),
        };
        foreach (string path in forbidden) Assert.NotNull(CleanupRunner.Refusal(path, _root, _env));
        Assert.NotNull(CleanupRunner.Refusal(@"C:\", @"C:\", _env));
        Assert.NotNull(CleanupRunner.Refusal(Path.Combine(Path.GetTempPath(), "somewhere-else"), _root, _env));
        Assert.NotNull(CleanupRunner.Refusal(Path.Combine(_root, "no-such-folder"), _root, _env));
        Assert.Null(CleanupRunner.Refusal(Cache, _root, _env));

        var outcome = await CleanupRunner.RunAsync(tree, forbidden.Select(tree.Find).Append(SizeTree.Root).ToList(), contentsOnly: true, minAgeDays: 0, _env);
        Assert.Equal(0, outcome.FilesDeleted);
        Assert.Equal(forbidden.Length, outcome.Refused.Count);
        Assert.Empty(outcome.Removed);
        Assert.Equal(6, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task TheAnalysisAndTheRunnerAgreeOnWhatIsSafe()
    {
        Write(@"Users\me\AppData\Local\Browser\Cache\data_0", 2_000_000);
        Write(@"Users\me\AppData\Local\Browser\Bookmarks", 1000);
        Write(@"Users\me\AppData\Local\Temp\old.tmp", 1_500_000, daysOld: 9);
        Write(@"ProgramData\Shared\Cache\blob", 3_000_000);
        var tree = SizeTreeLoader.FromDisk(_root);
        var report = UsageAnalyzer.Analyze(tree, _env);

        // Everything the analysis offers to clear, the runner is willing to clear; and it clears exactly that.
        var clearable = report.Candidates.Where(c => c.Action == CleanupAction.Clear).ToList();
        Assert.Equal(new[] { "app-caches", "temp" }, clearable.Select(c => c.Id).OrderBy(id => id).ToArray());
        long freed = 0;
        foreach (var candidate in clearable)
        {
            var outcome = await CleanupRunner.RunAsync(tree, candidate.Nodes, candidate.ContentsOnly, candidate.MinAgeDays, _env);
            Assert.Empty(outcome.Refused);
            Assert.Equal(candidate.Bytes, outcome.BytesFreed);
            freed += outcome.BytesFreed;
            tree.RemoveMany(outcome.Removed);
        }
        Assert.Equal(report.SafeBytes, freed);
        Assert.True(File.Exists(Path.Combine(_root, @"ProgramData\Shared\Cache\blob")));
        Assert.True(File.Exists(Path.Combine(_root, @"Users\me\AppData\Local\Browser\Bookmarks")));
        // Asked again, there is nothing left to offer.
        Assert.DoesNotContain(UsageAnalyzer.Analyze(tree, _env).Candidates, c => c.Action == CleanupAction.Clear);
    }

    [Fact]
    public void PickingOutFoldersLightsEverythingInsideThem()
    {
        var b = new SizeTree.Builder(@"C:\");
        int app = b.AddFolder(SizeTree.Root, "app");
        int inner = b.AddFolder(app, "inner");
        b.AddFile(inner, "a.bin", 4000);
        b.AddFile(app, "b.bin", 2000);
        b.AddFile(SizeTree.Root, "other.bin", 4000);
        var tree = b.Build();

        var plain = TreemapRenderer.Render(tree, SizeTree.Root, 300, 200);
        var picked = TreemapRenderer.Render(tree, SizeTree.Root, 300, 200, new TreemapHighlight(-1, null, new HashSet<int> { app }));
        int At(string path)
        {
            var rect = plain.Rects[tree.Find(path)];
            return (rect.Y + rect.H / 2) * 300 + rect.X + rect.W / 2;
        }
        static int Brightness(int pixel) => ((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF);
        // Files anywhere under the folder keep their colour; a file outside it is dimmed.
        Assert.Equal(plain.Pixels[At(@"C:\app\inner\a.bin")], picked.Pixels[At(@"C:\app\inner\a.bin")]);
        Assert.Equal(plain.Pixels[At(@"C:\app\b.bin")], picked.Pixels[At(@"C:\app\b.bin")]);
        Assert.True(Brightness(picked.Pixels[At(@"C:\other.bin")]) < Brightness(plain.Pixels[At(@"C:\other.bin")]) / 2);
    }
}
