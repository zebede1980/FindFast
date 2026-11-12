using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.App.Treemap;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.App.ViewModels;

/// <summary>One line of the disk usage tree: a file or folder, indented by how deep it is.</summary>
public sealed class UsageRow : ObservableObject
{
    private readonly SizeTree _tree;
    private bool _isExpanded;
    private ImageSource? _icon;

    public UsageRow(SizeTree tree, int node, int depth, bool expanded)
    {
        _tree = tree;
        Node = node;
        Depth = depth;
        _isExpanded = expanded;
    }

    public int Node { get; }
    public int Depth { get; }
    public string Name => _tree.Name(Node);
    public bool IsDirectory => _tree.IsDirectory(Node);
    public bool CanExpand => IsDirectory && _tree.ChildCount(Node) > 0;
    public bool IsCloudOnly => _tree.IsCloudOnly(Node);
    public Thickness Indent => new(4 + Depth * 18, 0, 0, 0);

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpanderGlyph)); }
    }

    /// <summary>Chevron pointing right (closed) or down (open).</summary>
    public string ExpanderGlyph => _isExpanded ? "" : "";

    public ImageSource? Icon => _icon ??= ShellIcons.GetIcon(_tree.GetPath(Node), IsDirectory, _tree.Extension(Node));

    /// <summary>The colour this file has on the map; nothing for folders.</summary>
    public Brush? Swatch => IsDirectory ? null : UsageBrushes.ForExtension(_tree, _tree.ExtensionId(Node));

    public string SizeText => IsCloudOnly ? "online-only" : SizeParser.Format(_tree.Size(Node));

    /// <summary>This item's share of the folder it is in (of itself, for the top row).</summary>
    public double Fraction
    {
        get
        {
            if (Node == SizeTree.Root) return 1;
            long parent = _tree.Size(_tree.Parent(Node));
            return parent <= 0 ? 0 : (double)_tree.Size(Node) / parent;
        }
    }

    public string PercentText => Fraction.ToString("0.0%");
    public double BarWidth => Math.Round(Fraction * 84);
    public string FilesText => IsDirectory ? _tree.FileCount(Node).ToString("N0") : "";
    public DateTime? Modified => _tree.ModifiedFileTime(Node) > 0 ? DateTime.FromFileTimeUtc(_tree.ModifiedFileTime(Node)).ToLocalTime() : null;

    /// <summary>What assistive technology announces for the row.</summary>
    public override string ToString() => Name;
}

/// <summary>Frozen brushes for map colours, shared between the tree, the type list and the legend.</summary>
public static class UsageBrushes
{
    private static readonly Dictionary<Color, Brush> Cache = new();

    public static Brush For(Color colour)
    {
        if (Cache.TryGetValue(colour, out var brush)) return brush;
        brush = new SolidColorBrush(colour);
        brush.Freeze();
        if (Cache.Count > 4096) Cache.Clear();
        return Cache[colour] = brush;
    }

    public static Brush ForExtension(SizeTree tree, int extensionId) => For(TreemapPalette.ExtensionColour(tree, extensionId));
    public static Brush ForCategory(FileCategory category) => For(TreemapPalette.CategoryColour(category));
}

/// <summary>
/// The disk usage tree flattened into the rows currently open, so an ordinary virtualised list can show it however
/// big a folder is (a folder of 300,000 files costs one number per file until a row scrolls into view).
/// </summary>
public sealed class UsageRowList : IList, IReadOnlyList<UsageRow>, INotifyCollectionChanged
{
    private readonly SizeTree _tree;
    private readonly List<int> _nodes = new();
    private readonly HashSet<int> _expanded = new();
    private readonly Dictionary<int, UsageRow> _rows = new();

