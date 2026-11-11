using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Api;
using FastFind.Core.Indexing;
using Xunit;

namespace FastFind.App.Tests;

/// <summary>Runs a test body on an STA thread with a live dispatcher, which is what the view models expect to be created on.</summary>
internal static class Ui
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var frame = new DispatcherFrame();
            Task task;
            try { task = body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); return; }
            task.ContinueWith(t =>
            {
                if (t.Exception != null) failure = ExceptionDispatchInfo.Capture(t.Exception.GetBaseException());
                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    public static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }
}

/// <summary>An index that covers nothing (so tabs use the live file system) but can hand out folder sizes.</summary>
internal sealed class StubIndex : IIndexService
{
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Searches;

    public bool IsRemote => false;
    public bool IsElevated => false;
    public IReadOnlyList<VolumeInfo> Volumes => Array.Empty<VolumeInfo>();
    public event Action? VolumesChanged { add { } remove { } }
    public event Action? IndexChanged { add { } remove { } }

    public Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        Searches++;
        return Task.FromResult<IResultSet>(EmptyResultSet.Instance);
    }

    public Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, long>>(Sizes);

    public void Configure(IndexSettings settings) { }
    public void Rebuild(string? root) { }
    public void RefreshVolumes() { }
    public void Dispose() { }
}

