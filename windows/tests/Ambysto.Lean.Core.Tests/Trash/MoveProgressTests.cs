using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>docs/spec/07 Windows note "Moving &lt;i&gt; of &lt;n&gt;: &lt;name&gt;" (owner decision 2026-10-18), plan Design
/// row <c>Trash.Trasher.Run</c>: <see cref="MoveProgress"/>(index from 1, the session's item count, the item's name) is
/// reported before each item reached, in order; nothing for items after a stop or a cancel (cancel still stops only between
/// items). The gate and the shell move are the doubles of <see cref="TrashBench"/>.</summary>
[TestClass]
public sealed class MoveProgressTests
{
    /// <summary>A synchronous sink that records each report with the calls the doubles had seen at that moment.</summary>
    private sealed class Recorder(CallLog log) : IProgress<MoveProgress>
    {
        public List<(MoveProgress Report, IReadOnlyList<string> CallsSoFar)> Reports { get; } = [];

        public void Report(MoveProgress value) => Reports.Add((value, log.All));

        public MoveProgress[] Values => Reports.Select(r => r.Report).ToArray();
    }

    private static void Reported(Recorder recorder, params (int Index, int Count, string Name)[] expected)
    {
        var got = recorder.Values;
        var shown = string.Join(", ", got.Select(p => $"({p.Index}, {p.Count}, {p.Name})"));
        Assert.AreEqual(expected.Length, got.Length, "reports: " + shown);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(new MoveProgress(expected[i].Index, expected[i].Count, expected[i].Name), got[i], "report " + i + " of: " + shown);
        }
    }

    // One report per item, in order, before the item: the gate has not been asked about it yet, and the item before it is
    // done (its move is in the log).
    [TestMethod]
    public async Task EachItemIsReportedBeforeItIsReachedInOrder()
    {
        using var b = new TrashBench("progress-order");
        var a = b.File("a.bin");
        var c = b.File("c.bin");
        var d = b.File(@"proj\d.bin");
        var tree = await b.ScanAsync();
        var recorder = new Recorder(b.Log);
        var session = TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"), TrashBench.Item(tree, "proj"));

        var outcomes = b.Trasher().Run(session, CancellationToken.None, recorder);

        Assert.AreEqual(3, outcomes.Count);
        Check.True(outcomes.All(o => o.Result == TrashResult.Trashed), "all moved: " + b.Log);
        Reported(recorder, (1, 3, "a.bin"), (2, 3, "c.bin"), (3, 3, "proj"));
        string[] items = [a, c, Path.GetDirectoryName(d)!];
        for (var i = 0; i < items.Length; i++)
        {
            var calls = recorder.Reports[i].CallsSoFar;
            Check.False(calls.Any(call => call.EndsWith(" " + items[i], StringComparison.OrdinalIgnoreCase) && call.StartsWith("check ", StringComparison.Ordinal)), $"item {i + 1} was reported after the gate was asked about it: {string.Join(" | ", calls)}");
            if (i > 0)
            {
                Check.True(calls.Any(call => call.StartsWith("recycle ", StringComparison.Ordinal) && call.EndsWith("\\" + Path.GetFileName(items[i - 1]), StringComparison.OrdinalIgnoreCase)), $"item {i + 1} was reported before item {i} was done: {string.Join(" | ", calls)}");
            }
        }
    }

    // An item the gate refuses is still reached, so it is reported; the session goes on.
    [TestMethod]
    public async Task ARefusedItemIsReportedToo()
    {
        using var b = new TrashBench("progress-refused");
        b.File("refused.bin");
        b.File("moved.bin");
        var tree = await b.ScanAsync();
        b.Gate.OnCheck = r => r.Path.EndsWith("refused.bin", StringComparison.OrdinalIgnoreCase) ? KitObjects.Refuse(RefusalReason.Protected) : KitObjects.ApproveNow(r.Path);
        var recorder = new Recorder(b.Log);

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "refused.bin"), TrashBench.Item(tree, "moved.bin")), CancellationToken.None, recorder);

        Assert.AreEqual(2, outcomes.Count);
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result);
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, outcomes[1].Reason);
        Reported(recorder, (1, 2, "refused.bin"), (2, 2, "moved.bin"));
    }

    // A stop (the item reported in the bin is another file: lost) reports nothing for the items after it.
    [TestMethod]
    public async Task NothingIsReportedAfterAStop()
    {
        using var b = new TrashBench("progress-stop");
        b.File("a.bin", 4096, seed: 5);
        b.File("c.bin");
        b.File("e.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            File.Delete(p);
            var other = b.Recycler.NextBinPath(p);
            TempFolder.WriteBytes(other, 4096, seed: 5);
            return new RecycleReport(true, other, false, 0);
        };
        var recorder = new Recorder(b.Log);

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"), TrashBench.Item(tree, "e.bin")), CancellationToken.None, recorder);

        Assert.AreEqual(1, outcomes.Count, "the session stopped after the first item");
        Reported(recorder, (1, 3, "a.bin"));
    }

    // A journal that cannot be written stops the session before any move: one report, for the item reached.
    [TestMethod]
    public async Task NothingIsReportedAfterAJournalStop()
    {
        using var b = new TrashBench("progress-journal-stop");
        b.File("a.bin");
        b.File("c.bin");
        var tree = await b.ScanAsync();
        var recorder = new Recorder(b.Log);
        IReadOnlyList<TrashOutcome> outcomes;
        using (FileSwitches.ReadOnly(b.JournalPath))
        {
            outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")), CancellationToken.None, recorder);
        }

        Check.AtMost(1, outcomes.Count, "at most the first item is reached");
        Assert.AreEqual(outcomes.Count, recorder.Reports.Count, "one report per item reached");
        if (outcomes.Count == 1)
        {
            Reported(recorder, (1, 2, "a.bin"));
        }
    }

    // A cancel during item 1 lets item 1 finish and reports nothing for item 2.
    [TestMethod]
    public async Task NothingIsReportedAfterACancel()
    {
        using var b = new TrashBench("progress-cancel");
        b.File("a.bin");
        b.File("c.bin");
        var tree = await b.ScanAsync();
        using var cancel = new CancellationTokenSource();
        b.Gate.AfterCheck = _ => cancel.Cancel();
        var recorder = new Recorder(b.Log);

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")), cancel.Token, recorder);

        Assert.AreEqual(1, outcomes.Count);
        Assert.AreEqual(TrashResult.Trashed, outcomes[0].Result, "item 1 finished: " + outcomes[0].Reason);
        Reported(recorder, (1, 2, "a.bin"));
    }

    // A cancel before the run reports nothing.
    [TestMethod]
    public async Task ACancelBeforeTheRunReportsNothing()
    {
        using var b = new TrashBench("progress-cancel-first");
        b.File("a.bin");
        var tree = await b.ScanAsync();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var recorder = new Recorder(b.Log);

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin")), cancel.Token, recorder);

        Assert.AreEqual(0, outcomes.Count);
        Assert.AreEqual(0, recorder.Reports.Count);
    }

    // A Temporary files session (module Temp) reports the same way.
    [TestMethod]
    public async Task ATempSessionReportsToo()
    {
        using var b = new TrashBench("progress-temp");
        b.File("old.tmp");
        var tree = await b.ScanAsync();
        var recorder = new Recorder(b.Log);

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Temp, TrashBench.Item(tree, "old.tmp")), CancellationToken.None, recorder);

        Assert.AreEqual(TrashResult.Trashed, outcomes.Single().Result, outcomes.Single().Reason);
        Reported(recorder, (1, 1, "old.tmp"));
    }

    // The 2-argument Run still works (no progress sink), and a null sink is allowed.
    [TestMethod]
    public async Task RunWithoutProgressStillWorks()
    {
        using var b = new TrashBench("progress-none");
        var a = b.File("a.bin");
        b.File("c.bin");
        var tree = await b.ScanAsync();

        var outcomes = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin")), CancellationToken.None);
        Assert.AreEqual(TrashResult.Trashed, outcomes.Single().Result, outcomes.Single().Reason);
        Check.False(File.Exists(a), "moved");

        var second = b.Trasher().Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "c.bin")), CancellationToken.None, null);
        Assert.AreEqual(TrashResult.Trashed, second.Single().Result, second.Single().Reason);
        Assert.AreEqual(2, b.Entries().Count);
    }
}
