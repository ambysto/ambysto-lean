using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Map;

namespace Ambysto.Lean.Core.Measure;

/// <summary>One walk of <see cref="Scanner"/> (docs/spec/01, walk rules and their Windows notes; plan "Walker"). A few
/// workers take folders from a shared stack. Each folder is opened once, as a directory handle, and its entries are read
/// in batches with <c>GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)</c>: names, attributes, reparse tags,
/// logical and allocated sizes, last write times and 128-bit file ids come from the batch, so no file is ever opened.
/// A name-surrogate reparse point (symbolic link, junction, mount point) is recorded and never entered; a file or folder
/// with a recall attribute (online-only) is recorded as a cloud file and never entered or opened.</summary>
internal sealed class ScanRun : IDisposable
{
    private const int BufferBytes = 128 * 1024;
    private const int MaxWorkers = 8;
    private const long ResidentLimit = 1024;
    private const long TicksPerMinute = 600_000_000;
    private const long UnixEpochFileTime = 116_444_736_000_000_000;
    private static readonly TimeSpan FirstReport = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly string rootPath;
    private readonly IProgress<ScanProgress>? progress;
    private readonly CancellationToken userToken;
    private readonly CancellationTokenSource stop;
    private readonly object gate = new();
    private readonly Stack<Work> pendingFolders = new();
    private readonly List<TopLevel> topLevels = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int pending;
    private bool incomplete;
    private bool volumeKnown;
    private ulong volumeSerial;
    private long filesSeen;
    private long measuredBytes;
    private string? reading;
    private long unreadable;
    private long cloud;
    private long allocationAsked;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<UInt128, long> askedAllocations = new();

    // QA only (--scan-delay): how long a worker waits after each folder batch, so a test can cancel a scan mid-walk.
    private readonly TimeSpan batchDelay;

