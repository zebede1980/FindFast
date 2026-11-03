using System.Runtime.InteropServices;

namespace FastFind.Core.Indexing;

/// <summary>Per-entry metadata stored alongside the name array. Kept as a struct for cache-friendly scanning.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EntryMeta
{
    /// <summary>NTFS file reference (record number + sequence) when MFT-indexed, otherwise a synthetic id.</summary>
    public ulong Key;
    public long Size;
    /// <summary>Last write time as a Windows FILETIME (0 = unknown).</summary>
    public long ModifiedFileTime;
    /// <summary>Creation time as a Windows FILETIME (0 = unknown).</summary>
    public long CreatedFileTime;
    /// <summary>Index of the parent directory entry, or -1 for the volume root.</summary>
    public int Parent;
    public FileAttributes Attributes;

    public readonly bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

public enum IndexMode
{
    None,
    /// <summary>Built by reading the NTFS Master File Table directly; kept fresh via the USN change journal.</summary>
    Mft,
    /// <summary>Built by walking directories; kept fresh via FileSystemWatcher.</summary>
    DirectoryWalk,
}

public readonly record struct IndexProgress(string Root, string Phase, long Processed, long Total);