public sealed class TabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFindAppTests_" + Guid.NewGuid().ToString("N"));

    public TabTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Docs", "Old"));
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));
        File.WriteAllText(Path.Combine(_root, "readme.md"), "hello");
        File.WriteAllText(Path.Combine(_root, "Notes.txt"), "some notes");
        File.WriteAllText(Path.Combine(_root, "Docs", "plan.txt"), "plan");
        File.WriteAllText(Path.Combine(_root, "Docs", "Old", "archive.txt"), "old");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private static string[] Names(FileTabViewModel tab) => tab.Results.Select(r => r.Name).ToArray();

    private static FileTabViewModel Open(IIndexService index, string location)
    {
        // Name order regardless of what the developer's own settings say.
        App.Settings.SortField = "Name";
        App.Settings.SortDescending = false;
        App.Settings.HideHiddenSystemByDefault = false;
        var tab = new FileTabViewModel(index, location) { IsSelectedTab = true };
        tab.Start();
        return tab;
    }

    [Fact]
    public void FolderTabListsLiveContentsFoldersFirstAndFilters() => Ui.Run(async () =>
    {
        using var tab = Open(new StubIndex(), _root);
        await Ui.WaitFor(() => tab.Results.Count == 4, "the folder to load");
        Assert.Equal(new[] { "Docs", "Empty", "Notes.txt", "readme.md" }, Names(tab));
        Assert.Equal(ViewMode.Folder, tab.Mode);
        Assert.False(tab.ShowPathColumn);
        Assert.Equal(Path.GetFileName(_root), tab.Title);
        Assert.StartsWith("4 items", tab.StatusText);

        tab.QueryText = "ext:txt";
        await Ui.WaitFor(() => tab.Results.Count == 1, "the filter to apply");
        Assert.Equal("Notes.txt", tab.Results[0].Name);
        Assert.StartsWith("1 of 4 items", tab.StatusText);

        tab.QueryText = "nothing-matches-this";
        await Ui.WaitFor(() => tab.Results.Count == 0, "the filter to exclude everything");
        Assert.Equal("No matches in this folder", tab.EmptyText);
    });

    [Fact]
    public void NavigationKeepsHistoryAndReselectsTheFolderYouCameFrom() => Ui.Run(async () =>
    {
        using var tab = Open(new StubIndex(), _root);
        await Ui.WaitFor(() => tab.Results.Count == 4, "the folder to load");
        Assert.False(tab.CanGoBack);

        tab.QueryText = "Docs";
        Assert.True(tab.Navigate(Path.Combine(_root, "Docs")));
        await Ui.WaitFor(() => tab.Results.Count == 2 && tab.Location.EndsWith("Docs"), "Docs to load");
        Assert.Equal("", tab.QueryText); // a filter belongs to the folder it was typed in
        Assert.Equal(new[] { "Old", "plan.txt" }, Names(tab));
        Assert.True(tab.CanGoBack);
        Assert.True(tab.CanGoUp);
        Assert.Equal("Docs", tab.Breadcrumbs[^1].Name);

        tab.GoUp();
        await Ui.WaitFor(() => tab.Results.Count == 4, "the parent to load");
        Assert.Equal("Docs", tab.PendingSelectName); // the view selects the folder just left

        tab.GoBack();
        await Ui.WaitFor(() => tab.Location.EndsWith("Docs") && tab.Results.Count == 2, "back to Docs");
        Assert.True(tab.CanGoForward);
        tab.GoForward();
        await Ui.WaitFor(() => tab.Location == _root && tab.Results.Count == 4, "forward to the root");

        Assert.False(tab.Navigate("not a path"));
        Assert.Equal(_root, tab.Location);
    });

    [Fact]
    public void SubfoldersFallsBackToALiveScanWhenTheFolderIsNotIndexed() => Ui.Run(async () =>
    {
        var index = new StubIndex();
        using var tab = Open(index, _root);
        await Ui.WaitFor(() => tab.Results.Count == 4, "the folder to load");

        tab.Recursive = true;
        await Ui.WaitFor(() => tab.Results.Count == 7, "the whole tree");
        Assert.Equal(ViewMode.Subtree, tab.Mode);
        Assert.True(tab.ShowPathColumn);
        Assert.Contains("archive.txt", Names(tab));
        Assert.Contains("scanned live", tab.StatusText);
        Assert.Equal(0, index.Searches); // nothing was asked of an index that does not cover the folder

        tab.QueryText = "ext:txt";
        await Ui.WaitFor(() => tab.Results.Count == 3, "the query over the tree");
        Assert.Equal(Path.Combine(_root, "Docs", "Old"), tab.Results.First(r => r.Name == "archive.txt").Directory);
    });

    [Fact]
    public void FolderRefreshesWhenFilesChangeOnDisk() => Ui.Run(async () =>
    {
        using var tab = Open(new StubIndex(), _root);
        await Ui.WaitFor(() => tab.Results.Count == 4, "the folder to load");
        await Task.Delay(400); // the watcher is attached just after the first load

        File.WriteAllText(Path.Combine(_root, "added.txt"), "x");
        await Ui.WaitFor(() => Names(tab).Contains("added.txt"), "the new file to appear");

        File.Delete(Path.Combine(_root, "added.txt"));
        await Ui.WaitFor(() => !Names(tab).Contains("added.txt"), "the deleted file to go");
    });

    [Fact]
    public void FolderSizesFromTheIndexFillInAndSort() => Ui.Run(async () =>
    {
        var index = new StubIndex { Sizes = { ["Docs"] = 5_000_000, ["Empty"] = 0 } };
        using var tab = Open(index, _root);
        await Ui.WaitFor(() => tab.Results.Count == 4 && tab.Results[0].Size == 5_000_000, "folder sizes to arrive");
        Assert.Equal("Docs", tab.Results[0].Name);
        Assert.Equal("", tab.Results[1].SizeText); // an empty folder shows nothing rather than "0 B"

        tab.SetSort(FastFind.Core.Search.SortField.Size, descending: true);
        await Ui.WaitFor(() => tab.Results.Count == 4 && tab.Results[0].Name == "Docs" && tab.Results[2].Name == "Notes.txt", "the size sort");
    });

    [Fact]
    public void UnreadableFolderExplainsItself() => Ui.Run(async () =>
    {
        using var tab = Open(new StubIndex(), Path.Combine(_root, "does-not-exist"));
        await Ui.WaitFor(() => tab.HasError, "the error");
        Assert.Equal(0, tab.Results.Count);
        Assert.Contains("no longer exists", tab.EmptyText);
    });

    [Fact]
    public void EverywhereTabSearchesTheIndex() => Ui.Run(async () =>
    {
        var index = new StubIndex();
        using var tab = Open(index, "");
        await Ui.WaitFor(() => index.Searches > 0, "the index to be asked");
        Assert.True(tab.IsEverywhere);
        Assert.Equal("Everywhere", tab.Title);
        Assert.False(tab.CanCreate);
        Assert.False(tab.CanPaste);
        tab.Recursive = true;
        Assert.False(tab.Recursive); // meaningless without a folder
    });
}

