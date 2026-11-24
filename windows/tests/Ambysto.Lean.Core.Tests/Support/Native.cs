using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>The volume serial and 128-bit file id Windows reports for one open file (FILE_ID_INFO).</summary>
internal readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId);

/// <summary>Free and total bytes of a volume as <c>GetDiskFreeSpaceEx</c> reports them to this user.</summary>
internal readonly record struct DiskSpace(long FreeBytesAvailable, long TotalBytes, long TotalFreeBytes);

/// <summary>The fixtures the tests need beyond what .NET offers: hard links, junctions, sparse files, file ids and the
/// volume numbers to compare against. Test-project only; the app never links or creates anything.</summary>
internal static partial class Native
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const int FileIdInfoClass = 18;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileReadAttributes = 0x80;

    /// <summary>Makes <paramref name="newPath"/> a second hard link of <paramref name="existingPath"/>.</summary>
    public static void CreateHardLink(string newPath, string existingPath)
    {
        if (!CreateHardLinkW(newPath, existingPath, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateHardLink " + newPath);
        }
    }

    /// <summary>Makes <paramref name="junctionPath"/> a junction (mount-point reparse point) to <paramref name="targetPath"/>.
    /// A standard user may do this.</summary>
    public static void CreateJunction(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);
        using var handle = CreateFileW(junctionPath, GenericWrite, FileShareAll, nint.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile " + junctionPath);
        }

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.TrimEndingDirectorySeparator(targetPath));
        var print = Encoding.Unicode.GetBytes(Path.TrimEndingDirectorySeparator(targetPath));
        var pathBuffer = substitute.Length + 2 + print.Length + 2;
        var reparseDataLength = 8 + pathBuffer;
        var buffer = new byte[8 + reparseDataLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), ReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)reparseDataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                if (!DeviceIoControl(handle, FsctlSetReparsePoint, (nint)p, (uint)buffer.Length, nint.Zero, 0, out _, nint.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FSCTL_SET_REPARSE_POINT " + junctionPath);
                }
            }
        }
    }

    /// <summary>Creates a sparse file whose logical length is <paramref name="length"/> and whose allocated size is
    /// (nearly) nothing.</summary>
    public static void CreateSparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "FSCTL_SET_SPARSE " + path);
        }

        stream.SetLength(length);
    }

    /// <summary>The identity of the file or folder at <paramref name="path"/>, read from an open handle (never through a
    /// link: the link itself is opened).</summary>
    public static FileIdentity IdentityOf(string path)
    {
        using var handle = CreateFileW(path, GenericRead, FileShareAll, nint.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile " + path);
        }

        Span<byte> info = stackalloc byte[24];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, (nint)p, 24))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_ID_INFO " + path);
                }
            }
        }

        return new FileIdentity(BitConverter.ToUInt64(info[..8]), MemoryMarshal.Read<UInt128>(info[8..]));
    }

    /// <summary>What <c>GetDiskFreeSpaceEx</c> reports for the volume of <paramref name="root"/>.</summary>
    public static DiskSpace DiskSpaceOf(string root)
    {
        if (!GetDiskFreeSpaceExW(root, out var available, out var total, out var totalFree))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDiskFreeSpaceEx " + root);
        }

        return new DiskSpace((long)available, (long)total, (long)totalFree);
    }

    /// <summary>The shell's answer for this user's Recycle Bin on <paramref name="root"/> (the oracle for the volume card).</summary>
    public static (int Hr, long Bytes, long Items) QueryRecycleBin(string root)
    {
        var info = new ShQueryRbInfo { Size = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
        var hr = SHQueryRecycleBinW(root, ref info);
        return (hr, info.Bytes, info.Items);
    }

    /// <summary>What File Explorer shows for <paramref name="bytes"/>: the text of <c>StrFormatByteSizeEx</c> with
    /// <c>SFBS_FLAGS_TRUNCATE_UNDISPLAYED_DECIMAL_DIGITS</c> in the Windows user locale (the oracle for ByteUnits.Format).</summary>
    public static string ExplorerSizeText(long bytes)
    {
        const int truncateUndisplayedDecimalDigits = 2;
        var buffer = new char[64];
        int result;
        unsafe
        {
            fixed (char* p = buffer)
            {
                result = StrFormatByteSizeEx((ulong)bytes, truncateUndisplayedDecimalDigits, p, (uint)buffer.Length);
            }
        }

        if (result < 0)
        {
            throw new InvalidOperationException($"StrFormatByteSizeEx({bytes}) failed: 0x{result:X8}");
        }

        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>The 8.3 (short-name) spelling of <paramref name="path"/>, or null when the volume keeps no short names
    /// for it (the spelling comes back unchanged).</summary>
    public static string? ShortPathOf(string path)
    {
        var buffer = new char[1024];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
            {
                length = GetShortPathNameW(path, p, (uint)buffer.Length);
            }
        }

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var shortPath = new string(buffer, 0, (int)length);
        return string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase) ? null : shortPath;
    }

    /// <summary>The bytes of disk storage the file at <paramref name="path"/> uses, as <c>GetCompressedFileSize</c> reports
    /// them (the compressed size of a WOF or NTFS-compressed file; the oracle for walk rule 3 and Windows note 3).</summary>
    public static long CompressedSizeOf(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == uint.MaxValue)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 0)
            {
                throw new Win32Exception(error, "GetCompressedFileSize " + path);
            }
        }

        return (long)(((ulong)high << 32) | low);
    }

    /// <summary>The cluster size of the volume of <paramref name="root"/>.</summary>
    public static long ClusterSizeOf(string root)
    {
        if (!GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDiskFreeSpace " + root);
        }

        return (long)sectorsPerCluster * bytesPerSector;
    }

    /// <summary>The attributes and reparse tag of the entry at <paramref name="path"/> itself (FILE_ATTRIBUTE_TAG_INFO,
    /// read through a handle opened for attributes only, never through a link).</summary>
    public static (FileAttributes Attributes, uint ReparseTag) AttributeTagOf(string path)
    {
        using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, nint.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile " + path);
        }

        Span<byte> info = stackalloc byte[8];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, (nint)p, 8))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_ATTRIBUTE_TAG_INFO " + path);
                }
            }
        }

        return ((FileAttributes)BitConverter.ToUInt32(info[..4]), BitConverter.ToUInt32(info[4..]));
    }

    /// <summary>The file's own allocated size and length (FILE_STANDARD_INFO), read through a handle opened for attributes
    /// only (the oracle for walk rule 4's Windows note: the numbers the file itself gives).</summary>
    public static (long Allocated, long EndOfFile) StandardInfoOf(string path)
    {
        using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, nint.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile " + path);
        }

        Span<byte> info = stackalloc byte[24];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, (nint)p, 24))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_STANDARD_INFO " + path);
                }
            }
        }

        return (BitConverter.ToInt64(info[..8]), BitConverter.ToInt64(info[8..16]));
    }

    /// <summary>The allocated size the file system keeps for the file at <paramref name="path"/> (FILE_STANDARD_INFO's
    /// AllocationSize, read from a handle opened for attributes only: no data is read). The oracle for walk rule 3.</summary>
    public static long AllocationSizeOf(string path) => StandardInfoOf(path).Allocated;

    /// <summary>The volume GUID path (<c>\\?\Volume{…}\</c>) of the volume whose root is <paramref name="root"/>, or null.</summary>
    public static string? VolumeGuidPathOf(string root)
    {
        var buffer = new char[64];
        unsafe
        {
            fixed (char* p = buffer)
            {
                if (!GetVolumeNameForVolumeMountPointW(root, p, (uint)buffer.Length))
                {
                    return null;
                }
            }
        }

        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>The file-system flags <c>GetVolumeInformation</c> reports for the volume whose root is <paramref name="root"/>,
    /// or null when it cannot be asked.</summary>
    public static uint? VolumeFlagsOf(string root) =>
        GetVolumeInformationW(root, nint.Zero, 0, out _, out _, out var flags, nint.Zero, 0) ? flags : null;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, nint fileInformation, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrFormatByteSizeEx")]
    private static unsafe partial int StrFormatByteSizeEx(ulong size, int flags, char* buffer, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial uint GetShortPathNameW(string longPath, char* shortPath, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationW(string rootPathName, nint volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, nint fileSystemNameBuffer, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);
}
