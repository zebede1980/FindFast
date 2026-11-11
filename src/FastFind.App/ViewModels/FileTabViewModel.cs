using System.IO;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

/// <summary>One clickable segment of the address bar.</summary>
public sealed record Crumb(string Name, string Path);

/// <summary>What a tab is currently showing.</summary>
public enum ViewMode
{
    /// <summary>A search across the whole index (the original FastFind view).</summary>
    Everywhere,
    /// <summary>The live contents of one folder, optionally narrowed by the filter box.</summary>
    Folder,
    /// <summary>A folder and everything beneath it as one flat list.</summary>
    Subtree,
}

/// <summary>
/// One tab: a location plus a query. With no location it searches everywhere; with a location and no query it is a
/// plain folder listing read live from disk; typing narrows it; "include subfolders" flattens the whole subtree.
/// </summary>
public sealed class FileTabViewModel : ObservableObject, IDisposable
{
    private readonly IIndexService _index;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _watchTimer;
    private CancellationTokenSource? _cts;
    private int _searchVersion;
    private bool _disposed;

    private string _location = "";
    private bool _recursive;
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    // Live folder state. _listing is only ever replaced on the UI thread.
    private FolderListing? _listing;
    private bool _listingStale;
    private FileSystemWatcher? _watcher;
    private TimeSpan _lastLoadTime;
    private VolumeIndex? _walkIndex;
    private string? _walkRoot;
    private bool _isSelectedTab;
    private bool _indexStale;

    private string _queryText = "";
    private bool _matchCase, _useRegex, _wholeWord, _matchPath, _filtersVisible;
    private string _kindFilter = "Any";
    private string _typeFilter = "Everything";
    private string _extensionsText = "";
    private string _minSizeText = "", _maxSizeText = "";
    private string _dateFilter = "Any time";
    private DateTime? _dateFrom, _dateTo;
    private bool _hideHiddenSystem;
    private ResultCollection _results = ResultCollection.Empty;
    private string _statusText = "";
    private string _selectionText = "";
    private bool _hasSelection;
    private string _emptyText = "";
    private string? _errorText;
    private SortField _sortField = SortField.Name;
    private bool _sortDescending;
    private bool _isSearching;

    public static readonly string[] TypeChoices = { "Everything", "Files", "Folders" };
    public static readonly string[] KindChoices = { "Any", "Documents", "Images", "Audio", "Video", "Archives", "Executables", "Code" };
    public static readonly string[] DateChoices = { "Any time", "Today", "Yesterday", "Last 7 days", "Last 30 days", "This month", "This year", "Custom range" };

    private Controls.Highlighter _nameHighlighter = Controls.Highlighter.None;
    private Controls.Highlighter _pathHighlighter = Controls.Highlighter.None;

    /// <summary>Terms to light up in the Name column (every positive text term).</summary>
    public Controls.Highlighter NameHighlighter { get => _nameHighlighter; private set => Set(ref _nameHighlighter, value); }
    /// <summary>Terms to light up in the Path column (only terms that match against the path).</summary>
    public Controls.Highlighter PathHighlighter { get => _pathHighlighter; private set => Set(ref _pathHighlighter, value); }

