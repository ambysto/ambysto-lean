using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ambysto.Lean.Core.Measure;

/// <summary>The outcome of <see cref="HardLinks.Resolve"/>: whether every group was settled, and how many files were opened.</summary>
internal readonly record struct HardLinkResult(bool Completed, int FilesOpened);

/// <summary>Walk rule 4: a file id seen twice is one file. Its size stays at the path that sorts first (ordinal, UTF-16
/// code units); every other link gets size 0 and a mark.
/// Each name has its own folder entry, and NTFS refreshes only the entry of the name a file was written through. So the
/// entries of the names of one file are compared (size, allocated size, last write time): when they agree they are trusted
/// and nothing is opened; when they disagree that one file is opened once, for attributes only (never for data, and a
/// cloud file never), and its own answer is used for every name (docs/spec/01 Windows note 4).</summary>
internal static class HardLinks
{
    private const int CheckEvery = 8192;

    public static unsafe HardLinkResult Resolve(List<ScanNode?> nodes, long fileCount, CancellationToken token)
    {
        // Keep only positions in the caller's node table. A dictionary entry for every file costs far more
        // than two integer buffers; stable byte passes preserve the original first-probe order within an id.
        if (token.IsCancellationRequested)
        {
            return new HardLinkResult(false, 0);
        }

        if (nodes.Count == 0)
        {
            return new HardLinkResult(true, 0);
        }

        var indices = (int*)NativeMemory.Alloc((nuint)nodes.Count, sizeof(int));
        try
        {
            var count = 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (i % CheckEvery == 0 && token.IsCancellationRequested)
                {
                    return new HardLinkResult(false, 0);
                }

                var node = nodes[i]!;
                if (!node.IsFolder && (node.Flags & (NodeMarks.Link | NodeMarks.CloudNotDownloaded | NodeMarks.Unreadable)) == 0
                    && node.FileId != 0)
                {
                    indices[count++] = i;
                }
            }

            if (!SortById(nodes, indices, count, token))
            {
                return new HardLinkResult(false, 0);
            }

            var opened = 0;
            for (var start = 0; start < count;)
            {
                if (token.IsCancellationRequested)
                {
                    return new HardLinkResult(false, opened);
                }

                var id = nodes[indices[start]]!.FileId;
                var end = start + 1;
                while (end < count && nodes[indices[end]]!.FileId == id)
                {
                    if ((end - start) % CheckEvery == 0 && token.IsCancellationRequested)
                    {
                        return new HardLinkResult(false, opened);
                    }

                    end++;
                }

                if (end - start == 1)
                {
                    start = end;
                    continue;
                }

                var group = new List<ScanNode>(end - start);
                for (var i = start; i < end; i++)
                {
                    if ((i - start) % CheckEvery == 0 && token.IsCancellationRequested)
                    {
                        return new HardLinkResult(false, opened);
                    }

                    group.Add(nodes[indices[i]]!);
                }

                start = end;
                if (Disagree(group) && Reread(group[0], out var answer))
                {
                    opened++;
                    foreach (var link in group)
                    {
                        link.AllocatedBytes = answer.Allocated;
                        link.LogicalBytes = answer.EndOfFile;
                        link.LastWriteMinutes = ScanRun.ToMinutes(answer.LastWrite);
                        link.Stamp = link.Stamp is { } own ? own with { LastWriteTimeUtcTicks = answer.LastWrite, Length = answer.EndOfFile } : null;
                    }
                }

                var owner = group[0];
                var ownerPath = owner.Path;
                for (var i = 1; i < group.Count; i++)
                {
                    var path = group[i].Path;
                    if (string.CompareOrdinal(path, ownerPath) < 0)
                    {
                        owner = group[i];
                        ownerPath = path;
                    }
                }

                foreach (var link in group)
                {
                    if (!ReferenceEquals(link, owner))
                    {
                        link.AllocatedBytes = 0;
                        link.LogicalBytes = 0;
                        link.Flags |= NodeMarks.HardLinkCountedElsewhere;
                    }
                }
            }

            return new HardLinkResult(true, opened);
        }
        finally
        {
            NativeMemory.Free(indices);
        }
    }

    private static unsafe bool SortById(List<ScanNode?> nodes, int* indices, int count, CancellationToken token)
    {
        if (count < 2)
        {
            return !token.IsCancellationRequested;
        }

        var scratch = (int*)NativeMemory.Alloc((nuint)count, sizeof(int));
        try
        {
            var source = indices;
            var destination = scratch;
            Span<int> offsets = stackalloc int[256];
            for (var shift = 0; shift < 128; shift += 8)
            {
                offsets.Clear();
                for (var i = 0; i < count; i++)
                {
                    if (i % CheckEvery == 0 && token.IsCancellationRequested)
                    {
                        return false;
                    }

                    offsets[(byte)(nodes[source[i]]!.FileId >> shift)]++;
                }

                var next = 0;
                for (var bucket = 0; bucket < offsets.Length; bucket++)
                {
                    var size = offsets[bucket];
                    offsets[bucket] = next;
                    next += size;
                }

                for (var i = 0; i < count; i++)
                {
                    if (i % CheckEvery == 0 && token.IsCancellationRequested)
                    {
                        return false;
                    }

                    var index = source[i];
                    var bucket = (byte)(nodes[index]!.FileId >> shift);
                    destination[offsets[bucket]++] = index;
                }

                var swapped = source;
                source = destination;
                destination = swapped;
            }

            return !token.IsCancellationRequested;
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }

    private readonly record struct FileAnswer(long Allocated, long EndOfFile, long LastWrite);

    // True when the folder entries of the names do not all say the same.
    private static bool Disagree(List<ScanNode> group)
    {
        var one = group[0];
        var oneStamp = one.Stamp!.Value;
        for (var i = 1; i < group.Count; i++)
        {
            var other = group[i];
            var otherStamp = other.Stamp!.Value;
            if (other.AllocatedBytes != one.AllocatedBytes || other.LogicalBytes != one.LogicalBytes
                || otherStamp.LastWriteTimeUtcTicks != oneStamp.LastWriteTimeUtcTicks || otherStamp.Length != oneStamp.Length)
            {
                return true;
            }
        }

        return false;
    }

    // The file's own numbers; false when it cannot be opened (the listed numbers then stay: a stale stamp fails closed
    // at move time, "changed since the scan").
    private static unsafe bool Reread(ScanNode node, out FileAnswer answer)
    {
        answer = default;
        using var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(node.Path), MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        var buffer = stackalloc byte[40];
        if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileStandardInfoClass, buffer, 24))
        {
            return false;
        }

        var allocated = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(buffer);
        var endOfFile = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(buffer + 8);
        if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileBasicInfoClass, buffer, 40))
        {
            return false;
        }

        answer = new FileAnswer(allocated, endOfFile, System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(buffer + 16));
        return true;
    }
}
