using System.Collections;
using System.IO;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.Core.Api;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

/// <summary>One file or folder in a pane. Rows are created lazily, only for what scrolls into view.</summary>
public sealed class FileRow
{
    public int Position { get; }
    public string Name { get; }
    public string Directory { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsDeleted { get; }
    public long Size { get; }
    public long ModifiedFileTime { get; }
    public long CreatedFileTime { get; }
    public FileAttributes Attributes { get; }
    public string Extension { get; }

    private ImageSource? _icon;

    public FileRow(int position, in FileEntry entry)
    {
        Position = position;
        IsDeleted = entry.Name == null;
        Name = entry.Name ?? "(deleted)";
        Directory = entry.Directory;
        IsDirectory = entry.IsDirectory;
        Size = entry.Size;
        ModifiedFileTime = entry.ModifiedFileTime;
        CreatedFileTime = entry.CreatedFileTime;
        Attributes = entry.Attributes;
        FullPath = IsDeleted ? "" : entry.FullPath;
        Extension = IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.');
    }

    /// <summary>Folders show their total size when the index knows it; an unknown or empty folder shows nothing.</summary>
    public string SizeText => IsDeleted ? "" : IsDirectory ? (Size > 0 ? SizeParser.Format(Size) : "") : SizeParser.Format(Size);
    public DateTime? Modified => ModifiedFileTime > 0 ? DateTime.FromFileTimeUtc(ModifiedFileTime).ToLocalTime() : null;
    public DateTime? Created => CreatedFileTime > 0 ? DateTime.FromFileTimeUtc(CreatedFileTime).ToLocalTime() : null;
    public string TypeText => IsDeleted ? "" : IsDirectory ? "Folder" : Extension.Length == 0 ? "File" : Extension.ToUpperInvariant();
    public bool IsHidden => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>Explorer-style attribute letters: R H S A C E L (read-only, hidden, system, archive, compressed, encrypted, link).</summary>
    public string AttributesText
    {
        get
        {
            Span<char> buf = stackalloc char[8];
            int n = 0;
            if ((Attributes & FileAttributes.ReadOnly) != 0) buf[n++] = 'R';
            if ((Attributes & FileAttributes.Hidden) != 0) buf[n++] = 'H';
            if ((Attributes & FileAttributes.System) != 0) buf[n++] = 'S';
            if ((Attributes & FileAttributes.Archive) != 0) buf[n++] = 'A';
            if ((Attributes & FileAttributes.Compressed) != 0) buf[n++] = 'C';
            if ((Attributes & FileAttributes.Encrypted) != 0) buf[n++] = 'E';
            if ((Attributes & FileAttributes.ReparsePoint) != 0) buf[n++] = 'L';
            return new string(buf[..n]);
        }
    }

    public ImageSource? Icon => IsDeleted ? null : _icon ??= ShellIcons.GetIcon(FullPath, IsDirectory, Extension);

    /// <summary>What assistive technology announces for the row.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Read-only IList over an <see cref="IResultSet"/> so the grid can virtualise a million rows. Rows are fetched from
/// the result a page at a time, which keeps scrolling cheap even when the index lives in another process.
/// </summary>
public sealed class ResultCollection : IList, IReadOnlyList<FileRow>
{
    private const int PageSize = 128;
    private readonly IResultSet _set;
    private readonly FileRow?[] _cache;

    public ResultCollection(IResultSet set)
    {
        _set = set;
        _cache = new FileRow?[set.Count];
    }

    public static readonly ResultCollection Empty = new(EmptyResultSet.Instance);

    public IResultSet Set => _set;
    public int Count => _cache.Length;

    public FileRow this[int index]
    {
        get
        {
            var row = _cache[index];
            if (row != null) return row;
            int start = index - index % PageSize;
            var page = _set.GetRows(start, PageSize);
            for (int i = 0; i < page.Length && start + i < _cache.Length; i++)
                _cache[start + i] ??= new FileRow(start + i, page[i]);
            return _cache[index] ??= new FileRow(index, new FileEntry(null, "", 0, 0, 0, 0));
        }
    }

    /// <summary>Position of the row with this name (case-insensitive), searching at most <paramref name="limit"/> rows. -1 if absent.</summary>
    public int IndexOfName(string name, int limit = 200_000)
    {
        int n = Math.Min(Count, limit);
        for (int i = 0; i < n; i++)
            if (string.Equals(this[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public IEnumerator<FileRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- IList (read-only) ----
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is FileRow r && r.Position < Count && ReferenceEquals(_cache[r.Position], r);
    int IList.IndexOf(object? value) => value is FileRow r && r.Position < Count && ReferenceEquals(_cache[r.Position], r) ? r.Position : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
}
