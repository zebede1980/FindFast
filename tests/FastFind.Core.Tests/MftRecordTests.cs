using System.Buffers.Binary;
using System.Text;
using FastFind.Core.Mft;
using Xunit;

namespace FastFind.Core.Tests;

/// <summary>Builds synthetic NTFS FILE records in memory and checks the parser reads them the way the on-disk layout dictates.</summary>
public class MftRecordTests
{
    private const int RecordSize = 1024;

    private static byte[] BuildRecord(long recordNumber, ushort sequence, bool inUse, bool isDir, string? name, ulong parentRef,
        long created, long modified, uint dosAttrs, long? residentDataLength, long? nonResidentRealSize, long baseRecord = 0, byte nameSpace = 1)
    {
        var rec = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);
        const ushort usaOffset = 0x30;
        const ushort usaCount = 3; // USN + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(6), usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x10), sequence);
        const ushort firstAttr = 0x38;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x14), firstAttr);
        ushort flags = (ushort)((inUse ? 1 : 0) | (isDir ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(0x20), (ulong)baseRecord);

        int pos = firstAttr;

        // $STANDARD_INFORMATION (0x10), resident, 48-byte value
        {
            var value = new byte[48];
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0), created);
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), modified);
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), dosAttrs);
            pos = WriteResident(rec, pos, 0x10, value);
        }
        // $FILE_NAME (0x30)
        if (name != null)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var value = new byte[66 + nameBytes.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0), parentRef);
            value[64] = (byte)name.Length;
            value[65] = nameSpace;
            nameBytes.CopyTo(value, 66);
            pos = WriteResident(rec, pos, 0x30, value);
        }
        // $DATA (0x80)
        if (residentDataLength != null)
        {
            pos = WriteResident(rec, pos, 0x80, new byte[residentDataLength.Value]);
        }
        else if (nonResidentRealSize != null)
        {
            int len = 0x48;
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 4), (uint)len);
            rec[pos + 8] = 1; // non-resident
            rec[pos + 9] = 0; // unnamed
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(pos + 0x10), 0); // start VCN
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 0x20), 0x40); // run offset
            BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(pos + 0x30), nonResidentRealSize.Value);
            rec[pos + 0x40] = 0x11; rec[pos + 0x41] = 0x08; rec[pos + 0x42] = 0x20; rec[pos + 0x43] = 0; // one run: 8 clusters at LCN 0x20
            pos += len;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), 0xFFFFFFFF);
        pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x18), (uint)pos);

        // Apply the update sequence: last two bytes of each 512-byte sector hold the USN; originals go in the array.
        ushort usn = 0x1234;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(usaOffset), usn);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * 512 - 2;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(usaOffset + i * 2), BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(sectorEnd)));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(sectorEnd), usn);
        }
        return rec;
    }

    private static int WriteResident(byte[] rec, int pos, uint type, byte[] value)
    {
        int len = (0x18 + value.Length + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), type);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 4), (uint)len);
        rec[pos + 8] = 0;
        rec[pos + 9] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 0x14), 0x18);
        value.CopyTo(rec, pos + 0x18);
        return pos + len;
    }

    [Fact]
    public void FixupsAreValidatedAndApplied()
    {
        var rec = BuildRecord(100, 7, true, false, "a.txt", 5 | (1UL << 48), 1, 2, 0x20, 10, null);
        var copy = (byte[])rec.Clone();
        Assert.True(MftRecord.ApplyFixups(copy));
        // corrupt a sector tail -> torn write detected
        copy = (byte[])rec.Clone();
        copy[510] ^= 0xFF;
        Assert.False(MftRecord.ApplyFixups(copy));
    }

    [Fact]
    public void ParsesNameParentTimesAndResidentSize()
    {
        var t = new RawTables(200);
        var rec = BuildRecord(100, 7, true, false, "Report.docx", 5 | (1UL << 48), 111, 222, 0x20, 77, null);
        Assert.True(MftRecord.ApplyFixups(rec));
        MftRecord.Parse(rec, 100, t);
        Assert.True(t.InUse[100]);
        Assert.False(t.IsDir[100]);
        Assert.Equal(7, t.Seq[100]);
        Assert.Equal("Report.docx", t.Names[100]);
        Assert.Equal(5UL | (1UL << 48), t.ParentRef[100]);
        Assert.Equal(111, t.Created[100]);
        Assert.Equal(222, t.Modified[100]);
        Assert.Equal(0x20u, t.DosAttributes[100]);
        Assert.Equal(77, t.Size[100]);
    }

    [Fact]
    public void ParsesNonResidentSizeAndDirectoryFlag()
    {
        var t = new RawTables(200);
        var file = BuildRecord(101, 1, true, false, "big.iso", 5 | (1UL << 48), 0, 0, 0, null, 4_000_000_000L);
        Assert.True(MftRecord.ApplyFixups(file));
        MftRecord.Parse(file, 101, t);
        Assert.Equal(4_000_000_000L, t.Size[101]);

        var dir = BuildRecord(102, 1, true, true, "Folder", 5 | (1UL << 48), 0, 0, 0x10, null, null);
        Assert.True(MftRecord.ApplyFixups(dir));
        MftRecord.Parse(dir, 102, t);
        Assert.True(t.IsDir[102]);
        Assert.Equal("Folder", t.Names[102]);
    }

    [Fact]
    public void SkipsDosOnlyNamesAndUnusedRecords()
    {
        var t = new RawTables(200);
        var dos = BuildRecord(103, 1, true, false, "REPORT~1.DOC", 5 | (1UL << 48), 0, 0, 0, 1, null, nameSpace: 2);
        Assert.True(MftRecord.ApplyFixups(dos));
        MftRecord.Parse(dos, 103, t);
        Assert.Null(t.Names[103]);

        var unused = BuildRecord(104, 1, false, false, "deleted.txt", 5 | (1UL << 48), 0, 0, 0, 1, null);
        Assert.True(MftRecord.ApplyFixups(unused));
        MftRecord.Parse(unused, 104, t);
        Assert.False(t.InUse[104]);
        Assert.Null(t.Names[104]);
    }

    [Fact]
    public void ExtensionRecordContributesToBase()
    {
        var t = new RawTables(200);
        var baseRec = BuildRecord(110, 3, true, false, "fragmented.bin", 5 | (1UL << 48), 0, 0, 0, null, null);
        Assert.True(MftRecord.ApplyFixups(baseRec));
        MftRecord.Parse(baseRec, 110, t);
        var ext = BuildRecord(111, 1, true, false, null, 0, 0, 0, 0, null, 123_456, baseRecord: 110 | (3L << 48));
        Assert.True(MftRecord.ApplyFixups(ext));
        MftRecord.Parse(ext, 111, t);
        Assert.Equal("fragmented.bin", t.Names[110]);
        Assert.Equal(123_456, t.Size[110]);
        Assert.False(t.InUse[111]); // extension records are not entries themselves
    }

    [Fact]
    public void DataRunsDecodeWithNegativeOffsets()
    {
        // run 1: 0x11 len=0x10 offset=+0x30 ; run 2: 0x11 len=0x08 offset=-0x10 (0xF0) ; end
        var runs = new byte[] { 0x11, 0x10, 0x30, 0x11, 0x08, 0xF0, 0x00 };
        var decoded = MftRecord.DecodeDataRuns(runs);
        Assert.Equal(2, decoded.Count);
        Assert.Equal((0x30L, 0x10L), decoded[0]);
        Assert.Equal((0x20L, 0x08L), decoded[1]);
    }

    [Fact]
    public void GetDataRunsFindsUnnamedNonResidentData()
    {
        var rec = BuildRecord(0, 1, true, false, "$MFT", 5 | (1UL << 48), 0, 0, 0, null, 1 << 20);
        Assert.True(MftRecord.ApplyFixups(rec));
        var runs = MftRecord.GetDataRuns(rec);
        Assert.NotNull(runs);
        Assert.Single(runs!);
        Assert.Equal((0x20L, 8L), runs![0]);
    }
}
