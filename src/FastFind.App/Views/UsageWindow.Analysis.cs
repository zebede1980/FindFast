using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FastFind.App.Services;
using FastFind.App.Treemap;
using FastFind.App.ViewModels;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.App.Views;

// The analysis side of the disk usage window: what each application takes, and what could be removed.
public partial class UsageWindow
{
    private readonly UsageEnvironment _env = UsageEnvironment.Current(KnownFolders.Downloads);
    private UsageReport? _report;
    private bool _clearing;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    /// <summary>Works out the applications and clean-up candidates for the tree on show. Quick enough (tens of milliseconds for a whole drive) to do in place.</summary>
    private void Analyse()
    {
        var tree = _tree;
        if (tree == null) return;
        try
        {
            _report = UsageAnalyzer.Analyze(tree, _env);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Disk usage analysis failed for {_folder}: {ex}");
            _report = new UsageReport { Apps = Array.Empty<AppUsage>(), Candidates = Array.Empty<CleanupCandidate>() };
        }
        var report = _report;

        long largest = report.Apps.Count > 0 ? report.Apps[0].Total : 0;
        _syncing = true;
        try { Apps.ItemsSource = report.Apps.Select(a => new UsageAppRow(a, largest)).ToList(); }
        finally { _syncing = false; }
        AppsEmpty.Visibility = report.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        long total = tree.Size(SizeTree.Root);
        AppsSummary.Text = report.Apps.Count == 0 ? ""
            : $"{report.Apps.Count:N0} applications take {SizeParser.Format(report.AppBytes)}" + (total > 0 ? $", {(double)report.AppBytes / total:0%} of what was measured." : ".")
              + (tree.Find(_env.ProgramFiles) < 0 ? " Program Files is outside this folder, so the programs themselves are not counted." : "");

        // Safest first; within a group, biggest first. Each group gets a heading on its first row.
        var rows = new List<UsageCleanupRow>();
        CleanupSafety? group = null;
        foreach (var candidate in report.Candidates.OrderBy(c => c.Safety).ThenByDescending(c => c.Bytes))
        {
            rows.Add(new UsageCleanupRow(candidate, candidate.Safety != group ? UsageCleanupRow.GroupName(candidate.Safety) : null));
            group = candidate.Safety;
        }
        Cleanup.ItemsSource = rows;
        CleanupEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        long safe = report.SafeBytes;
        long rebuildable = report.Candidates.Where(c => c.Safety == CleanupSafety.Rebuildable).Sum(c => c.Bytes);
        long other = report.Candidates.Where(c => c.Safety is CleanupSafety.Windows or CleanupSafety.Review).Sum(c => c.Bytes);
        var parts = new List<string>();
        if (safe > 0) parts.Add($"{SizeParser.Format(safe)} can be cleared now with nothing lost.");
        if (rebuildable > 0) parts.Add($"{SizeParser.Format(rebuildable)} more can be rebuilt if cleared.");
        if (other > 0) parts.Add($"{SizeParser.Format(other)} is worth a look.");
        if (rows.Count > 0) parts.Add("Click an entry to see it on the map.");
        CleanupSummary.Text = string.Join(" ", parts);
        CleanupBadge.Text = safe >= 1024 * 1024 ? SizeParser.Format(safe) : "";
    }

    private void OnPanelTabChecked(object sender, RoutedEventArgs e)
    {
        // Raised for the first time while the window is still being built, before the panels exist.
        if (AppsPanel == null || CleanupPanel == null || Types == null) return;
        Types.Visibility = TypesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = AppsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CleanupPanel.Visibility = CleanupTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"1 file", "2,345 files".</summary>
    private static string Count(int number, string noun) => $"{number:N0} {noun}{(number == 1 ? "" : "s")}";

    // ---- pointing things out ----

    /// <summary>Lights up a set of folders on the map (an application's, or what a clean-up would remove) and finds the biggest in the tree.</summary>
    private void PickOut(IReadOnlyList<int> nodes)
    {
        var tree = _tree;
        if (tree == null) return;
        var live = nodes.Where(n => !tree.IsRemoved(n)).ToList();
        if (live.Count == 0) return;
        foreach (var k in _kinds) k.IsPicked = false;
        _syncing = true;
        try { Types.SelectedItem = null; } finally { _syncing = false; }
        // Zoomed in somewhere that shows none of it: step back out so there is something to see.
        if (Map.Root != SizeTree.Root && !live.Any(n => tree.IsSameOrInside(n, Map.Root) || tree.IsSameOrInside(Map.Root, n))) Map.ZoomTo(SizeTree.Root);
        Map.Highlight = new TreemapHighlight(-1, null, new HashSet<int>(live));
        int biggest = live.MaxBy(tree.Size);
        _lastClicked = -1;
        Select(biggest, reveal: true);
        // The highlight already shows where everything is; a spotlight on one folder would dim the rest of them.
        Map.Selected = -1;
        long bytes = live.Sum(tree.Size);
        SelectionText.Text = $"{Count(live.Count, "item")} picked out  ·  {SizeParser.Format(bytes)}  ·  Esc to clear";
    }

    private void OnAppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (Apps.SelectedItem is UsageAppRow row) PickOut(row.App.Roots);
        else if (Map.Highlight?.Folders != null) Map.Highlight = null;
    }

