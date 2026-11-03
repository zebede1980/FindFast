using System.Collections;
using System.IO;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.App.ViewModels;

/// <summary>A lightweight, lazily materialised row. Only rows that scroll into view are ever created.</summary>
public sealed class ResultRow
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

    public ResultRow(int position, VolumeIndex volume, int index)
    {
        Position = position;
        volume.Lock.EnterReadLock();
        try
        {
            var name = volume.GetName(index);
            IsDeleted = name == null;
            Name = name ?? "(deleted)";
            if (!IsDeleted)
            {
                ref var m = ref volume.GetMeta(index);
                Directory = volume.GetParentPath(index);
                IsDirectory = m.IsDirectory;
                Size = m.Size;
                ModifiedFileTime = m.ModifiedFileTime;
                CreatedFileTime = m.CreatedFileTime;
                Attributes = m.Attributes;
            }
            else
            {
                Directory = "";
            }
        }
        finally
        {
            volume.Lock.ExitReadLock();
        }
        FullPath = VolumeIndex.Combine(Directory, Name);
        Extension = IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.');
    }

    public string SizeText => IsDirectory ? "" : SizeParser.Format(Size);
    public DateTime? Modified => ModifiedFileTime > 0 ? DateTime.FromFileTimeUtc(ModifiedFileTime).ToLocalTime() : null;
    public DateTime? Created => CreatedFileTime > 0 ? DateTime.FromFileTimeUtc(CreatedFileTime).ToLocalTime() : null;
    public string TypeText => IsDirectory ? "Folder" : Extension.Length == 0 ? "File" : Extension.ToUpperInvariant();
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

    public ImageSource? Icon => _icon ??= ShellIcons.GetIcon(FullPath, IsDirectory, Extension);
}

/// <summary>Read-only IList facade over a <see cref="SearchResult"/> so the DataGrid can virtualise a million rows.</summary>
public sealed class ResultCollection : IList, IReadOnlyList<ResultRow>
{
    private readonly SearchResult _result;
    private readonly ResultRow?[] _cache;

    public ResultCollection(SearchResult result)
    {
        _result = result;
        _cache = new ResultRow?[result.Count];
    }

    public static readonly ResultCollection Empty = new(SearchResult.Empty);

    public SearchResult Result => _result;
    public int Count => _result.Count;

    public ResultRow this[int index]
    {
        get
        {
            var row = _cache[index];
            if (row == null)
            {
                var hit = _result.Hits[index];
                row = new ResultRow(index, _result.Volumes[hit.Volume], hit.Index);
                _cache[index] = row;
            }
            return row;
        }
    }

    public IEnumerator<ResultRow> GetEnumerator()
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
    bool IList.Contains(object? value) => value is ResultRow r && r.Position < Count && ReferenceEquals(_cache[r.Position], r);
    int IList.IndexOf(object? value) => value is ResultRow r && r.Position < Count && ReferenceEquals(_cache[r.Position], r) ? r.Position : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
}
