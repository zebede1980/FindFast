using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

public enum SidebarKind { Search, Folder, Favourite, PinAction, Drive, Network, Shell }

/// <summary>One entry in the sidebar: a place to go.</summary>
public sealed class SidebarItem
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    /// <summary>Folder to navigate to; "" for the search-everywhere view; a shell: target for <see cref="SidebarKind.Shell"/>.</summary>
    public required string Path { get; init; }
    public string Detail { get; init; } = "";
    public SidebarKind Kind { get; init; }
    /// <summary>0..1 fill of the drive's capacity bar; negative hides the bar.</summary>
    public double UsedFraction { get; init; } = -1;
    public bool HasBar => UsedFraction >= 0;
    public override string ToString() => Title;
    public bool IsNearlyFull => UsedFraction >= 0.9;
    public string ToolTip => Kind switch
    {
        SidebarKind.Search => "Search every indexed file and folder (Ctrl+Shift+F)",
        SidebarKind.Shell => "Opens in Windows Explorer",
        SidebarKind.PinAction => "Pins the folder on show in the active pane. You can also drag a folder here, or right-click any folder and choose Pin to sidebar.",
        _ => Detail.Length > 0 ? $"{Path}\n{Detail}" : Path,
    };
}

public sealed class SidebarGroup
{
    public required string Title { get; init; }
    public ObservableCollection<SidebarItem> Items { get; } = new();
    public override string ToString() => Title;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private PaneViewModel _activePane;
    private bool _isDualPane;
    private bool _sidebarVisible;
    private bool _previewVisible;
    private string _indexStatus = "Starting…";
    private string _indexDetail = "";
    private bool _isIndexing;
    private bool _serviceHintVisible;

    public IndexHost Index { get; }
    public PaneViewModel Left { get; }
    public PaneViewModel Right { get; }
    public ObservableCollection<VolumeInfo> Volumes { get; } = new();
    public ObservableCollection<SidebarGroup> Sidebar { get; } = new();

    public RelayCommand ToggleDualPaneCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand TogglePreviewCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand JumpCommand { get; }

    /// <summary>Set by the window; false while hidden in the tray so live-update refreshes are deferred.</summary>
    public Func<bool>? IsWindowVisible { get; set; }
    public Action? ShowSettings { get; set; }
    public Action? ShowHelp { get; set; }
    public Action? ShowJump { get; set; }

    public MainViewModel(IndexHost index, Dispatcher dispatcher)
    {
        Index = index;
        _dispatcher = dispatcher;
        Left = new PaneViewModel(this, index, "Left");
        Right = new PaneViewModel(this, index, "Right");
        _activePane = Left;

        ToggleDualPaneCommand = new RelayCommand(() => IsDualPane = !IsDualPane);
        ToggleSidebarCommand = new RelayCommand(() => SidebarVisible = !SidebarVisible);
        TogglePreviewCommand = new RelayCommand(() => PreviewVisible = !PreviewVisible);
        ShowSettingsCommand = new RelayCommand(() => ShowSettings?.Invoke());
        ShowHelpCommand = new RelayCommand(() => ShowHelp?.Invoke());
        JumpCommand = new RelayCommand(() => ShowJump?.Invoke());

        var s = App.Settings;
        _sidebarVisible = s.SidebarVisible;
        _previewVisible = s.PreviewVisible;
        _isDualPane = s.DualPane;
        RestoreSession(s.Session);
        UpdatePaneFlags();

        index.VolumesChanged += () => _dispatcher.BeginInvoke(UpdateVolumeStatus);
        index.SourceChanged += () => _dispatcher.BeginInvoke(UpdateVolumeStatus);
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
            foreach (var t in AllTabs) t.OnIndexChanged();
        };
        index.IndexChanged += () => _dispatcher.BeginInvoke(() =>
        {
            UpdateVolumeStatus();
            refreshPending = true;
            if (!refreshTimer.IsEnabled) refreshTimer.Start();
        });

