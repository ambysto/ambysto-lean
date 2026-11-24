using System;
using System.IO;
using System.Threading;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>A fresh scratch folder for one test: under the folder in <c>AMBYSTO_TESTDATA</c> when that is set (the
/// test machine's Defender-excluded test-data folder), otherwise under <c>%TEMP%</c>. Removed on dispose, links
/// included (a junction or symbolic link is removed, never followed).</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string name)
    {
        Path = System.IO.Path.Combine(BaseFolder, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>The folder every scratch folder of this test project lives in.</summary>
    public static string BaseFolder
    {
        get
        {
            var baseFolder = Environment.GetEnvironmentVariable("AMBYSTO_TESTDATA");
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
            {
                baseFolder = System.IO.Path.GetTempPath();
            }

            return System.IO.Path.Combine(baseFolder, "LeanCoreTests");
        }
    }

    /// <summary>The full path of the scratch folder.</summary>
    public string Path { get; }

    /// <summary>A path inside the scratch folder.</summary>
    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Creates a folder inside and returns its path.</summary>
    public string Folder(params string[] parts)
    {
        var path = Sub(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file of <paramref name="length"/> bytes with a deterministic pattern chosen by
    /// <paramref name="seed"/> (two files with the same seed and length are byte-for-byte equal).</summary>
    public string File(string relativePath, long length, int seed = 1)
    {
        var path = Sub(relativePath.Split('\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        WriteBytes(path, length, seed);
        return path;
    }

    /// <summary>Writes <paramref name="length"/> bytes of the pattern of <paramref name="seed"/> to <paramref name="path"/>.</summary>
    public static void WriteBytes(string path, long length, int seed)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(buffer.Length, length - written);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)(((written + i) * 31 + seed * 17 + 7) & 0xFF);
            }

            stream.Write(buffer, 0, count);
            written += count;
        }
    }

    public void Dispose()
    {
        // Removing a junction inside the tree completes a moment after the call returns, so the parent can be "not empty"
        // on the first try (measured 2026-10-17: emptied fixtures left behind); a few short retries clear it.
        for (var attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                ClearAttributes(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A fixture a test could not fully undo stays behind in the scratch area; it never blocks the run.
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void ClearAttributes(DirectoryInfo folder)
    {
        if (folder.LinkTarget is not null)
        {
            // A junction or symbolic link: left as it is and removed as a link; never entered.
            return;
        }

        folder.Attributes = FileAttributes.Directory;
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub)
            {
                ClearAttributes(sub);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
            }
        }
    }
}
