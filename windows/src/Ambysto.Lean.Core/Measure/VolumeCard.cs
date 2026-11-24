using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ambysto.Lean.Core.Measure;

/// <summary>The volume card of docs/spec/01 for one volume (Windows notes): used is total − free; free is what this user
/// may use; the Recycle Bin is this user's on that volume, in logical bytes.</summary>
public sealed record VolumeCardFacts(
    string VolumeRoot,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    long? RecycleBinBytes,
    long? RecycleBinItems);

/// <summary>Reads the volume card. Reads only.</summary>
public static class VolumeCard
{
    /// <summary>The card for the volume whose root is <paramref name="volumeRoot"/> (for example <c>C:\</c>). Free is
    /// <c>GetDiskFreeSpaceEx</c>'s "available to the caller"; used is total − free. The Recycle Bin numbers come from
    /// <c>SHQueryRecycleBin</c> (this user's bin on that volume, logical sizes). A volume without a bin answers 0 through
    /// the shell. When the shell cannot read the bin (an error), both are null, never 0: the size is unknown.</summary>
    public static VolumeCardFacts Read(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        var (total, free) = ReadSpace(volumeRoot);
        var bin = new RecycleBinQuery { Size = (uint)Marshal.SizeOf<RecycleBinQuery>() };
        var result = MeasureNative.SHQueryRecycleBin(volumeRoot, ref bin);
        long? binBytes = result >= 0 ? bin.Bytes : null;
        long? binItems = result >= 0 ? bin.Items : null;
        return new VolumeCardFacts(volumeRoot, total, total - free, free, binBytes, binItems);
    }

    /// <summary>Used bytes (total − free available to this user) of the volume that holds <paramref name="path"/>; what
    /// <see cref="Scanner"/> measures coverage against. Does not touch the Recycle Bin.</summary>
    internal static long UsedBytesOf(string path)
    {
        var (total, free) = ReadSpace(path);
        return total - free;
    }

    /// <summary>True when <paramref name="path"/> and <paramref name="otherPath"/> are on the same volume, after mount points: each
    /// is resolved to its volume's GUID name (<c>GetVolumePathName</c>, then <c>GetVolumeNameForVolumeMountPoint</c>), so a folder
    /// that is a mount point of another volume, in a tree on this one, is on the other volume, and a <c>\\?\</c> path is on the
    /// volume its drive letter names. Reads only; false when either volume cannot be told (a network path, an unreadable path).</summary>
    public static bool IsOnSameVolume(string path, string otherPath)
    {
        var volume = VolumeGuidOf(path);
        return volume is not null && string.Equals(volume, VolumeGuidOf(otherPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string? VolumeGuidOf(string path)
    {
        // "\\?\C:\x" asks for the same volume as "C:\x"; "\\?\UNC\..." and "\\?\Volume{...}\" are left as they are.
        var plain = path.Length >= 6 && path.StartsWith(@"\\?\", StringComparison.Ordinal) && path[5] == ':' && char.IsAsciiLetter(path[4]) ? path[4..] : path;
        var mountPoint = new char[1024];
        if (!MeasureNative.GetVolumePathName(plain, mountPoint, (uint)mountPoint.Length))
        {
            return null;
        }

        var volumePath = new string(mountPoint, 0, Array.IndexOf(mountPoint, '\0') is var end and >= 0 ? end : mountPoint.Length);
        var name = new char[64];
        return MeasureNative.GetVolumeNameForVolumeMountPoint(volumePath, name, (uint)name.Length)
            ? new string(name, 0, Array.IndexOf(name, '\0') is var nameEnd and >= 0 ? nameEnd : name.Length)
            : null;
    }

    private static (long Total, long Free) ReadSpace(string path)
    {
        if (!MeasureNative.GetDiskFreeSpaceEx(path, out var available, out var total, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDiskFreeSpaceEx " + path);
        }

        return ((long)total, (long)available);
    }
}