    public ScanRun(string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken, TimeSpan batchDelay = default)
    {
        this.batchDelay = batchDelay;
        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.progress = progress;
        userToken = cancellationToken;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    // One zone of the map (ASP-14): a folder directly in the root. PendingWork counts the zone's folders not yet read to the end
    // (its own, plus every subfolder found); the worker that brings it to zero has read the zone's last subfolder and publishes
    // the zone's colour, worked out from the finished subtree, which no other thread writes. Colour is written before Done
    // (a volatile write), so a reader of the snapshot that sees Done sees the Colour.
    private sealed class TopLevel(string path, string name, ScanNode node)
    {
        public readonly string Path = path;
        public readonly string Name = name;
        public readonly ScanNode Node = node;
        public long Bytes;
        public int PendingWork;
        public volatile bool Done;
        public TypeGroupKind Colour;
    }

    private sealed class Work(ScanNode folder, TopLevel? top)
    {
        public readonly ScanNode Folder = folder;
        public readonly TopLevel? Top = top;
    }

    public void Dispose() => stop.Dispose();

    public async Task<ScanTree> RunAsync()
    {
        var root = OpenRoot(out var enter);
        if (userToken.IsCancellationRequested)
        {
            incomplete = true;
        }
        else if (enter)
        {
            await WalkAsync(root).ConfigureAwait(false);
        }

        long used;
        try
        {
            used = VolumeCard.UsedBytesOf(rootPath);
        }
        catch (Win32Exception)
        {
            used = 0; // no number to compare with: the coverage then reads "measured more than used", which is honest
        }

        var tree = ScanTree.Build(root, incomplete, used, Interlocked.Read(ref unreadable), Interlocked.Read(ref cloud), Interlocked.Read(ref filesSeen), Interlocked.Read(ref allocationAsked), userToken);
        tree.Elapsed = clock.Elapsed;
        return tree;
    }

    private async Task WalkAsync(ScanNode root)
    {
        pending = 1;
        pendingFolders.Push(new Work(root, null));
        using var wake = stop.Token.Register(() =>
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        });

        using var reportStop = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : Task.Run(() => ReportLoopAsync(reportStop.Token), CancellationToken.None);
        var workers = new Task[Math.Clamp(Environment.ProcessorCount, 2, MaxWorkers)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(WorkerLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            // No report may reach the caller once ScanAsync has returned.
            await reportStop.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }

        // One last report, after the reporter has stopped and every worker has ended: every zone in its final state (done, unless
        // the walk was cancelled), so a window that draws the last report it got ends on the finished zones. Values only, as always.
        progress?.Report(Snapshot());
    }

    private async Task ReportLoopAsync(CancellationToken stopReports)
    {
        var due = FirstReport;
        try
        {
            while (!stopReports.IsCancellationRequested)
            {
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stopReports).ConfigureAwait(false);
                    continue;
                }

                progress!.Report(Snapshot());
                due = clock.Elapsed + ReportInterval;
            }
        }
        catch (OperationCanceledException)
        {
            // The walk ended; this is the normal way out.
        }
    }

    // Values only: no ScanNode leaves the walk while it runs.
    private ScanProgress Snapshot()
    {
        List<KeyValuePair<string, long>> totals;
        List<(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)> zones;
        lock (gate)
        {
            totals = new List<KeyValuePair<string, long>>(topLevels.Count);
            zones = new List<(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)>(topLevels.Count);
            foreach (var top in topLevels)
            {
                var bytes = Interlocked.Read(ref top.Bytes);
                totals.Add(new KeyValuePair<string, long>(top.Path, bytes));
                var done = top.Done;
                zones.Add((top.Path, bytes, done, done ? top.Colour : null, top.Name));
            }
        }

        return new ScanProgress(clock.Elapsed, Interlocked.Read(ref filesSeen), totals, zones, Interlocked.Read(ref measuredBytes), Volatile.Read(ref reading));
    }

    // The root, read once for its own facts. A folder that cannot be opened or is itself a link is returned unentered.
    // A file given as the root becomes a one-node tree; it is found in a listing of its folder, never opened.
    private ScanNode OpenRoot(out bool enter)
    {
        enter = false;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(rootPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            unreadable++;
            return ScanNode.Folder(rootPath, 0, null, NodeMarks.Unreadable);
        }

        return (attributes & FileAttributes.Directory) == 0 ? ReadFileRoot() : OpenFolderRoot(out enter);
    }

    private unsafe ScanNode OpenFolderRoot(out bool enter)
    {
        enter = false;
        using var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(rootPath), MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            unreadable++;
            return ScanNode.Folder(rootPath, 0, null, NodeMarks.Unreadable);
        }

        // The facts of the root itself, read from its handle into a small fixed buffer (offsets of FILE_BASIC_INFO,
        // FILE_ID_INFO and FILE_ATTRIBUTE_TAG_INFO).
        var marks = NodeMarks.None;
        var lastWrite = UnixEpochFileTime;
        var buffer = stackalloc byte[40];
        if (MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileBasicInfoClass, buffer, 40))
        {
            lastWrite = Unsafe.ReadUnaligned<long>(buffer + 16);
            marks |= (Unsafe.ReadUnaligned<uint>(buffer + 32) & MeasureNative.AttributeHidden) != 0 ? NodeMarks.Hidden : NodeMarks.None;
        }

        ItemStamp? stamp = null;
        if (MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileIdInfoClass, buffer, 24))
        {
            volumeKnown = true;
            volumeSerial = Unsafe.ReadUnaligned<ulong>(buffer);
            stamp = new ItemStamp(volumeSerial, new UInt128(Unsafe.ReadUnaligned<ulong>(buffer + 16), Unsafe.ReadUnaligned<ulong>(buffer + 8)), lastWrite, null);
        }

        uint attributes = 0;
        uint reparseTag = 0;
        var tagKnown = MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileAttributeTagInfoClass, buffer, 8);
        if (tagKnown)
        {
            attributes = Unsafe.ReadUnaligned<uint>(buffer);
            reparseTag = Unsafe.ReadUnaligned<uint>(buffer + 4);
        }

        if (!tagKnown || (attributes & MeasureNative.AttributeDirectory) == 0)
        {
            unreadable++;
            return ScanNode.Folder(rootPath, ToMinutes(lastWrite), null, marks | NodeMarks.Unreadable);
        }

        if ((attributes & MeasureNative.AttributeReparsePoint) != 0 && (reparseTag & MeasureNative.ReparseTagNameSurrogate) != 0)
        {
            return ScanNode.Folder(rootPath, ToMinutes(lastWrite), stamp, marks | NodeMarks.Link);
        }

        enter = true;
        return ScanNode.Folder(rootPath, ToMinutes(lastWrite), stamp, marks);
    }

    private unsafe ScanNode ReadFileRoot()
    {
        var parentPath = Path.GetDirectoryName(rootPath);
        if (parentPath is null)
        {
            unreadable++;
            return ScanNode.File(rootPath, 0, 0, 0, null, NodeMarks.Unreadable);
        }

        using (var parentHandle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(parentPath), MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0))
        {
            var id = stackalloc byte[24];
            if (!parentHandle.IsInvalid && MeasureNative.GetFileInformationByHandleEx(parentHandle, MeasureNative.FileIdInfoClass, id, 24))
            {
                volumeKnown = true;
                volumeSerial = Unsafe.ReadUnaligned<ulong>(id);
            }
        }

        var parent = ScanNode.Folder(parentPath);
        ReadFolder(new Work(parent, null), GC.AllocateUninitializedArray<byte>(BufferBytes, pinned: true), Path.GetFileName(rootPath));
        var found = parent.ChildList.Count == 1 ? parent.ChildList[0] : null;
        if (found is null)
        {
            if ((parent.Flags & NodeMarks.Unreadable) == 0)
            {
                unreadable++;
            }

            return ScanNode.File(rootPath, 0, 0, 0, null, NodeMarks.Unreadable);
        }

        parent.Detach(found);
        return found;
    }

    // Scan-worker-local, bounded flyweights for exact repeated entry names. Never interns globally or changes case.
    // The one-million-file fixture repeats 1,000 names across 1,000 directories; reuse avoids 992,000+ duplicate strings.
    private sealed class EntryNames
    {
        private const int Limit = 4096;
        private const int MaxLength = 128;
        private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);

        public string Get(ReadOnlySpan<char> value)
        {
            if (value.Length <= MaxLength)
            {
                var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
                if (lookup.TryGetValue(value, out var existing))
                {
                    return existing;
                }

                var created = value.ToString();
                if (names.Count < Limit)
                {
                    names.Add(created, created);
                }

                return created;
            }

            return value.ToString();
        }
    }

    private void WorkerLoop()
    {
        var names = new EntryNames();
        var buffer = GC.AllocateUninitializedArray<byte>(BufferBytes, pinned: true);
        try
        {
            while (TryTake(out var work))
            {
                try
                {
                    ReadFolder(work, buffer, null, names);
                    if (work.Top is { } zone)
                    {
                        FinishWork(zone);
                    }
                }
                finally
                {
                    lock (gate)
                    {
                        pending--;
                        if (pending == 0)
                        {
                            Monitor.PulseAll(gate);
                        }
                    }
                }
            }
        }
        catch
        {
            // An unexpected failure ends the whole walk; the caller sees the exception.
            stop.Cancel();
            throw;
        }
    }

    // One of the zone's folders has been read to the end, its subfolders queued first (so the count never reaches zero early). The
    // worker that brings the count to zero publishes the zone as done. A walk that is stopping may have dropped subfolders, so it
    // publishes nothing; a failed ReadFolder never gets here.
    private void FinishWork(TopLevel zone)
    {
        if (Interlocked.Decrement(ref zone.PendingWork) == 0 && !stop.IsCancellationRequested)
        {
            zone.Colour = ZoneColour(zone.Node);
            zone.Done = true;
        }
    }

    private struct ColourFrame(ScanNode node)
    {
        public readonly ScanNode Node = node;
        public int Next;
        public long Sum;
        public long BestBytes;
        public string? BestName;
        public ScanNode? BestLeaf;
        public bool HasBest;
    }

    // The colour of a finished zone, as TypeGroup.ColourOf gives it after the totals (the file reached by following the largest
    // child; of equal sizes the first in ScanNode's order, which is by name, ignoring case), but without writing a total into any node: one pass over the zone's subtree with an explicit
    // stack. Only the winning file's name is looked at.
    private static TypeGroupKind ZoneColour(ScanNode zone)
    {
        var stack = new ColourFrame[32];
        var depth = 0;
        stack[depth++] = new ColourFrame(zone);
        while (true)
        {
            ref var frame = ref stack[depth - 1];
            var children = frame.Node.ChildList;
            if (frame.Next < children.Count)
            {
                var child = children[frame.Next++];
                if (child.IsFolder)
                {
                    if (depth == stack.Length)
                    {
                        Array.Resize(ref stack, depth * 2);
                    }

                    stack[depth++] = new ColourFrame(child);
                }
                else
                {
                    Offer(ref frame, child.AllocatedBytes, child, child);
                }

                continue;
            }

            var sum = frame.Sum;
            var leaf = frame.HasBest ? frame.BestLeaf : null;
            var finished = frame.Node;
            depth--;
            if (depth == 0)
            {
                return leaf is null ? TypeGroupKind.Other : TypeGroup.Of(leaf.Name);
            }

            // A folder offers its total and the file its own largest child reached.
            Offer(ref stack[depth - 1], sum, leaf, finished);
        }
    }

    private static void Offer(ref ColourFrame frame, long bytes, ScanNode? leaf, ScanNode child)
    {
        frame.Sum += bytes;
        // The largest child wins; equal sizes go to the one ScanNode.LargestFirst puts first (by name), as the final map's ColourOf does.
        if (!frame.HasBest || bytes > frame.BestBytes
            || (bytes == frame.BestBytes && string.Compare(child.Name, frame.BestName, StringComparison.OrdinalIgnoreCase) < 0))
        {
            frame.HasBest = true;
            frame.BestBytes = bytes;
            frame.BestName = child.Name;
            frame.BestLeaf = leaf;
        }
    }

    private bool TryTake(out Work work)
    {
        lock (gate)
        {
            while (true)
            {
                if (stop.IsCancellationRequested)
                {
                    work = null!;
                    if (pending > 0 && userToken.IsCancellationRequested)
                    {
                        incomplete = true;
                    }

                    return false;
                }

                if (pendingFolders.Count > 0)
                {
                    work = pendingFolders.Pop();
                    return true;
                }

                if (pending == 0)
                {
                    work = null!;
                    return false;
                }

                Monitor.Wait(gate);
            }
        }
    }

    private unsafe void ReadFolder(Work work, byte[] buffer, string? only, EntryNames? names = null)
    {
        var folder = work.Folder;
        var path = folder.Path;
        Volatile.Write(ref reading, path); // the readout's "reading" line; the path is joined once, for the open and for this
        using var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(path), MeasureNative.FileListDirectory | MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            MarkUnreadable(folder);
            return;
        }

        var isRoot = folder.Parent is null;
        var subfolders = new List<Work>();
        var restart = true;
        string? folderPath = path;
        fixed (byte* start = buffer)
        {
            while (true)
            {
                if (stop.IsCancellationRequested)
                {
                    if (userToken.IsCancellationRequested)
                    {
                        lock (gate)
                        {
                            incomplete = true;
                        }
                    }

                    return;
                }

                var infoClass = restart ? MeasureNative.FileIdExtdDirectoryRestartInfoClass : MeasureNative.FileIdExtdDirectoryInfoClass;
                restart = false;
                if (!MeasureNative.GetFileInformationByHandleEx(handle, infoClass, start, BufferBytes))
                {
                    if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != MeasureNative.ErrorNoMoreFiles)
                    {
                        MarkUnreadable(folder);
                    }

                    break;
                }

                long files = 0;
                long bytes = 0;
                var entry = start;
                while (true)
                {
                    var next = Unsafe.ReadUnaligned<uint>(entry);
                    ReadEntry(entry, folder, only, isRoot, work.Top, subfolders, names, ref folderPath, ref files, ref bytes);
                    if (next == 0)
                    {
                        break;
                    }

                    entry += next;
                }

                if (files > 0)
                {
                    Interlocked.Add(ref filesSeen, files);
                }

                if (bytes > 0)
                {
                    Interlocked.Add(ref measuredBytes, bytes);
                    if (work.Top is { } top)
                    {
                        Interlocked.Add(ref top.Bytes, bytes);
                    }
                }

                if (batchDelay > TimeSpan.Zero)
                {
                    // A cancel ends the wait at once.
                    stop.Token.WaitHandle.WaitOne(batchDelay);
                }
            }
        }

        if (subfolders.Count > 0)
        {
            if (work.Top is { } zone)
            {
                // Counted before the subfolders can be taken by another worker.
                Interlocked.Add(ref zone.PendingWork, subfolders.Count);
            }

            lock (gate)
            {
                foreach (var sub in subfolders)
                {
                    pending++;
                    pendingFolders.Push(sub);
                }

                Monitor.PulseAll(gate);
            }
        }
    }

    // One FILE_ID_EXTD_DIR_INFO: NextEntryOffset 0, LastWriteTime 24, EndOfFile 40, AllocationSize 48, FileAttributes 56,
    // FileNameLength 60, ReparsePointTag 68, FileId 72 (128 bits), FileName 88.
    private unsafe void ReadEntry(byte* entry, ScanNode folder, string? only, bool isRoot, TopLevel? top, List<Work> subfolders, EntryNames? names, ref string? folderPath, ref long files, ref long bytes)
    {
        var lastWrite = Unsafe.ReadUnaligned<long>(entry + 24);
        var endOfFile = Unsafe.ReadUnaligned<long>(entry + 40);
        var allocated = Unsafe.ReadUnaligned<long>(entry + 48);
        var attributes = Unsafe.ReadUnaligned<uint>(entry + 56);
        var nameBytes = Unsafe.ReadUnaligned<uint>(entry + 60);
        var tag = Unsafe.ReadUnaligned<uint>(entry + 68);
        var name = new ReadOnlySpan<char>(entry + 88, (int)(nameBytes / 2));
        var isFolder = (attributes & MeasureNative.AttributeDirectory) != 0;
        if (isFolder && (name.SequenceEqual(".") || name.SequenceEqual("..")))
        {
            return;
        }

        if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var marks = (attributes & MeasureNative.AttributeHidden) != 0 ? NodeMarks.Hidden : NodeMarks.None;
        var surrogate = (attributes & MeasureNative.AttributeReparsePoint) != 0 && (tag & MeasureNative.ReparseTagNameSurrogate) != 0;
        var recall = (attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess)) != 0;
        var enter = false;
        if (surrogate)
        {
            marks |= NodeMarks.Link;
            isFolder = false;
        }
        else if (recall)
        {
            marks |= NodeMarks.CloudNotDownloaded;
            Interlocked.Increment(ref cloud);
        }
        else
        {
            enter = isFolder;
        }

        var low = Unsafe.ReadUnaligned<ulong>(entry + 72);
        var high = Unsafe.ReadUnaligned<ulong>(entry + 80);
        if (!isFolder && !surrogate && !recall && allocated == 0 && endOfFile > ResidentLimit)
        {
            // Walk rule 3: a folder listing reports 0 for compressed (WOF) files; one too large to live inside its file
            // record is asked once for its own allocated size (attributes only, never a cloud file).
            folderPath ??= folder.Path;
            var path = folderPath.EndsWith('\\') ? string.Concat(folderPath.AsSpan(), name) : string.Concat(folderPath.AsSpan(), "\\", name);
            if (AskAllocated(path, new UInt128(high, low), out var own))
            {
                allocated = own;
            }
            else
            {
                // Walk rule 7: its allocated size is unknown. The listed 0 stays as a number, marked unreadable and counted.
                marks |= NodeMarks.Unreadable;
                Interlocked.Increment(ref unreadable);
            }
        }

        ItemStamp? stamp = null;
        if (volumeKnown && (marks & NodeMarks.Unreadable) == 0)
        {
            // A directory, a directory link included, has no length in the stamp (as the Kit's FileStamp).
            stamp = new ItemStamp(volumeSerial, new UInt128(high, low), lastWrite, (attributes & MeasureNative.AttributeDirectory) != 0 ? null : endOfFile);
        }

        var entryName = names is null ? name.ToString() : names.Get(name);
        var node = ScanNode.Entry(entryName, isFolder, isFolder ? 0 : endOfFile, isFolder ? 0 : allocated, ToMinutes(lastWrite), stamp, marks, folder);
        folder.AddEntry(node);
        if (!isFolder)
        {
            files++;
            bytes += allocated;
        }

        if (isRoot && isFolder)
        {
            var own = new TopLevel(node.Path, entryName, node);
            if (enter)
            {
                own.PendingWork = 1; // its own folder, which the Work below carries
            }
            else
            {
                own.Colour = TypeGroupKind.Other; // never entered (a cloud folder): nothing inside to read
                own.Done = true;
            }

            lock (gate)
            {
                topLevels.Add(own);
            }

            if (enter)
            {
                subfolders.Add(new Work(node, own));
            }
        }
        else if (enter)
        {
            subfolders.Add(new Work(node, top));
        }
    }

    // The file's own allocated size, once per file id; false when it cannot be asked (the caller marks the file unreadable).
    private unsafe bool AskAllocated(string path, UInt128 fileId, out long allocated)
    {
        var known = volumeKnown && fileId != 0;
        if (known && askedAllocations.TryGetValue(fileId, out allocated))
        {
            return true;
        }

        allocated = 0;
        var ok = false;
        using (var handle = MeasureNative.CreateFile(MeasureNative.ToExtendedPath(path), MeasureNative.FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint, 0))
        {
            var buffer = stackalloc byte[24];
            if (!handle.IsInvalid && MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileStandardInfoClass, buffer, 24))
            {
                allocated = Unsafe.ReadUnaligned<long>(buffer);
                ok = true;
            }
        }

        Interlocked.Increment(ref allocationAsked);
        if (ok && known)
        {
            askedAllocations[fileId] = allocated;
        }

        return ok;
    }

    private void MarkUnreadable(ScanNode folder)
    {
        folder.Flags |= NodeMarks.Unreadable;
        folder.Stamp = null;
        Interlocked.Increment(ref unreadable);
    }

    // Walk rule 8: signed minutes since 1970-01-01 UTC (floored, so a time before 1970 is negative).
    internal static long ToMinutes(long fileTime)
    {
        var ticks = fileTime - UnixEpochFileTime;
        var minutes = ticks / TicksPerMinute;
        return ticks % TicksPerMinute < 0 ? minutes - 1 : minutes;
    }
}
