using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Core.Native;

internal static unsafe class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;

    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer,
        uint nInBufferSize, void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>Opens a raw volume handle such as \\.\C: (requires administrator rights).</summary>
    public static SafeFileHandle OpenVolume(char driveLetter)
    {
        var handle = CreateFileW(@"\\.\" + char.ToUpperInvariant(driveLetter) + ":", GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open volume {driveLetter}:");
        return handle;
    }

    /// <summary>Positioned read. Offset and length must be sector aligned for raw volume handles.</summary>
    public static int ReadAt(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            NativeOverlapped overlapped = default;
            overlapped.OffsetLow = (int)(offset & 0xFFFFFFFF);
            overlapped.OffsetHigh = (int)(offset >> 32);
            if (!ReadFile(handle, p, (uint)buffer.Length, out uint read, &overlapped))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_HANDLE_EOF) return 0;
                throw new Win32Exception(err, $"ReadFile failed at offset {offset}");
            }
            return (int)read;
        }
    }

    public static NTFS_VOLUME_DATA_BUFFER GetNtfsVolumeData(SafeFileHandle handle)
    {
        NTFS_VOLUME_DATA_BUFFER data;
        if (!DeviceIoControl(handle, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &data, (uint)sizeof(NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_NTFS_VOLUME_DATA failed");
        return data;
    }

    public static bool TryQueryUsnJournal(SafeFileHandle handle, out USN_JOURNAL_DATA_V0 data, out int error)
    {
        fixed (USN_JOURNAL_DATA_V0* p = &data)
        {
            if (DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, null, 0, p, (uint)sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            {
                error = 0;
                return true;
            }
            error = Marshal.GetLastWin32Error();
            return false;
        }
    }

    public static void CreateUsnJournal(SafeFileHandle handle)
    {
        CREATE_USN_JOURNAL_DATA input = new() { MaximumSize = 32 * 1024 * 1024, AllocationDelta = 4 * 1024 * 1024 };
        if (!DeviceIoControl(handle, FSCTL_CREATE_USN_JOURNAL, &input, (uint)sizeof(CREATE_USN_JOURNAL_DATA), null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_CREATE_USN_JOURNAL failed");
    }

    /// <summary>Reads a batch of USN records. Returns bytes written to the buffer (first 8 bytes are the next USN), or -1 with an error code.</summary>
    public static int ReadUsnJournal(SafeFileHandle handle, ref READ_USN_JOURNAL_DATA_V0 request, Span<byte> buffer, out int error)
    {
        fixed (READ_USN_JOURNAL_DATA_V0* req = &request)
        fixed (byte* buf = buffer)
        {
            if (DeviceIoControl(handle, FSCTL_READ_USN_JOURNAL, req, (uint)sizeof(READ_USN_JOURNAL_DATA_V0), buf, (uint)buffer.Length, out uint returned, IntPtr.Zero))
            {
                error = 0;
                return (int)returned;
            }
            error = Marshal.GetLastWin32Error();
            return -1;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NTFS_VOLUME_DATA_BUFFER
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct USN_JOURNAL_DATA_V0
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

[StructLayout(LayoutKind.Sequential)]
internal struct READ_USN_JOURNAL_DATA_V0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CREATE_USN_JOURNAL_DATA
{
    public ulong MaximumSize;
    public ulong AllocationDelta;
}
