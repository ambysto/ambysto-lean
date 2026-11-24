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

/// <summary>docs/spec/01, "Progress and cancel": top-level totals refresh at most 4 times per second from the first
/// second; cancel stops every worker within 1 second, including inside a folder with hundreds of thousands of
/// entries; after cancel the map shows what was measured so far, labelled "Scan cancelled — partial".
/// One shared fixture per class: a folder of 200,000 entries plus five small top-level folders. The class does not run
/// in parallel with other tests: its assertions are about time.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ProgressAndCancelTests
{
    private const int BigFolderEntries = 200_000;
    private const int SmallFolders = 5;
    private const int SmallFolderEntries = 1_000;
    private static readonly TimeSpan ShortScan = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
    private static TempFolder? fixture;

    [ClassInitialize]
    public static void BuildFixture(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = new TempFolder("progress-cancel");
        var big = fixture.Folder("big");
        for (var i = 0; i < BigFolderEntries; i++)
        {
            using var stream = new FileStream(Path.Combine(big, i.ToString("D6", CultureInfo.InvariantCulture) + ".dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None);
        }

        for (var f = 0; f < SmallFolders; f++)
        {
            var folder = fixture.Folder("top" + f.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < SmallFolderEntries; i++)
            {
                File.WriteAllBytes(Path.Combine(folder, i.ToString(CultureInfo.InvariantCulture) + ".dat"), new byte[1024]);
            }
        }
    }

    [ClassCleanup]
    public static void RemoveFixture() => fixture?.Dispose();

    /// <summary>Records every report with the wall clock and signals the test thread at the first report that has seen
    /// a file (<see cref="FirstFileSeen"/>), so the test can cancel while the walk is inside the tree.</summary>
    private sealed class Recorder : IProgress<ScanProgress>
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource firstFileSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int afterFinish;
        private volatile bool finished;

        public ConcurrentQueue<(TimeSpan Wall, ScanProgress Report)> Reports { get; } = new();

        /// <summary>Completes at the first report with FilesSeen > 0 (never on the reporting thread's stack).</summary>
        public Task FirstFileSeen => firstFileSeen.Task;

        public TimeSpan Elapsed => clock.Elapsed;

        /// <summary>Reports that arrived after <see cref="MarkFinished"/>.</summary>
        public int AfterFinish => Volatile.Read(ref afterFinish);

        public void MarkFinished() => finished = true;

        public void Report(ScanProgress value)
        {
            if (finished)
            {
                Interlocked.Increment(ref afterFinish);
            }

            Reports.Enqueue((clock.Elapsed, value));
            if (value.FilesSeen > 0)
            {
                firstFileSeen.TrySetResult();
            }
        }
    }

    /// <summary>Starts a scan and cancels it from the test thread at the first report that has seen a file. Returns the
    /// scan task and when the cancel was requested on the recorder's clock. A walker that finishes the whole fixture
    /// before any such report cannot be cancelled mid-walk by this test: Inconclusive, not a failure (chosen over a
    /// fixture large enough to rule it out, which would cost minutes per run; the fixture here is 205,000 entries).</summary>
    private static async Task<(Task<ScanTree> Scan, TimeSpan CancelledAt)> StartAndCancelMidWalk(Recorder recorder, CancellationTokenSource cts)
    {
        var scan = new Scanner().ScanAsync(fixture!.Path, recorder, cts.Token);
        var first = await Task.WhenAny(recorder.FirstFileSeen, scan);
        if (ReferenceEquals(first, scan))
        {
            Assert.Inconclusive($"the walk finished in {recorder.Elapsed.TotalMilliseconds:F0} ms before any progress report had seen a file; the cancel could not land mid-walk");
        }

        var cancelledAt = recorder.Elapsed;
        await cts.CancelAsync();
        return (scan, cancelledAt);
    }

    private static Task<ScanTree> Scan(Recorder recorder, CancellationToken token) => new Scanner().ScanAsync(fixture!.Path, recorder, token);

    private static void RequireAReport(Recorder recorder, TimeSpan duration) =>
        Check.True(!recorder.Reports.IsEmpty || duration <= ShortScan, $"no progress report at all for a scan of {duration.TotalMilliseconds:F0} ms (results must appear progressively)");

    // docs/spec/01 Progress and cancel: top-level folder totals refresh at most 4 times per second.
    [TestMethod]
    [Timeout(120_000)]
    public async Task ProgressArrivesAtMostFourTimesPerSecond()
    {
        var recorder = new Recorder();
        var tree = await Scan(recorder, CancellationToken.None);
        var total = recorder.Elapsed;
        RequireAReport(recorder, total);
        var times = recorder.Reports.Select(r => r.Wall).OrderBy(t => t).ToList();
        for (var i = 4; i < times.Count; i++)
        {
            var span = times[i] - times[i - 4];
            Check.True(span >= TimeSpan.FromMilliseconds(900), $"five reports within {span.TotalMilliseconds:F0} ms (reports {i - 4}..{i}): more than 4 per second");
        }

        Check.AtMost(4 * (long)Math.Ceiling(total.TotalSeconds) + 1, times.Count, $"reports for a scan of {total.TotalSeconds:F2} s");
        Assert.IsFalse(tree.Cancelled);
    }

    // docs/spec/01 Progress and cancel: results appear progressively, from the first second: at least one report
    // before completion, and the first one within the first second (with a quarter second of slack).
    [TestMethod]
    [Timeout(120_000)]
    public async Task FirstProgressArrivesWithinTheFirstSecond()
    {
        var recorder = new Recorder();
        await Scan(recorder, CancellationToken.None);
        var total = recorder.Elapsed;
        RequireAReport(recorder, total);
        if (recorder.Reports.TryPeek(out var first))
        {
            Check.True(first.Wall <= TimeSpan.FromSeconds(1.25), $"first report after {first.Wall.TotalMilliseconds:F0} ms (scan of {total.TotalMilliseconds:F0} ms)");
        }
    }

    // docs/spec/01 Progress and cancel: each report carries top-level totals so far (keyed by full path), never decreasing.
    [TestMethod]
    [Timeout(120_000)]
    public async Task ProgressReportsTopLevelTotalsThatOnlyGrow()
    {
        var recorder = new Recorder();
        var tree = await Scan(recorder, CancellationToken.None);
        RequireAReport(recorder, recorder.Elapsed);
        var reports = recorder.Reports.Select(r => r.Report).ToList();
        var last = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long lastFiles = 0;
        foreach (var report in reports)
        {
            Check.AtLeast(lastFiles, report.FilesSeen, "files seen");
            lastFiles = report.FilesSeen;
            foreach (var (folder, bytes) in report.TopLevelAllocatedBytes)
            {
                Check.True(Path.IsPathFullyQualified(folder), "top-level totals are keyed by full path: " + folder);
                if (last.TryGetValue(folder, out var previous))
                {
                    Check.AtLeast(previous, bytes, "top-level total of " + folder);
                }

                last[folder] = bytes;
            }
        }

        foreach (var child in tree.Root.Children)
        {
            if (last.TryGetValue(child.Path, out var reported))
            {
                Check.AtMost(child.AllocatedBytes, reported, "a report never exceeds the final total of " + child.Name);
            }
        }

        Check.AtMost(tree.Root.FileCount, lastFiles, "files seen never exceeds the final count");
    }

    // docs/spec/01 Progress and cancel: once ScanAsync has returned, no report arrives any more (completion).
    [TestMethod]
    [Timeout(120_000)]
    public async Task NoProgressArrivesAfterACompletedScanReturns()
    {
        var recorder = new Recorder();
        await Scan(recorder, CancellationToken.None);
        recorder.MarkFinished();
        await Task.Delay(Settle);
        Assert.AreEqual(0, recorder.AfterFinish, "reports after ScanAsync returned");
    }

    // docs/spec/01 Progress and cancel: cancel stops every worker within 1 second, inside the folder of 200,000
    // entries: the cancel is requested by the test thread at the first report that has seen a file, so it lands mid-walk.
    [TestMethod]
    [Timeout(60_000)]
    public async Task CancelFromTheFirstReportReturnsAPartialTreeWithinOneSecond()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var (scan, cancelledAt) = await StartAndCancelMidWalk(recorder, cts);
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None));
        var returnedAt = recorder.Elapsed;
        recorder.MarkFinished();
        Check.True(ReferenceEquals(finished, scan), "the scan did not return within 60 s of the cancel");
        var latency = returnedAt - cancelledAt;
        Check.True(latency <= TimeSpan.FromSeconds(1), $"cancel took {latency.TotalMilliseconds:F0} ms");
        var tree = await scan;
        Assert.IsTrue(tree.Cancelled, "the tree says it is partial");
        Assert.IsNotNull(tree.Root);
        await Task.Delay(Settle);
        Assert.AreEqual(0, recorder.AfterFinish, "reports after the cancelled ScanAsync returned");
    }

    // docs/spec/01 Progress and cancel: after cancel the map shows what was measured so far (a partial tree, no throw).
    [TestMethod]
    [Timeout(120_000)]
    public async Task CancelledScanKeepsWhatWasMeasuredAndNeverThrows()
    {
        var full = await Scan(new Recorder(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var (scan, _) = await StartAndCancelMidWalk(new Recorder(), cts);
        var partial = await scan;
        Assert.IsTrue(partial.Cancelled);
        Check.AtMost(full.Root.AllocatedBytes, partial.Root.AllocatedBytes, "a partial tree never measures more than the full one");
        Check.AtMost(full.Root.FileCount, partial.Root.FileCount, "a partial tree never counts more files than the full one");
        Assert.IsTrue(string.Equals(full.Root.Path, partial.Root.Path, StringComparison.OrdinalIgnoreCase));
    }

    // docs/spec/01 Progress and cancel: a cancel before the first entry still returns a (cancelled) tree.
    [TestMethod]
    [Timeout(30_000)]
    public async Task AlreadyCancelledTokenReturnsACancelledTreeAtOnce()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var clock = Stopwatch.StartNew();
        var tree = await new Scanner().ScanAsync(fixture!.Path, null, cts.Token);
        Check.True(clock.Elapsed <= TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
        Assert.IsTrue(tree.Cancelled);
    }

    // docs/spec/01 Progress and cancel: a scan with no progress sink works the same (progress is optional).
    [TestMethod]
    [Timeout(120_000)]
    public async Task ScanWithoutProgressSinkCountsEveryEntry()
    {
        var tree = await new Scanner().ScanAsync(fixture!.Path, null, CancellationToken.None);
        Assert.AreEqual((long)BigFolderEntries + (SmallFolders * SmallFolderEntries), tree.Root.FileCount);
        Assert.AreEqual((long)BigFolderEntries, Trees.Node(tree, "big").FileCount);
    }
}
