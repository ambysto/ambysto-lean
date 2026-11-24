using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>A cache folder as Lean checks and measures it: a real folder, never a link, measured with Lean's own walker.</summary>
internal static class CacheFolder
{
    /// <summary>True when <paramref name="path"/> is a folder and not a reparse point (a link would put the size elsewhere).</summary>
    internal static bool IsPlainFolder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when nothing is at <paramref name="path"/> any more.</summary>
    internal static bool IsGone(string path)
    {
        try
        {
            File.GetAttributes(path);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // something is there that cannot be read: not gone
        }
    }

    /// <summary>The bytes the folder takes on the disk (the walker's allocated size of its root).</summary>
    internal static long Measure(string folder, CancellationToken cancellationToken) =>
        Task.Run(() => new Scanner().ScanAsync(folder, null, cancellationToken), cancellationToken).GetAwaiter().GetResult().Root.AllocatedBytes;

    /// <summary>True when a process whose image file name is one of <paramref name="images"/> runs, judged on the full image
    /// paths given.</summary>
    internal static bool AnyRunning(System.Collections.Generic.IReadOnlyList<string> images, System.Collections.Generic.IReadOnlyList<string> processImages)
    {
        foreach (var image in processImages)
        {
            var name = Path.GetFileName(image);
            foreach (var busy in images)
            {
                if (string.Equals(name, busy, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
