using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ambysto.Lean.App;

/// <summary>What a drive card shows (ASP-14): the drive ("C:"), its label (empty when it has none) and its space.</summary>
internal sealed record DriveFacts(string Drive, string Label, long TotalBytes, long FreeBytes);

/// <summary>The fixed drives of the start screen (ASP-14). Reads only: every drive is asked on its own pool thread, and one that does
/// not answer within the limit is left out, so the window never waits on a drive.</summary>
internal static class FixedDrives
{
    /// <summary>The fixed drives (DRIVE_FIXED: not removable, network, CD or RAM drives) that answered within
    /// <paramref name="limit"/>, in drive-letter order.</summary>
    internal static async Task<List<DriveFacts>> ReadAsync(TimeSpan limit)
    {
        string[] roots;
        try
        {
            roots = Environment.GetLogicalDrives();
        }
        catch (IOException)
        {
            return [];
        }

        var reads = roots.Select(root => ReadWithinAsync(root, limit)).ToArray();
        var facts = await Task.WhenAll(reads).ConfigureAwait(true);
        return [.. facts.OfType<DriveFacts>().OrderBy(f => f.Drive, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<DriveFacts?> ReadWithinAsync(string root, TimeSpan limit)
    {
        var read = Task.Run(() => ReadOne(root));
        var finished = await Task.WhenAny(read, Task.Delay(limit)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, read))
        {
            return null; // no answer in time; the read may still end on its own thread
        }

        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // GetDriveType, GetVolumeInformation and GetDiskFreeSpaceEx through DriveInfo; any of them can wait on a drive that is slow.
    private static DriveFacts? ReadOne(string root)
    {
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
        {
            return null;
        }

        return new DriveFacts(root.TrimEnd('\\'), drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace);
    }
}
