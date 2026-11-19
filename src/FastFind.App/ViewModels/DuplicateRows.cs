using System.IO;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.Core.Browse;
using FastFind.Core.Duplicates;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

/// <summary>
/// One line of the duplicates list: either the heading of a group of identical files, or one of the copies beneath it.
/// </summary>
public sealed class DuplicateRow : ObservableObject
{
    private bool _isTicked;
    private ImageSource? _icon;

    /// <summary>The heading of a group.</summary>
    public DuplicateRow(DuplicateGroup group, int copies)
    {
        Group = group;
        Copies = copies;
    }

    /// <summary>One copy in a group.</summary>
    public DuplicateRow(DuplicateGroup group, DuplicateFile file)
    {
        Group = group;
        File = file;
    }

    public DuplicateGroup Group { get; }
    /// <summary>The copy this row stands for; null on a group's heading.</summary>
    public DuplicateFile? File { get; }
    public bool IsHeader => File == null;
    public bool IsFile => File != null;
    /// <summary>On a heading: how many copies are still listed under it.</summary>
    public int Copies { get; }

    /// <summary>Ticked means "this copy can go". Changed only through <see cref="DuplicateList"/>, which keeps one copy of everything.</summary>
    public bool IsTicked
    {
        get => _isTicked;
        internal set => Set(ref _isTicked, value);
    }

    /// <summary>Makes a tick box show what the row really holds, after a click that was not allowed to change it.</summary>
    internal void RefreshTick() => OnPropertyChanged(nameof(IsTicked));

    public string Name => File?.Name ?? Group.Files[0].Name;

    /// <summary>The folder a copy is in. On a heading, what the group comes to.</summary>
    public string Detail => File?.Directory
        ?? $"{Copies:N0} copies  ·  {SizeParser.Format(Group.Size)} each  ·  {SizeParser.Format(Group.Size * (Copies - 1))} could be freed";

    public DateTime? Modified => File?.ModifiedUtc.ToLocalTime();

    public ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            string path = File?.Path ?? Group.Files[0].Path;
            return _icon = ShellIcons.GetIcon(path, false, Path.GetExtension(path).TrimStart('.'));
        }
    }
}

/// <summary>
/// The duplicates that were found, as the list shows them, and which copies have been ticked for deletion.
/// One rule holds whatever is clicked: at least one copy in every group stays unticked, so ticking can never lead to
/// the last copy of a file being deleted.
/// </summary>
public sealed class DuplicateList
{
    private readonly List<(DuplicateGroup Group, List<DuplicateRow> Files)> _groups = new();
    private readonly Dictionary<DuplicateGroup, List<DuplicateRow>> _byGroup = new();

    public DuplicateList(IEnumerable<DuplicateGroup> groups)
    {
        foreach (var group in groups)
        {
            var files = group.Files.Select(f => new DuplicateRow(group, f)).ToList();
            _groups.Add((group, files));
            _byGroup[group] = files;
        }
        Rebuild();
    }

    /// <summary>Headings and copies in display order.</summary>
    public IReadOnlyList<DuplicateRow> Rows { get; private set; } = Array.Empty<DuplicateRow>();

    /// <summary>Raised when ticks change.</summary>
    public event Action? TicksChanged;

    public int GroupCount => _groups.Count;
    /// <summary>How many files could go while leaving one copy of each.</summary>
    public int Surplus => _groups.Sum(g => g.Files.Count - 1);
    public long Wasted => _groups.Sum(g => g.Group.Size * (g.Files.Count - 1));
    public int TickedCount { get; private set; }
    public long TickedBytes { get; private set; }

    public IEnumerable<DuplicateGroup> Groups => _groups.Select(g => g.Group);

