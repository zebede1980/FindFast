using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Search;

namespace FastFind.App.Views;

public partial class SearchTabView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;
    private DataGridRow? _deferredSelectRow;

    private SearchTabViewModel? Vm => DataContext as SearchTabViewModel;

    public SearchTabView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CreatedColumn.Visibility = App.Settings.ShowCreatedColumn ? Visibility.Visible : Visibility.Collapsed;
            FocusSearch();
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Dispatcher.BeginInvoke(FocusSearch); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnVmPropertyChanged;
            UpdateEmptyState();
        };
        DragOver += OnDragOverView;
        Drop += OnDropView;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchTabViewModel.Results)) UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var vm = Vm;
        EmptyState.Visibility = vm != null && vm.Results.Count == 0 && (!string.IsNullOrEmpty(vm.QueryText) || vm.HasFilters) ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<ResultRow> SelectedRows()
    {
        var rows = Grid.SelectedItems.OfType<ResultRow>().Where(r => !r.IsDeleted).ToList();
        if (rows.Count == 0 && Grid.CurrentItem is ResultRow current && !current.IsDeleted) rows.Add(current);
        return rows;
    }

    private ResultRow? FirstSelected() => SelectedRows().FirstOrDefault();

    // ---- search box ----

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Clear(); else Grid.UnselectAll();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter when Grid.Items.Count > 0 && Grid.SelectedItem == null:
                if (Grid.Items.Count > 0)
                {
                    Grid.SelectedIndex = 0;
                    Grid.CurrentItem = Grid.Items[0];
                    Grid.ScrollIntoView(Grid.Items[0]);
                    (Grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow)?.Focus();
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (FirstSelected() is { } row) ShellOps.Open(row.FullPath);
                e.Handled = true;
                break;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        FocusSearch();
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Limit search to folder" };
        if (dialog.ShowDialog() == true && Vm != null) Vm.ScopeFolder = dialog.FolderName;
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

    // ---- grid: keyboard & mouse ----

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (e.Key == Key.Enter && alt || e.SystemKey == Key.Enter && alt) { if (FirstSelected() is { } r) ShellOps.ShowProperties(r.FullPath); e.Handled = true; }
        else if (e.Key == Key.Enter) { if (FirstSelected() is { } r) ShellOps.Open(r.FullPath); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.C) { CopyPaths(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopyFiles(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.E) { if (FirstSelected() is { } r) ShellOps.ShowInExplorer(r.FullPath); e.Handled = true; }
        else if (e.Key == Key.Escape) { FocusSearch(); e.Handled = true; }
        else if (e.Key == Key.Back) { FocusSearch(); }
        else if (!ctrl && !alt && e.Key >= Key.A && e.Key <= Key.Z || e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key == Key.Space)
        {
            // Typing while the list has focus goes to the search box, like Explorer's address bar.
            FocusSearch();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.SelectionLength = 0;
        }
    }

    /// <summary>Double-click on the name opens the item; double-click on the path opens its folder in Explorer.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        if (MainWindow.FindVisualParent<DataGridRow>(d) is not { DataContext: ResultRow row } || row.IsDeleted) return;
        var cell = MainWindow.FindVisualParent<DataGridCell>(d);
        if (cell?.Column?.SortMemberPath == "Path")
        {
            if (row.Directory.Length > 0) ShellOps.OpenFolder(row.Directory);
        }
        else
        {
            ShellOps.Open(row.FullPath);
        }
        e.Handled = true;
    }

    private void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _deferredSelectRow = null;
        if (e.OriginalSource is not DependencyObject d) return;
        if (MainWindow.FindVisualParent<DataGridColumnHeader>(d) != null) return;
        var row = MainWindow.FindVisualParent<DataGridRow>(d);
        if (row == null) return;
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

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        _deferredSelectRow = null;
        var paths = SelectedRows().Select(r => r.FullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetText(string.Join(Environment.NewLine, paths));
        DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedRows().Count == 0) e.Handled = true;
    }

    // ---- context menu actions ----

    private void OnOpenClick(object sender, RoutedEventArgs e) { foreach (var r in SelectedRows().Take(10)) ShellOps.Open(r.FullPath); }
    private void OnOpenWithClick(object sender, RoutedEventArgs e) { if (FirstSelected() is { } r) ShellOps.OpenWith(r.FullPath); }
    private void OnShowInExplorerClick(object sender, RoutedEventArgs e) { if (FirstSelected() is { } r) ShellOps.ShowInExplorer(r.FullPath); }
    private void OnCopyFilesClick(object sender, RoutedEventArgs e) => CopyFiles();
    private void OnCopyPathClick(object sender, RoutedEventArgs e) => CopyPaths();
    private void OnCopyNameClick(object sender, RoutedEventArgs e) => ShellOps.CopyText(string.Join(Environment.NewLine, SelectedRows().Select(r => r.Name)));
    private void OnPropertiesClick(object sender, RoutedEventArgs e) { if (FirstSelected() is { } r) ShellOps.ShowProperties(r.FullPath); }

    private void OnSearchInFolderClick(object sender, RoutedEventArgs e)
    {
        if (FirstSelected() is not { } r) return;
        string folder = r.IsDirectory ? r.FullPath : r.Directory;
        if (Window.GetWindow(this) is MainWindow main) main.ViewModel.AddTab(scope: folder);
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => ExportResults(selectedOnly: false);
    private void OnExportSelectedClick(object sender, RoutedEventArgs e) => ExportResults(selectedOnly: true);

    /// <summary>Saves the current results (or the selection) as CSV, a path list, or an Everything EFU file list.</summary>
    public void ExportResults(bool selectedOnly)
    {
        var vm = Vm;
        if (vm == null) return;
        var result = vm.Results.Result;
        IReadOnlyList<int>? positions = null;
        if (selectedOnly)
        {
            positions = Grid.SelectedItems.OfType<ResultRow>().Select(r => r.Position).OrderBy(p => p).ToList();
            if (positions.Count == 0) { selectedOnly = false; positions = null; }
        }
        if (result.Count == 0) return;

        string suggested = string.IsNullOrWhiteSpace(vm.QueryText) ? "FastFind results" : "FastFind " + string.Concat(vm.QueryText.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = selectedOnly ? "Export selected items" : "Export all results",
            Filter = ResultExporter.DialogFilter,
            FileName = suggested.Length > 60 ? suggested[..60] : suggested,
            DefaultExt = ".csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string path = dialog.FileName;
        var status = vm.StatusText;
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

    private void CopyFiles() => ShellOps.CopyFiles(SelectedRows().Select(r => r.FullPath).ToList());
    private void CopyPaths() => ShellOps.CopyText(string.Join(Environment.NewLine, SelectedRows().Select(r => r.FullPath)));

    // ---- drop a folder onto the tab to scope the search ----

    private void OnDragOverView(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Source != Grid)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void OnDropView(object sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        string folder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]) ?? paths[0];
        Vm.FiltersVisible = true;
        Vm.ScopeFolder = folder;
        e.Handled = true;
    }
}
