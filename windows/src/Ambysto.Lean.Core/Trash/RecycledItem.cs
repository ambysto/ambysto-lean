using System;
using System.Collections.Generic;
using System.IO;
using Ambysto.Kit.Safety;

namespace Ambysto.Lean.Core.Trash;

/// <summary>Is the item at a path the one that was moved? Shared by the Trasher's post-verify and Put back (docs/spec/02
/// and 05 Windows notes): its volume serial and file id, read through the Kit's <see cref="FileFacts"/>, equal the
/// expected identity, and its size by <see cref="Measure"/> equals the size measured the same way just before the move.
/// One measure on both sides, so hard links, which the Kit counts per name and Lean's scanner once, cannot make an
/// intact item look lost (interim review MAJOR 1, 2026-10-17).</summary>
internal static class RecycledItem
{
    internal static bool Matches(IItemGate gate, string path, FileIdentity identity, long logicalBytes) =>
        HasIdentity(gate, path, identity) && Measure(path) == logicalBytes;

    /// <summary>The item at <paramref name="path"/>, opened for its facts, has <paramref name="identity"/>.</summary>
    internal static bool HasIdentity(IItemGate gate, string path, FileIdentity identity) =>
        gate.Facts(path).Stamp is { } stamp && stamp.Identity == identity;

    /// <summary>The sum of the real lengths of every file name in the item's tree (the file itself for a file): each
    /// name's length asked by path, so a stale folder entry of a hard link is not used; links (junctions, symbolic links,
    /// mount points) are not entered; every hard-link name counts. Null when any part cannot be read.</summary>
    internal static long? Measure(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
            {
                return Length(path);
            }

            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
            long total = 0;
            var folders = new Stack<string>();
            folders.Push(path);
            while (folders.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folders.Pop(), "*", options))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        total += Length(entry) ?? throw new IOException("length of " + entry);
                    }
                    else if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        folders.Push(entry);
                    }
                }
            }

            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // FILE_STANDARD_INFO.EndOfFile (offset 8) from a handle opened for attributes only, never following a link: the
    // file's own length, not a folder entry's copy of it.
    private static unsafe long? Length(string path)
    {
        using var handle = Ambysto.Lean.Core.Measure.MeasureNative.CreateFile(Ambysto.Lean.Core.Measure.MeasureNative.ToExtendedPath(path), Ambysto.Lean.Core.Measure.MeasureNative.FileReadAttributes, Ambysto.Lean.Core.Measure.MeasureNative.ShareAll, 0, Ambysto.Lean.Core.Measure.MeasureNative.OpenExisting, Ambysto.Lean.Core.Measure.MeasureNative.FlagBackupSemantics | Ambysto.Lean.Core.Measure.MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = stackalloc byte[24];
        return Ambysto.Lean.Core.Measure.MeasureNative.GetFileInformationByHandleEx(handle, Ambysto.Lean.Core.Measure.MeasureNative.FileStandardInfoClass, buffer, 24)
            ? System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(buffer + 8)
            : null;
    }
}
