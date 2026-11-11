using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Browse;
using FastFind.Core.Search;

namespace FastFind.App.Views;

/// <summary>The contents of one tab: address bar, filter box, file list and status line, plus everything you can do to the files.</summary>
public partial class FilePaneView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;
    private DataGridRow? _deferredSelectRow;
    private bool _focusGridOnResults;
    private ScrollViewer? _gridScroller;

    /// <summary>Scroll position and selection of tabs that are not on screen, so switching tabs does not lose your place.</summary>
    private sealed class TabViewState
    {
        public double Offset;
        public List<string> Selected = new();
    }

    private readonly ConditionalWeakTable<FileTabViewModel, TabViewState> _tabStates = new();

    private FileTabViewModel? Vm => DataContext as FileTabViewModel;
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;
    private MainViewModel? MainVm => Main?.ViewModel;

    private PaneViewModel? Pane
    {
        get
        {
            var main = MainVm;
            var vm = Vm;
            if (main == null || vm == null) return null;
            return main.Right.Tabs.Contains(vm) ? main.Right : main.Left;
        }
    }

    public FilePaneView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyColumnLayout();
        Unloaded += (_, _) => SaveColumnLayout();
        Grid.ColumnReordered += (_, _) => SaveColumnLayout();
        foreach (var column in Grid.Columns)
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn)).AddValueChanged(column, (_, _) => ScheduleLayoutSave());
        DataContextChanged += OnDataContextChanged;
        // Pane shortcuts work wherever the focus is inside the pane (address bar, filter box or file list).
        PreviewKeyDown += (_, e) => { if (HandleKey(e)) e.Handled = true; };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FileTabViewModel old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            var state = _tabStates.GetOrCreateValue(old);
            state.Offset = Scroller?.VerticalOffset ?? 0;
            state.Selected = Grid.SelectedItems.OfType<FileRow>().Take(500).Select(r => r.FullPath).ToList();
        }
        EndAddressEdit(commit: false);
        if (e.NewValue is FileTabViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            ApplyModeColumns();
            UpdateSortGlyphs();
            UpdateEmptyState();
            ScrollCrumbsToEnd();
            if (_tabStates.TryGetValue(vm, out var saved))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (!ReferenceEquals(Vm, vm)) return;
                    if (saved.Offset > 0) Scroller?.ScrollToVerticalOffset(saved.Offset);
                    Reselect(vm, saved.Selected, (int)saved.Offset);
                });
            }
        }
    }

    private ScrollViewer? Scroller => _gridScroller ??= MainWindow.FindVisualChild<ScrollViewer>(Grid);

    // ---- focus ----

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void FocusGrid()
    {
        if (Grid.SelectedItem is FileRow row) FocusRow(row.Position, select: false);
        else Grid.Focus();
    }

    public bool IsTextEntryFocused => Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    /// <summary>Puts the keyboard cursor on a row (scrolling it into view) and optionally makes it the only selected row.</summary>
    private void FocusRow(int index, bool select)
    {
        if (index < 0 || index >= Grid.Items.Count) { Grid.Focus(); return; }
        var item = Grid.Items[index];
        if (select)
        {
            Grid.SelectedItems.Clear();
            Grid.SelectedItem = item;
        }
        Grid.ScrollIntoView(item);
        Grid.UpdateLayout();
        var column = Grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (column != null) Grid.CurrentCell = new DataGridCellInfo(item, column);
        if (Grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row && MainWindow.FindVisualChild<DataGridCell>(row) is { } cell) cell.Focus();
        else Grid.Focus();
    }

    // ---- address bar ----

    public void BeginAddressEdit()
    {
        var vm = Vm;
        if (vm == null) return;
        AddressBox.Text = vm.Location;
        CrumbScroller.Visibility = Visibility.Hidden;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit(bool commit)
    {
        if (AddressBox.Visibility != Visibility.Visible) return;
        string text = AddressBox.Text.Trim();
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbScroller.Visibility = Visibility.Visible;
        if (!commit || Vm == null) return;
        GoToTypedPath(text);
    }

    /// <summary>Handles what was typed in the address bar: a folder, a file (shown selected), or something for Explorer.</summary>
    private void GoToTypedPath(string text)
    {
        var vm = Vm;
        if (vm == null) return;
        if (text.Length == 0) { vm.Navigate(""); return; }
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("::")) { ShellOps.OpenInExplorer(text); return; }
        string path = PathUtil.Normalize(text);
        if (path.Length == 0)
        {
            // Not a path: treat it as a search across everything, which is what people expect from typing into a bar.
            vm.Navigate("");
            vm.QueryText = text;
            return;
        }
        _focusGridOnResults = true;
        if (!Directory.Exists(path) && File.Exists(path)) vm.Navigate(Path.GetDirectoryName(path), Path.GetFileName(path));
        else vm.Navigate(path);
    }

    private void OnAddressBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && MainWindow.FindVisualParent<ButtonBase>(d) != null) return;
        BeginAddressEdit();
        e.Handled = true;
    }

    private void OnAddressBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndAddressEdit(commit: true); FocusGrid(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndAddressEdit(commit: false); FocusGrid(); e.Handled = true; }
    }

    private void OnAddressBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit(commit: false);

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            _focusGridOnResults = true;
            Vm?.Navigate(path);
        }
    }

    private void ScrollCrumbsToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CrumbScroller.ScrollToRightEnd());

    private void OnBackClick(object sender, RoutedEventArgs e) { _focusGridOnResults = true; Vm?.GoBack(); }
    private void OnForwardClick(object sender, RoutedEventArgs e) { _focusGridOnResults = true; Vm?.GoForward(); }
    private void OnUpClick(object sender, RoutedEventArgs e) { _focusGridOnResults = true; Vm?.GoUp(); }
    private void OnRefreshClick(object sender, RoutedEventArgs e) => Vm?.Reload();

    // ---- column chooser and layout persistence ----

    private bool _applyingLayout;
    private DispatcherTimer? _layoutSaveTimer;

    private static string ColumnKey(DataGridColumn c) => c.Header?.ToString() ?? "";

    private static bool IsStarColumn(DataGridColumn c) => ColumnKey(c) is "Name" or "Path" or "Full path";

    /// <summary>Columns that only make sense when rows come from several folders; hidden in a plain folder view whatever the saved layout says.</summary>
    private bool IsModeHidden(DataGridColumn c) => Vm is { ShowPathColumn: false } && (ReferenceEquals(c, PathColumn) || ReferenceEquals(c, FullPathColumn));

    private bool SavedVisible(DataGridColumn c)
    {
        var s = App.Settings.Columns.FirstOrDefault(x => x.Key == ColumnKey(c));
        if (s != null) return s.Visible;
        if (ReferenceEquals(c, CreatedColumn)) return App.Settings.ShowCreatedColumn;
        return ColumnKey(c) is not ("Attributes" or "Full path");
    }

    private void ApplyModeColumns()
    {
        _applyingLayout = true;
        try
        {
            foreach (var c in Grid.Columns)
                c.Visibility = SavedVisible(c) && !IsModeHidden(c) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _applyingLayout = false; }
    }

    public void ApplyColumnLayout()
    {
        var saved = App.Settings.Columns;
        _applyingLayout = true;
        try
        {
            foreach (var column in Grid.Columns)
            {
                var s = saved.FirstOrDefault(x => x.Key == ColumnKey(column));
                // Name and Path share whatever width is left (star-sized) so the list always fits its pane; only fixed columns keep a saved width.
                if (s != null && s.Width > 20 && !IsStarColumn(column)) column.Width = new DataGridLength(s.Width);
            }
            // Apply display order from lowest to highest so indexes stay valid.
            foreach (var s in saved.Where(x => x.DisplayIndex >= 0).OrderBy(x => x.DisplayIndex))
            {
                var column = Grid.Columns.FirstOrDefault(c => ColumnKey(c) == s.Key);
                if (column != null && s.DisplayIndex < Grid.Columns.Count) column.DisplayIndex = s.DisplayIndex;
            }
        }
        finally { _applyingLayout = false; }
        ApplyModeColumns();
        UpdateSortGlyphs();
    }

    private void UpdateSortGlyphs()
    {
        var vm = Vm;
        if (vm == null) return;
        var sortPath = vm.SortField switch
        {
            SortField.Path => "Path", SortField.Size => "Size", SortField.Modified => "Modified",
            SortField.Created => "Created", SortField.Extension => "Extension", _ => "Name",
        };
        foreach (var c in Grid.Columns)
            c.SortDirection = c.SortMemberPath == sortPath && (sortPath != "Path" || ReferenceEquals(c, PathColumn))
                ? (vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }

    private void ScheduleLayoutSave()
    {
        if (_applyingLayout) return;
        _layoutSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Tick -= OnLayoutSaveTick;
        _layoutSaveTimer.Tick += OnLayoutSaveTick;
        _layoutSaveTimer.Start();
    }

    private void OnLayoutSaveTick(object? sender, EventArgs e)
    {
        _layoutSaveTimer?.Stop();
        SaveColumnLayout();
    }

    private void SaveColumnLayout()
    {
        if (_applyingLayout || !IsLoaded) return;
        App.Settings.Columns = Grid.Columns.Select(c => new ColumnSetting
        {
            Key = ColumnKey(c),
            // A column hidden only because of the current view keeps whatever the user last chose for it.
            Visible = IsModeHidden(c) ? SavedVisible(c) : c.Visibility == Visibility.Visible,
            Width = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth,
            DisplayIndex = c.DisplayIndex,
        }).ToList();
        App.Settings.ShowCreatedColumn = SavedVisible(CreatedColumn);
        App.Settings.Save();
    }

    /// <summary>Right-click on a column header: tick the columns to show.</summary>
    private void ShowColumnChooser(DataGridColumnHeader header)
    {
        var menu = new ContextMenu();
        foreach (var column in Grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            var col = column;
            var item = new MenuItem { Header = ColumnKey(col), IsCheckable = true, IsChecked = SavedVisible(col), StaysOpenOnClick = true };
            if (ColumnKey(col) == "Name") item.IsEnabled = false;
            if (IsModeHidden(col)) item.ToolTip = "Shown when searching or including subfolders";
            item.Click += (_, _) =>
            {
                var saved = App.Settings.Columns.FirstOrDefault(x => x.Key == ColumnKey(col));
                if (saved == null)
                {
                    SaveColumnLayout();
                    saved = App.Settings.Columns.FirstOrDefault(x => x.Key == ColumnKey(col));
                }
                if (saved != null) saved.Visible = item.IsChecked;
                if (ReferenceEquals(col, CreatedColumn)) App.Settings.ShowCreatedColumn = item.IsChecked;
                ApplyModeColumns();
                App.Settings.Save();
                Main?.ApplyColumnLayoutToAllPanes(this);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset layout" };
        reset.Click += (_, _) =>
        {
            App.Settings.Columns = new List<ColumnSetting>();
            App.Settings.ShowCreatedColumn = false;
            _applyingLayout = true;
            int i = 0;
            foreach (var c in Grid.Columns) { c.DisplayIndex = i++; }
            Grid.Columns[0].Width = new DataGridLength(2.2, DataGridLengthUnitType.Star);
            Grid.Columns[1].Width = new DataGridLength(3, DataGridLengthUnitType.Star);
            Grid.Columns[2].Width = new DataGridLength(90);
            Grid.Columns[3].Width = new DataGridLength(130);
            Grid.Columns[4].Width = new DataGridLength(130);
            Grid.Columns[5].Width = new DataGridLength(70);
            Grid.Columns[6].Width = new DataGridLength(80);
            Grid.Columns[7].Width = new DataGridLength(3, DataGridLengthUnitType.Star);
            _applyingLayout = false;
            ApplyModeColumns();
            App.Settings.Save();
            Main?.ApplyColumnLayoutToAllPanes(this);
        };
        menu.Items.Add(reset);
        menu.PlacementTarget = header;
        menu.IsOpen = true;
    }

    // ---- reacting to the view model ----

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = Vm;
        if (vm == null || !ReferenceEquals(sender, vm)) return;
        switch (e.PropertyName)
        {
            case nameof(FileTabViewModel.Results):
                OnResultsChanged(vm);
                break;
            case nameof(FileTabViewModel.ShowPathColumn):
                ApplyModeColumns();
                break;
            case nameof(FileTabViewModel.Breadcrumbs):
                EndAddressEdit(commit: false);
                ScrollCrumbsToEnd();
                break;
            case nameof(FileTabViewModel.EmptyText):
                UpdateEmptyState();
                break;
        }
    }

    private void OnResultsChanged(FileTabViewModel vm)
    {
        UpdateEmptyState();
        UpdateSelectionText();
        string? pending = vm.PendingSelectName;
        bool focusGrid = _focusGridOnResults;
        _focusGridOnResults = false;

        if (vm.IsRefreshing)
        {
            // A refresh should not yank the list back to the top or drop the selection.
            double offset = Scroller?.VerticalOffset ?? 0;
            var selectedPaths = Grid.SelectedItems.OfType<FileRow>().Take(500).Select(r => r.FullPath).ToList();
            bool hadFocus = Grid.IsKeyboardFocusWithin;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!ReferenceEquals(Vm, vm)) return;
                UpdateSelectionText(); // the grid has swapped its rows by now, so the count reflects the new list
                if (offset > 0) Scroller?.ScrollToVerticalOffset(offset);
                if (pending != null && SelectByName(vm, pending)) return;
                Reselect(vm, selectedPaths, (int)offset);
                if (hadFocus && !Grid.IsKeyboardFocusWithin) FocusGrid();
            });
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(Vm, vm)) return;
            UpdateSelectionText();
            if (pending != null && SelectByName(vm, pending)) return;
            Scroller?.ScrollToVerticalOffset(0);
            if (focusGrid) Grid.Focus();
        });
    }

    private bool SelectByName(FileTabViewModel vm, string name)
    {
        vm.PendingSelectName = null;
        int i = vm.Results.IndexOfName(name);
        if (i < 0) return false;
        FocusRow(i, select: true);
        return true;
    }

    private void Reselect(FileTabViewModel vm, List<string> paths, int around)
    {
        if (paths.Count == 0) return;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        // A folder listing is small enough to search whole; a huge result is searched around where the user was.
        int first = vm.Results.Count <= 5000 ? 0 : Math.Max(0, around - 100);
        int last = vm.Results.Count <= 5000 ? vm.Results.Count : Math.Min(vm.Results.Count, around + 400);
        for (int i = first; i < last && wanted.Count > 0; i++)
        {
            var row = vm.Results[i];
            if (wanted.Remove(row.FullPath)) Grid.SelectedItems.Add(row);
        }
    }

    private void UpdateEmptyState()
    {
        var vm = Vm;
        bool show = vm != null && vm.Results.Count == 0 && vm.EmptyText.Length > 0;
        EmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (vm != null) EmptyGlyph.Text = vm.HasError ? "\uE7BA" : vm.Mode == ViewMode.Folder && vm.QueryText.Length == 0 && !vm.HasFilters ? "\uE8B7" : "\uE721";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionText();
        Main?.OnPaneSelectionChanged(this);
    }

    private void UpdateSelectionText()
    {
        var vm = Vm;
        if (vm == null) return;
        int count = Grid.SelectedItems.Count;
        vm.HasSelection = count > 0;
        if (count == 0) { vm.SelectionText = ""; return; }
        if (count > 20_000) { vm.SelectionText = $"{count:N0} selected"; return; }
        long size = 0;
        bool anyFolderUnknown = false;
        foreach (var item in Grid.SelectedItems)
        {
            if (item is not FileRow r) continue;
            size += r.Size;
            if (r.IsDirectory && r.Size == 0) anyFolderUnknown = true;
        }
        vm.SelectionText = size > 0 ? $"{count:N0} selected  ·  {SizeParser.Format(size)}{(anyFolderUnknown ? "+" : "")}" : $"{count:N0} selected";
    }

    public IReadOnlyList<FileRow> SelectedRows()
    {
        var rows = Grid.SelectedItems.OfType<FileRow>().Where(r => !r.IsDeleted).ToList();
        if (rows.Count == 0 && Grid.CurrentItem is FileRow current && !current.IsDeleted && Grid.IsKeyboardFocusWithin) rows.Add(current);
        return rows;
    }

    private FileRow? FirstSelected() => SelectedRows().FirstOrDefault();
    private List<string> SelectedPaths() => SelectedRows().Select(r => r.FullPath).ToList();

    /// <summary>The single selected row, for the preview pane.</summary>
    public FileRow? PreviewRow => Grid.SelectedItems.Count == 1 ? Grid.SelectedItem as FileRow : null;

    // ---- search box ----

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else FocusGrid();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter when Grid.Items.Count > 0 && Grid.SelectedItem == null:
                if (Grid.Items.Count > 0) FocusRow(0, select: true);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected(newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        FocusSearch();
    }

    // ---- grid: sorting ----

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var vm = Vm;
        if (vm == null) return;
        var field = e.Column.SortMemberPath switch
        {
            "Path" => SortField.Path,
            "Size" => SortField.Size,
            "Modified" => SortField.Modified,
            "Created" => SortField.Created,
            "Extension" => SortField.Extension,
            _ => SortField.Name,
        };
        bool descending = field == vm.SortField && !vm.SortDescending;
        if (field != vm.SortField && field is SortField.Size or SortField.Modified or SortField.Created) descending = true; // biggest / newest first
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        vm.SetSort(field, descending);
    }

    // ---- keyboard ----

    /// <summary>
    /// Shortcuts that act on this pane wherever the focus is inside it. Returns true when the key was used.
    /// Keys that would fight with typing (Delete, Ctrl+C, letters) are only taken when the file list has the focus.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        bool typing = IsTextEntryFocused;
        bool inList = Grid.IsKeyboardFocusWithin;

        // Navigation: valid anywhere in the pane.
        if (alt && !ctrl)
        {
            switch (key)
            {
                case Key.Left: _focusGridOnResults = !typing; vm.GoBack(); return true;
                case Key.Right: _focusGridOnResults = !typing; vm.GoForward(); return true;
                case Key.Up: _focusGridOnResults = !typing; vm.GoUp(); return true;
                case Key.D: BeginAddressEdit(); return true;
                case Key.Enter when inList: if (FirstSelected() is { } p) ShellOps.ShowProperties(p.FullPath); else if (!vm.IsEverywhere) ShellOps.ShowProperties(vm.Location); return true;
            }
            return false;
        }
        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.L: BeginAddressEdit(); return true;
                case Key.F when !shift: case Key.E when !shift: FocusSearch(); return true;
                case Key.R when shift: vm.Recursive = !vm.Recursive; return true;
                case Key.R: vm.Reload(); return true;
                case Key.H: vm.HideHiddenSystem = !vm.HideHiddenSystem; return true;
                case Key.N when shift: NewFolder(); return true;
                case Key.N: NewFile(); return true;
                case Key.S: ExportResults(selectedOnly: shift); return true;
            }
        }
        if (key == Key.F5 && mods == ModifierKeys.None && (!inList || MainVm?.IsDualPane != true || SelectedRows().Count == 0)) { vm.Reload(); return true; }
        // Everything below acts on files, so it only applies while the file list itself has the keyboard.
        if (!inList) return false;

        // File list shortcuts.
        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.C when shift: CopyPaths(); return true;
                case Key.C: CopyToClipboard(cut: false); return true;
                case Key.X: CopyToClipboard(cut: true); return true;
                case Key.V: Paste(); return true;
                case Key.E when shift: if (FirstSelected() is { } r) ShellOps.ShowInExplorer(r.FullPath); return true;
                case Key.F2: BulkRenameSelected(); return true;
                case Key.I: InvertSelection(); return true;
                case Key.Enter: OpenSelected(newTab: true); return true;
            }
            return false;
        }
        switch (key)
        {
            case Key.Enter: OpenSelected(newTab: false); return true;
            case Key.Back: _focusGridOnResults = true; vm.GoUp(); return true;
            case Key.F2: RenameSelected(); return true;
            case Key.F5: TransferToOtherPane(move: false); return true;
            case Key.F6: TransferToOtherPane(move: true); return true;
            case Key.F7: NewFolder(); return true;
            case Key.Delete: case Key.F8: DeleteSelected(permanent: shift); return true;
            case Key.Escape: if (Grid.SelectedItems.Count > 0) Grid.UnselectAll(); else FocusSearch(); return true;
        }
        return false;
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Tab && mods is ModifierKeys.None or ModifierKeys.Shift)
        {
            // Tab switches panes, the way two-pane file managers have always worked.
            if (Main?.FocusOtherPane(this) == true) e.Handled = true;
            return;
        }
        if ((key == Key.Down || key == Key.Up) && Grid.SelectedItem == null && Grid.Items.Count > 0)
        {
            FocusRow(key == Key.Down ? 0 : Grid.Items.Count - 1, select: true);
            e.Handled = true;
            return;
        }
        if (mods is ModifierKeys.None or ModifierKeys.Shift && (key >= Key.A && key <= Key.Z || key >= Key.D0 && key <= Key.D9 || key >= Key.NumPad0 && key <= Key.NumPad9 || key == Key.OemPeriod || key == Key.OemMinus))
        {
            // Typing while the list has focus filters it: the keystroke lands in the filter box.
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.SelectionLength = 0;
        }
    }

    // ---- mouse ----

    private void OnGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The extra mouse buttons go back and forward, as in Explorer and browsers.
        if (e.ChangedButton == MouseButton.XButton1) { Vm?.GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Vm?.GoForward(); e.Handled = true; }
    }

    /// <summary>Double-click opens the item; double-click on the path column goes to that folder.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject d) return;
        if (MainWindow.FindVisualParent<DataGridColumnHeader>(d) != null) return;
        if (MainWindow.FindVisualParent<DataGridRow>(d) is not { DataContext: FileRow row } || row.IsDeleted) return;
        var cell = MainWindow.FindVisualParent<DataGridCell>(d);
        if (ReferenceEquals(cell?.Column, PathColumn) || ReferenceEquals(cell?.Column, FullPathColumn))
        {
            if (row.Directory.Length > 0) { _focusGridOnResults = true; Vm?.Navigate(row.Directory, row.Name); }
        }
        else
        {
            Open(row, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        e.Handled = true;
    }

    private void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _deferredSelectRow = null;
        if (e.OriginalSource is not DependencyObject d) return;
        if (MainWindow.FindVisualParent<DataGridColumnHeader>(d) != null || MainWindow.FindVisualParent<ScrollBar>(d) != null) return;
        var row = MainWindow.FindVisualParent<DataGridRow>(d);
        if (row == null)
        {
            // Clicking the empty space below the files clears the selection, as in Explorer.
            if (Keyboard.Modifiers == ModifierKeys.None) Grid.UnselectAll();
            Grid.Focus();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
        // Clicking an already-selected row inside a multi-selection must not collapse the selection before a drag starts.
        if (row.IsSelected && Grid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
        {
            _deferredSelectRow = row;
            e.Handled = true;
        }
    }

    private void OnGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelectRow != null)
        {
            Grid.SelectedItems.Clear();
            _deferredSelectRow.IsSelected = true;
            Grid.CurrentItem = _deferredSelectRow.DataContext;
            _deferredSelectRow = null;
        }
        _dragArmed = false;
    }

    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || MainWindow.FindVisualParent<DataGridColumnHeader>(d) != null) return;
        var row = MainWindow.FindVisualParent<DataGridRow>(d);
        if (row == null) Grid.UnselectAll();
        else if (!row.IsSelected)
        {
            Grid.SelectedItems.Clear();
            row.IsSelected = true;
            Grid.CurrentItem = row.DataContext;
        }
        Grid.Focus();
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        _deferredSelectRow = null;
        var paths = SelectedPaths().ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetText(string.Join(Environment.NewLine, paths));
        try { DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception ex) { Logger.Warn($"Drag failed: {ex.Message}"); }
    }

    // ---- drop target ----

    private sealed record DropPlan(string? Folder, bool Move, DragDropEffects Effect, string Hint, string[] Paths);

    /// <summary>Works out what dropping here would do: which folder receives the files and whether they are copied or moved.</summary>
    private DropPlan PlanDrop(DragEventArgs e, string? forcedFolder = null)
    {
        var none = new DropPlan(null, false, DragDropEffects.None, "", Array.Empty<string>());
        var vm = Vm;
        if (vm == null || !e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return none;

        string? folder = forcedFolder;
        if (folder == null && e.OriginalSource is DependencyObject d && MainWindow.FindVisualParent<DataGridRow>(d) is { DataContext: FileRow { IsDirectory: true, IsDeleted: false } row })
            folder = row.FullPath;
        folder ??= vm.Mode == ViewMode.Folder ? vm.Location : null;
        if (folder == null)
        {
            // A search view has no folder to receive files; dropping a folder there opens it instead.
            return e.AllowedEffects.HasFlag(DragDropEffects.Link)
                ? new DropPlan(null, false, DragDropEffects.Link, "Open this folder here", paths)
                : none;
        }

        // A folder cannot go inside itself, and moving something to where it already is does nothing.
        if (paths.Any(p => PathUtil.IsSameOrInside(folder, p))) return none;
        bool ctrl = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey), shift = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        bool move = shift || !ctrl && PathUtil.SameVolume(paths[0], folder);
        if (move && !e.AllowedEffects.HasFlag(DragDropEffects.Move)) move = false;
        if (!move && !e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return none;
        bool alreadyThere = paths.All(p => string.Equals(PathUtil.Parent(p) ?? "", PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));
        if (move && alreadyThere) return none;
        string name = PathUtil.LeafName(folder);
        return new DropPlan(folder, move, move ? DragDropEffects.Move : DragDropEffects.Copy, $"{(move ? "Move" : "Copy")} to {name}", paths);
    }

    private void OnGridDragOver(object sender, DragEventArgs e)
    {
        var plan = PlanDrop(e);
        e.Effects = plan.Effect;
        e.Handled = true;
        DropHint.Visibility = plan.Effect == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible;
        DropHintText.Text = plan.Hint;
    }

    private void OnGridDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void OnGridDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        var plan = PlanDrop(e);
        e.Effects = plan.Effect;
        e.Handled = true;
        ExecuteDrop(plan);
    }

    private void ExecuteDrop(DropPlan plan)
    {
        if (plan.Effect == DragDropEffects.None) return;
        MainVm?.ActivatePaneOf(Vm);
        if (plan.Folder == null)
        {
            string target = Directory.Exists(plan.Paths[0]) ? plan.Paths[0] : Path.GetDirectoryName(plan.Paths[0]) ?? plan.Paths[0];
            Vm?.Navigate(target, Directory.Exists(plan.Paths[0]) ? null : Path.GetFileName(plan.Paths[0]));
            return;
        }
        Transfer(plan.Paths, plan.Folder, plan.Move);
    }

    private void OnCrumbDragOver(object sender, DragEventArgs e)
    {
        var plan = sender is FrameworkElement { Tag: string path } ? PlanDrop(e, path) : PlanDrop(e);
        e.Effects = plan.Effect;
        e.Handled = true;
    }

    private void OnCrumbDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        var plan = PlanDrop(e, path);
        e.Effects = plan.Effect;
        e.Handled = true;
        ExecuteDrop(plan);
    }

    /// <summary>What dropping the dragged files on a folder outside the list (a sidebar place) would do.</summary>
    public DragDropEffects PlanDropOnFolder(DragEventArgs e, string folder) => PlanDrop(e, folder).Effect;

    /// <summary>Carries out a drop on a folder outside the list.</summary>
    public void DropOnFolder(DragEventArgs e, string folder) => ExecuteDrop(PlanDrop(e, folder));

    // ---- opening ----

    private void OpenSelected(bool newTab)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        if (rows[0].IsDirectory || rows.Count == 1) { Open(rows[0], newTab); return; }
        foreach (var r in rows.Where(r => !r.IsDirectory).Take(10)) ShellOps.Open(r.FullPath);
    }

    private void Open(FileRow row, bool newTab)
    {
        string? folder = row.IsDirectory ? row.FullPath : ShellLinks.FolderTarget(row.FullPath);
        if (folder == null) { ShellOps.Open(row.FullPath); return; }
        if (newTab) Pane?.AddTab(folder);
        else { _focusGridOnResults = true; Vm?.Navigate(folder); }
    }

    // ---- command bar ----

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => NewFolder();
    private void OnNewFileClick(object sender, RoutedEventArgs e) => NewFile();
    private void OnCutClick(object sender, RoutedEventArgs e) => CopyToClipboard(cut: true);
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyToClipboard(cut: false);
    private void OnPasteClick(object sender, RoutedEventArgs e) => Paste();
    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameSelected();
    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected(permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    private void OnCopyAcrossClick(object sender, RoutedEventArgs e) => TransferToOtherPane(move: false);
    private void OnMoveAcrossClick(object sender, RoutedEventArgs e) => TransferToOtherPane(move: true);

    // ---- clipboard ----

    private void CopyToClipboard(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        ShellOps.CopyFiles(paths, cut);
        Vm?.SetTransientStatus($"{(cut ? "Cut" : "Copied")} {Count(paths.Count)}. Paste with Ctrl+V.");
    }

    private void CopyPaths()
    {
        var rows = SelectedRows();
        string text = rows.Count > 0 ? string.Join(Environment.NewLine, rows.Select(r => r.FullPath)) : Vm?.Location ?? "";
        if (text.Length > 0) ShellOps.CopyText(text);
    }

    private void Paste()
    {
        var vm = Vm;
        if (vm == null) return;
        // Paste goes into the folder on show; a search view has no single folder to receive files.
        string? folder = vm.Mode == ViewMode.Folder ? vm.Location : null;
        if (folder == null) { vm.SetTransientStatus("Open a folder to paste into."); return; }
        var paths = ShellOps.ClipboardFiles(out bool cut);
        if (paths.Count == 0) { vm.SetTransientStatus("There are no files on the clipboard."); return; }
        if (paths.Any(p => PathUtil.IsSameOrInside(folder, p))) { vm.SetTransientStatus("A folder cannot be pasted into itself."); return; }
        Transfer(paths, folder, cut, afterMove: cut ? ShellOps.ClearClipboard : null);
    }

    // ---- copy / move / delete ----

    private static string Count(int n) => $"{n:N0} {(n == 1 ? "item" : "items")}";

    /// <summary>Copies or moves files into a folder with the Windows progress dialog, then brings both panes up to date.</summary>
    private void Transfer(IReadOnlyList<string> paths, string folder, bool move, Action? afterMove = null)
    {
        var vm = Vm;
        var main = MainVm;
        string firstName = Path.GetFileName(paths[0].TrimEnd('\\'));
        Action<FileOperations.Outcome> done = outcome =>
        {
            if (outcome.Error != null) vm?.SetTransientStatus($"{(move ? "Move" : "Copy")} failed: {outcome.Error}");
            else if (!outcome.Aborted)
            {
                afterMove?.Invoke();
                // Select what arrived if the destination is on show in a pane.
                var target = main?.AllTabs.FirstOrDefault(t => t.IsSelectedTab && t.Mode == ViewMode.Folder && string.Equals(t.Location, PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));
                if (target != null) target.PendingSelectName = firstName;
            }
            main?.RefreshFolderViews();
            if (outcome.Error == null && !outcome.Aborted)
                vm?.SetTransientStatus($"{(move ? "Moved" : "Copied")} {Count(paths.Count)} to {PathUtil.LeafName(folder)}.");
        };
        if (move) FileOperations.Move(paths, folder, done);
        else FileOperations.Copy(paths, folder, done);
    }

    /// <summary>F5 / F6: send the selection to the folder shown in the other pane.</summary>
    private void TransferToOtherPane(bool move)
    {
        var paths = SelectedPaths();
        var pane = Pane;
        var other = pane != null ? MainVm?.OtherPane(pane)?.SelectedTab : null;
        if (paths.Count == 0) return;
        if (other == null || other.Mode != ViewMode.Folder)
        {
            Vm?.SetTransientStatus(other == null ? "Turn on the second pane (Ctrl+\\) to copy or move across." : "Open a folder in the other pane to copy or move into it.");
            return;
        }
        string folder = other.Location;
        if (paths.Any(p => PathUtil.IsSameOrInside(folder, p))) { Vm?.SetTransientStatus("A folder cannot be copied into itself."); return; }
        string verb = move ? "Move" : "Copy";
        string what = paths.Count == 1 ? $"'{Path.GetFileName(paths[0])}'" : $"these {paths.Count:N0} items";
        var answer = MessageBox.Show(Window.GetWindow(this), $"{verb} {what} to\n{folder}?", $"{verb} to other pane", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (answer != MessageBoxResult.OK) return;
        Transfer(paths, folder, move);
    }

    private void TransferToChosenFolder(bool move)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = move ? $"Move {Count(paths.Count)} to" : $"Copy {Count(paths.Count)} to" };
        if (Vm is { IsEverywhere: false } vm) dialog.InitialDirectory = vm.Location;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (paths.Any(p => PathUtil.IsSameOrInside(dialog.FolderName, p))) { Vm?.SetTransientStatus("A folder cannot be copied into itself."); return; }
        Transfer(paths, dialog.FolderName, move);
    }

    private void DeleteSelected(bool permanent)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        var vm = Vm;
        var main = MainVm;
        string what = paths.Count == 1 ? $"'{Path.GetFileName(paths[0].TrimEnd('\\'))}'" : $"these {paths.Count:N0} items";
        if (permanent)
        {
            var answer = MessageBox.Show(Window.GetWindow(this), $"Permanently delete {what}?\n\nThis cannot be undone: the items do not go to the Recycle Bin.",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        else if (App.Settings.ConfirmRecycle)
        {
            var answer = MessageBox.Show(Window.GetWindow(this), $"Send {what} to the Recycle Bin?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
        }
        Action<FileOperations.Outcome> done = outcome =>
        {
            main?.RefreshFolderViews();
            if (outcome.Error != null) vm?.SetTransientStatus($"Delete failed: {outcome.Error}");
            else if (!outcome.Aborted) vm?.SetTransientStatus(permanent ? $"Deleted {Count(paths.Count)}." : $"Sent {Count(paths.Count)} to the Recycle Bin.");
        };
        if (permanent) FileOperations.DeletePermanently(paths, done);
        else FileOperations.Recycle(paths, done);
    }

    // ---- rename and create ----

    private void RenameSelected()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        if (rows.Count > 1) { BulkRenameSelected(); return; }
        var dialog = new RenameWindow(rows[0].FullPath) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (Vm is { } vm)
        {
            vm.PendingSelectName = dialog.NewName;
            vm.Reload();
        }
        MainVm?.RefreshFolderViews(except: Vm);
    }

    private void BulkRenameSelected()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var dialog = new BulkRenameWindow(rows.Select(r => r.FullPath)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        MainVm?.RefreshFolderViews();
        Vm?.SetTransientStatus($"Renamed {Count(dialog.RenamedCount)}.");
    }

    /// <summary>The folder new items are created in: the one on show (a search view has none).</summary>
    private string? CreationFolder()
    {
        var vm = Vm;
        if (vm == null) return null;
        if (!vm.IsEverywhere) return vm.Location;
        vm.SetTransientStatus("Open a folder to create things in it.");
        return null;
    }

    public void NewFolder()
    {
        string? folder = CreationFolder();
        if (folder == null) return;
        Create(folder, "New folder", "Name for the new folder in " + folder, FileOperations.UniqueName(folder, "New folder"), -1,
            name => FileOperations.CreateFolder(folder, name, out string? error) == null ? error : null);
    }

    public void NewFile()
    {
        string? folder = CreationFolder();
        if (folder == null) return;
        string initial = FileOperations.UniqueName(folder, "New file", ".txt");
        Create(folder, "New file", "Name for the new file in " + folder, initial, initial.Length - 4,
            name => FileOperations.CreateFile(folder, name, out string? error) == null ? error : null);
    }

    private void Create(string folder, string title, string label, string initial, int selectLength, Func<string, string?> create)
    {
        var prompt = new PromptWindow(title, label, initial, "Create", create, selectLength) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        if (Vm is { } vm)
        {
            vm.PendingSelectName = prompt.Value;
            // With "include subfolders" on the new item is found through the index a moment later; a folder view reloads now.
            vm.Reload();
        }
        MainVm?.RefreshFolderViews(except: Vm);
    }

    // ---- zip ----

    private void CompressSelected()
    {
        var paths = SelectedPaths();
        var vm = Vm;
        if (paths.Count == 0 || vm == null) return;
        string folder = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? vm.Location;
        var main = MainVm;
        vm.SetTransientStatus($"Compressing {Count(paths.Count)}…");
        Task.Run(() => ZipOps.Compress(paths, folder)).ContinueWith(t =>
        {
            if (t.IsFaulted) { vm.SetTransientStatus($"Compress failed: {t.Exception?.GetBaseException().Message}"); return; }
            if (string.Equals(vm.Location, PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase)) vm.PendingSelectName = Path.GetFileName(t.Result);
            main?.RefreshFolderViews();
            vm.SetTransientStatus($"Created {Path.GetFileName(t.Result)}.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ExtractSelected()
    {
        var zips = SelectedPaths().Where(ZipOps.IsZip).ToList();
        var vm = Vm;
        if (zips.Count == 0 || vm == null) return;
        var main = MainVm;
        vm.SetTransientStatus($"Extracting {Count(zips.Count)}…");
        Task.Run(() => zips.Select(ZipOps.Extract).ToList()).ContinueWith(t =>
        {
            if (t.IsFaulted) { vm.SetTransientStatus($"Extract failed: {t.Exception?.GetBaseException().Message}"); main?.RefreshFolderViews(); return; }
            vm.PendingSelectName = Path.GetFileName(t.Result[0]);
            main?.RefreshFolderViews();
            vm.SetTransientStatus($"Extracted to {Path.GetFileName(t.Result[0])}.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void InvertSelection()
    {
        var vm = Vm;
        if (vm == null || vm.Results.Count > 100_000) return; // inverting a huge search result would materialise every row
        var selected = new HashSet<int>(Grid.SelectedItems.OfType<FileRow>().Select(r => r.Position));
        Grid.SelectedItems.Clear();
        for (int i = 0; i < vm.Results.Count; i++)
            if (!selected.Contains(i)) Grid.SelectedItems.Add(vm.Results[i]);
    }

    // ---- context menus ----

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture != null) item.InputGestureText = gesture;
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && MainWindow.FindVisualParent<DataGridColumnHeader>(d) is { } header)
        {
            e.Handled = true;
            ShowColumnChooser(header);
            return;
        }
        var vm = Vm;
        if (vm == null) { e.Handled = true; return; }
        var rows = SelectedRows();

        // Shift+right-click goes straight to the full Windows menu.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            ShowWindowsMenu(rows, PointToScreen(Mouse.GetPosition(this)), extended: true);
            return;
        }

        RowMenu.Items.Clear();
        if (rows.Count > 0) BuildItemMenu(vm, rows);
        else if (!vm.IsEverywhere) BuildBackgroundMenu(vm);
        else { e.Handled = true; return; }
    }

    private void BuildItemMenu(FileTabViewModel vm, IReadOnlyList<FileRow> rows)
    {
        var first = rows[0];
        var main = MainVm;
        var pane = Pane;
        bool single = rows.Count == 1;
        bool dual = main?.IsDualPane == true;
        var items = RowMenu.Items;

        items.Add(Item("_Open", () => OpenSelected(false), "Enter", bold: true));
        if (single && first.IsDirectory)
        {
            items.Add(Item("Open in new _tab", () => pane?.AddTab(first.FullPath), "Ctrl+Enter"));
            if (dual && pane != null) items.Add(Item("Open in other pa_ne", () => main!.OtherPane(pane)?.SelectedTab?.Navigate(first.FullPath)));
        }
        if (single && !first.IsDirectory) items.Add(Item("Open _with…", () => ShellOps.OpenWith(first.FullPath)));
        if (single && vm.ShowPathColumn) items.Add(Item("Go to containing _folder", () => { _focusGridOnResults = true; vm.Navigate(first.Directory, first.Name); }));
        items.Add(new Separator());

        items.Add(Item("Cu_t", () => CopyToClipboard(true), "Ctrl+X"));
        items.Add(Item("_Copy", () => CopyToClipboard(false), "Ctrl+C"));
        if (single && first.IsDirectory && vm.Mode == ViewMode.Folder)
            items.Add(Item("_Paste into folder", () => PasteInto(first.FullPath), enabled: ShellOps.ClipboardHasFiles()));
        items.Add(Item("Copy full p_ath", CopyPaths, "Ctrl+Shift+C"));
        items.Add(Item("Copy na_me", () => ShellOps.CopyText(string.Join(Environment.NewLine, SelectedRows().Select(r => r.Name)))));
        items.Add(new Separator());

        if (dual)
        {
            items.Add(Item("Copy to other pane", () => TransferToOtherPane(false), "F5"));
            items.Add(Item("Move to other pane", () => TransferToOtherPane(true), "F6"));
        }
        items.Add(Item("Copy to folder…", () => TransferToChosenFolder(false)));
        items.Add(Item("Move to folder…", () => TransferToChosenFolder(true)));
        items.Add(new Separator());

        items.Add(Item("Rena_me", RenameSelected, "F2"));
        items.Add(Item("Rename multiple…", BulkRenameSelected, "Ctrl+F2"));
        items.Add(Item("_Delete", () => DeleteSelected(false), "Del"));
        items.Add(new Separator());

        if (rows.All(r => ZipOps.IsZip(r.FullPath))) items.Add(Item(single ? $"Extract to \"{Path.GetFileNameWithoutExtension(first.Name)}\\\"" : "Extract each to its own folder", ExtractSelected));
        items.Add(Item("Compress to ZIP", CompressSelected));
        if (single && first.IsDirectory)
        {
            bool pinned = main?.IsPinned(first.FullPath) == true;
            items.Add(Item(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => { if (pinned) main?.Unpin(first.FullPath); else main?.Pin(first.FullPath); }));
            items.Add(Item("Open in Terminal", () => ShellOps.OpenTerminal(first.FullPath)));
        }
        if (single) items.Add(Item("Show in Windows Explorer", () => ShellOps.ShowInExplorer(first.FullPath), "Ctrl+Shift+E"));
        items.Add(new Separator());

        items.Add(Item("Export selected…", () => ExportResults(true), "Ctrl+Shift+S"));
        items.Add(new Separator());
        items.Add(Item("More options (Windows menu)…", () => ShowWindowsMenu(SelectedRows(), MenuAnchor(), extended: false), "Shift+Right-click"));
        items.Add(Item("P_roperties", () => ShellOps.ShowProperties(first.FullPath), "Alt+Enter"));
    }

    private void BuildBackgroundMenu(FileTabViewModel vm)
    {
        var main = MainVm;
        var items = RowMenu.Items;
        string folder = vm.Location;
        items.Add(Item("New _folder", NewFolder, "Ctrl+Shift+N"));
        items.Add(Item("New fi_le…", NewFile, "Ctrl+N"));
        items.Add(Item("_Paste", Paste, "Ctrl+V", enabled: vm.Mode == ViewMode.Folder && ShellOps.ClipboardHasFiles()));
        items.Add(new Separator());
        items.Add(Item("_Refresh", vm.Reload, "Ctrl+R"));
        var sub = new MenuItem { Header = "Include _subfolders", IsCheckable = true, IsChecked = vm.Recursive, InputGestureText = "Ctrl+Shift+R" };
        sub.Click += (_, _) => vm.Recursive = !vm.Recursive;
        items.Add(sub);
        items.Add(new Separator());
        items.Add(Item("Copy folder p_ath", () => ShellOps.CopyText(folder)));
        bool pinned = main?.IsPinned(folder) == true;
        items.Add(Item(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => { if (pinned) main?.Unpin(folder); else main?.Pin(folder); }));
        items.Add(Item("Open in Terminal", () => ShellOps.OpenTerminal(folder)));
        items.Add(Item("Open in Windows Explorer", () => ShellOps.OpenFolder(folder)));
        items.Add(new Separator());
        items.Add(Item("Export list…", () => ExportResults(false), "Ctrl+S"));
        items.Add(new Separator());
        items.Add(Item("More options (Windows menu)…", () => ShowWindowsMenu(Array.Empty<FileRow>(), MenuAnchor(), extended: false), "Shift+Right-click"));
        items.Add(Item("P_roperties", () => ShellOps.ShowProperties(folder), "Alt+Enter"));
    }

    private Point _menuAnchor;

    /// <summary>Where our own menu was opened, so the Windows menu that replaces it appears in the same place.</summary>
    private Point MenuAnchor() => _menuAnchor;

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        _menuAnchor = PointToScreen(e.GetPosition(this));
        base.OnPreviewMouseRightButtonUp(e);
    }

    private void PasteInto(string folder)
    {
        var paths = ShellOps.ClipboardFiles(out bool cut);
        if (paths.Count == 0) return;
        if (paths.Any(p => PathUtil.IsSameOrInside(folder, p))) { Vm?.SetTransientStatus("A folder cannot be pasted into itself."); return; }
        Transfer(paths, folder, cut, afterMove: cut ? ShellOps.ClearClipboard : null);
    }

    /// <summary>Shows the genuine Windows context menu (Send to, 7-Zip, Git, New…) for the selection or the folder background.</summary>
    private void ShowWindowsMenu(IReadOnlyList<FileRow> rows, Point screenPoint, bool extended)
    {
        var window = Window.GetWindow(this);
        var vm = Vm;
        if (window == null || vm == null) return;
        if (screenPoint == default) screenPoint = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        // Let our own menu finish closing first; a native menu opened underneath it is dismissed immediately.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (rows.Count > 0)
            {
                string? verb = ShellContextMenu.ShowForItems(window, rows.Select(r => r.FullPath).ToList(), screenPoint, extended);
                if (verb == "rename") RenameSelected();
                else if (verb != null) MainVm?.RefreshFolderViews();
            }
            else if (!vm.IsEverywhere)
            {
                ShellContextMenu.ShowForFolderBackground(window, vm.Location, screenPoint, extended);
                MainVm?.RefreshFolderViews();
            }
        });
    }

    // ---- export ----

    /// <summary>Saves the current rows (or the selection) as CSV, a path list, or an Everything EFU file list.</summary>
    public void ExportResults(bool selectedOnly)
    {
        var vm = Vm;
        if (vm == null) return;
        var result = vm.Results.Set;
        IReadOnlyList<int>? positions = null;
        if (selectedOnly)
        {
            positions = Grid.SelectedItems.OfType<FileRow>().Select(r => r.Position).OrderBy(p => p).ToList();
            if (positions.Count == 0) { selectedOnly = false; positions = null; }
        }
        if (result.Count == 0) return;

        string basis = !string.IsNullOrWhiteSpace(vm.QueryText) ? vm.QueryText : vm.IsEverywhere ? "results" : PathUtil.LeafName(vm.Location).TrimEnd(':');
        string suggested = "FastFind " + string.Concat(basis.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = selectedOnly ? "Export selected items" : "Export list",
            Filter = ResultExporter.DialogFilter,
            FileName = suggested.Length > 60 ? suggested[..60] : suggested,
            DefaultExt = ".csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string path = dialog.FileName;
        Task.Run(() => ResultExporter.Export(result, positions, path)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Logger.Warn($"Export failed: {t.Exception?.GetBaseException().Message}");
                MessageBox.Show(Window.GetWindow(this), t.Exception?.GetBaseException().Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            vm.SetTransientStatus($"Exported {t.Result:N0} items to {Path.GetFileName(path)}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
