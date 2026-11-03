using System.Buffers.Binary;
using System.Text;
using FastFind.Core.Indexing;
using FastFind.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Core.Mft;

/// <summary>
/// Builds a <see cref="VolumeIndex"/> by reading the NTFS Master File Table directly from the raw volume.
/// One sequential read of the MFT yields every file name, parent link, size and timestamp on the volume
/// in a couple of seconds, which is the same trick "Everything" uses. Requires administrator rights.
/// </summary>
public static class MftReader
{
    private const int ChunkBytes = 8 * 1024 * 1024;

    public static VolumeIndex Read(string root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        char letter = root[0];
        using var handle = NativeMethods.OpenVolume(letter);
        var volume = NativeMethods.GetNtfsVolumeData(handle);

        int recordSize = (int)volume.BytesPerFileRecordSegment;
        int clusterSize = (int)volume.BytesPerCluster;
        long mftStart = volume.MftStartLcn * clusterSize;
        long mftValid = volume.MftValidDataLength;
        if (recordSize <= 0 || clusterSize <= 0 || mftValid <= 0)
            throw new InvalidDataException($"Unexpected NTFS geometry on {root}");

        // Record 0 describes the MFT itself; its $DATA runs tell us where the rest of the table lives.
        var record0 = new byte[recordSize];
        NativeMethods.ReadAt(handle, mftStart, record0);
        if (!MftRecord.ApplyFixups(record0))
            throw new InvalidDataException("MFT record 0 failed fixup validation");
        var runs = MftRecord.GetDataRuns(record0) ?? throw new InvalidDataException("MFT record 0 has no non-resident $DATA attribute");

        long totalRecords = mftValid / recordSize;
        var tables = new RawTables(totalRecords);
        progress?.Report(new IndexProgress(root, "Reading MFT", 0, totalRecords));

        ReadAllRecords(handle, runs, clusterSize, recordSize, mftValid, tables, root, progress, ct);

        progress?.Report(new IndexProgress(root, "Building index", totalRecords, totalRecords));
        return BuildIndex(root, tables, ct);
    }

    private static void ReadAllRecords(SafeFileHandle handle, List<(long Lcn, long Clusters)> runs, int clusterSize,
        int recordSize, long mftValid, RawTables tables, string root, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        // Flatten the runs into sector-aligned chunk reads, clipped to the valid length of the MFT.
        var chunks = new List<(long Offset, int Length)>();
        long consumed = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long byteOffset = lcn * clusterSize;
            long byteLength = clusters * clusterSize;
            if (consumed >= mftValid) break;
            byteLength = Math.Min(byteLength, mftValid - consumed);
            long pos = 0;
            while (pos < byteLength)
            {
                int len = (int)Math.Min(ChunkBytes, byteLength - pos);
                chunks.Add((byteOffset + pos, len));
                pos += len;
            }
            consumed += byteLength;
        }

        var bufferA = new byte[ChunkBytes];
        var bufferB = new byte[ChunkBytes];
        var carry = new byte[recordSize];
        int carryLen = 0;
        long recordNumber = 0;

        Task<int>? pending = null;
        byte[] pendingBuffer = bufferA;
        if (chunks.Count > 0)
        {
            var first = chunks[0];
            pending = Task.Run(() => NativeMethods.ReadAt(handle, first.Offset, bufferA.AsSpan(0, first.Length)));
        }

