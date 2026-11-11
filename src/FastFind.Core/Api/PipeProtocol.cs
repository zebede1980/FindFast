using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FastFind.Core.Indexing;
using FastFind.Core.Native;
using FastFind.Core.Search;

namespace FastFind.Core.Api;

/// <summary>
/// Wire format shared by <see cref="PipeIndexServer"/> and <see cref="PipeIndexClient"/>.
/// Every message is a frame: int32 length, then [kind:byte][id:int32][op:byte][body]. Strings are BinaryWriter strings (UTF-8).
/// A client sends requests; the server answers each with a response (or error) carrying the same id, and may send
/// events (id 0) at any time.
/// </summary>
public static class PipeProtocol
{
    public const int Version = 1;
    public const int MaxFrame = 64 * 1024 * 1024;

    public enum Kind : byte { Request = 0, Response = 1, Error = 2, Event = 3 }

    public enum Op : byte
    {
        Hello = 1, Search = 3, GetRows = 4, Release = 5, Cancel = 6, Rebuild = 7, Configure = 8, FolderSizes = 9, RefreshVolumes = 10,
        // events
        VolumesChanged = 100, IndexChanged = 101,
    }

    /// <summary>The pipe is per user, so two people signed in to one machine each talk to their own index.</summary>
    public static string PipeName(string userSid) => "FastFind.Index.v1." + userSid;

    public static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value ?? "unknown";