    public IReadOnlySet<string> TickedPaths()
        => _groups.SelectMany(g => g.Files).Where(r => r.IsTicked).Select(r => r.File!.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private List<DuplicateRow> FilesOf(DuplicateRow row) => _byGroup[row.Group];

    /// <summary>
    /// Ticks or unticks one copy. Returns false, changing nothing, when the tick would leave no copy of the file unticked.
    /// </summary>
    public bool SetTicked(DuplicateRow row, bool ticked)
    {
        if (row.IsHeader) return false;
        if (row.IsTicked == ticked) return true;
        if (ticked && FilesOf(row).Count(r => !r.IsTicked) <= 1)
        {
            row.RefreshTick();
            return false;
        }
        row.IsTicked = ticked;
        Recount();
        return true;
    }

    /// <summary>Ticks every copy in each group except the one modified most recently (the first by path when dates tie).</summary>
    public void KeepNewest() => KeepOne(files => files.OrderByDescending(r => r.File!.ModifiedUtc).First());

    /// <summary>Ticks every copy in each group except the oldest.</summary>
    public void KeepOldest() => KeepOne(files => files.OrderBy(r => r.File!.ModifiedUtc).First());

    private void KeepOne(Func<List<DuplicateRow>, DuplicateRow> pick)
    {
        foreach (var (_, files) in _groups)
        {
            var keep = pick(files);
            foreach (var row in files) row.IsTicked = !ReferenceEquals(row, keep);
        }
        Recount();
    }

    /// <summary>Ticks every other copy in this row's group, leaving this one.</summary>
    public void KeepOnly(DuplicateRow row)
    {
        if (row.IsHeader) return;
        foreach (var other in FilesOf(row)) other.IsTicked = !ReferenceEquals(other, row);
        Recount();
    }

    /// <summary>
    /// Ticks every copy that lies in <paramref name="folder"/> or beneath it, wherever a copy outside the folder is left
    /// to keep. Returns how many it ticked. For "these are all spare; the real ones are over there".
    /// </summary>
    public int TickInFolder(string folder)
    {
        int ticked = 0;
        foreach (var (_, files) in _groups)
        {
            var inside = files.Where(r => PathUtil.IsSameOrInside(r.File!.Directory, folder)).ToList();
            if (inside.Count == 0) continue;
            // If every copy is in the folder, or the only ones outside are already ticked, something inside has to stay.
            bool survivorOutside = files.Any(r => !r.IsTicked && !inside.Contains(r));
            foreach (var row in survivorOutside ? inside : inside.Skip(1))
            {
                if (row.IsTicked) continue;
                if (files.Count(r => !r.IsTicked) <= 1) break;
                row.IsTicked = true;
                ticked++;
            }
        }
        Recount();
        return ticked;
    }

    public void ClearTicks()
    {
        foreach (var (_, files) in _groups)
            foreach (var row in files) row.IsTicked = false;
        Recount();
    }

    /// <summary>
    /// Drops the copies that are no longer on disk (after a delete), and any group left with a single copy: a file
    /// with no twin is not a duplicate. Returns the size of what went.
    /// </summary>
    public long RemoveGone(Func<string, bool>? exists = null)
    {
        exists ??= System.IO.File.Exists;
        long freed = 0;
        foreach (var (group, files) in _groups)
            freed += group.Size * files.RemoveAll(r => !exists(r.File!.Path));
        _groups.RemoveAll(g => g.Files.Count < 2);
        Rebuild();
        return freed;
    }

    private void Rebuild()
    {
        var rows = new List<DuplicateRow>(_groups.Sum(g => g.Files.Count + 1));
        foreach (var (group, files) in _groups)
        {
            // A group down to its last unticked copy must not be left with every survivor ticked.
            if (files.All(r => r.IsTicked)) files[0].IsTicked = false;
            rows.Add(new DuplicateRow(group, files.Count));
            rows.AddRange(files);
        }
        Rows = rows;
        Recount();
    }

    private void Recount()
    {
        int count = 0;
        long bytes = 0;
        foreach (var (group, files) in _groups)
            foreach (var row in files)
                if (row.IsTicked) { count++; bytes += group.Size; }
        TickedCount = count;
        TickedBytes = bytes;
        TicksChanged?.Invoke();
    }
}
