using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>A folder for the items one group 11 test really moves: <c>%USERPROFILE%\LeanUiMove-&lt;name&gt;-&lt;id&gt;</c>, so
/// every item inside is in the Map module's root (a) (docs/spec/02 Windows note: inside the profile, at least 2 components
/// below it, outside <c>AppData</c>). Items the test moved stay in the Recycle Bin or are put back by the test; the bin
/// is never emptied. On dispose the folder (with what was put back) is removed.</summary>
internal sealed partial class MoveFixture : IDisposable
{
    public MoveFixture(string name)
    {
        var profile = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Path = System.IO.Path.Combine(profile, "LeanUiMove-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string relative) => System.IO.Path.Combine([Path, .. relative.Split('\\')]);

    /// <summary>A file of <paramref name="length"/> bytes in a pattern chosen by <paramref name="seed"/> (equal seeds and
    /// lengths give equal bytes), parents created. Lengths in whole 4 KB clusters keep the allocated size equal to the
    /// length, so every size the app may show for it (allocated or logical) is one number.</summary>
    public string File(string relative, long length, int seed)
    {
        var path = Sub(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var block = new byte[1 << 20];
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(block.Length, length - written);
            for (var i = 0; i < count; i++)
            {
                block[i] = (byte)(((written + i) * 31 + seed * 131 + 7) & 0xFF);
            }

            stream.Write(block, 0, count);
            written += count;
        }

        return path;
    }

    /// <summary>The file at <paramref name="relative"/> holding exactly <paramref name="content"/>, parents created.</summary>
    public string FileWithBytes(string relative, byte[] content)
    {
        var path = Sub(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Sets the last-write time of <paramref name="relative"/> (UTC).</summary>
    public void SetModified(string relative, DateTime utc) => System.IO.File.SetLastWriteTimeUtc(Sub(relative), utc);

    /// <summary>Makes <paramref name="newRelative"/> a hard link of <paramref name="existingRelative"/>.</summary>
    public string HardLink(string newRelative, string existingRelative)
    {
        var newPath = Sub(newRelative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newPath)!);
        if (!CreateHardLinkW(newPath, Sub(existingRelative), nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateHardLink " + newPath);
        }

        return newPath;
    }

    /// <summary>FILE_STANDARD_INFO.AllocationSize of the file at <paramref name="path"/>.</summary>
    public static long AllocatedBytes(string path)
    {
        using var handle = Open(path);
        Span<byte> info = stackalloc byte[24];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, (nint)p, (uint)info.Length))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_STANDARD_INFO " + path);
                }
            }
        }

        return BitConverter.ToInt64(info[..8]);
    }

    /// <summary>The volume serial and 128-bit file id of the file or folder at <paramref name="path"/> (FILE_ID_INFO): a
    /// move keeps them, a copy does not.</summary>
    public static (ulong Volume, UInt128 FileId) Identity(string path)
    {
        using var handle = Open(path);
        Span<byte> info = stackalloc byte[24];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, (nint)p, (uint)info.Length))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_ID_INFO " + path);
                }
            }
        }

        return (BitConverter.ToUInt64(info[..8]), MemoryMarshal.Read<UInt128>(info[8..]));
    }

    /// <summary>The SHA-256 of the file's bytes, as hex.</summary>
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes, 7, nint.Zero, 3, FlagBackupSemantics | FlagOpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "CreateFile " + path);
        }

        return handle;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private const int FileStandardInfoClass = 1;
    private const int FileIdInfoClass = 18;
    private const uint FileReadAttributes = 0x80;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass, nint fileInformation, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFileName, string existingFileName, nint securityAttributes);
}
