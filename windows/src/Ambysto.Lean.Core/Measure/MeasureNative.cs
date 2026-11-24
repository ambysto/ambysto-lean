using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Measure;

/// <summary>What <c>SHQueryRecycleBin</c> fills in (SHQUERYRBINFO, natural layout: 24 bytes on x64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RecycleBinQuery
{
    public uint Size;
    public long Bytes;
    public long Items;
}

/// <summary>The read-only Win32 calls of the Measure module. Nothing here opens a file for data, deletes, moves or
/// changes anything: a folder is opened for listing, a volume is asked for its numbers.</summary>
internal static partial class MeasureNative
{
    // CreateFile arguments for a folder opened only to list it. FILE_FLAG_OPEN_REPARSE_POINT: a link is opened itself,
    // never followed (walk rule 2).
    public const uint FileListDirectory = 0x0001;
    public const uint FileReadAttributes = 0x0080;
    public const uint ShareAll = 0x7;
    public const uint OpenExisting = 3;
    public const uint FlagBackupSemantics = 0x02000000;
    public const uint FlagOpenReparsePoint = 0x00200000;

    // GetFileInformationByHandleEx classes.
    public const int FileBasicInfoClass = 0;
    public const int FileStandardInfoClass = 1;
    public const int FileAttributeTagInfoClass = 9;
    public const int FileIdInfoClass = 18;
    public const int FileIdExtdDirectoryInfoClass = 19;
    public const int FileIdExtdDirectoryRestartInfoClass = 20;

    // File attributes.
    public const uint AttributeHidden = 0x2;
    public const uint AttributeDirectory = 0x10;
    public const uint AttributeReparsePoint = 0x400;
    public const uint AttributeRecallOnOpen = 0x40000;
    public const uint AttributeRecallOnDataAccess = 0x400000;

    // A reparse tag with this bit is a name surrogate: symbolic link, junction, mount point (walk rule 2).
    public const uint ReparseTagNameSurrogate = 0x20000000;

    public const int ErrorNoMoreFiles = 18;

    /// <summary>The path in the form that lifts the 260-character limit.</summary>
    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", System.StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", System.StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, byte* fileInformation, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHQueryRecycleBin(string rootPath, ref RecycleBinQuery query);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrFormatByteSizeEx")]
    public static unsafe partial int StrFormatByteSizeEx(ulong size, int flags, char* buffer, uint bufferLength);
}