    /// <summary>
    /// Access list for the server end: the served user may read and write, but only an administrator (or, when the
    /// server is not elevated, the user themself) may create further instances, so another process cannot pose as the index.
    /// </summary>
    public static PipeSecurity CreateSecurity(string userSid)
    {
        var security = new PipeSecurity();
        var user = new SecurityIdentifier(userSid);
        var self = WindowsIdentity.GetCurrent().User;
        bool elevated = Elevation.IsElevated;
        security.AddAccessRule(new PipeAccessRule(user, elevated ? PipeAccessRights.ReadWrite : PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!elevated && self != null && self != user)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // ---- framing ----

    public static void WriteFrame(Stream stream, object writeLock, Kind kind, int id, Op op, Action<BinaryWriter>? body)
    {
        using var ms = new MemoryStream(256);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0); // length placeholder
            w.Write((byte)kind);
            w.Write(id);
            w.Write((byte)op);
            body?.Invoke(w);
        }
        int length = (int)ms.Length - 4;
        var buffer = ms.GetBuffer();
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), length);
        lock (writeLock)
        {
            stream.Write(buffer, 0, length + 4);
            stream.Flush();
        }
    }

    /// <summary>Reads one frame, or returns null when the other end has closed the pipe.</summary>
    public static byte[]? ReadFrame(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactly(stream, header)) return null;
        int length = BitConverter.ToInt32(header);
        if (length < 6 || length > MaxFrame) throw new InvalidDataException($"Bad frame length {length}");
        var payload = new byte[length];
        return ReadExactly(stream, payload) ? payload : null;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    // ---- bodies ----

    private static void WriteNullable(BinaryWriter w, string? s)
    {
        w.Write(s != null);
        if (s != null) w.Write(s);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteNullable(BinaryWriter w, long? v)
    {
        w.Write(v != null);
        if (v != null) w.Write(v.Value);
    }

    private static long? ReadNullableLong(BinaryReader r) => r.ReadBoolean() ? r.ReadInt64() : null;

    private static void WriteNullable(BinaryWriter w, DateTime? v) => WriteNullable(w, v?.ToUniversalTime().Ticks);

    private static DateTime? ReadNullableDate(BinaryReader r) => ReadNullableLong(r) is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public static void Write(BinaryWriter w, SearchRequest q)
    {
        w.Write(q.Text ?? "");
        w.Write(q.MatchCase); w.Write(q.UseRegex); w.Write(q.WholeWord); w.Write(q.MatchPath);
        w.Write((byte)q.Kind);
        w.Write(q.Extensions?.Length ?? 0);
        if (q.Extensions != null) foreach (var e in q.Extensions) w.Write(e);
        WriteNullable(w, q.MinSize); WriteNullable(w, q.MaxSize);
        WriteNullable(w, q.ModifiedFromUtc); WriteNullable(w, q.ModifiedToUtcExclusive);
        WriteNullable(w, q.CreatedFromUtc); WriteNullable(w, q.CreatedToUtcExclusive);
        WriteNullable(w, q.ScopeFolder);
        w.Write(q.ScopeRecursive); w.Write(q.HideHiddenAndSystem);
        w.Write((byte)q.Sort); w.Write(q.Descending); w.Write(q.FoldersFirst);
    }

    public static SearchRequest ReadSearchRequest(BinaryReader r)
    {
        var q = new SearchRequest
        {
            Text = r.ReadString(),
            MatchCase = r.ReadBoolean(), UseRegex = r.ReadBoolean(), WholeWord = r.ReadBoolean(), MatchPath = r.ReadBoolean(),
            Kind = (EntryKind)r.ReadByte(),
        };
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000) throw new InvalidDataException("Bad extension count");
        if (n > 0)
        {
            q.Extensions = new string[n];
            for (int i = 0; i < n; i++) q.Extensions[i] = r.ReadString();
        }
        q.MinSize = ReadNullableLong(r); q.MaxSize = ReadNullableLong(r);
        q.ModifiedFromUtc = ReadNullableDate(r); q.ModifiedToUtcExclusive = ReadNullableDate(r);
        q.CreatedFromUtc = ReadNullableDate(r); q.CreatedToUtcExclusive = ReadNullableDate(r);
        q.ScopeFolder = ReadNullable(r);
        q.ScopeRecursive = r.ReadBoolean(); q.HideHiddenAndSystem = r.ReadBoolean();
        q.Sort = (SortField)r.ReadByte(); q.Descending = r.ReadBoolean(); q.FoldersFirst = r.ReadBoolean();
        return q;
    }

    /// <summary>Rows of one page. Consecutive rows usually share a folder, so the folder is sent once per run.</summary>
    public static void Write(BinaryWriter w, FileEntry[] rows)
    {
        w.Write(rows.Length);
        string? previousDir = null;
        foreach (var e in rows)
        {
            if (e.Name == null) { w.Write((byte)0); continue; }
            bool sameDir = previousDir != null && previousDir == e.Directory;
            w.Write((byte)(sameDir ? 2 : 1));
            w.Write(e.Name);
            if (!sameDir) { w.Write(e.Directory); previousDir = e.Directory; }
            w.Write(e.Size); w.Write(e.ModifiedFileTime); w.Write(e.CreatedFileTime); w.Write((int)e.Attributes);
        }
    }

    public static FileEntry[] ReadRows(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 1_000_000) throw new InvalidDataException("Bad row count");
        var rows = new FileEntry[n];
        string dir = "";
        for (int i = 0; i < n; i++)
        {
            byte flag = r.ReadByte();
            if (flag == 0) { rows[i] = new FileEntry(null, "", 0, 0, 0, 0); continue; }
            string name = r.ReadString();
            if (flag == 1) dir = r.ReadString();
            rows[i] = new FileEntry(name, dir, r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), (FileAttributes)r.ReadInt32());
        }
        return rows;
    }

    public static void Write(BinaryWriter w, IReadOnlyList<VolumeInfo> volumes)
    {
        w.Write(volumes.Count);
        foreach (var v in volumes)
        {
            w.Write(v.Root); w.Write(v.Label); w.Write(v.Format);
            w.Write((byte)v.Status); w.Write((byte)v.Mode); w.Write(v.Entries);
            WriteNullable(w, v.Error); w.Write(v.ProgressText ?? "");
            w.Write(v.BuildTime.Ticks); w.Write(v.LiveUpdates); w.Write(v.IsNetwork);
            WriteNullable(w, v.IndexedAtUtc); WriteNullable(w, v.NextScanUtc);
        }
    }

    public static List<VolumeInfo> ReadVolumes(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000) throw new InvalidDataException("Bad volume count");
        var list = new List<VolumeInfo>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new VolumeInfo
            {
                Root = r.ReadString(), Label = r.ReadString(), Format = r.ReadString(),
                Status = (VolumeStatus)r.ReadByte(), Mode = (IndexMode)r.ReadByte(), Entries = r.ReadInt64(),
                Error = ReadNullable(r), ProgressText = r.ReadString(),
                BuildTime = new TimeSpan(r.ReadInt64()), LiveUpdates = r.ReadBoolean(), IsNetwork = r.ReadBoolean(),
                IndexedAtUtc = ReadNullableDate(r), NextScanUtc = ReadNullableDate(r),
            });
        }
        return list;
    }

    public static void Write(BinaryWriter w, IndexSettings s)
    {
        w.Write(s.IncludeRemovable); w.Write(s.IncludeNetwork);
        w.Write(s.ExcludedRoots.Count);
        foreach (var x in s.ExcludedRoots) w.Write(x);
        w.Write(s.NetworkLocations.Count);
        foreach (var x in s.NetworkLocations) w.Write(x);
        w.Write(s.NetworkRescanHours);
    }

    public static IndexSettings ReadSettings(BinaryReader r)
    {
        var s = new IndexSettings { IncludeRemovable = r.ReadBoolean(), IncludeNetwork = r.ReadBoolean() };
        int n = r.ReadInt32();
        if (n < 0 || n > 10_000) throw new InvalidDataException("Bad list count");
        for (int i = 0; i < n; i++) s.ExcludedRoots.Add(r.ReadString());
        n = r.ReadInt32();
        if (n < 0 || n > 10_000) throw new InvalidDataException("Bad list count");
        for (int i = 0; i < n; i++) s.NetworkLocations.Add(r.ReadString());
        s.NetworkRescanHours = r.ReadDouble();
        return s;
    }

    public static void Write(BinaryWriter w, string? value) => WriteNullable(w, value);
    public static string? ReadNullableString(BinaryReader r) => ReadNullable(r);
}
