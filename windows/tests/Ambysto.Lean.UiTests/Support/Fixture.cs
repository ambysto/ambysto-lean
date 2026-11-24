using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>A scratch folder for one test's files, under <c>AMBYSTO_TESTDATA</c> when set, otherwise <c>%TEMP%</c>;
/// removed on dispose.</summary>
internal sealed partial class Fixture : IDisposable
{
    public Fixture(string name)
        : this(name, DefaultBaseFolder())
    {
    }

    /// <summary>A scratch folder under <paramref name="baseFolder"/> instead of the default one.</summary>
    public Fixture(string name, string baseFolder)
    {
        var parent = System.IO.Path.Combine(baseFolder, "LeanUiTests");
        RemoveStale(parent, name);
        Path = System.IO.Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    // ASP-22: a run that was killed (a gate timeout, a crashed test host) never reached Dispose and left its folder behind (ui-a11y-04e53c25).
    // The next fixture of the same name removes such folders, when they are more than two hours old (no run lasts that long, and a
    // test running now owns a younger one). Best effort: a folder that cannot be removed stays and is tried again next time.
    private static void RemoveStale(string parent, string name)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(parent, name + "-*"))
            {
                var leaf = System.IO.Path.GetFileName(folder) ?? string.Empty;
                var suffix = leaf.Length > name.Length + 1 ? leaf[(name.Length + 1)..] : string.Empty;
                if (suffix.Length == 8 && suffix.All(Uri.IsHexDigit) && Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddHours(-2))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next run.
        }
    }

    private static string DefaultBaseFolder()
    {
        var baseFolder = Environment.GetEnvironmentVariable("AMBYSTO_TESTDATA");
        return string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder) ? System.IO.Path.GetTempPath() : baseFolder;
    }

    /// <summary>A base folder inside the user's profile, at least one component below it and outside <c>AppData</c>, so
    /// that the items of a fixture under it are in the allowed root (docs/spec/02 (a)) and in the duplicates' home-item
    /// root (docs/spec/03): <c>AMBYSTO_TESTDATA</c> when it is such a folder, otherwise <c>%USERPROFILE%\AmbyTestData</c>.</summary>
    public static string ProfileBaseFolder()
    {
        var profile = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var appData = System.IO.Path.Combine(profile, "AppData");
        var configured = Environment.GetEnvironmentVariable("AMBYSTO_TESTDATA");
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
        {
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(configured));
            if (full.StartsWith(profile + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(appData, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(appData + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        var fallback = System.IO.Path.Combine(profile, "AmbyTestData");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>The file at <paramref name="relative"/> holding exactly <paramref name="content"/>, parents created.</summary>
    public string FileWithBytes(string relative, byte[] content)
    {
        var path = Sub(relative.Split('\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A sparse file of logical length <paramref name="length"/> with no data written (FSCTL_SET_SPARSE, then the
    /// length): its allocated size stays near zero, whatever its length.</summary>
    public string SparseFile(string relative, long length)
    {
        var path = Sub(relative.Split('\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "FSCTL_SET_SPARSE " + path);
        }

        stream.SetLength(length);
        return path;
    }

    /// <summary>Sets the last-write time of <paramref name="relative"/> (UTC).</summary>
    public void SetModified(string relative, DateTime utc) => System.IO.File.SetLastWriteTimeUtc(Sub(relative.Split('\\')), utc);

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>A file of <paramref name="length"/> bytes (zeros) at <paramref name="relative"/>, parents created.</summary>
    public string File(string relative, long length)
    {
        var path = Sub(relative.Split('\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(0);
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(buffer.Length, length - written);
            stream.Write(buffer, 0, count);
            written += count;
        }

        return path;
    }

    /// <summary><paramref name="count"/> empty files in the folder <paramref name="relative"/>.</summary>
    public string ManyEmptyFiles(string relative, int count)
    {
        var folder = Sub(relative);
        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
        {
            using var stream = new FileStream(System.IO.Path.Combine(folder, i.ToString("D6", CultureInfo.InvariantCulture) + ".dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
        }

        return folder;
    }

    /// <summary>Makes <paramref name="newRelative"/> a hard link of <paramref name="existingRelative"/>.</summary>
    public string HardLink(string newRelative, string existingRelative)
    {
        var newPath = Sub(newRelative.Split('\\'));
        if (!CreateHardLinkW(newPath, Sub(existingRelative.Split('\\')), nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateHardLink " + newPath);
        }

        return newPath;
    }

    // ASP-22: the folder is always removed. A handle that stays open for a few seconds (the app that scanned it still shutting down, Windows
    // Defender or the indexer reading a file that was just written) used to leave it behind after five tries in one second, with no word.
    // Now it is tried for 20 seconds, read-only files are made writable on the way, and a folder that still stays is named in the output.
    public void Dispose()
    {
        for (var attempt = 0; attempt < 80 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                ClearReadOnly();
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                ClearReadOnly();
                Thread.Sleep(250);
            }
        }

        if (Directory.Exists(Path))
        {
            Console.WriteLine("fixture folder could not be removed and is left behind: " + Path);
        }
    }

    private void ClearReadOnly()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                if ((System.IO.File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                {
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next try says what stays.
        }
    }

    /// <summary>The allocated size of the file at <paramref name="relative"/>: FILE_STANDARD_INFO.AllocationSize, read by the
    /// test through its own handle (the oracle for what a tile shows, docs/spec/01 walk rule 3).</summary>
    public long AllocatedBytes(string relative)
    {
        using var stream = new FileStream(Sub(relative.Split('\\')), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> info = stackalloc byte[32];
        unsafe
        {
            fixed (byte* p = info)
            {
                if (!GetFileInformationByHandleEx(stream.SafeFileHandle, FileStandardInfoClass, (nint)p, (uint)info.Length))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_STANDARD_INFO " + relative);
                }
            }
        }

        return BitConverter.ToInt64(info[..8]);
    }

    private const int FileStandardInfoClass = 1;
    private const uint FsctlSetSparse = 0x000900C4;

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass, nint fileInformation, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFileName, string existingFileName, nint securityAttributes);
}

/// <summary>Keystrokes to the focused window (the app's, after SetFocus on one of its elements).</summary>
internal static partial class Keys
{
    public const byte Tab = 0x09;
    public const byte Enter = 0x0D;
    public const byte Shift = 0x10;
    public const byte Alt = 0x12;
    public const byte Escape = 0x1B;
    public const byte Space = 0x20;
    public const byte Left = 0x25;
    public const byte Up = 0x26;
    public const byte Right = 0x27;
    public const byte Down = 0x28;
    public const byte F10 = 0x79;
    private const uint KeyUp = 0x0002;

    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, nuint.Zero);
        keybd_event(key, 0, KeyUp, nuint.Zero);
        Thread.Sleep(150);
    }

    public static void PressWithShift(byte key)
    {
        keybd_event(Shift, 0, 0, nuint.Zero);
        keybd_event(key, 0, 0, nuint.Zero);
        keybd_event(key, 0, KeyUp, nuint.Zero);
        keybd_event(Shift, 0, KeyUp, nuint.Zero);
        Thread.Sleep(300);
    }

    /// <summary>Alt held while <paramref name="key"/> is pressed (Alt+Left: the keyboard's back).</summary>
    public static void PressWithAlt(byte key)
    {
        keybd_event(Alt, 0, 0, nuint.Zero);
        keybd_event(key, 0, 0, nuint.Zero);
        keybd_event(key, 0, KeyUp, nuint.Zero);
        keybd_event(Alt, 0, KeyUp, nuint.Zero);
        Thread.Sleep(300);
    }

    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
