using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Find;

/// <summary>docs/spec/03 (Duplicates): the minimum size, 1 MB by default, adjustable.</summary>
public sealed record DuplicateOptions(long MinimumBytes)
{
    /// <summary>At least 1 MB (binary, owner decision Q3 = A).</summary>
    public static DuplicateOptions Default { get; } = new(ByteUnits.MB);
}

/// <summary>One copy in a group. <see cref="MayShareStorage"/> labels it "May share storage; moving it may free less".</summary>
public sealed record DuplicateCopy(ScanNode Node, bool MayShareStorage)
{
    /// <summary>Set for a copy Lean may not move (docs/spec/02: refused at selection, for example "Protected by you"): it
    /// stays in its group, display-only, and is not counted in the reclaimable estimate.</summary>
    public string? Hint { get; init; }
}

/// <summary>Byte-for-byte equal files. <see cref="SuggestedKeep"/> is a badge only (newest modification; ties: shortest
/// path; then alphabetical). <see cref="ReclaimableEstimateBytes"/> is an estimate and labelled so.</summary>
public sealed record DuplicateGroup(long LogicalBytes, IReadOnlyList<DuplicateCopy> Copies, DuplicateCopy SuggestedKeep, long ReclaimableEstimateBytes);

/// <summary>The duplicates finder of docs/spec/03: candidates from the home-item root under its exclusions; grouped by
/// length, then the first and last 4 KB, then every byte in 1 MB chunks. A hash or a size is never proof; a read error
/// means "not identical". Never opens a cloud file; reads file data only to compare.</summary>
public static class DuplicateFinder
{
    private const int EndBlockBytes = 4 * 1024;
    private const int ChunkBytes = 1024 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint FlagOpenNoRecall = 0x00100000;
    private const uint FlagSequentialScan = 0x08000000;
    private const uint FileSupportsBlockRefcounting = 0x08000000;

    /// <summary>The groups of identical files in <paramref name="tree"/>, largest first.</summary>
    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(ScanTree tree, DuplicateOptions options, PathContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return Task.Run<IReadOnlyList<DuplicateGroup>>(() => Find(tree, options, context, cancellationToken), cancellationToken);
    }

    private static List<DuplicateGroup> Find(ScanTree tree, DuplicateOptions options, PathContext context, CancellationToken cancellationToken)
    {
        var folders = CanonicalContext.For(context);
        if (folders.Profile is null)
        {
            return [];
        }

        // Step 1: candidates, grouped by logical length.
        var bySize = new Dictionary<long, List<Candidate>>();
        foreach (var candidate in Candidates(tree.Root, folders, options.MinimumBytes, cancellationToken))
        {
            if (!bySize.TryGetValue(candidate.Node.LogicalBytes, out var list))
            {
                list = [];
                bySize[candidate.Node.LogicalBytes] = list;
            }

            list.Add(candidate);
        }

        var groups = new List<DuplicateGroup>();
        var sharing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // At most MaxOpen buffers of 1 MB for the whole search, made on first use and reused by every compare (measured
        // 2026-10-17 over 2,000 identical pairs: the search alone stays near 48 MB private, before and after this change).
        var buffers = new byte[MaxOpen][];
        foreach (var (length, sameSize) in bySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var distinct = DistinctFiles(sameSize);
            if (distinct.Count < 2)
            {
                continue;
            }

            // Step 2: the first and the last 4 KB. An equal key only makes a set to compare in full; it proves nothing.
            var byEnds = new Dictionary<EndsKey, List<Candidate>>();
            foreach (var candidate in distinct)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadEnds(candidate, length) is not { } ends)
                {
                    continue; // a read error means "not identical": the file joins no group
                }

                if (!byEnds.TryGetValue(ends, out var set))
                {
                    set = [];
                    byEnds[ends] = set;
                }

                set.Add(candidate);
            }

