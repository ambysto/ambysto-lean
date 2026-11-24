using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01, "Progress and cancel", with a walk made slow on purpose (brief M25): the internal walk with a
/// wait after each folder batch (the seam of the app's <c>--scan-delay</c>), so a walk always lasts over a second whatever
/// the machine. The fixture is <see cref="TopFolders"/> chains of <see cref="ChainDepth"/> nested folders, each folder
/// holding <see cref="FilesPerLevel"/> files, plus <see cref="RootFiles"/> files at the root: a folder of a chain is found
/// only once its parent's batch was read, so however many workers walk, the walk takes at least
/// <see cref="ChainDepth"/> × the delay. Not parallel with other tests: its assertions are about time.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SlowWalkTests
{
    private const int TopFolders = 4;
    private const int ChainDepth = 10;
    private const int FilesPerLevel = 3;
    private const int RootFiles = 2;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);
    private static TempFolder? fixture;
    private static string[] topFolders = [];

    [ClassInitialize]
    public static void BuildFixture(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = new TempFolder("slow-walk");
        var tops = new List<string>();
        for (var t = 0; t < TopFolders; t++)
        {
            var folder = fixture.Folder("top" + t.ToString(CultureInfo.InvariantCulture));
            tops.Add(folder);
            for (var level = 0; level < ChainDepth; level++)
            {
                for (var f = 0; f < FilesPerLevel; f++)
                {
                    TempFolder.WriteBytes(Path.Combine(folder, "f" + f.ToString(CultureInfo.InvariantCulture) + ".bin"), 4 * ByteUnits.KB, f);
                }

                if (level < ChainDepth - 1)
                {
                    folder = Path.Combine(folder, "l" + level.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(folder);
                }
            }
        }

        for (var f = 0; f < RootFiles; f++)
        {
            fixture.File("root" + f.ToString(CultureInfo.InvariantCulture) + ".bin", 4 * ByteUnits.KB);
        }

        topFolders = [.. tops];
    }

    [ClassCleanup]
    public static void RemoveFixture() => fixture?.Dispose();

    private const long AllFiles = (TopFolders * ChainDepth * FilesPerLevel) + RootFiles;

    /// <summary>Records every report with the wall clock; signals the first report; can block each report call; counts the
    /// calls that start after <see cref="MarkReturned"/> and those still running at that moment.</summary>
    private sealed class Recorder(TimeSpan blockFor) : IProgress<ScanProgress>
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int startedAfterReturn;
        private volatile bool returned;

        public Recorder() : this(TimeSpan.Zero)
        {
        }

        public ConcurrentQueue<(TimeSpan Wall, ScanProgress Report)> Reports { get; } = new();

        public Task First => first.Task;

        public TimeSpan Elapsed => clock.Elapsed;

        public int StartedAfterReturn => Volatile.Read(ref startedAfterReturn);

        public int ActiveAtReturn { get; private set; }

        public void MarkReturned()
        {
            returned = true;
            ActiveAtReturn = Volatile.Read(ref active);
        }

        public void Report(ScanProgress value)
        {
            if (returned)
            {
                Interlocked.Increment(ref startedAfterReturn);
            }

            Interlocked.Increment(ref active);
            try
            {
                Reports.Enqueue((clock.Elapsed, value));
                first.TrySetResult();
                if (blockFor > TimeSpan.Zero)
                {
                    Thread.Sleep(blockFor);
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private static Task<ScanTree> Walk(IProgress<ScanProgress> progress, CancellationToken token) => Scanner.ScanAsync(fixture!.Path, progress, Delay, token);

    // docs/spec/01 Progress and cancel, with a slow walk: (a) results appear progressively, the first within the first
    // second; (b) reports carry the totals of the root's direct folders (full paths), a folder holding files reaches a total
    // above 0, files seen grows and never exceeds the final count; (c) a cancel at the first report lands mid-walk, returns
    // within 1 second with a partial tree holding clearly fewer files; (d) no report call starts, or is still running, once
    // the scan has returned, even with a sink that blocks 200 ms per call; (e) a walk of a chain of N folder batches with
    // delay d takes at least N × d.
    [TestMethod]
    [Timeout(180_000)]
    public async Task ProgressAndCancelWithASlowWalk()
    {
        // (e) and the fixture: the full walk.
        var recorder = new Recorder();
        var full = await Walk(recorder, CancellationToken.None);
        var duration = recorder.Elapsed;
        recorder.MarkReturned();
        Assert.IsFalse(full.Cancelled);
        Assert.AreEqual(AllFiles, full.Root.FileCount, "the full walk counts every file");
        Check.True(duration >= ChainDepth * Delay, $"(e) a chain of {ChainDepth} folder batches with a delay of {Delay.TotalMilliseconds} ms took {duration.TotalMilliseconds:F0} ms");
        Check.True(duration > TimeSpan.FromSeconds(1), $"precondition: the slow walk lasts over a second ({duration.TotalMilliseconds:F0} ms)");

        // (a) progressively, from the first second.
        var reports = recorder.Reports.ToList();
        Check.True(reports.Count > 0, $"(a) no progress report in a walk of {duration.TotalMilliseconds:F0} ms");
        Check.True(reports[0].Wall <= TimeSpan.FromSeconds(1.25), $"(a) the first report came after {reports[0].Wall.TotalMilliseconds:F0} ms");

        // (b) the keys are the root's direct folders, by full path; totals; files seen.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var largest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long lastSeen = 0;
        foreach (var (_, report) in reports)
        {
            Check.AtLeast(lastSeen, report.FilesSeen, "(b) files seen never decreases");
            lastSeen = report.FilesSeen;
            foreach (var (folder, bytes) in report.TopLevelAllocatedBytes)
            {
                Check.True(topFolders.Contains(folder, StringComparer.OrdinalIgnoreCase), "(b) a report key that is not a folder directly in the root (full path): " + folder);
                keys.Add(folder);
                largest[folder] = Math.Max(largest.GetValueOrDefault(folder), bytes);
            }
        }

        Check.Set(keys, topFolders);
        foreach (var folder in topFolders)
        {
            Check.AtLeast(1, largest.GetValueOrDefault(folder), "(b) the reported total of " + folder + ", which holds files");
        }

        Check.AtLeast(1, reports.Max(r => r.Report.FilesSeen), "(b) files seen grows above 0");
        Check.AtMost(full.Root.FileCount, lastSeen, "(b) files seen never exceeds the final count");

        // (c) a cancel at the first report lands mid-walk, every time.
        using (var cts = new CancellationTokenSource())
        {
            var cancelling = new Recorder();
            var scan = Walk(cancelling, cts.Token);
            var firstDone = await Task.WhenAny(cancelling.First, scan);
            Assert.IsFalse(ReferenceEquals(firstDone, scan), $"(c) the slow walk finished in {cancelling.Elapsed.TotalMilliseconds:F0} ms before its first report");
            var cancelledAt = cancelling.Elapsed;
            await cts.CancelAsync();
            var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None));
            var returnedAt = cancelling.Elapsed;
            Assert.IsTrue(ReferenceEquals(finished, scan), "(c) the scan did not return within 30 s of the cancel");
            Check.True(returnedAt - cancelledAt <= TimeSpan.FromSeconds(1), $"(c) the cancel took {(returnedAt - cancelledAt).TotalMilliseconds:F0} ms");
            var partial = await scan;
            Assert.IsTrue(partial.Cancelled, "(c) the tree says it is partial");
            Check.True(partial.Root.FileCount * 4 < full.Root.FileCount * 3, $"(c) the partial tree holds {partial.Root.FileCount} of {full.Root.FileCount} files (cancelled at {cancelledAt.TotalMilliseconds:F0} ms)");
        }

        // (d) a sink that blocks 200 ms per call: no call starts or is still running once the scan has returned.
        var blocking = new Recorder(TimeSpan.FromMilliseconds(200));
        var blocked = await Walk(blocking, CancellationToken.None);
        blocking.MarkReturned();
        Assert.IsFalse(blocked.Cancelled);
        Check.True(!blocking.Reports.IsEmpty, "(d) the blocking sink received reports");
        Assert.AreEqual(0, blocking.ActiveAtReturn, "(d) report calls still running when the scan returned");
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.AreEqual(0, blocking.StartedAfterReturn, "(d) report calls that started after the scan returned");
    }
}
