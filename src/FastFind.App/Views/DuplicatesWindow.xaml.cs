using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Browse;
using FastFind.Core.Duplicates;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.App.Views;

/// <summary>
/// Files with identical content under a folder (or on a whole drive), grouped, with the group wasting the most space
/// at the top. Sizes come from the index when it covers the folder, so only files that share a size with another are
/// ever opened; those are compared by content, never by name or date. Tick the copies that can go and send them to
/// the Recycle Bin: one copy of every file always stays.
/// </summary>
public partial class DuplicatesWindow : Window
{
    private static readonly List<DuplicatesWindow> OpenWindows = new();

    private readonly string _folder;
    private readonly bool _settingUp;
    private DuplicateList? _list;
    private CancellationTokenSource? _searching;
    private long _minSize;
    private bool _deleting;

    /// <summary>Opens the duplicates window for a folder, or brings forward the one already showing it.</summary>
    public static void ShowFor(string folder)
    {
        folder = PathUtil.Normalize(folder);
        if (folder.Length == 0) return;
        var existing = OpenWindows.FirstOrDefault(w => string.Equals(w._folder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        new DuplicatesWindow(folder).Show();
    }

    private DuplicatesWindow(string folder)
    {
        _settingUp = true;
        InitializeComponent();
        GlassChrome.Attach(this, RootGrid, Aurora, CaptionSpacer, captionHeight: 62); // the header doubles as the title bar
        _folder = folder;
        Title = $"Duplicate files - {folder}";
        TitleText.Text = folder;
        var s = App.Settings;
        Width = Math.Max(MinWidth, s.DuplicatesWindowWidth);
        Height = Math.Max(MinHeight, s.DuplicatesWindowHeight);
        if (s.DuplicatesWindowMaximized) WindowState = WindowState.Maximized;

        // The nearest choice at or below the remembered size.
        _minSize = Math.Max(1, s.DuplicatesMinSize);
        var choices = MinSizeBox.Items.OfType<ComboBoxItem>().ToList();
        MinSizeBox.SelectedItem = choices.LastOrDefault(c => SizeOf(c) <= _minSize) ?? choices[0];
        _minSize = SizeOf((ComboBoxItem)MinSizeBox.SelectedItem);
        _settingUp = false;

        PreviewKeyDown += OnPreviewKeyDown;
        OpenWindows.Add(this);
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            _searching?.Cancel();
            s.DuplicatesWindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { s.DuplicatesWindowWidth = Width; s.DuplicatesWindowHeight = Height; }
            s.Save();
        };
        Loaded += (_, _) => _ = SearchAsync();
    }

    private static long SizeOf(ComboBoxItem item) => long.Parse((string)item.Tag);

    private string SizeWords => _minSize <= 1 ? "" : " of " + (string)((ComboBoxItem)MinSizeBox.SelectedItem).Content;

    // ---- searching ----

    private async Task SearchAsync()
    {
        _searching?.Cancel();
        var cancel = _searching = new CancellationTokenSource();
        var ct = cancel.Token;
        _list = null;
        List.ItemsSource = null;
        TickBar.IsEnabled = false;
        UpdateTicked();
        LoadingPanel.Visibility = LoadingBar.Visibility = StopButton.Visibility = Visibility.Visible;
        LoadingBar.IsIndeterminate = true;
        LoadingText.Text = "Looking for files of the same size…";
        LoadingDetail.Text = "";
        SummaryText.Text = StatusText.Text = "";
        RefreshButton.IsEnabled = false;
        var sw = Stopwatch.StartNew();
        long minSize = _minSize;
        try
        {
            if (!Directory.Exists(_folder)) throw new DirectoryNotFoundException($"{_folder} cannot be opened.");
            var env = UsageEnvironment.Current();
            var index = App.Index;
            // Opened in the first moments after start-up, the index may not have been found yet; a short wait beats a slow disk read.
            for (int i = 0; i < 40 && !index.HasSource; i++) await Task.Delay(100, ct);

            DuplicateCandidateSet? candidates = null;
            bool fromIndex = false;
            if (index.Volumes.Any(v => v.Covers(_folder)))
            {
                var reading = new Progress<SizeTreeProgress>(p =>
                {
                    if (ct.IsCancellationRequested) return;
                    LoadingBar.IsIndeterminate = false;
                    LoadingBar.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    LoadingText.Text = $"Reading the sizes of {p.Total:N0} files from the index…";
                });
                try { candidates = await DuplicateCandidates.FromIndexAsync(index, _folder, minSize, env, reading, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Logger.Warn($"Duplicates: the index could not list {_folder}: {ex.Message}"); }
                fromIndex = candidates != null;
            }
            if (candidates == null)
            {
                LoadingBar.IsIndeterminate = true;
                LoadingText.Text = "Reading the disk…";
                var reading = new Progress<SizeTreeProgress>(p =>
                {
                    if (!ct.IsCancellationRequested) LoadingText.Text = $"Reading the disk… {p.Done:N0} items so far";
                });
                candidates = await Task.Run(() => DuplicateCandidates.FromDisk(_folder, minSize, env, reading, ct), ct);
            }
            ct.ThrowIfCancellationRequested();

            LoadingBar.IsIndeterminate = true;
            LoadingText.Text = "Comparing files of the same size…";
            var comparing = new Progress<DuplicateProgress>(p =>
            {
                if (ct.IsCancellationRequested) return;
                LoadingBar.IsIndeterminate = false;
                if (p.Stage == DuplicateStage.Sampling)
                {
                    LoadingBar.Value = p.FilesTotal > 0 ? (double)p.FilesDone / p.FilesTotal : 0;
                    LoadingText.Text = $"Comparing {p.FilesTotal:N0} files that share a size with another…";
                    LoadingDetail.Text = $"{p.FilesDone:N0} looked at";
                }
                else
                {
                    LoadingBar.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
                    LoadingText.Text = $"Reading {p.FilesTotal:N0} likely duplicates in full to be sure…";
                    LoadingDetail.Text = $"{SizeParser.Format(p.BytesDone)} of {SizeParser.Format(p.BytesTotal)}";
                }
            });
            var files = candidates.Files;
            var result = await Task.Run(() => DuplicateFinder.Find(files, minSize, comparing, ct), ct);
            ct.ThrowIfCancellationRequested();

            Show(result, candidates, (fromIndex ? "sizes from the index" : "read from disk") + $", {sw.Elapsed.TotalSeconds:0.0}s");
            Logger.Info($"Duplicates under {_folder}{SizeWords}: {result.Groups.Count:N0} groups, {result.Surplus:N0} spare files, {result.Wasted:N0} bytes; "
                + $"{result.Compared:N0} compared, {result.BytesRead:N0} bytes read in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_searching, cancel)) return; // a newer search has taken over the window
            ShowMessage("Stopped.", "Press F5 to search again.");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Logger.Warn($"Duplicates failed for {_folder}: {ex}");
            ShowMessage(ex.Message, "");
        }
    }

    private void Show(DuplicateResult result, DuplicateCandidateSet candidates, string source)
    {
        var list = _list = new DuplicateList(result.Groups);
        list.TicksChanged += UpdateTicked;
        List.ItemsSource = list.Rows;
        RefreshButton.IsEnabled = true;

        // What was not compared, so "nothing found" is never mistaken for "nothing there".
        var left = new List<string>();
        if (candidates.Protected > 0) left.Add($"{candidates.Protected:N0} in Windows and program folders");
        int online = candidates.OnlineOnly + result.OnlineOnly;
        if (online > 0) left.Add($"{online:N0} online-only");
        if (result.Links > 0) left.Add($"{result.Links:N0} links");
        if (result.Unreadable > 0) left.Add($"{result.Unreadable:N0} that could not be read");
        string leftOut = left.Count > 0 ? "Not compared: " + string.Join(", ", left) + "." : "";

        if (list.GroupCount == 0)
        {
            ShowMessage($"No duplicate files{SizeWords} here.", leftOut);
            SummaryText.Text = $"{candidates.Considered:N0} files looked at  ·  {source}";
            return;
        }
        LoadingPanel.Visibility = Visibility.Collapsed;
        TickBar.IsEnabled = true;
        StatusText.Text = leftOut.Length > 0 ? leftOut : "Tick the copies that can go. Space ticks the selected rows; right-click for more.";
        UpdateSummary(source);
        List.Focus();
    }

    private string _source = "";

    private void UpdateSummary(string? source = null)
    {
        if (source != null) _source = source;
        if (_list is not { } list) return;
        if (list.GroupCount == 0) { SummaryText.Text = _source; return; }
        SummaryText.Text = $"{list.Surplus:N0} spare {(list.Surplus == 1 ? "copy" : "copies")} in {list.GroupCount:N0} {(list.GroupCount == 1 ? "group" : "groups")}"
            + $"  ·  {SizeParser.Format(list.Wasted)} could be freed  ·  {_source}";
    }

    /// <summary>Puts a message where the list would be: nothing found, stopped, or what went wrong.</summary>
    private void ShowMessage(string text, string detail)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingBar.Visibility = StopButton.Visibility = Visibility.Collapsed;
        LoadingText.Text = text;
        LoadingDetail.Text = detail;
        RefreshButton.IsEnabled = true;
    }

    private void UpdateTicked()
    {
        var list = _list;
        int count = list?.TickedCount ?? 0;
        TickedText.Text = count == 0 ? "" : $"{count:N0} ticked  ·  {SizeParser.Format(list!.TickedBytes)}";
        DeleteButton.IsEnabled = count > 0 && !_deleting;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = SearchAsync();
    private void OnStopClick(object sender, RoutedEventArgs e) => _searching?.Cancel();

    private void OnMinSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingUp || MinSizeBox.SelectedItem is not ComboBoxItem item) return;
        _minSize = SizeOf(item);
        App.Settings.DuplicatesMinSize = _minSize;
        if (IsLoaded) _ = SearchAsync();
    }

    // ---- ticking ----

    private void OnTickClick(object sender, RoutedEventArgs e)
    {
        if (_list is not { } list || sender is not FrameworkElement { DataContext: DuplicateRow row }) return;
        Tick(list, new[] { row }, !row.IsTicked);
    }

    /// <summary>Ticks or unticks rows, saying so when the rule that one copy is always kept stopped some of them.</summary>
    private void Tick(DuplicateList list, IEnumerable<DuplicateRow> rows, bool ticked)
    {
        int refused = 0;
        foreach (var row in rows)
            if (row.IsFile && !list.SetTicked(row, ticked)) refused++;
        if (refused > 0) StatusText.Text = "One copy of every file is always kept: the last unticked copy in a group cannot be ticked.";
    }

    private void OnKeepNewestClick(object sender, RoutedEventArgs e) => _list?.KeepNewest();
    private void OnKeepOldestClick(object sender, RoutedEventArgs e) => _list?.KeepOldest();
    private void OnClearTicksClick(object sender, RoutedEventArgs e) => _list?.ClearTicks();

    private List<DuplicateRow> SelectedFiles() => List.SelectedItems.OfType<DuplicateRow>().Where(r => r.IsFile).ToList();

    // ---- keyboard and mouse ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && RefreshButton.IsEnabled) { _ = SearchAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape && StopButton.IsVisible) { _searching?.Cancel(); e.Handled = true; }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (_list is not { } list) return;
        var rows = SelectedFiles();
        if (rows.Count == 0) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Space && mods == ModifierKeys.None)
        {
            // The whole selection follows the first row, so a mixed selection ends up all one way.
            Tick(list, rows, !rows[0].IsTicked);
            e.Handled = true;
        }
        else if (key == Key.Enter && mods == ModifierKeys.None) { ShowInFastFind(rows[0]); e.Handled = true; }
        else if (key == Key.Enter && mods == ModifierKeys.Alt) { ShellOps.ShowProperties(rows[0].File!.Path); e.Handled = true; }
        else if (key == Key.C && mods == ModifierKeys.Control) { ShellOps.CopyText(string.Join(Environment.NewLine, rows.Select(r => r.File!.Path))); e.Handled = true; }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject d) return;
        // Two quick clicks on a tick box are two ticks, not a request to go to the file.
        if (MainWindow.FindVisualParent<CheckBox>(d) != null) return;
        if (MainWindow.FindVisualParent<DataGridRow>(d) is { DataContext: DuplicateRow { IsFile: true } row }) ShowInFastFind(row);
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = SelectedFiles();
        if (_list is not { } list || rows.Count == 0) { e.Handled = true; return; }
        var row = rows[0];
        string path = row.File!.Path, folder = row.File.Directory;
        ListMenu.Items.Clear();
        void Add(string header, Action action, string? gesture = null, bool bold = false)
        {
            var item = new MenuItem { Header = header };
            if (gesture != null) item.InputGestureText = gesture;
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => action();
            ListMenu.Items.Add(item);
        }
        Add("Show in _FastFind", () => ShowInFastFind(row), "Enter", bold: true);
        Add("_Open", () => ShellOps.Open(path));
        Add("Show in Windows _Explorer", () => ShellOps.ShowInExplorer(path));
        ListMenu.Items.Add(new Separator());
        Add(rows.Count == 1 ? (row.IsTicked ? "Un_tick" : "_Tick for deletion") : (row.IsTicked ? "Un_tick these" : "_Tick these for deletion"),
            () => Tick(list, rows, !row.IsTicked), "Space");
        Add("_Keep only this copy", () => list.KeepOnly(row));
        // An underscore in the folder's name would otherwise be taken as marking the entry's access key.
        Add($"Tick every copy in \"{PathUtil.LeafName(folder).Replace("_", "__")}\" and below", () =>
        {
            int ticked = list.TickInFolder(folder);
            StatusText.Text = ticked == 0 ? $"Nothing more to tick in {folder}: a copy of each file has to stay." : $"Ticked {ticked:N0} {(ticked == 1 ? "copy" : "copies")} in {folder}.";
        });
        ListMenu.Items.Add(new Separator());
        Add("Copy p_ath", () => ShellOps.CopyText(string.Join(Environment.NewLine, rows.Select(r => r.File!.Path))), "Ctrl+C");
        Add("P_roperties", () => ShellOps.ShowProperties(path), "Alt+Enter");
    }

    /// <summary>Shows the copy selected in its folder, in a new tab of the main window.</summary>
    private static void ShowInFastFind(DuplicateRow row)
    {
        if (row.File is not { } file || Application.Current?.MainWindow is not MainWindow main) return;
        main.ShowFromTray(search: false);
        main.ViewModel.Open(file.Directory, newTab: true, select: file.Name);
    }

    // ---- deleting ----

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var list = _list;
        if (list == null || _deleting || list.TickedCount == 0) return;
        int count = list.TickedCount;
        var answer = MessageBox.Show(this,
            $"Move {count:N0} {(count == 1 ? "file" : "files")} ({SizeParser.Format(list.TickedBytes)}) to the Recycle Bin?\n\nAn identical copy of each one stays where it is.",
            "Delete duplicates", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await DeleteTickedAsync(list);
    }

    /// <summary>
    /// Sends the ticked copies to the Recycle Bin once the user has agreed, then takes what really went out of the list.
    /// </summary>
    private async Task DeleteTickedAsync(DuplicateList list)
    {
        _deleting = true;
        UpdateTicked();
        try
        {
            var ticked = list.TickedPaths();
            var groups = list.Groups.ToList();
            // Checked again now: a file that has changed since it was compared is no longer known to be a duplicate.
            var plan = await Task.Run(() => DuplicateDeletePlan.For(groups, ticked));
            foreach (string reason in plan.Left) Logger.Info($"Duplicates: left alone, {reason}");
            if (!ReferenceEquals(list, _list)) return;

            int gone = 0;
            long freed = 0;
            string? error = null;
            if (plan.Delete.Count > 0)
            {
                var going = plan.Delete.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var done = new TaskCompletionSource<FileOperations.Outcome>();
                FileOperations.Recycle(plan.Delete, done.SetResult);
                error = (await done.Task).Error;

                (Application.Current?.MainWindow as MainWindow)?.ViewModel.RefreshFolderViews();
                if (!ReferenceEquals(list, _list)) return;
                // Trust the disk, not the outcome: a delete that was partly refused leaves those files in the list.
                var stillThere = going.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
                gone = going.Count - stillThere.Count;
                freed = list.RemoveGone(p => !going.Contains(p) || stillThere.Contains(p));
            }
            // A file that has changed is no longer known to be a copy of anything: it comes off the list, ticked or not.
            if (plan.Changed.Count > 0)
            {
                var changed = plan.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                list.RemoveGone(p => !changed.Contains(p));
            }
            List.ItemsSource = list.Rows;
            UpdateSummary();
            Logger.Info($"Duplicates under {_folder}: {gone:N0} files sent to the Recycle Bin ({freed:N0} bytes), {plan.Left.Count:N0} left because of changes since the comparison"
                + (error != null ? $"; {error}" : ""));

            string status = gone > 0 ? $"Sent {gone:N0} {(gone == 1 ? "file" : "files")} to the Recycle Bin: {SizeParser.Format(freed)} will be free once the bin is emptied."
                : error != null ? $"Delete failed: {error}"
                : "Nothing was deleted.";
            if (plan.Left.Count == 1) status += " 1 file was left alone: it, or the copy being kept, has changed since it was compared.";
            else if (plan.Left.Count > 1) status += $" {plan.Left.Count:N0} files were left alone: they, or the copies being kept, have changed since they were compared.";
            StatusText.Text = status;
            if (list.GroupCount == 0)
            {
                TickBar.IsEnabled = false;
                ShowMessage("No duplicates left.", "");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Duplicates: delete failed under {_folder}: {ex}");
            StatusText.Text = $"Delete failed: {ex.Message}";
        }
        finally
        {
            _deleting = false;
            UpdateTicked();
        }
    }
}