            // Step 3: every byte, in 1 MB chunks.
            foreach (var set in byEnds.Values)
            {
                if (set.Count < 2)
                {
                    continue;
                }

                foreach (var identical in IdenticalSets(set, length, buffers, cancellationToken))
                {
                    groups.Add(MakeGroup(length, identical, sharing, context));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        groups.Sort((a, b) =>
        {
            var bySizeDescending = b.LogicalBytes.CompareTo(a.LogicalBytes);
            return bySizeDescending != 0 ? bySizeDescending : string.Compare(a.Copies[0].Node.Path, b.Copies[0].Node.Path, StringComparison.OrdinalIgnoreCase);
        });
        return groups;
    }

    private readonly record struct Candidate(ScanNode Node, string Path);

    // Regular files in the home-item root (docs/spec/02: inside the profile, at least 2 components below it, not inside
    // AppData), at least the minimum size, outside hidden folders (a leading dot or the hidden attribute) and
    // node_modules, never a cloud file, a link, an unreadable entry or a hard link counted at another path.
    private static IEnumerable<Candidate> Candidates(ScanNode root, CanonicalContext folders, long minimumBytes, CancellationToken cancellationToken)
    {
        var pending = new Stack<ScanNode>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0)
        {
            if ((++visited & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = pending.Pop();
            if (node.IsFolder)
            {
                if (node != root && IsExcludedFolder(node, folders.Profile!))
                {
                    continue;
                }

                foreach (var child in node.Children)
                {
                    pending.Push(child);
                }

                continue;
            }

            const NodeMarks never = NodeMarks.Link | NodeMarks.CloudNotDownloaded | NodeMarks.Unreadable | NodeMarks.HardLinkCountedElsewhere;
            if ((node.Flags & never) != 0 || node.LogicalBytes < minimumBytes || node.LogicalBytes <= 0)
            {
                continue;
            }

            var path = node.Path;
            if (CanonicalPath.Depth(folders.Profile!, path) < 2 || folders.InAppData(path))
            {
                continue;
            }

            yield return new Candidate(node, path);
        }
    }

    // A folder below the profile whose name starts with "." or is node_modules, or that has the hidden attribute: it and
    // everything in it are excluded. Folders above the profile are never excluded (no candidate lies outside it anyway).
    private static bool IsExcludedFolder(ScanNode folder, string profile)
    {
        var name = folder.Name;
        var suspicious = name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
            || (folder.Flags & NodeMarks.Hidden) != 0;
        return suspicious && CanonicalPath.Depth(profile, folder.Path) >= 1;
    }

    // Two names of one file (a hard link) are one file, never duplicates of each other: the walk counts the extra names
    // elsewhere, and a file id seen twice here is kept once (the name that sorts first).
    private static List<Candidate> DistinctFiles(List<Candidate> sameSize)
    {
        sameSize.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var seen = new HashSet<(ulong, UInt128)>();
        var result = new List<Candidate>(sameSize.Count);
        foreach (var candidate in sameSize)
        {
            if (candidate.Node.Stamp is { } stamp && stamp.FileId != UInt128.Zero && !seen.Add((stamp.VolumeSerial, stamp.FileId)))
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    // The sets of byte-for-byte equal files among candidates whose ends match, compared in lockstep: every member is read
    // once, chunk by chunk, and a set splits as soon as its members' bytes differ, so every member of a group was compared
    // with every other on the same reading of each chunk. A file that cannot be read in full, or whose length or identity
    // changed since the scan, is "not identical" and leaves its set. Sets larger than MaxOpen are compared in batches
    // (copies in different batches are never grouped: a missed group, never a wrong one).
    private const int MaxOpen = 32;

    private static IEnumerable<List<Candidate>> IdenticalSets(List<Candidate> set, long length, byte[][] buffers, CancellationToken cancellationToken)
    {
        for (var start = 0; start < set.Count; start += MaxOpen)
        {
            var batch = set.GetRange(start, Math.Min(MaxOpen, set.Count - start));
            if (batch.Count < 2)
            {
                continue;
            }

            foreach (var identical in Lockstep(batch, length, buffers, cancellationToken))
            {
                yield return identical;
            }
        }
    }

    private sealed class Member(Candidate candidate, SafeFileHandle handle, byte[] buffer) : IDisposable
    {
        public Candidate Candidate { get; } = candidate;

        public SafeFileHandle Handle { get; } = handle;

        public byte[] Buffer { get; } = buffer;

        public void Dispose() => Handle.Dispose();
    }

    private static List<List<Candidate>> Lockstep(List<Candidate> batch, long length, byte[][] buffers, CancellationToken cancellationToken)
    {
        var all = new List<Member>();
        try
        {
            foreach (var candidate in batch)
            {
                if (OpenForRead(candidate, length) is { } handle)
                {
                    var slot = all.Count; // at most MaxOpen members per batch, one buffer each
                    all.Add(new Member(candidate, handle, buffers[slot] ??= new byte[ChunkBytes]));
                }
            }

            var classes = new List<List<Member>> { new(all) };
            for (long offset = 0; offset < length && classes.Count > 0; offset += ChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkBytes, length - offset);
                var next = new List<List<Member>>();
                foreach (var members in classes)
                {
                    var readable = members.Where(m => ReadExactly(m.Handle, m.Buffer.AsSpan(0, count), offset)).ToList();
                    while (readable.Count >= 2)
                    {
                        var reference = readable[0];
                        var same = readable.Where(m => m.Buffer.AsSpan(0, count).SequenceEqual(reference.Buffer.AsSpan(0, count))).ToList();
                        if (same.Count >= 2)
                        {
                            next.Add(same);
                        }

                        readable.RemoveAll(same.Contains);
                    }
                }

                classes = next;
            }

            return classes.Select(c => c.Select(m => m.Candidate).ToList()).ToList();
        }
        finally
        {
            foreach (var member in all)
            {
                member.Dispose();
            }
        }
    }

    private static EndsKey? ReadEnds(Candidate candidate, long length)
    {
        using var handle = OpenForRead(candidate, length);
        if (handle is null)
        {
            return null;
        }

        var headLength = (int)Math.Min(EndBlockBytes, length);
        var head = new byte[headLength];
        if (!ReadExactly(handle, head, 0))
        {
            return null;
        }

        var tailLength = (int)Math.Min(EndBlockBytes, length);
        var tail = new byte[tailLength];
        return ReadExactly(handle, tail, length - tailLength) ? new EndsKey(head, tail) : null;
    }

    // Opens a file to read its data, only to compare it. A cloud file (recall attributes, read by name, before any open) is
    // never opened, and the handle cannot recall data either (FILE_FLAG_OPEN_NO_RECALL). Null when the file cannot be
    // opened, its length is no longer the scanned one, or (when the scan read its identity) it is not the scanned file:
    // a link or another file put at the path since the scan is never read in its place.
    private static unsafe SafeFileHandle? OpenForRead(Candidate candidate, long expectedLength)
    {
        var path = candidate.Path;
        var attributes = RulesNative.GetFileAttributes(MeasureNative.ToExtendedPath(path));
        if (attributes == RulesNative.InvalidFileAttributes
            || (attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | MeasureNative.AttributeDirectory)) != 0)
        {
            return null;
        }

        var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(path), GenericRead, MeasureNative.ShareAll, 0,
            MeasureNative.OpenExisting, FlagOpenNoRecall | FlagSequentialScan, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            var sameFile = true;
            if (candidate.Node.Stamp is { } stamp)
            {
                var id = stackalloc byte[24];
                sameFile = MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileIdInfoClass, id, 24)
                    && System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(id) == stamp.VolumeSerial
                    && new UInt128(System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(id + 16), System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(id + 8)) == stamp.FileId;
            }

            if (sameFile && RandomAccess.GetLength(handle) == expectedLength)
            {
                return handle;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        handle.Dispose();
        return null;
    }

    private static bool ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        try
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer[done..], offset + done);
                if (read <= 0)
                {
                    return false; // shorter than scanned: changed since, not identical
                }

                done += read;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DuplicateGroup MakeGroup(long length, List<Candidate> identical, Dictionary<string, bool> sharing, PathContext context)
    {
        identical.Sort((a, b) =>
        {
            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.Path, b.Path);
        });
        var copies = identical.Select(c => new DuplicateCopy(c.Node, MayShareStorage(c.Path, sharing)) { Hint = DisplayOnly.HintFor(c.Path, context) }).ToList();

        // Suggested keep, a badge only: newest modification; ties: shortest path; then alphabetical.
        var keep = copies[0];
        foreach (var copy in copies.Skip(1))
        {
            if (IsBetterKeep(copy, keep))
            {
                keep = copy;
            }
        }

        long reclaimable = 0;
        foreach (var copy in copies)
        {
            // A display-only copy cannot be moved, so it frees nothing.
            if (!ReferenceEquals(copy, keep) && copy.Hint is null)
            {
                reclaimable += copy.Node.AllocatedBytes;
            }
        }

        return new DuplicateGroup(length, copies, keep, reclaimable);
    }