public sealed class FileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFindAppTests_" + Guid.NewGuid().ToString("N"));

    public FileServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void RestartArgumentIsTakenOutOfTheCommandLine()
    {
        // "--wait-for <pid>" is how a restarting instance tells its replacement to wait; it must not be mistaken for a folder to open.
        var args = new List<string> { "--wait-for", "4242", @"C:\Windows" };
        Assert.Equal(4242, FastFind.App.App.TakeWaitForPid(args));
        Assert.Equal(new[] { @"C:\Windows" }, args);

        args = new List<string> { @"C:\Windows" };
        Assert.Null(FastFind.App.App.TakeWaitForPid(args));
        Assert.Single(args);

        args = new List<string> { "--wait-for" }; // malformed: dropped, nothing to wait for
        Assert.Null(FastFind.App.App.TakeWaitForPid(args));
        Assert.Empty(args);
    }

    [Fact]
    public void DraggedFilesUseTheShellsDataObject() => Ui.Run(() =>
    {
        // Two files in different folders, as a search result would give.
        Directory.CreateDirectory(Path.Combine(_root, "one"));
        Directory.CreateDirectory(Path.Combine(_root, "two"));
        string a = Path.Combine(_root, "one", "a.txt"), b = Path.Combine(_root, "two", "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var data = ShellDataObject.ForDrag(new[] { a, b });
        var dropped = Assert.IsType<string[]>(data.GetData(System.Windows.DataFormats.FileDrop));
        Assert.Equal(new[] { "a.txt", "b.txt" }, dropped.Select(Path.GetFileName).Order());
        Assert.All(dropped, p => Assert.True(File.Exists(p)));
        Assert.True(data.GetDataPresent("Shell IDList Array")); // what Explorer's own drags carry
        Assert.False(data.GetDataPresent(System.Windows.DataFormats.UnicodeText)); // text alongside files confuses Teams
        Assert.False(data.GetDataPresent(System.Windows.DataFormats.Text));

        // A file that has gone since it was listed still drags, as a plain file list.
        string missing = Path.Combine(_root, "gone.txt");
        var plain = ShellDataObject.ForDrag(new[] { a, missing });
        Assert.Equal(new[] { a, missing }, Assert.IsType<string[]>(plain.GetData(System.Windows.DataFormats.FileDrop)));
        Assert.False(plain.GetDataPresent(System.Windows.DataFormats.UnicodeText));
        return Task.CompletedTask;
    });

    [Fact]
    public void UniqueNameAvoidsExistingFilesAndFolders()
    {
        Assert.Equal("New folder", FileOperations.UniqueName(_root, "New folder"));
        Directory.CreateDirectory(Path.Combine(_root, "New folder"));
        Assert.Equal("New folder (2)", FileOperations.UniqueName(_root, "New folder"));
        File.WriteAllText(Path.Combine(_root, "New file.txt"), "");
        File.WriteAllText(Path.Combine(_root, "New file (2).txt"), "");
        Assert.Equal("New file (3).txt", FileOperations.UniqueName(_root, "New file", ".txt"));
    }

    [Fact]
    public void CreateFolderAndFileValidateNames()
    {
        Assert.NotNull(FileOperations.CreateFolder(_root, "Reports", out string? error));
        Assert.Null(error);
        Assert.Null(FileOperations.CreateFolder(_root, "Reports", out error));
        Assert.Contains("already exists", error);
        Assert.Null(FileOperations.CreateFolder(_root, "a:b", out error));
        Assert.NotNull(error);
        Assert.Null(FileOperations.CreateFile(_root, "trailing. ", out error));
        Assert.NotNull(error);
        string? file = FileOperations.CreateFile(_root, "notes.txt", out error);
        Assert.True(File.Exists(file));
        Assert.Null(FileOperations.CreateFile(_root, "notes.txt", out error)); // never overwrites
    }

    [Fact]
    public void ZipRoundTripKeepsFoldersAndEmptyFolders()
    {
        string source = Path.Combine(_root, "Project");
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "readme.md"), "read me");
        File.WriteAllText(Path.Combine(source, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "loose.txt"), "loose");

        string zip = ZipOps.Compress(new[] { source }, _root);
        Assert.Equal(Path.Combine(_root, "Project.zip"), zip);
        Assert.False(File.Exists(zip + ".partial"));
        // A second archive of the same thing gets a new name instead of overwriting.
        Assert.Equal(Path.Combine(_root, "Project (2).zip"), ZipOps.Compress(new[] { source }, _root));

        string extracted = ZipOps.Extract(zip);
        Assert.Equal(Path.Combine(_root, "Project (2)"), extracted); // "Project" already exists beside the archive
        Assert.Equal("read me", File.ReadAllText(Path.Combine(extracted, "Project", "readme.md")));
        Assert.Equal("class A {}", File.ReadAllText(Path.Combine(extracted, "Project", "src", "a.cs")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "Project", "empty")));

        // Several items: the archive is named after the folder they are in.
        string multi = ZipOps.Compress(new[] { source, Path.Combine(_root, "loose.txt") }, _root);
        Assert.Equal(Path.GetFileName(_root) + ".zip", Path.GetFileName(multi));
    }

    [Fact]
    public void ExportWritesCsvForAllRowsOrASelection()
    {
        var index = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        long when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
        int docs = index.Add(0, "Docs", index.RootIndex, FileAttributes.Directory, 0, when, when);
        index.Add(0, "a, b.txt", docs, FileAttributes.Normal, 12, when, when);
        index.Add(0, "c.pdf", docs, FileAttributes.Normal, 3456, when, when);
        using var set = new LocalResultSet(FastFind.Core.Search.SearchEngine.Search(new[] { index }, FastFind.Core.Search.QueryParser.Parse("")));

        string csv = Path.Combine(_root, "all.csv");
        Assert.Equal(3, ResultExporter.Export(set, null, csv));
        var lines = File.ReadAllLines(csv);
        Assert.Equal("Name,Path,Full Path,Size,Date Modified,Date Created,Type", lines[0]);
        Assert.StartsWith("\"a, b.txt\",C:\\Docs,\"C:\\Docs\\a, b.txt\",12,", lines[1]); // commas are quoted
        Assert.EndsWith(",PDF", lines[2]);
        Assert.EndsWith(",Folder", lines[3]);

        string txt = Path.Combine(_root, "some.txt");
        Assert.Equal(2, ResultExporter.Export(set, new[] { 0, 2 }, txt));
        Assert.Equal(new[] { @"C:\Docs\a, b.txt", @"C:\Docs" }, File.ReadAllLines(txt));
    }

    [Fact]
    public void ShortcutTargetIsOnlyReportedForFolders()
    {
        Assert.Null(ShellLinks.FolderTarget(Path.Combine(_root, "plain.txt")));
        Assert.Null(ShellLinks.FolderTarget(Path.Combine(_root, "missing.lnk")));
    }
}

