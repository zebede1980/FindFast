using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.App.Treemap;
using FastFind.App.ViewModels;
using FastFind.Core.Browse;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.App.Views;

/// <summary>
/// Where the space went: a folder (or a whole drive) as a tree sorted by size on top and a map underneath, in which
/// every file is a block whose area is its size. Clicking the map finds the file in the tree and the other way
/// round. The numbers come from the index when it covers the folder (instant), otherwise from reading the disk.
/// </summary>
public partial class UsageWindow : Window
{
    private static readonly List<UsageWindow> OpenWindows = new();

    private readonly string _folder;
    private SizeTree? _tree;
    private UsageRowList? _rows;
    private CancellationTokenSource? _loading;
    private bool _syncing;
    private int _selected = -1;
    /// <summary>The block last clicked on the map; clicking it again steps the selection out to the folder around it.</summary>
    private int _lastClicked = -1;
    private List<UsageKindChip> _kinds = new();
    private string _sourceText = "";

    /// <summary>Opens the disk usage window for a folder, or brings forward the one already showing it.</summary>
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
        new UsageWindow(folder).Show();
    }

    private UsageWindow(string folder)
    {
        InitializeComponent();
        _folder = folder;
        Title = $"Disk usage - {folder}";
        TitleText.Text = folder;
        var s = App.Settings;
        Width = Math.Max(MinWidth, s.UsageWindowWidth);
        Height = Math.Max(MinHeight, s.UsageWindowHeight);
        if (s.UsageWindowMaximized) WindowState = WindowState.Maximized;
        LabelsToggle.IsChecked = Map.ShowLabels = s.UsageShowNames;

        Map.HoverChanged += OnMapHover;
        Map.RootChanged += OnMapRootChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        OpenWindows.Add(this);
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            _loading?.Cancel();
            s.UsageWindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { s.UsageWindowWidth = Width; s.UsageWindowHeight = Height; }
            s.Save();
        };
        Loaded += (_, _) => _ = LoadAsync();
    }

    // ---- loading ----

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        var ct = cancel.Token;
        LoadingPanel.Visibility = LoadingBar.Visibility = Visibility.Visible;
        LoadingBar.IsIndeterminate = true;
        LoadingText.Text = "Measuring…";
        SummaryText.Text = "";
        RefreshButton.IsEnabled = false;
        var sw = Stopwatch.StartNew();
        SizeTree? tree = null;
        bool fromIndex = false;
        try
        {
            if (!Directory.Exists(_folder)) throw new DirectoryNotFoundException($"{_folder} cannot be opened.");
            var index = App.Index;
            // Opened in the first moments after start-up, the index may not have been found yet; a short wait beats a slow disk read.
            for (int i = 0; i < 40 && !index.HasSource; i++) await Task.Delay(100, ct);
            if (index.Volumes.Any(v => v.Covers(_folder)))
            {
                var progress = new Progress<SizeTreeProgress>(p =>
                {
                    if (ct.IsCancellationRequested) return;
                    LoadingBar.IsIndeterminate = false;
                    LoadingBar.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    LoadingText.Text = $"Reading {p.Total:N0} items from the index…";
                });
                try { tree = await SizeTreeLoader.FromIndexAsync(index, _folder, progress, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Logger.Warn($"Disk usage from the index failed for {_folder}: {ex.Message}"); }
                fromIndex = tree != null;
            }
            if (tree == null)
            {
                LoadingBar.IsIndeterminate = true;
                LoadingText.Text = "Reading the disk…";
                var progress = new Progress<SizeTreeProgress>(p =>
                {
                    if (!ct.IsCancellationRequested) LoadingText.Text = $"Reading the disk… {p.Done:N0} items so far";
                });
                tree = await Task.Run(() => SizeTreeLoader.FromDisk(_folder, progress, ct), ct);
            }
            if (ct.IsCancellationRequested) return;

            _sourceText = (fromIndex ? "from the index" : "read from disk") + $" at {DateTime.Now:HH:mm} in {sw.Elapsed.TotalSeconds:0.0}s";
            RefreshButton.IsEnabled = true;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ShowTree(tree);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Logger.Warn($"Disk usage failed for {_folder}: {ex}");
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingBar.Visibility = Visibility.Collapsed;
            LoadingText.Text = ex.Message;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowTree(SizeTree tree)
    {
        _tree = tree;
        _selected = _lastClicked = -1;
        _rows = new UsageRowList(tree);
        Tree.ItemsSource = _rows;
        Map.Show(tree);
        UpdateSummary();
        Analyse();
        if (tree.Size(SizeTree.Root) <= 0)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingBar.Visibility = Visibility.Collapsed;
            LoadingText.Text = "Nothing in this folder takes up any space.";
        }
    }

    private void UpdateSummary()
    {
        var tree = _tree;
        if (tree == null) return;
        string text = $"{SizeParser.Format(tree.Size(SizeTree.Root))} in {tree.FileCount(SizeTree.Root):N0} files  ·  {_sourceText}";
        if (tree.CloudFiles > 0)
            text += $"  ·  {SizeParser.Format(tree.CloudBytes)} more is online-only and takes no space here";
        SummaryText.Text = text;
        SummaryText.ToolTip = "Sizes are file sizes. The drive itself reports a little more in use: the file system's own tables, restore points and space lost to rounding files up to whole clusters.";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = LoadAsync();

    // ---- zoom: which folder the map shows ----

    private sealed record Crumb(string Name, int Node, bool HasNext);

    private void OnMapRootChanged()
    {
        var tree = _tree;
        if (tree == null) return;
        int root = Map.Root;
        var crumbs = new List<Crumb>();
        for (int n = root; n >= 0; n = tree.Parent(n))
            crumbs.Insert(0, new Crumb(n == SizeTree.Root ? PathUtil.LeafName(tree.RootPath) : tree.Name(n), n, n != root));
        Crumbs.ItemsSource = crumbs;
        ZoomOutButton.IsEnabled = root != SizeTree.Root;
        ZoomSizeText.Text = root == SizeTree.Root ? "" : SizeParser.Format(tree.Size(root));
        UpdateKinds();
    }

    /// <summary>The file type list, the legend and the bar of kinds describe the folder the map is showing.</summary>
    private void UpdateKinds()
    {
        var tree = _tree;
        if (tree == null) return;
        int root = Map.Root;
        long total = tree.Size(root);
        var stats = tree.ExtensionStats(root);
        _syncing = true;
        try
        {
            Types.ItemsSource = stats.Where(s => s.Bytes > 0).Select(s => new UsageTypeRow(s, total, UsageBrushes.ForExtension(tree, s.Id))).ToList();
        }
        finally { _syncing = false; }

        var perKind = new long[FileCategories.Count];
        foreach (var s in stats) perKind[(int)s.Category] += s.Bytes;
        // Legend order is fixed (so a colour's position never moves); kinds with nothing in this folder are left out. "Other" goes last.
        _kinds = Enumerable.Range(1, FileCategories.Count - 1).Append(0)
            .Where(i => perKind[i] > 0)
            .Select(i => new UsageKindChip((FileCategory)i, perKind[i], total)).ToList();
        Legend.ItemsSource = _kinds;

        // The same split as one bar, a segment per kind, with a sliver of space between segments.
        KindBar.Children.Clear();
        KindBar.ColumnDefinitions.Clear();
        foreach (var chip in _kinds)
        {
            // A kind too small to see still gets a few pixels, so it can be found and clicked.
            KindBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(chip.Bytes, total * 0.004), GridUnitType.Star) });
            var segment = new Border { Background = chip.Swatch, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 2, 0), ToolTip = chip.ToolTip, Cursor = Cursors.Hand };
            segment.MouseLeftButtonUp += (_, _) => ToggleKind(chip);
            Grid.SetColumn(segment, KindBar.Children.Count);
            KindBar.Children.Add(segment);
        }

        // An application or clean-up picked out by its folders stays picked out when the map zooms; a kind or type does not carry over.
        if (Map.Highlight?.Folders == null) Map.Highlight = null;
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void ZoomOut()
    {
        if (_tree != null && Map.Root != SizeTree.Root) Map.ZoomTo(_tree.Parent(Map.Root));
    }

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int node }) Map.ZoomTo(node);
    }

    private void OnMapMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            int target = Map.TopLevelOf(Map.NodeAt(e.GetPosition(Map)));
            if (target >= 0) Map.ZoomTo(target);
        }
        else ZoomOut();
        e.Handled = true;
    }

    // ---- picking out a kind of file ----

    private void OnKindClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UsageKindChip chip) ToggleKind(chip);
    }

    /// <summary>Picks a kind of file out on the map, or puts the map back to normal if it was already picked.</summary>
    private void ToggleKind(UsageKindChip chip)
    {
        bool pick = !chip.IsPicked;
        foreach (var k in _kinds) k.IsPicked = pick && ReferenceEquals(k, chip);
        _syncing = true;
        try { Types.SelectedItem = null; Apps.SelectedItem = null; } finally { _syncing = false; }
        Map.Highlight = pick ? new TreemapHighlight(-1, chip.Category) : null;
    }

    private void OnTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        foreach (var k in _kinds) k.IsPicked = false;
        _syncing = true;
        try { Apps.SelectedItem = null; } finally { _syncing = false; }
        Map.Highlight = Types.SelectedItem is UsageTypeRow row ? new TreemapHighlight(row.ExtensionId, null) : null;
    }

    /// <summary>Clicking the type that is already picked out puts the map back to normal.</summary>
    private void OnTypesMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && MainWindow.FindVisualParent<DataGridRow>(d) is { IsSelected: true })
        {
            Types.SelectedItem = null;
            e.Handled = true;
        }
    }

    private bool ClearHighlight()
    {
        if (Map.Highlight == null) return false;
        foreach (var k in _kinds) k.IsPicked = false;
        _syncing = true;
        try { Types.SelectedItem = null; Apps.SelectedItem = null; } finally { _syncing = false; }
        Map.Highlight = null;
        return true;
    }

    private void OnLabelsClick(object sender, RoutedEventArgs e)
    {
        Map.ShowLabels = LabelsToggle.IsChecked == true;
        App.Settings.UsageShowNames = Map.ShowLabels;
    }

    // ---- selection: the tree and the map follow each other ----

    private void Select(int node, bool reveal)
    {
        var tree = _tree;
        var rows = _rows;
        if (tree == null || rows == null) return;
        _selected = node;
        Map.Selected = node;
        _syncing = true;
        try
        {
            if (node < 0) Tree.SelectedItem = null;
            else if (reveal)
            {
                int at = rows.Reveal(node);
                if (at >= 0)
                {
                    var row = rows[at];
                    Tree.SelectedItem = row;
                    Tree.ScrollIntoView(row);
                }
            }
        }
        finally { _syncing = false; }
        SelectionText.Text = node < 0 ? "" : $"{tree.GetPath(node)}  ·  {Describe(node)}";
    }

    private string Describe(int node)
    {
        var tree = _tree!;
        string text = SizeParser.Format(tree.Size(node));
        if (tree.IsDirectory(node)) text += $" in {tree.FileCount(node):N0} files";
        long shown = tree.Size(Map.Root);
        if (shown > 0 && tree.IsSameOrInside(node, Map.Root) && node != Map.Root) text += $"  ·  {(double)tree.Size(node) / shown:0.0%} of {(Map.Root == SizeTree.Root ? "the total" : tree.Name(Map.Root))}";
        return text;
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _lastClicked = -1;
        Select(Tree.SelectedItem is UsageRow row ? row.Node : -1, reveal: false);
    }

    private void OnMapMouseDown(object sender, MouseButtonEventArgs e)
    {
        var tree = _tree;
        if (tree == null) return;
        int node = Map.NodeAt(e.GetPosition(Map));
        if (node < 0) return;
        if (e.ClickCount == 2)
        {
            // Double-click: go one level into the map, towards what was clicked.
            int target = Map.TopLevelOf(node);
            if (target >= 0 && tree.IsDirectory(target)) Map.ZoomTo(target);
            _lastClicked = -1;
            e.Handled = true;
            return;
        }
        int pick = node;
        // Clicking the same block again steps out: the file, then its folder, then that folder's folder, as far as the folder on show.
        if (node == _lastClicked && _selected >= 0 && tree.IsSameOrInside(node, _selected) && _selected != Map.Root)
        {
            int parent = tree.Parent(_selected);
            pick = parent >= 0 && parent != Map.Root && tree.IsSameOrInside(parent, Map.Root) ? parent : node;
        }
        _lastClicked = node;
        Select(pick, reveal: true);
        e.Handled = true;
    }

    private void OnMapHover(int node, Point point)
    {
        var tree = _tree;
        if (tree == null || node < 0)
        {
            HoverCard.Visibility = Visibility.Collapsed;
            return;
        }
        // A block can stand for a whole small folder; describe what the block is.
        bool folder = tree.IsDirectory(node);
        HoverName.Text = tree.Name(node);
        HoverPath.Text = tree.GetPath(tree.Parent(node) >= 0 ? tree.Parent(node) : node);
        HoverSwatch.Background = folder ? Brushes.Transparent : UsageBrushes.ForExtension(tree, tree.ExtensionId(node));
        HoverSwatch.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        long shown = tree.Size(Map.Root);
        string detail = SizeParser.Format(tree.Size(node));
        if (shown > 0) detail += $"  ·  {(double)tree.Size(node) / shown:0.00%}";
        detail += folder ? $"  ·  folder, {tree.FileCount(node):N0} files" : $"  ·  {FileCategories.Name(tree.Category(node))}";
        if (!folder && tree.ModifiedFileTime(node) > 0) detail += $"  ·  {DateTime.FromFileTimeUtc(tree.ModifiedFileTime(node)).ToLocalTime():dd/MM/yyyy}";
        HoverDetail.Text = detail;

        HoverCard.Visibility = Visibility.Visible;
        HoverCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HoverCard.DesiredSize;
        // Beside the pointer, flipped to the other side near the right and bottom edges so it never leaves the map.
        double x = point.X + 16, y = point.Y + 18;
        if (x + size.Width > Map.ActualWidth - 4) x = point.X - size.Width - 12;
        if (y + size.Height > Map.ActualHeight - 4) y = point.Y - size.Height - 12;
        Canvas.SetLeft(HoverCard, Math.Max(4, x));
        Canvas.SetTop(HoverCard, Math.Max(4, y));
    }

    private void OnMapHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Round the corners of everything inside the map's surface.
        MapClip.Clip = new RectangleGeometry(new Rect(0, 0, MapHost.ActualWidth, MapHost.ActualHeight), 9, 9);
    }

    // ---- the tree ----

    private void OnExpanderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UsageRow row) Toggle(row.Node);
        e.Handled = true;
    }

    private void Toggle(int node)
    {
        var rows = _rows;
        if (rows == null) return;
        _syncing = true;
        try
        {
            rows.Toggle(node);
            // Closing a folder that held the selection leaves the folder itself selected.
            if (_selected >= 0 && rows.IndexOf(_selected) < 0) _selected = node;
            RestoreTreeSelection();
        }
        finally { _syncing = false; }
        Map.Selected = _selected;
    }

    /// <summary>The list is rebuilt whenever a folder opens or closes; put the highlight back on the selected row.</summary>
    private void RestoreTreeSelection()
    {
        var rows = _rows;
        if (rows == null) return;
        int at = _selected >= 0 ? rows.IndexOf(_selected) : -1;
        Tree.SelectedItem = at >= 0 ? rows[at] : null;
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || MainWindow.FindVisualParent<DataGridRow>(d) is not { DataContext: UsageRow row }) return;
        if (MainWindow.FindVisualParent<Button>(d) != null) return; // the chevron has its own click
        if (row.CanExpand) Toggle(row.Node);
        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        var tree = _tree;
        var rows = _rows;
        if (tree == null || rows == null || Tree.SelectedItem is not UsageRow row || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Right when row.CanExpand && !rows.IsExpanded(row.Node):
            case Key.Left when rows.IsExpanded(row.Node):
            case Key.Enter when row.CanExpand:
                Toggle(row.Node);
                FocusSelectedRow();
                e.Handled = true;
                break;
            case Key.Left when row.Node != SizeTree.Root:
                Select(tree.Parent(row.Node), reveal: true);
                FocusSelectedRow();
                e.Handled = true;
                break;
        }
    }

    /// <summary>After the list is rebuilt the keyboard focus is on a row that no longer exists; move it to the selected one so the arrow keys carry on.</summary>
    private void FocusSelectedRow()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Tree.SelectedItem == null) return;
            Tree.ScrollIntoView(Tree.SelectedItem);
            if (Tree.ItemContainerGenerator.ContainerFromItem(Tree.SelectedItem) is DataGridRow container)
                (MainWindow.FindVisualChild<DataGridCell>(container) ?? (UIElement)container).Focus();
        });
    }

    // ---- keyboard ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool handled = true;
        if (key == Key.F5 && mods == ModifierKeys.None) _ = LoadAsync();
        else if (key == Key.Back && mods == ModifierKeys.None || key == Key.Up && mods == ModifierKeys.Alt) ZoomOut();
        else if (key == Key.Escape && mods == ModifierKeys.None) { if (!ClearHighlight()) Select(-1, reveal: false); }
        else if (key == Key.C && mods == ModifierKeys.Control && _selected >= 0 && _tree != null) ShellOps.CopyText(_tree.GetPath(_selected));
        else if (key == Key.Delete && (mods == ModifierKeys.None || mods == ModifierKeys.Shift) && _selected > SizeTree.Root) Delete(_selected, permanent: mods == ModifierKeys.Shift);
        else if (key == Key.Enter && mods == ModifierKeys.Alt && _selected >= 0 && _tree != null) ShellOps.ShowProperties(_tree.GetPath(_selected));
        else if (key == Key.W && mods == ModifierKeys.Control) Close();
        else handled = false;
        if (handled) e.Handled = true;
    }

    // ---- right-click menus ----

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? MainWindow.FindVisualParent<DataGridRow>(d)?.DataContext as UsageRow : null;
        row ??= Tree.SelectedItem as UsageRow;
        if (row == null || _tree == null) { e.Handled = true; return; }
        if (!ReferenceEquals(Tree.SelectedItem, row)) Tree.SelectedItem = row;
        BuildMenu(TreeMenu, row.Node);
    }

    private void OnMapContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_tree == null) { e.Handled = true; return; }
        // Opened from the keyboard there is no pointer position: act on the selection.
        int node = e.CursorLeft < 0 && e.CursorTop < 0 ? _selected : Map.NodeAt(Mouse.GetPosition(Map));
        if (node < 0) { e.Handled = true; return; }
        // Right-clicking inside the selected folder acts on that folder; anywhere else, on the file under the pointer.
        if (_selected >= 0 && _tree.IsSameOrInside(node, _selected)) node = _selected;
        else Select(node, reveal: true);
        BuildMenu(MapMenu, node);
    }

    private void BuildMenu(ContextMenu menu, int node)
    {
        var tree = _tree!;
        menu.Items.Clear();
        void Add(string header, Action action, string? gesture = null, bool enabled = true, bool bold = false)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) item.InputGestureText = gesture;
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        bool folder = tree.IsDirectory(node);
        string path = tree.GetPath(node);
        int container = folder ? node : tree.Parent(node);

        Add("Show in _FastFind", () => ShowInFastFind(node), bold: true);
        if (!folder) Add("_Open", () => ShellOps.Open(path));
        Add("Show in Windows _Explorer", () => { if (node == SizeTree.Root) ShellOps.OpenFolder(path); else ShellOps.ShowInExplorer(path); });
        menu.Items.Add(new Separator());
        Add(folder ? "_Zoom the map to this folder" : "_Zoom the map to its folder", () => Map.ZoomTo(container),
            enabled: container >= 0 && container != Map.Root && tree.Size(container) > 0);
        Add("Zoom _out", ZoomOut, "Backspace", enabled: Map.Root != SizeTree.Root);
        menu.Items.Add(new Separator());
        Add("Copy p_ath", () => ShellOps.CopyText(path), "Ctrl+C");
        if (node != SizeTree.Root)
        {
            Add("_Delete", () => Delete(node, permanent: false), "Del");
            Add("Delete _permanently…", () => Delete(node, permanent: true), "Shift+Del");
        }
        menu.Items.Add(new Separator());
        Add("P_roperties", () => ShellOps.ShowProperties(path), "Alt+Enter");
    }

    /// <summary>Shows the item in the main window: a folder is opened in a new tab, a file is shown selected in its folder.</summary>
    private void ShowInFastFind(int node)
    {
        var tree = _tree;
        if (tree == null || Application.Current?.MainWindow is not MainWindow main) return;
        main.ShowFromTray(search: false);
        if (tree.IsDirectory(node)) main.ViewModel.Open(tree.GetPath(node), newTab: true);
        else main.ViewModel.Open(tree.GetPath(tree.Parent(node)), newTab: true, select: tree.Name(node));
    }

    // ---- deleting ----

    private void Delete(int node, bool permanent)
    {
        var tree = _tree;
        if (tree == null || node <= SizeTree.Root || tree.IsRemoved(node)) return;
        string path = tree.GetPath(node);
        string what = $"'{tree.Name(node)}' ({SizeParser.Format(tree.Size(node))}{(tree.IsDirectory(node) ? $", {tree.FileCount(node):N0} files" : "")})";
        if (permanent)
        {
            var answer = MessageBox.Show(this, $"Permanently delete {what}?\n\n{path}\n\nThis cannot be undone: it does not go to the Recycle Bin.",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        else if (App.Settings.ConfirmRecycle)
        {
            var answer = MessageBox.Show(this, $"Send {what} to the Recycle Bin?\n\n{path}", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
        }
        DeleteConfirmed(node, permanent);
    }

    /// <summary>Deletes an item the user has agreed to delete, then takes it off the map once it is really gone.</summary>
    private void DeleteConfirmed(int node, bool permanent)
    {
        var tree = _tree;
        if (tree == null) return;
        string path = tree.GetPath(node);
        Action<FileOperations.Outcome> done = outcome =>
        {
            (Application.Current?.MainWindow as MainWindow)?.ViewModel.RefreshFolderViews();
            if (outcome.Error != null) StatusText.Text = $"Delete failed: {outcome.Error}";
            // Trust the disk, not the outcome: a delete that was partly refused leaves the item where it was.
            if (!ReferenceEquals(tree, _tree) || File.Exists(path) || Directory.Exists(path)) return;
            long freed = tree.Size(node);
            Forget(node);
            StatusText.Text = permanent ? $"Deleted {path}: {SizeParser.Format(freed)} freed." : $"Sent {path} to the Recycle Bin: {SizeParser.Format(freed)} will be free once the bin is emptied.";
        };
        if (permanent) FileOperations.DeletePermanently(new[] { path }, done);
        else FileOperations.Recycle(new[] { path }, done);
    }

    /// <summary>Takes a deleted item out of the tree, the list and the map.</summary>
    private void Forget(int node)
    {
        var tree = _tree;
        var rows = _rows;
        if (tree == null || rows == null) return;
        int parent = tree.Parent(node);
        // If the map was showing the deleted folder (or something inside it), step out to what is left.
        int root = Map.Root;
        bool rootGone = tree.IsSameOrInside(root, node);
        tree.Remove(node);
        _lastClicked = -1;
        _syncing = true;
        try { rows.Rebuild(); } finally { _syncing = false; }
        if (rootGone) Map.Show(tree);
        else Map.Refresh();
        OnMapRootChanged();
        UpdateSummary();
        Analyse();
        Select(parent, reveal: true);
    }
}