    private static bool IsBetterKeep(DuplicateCopy candidate, DuplicateCopy current)
    {
        if (candidate.Node.LastWriteMinutes != current.Node.LastWriteMinutes)
        {
            return candidate.Node.LastWriteMinutes > current.Node.LastWriteMinutes;
        }

        var a = candidate.Node.Path;
        var b = current.Node.Path;
        if (a.Length != b.Length)
        {
            return a.Length < b.Length;
        }

        var alphabetical = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return alphabetical != 0 ? alphabetical < 0 : string.CompareOrdinal(a, b) < 0;
    }

    // docs/spec/03 Windows note: every file on a volume that reports block reference counting may share storage; a volume
    // that does not report it (or cannot be asked) has none.
    private static bool MayShareStorage(string path, Dictionary<string, bool> cache)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (!root.EndsWith('\\'))
        {
            root += "\\";
        }

        if (!cache.TryGetValue(root, out var shares))
        {
            shares = FindNative.GetVolumeInformation(root, null, 0, out _, out _, out var flags, null, 0)
                && (flags & FileSupportsBlockRefcounting) != 0;
            cache[root] = shares;
        }

        return shares;
    }

    // The first and last 4 KB of a file: equal keys only make a set to compare byte by byte.
    private readonly struct EndsKey(byte[] head, byte[] tail) : IEquatable<EndsKey>
    {
        private readonly byte[] head = head;
        private readonly byte[] tail = tail;

        public bool Equals(EndsKey other) => head.AsSpan().SequenceEqual(other.head) && tail.AsSpan().SequenceEqual(other.tail);

        public override bool Equals(object? obj) => obj is EndsKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(head);
            hash.AddBytes(tail);
            return hash.ToHashCode();
        }
    }
}