        for (int c = 0; c < chunks.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            int read = pending!.Result;
            var current = pendingBuffer;
            if (c + 1 < chunks.Count)
            {
                var next = chunks[c + 1];
                var nextBuffer = ReferenceEquals(current, bufferA) ? bufferB : bufferA;
                pendingBuffer = nextBuffer;
                pending = Task.Run(() => NativeMethods.ReadAt(handle, next.Offset, nextBuffer.AsSpan(0, next.Length)));
            }

            int pos = 0;
            if (carryLen > 0)
            {
                int need = recordSize - carryLen;
                int take = Math.Min(need, read);
                Array.Copy(current, 0, carry, carryLen, take);
                carryLen += take;
                pos = take;
                if (carryLen == recordSize)
                {
                    ParseOne(carry, recordNumber++, tables);
                    carryLen = 0;
                }
            }
            while (read - pos >= recordSize)
            {
                ParseOne(current.AsSpan(pos, recordSize), recordNumber++, tables);
                pos += recordSize;
            }
            if (pos < read)
            {
                carryLen = read - pos;
                Array.Copy(current, pos, carry, 0, carryLen);
            }
            if ((c & 3) == 0) progress?.Report(new IndexProgress(root, "Reading MFT", recordNumber, tables.Total));
        }
    }

    private static void ParseOne(Span<byte> record, long recordNumber, RawTables t)
    {
        if (recordNumber >= t.Total) return;
        if (!(record[0] == (byte)'F' && record[1] == (byte)'I' && record[2] == (byte)'L' && record[3] == (byte)'E')) return;
        if (!MftRecord.ApplyFixups(record)) return;
        MftRecord.Parse(record, recordNumber, t);
    }

    private static VolumeIndex BuildIndex(string root, RawTables t, CancellationToken ct)
    {
        long total = t.Total;
        const long RootRecord = 5;
        const long FirstUserRecord = 16;

        // Decide which records are reachable from the root through valid parent links.
        // 0 = unknown, 1 = reachable, 2 = orphan/skip
        var state = new byte[total];
        var stack = new Stack<long>();
        for (long r = 0; r < total; r++)
        {
            if (state[r] != 0) continue;
            if (!IsCandidate(r, t, RootRecord, FirstUserRecord)) { state[r] = 2; continue; }
            // walk up until we hit a decided record
            long cur = r;
            stack.Clear();
            byte result;
            while (true)
            {
                if (cur == RootRecord) { result = 1; break; }
                if (state[cur] != 0) { result = state[cur]; break; }
                if (!IsCandidate(cur, t, RootRecord, FirstUserRecord)) { result = 2; break; }
                stack.Push(cur);
                ulong pref = t.ParentRef[cur];
                long parent = (long)(pref & 0xFFFFFFFFFFFF);
                ushort parentSeq = (ushort)(pref >> 48);
                if (parent < 0 || parent >= total || !t.InUse[parent] || !t.IsDir[parent] || t.Seq[parent] != parentSeq) { result = 2; break; }
                if (stack.Count > 4096) { result = 2; break; } // cycle guard
                cur = parent;
            }
            while (stack.Count > 0) state[stack.Pop()] = result;
            if (state[r] == 0) state[r] = result;
        }

        int count = 1; // root entry
        for (long r = 0; r < total; r++) if (state[r] == 1 && r != RootRecord) count++;

        var names = new string?[count + count / 8 + 64];
        var meta = new EntryMeta[names.Length];
        var byKey = new Dictionary<ulong, int>(count);
        var recordToIndex = new int[total];
        Array.Fill(recordToIndex, -1);

        names[0] = root;
        meta[0] = new EntryMeta { Key = MakeKey(RootRecord, t.Seq[RootRecord]), Parent = -1, Attributes = FileAttributes.Directory };
        byKey[meta[0].Key] = 0;
        recordToIndex[RootRecord] = 0;

        int n = 1;
        for (long r = 0; r < total; r++)
        {
            if (state[r] != 1 || r == RootRecord) continue;
            recordToIndex[r] = n++;
        }

        n = 1;
        for (long r = 0; r < total; r++)
        {
            if (state[r] != 1 || r == RootRecord) continue;
            if ((r & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            long parentRec = (long)(t.ParentRef[r] & 0xFFFFFFFFFFFF);
            var attrs = (FileAttributes)t.DosAttributes[r];
            if (t.IsDir[r]) attrs |= FileAttributes.Directory;
            var key = MakeKey(r, t.Seq[r]);
            names[n] = t.Names[r];
            meta[n] = new EntryMeta
            {
                Key = key,
                Parent = recordToIndex[parentRec],
                Attributes = attrs,
                Size = t.IsDir[r] ? 0 : t.Size[r],
                ModifiedFileTime = t.Modified[r],
                CreatedFileTime = t.Created[r],
            };
            byKey[key] = n;
            n++;
        }

        return new VolumeIndex(root, names, meta, n, byKey, 0, IndexMode.Mft);
    }

    private static bool IsCandidate(long r, RawTables t, long rootRecord, long firstUserRecord)
        => r == rootRecord || (r >= firstUserRecord && t.InUse[r] && t.Names[r] != null);

    public static ulong MakeKey(long recordNumber, ushort sequence) => ((ulong)recordNumber & 0xFFFFFFFFFFFF) | ((ulong)sequence << 48);
}

/// <summary>Column-oriented scratch tables indexed by MFT record number, filled while streaming the table.</summary>
public sealed class RawTables
{
    public readonly long Total;
    public readonly string?[] Names;
    public readonly byte[] NameNamespace;
    public readonly ulong[] ParentRef;
    public readonly long[] Size;
    public readonly long[] Modified;
    public readonly long[] Created;
    public readonly uint[] DosAttributes;
    public readonly ushort[] Seq;
    public readonly bool[] InUse;
    public readonly bool[] IsDir;

    public RawTables(long total)
    {
        Total = total;
        Names = new string?[total];
        NameNamespace = new byte[total];
        ParentRef = new ulong[total];
        Size = new long[total];
        Modified = new long[total];
        Created = new long[total];
        DosAttributes = new uint[total];
        Seq = new ushort[total];
        InUse = new bool[total];
        IsDir = new bool[total];
    }
}

/// <summary>Low-level parsing of a single FILE record and its attributes.</summary>
public static class MftRecord
{
    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    private const byte NamespacePosix = 0, NamespaceWin32 = 1, NamespaceDos = 2, NamespaceWin32AndDos = 3;

    /// <summary>Applies the update sequence array (fixups) in place. Returns false if the record is torn/corrupt.</summary>
    public static bool ApplyFixups(Span<byte> record)
    {
        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return false;
        int stride = record.Length / (usaCount - 1);
        if (stride <= 2) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * stride - 2;
            if (pos + 2 > record.Length) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != usn) return false;
            record[pos] = record[usaOffset + i * 2];
            record[pos + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    public static void Parse(ReadOnlySpan<byte> record, long recordNumber, RawTables t)
    {
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        bool inUse = (flags & 0x01) != 0;
        if (!inUse) return;
        bool isDir = (flags & 0x02) != 0;

        ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
        long baseRecord = (long)(baseRef & 0xFFFFFFFFFFFF);
        bool isExtension = baseRecord != 0;
        long target = isExtension ? baseRecord : recordNumber;
        if (target < 0 || target >= t.Total) return;

        if (!isExtension)
        {
            t.InUse[recordNumber] = true;
            t.IsDir[recordNumber] = isDir;
            t.Seq[recordNumber] = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
        }

        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);

        while (attrOffset + 16 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[attrOffset..]);
            if (type == AttrEnd) break;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record[(attrOffset + 4)..]);
            if (length < 16 || attrOffset + length > used) break;
            var attr = record.Slice(attrOffset, (int)length);
            bool nonResident = attr[8] != 0;
            byte attrNameLength = attr[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident && !isExtension:
                {
                    var value = ResidentValue(attr);
                    if (value.Length >= 36)
                    {
                        t.Created[recordNumber] = BinaryPrimitives.ReadInt64LittleEndian(value);
                        t.Modified[recordNumber] = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                        t.DosAttributes[recordNumber] = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]) & 0x0000FFFF;
                    }
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var value = ResidentValue(attr);
                    if (value.Length >= 66)
                    {
                        byte nameLength = value[64];
                        byte ns = value[65];
                        if (ns != NamespaceDos && value.Length >= 66 + nameLength * 2)
                        {
                            bool take = t.Names[target] == null || (t.NameNamespace[target] == NamespacePosix && ns != NamespacePosix);
                            if (take)
                            {
                                t.Names[target] = Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
                                t.NameNamespace[target] = ns;
                                t.ParentRef[target] = BinaryPrimitives.ReadUInt64LittleEndian(value);
                            }
                        }
                    }
                    break;
                }
                case AttrData when attrNameLength == 0:
                {
                    if (nonResident)
                    {
                        if (attr.Length >= 0x38)
                        {
                            ulong startVcn = BinaryPrimitives.ReadUInt64LittleEndian(attr[0x10..]);
                            if (startVcn == 0) t.Size[target] = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                        }
                    }
                    else
                    {
                        t.Size[target] = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                    }
                    break;
                }
            }
            attrOffset += (int)length;
        }
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < 0x18) return default;
        uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
        ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
        if (valueOffset + valueLength > attr.Length) return default;
        return attr.Slice(valueOffset, (int)valueLength);
    }

    /// <summary>Returns the data runs (LCN, cluster count) of the unnamed non-resident $DATA attribute, or null.</summary>
    public static List<(long Lcn, long Clusters)>? GetDataRuns(ReadOnlySpan<byte> record)
    {
        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        while (attrOffset + 16 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[attrOffset..]);
            if (type == AttrEnd) break;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record[(attrOffset + 4)..]);
            if (length < 16 || attrOffset + length > used) break;
            var attr = record.Slice(attrOffset, (int)length);
            if (type == AttrData && attr[9] == 0 && attr[8] != 0)
            {
                ushort runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                return DecodeDataRuns(attr[runOffset..]);
            }
            attrOffset += (int)length;
        }
        return null;
    }

    public static List<(long Lcn, long Clusters)> DecodeDataRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        int pos = 0;
        while (pos < runs.Length)
        {
            byte header = runs[pos++];
            if (header == 0) break;
            int lengthSize = header & 0x0F;
            int offsetSize = header >> 4;
            if (lengthSize == 0 || pos + lengthSize + offsetSize > runs.Length) break;

            long clusters = 0;
            for (int i = 0; i < lengthSize; i++) clusters |= (long)runs[pos + i] << (8 * i);
            pos += lengthSize;

            if (offsetSize == 0)
            {
                // sparse run: no clusters on disk. Not expected for $MFT; skip.
                continue;
            }
            long delta = 0;
            for (int i = 0; i < offsetSize; i++) delta |= (long)runs[pos + i] << (8 * i);
            // sign-extend
            int shift = 64 - 8 * offsetSize;
            delta = (delta << shift) >> shift;
            pos += offsetSize;

            lcn += delta;
            result.Add((lcn, clusters));
        }
        return result;
    }
}
