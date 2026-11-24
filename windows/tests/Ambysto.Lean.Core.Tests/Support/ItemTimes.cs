using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Sets the last-write (and last-access) time of a file, a folder or a link itself, never through a link: the
/// entry is opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a junction gets its own time and its target is untouched.
/// Test-project only; for fixtures that need known ages (Temporary files).</summary>
internal static partial class ItemTimes
{
    private const uint FileWriteAttributes = 0x100;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary>Sets the own times of <paramref name="path"/> (a file, a folder or a link) to <paramref name="utc"/>.</summary>
    public static void Set(string path, DateTime utc)
    {
        using var handle = CreateFileW(path, FileWriteAttributes, FileShareAll, nint.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile " + path);
        }

        var time = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToFileTimeUtc();
        unsafe
        {
            if (!SetFileTime(handle, null, &time, &time))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetFileTime " + path);
            }
        }
    }

    /// <summary>Sets every entry at or below <paramref name="root"/> to <paramref name="utc"/>: files, folders (after their
    /// contents) and links (their own time; a link is never entered).</summary>
    public static void SetTree(string root, DateTime utc)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists)
        {
            Set(root, utc);
            return;
        }

        if (info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry is not DirectoryInfo)
                {
                    Set(entry.FullName, utc);
                }
                else
                {
                    SetTree(entry.FullName, utc);
                }
            }
        }

        Set(root, utc);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileTime", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileTime(SafeFileHandle file, long* creationTime, long* lastAccessTime, long* lastWriteTime);
}
