using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Indexing;

namespace FastFind.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private SearchTabViewModel? _selectedTab;
    private string _indexStatus = "Starting…";
    private string _indexDetail = "";
    private bool _isIndexing;
    private double _indexProgress;

    public IndexManager Manager { get; }
    public ObservableCollection<SearchTabViewModel> Tabs { get; } = new();
    public ObservableCollection<VolumeState> Volumes { get; } = new();

    public RelayCommand NewTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand RebuildIndexCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowHelpCommand { get; }

    /// <summary>Set by the window; false while hidden in the tray so live-update refreshes are deferred.</summary>
    public Func<bool>? IsWindowVisible { get; set; }
    /// <summary>Set by the window; opens the settings dialog.</summary>
    public Action? ShowSettings { get; set; }
    /// <summary>Set by the window; opens the syntax help window.</summary>
    public Action? ShowHelp { get; set; }

    public MainViewModel(IndexManager manager, Dispatcher dispatcher)
    {
        Manager = manager;
        _dispatcher = dispatcher;
        NewTabCommand = new RelayCommand(() => AddTab());
        CloseTabCommand = new RelayCommand(p => CloseTab(p as SearchTabViewModel ?? SelectedTab));
        RebuildIndexCommand = new RelayCommand(() => _ = Manager.RebuildAllAsync());
        ShowSettingsCommand = new RelayCommand(() => ShowSettings?.Invoke());
        ShowHelpCommand = new RelayCommand(() => ShowHelp?.Invoke());

        manager.VolumesChanged += () => _dispatcher.BeginInvoke(UpdateVolumeStatus);
        // Live updates: refresh the status immediately, but re-run searches at most every 1.5 s and only while visible,
        // so a burst of file changes (a build, an update) does not keep resetting the results grid.
        var refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(1500) };
        bool refreshPending = false;
        refreshTimer.Tick += (_, _) =>
        {
            if (!refreshPending) { refreshTimer.Stop(); return; }
            if (IsWindowVisible?.Invoke() == false) return; // keep waiting; refresh when shown again
            refreshPending = false;
            refreshTimer.Stop();
            foreach (var t in Tabs) t.Refresh();
        };
        manager.IndexChanged += () => _dispatcher.BeginInvoke(() =>
        {
            UpdateVolumeStatus();
            refreshPending = true;
            if (!refreshTimer.IsEnabled) refreshTimer.Start();
        });
        AddTab();
    }

    public SearchTabViewModel? SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public string IndexStatus { get => _indexStatus; private set => Set(ref _indexStatus, value); }
    public string IndexDetail { get => _indexDetail; private set => Set(ref _indexDetail, value); }
    public bool IsIndexing { get => _isIndexing; private set => Set(ref _isIndexing, value); }
    public double IndexProgress { get => _indexProgress; private set => Set(ref _indexProgress, value); }
    public bool IsElevated => Manager.IsElevated;
    public Visibility ElevationHintVisibility => Manager.IsElevated ? Visibility.Collapsed : Visibility.Visible;

    public SearchTabViewModel AddTab(string? query = null, string? scope = null)
    {
        var tab = new SearchTabViewModel(Manager);
        if (scope != null) { tab.ScopeFolder = scope; tab.FiltersVisible = true; }
        if (query != null) tab.QueryText = query;
        Tabs.Add(tab);
        SelectedTab = tab;
        tab.Requery(immediate: true);
        return tab;
    }

    public void CloseTab(SearchTabViewModel? tab)
    {
        if (tab == null) return;
        int i = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count == 0) AddTab();
        else if (ReferenceEquals(SelectedTab, tab) || SelectedTab == null) SelectedTab = Tabs[Math.Clamp(i, 0, Tabs.Count - 1)];
    }

    private void UpdateVolumeStatus()
    {
        var vols = Manager.Volumes;
        Volumes.Clear();
        foreach (var v in vols) Volumes.Add(v);

        long total = vols.Where(v => v.Status == VolumeStatus.Ready).Sum(v => v.Entries);
        var building = vols.Where(v => v.Status == VolumeStatus.Indexing).ToList();
        var failed = vols.Where(v => v.Status == VolumeStatus.Failed).ToList();
        IsIndexing = building.Count > 0;

        if (building.Count > 0)
        {
            IndexStatus = $"Indexing {string.Join(", ", building.Select(b => b.Root.TrimEnd('\\')))}";
            IndexDetail = string.Join("  ", building.Select(b => b.ProgressText));
        }
        else if (vols.Count == 0)
        {
            IndexStatus = "No volumes found";
            IndexDetail = "";
        }
        else
        {
            bool allMft = vols.Where(v => v.Status == VolumeStatus.Ready).All(v => v.Mode == IndexMode.Mft);
            IndexStatus = $"{total:N0} items indexed";
            IndexDetail = $"{vols.Count(v => v.Status == VolumeStatus.Ready)} volume{(vols.Count == 1 ? "" : "s")} · {(allMft ? "NTFS journal" : "folder watcher")} live updates"
                          + (failed.Count > 0 ? $" · {failed.Count} failed" : "");
        }
    }
}