    private void OnCleanupCardClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UsageCleanupRow row) return;
        _syncing = true;
        try { Apps.SelectedItem = null; } finally { _syncing = false; }
        PickOut(row.Candidate.Nodes);
        e.Handled = true;
    }

    // ---- acting ----

    private void OnAppClearClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UsageAppRow row) return;
        var app = row.App;
        _ = ClearAsync($"the caches of {app.Name}", app.ClearableCaches, contentsOnly: true, minAgeDays: 0, CleanupSafety.Safe, app.ClearableBytes, app.ClearableFiles);
        e.Handled = true;
    }

    private void OnCleanupActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UsageCleanupRow row) return;
        var candidate = row.Candidate;
        e.Handled = true;
        switch (candidate.Action)
        {
            case CleanupAction.Clear:
                _ = ClearAsync(char.ToLowerInvariant(candidate.Title[0]) + candidate.Title[1..], candidate.Nodes, candidate.ContentsOnly, candidate.MinAgeDays, candidate.Safety, candidate.Bytes, candidate.Files);
                break;
            case CleanupAction.EmptyRecycleBin:
                EmptyRecycleBin();
                break;
            case CleanupAction.DiskCleanup:
                try { Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
                catch (Exception ex) { StatusText.Text = $"Disk Clean-up could not be opened: {ex.Message}"; }
                break;
            case CleanupAction.CopyCommand when candidate.Command != null:
                ShellOps.CopyText(candidate.Command);
                StatusText.Text = $"Copied '{candidate.Command}'. Paste it into a terminal opened as administrator.";
                break;
        }
    }

    private void EmptyRecycleBin()
    {
        var tree = _tree;
        if (tree == null) return;
        // Windows asks for confirmation itself and shows its own progress.
        int result = SHEmptyRecycleBinW(new WindowInteropHelper(this).Handle, System.IO.Path.GetPathRoot(tree.RootPath), 0);
        if (result == 0)
        {
            Logger.Info($"Disk usage: emptied the Recycle Bin on {System.IO.Path.GetPathRoot(tree.RootPath)}");
            _ = LoadAsync(); // measure again: the index hears of the deletions within a moment
        }
    }

    /// <summary>Asks, then deletes (see <see cref="CleanupRunner"/>), then takes what went off the map and out of the lists.</summary>
    private async Task ClearAsync(string what, IReadOnlyList<int> nodes, bool contentsOnly, int minAgeDays, CleanupSafety safety, long bytes, int files)
    {
        var tree = _tree;
        if (tree == null || _clearing) return;
        var live = nodes.Where(n => !tree.IsRemoved(n) && tree.Size(n) > 0).ToList();
        if (live.Count == 0) return;
        // Show what is about to happen before anything does.
        PickOut(live);
        var biggest = live.OrderByDescending(tree.Size).Take(5).Select(n => $"    {SizeParser.Format(tree.Size(n))}   {tree.GetPath(n)}");
        string text = $"Clear {what}?\n\n"
            + $"Up to {SizeParser.Format(bytes)} in {Count(files, "file")} will be deleted for good, not sent to the Recycle Bin. "
            + (safety == CleanupSafety.Safe
                ? "Nothing of yours is lost: these are copies programs keep to save time, and they make them again as they need them."
                : "It can be downloaded or built again, which takes a while the next time it is needed.")
            + (minAgeDays > 0 ? $" Files less than {minAgeDays} days old are left alone." : "")
            + "\n\nFiles in use are skipped. To clear the most, close the applications concerned first."
            + $"\n\n{live.Count:N0} folder{(live.Count == 1 ? "" : "s")}" + (live.Count > 5 ? ", the biggest:" : ":") + "\n" + string.Join("\n", biggest);
        var answer = MessageBox.Show(this, text, "Clean up", MessageBoxButton.YesNo, safety == CleanupSafety.Safe ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await ClearConfirmedAsync(tree, what, live, contentsOnly, minAgeDays, files);
    }

    /// <summary>Carries out a clean-up the user has agreed to.</summary>
    private async Task ClearConfirmedAsync(SizeTree tree, string what, IReadOnlyList<int> live, bool contentsOnly, int minAgeDays, int files)
    {
        if (_clearing) return;
        _clearing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(done => StatusText.Text = $"Clearing {what}: {done:N0} of {files:N0} files…");
            StatusText.Text = $"Clearing {what}…";
            var outcome = await CleanupRunner.RunAsync(tree, live, contentsOnly, minAgeDays, _env, progress);
            Logger.Info($"Disk usage clean-up of {what} under {_folder}: {outcome.FilesDeleted:N0} files deleted, {outcome.BytesFreed:N0} bytes freed, {outcome.Left:N0} left"
                + (outcome.Refused.Count > 0 ? "; refused: " + string.Join("; ", outcome.Refused) : ""));

            if (ReferenceEquals(tree, _tree) && _rows != null)
            {
                tree.RemoveMany(outcome.Removed);
                _lastClicked = -1;
                _syncing = true;
                try { _rows.Rebuild(); } finally { _syncing = false; }
                Map.Highlight = null;
                Map.Refresh();
                OnMapRootChanged();
                UpdateSummary();
                Analyse();
                Select(-1, reveal: false);
            }
            (Application.Current?.MainWindow as MainWindow)?.ViewModel.RefreshFolderViews();

            string message = outcome.FilesDeleted == 0 ? "Nothing could be cleared." : $"Cleared {SizeParser.Format(outcome.BytesFreed)} ({Count(outcome.FilesDeleted, "file")}).";
            if (outcome.Left > 0) message += $" {Count(outcome.Left, "file")} in use{(minAgeDays > 0 ? " or too new" : "")} {(outcome.Left == 1 ? "was" : "were")} left.";
            if (outcome.Refused.Count > 0) message += $" {outcome.Refused.Count:N0} folder{(outcome.Refused.Count == 1 ? " was" : "s were")} not touched (see fastfind.log).";
            StatusText.Text = message;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Disk usage clean-up of {what} failed: {ex}");
            StatusText.Text = $"The clean-up stopped: {ex.Message}";
        }
        finally
        {
            _clearing = false;
            RefreshButton.IsEnabled = true;
        }
    }
}
