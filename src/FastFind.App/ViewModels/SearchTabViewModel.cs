using System.Windows.Threading;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

public sealed class SearchTabViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _searchVersion;

    private string _queryText = "";
    private bool _matchCase, _useRegex, _wholeWord, _matchPath, _filtersVisible;
    private string _kindFilter = "Any";
    private string _typeFilter = "Everything";
    private string _extensionsText = "";
    private string _minSizeText = "", _maxSizeText = "";
    private string _dateFilter = "Any time";
    private DateTime? _dateFrom, _dateTo;
    private string _scopeFolder = "";
    private bool _hideHiddenSystem;
    private ResultCollection _results = ResultCollection.Empty;
    private string _statusText = "";
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

    public SearchTabViewModel(IndexManager manager)
    {
        _manager = manager;
        if (Enum.TryParse<SortField>(App.Settings.SortField, out var savedSort)) _sortField = savedSort;
        _sortDescending = App.Settings.SortDescending;
        _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public RelayCommand ClearFiltersCommand { get; }

    public string Title => string.IsNullOrWhiteSpace(QueryText) ? "New search" : QueryText.Length > 24 ? QueryText[..24] + "…" : QueryText;

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
    public string ScopeFolder { get => _scopeFolder; set { if (Set(ref _scopeFolder, value)) { OnPropertyChanged(nameof(HasFilters)); Requery(); } } }
    public bool HideHiddenSystem { get => _hideHiddenSystem; set { if (Set(ref _hideHiddenSystem, value)) Requery(); } }

    public bool HasFilters => !BuildFilters().IsEmpty;

    public ResultCollection Results { get => _results; private set => Set(ref _results, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Shows a message in the status bar until the next search replaces it.</summary>
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
        _dateFilter = "Any time"; _dateFrom = null; _dateTo = null; _scopeFolder = ""; _hideHiddenSystem = false;
        foreach (var p in new[] { nameof(TypeFilter), nameof(KindFilter), nameof(ExtensionsText), nameof(MinSizeText), nameof(MaxSizeText),
                     nameof(DateFilter), nameof(IsCustomDate), nameof(DateFrom), nameof(DateTo), nameof(ScopeFolder), nameof(HideHiddenSystem), nameof(HasFilters) })
            OnPropertyChanged(p);
        Requery();
    }

    /// <summary>True while the next result set comes from a live-update refresh rather than user input (the view keeps its scroll position).</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Called by the index manager when live updates land; re-runs the current search quietly.</summary>
    public void Refresh()
    {
        IsRefreshing = true;
        Requery(immediate: true);
    }

    public void Requery(bool immediate = false)
    {
        OnPropertyChanged(nameof(HasFilters));
        _debounce.Stop();
        if (immediate) RunSearch();
        else _debounce.Start();
    }

    private FilterSet BuildFilters()
    {
        var f = new FilterSet
        {
            Kind = _typeFilter switch { "Files" => EntryKind.File, "Folders" => EntryKind.Folder, _ => EntryKind.Any },
            ScopeFolder = string.IsNullOrWhiteSpace(_scopeFolder) ? null : _scopeFolder.Trim(),
            HideHiddenAndSystem = _hideHiddenSystem,
        };
        var exts = new List<string>();
        if (_kindFilter != "Any" && FileKinds.TryGet(_kindFilter, out var kindExts)) exts.AddRange(kindExts);
        if (!string.IsNullOrWhiteSpace(_extensionsText))
            exts.AddRange(_extensionsText.Split(new[] { ';', ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries));
        if (exts.Count > 0) f.Extensions = exts;
        if (SizeParser.TryParseSize(_minSizeText, out long min, out _)) f.MinSize = min;
        if (SizeParser.TryParseSize(_maxSizeText, out long max, out _)) f.MaxSize = max;

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
        if (lo != null) f.ModifiedFromUtc = lo.Value.ToUniversalTime();
        if (hi != null) f.ModifiedToUtcExclusive = hi.Value.ToUniversalTime();
        return f;
    }

    private void RunSearch()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int version = ++_searchVersion;
        var options = new QueryOptions { MatchCase = _matchCase, UseRegex = _useRegex, WholeWord = _wholeWord, MatchPath = _matchPath };
        var filters = BuildFilters();
        string text = _queryText;
        var sort = _sortField;
        bool desc = _sortDescending;
        var volumes = _manager.ReadyIndexes;
        IsSearching = true;

        Task.Run(() =>
        {
            var query = QueryParser.Parse(text, options, filters);
            var result = SearchEngine.Search(volumes, query, sort, desc, cts.Token);
            var terms = query.TextTerms().ToList();
            return (result, name: new Controls.Highlighter(terms), path: new Controls.Highlighter(terms.Where(t => t.MatchPath)));
        }, cts.Token).ContinueWith(t =>
        {
            if (version != _searchVersion) return;
            IsSearching = false;
            if (t.IsCanceled) return;
            if (t.IsFaulted)
            {
                ErrorText = t.Exception?.GetBaseException().Message;
                return;
            }
            var (result, nameHighlighter, pathHighlighter) = t.Result;
            NameHighlighter = nameHighlighter;
            PathHighlighter = pathHighlighter;
            Results = new ResultCollection(result);
            IsRefreshing = false;
            ErrorText = result.Error;
            StatusText = volumes.Count == 0
                ? "Index is still building…"
                : $"{result.Count:N0} {(result.Count == 1 ? "item" : "items")}  ·  {result.Elapsed.TotalMilliseconds:0.#} ms";
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