    public UsageRowList(SizeTree tree)
    {
        _tree = tree;
        _nodes.Add(SizeTree.Root);
        Expand(SizeTree.Root);
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _nodes.Count;

    public UsageRow this[int index]
    {
        get
        {
            int node = _nodes[index];
            if (_rows.TryGetValue(node, out var row)) return row;
            if (_rows.Count > 20_000) _rows.Clear(); // rows are cheap to make again; do not keep every row ever scrolled past
            return _rows[node] = new UsageRow(_tree, node, _tree.Depth(node), _expanded.Contains(node));
        }
    }

    public bool IsExpanded(int node) => _expanded.Contains(node);

    /// <summary>Position of a node among the open rows, or -1 when a folder above it is closed.</summary>
    public int IndexOf(int node) => _nodes.IndexOf(node);

    public void Toggle(int node)
    {
        if (_expanded.Contains(node)) Collapse(node);
        else Expand(node);
    }

    public void Expand(int node)
    {
        if (ExpandQuietly(node)) Changed();
    }

    private bool ExpandQuietly(int node)
    {
        if (!_tree.IsDirectory(node) || _tree.ChildCount(node) == 0 || !_expanded.Add(node)) return false;
        int at = _nodes.IndexOf(node);
        if (at < 0) return false; // hidden inside a closed folder: it will open when that one does
        _nodes.InsertRange(at + 1, _tree.Children(node).ToArray());
        if (_rows.TryGetValue(node, out var row)) row.IsExpanded = true;
        return true;
    }

    public void Collapse(int node)
    {
        if (!_expanded.Remove(node)) return;
        if (_rows.TryGetValue(node, out var row)) row.IsExpanded = false;
        int at = _nodes.IndexOf(node);
        if (at < 0) return;
        int depth = _tree.Depth(node), end = at + 1;
        while (end < _nodes.Count && _tree.Depth(_nodes[end]) > depth)
        {
            // Folders inside close with it, so reopening shows one level again.
            if (_expanded.Remove(_nodes[end]) && _rows.TryGetValue(_nodes[end], out var inner)) inner.IsExpanded = false;
            end++;
        }
        _nodes.RemoveRange(at + 1, end - at - 1);
        Changed();
    }

    /// <summary>Opens every folder above a node so that it has a row, and returns that row's position (-1 if the node is not in the tree).</summary>
    public int Reveal(int node)
    {
        if (node < 0 || _tree.IsRemoved(node)) return -1;
        var chain = new List<int>();
        for (int n = _tree.Parent(node); n >= 0; n = _tree.Parent(n)) chain.Add(n);
        bool changed = false;
        for (int i = chain.Count - 1; i >= 0; i--) changed |= ExpandQuietly(chain[i]);
        if (changed) Changed();
        return _nodes.IndexOf(node);
    }

    /// <summary>Rebuilds the rows after the tree changed (something was removed), keeping open what was open.</summary>
    public void Rebuild()
    {
        _expanded.RemoveWhere(n => _tree.IsRemoved(n) || !IsAttached(n));
        _expanded.Add(SizeTree.Root);
        _rows.Clear();
        _nodes.Clear();
        Append(SizeTree.Root);
        Changed();
    }

    private bool IsAttached(int node)
    {
        for (int n = node; n > SizeTree.Root; n = _tree.Parent(n))
            if (_tree.IsRemoved(n)) return false;
        return true;
    }

    private void Append(int node)
    {
        _nodes.Add(node);
        if (!_expanded.Contains(node)) return;
        foreach (int child in _tree.Children(node)) Append(child);
    }

    private void Changed() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    public IEnumerator<UsageRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- IList (read-only) ----
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is UsageRow row && _nodes.Contains(row.Node);
    int IList.IndexOf(object? value) => value is UsageRow row ? _nodes.IndexOf(row.Node) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
}

/// <summary>One line of the file type list beside the tree.</summary>
public sealed class UsageTypeRow
{
    public UsageTypeRow(ExtensionStat stat, long total, Brush swatch)
    {
        ExtensionId = stat.Id;
        Extension = stat.Extension.Length == 0 ? "(none)" : "." + stat.Extension;
        Kind = FileCategories.Name(stat.Category);
        Bytes = stat.Bytes;
        Files = stat.Files;
        SizeText = SizeParser.Format(stat.Bytes);
        PercentText = total <= 0 ? "" : ((double)stat.Bytes / total).ToString("0.0%");
        FilesText = stat.Files.ToString("N0");
        Swatch = swatch;
    }

    public int ExtensionId { get; }
    public string Extension { get; }
    public string Kind { get; }
    public long Bytes { get; }
    public int Files { get; }
    public string SizeText { get; }
    public string PercentText { get; }
    public string FilesText { get; }
    public Brush Swatch { get; }
    public override string ToString() => Extension;
}

/// <summary>One kind of file in the legend above the map.</summary>
public sealed class UsageKindChip : ObservableObject
{
    private bool _isPicked;