    public FileTabViewModel(IIndexService index, string location = "", string query = "", bool recursive = false)
    {
        _index = index;
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (Enum.TryParse<SortField>(App.Settings.SortField, out var savedSort) && savedSort != SortField.Relevance) _sortField = savedSort;
        _sortDescending = App.Settings.SortDescending;
        _hideHiddenSystem = App.Settings.HideHiddenSystemByDefault;
        _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        _watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); if (_listingStale && !_disposed) Refresh(); };
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _location = PathUtil.Normalize(location);
        _recursive = recursive && _location.Length > 0;
        _queryText = query ?? "";
    }

    public RelayCommand ClearFiltersCommand { get; }

    // ---- location and navigation ----

    /// <summary>The folder shown, or "" when searching everywhere.</summary>
    public string Location => _location;
    public bool IsEverywhere => _location.Length == 0;
    public ViewMode Mode => IsEverywhere ? ViewMode.Everywhere : _recursive ? ViewMode.Subtree : ViewMode.Folder;

    /// <summary>Include everything beneath the folder as one flat list.</summary>
    public bool Recursive
    {
        get => _recursive;
        set
        {
            if (IsEverywhere) value = false;
            if (!Set(ref _recursive, value)) return;
            OnModeChanged();
            Requery(immediate: true);
        }
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !IsEverywhere && PathUtil.Parent(_location) != null;
    /// <summary>The Path column only earns its space when rows can come from different folders.</summary>
    public bool ShowPathColumn => Mode != ViewMode.Folder;

    public IReadOnlyList<Crumb> Breadcrumbs
        => IsEverywhere ? Array.Empty<Crumb>() : PathUtil.Segments(_location).Select(s => new Crumb(s.Name, s.Path)).ToList();

    public string Title
    {
        get
        {
            string q = QueryText.Trim();
            if (q.Length > 24) q = q[..24] + "…";
            if (IsEverywhere) return q.Length == 0 ? "Everywhere" : q;
            string leaf = PathUtil.LeafName(_location);
            return q.Length == 0 ? leaf : $"{leaf}: {q}";
        }
    }

    /// <summary>Icon shown on the tab: a magnifier for a search across everything, a folder otherwise.</summary>
    public string TabGlyph => IsEverywhere ? "\uE721" : _recursive ? "\uE8B7" : "\uE838";

    public string SearchPlaceholder => Mode switch
    {
        ViewMode.Everywhere => "Search every file and folder…  (try  ext:pdf dm:thisweek  or  *.log size:>10mb)",
        ViewMode.Subtree => $"Search {PathUtil.LeafName(_location)} and its subfolders…",
        _ => $"Filter {PathUtil.LeafName(_location)}…",
    };

    /// <summary>A name the view should select and scroll to once the next results arrive (the folder just left, a file just created).</summary>
    public string? PendingSelectName { get; set; }

    /// <summary>Set by the pane: true while this is the tab on screen. A hidden tab defers its reloads until it is shown.</summary>
    public bool IsSelectedTab
    {
        get => _isSelectedTab;
        set
        {
            _isSelectedTab = value;
            if (value && (_listingStale || _indexStale) && _started) Refresh();
        }
    }

    private bool _started;

    /// <summary>Runs the first search. Separate from construction so restoring a session does not load every tab at once.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Requery(immediate: true);
    }

    /// <summary>Goes to a folder ("" for everywhere). Returns false if the text is not a usable path.</summary>
    public bool Navigate(string? path, string? select = null)
    {
        string target = "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            target = PathUtil.Normalize(path);
            if (target.Length == 0)
            {
                SetTransientStatus($"'{path.Trim()}' is not a folder path.");
                return false;
            }
        }
        if (string.Equals(target, _location, StringComparison.OrdinalIgnoreCase))
        {
            if (select != null) PendingSelectName = select;
            Reload();
            return true;
        }
        _back.Add(_location);
        if (_back.Count > 100) _back.RemoveAt(0);
        _forward.Clear();
        SetLocation(target, select);
        return true;
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        string target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(_location);
        SetLocation(target, ChildToSelect(target, _location));
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        string target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_location);
        SetLocation(target, null);
    }

    public void GoUp()
    {
        if (IsEverywhere) return;
        string? parent = PathUtil.Parent(_location);
        if (parent != null) Navigate(parent, PathUtil.LeafName(_location));
    }

    /// <summary>When stepping back out of a folder, the name of the child we came from so it can be re-selected.</summary>
    private static string? ChildToSelect(string target, string from)
    {
        if (target.Length == 0 || from.Length <= target.Length || !PathUtil.IsSameOrInside(from, target)) return null;
        string rest = from[target.TrimEnd('\\').Length..].TrimStart('\\');
        int slash = rest.IndexOf('\\');
        return slash < 0 ? rest : rest[..slash];
    }

    private void SetLocation(string target, string? select)
    {
        _location = target;
        _recursive = false;
        _queryText = ""; // a filter belongs to the folder it was typed in
        DropListing();
        PendingSelectName = select;
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(IsEverywhere));
        OnPropertyChanged(nameof(Recursive));
        OnPropertyChanged(nameof(QueryText));
        OnPropertyChanged(nameof(Breadcrumbs));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnModeChanged();
        IsRefreshing = false;
        _started = true;
        Requery(immediate: true);
    }

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ShowPathColumn));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(TabGlyph));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanPaste));
    }

    private void DropListing()
    {
        _listing = null;
        _listingStale = false;
        _walkIndex = null;
        _walkRoot = null;
        _watchTimer.Stop();
        var watcher = _watcher;
        _watcher = null;
        if (watcher != null)
        {
            // Disposing a watcher on a slow share can block, so do it off the UI thread.
            Task.Run(() => { try { watcher.Dispose(); } catch (Exception) { } });
        }
    }

    // ---- query and filters ----

    public string QueryText { get => _queryText; set { if (Set(ref _queryText, value)) { OnPropertyChanged(nameof(Title)); Requery(); } } }
    public bool MatchCase { get => _matchCase; set { if (Set(ref _matchCase, value)) Requery(); } }
    public bool UseRegex { get => _useRegex; set { if (Set(ref _useRegex, value)) Requery(); } }
    public bool WholeWord { get => _wholeWord; set { if (Set(ref _wholeWord, value)) Requery(); } }
    public bool MatchPath { get => _matchPath; set { if (Set(ref _matchPath, value)) Requery(); } }
    public bool FiltersVisible { get => _filtersVisible; set => Set(ref _filtersVisible, value); }

    public string TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) Requery(); } }
    public string KindFilter { get => _kindFilter; set { if (Set(ref _kindFilter, value)) Requery(); } }
    public string ExtensionsText { get => _extensionsText; set { if (Set(ref _extensionsText, value)) Requery(); } }
    public string MinSizeText { get => _minSizeText; set { if (Set(ref _minSizeText, value)) Requery(); } }
    public string MaxSizeText { get => _maxSizeText; set { if (Set(ref _maxSizeText, value)) Requery(); } }
    public string DateFilter { get => _dateFilter; set { if (Set(ref _dateFilter, value)) { OnPropertyChanged(nameof(IsCustomDate)); Requery(); } } }
    public bool IsCustomDate => _dateFilter == "Custom range";
    public DateTime? DateFrom { get => _dateFrom; set { if (Set(ref _dateFrom, value)) Requery(); } }
    public DateTime? DateTo { get => _dateTo; set { if (Set(ref _dateTo, value)) Requery(); } }
    public bool HideHiddenSystem { get => _hideHiddenSystem; set { if (Set(ref _hideHiddenSystem, value)) Requery(); } }

    /// <summary>True when the filter panel (or a match option) is narrowing the results.</summary>
    public bool HasFilters
    {
        get
        {
            var r = BuildRequest();
            return r.Kind != EntryKind.Any || r.Extensions != null || r.MinSize != null || r.MaxSize != null
                   || r.ModifiedFromUtc != null || r.ModifiedToUtcExclusive != null || r.HideHiddenAndSystem
                   || _matchCase || _useRegex || _wholeWord || _matchPath;
        }
    }

    public ResultCollection Results { get => _results; private set => Set(ref _results, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    /// <summary>True while at least one row is selected (maintained by the view); enables the command bar.</summary>
    public bool HasSelection { get => _hasSelection; set => Set(ref _hasSelection, value); }
    /// <summary>New items need a folder to be created in; a search across everything has none.</summary>
    public bool CanCreate => !IsEverywhere;
    /// <summary>Pasting needs a single folder on show.</summary>
    public bool CanPaste => Mode == ViewMode.Folder;

    /// <summary>"3 selected · 4.2 MB", maintained by the view.</summary>
    public string SelectionText { get => _selectionText; set => Set(ref _selectionText, value); }
    /// <summary>Message for an empty list: an empty folder, no matches, or why the folder could not be read.</summary>
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    /// <summary>Shows a message in the status line until the next search replaces it.</summary>
    public void SetTransientStatus(string text) => StatusText = text;
    public string? ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText != null;
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }
    public SortField SortField => _sortField;
    public bool SortDescending => _sortDescending;

    public void SetSort(SortField field, bool descending)
    {
        _sortField = field;
        _sortDescending = descending;
        App.Settings.SortField = field.ToString();
        App.Settings.SortDescending = descending;
        Requery(immediate: true);
    }

    public void ClearFilters()
    {
        _typeFilter = "Everything"; _kindFilter = "Any"; _extensionsText = ""; _minSizeText = ""; _maxSizeText = "";
        _dateFilter = "Any time"; _dateFrom = null; _dateTo = null; _hideHiddenSystem = false;
        _matchCase = _useRegex = _wholeWord = _matchPath = false;
        foreach (var p in new[] { nameof(TypeFilter), nameof(KindFilter), nameof(ExtensionsText), nameof(MinSizeText), nameof(MaxSizeText),
                     nameof(DateFilter), nameof(IsCustomDate), nameof(DateFrom), nameof(DateTo), nameof(HideHiddenSystem), nameof(HasFilters),
                     nameof(MatchCase), nameof(UseRegex), nameof(WholeWord), nameof(MatchPath) })
            OnPropertyChanged(p);
        Requery();
    }

    /// <summary>True while the next result set comes from a refresh rather than user input (the view keeps its scroll position and selection).</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Re-runs the current view quietly: same query, fresh data.</summary>
    public void Refresh()
    {
        if (!_started) return;
        IsRefreshing = true;
        Requery(immediate: true);
    }

    /// <summary>Re-reads the folder from disk (F5 / Ctrl+R), discarding anything cached.</summary>
    public void Reload()
    {
        _listingStale = true;
        _walkIndex = null;
        _walkRoot = null;
        Refresh();
    }

    /// <summary>Called when the index changes. A plain folder view has its own watcher, so only index-backed views re-run.</summary>
    public void OnIndexChanged()
    {
        if (Mode == ViewMode.Folder)
        {
            // The folder itself is watched directly, but its folder sizes come from the index, which may only just have become ready.
            if (_isSelectedTab && _listing != null && !_listingStale) LoadFolderSizes(_listing, _location);
            return;
        }
        if (_isSelectedTab) Refresh();
        else _indexStale = true; // caught up when the tab is next shown
    }

    public void Requery(bool immediate = false)
    {
        if (!_started || _disposed) return;
        OnPropertyChanged(nameof(HasFilters));
        _debounce.Stop();
        if (immediate) RunSearch();
        else _debounce.Start();
    }

    private SearchRequest BuildRequest()
    {
        var r = new SearchRequest
        {
            Text = _queryText,
            MatchCase = _matchCase, UseRegex = _useRegex, WholeWord = _wholeWord, MatchPath = _matchPath,
            Kind = _typeFilter switch { "Files" => EntryKind.File, "Folders" => EntryKind.Folder, _ => EntryKind.Any },
            HideHiddenAndSystem = _hideHiddenSystem,
            Sort = _sortField,
            Descending = _sortDescending,
        };
        var exts = new List<string>();
        if (_kindFilter != "Any" && FileKinds.TryGet(_kindFilter, out var kindExts)) exts.AddRange(kindExts);
        if (!string.IsNullOrWhiteSpace(_extensionsText))
            exts.AddRange(_extensionsText.Split(new[] { ';', ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries));
        if (exts.Count > 0) r.Extensions = exts.ToArray();
        if (SizeParser.TryParseSize(_minSizeText, out long min, out _)) r.MinSize = min;
        if (SizeParser.TryParseSize(_maxSizeText, out long max, out _)) r.MaxSize = max;

        var today = DateTime.Today;
        (DateTime? lo, DateTime? hi) = _dateFilter switch
        {
            "Today" => (today, today.AddDays(1)),
            "Yesterday" => (today.AddDays(-1), today),
            "Last 7 days" => (today.AddDays(-6), today.AddDays(1)),
            "Last 30 days" => (today.AddDays(-29), today.AddDays(1)),
            "This month" => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1)),
            "This year" => (new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1)),
            "Custom range" => (_dateFrom?.Date, _dateTo?.Date.AddDays(1)),
            _ => ((DateTime?)null, (DateTime?)null),
        };
        if (lo != null) r.ModifiedFromUtc = lo.Value.ToUniversalTime();
        if (hi != null) r.ModifiedToUtcExclusive = hi.Value.ToUniversalTime();
        return r;
    }

    private sealed record Outcome(IResultSet Set, SearchQuery Query, ViewMode Mode, string Location, FolderListing? Listing, bool FreshListing,
        VolumeIndex? Walk, string? Error, int Total, string FreeSpace, TimeSpan LoadTime, bool Indexed);

    private void RunSearch()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        int version = ++_searchVersion;
        _indexStale = false;
        var request = BuildRequest();
        string location = _location;
        var mode = Mode;
        // Drop a stale listing here rather than just skipping it, so a search that gets superseded cannot resurrect it.
        if (_listingStale) { _listing = null; _listingStale = false; }
        var listing = _listing;
        var walk = _walkRoot == location ? _walkIndex : null;
        var index = _index;
        IsSearching = true;

        Task.Run(async () =>
        {
            var query = request.ToQuery();
            switch (mode)
            {
                case ViewMode.Everywhere:
                {
                    var set = await index.SearchAsync(request, ct).ConfigureAwait(false);
                    return new Outcome(set, query, mode, location, null, false, null, null, set.Count, "", TimeSpan.Zero, index.Volumes.Any(v => v.Status == VolumeStatus.Ready));
                }
                case ViewMode.Subtree when index.Volumes.Any(v => v.Covers(location)):
                {
                    request.ScopeFolder = location;
                    request.ScopeRecursive = true;
                    var set = await index.SearchAsync(request, ct).ConfigureAwait(false);
                    return new Outcome(set, query, mode, location, null, false, null, null, set.Count, "", TimeSpan.Zero, true);
                }
                case ViewMode.Subtree:
                {
                    // Not covered by the index (a share that is not indexed, a drive still being scanned): walk it now.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    walk ??= DirectoryWalkIndexer.Build(location, null, ct);
                    var result = SearchEngine.Search(new[] { walk }, query, request.Sort, request.Descending, ct);
                    return new Outcome(new LocalResultSet(result), query, mode, location, null, false, walk, null, walk.LiveCount, "", sw.Elapsed, false);
                }
                default:
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool fresh = listing == null;
                    listing ??= FolderListing.Load(location, ct);
                    var loadTime = sw.Elapsed;
                    var result = SearchEngine.Search(new[] { listing.Index }, query, request.Sort, request.Descending, ct, foldersFirst: true);
                    return new Outcome(new LocalResultSet(result), query, mode, location, listing, fresh, null, listing.Error, listing.Count,
                        fresh ? FreeSpace(location) : "", loadTime, true);
                }
            }
        }, ct).ContinueWith(t =>
        {
            if (version != _searchVersion || _disposed)
            {
                if (t.IsCompletedSuccessfully) t.Result.Set.Dispose();
                return;
            }
            IsSearching = false;
            if (t.IsCanceled) return;
            if (t.IsFaulted)
            {
                var ex = t.Exception?.GetBaseException();
                if (ex is OperationCanceledException) return;
                ErrorText = ex?.Message;
                EmptyText = ex?.Message ?? "";
                return;
            }
            Apply(t.Result, request);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string _freeSpace = "";

    private void Apply(Outcome o, SearchRequest request)
    {
        var terms = o.Query.TextTerms().ToList();
        NameHighlighter = new Controls.Highlighter(terms);
        PathHighlighter = new Controls.Highlighter(terms.Where(x => x.MatchPath));

        if (o.Walk != null) { _walkIndex = o.Walk; _walkRoot = o.Location; }
        if (o.Listing != null)
        {
            _listing = o.Listing;
            if (o.FreshListing)
            {
                _lastLoadTime = o.LoadTime;
                _freeSpace = o.FreeSpace;
                Watch(o.Location);
                LoadFolderSizes(o.Listing, o.Location);
            }
        }

        var previous = _results;
        Results = new ResultCollection(o.Set);
        if (!ReferenceEquals(previous.Set, o.Set)) previous.Set.Dispose();
        IsRefreshing = false;
        ErrorText = o.Error ?? o.Set.Error;

        bool narrowed = request.HasCriteria;
        switch (o.Mode)
        {
            case ViewMode.Folder:
                StatusText = (narrowed ? $"{o.Set.Count:N0} of {o.Total:N0} items" : Items(o.Set.Count)) + (_freeSpace.Length > 0 ? $"  ·  {_freeSpace}" : "");
                EmptyText = o.Error ?? (narrowed ? "No matches in this folder" : "This folder is empty");
                break;
            case ViewMode.Subtree:
                StatusText = $"{Items(o.Set.Count)} in this folder and its subfolders" + (o.Indexed ? $"  ·  {o.Set.Elapsed.TotalMilliseconds:0.#} ms" : "  ·  scanned live (not indexed)");
                EmptyText = narrowed ? "No matches" : "Nothing in this folder";
                break;
            default:
                StatusText = !o.Indexed ? "Index is still building…" : $"{Items(o.Set.Count)}  ·  {o.Set.Elapsed.TotalMilliseconds:0.#} ms";
                EmptyText = !o.Indexed ? "The index is still building…" : narrowed ? "No matches" : "";
                break;
        }
    }

    private static string Items(int count) => $"{count:N0} {(count == 1 ? "item" : "items")}";

    private static string FreeSpace(string location)
    {
        try
        {
            string? root = Path.GetPathRoot(location);
            if (root == null || root.Length != 3 || root[1] != ':') return "";
            var drive = new DriveInfo(root);
            return drive.IsReady ? $"{SizeParser.Format(drive.AvailableFreeSpace)} free on {root.TrimEnd('\\')}" : "";
        }
        catch (Exception) { return ""; }
    }

    // ---- keeping a folder view live ----

    private void Watch(string location)
    {
        if (_watcher != null) return;
        // Creating a watcher on an unreachable share can block for seconds, so it is set up off the UI thread.
        Task.Run(() =>
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(location)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                };
                FileSystemEventHandler changed = (_, _) => _dispatcher.BeginInvoke(OnFolderChanged);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Changed += changed;
                watcher.Renamed += (_, _) => _dispatcher.BeginInvoke(OnFolderChanged);
                watcher.Error += (_, _) => _dispatcher.BeginInvoke(OnFolderChanged);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception)
            {
                watcher?.Dispose();
                return; // cannot be watched (some network and virtual drives): F5 still reloads
            }
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _watcher != null || !string.Equals(_location, location, StringComparison.OrdinalIgnoreCase)) watcher.Dispose();
                else _watcher = watcher;
            });
        });
    }

    private void OnFolderChanged()
    {
        if (_disposed) return;
        _listingStale = true;
        if (!_isSelectedTab || _watchTimer.IsEnabled) return;
        // A folder that takes a while to read is reloaded less often, so a busy directory cannot pin a core.
        double ms = Math.Clamp(_lastLoadTime.TotalMilliseconds * 4, 250, 5000);
        _watchTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _watchTimer.Start();
    }

    private void LoadFolderSizes(FolderListing listing, string location)
    {
        if (!App.Settings.ShowFolderSizes) return;
        _index.GetFolderSizesAsync(location).ContinueWith(t =>
        {
            if (_disposed || !t.IsCompletedSuccessfully || !ReferenceEquals(_listing, listing) || _listingStale) return;
            // Sizes arrive after the listing is on screen; re-run so the column fills in and a size sort is right.
            if (listing.ApplyFolderSizes(t.Result)) Refresh();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public TabState ToState() => new() { Location = _location, Query = _queryText, Recursive = _recursive };

    /// <summary>What assistive technology announces for the tab.</summary>
    public override string ToString() => IsEverywhere ? Title : $"{Title} ({_location})";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _debounce.Stop();
        DropListing();
        _results.Set.Dispose();
    }
}