/// <summary>The read-only volume query of the duplicates finder.</summary>
internal static partial class FindNative
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char[]? volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, char[]? fileSystemNameBuffer, uint fileSystemNameSize);
}

/// <summary>The ticks of one group (docs/spec/03): nothing is pre-selected, and the last unticked copy can never be
/// ticked. The confirmation lists "Keeping: &lt;path&gt;" per group from <see cref="Kept"/>.</summary>
public sealed class DuplicateSelection
{
    private readonly DuplicateGroup group;
    private readonly HashSet<DuplicateCopy> ticked = new(ReferenceEqualityComparer.Instance);

    /// <summary>A selection of <paramref name="group"/> with nothing ticked.</summary>
    public DuplicateSelection(DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        this.group = group;
    }

    /// <summary>The copies ticked to be moved.</summary>
    public IReadOnlyList<DuplicateCopy> Ticked => group.Copies.Where(ticked.Contains).ToList();

    /// <summary>The copies that stay.</summary>
    public IReadOnlyList<DuplicateCopy> Kept => group.Copies.Where(c => !ticked.Contains(c)).ToList();

    /// <summary>False when ticking <paramref name="copy"/> would tick every copy of the group, or when the copy is
    /// display-only (<see cref="DuplicateCopy.Hint"/>, docs/spec/03 Windows note: never ticked).</summary>
    public bool CanTick(DuplicateCopy copy)
    {
        if (!IsInGroup(copy) || (copy.Hint is not null && !ticked.Contains(copy)))
        {
            return false;
        }

        return ticked.Contains(copy) || ticked.Count + 1 < group.Copies.Count;
    }

    /// <summary>Ticks or unticks <paramref name="copy"/>; returns false, changing nothing, when the tick is not allowed.</summary>
    public bool Toggle(DuplicateCopy copy)
    {
        if (!IsInGroup(copy))
        {
            return false;
        }

        if (ticked.Remove(copy))
        {
            return true; // unticking is always allowed
        }

        if (copy.Hint is not null)
        {
            return false; // a display-only copy is never ticked
        }

        if (ticked.Count + 1 >= group.Copies.Count)
        {
            return false; // the last unticked copy is never ticked
        }

        ticked.Add(copy);
        return true;
    }

    /// <summary>ASP-15, select by rule: unticks every copy, then ticks every copy except <paramref name="keep"/> that
    /// <see cref="Toggle"/> allows (never a hinted copy, never the last unticked copy). Returns false, changing nothing, when
    /// <paramref name="keep"/> is not a copy of this group.</summary>
    public bool TickAllBut(DuplicateCopy? keep)
    {
        if (keep is null || !IsInGroup(keep))
        {
            return false;
        }

        ticked.Clear();
        foreach (var copy in group.Copies)
        {
            if (!ReferenceEquals(copy, keep))
            {
                Toggle(copy); // refuses a hinted copy and the last unticked copy
            }
        }

        return true;
    }

    private bool IsInGroup(DuplicateCopy copy) => copy is not null && group.Copies.Any(c => ReferenceEquals(c, copy));
}