        var sidebarTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        sidebarTimer.Tick += (_, _) => { if (IsWindowVisible?.Invoke() != false && SidebarVisible) RefreshSidebar(); };
        sidebarTimer.Start();
        RefreshSidebar();
        UpdateVolumeStatus();
    }

    // ---- panes ----

    public IEnumerable<FileTabViewModel> AllTabs => Left.Tabs.Concat(Right.Tabs);

    public PaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (!IsDualPane) value = Left;
            if (!Set(ref _activePane, value)) return;
            UpdatePaneFlags();
        }
    }

    /// <summary>The pane a copy or move "to the other side" lands in, or null when only one pane is showing.</summary>
    public PaneViewModel? OtherPane(PaneViewModel pane) => !IsDualPane ? null : ReferenceEquals(pane, Left) ? Right : Left;

    public FileTabViewModel? ActiveTab => ActivePane.SelectedTab;

    public bool IsDualPane
    {
        get => _isDualPane;
        set
        {
            if (!Set(ref _isDualPane, value)) return;
            App.Settings.DualPane = value;
            if (!value) ActivePane = Left;
            UpdatePaneFlags();
            // A pane that was hidden may have missed changes; bring its visible tab up to date.
            if (value) Right.SelectedTab?.Reload();
        }
    }

    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set
        {
            if (!Set(ref _sidebarVisible, value)) return;
            App.Settings.SidebarVisible = value;
            if (value) RefreshSidebar();
        }
    }

    public bool PreviewVisible
    {
        get => _previewVisible;
        set { if (Set(ref _previewVisible, value)) App.Settings.PreviewVisible = value; }
    }

    private void UpdatePaneFlags()
    {
        Left.IsActive = ReferenceEquals(_activePane, Left);
        Right.IsActive = ReferenceEquals(_activePane, Right) && IsDualPane;
        Left.ShowWindowCommands = !IsDualPane;
        Right.ShowWindowCommands = IsDualPane;
        OnPropertyChanged(nameof(ActiveTab));
    }

    public PaneViewModel? PaneOf(FileTabViewModel? tab)
        => tab == null ? null : Right.Tabs.Contains(tab) ? Right : Left.Tabs.Contains(tab) ? Left : null;

    public void ActivatePaneOf(FileTabViewModel? tab)
    {
        if (PaneOf(tab) is { } pane) ActivePane = pane;
    }

    /// <summary>
    /// Re-reads the folder views on screen after files were copied, moved, renamed or deleted. The folder watchers
    /// usually get there first, but some network shares never raise change events.
    /// </summary>
    public void RefreshFolderViews(FileTabViewModel? except = null)
    {
        foreach (var tab in AllTabs)
            if (tab.IsSelectedTab && !ReferenceEquals(tab, except) && tab.Mode == ViewMode.Folder) tab.Reload();
        RefreshSidebar();
    }

    /// <summary>Opens a folder: in the active tab, or in a new tab of the active pane.</summary>
    public void Open(string location, bool newTab = false, string? select = null)
    {
        var pane = ActivePane;
        if (newTab || pane.SelectedTab == null) pane.AddTab(location, selectName: select);
        else pane.SelectedTab.Navigate(location, select);
    }

    /// <summary>Opens whatever a path points at: a folder is browsed, a file is shown selected in its folder.</summary>
    public void OpenPath(string path, bool newTab)
    {
        string normal = PathUtil.Normalize(path);
        if (normal.Length == 0) return;
        if (Directory.Exists(normal)) Open(normal, newTab);
        else if (File.Exists(normal)) Open(Path.GetDirectoryName(normal) ?? normal, newTab, Path.GetFileName(normal));
        else Open(normal, newTab);
    }

    // ---- session ----

    private void RestoreSession(SessionState? session)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (session == null)
        {
            // First run: the search everyone knows on the left, the home folder on the right.
            Left.Restore(new[] { new TabState { Location = "" }, new TabState { Location = home } }, 1, home);
            Right.Restore(new[] { new TabState { Location = KnownFolders.Downloads ?? home } }, 0, home);
            return;
        }
        Left.Restore(session.Left, session.LeftSelected, home);
        Right.Restore(session.Right, session.RightSelected, home);
        if (session.RightActive && IsDualPane) _activePane = Right;
    }

    public void SaveSession()
    {
        App.Settings.Session = new SessionState
        {
            Left = Left.Tabs.Select(t => t.ToState()).ToList(),
            Right = Right.Tabs.Select(t => t.ToState()).ToList(),
            LeftSelected = Left.SelectedTab != null ? Left.Tabs.IndexOf(Left.SelectedTab) : 0,
            RightSelected = Right.SelectedTab != null ? Right.Tabs.IndexOf(Right.SelectedTab) : 0,
            RightActive = ReferenceEquals(ActivePane, Right),
        };
    }

    // ---- sidebar ----

    private int _sidebarVersion;

    /// <summary>Rebuilds the sidebar. Drive details are read off the UI thread because an unreachable network drive can block.</summary>
    public void RefreshSidebar()
    {
        int version = ++_sidebarVersion;
        var favourites = App.Settings.Favourites.ToList();
        var network = App.Settings.NetworkLocations.ToList();
        Task.Run(() =>
        {
            var places = new List<SidebarItem>
            {
                new() { Glyph = "\uE721", Title = "Search everywhere", Path = "", Kind = SidebarKind.Search },
            };
            void Place(string glyph, string title, string? path)
            {
                if (!string.IsNullOrEmpty(path)) places.Add(new SidebarItem { Glyph = glyph, Title = title, Path = path, Kind = SidebarKind.Folder });
            }
            Place("\uE80F", "Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Place("\uE7F4", "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Place("\uE8A5", "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Place("\uE896", "Downloads", KnownFolders.Downloads);
            Place("\uE8B9", "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

            // Your own locations. Two pins with the same folder name are told apart by their parent folder.
            var pinned = favourites.Select(f =>
            {
                string leaf = PathUtil.LeafName(f);
                bool ambiguous = favourites.Count(o => string.Equals(PathUtil.LeafName(o), leaf, StringComparison.OrdinalIgnoreCase)) > 1;
                string? parent = PathUtil.Parent(f);
                string title = ambiguous && parent != null ? $"{leaf}  ({PathUtil.LeafName(parent)})" : leaf;
                return new SidebarItem { Glyph = "\uE734", Title = title, Path = f, Kind = SidebarKind.Favourite };
            }).ToList();
            // Always present, so the section is visible (and can be dropped onto) even before anything is pinned.
            pinned.Add(new SidebarItem { Glyph = "\uE710", Title = "Pin current folder", Path = "", Kind = SidebarKind.PinAction });

            var drives = new List<SidebarItem>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown && !d.IsReady) continue;
                    string letter = d.Name.TrimEnd('\\');
                    if (!d.IsReady)
                    {
                        drives.Add(new SidebarItem { Glyph = "\uEDA2", Title = letter, Path = d.Name, Kind = SidebarKind.Drive, Detail = "Not ready" });
                        continue;
                    }
                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Network ? "Network drive" : "Local disk") : d.VolumeLabel;
                    double used = d.TotalSize > 0 ? 1.0 - (double)d.AvailableFreeSpace / d.TotalSize : -1;
                    drives.Add(new SidebarItem
                    {
                        Glyph = d.DriveType switch { DriveType.Removable => "\uE88E", DriveType.Network => "\uE8CE", DriveType.CDRom => "\uE958", _ => "\uEDA2" },
                        Title = $"{label} ({letter})",
                        Path = d.Name,
                        Kind = SidebarKind.Drive,
                        Detail = $"{SizeParser.Format(d.AvailableFreeSpace)} free of {SizeParser.Format(d.TotalSize)}",
                        UsedFraction = used,
                    });
                }
                catch (Exception) { /* drive vanished or is locked */ }
            }
            foreach (var n in network)
            {
                string root = n.TrimEnd('\\');
                if (root.StartsWith(@"\\")) drives.Add(new SidebarItem { Glyph = "\uE8CE", Title = root, Path = root, Kind = SidebarKind.Network, Detail = "Indexed network location" });
            }

            var system = new List<SidebarItem>
            {
                new() { Glyph = "\uE74D", Title = "Recycle Bin", Path = "shell:RecycleBinFolder", Kind = SidebarKind.Shell },
                new() { Glyph = "\uE977", Title = "This PC", Path = "shell:MyComputerFolder", Kind = SidebarKind.Shell },
            };
            return (places, pinned, drives, system);
        }).ContinueWith(t =>
        {
            if (version != _sidebarVersion || !t.IsCompletedSuccessfully) return;
            var (places, pinned, drives, system) = t.Result;
            SetGroup("Places", places);
            SetGroup("Pinned", pinned);
            SetGroup("Drives", drives);
            SetGroup("Windows", system);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetGroup(string title, List<SidebarItem> items)
    {
        var group = Sidebar.FirstOrDefault(g => g.Title == title);
        if (items.Count == 0)
        {
            if (group != null) Sidebar.Remove(group);
            return;
        }
        if (group == null)
        {
            group = new SidebarGroup { Title = title };
            // Keep the fixed order regardless of which group appears first.
            string[] order = { "Places", "Pinned", "Drives", "Windows" };
            int at = Sidebar.Count(g => Array.IndexOf(order, g.Title) < Array.IndexOf(order, title));
            Sidebar.Insert(at, group);
        }
        // Leave the list alone when nothing changed so hover and scroll state are not disturbed every refresh.
        if (group.Items.Count == items.Count && group.Items.Zip(items).All(p => p.First.Title == p.Second.Title && p.First.Path == p.Second.Path
                && p.First.Detail == p.Second.Detail && Math.Abs(p.First.UsedFraction - p.Second.UsedFraction) < 0.001)) return;
        group.Items.Clear();
        foreach (var i in items) group.Items.Add(i);
    }

    public bool IsPinned(string path) => App.Settings.Favourites.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    public void Pin(string path)
    {
        if (path.Length == 0 || IsPinned(path)) return;
        App.Settings.Favourites.Add(path);
        App.Settings.Save();
        RefreshSidebar();
    }

    /// <summary>Moves a pinned folder up (-1) or down (+1) in the sidebar.</summary>
    public void MovePin(string path, int delta)
    {
        var pins = App.Settings.Favourites;
        int i = pins.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= pins.Count) return;
        (pins[i], pins[j]) = (pins[j], pins[i]);
        App.Settings.Save();
        RefreshSidebar();
    }

    public void Unpin(string path)
    {
        App.Settings.Favourites.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        App.Settings.Save();
        RefreshSidebar();
    }

    // ---- index status ----

    public string IndexStatus { get => _indexStatus; private set => Set(ref _indexStatus, value); }
    public string IndexDetail { get => _indexDetail; private set => Set(ref _indexDetail, value); }
    public bool IsIndexing { get => _isIndexing; private set => Set(ref _isIndexing, value); }
    /// <summary>Offer the index service when NTFS volumes are being scanned the slow way and the service could do better.</summary>
    public bool ServiceHintVisible { get => _serviceHintVisible; private set => Set(ref _serviceHintVisible, value); }

    private void UpdateVolumeStatus()
    {
        var vols = Index.Volumes;
        Volumes.Clear();
        foreach (var v in vols) Volumes.Add(v);

        long total = vols.Where(v => v.Status == VolumeStatus.Ready).Sum(v => v.Entries);
        var building = vols.Where(v => v.Status == VolumeStatus.Indexing).ToList();
        var failed = vols.Where(v => v.Status == VolumeStatus.Failed).ToList();
        IsIndexing = building.Count > 0;
        ServiceHintVisible = Index.HasSource && !Index.IsRemote && !Index.IsElevated;

        if (!Index.HasSource)
        {
            IndexStatus = "Connecting to the index…";
            IndexDetail = "";
        }
        else if (building.Count > 0)
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
            var ready = vols.Where(v => v.Status == VolumeStatus.Ready).ToList();
            bool allMft = ready.Count > 0 && ready.Where(v => !v.IsNetwork).All(v => v.Mode == IndexMode.Mft);
            IndexStatus = $"{total:N0} items indexed";
            IndexDetail = $"{ready.Count} volume{(ready.Count == 1 ? "" : "s")} · {(allMft ? "NTFS journal" : "folder watcher")} live updates"
                          + (Index.IsRemote ? " · index service" : "")
                          + (failed.Count > 0 ? $" · {failed.Count} failed" : "");
        }
    }
}

/// <summary>Folders Environment.SpecialFolder does not know about.</summary>
public static class KnownFolders
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? Get(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception) { return null; }
    }

    public static string? Downloads { get; } = Get(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
}