    public UsageKindChip(FileCategory category, long bytes, long total)
    {
        Category = category;
        Bytes = bytes;
        Name = FileCategories.Name(category);
        Swatch = UsageBrushes.ForCategory(category);
        SizeText = SizeParser.Format(bytes);
        ToolTip = $"{Name}: {SizeText}" + (total > 0 ? $" ({(double)bytes / total:0.0%})" : "") + ". Click to pick these out on the map.";
    }

    public FileCategory Category { get; }
    public long Bytes { get; }
    public string Name { get; }
    public Brush Swatch { get; }
    public string SizeText { get; }
    public string ToolTip { get; }
    public bool IsPicked { get => _isPicked; set => Set(ref _isPicked, value); }
    public override string ToString() => Name;
}

/// <summary>One application in the disk usage window's Applications list.</summary>
public sealed class UsageAppRow
{
    private const double BarLength = 150;

    public UsageAppRow(AppUsage app, long largest)
    {
        App = app;
        Name = app.Name;
        SizeText = SizeParser.Format(app.Total);
        var parts = new List<string>();
        if (app.Program > 0) parts.Add("program " + SizeParser.Format(app.Program));
        if (app.Data > 0) parts.Add("data " + SizeParser.Format(app.Data));
        if (app.Caches > 0) parts.Add("caches " + SizeParser.Format(app.Caches));
        if (app.Logs > 0) parts.Add("logs " + SizeParser.Format(app.Logs));
        Detail = string.Join("  ·  ", parts);
        // One bar per application, as long as its share of the biggest, split into what the space is used for.
        double scale = largest <= 0 ? 0 : BarLength / largest;
        ProgramWidth = Math.Round(app.Program * scale);
        DataWidth = Math.Round(app.Data * scale);
        CacheWidth = Math.Round(app.Caches * scale);
        LogWidth = Math.Round(app.Logs * scale);
        CanClear = app.ClearableBytes >= 1024 * 1024;
        ClearText = CanClear ? "Clear " + SizeParser.Format(app.ClearableBytes) : "";
        ToolTip = $"{app.Name}: {SizeText} in {app.Files:N0} files across {app.Roots.Count:N0} folder{(app.Roots.Count == 1 ? "" : "s")}. Click to pick it out on the map.";
    }

    public AppUsage App { get; }
    public string Name { get; }
    public string SizeText { get; }
    public string Detail { get; }
    public double ProgramWidth { get; }
    public double DataWidth { get; }
    public double CacheWidth { get; }
    public double LogWidth { get; }
    public bool CanClear { get; }
    public string ClearText { get; }
    public string ToolTip { get; }
    public override string ToString() => Name;
}

/// <summary>One thing that could be removed, in the disk usage window's Clean up list.</summary>
public sealed class UsageCleanupRow
{
    public UsageCleanupRow(CleanupCandidate candidate, string? heading)
    {
        Candidate = candidate;
        Heading = heading;
        Title = candidate.Title;
        Detail = candidate.Detail;
        SizeText = SizeParser.Format(candidate.Bytes);
        ActionText = candidate.Action switch
        {
            CleanupAction.Clear => "Clear " + SizeText,
            CleanupAction.EmptyRecycleBin => "Empty Recycle Bin",
            CleanupAction.DiskCleanup => "Open Disk Clean-up",
            CleanupAction.CopyCommand => "Copy command",
            _ => "",
        };
        HasAction = ActionText.Length > 0;
        IsPrimary = candidate.Safety == CleanupSafety.Safe && candidate.Action == CleanupAction.Clear;
        Command = candidate.Command;
    }

    public CleanupCandidate Candidate { get; }
    /// <summary>Shown above the first row of each group.</summary>
    public string? Heading { get; }
    public string Title { get; }
    public string Detail { get; }
    public string SizeText { get; }
    public string ActionText { get; }
    public bool HasAction { get; }
    /// <summary>The safe, one-click kind: its button is drawn as the obvious thing to press.</summary>
    public bool IsPrimary { get; }
    public string? Command { get; }
    public override string ToString() => Title;

    public static string GroupName(CleanupSafety safety) => safety switch
    {
        CleanupSafety.Safe => "SAFE TO CLEAR",
        CleanupSafety.Rebuildable => "CAN BE REBUILT",
        CleanupSafety.Windows => "WINDOWS LOOKS AFTER THESE",
        _ => "YOURS TO REVIEW",
    };
}
