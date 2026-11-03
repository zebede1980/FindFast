using System.Buffers.Binary;
using System.Text;
using FastFind.Core.Indexing;
using FastFind.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Core.Usn;

/// <summary>
/// Tails the NTFS USN change journal and applies creates, deletes, renames and size/date changes to the index.
/// </summary>
public sealed class UsnJournalWatcher : IDisposable
{
    private const uint ReasonDataOverwrite = 0x00000001;
    private const uint ReasonDataExtend = 0x00000002;
    private const uint ReasonDataTruncation = 0x00000004;
    private const uint ReasonFileCreate = 0x00000100;
    private const uint ReasonFileDelete = 0x00000200;
    private const uint ReasonRenameOldName = 0x00001000;
    private const uint ReasonRenameNewName = 0x00002000;
    private const uint ReasonBasicInfoChange = 0x00008000;
    private const uint ReasonClose = 0x80000000;
    private const uint ReasonsNeedingStat = ReasonDataOverwrite | ReasonDataExtend | ReasonDataTruncation | ReasonBasicInfoChange | ReasonFileCreate | ReasonRenameNewName;

    private readonly VolumeIndex _index;
    private readonly ulong _journalId;
    private long _nextUsn;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    /// <summary>Raised (on the watcher thread) after a batch of changes has been applied.</summary>
    public event Action<VolumeIndex>? Changed;

    /// <summary>Raised when the journal was truncated or recreated and the index must be rebuilt.</summary>
    public event Action<VolumeIndex, string>? RebuildRequired;

    public event Action<string>? Log;

    private UsnJournalWatcher(VolumeIndex index, ulong journalId, long startUsn)
    {
        _index = index;
        _journalId = journalId;
        _nextUsn = startUsn;
    }

    /// <summary>Captures the journal position. Call this before reading the MFT so no change is missed.</summary>
    public static (ulong JournalId, long NextUsn) CapturePosition(char driveLetter)
    {
        using var handle = NativeMethods.OpenVolume(driveLetter);
        if (!NativeMethods.TryQueryUsnJournal(handle, out var data, out int err))
        {
            if (err == NativeMethods.ERROR_JOURNAL_NOT_ACTIVE)
            {
                NativeMethods.CreateUsnJournal(handle);
                if (!NativeMethods.TryQueryUsnJournal(handle, out data, out err))
                    throw new System.ComponentModel.Win32Exception(err, "Cannot query USN journal after creating it");
            }
            else throw new System.ComponentModel.Win32Exception(err, "Cannot query USN journal");
        }
        return (data.UsnJournalID, data.NextUsn);
    }

    public static UsnJournalWatcher Start(VolumeIndex index, ulong journalId, long startUsn)
    {
        var w = new UsnJournalWatcher(index, journalId, startUsn);
        w._thread = new Thread(w.Run) { IsBackground = true, Name = $"USN {index.Root}" };
        w._thread.Start();
        return w;
    }

    private void Run()
    {
        SafeFileHandle? handle = null;
        var buffer = new byte[256 * 1024];
        var ct = _cts.Token;
        try
        {
            handle = NativeMethods.OpenVolume(_index.Root[0]);
            while (!ct.IsCancellationRequested)
            {
                var request = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = _nextUsn,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = _journalId,
                };
                int returned = NativeMethods.ReadUsnJournal(handle, ref request, buffer, out int err);
                if (returned < 0)
                {
                    if (err == NativeMethods.ERROR_JOURNAL_ENTRY_DELETED || err == NativeMethods.ERROR_JOURNAL_NOT_ACTIVE || err == NativeMethods.ERROR_JOURNAL_DELETE_IN_PROGRESS)
                    {
                        RebuildRequired?.Invoke(_index, $"USN journal on {_index.Root} was reset (error {err})");
                        return;
                    }
                    Log?.Invoke($"USN read error {err} on {_index.Root}; retrying");
                    if (ct.WaitHandle.WaitOne(2000)) return;
                    continue;
                }
                if (returned <= 8)
                {
                    if (returned == 8) _nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                    if (ct.WaitHandle.WaitOne(400)) return;
                    continue;
                }
                _nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                int applied = ApplyBatch(buffer.AsSpan(8, returned - 8));
                if (applied > 0) Changed?.Invoke(_index);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log?.Invoke($"USN watcher on {_index.Root} stopped: {ex.Message}");
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private int ApplyBatch(ReadOnlySpan<byte> data)
    {
        var pendingStat = new List<int>();
        int applied = 0;
        _index.Lock.EnterWriteLock();
        try
        {
            int pos = 0;
            while (pos + 60 <= data.Length)
            {
                int recordLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                if (recordLength < 60 || pos + recordLength > data.Length) break;
                var rec = data.Slice(pos, recordLength);
                pos += recordLength;

                ushort major = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
                if (major != 2) continue; // only V2 (64-bit ids) is produced for NTFS

                ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                ulong parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);
                long timestamp = BinaryPrimitives.ReadInt64LittleEndian(rec[32..]);
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(rec[40..]);
                var attrs = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(rec[52..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(rec[56..]);
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec[58..]);
                if (nameOffset + nameLength > rec.Length) continue;
                var nameSpan = rec.Slice(nameOffset, nameLength);

                bool known = _index.TryGetIndex(frn, out int idx);

                if ((reason & ReasonFileDelete) != 0)
                {
                    if (known) { _index.Remove(idx); applied++; }
                    continue;
                }

                if ((reason & ReasonRenameNewName) != 0)
                {
                    string name = Encoding.Unicode.GetString(nameSpan);
                    if (known)
                    {
                        if (_index.TryGetIndex(parentFrn, out int parentIdx)) _index.Rename(idx, name, parentIdx);
                        else _index.Remove(idx);
                        applied++;
                    }
                    else if (_index.TryGetIndex(parentFrn, out int parentIdx))
                    {
                        idx = _index.Add(frn, name, parentIdx, attrs, 0, timestamp, timestamp);
                        known = true;
                        applied++;
                    }
                }
                else if ((reason & ReasonFileCreate) != 0 && !known)
                {
                    if (_index.TryGetIndex(parentFrn, out int parentIdx))
                    {
                        string name = Encoding.Unicode.GetString(nameSpan);
                        idx = _index.Add(frn, name, parentIdx, attrs, 0, timestamp, timestamp);
                        known = true;
                        applied++;
                    }
                }

                if (known)
                {
                    if ((reason & ReasonBasicInfoChange) != 0) { _index.UpdateAttributes(idx, attrs); applied++; }
                    if ((reason & ReasonClose) != 0 && (reason & ReasonsNeedingStat) != 0 && (attrs & FileAttributes.Directory) == 0)
                        pendingStat.Add(idx);
                }
            }

            // Refresh sizes/dates for closed files. One stat per touched file; cheap relative to the write itself.
            foreach (int i in pendingStat.Distinct())
            {
                if (!_index.IsLive(i)) continue;
                string path = _index.GetFullPath(i);
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists)
                    {
                        _index.Update(i, fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc(), fi.CreationTimeUtc.ToFileTimeUtc(), fi.Attributes);
                        applied++;
                    }
                }
                catch (Exception) { /* transient: file vanished or access denied */ }
            }
        }
        finally
        {
            _index.Lock.ExitWriteLock();
        }
        return applied;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(3000);
        _cts.Dispose();
    }
}
