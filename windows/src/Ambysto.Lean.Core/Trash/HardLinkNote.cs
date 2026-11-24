using System;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Trash;

/// <summary>docs/spec/02: a file with more than one hard link is noted "Frees nothing while other links exist". The
/// confirmation asks each file item once, by opening it for attributes only (never a folder, a link or a cloud file).
/// The note informs; it never decides a move.</summary>
public static class HardLinkNote
{
    /// <summary>The note for <paramref name="node"/>, or null when it has one link, is not a plain file, or its link count
    /// cannot be read.</summary>
    public static string? For(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsFolder || (node.Flags & (NodeMarks.CloudNotDownloaded | NodeMarks.Link | NodeMarks.Unreadable)) != 0)
        {
            return null;
        }

        return LinkCount(node.Path) is > 1 ? Copy.Text.FreesNothingWhileLinked : null;
    }

    // FILE_STANDARD_INFO.NumberOfLinks (offset 16), from a handle opened for attributes only.
    internal static unsafe uint? LinkCount(string path)
    {
        using var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(path), MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = stackalloc byte[24];
        return MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileStandardInfoClass, buffer, 24)
            ? System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(buffer + 16)
            : null;
    }
}