public sealed class OtherPaneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFindAppTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _docs, _old, _empty;

    public OtherPaneTests()
    {
        _docs = Path.Combine(_root, "Docs");
        _old = Path.Combine(_docs, "Old");
        _empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(_old);
        Directory.CreateDirectory(_empty);
        File.WriteAllText(Path.Combine(_docs, "plan.txt"), "plan");
    }

    public void Dispose()
    {
        App.Settings.Session = null;
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private MainViewModel Start(bool dualPane)
    {
        App.Settings.SortField = "Name";
        App.Settings.SortDescending = false;
        App.Settings.DualPane = dualPane;
        App.Settings.Session = new SessionState
        {
            Left = { new TabState { Location = _root } },
            Right = { new TabState { Location = _empty } },
        };
        // The index host is never started: folder views read the file system and need no index.
        return new MainViewModel(new IndexHost(new IndexSettings()), Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void FromASinglePaneItBringsUpTheSecondPaneAtThatFolder() => Ui.Run(async () =>
    {
        var main = Start(dualPane: false);
        Assert.False(main.IsDualPane);

        main.OpenInOtherPane(main.Left, _docs);

        Assert.True(main.IsDualPane);
        Assert.Same(main.Left, main.ActivePane); // you stay where you were
        await Ui.WaitFor(() => main.Right.SelectedTab!.Location == _docs && main.Right.SelectedTab.Results.Count == 2, "the right pane to show Docs");
        Assert.Equal(_root, main.Left.SelectedTab!.Location); // the pane you were in is untouched
        Assert.Single(main.Right.Tabs);
    });

    [Fact]
    public void FromTheLeftItOpensOnTheRightAndFromTheRightOnTheLeft() => Ui.Run(async () =>
    {
        var main = Start(dualPane: true);

        main.OpenInOtherPane(main.Left, _docs);
        await Ui.WaitFor(() => main.Right.SelectedTab!.Location == _docs, "the right pane to show Docs");
        Assert.Equal(_root, main.Left.SelectedTab!.Location);

        main.ActivePane = main.Right;
        main.OpenInOtherPane(main.Right, _old);
        await Ui.WaitFor(() => main.Left.SelectedTab!.Location == _old, "the left pane to show Old");
        Assert.Equal(_docs, main.Right.SelectedTab!.Location);
        Assert.Same(main.Right, main.ActivePane);
        Assert.True(main.Left.SelectedTab.CanGoBack); // it navigated the existing tab, so Back returns to what was there
    });

    [Fact]
    public void AFileIsShownSelectedInItsFolderAndATabCanBeSentAcrossAsANewTab() => Ui.Run(async () =>
    {
        var main = Start(dualPane: true);

        main.OpenInOtherPane(main.Left, _docs, select: "plan.txt");
        await Ui.WaitFor(() => main.Right.SelectedTab!.Location == _docs && main.Right.SelectedTab.Results.Count == 2, "the right pane to show Docs");
        Assert.Equal("plan.txt", main.Right.SelectedTab!.PendingSelectName);

        main.OpenInOtherPane(main.Left, _old, newTab: true);
        Assert.Equal(2, main.Right.Tabs.Count);
        Assert.Equal(_old, main.Right.SelectedTab!.Location);
    });
}
