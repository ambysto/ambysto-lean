using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>docs/spec/02 "Order per item" and its Windows note on steps 6–7; intent Outcome 6 and criterion 2; plan Design
/// "Trasher". The gate and the shell move are doubles (<see cref="FakeItemGate"/>, <see cref="FakeRecycler"/>); the items
/// are real files scanned by Lean's own scanner; the "Recycle Bin" is a stand-in folder on the same volume (a move keeps
/// the file id, as a real recycle does); the journal is real, in the test's scratch folder. Post-verify facts come from
/// <see cref="IItemGate.Facts"/> (the Kit's real read unless a test replaces them).</summary>
[TestClass]
public sealed class TrasherTests
{
    private static readonly string ChangedSinceScan = RefusalReason.ChangedSinceScan.Message();

    private static TrashOutcome Single(IReadOnlyList<TrashOutcome> outcomes)
    {
        Assert.AreEqual(1, outcomes.Count, "outcomes: " + string.Join(", ", outcomes.Select(o => o.Item.Node.Name + "=" + o.Result)));
        return outcomes[0];
    }

    private static void InPlace(string path, string why) => Check.True(File.Exists(path) || Directory.Exists(path), why + ": " + path + " is not in place");

    // --- docs/spec/02 "Order per item" --------------------------------------------------------------------------

    // Steps 1–8 in order: kept copy, (re-measure), gate, in use, journal check, move, post-verify, journal. The journal has
    // no entry for the item before the move and its post-verify; exactly one after.
    [TestMethod]
    public async Task TheStepsRunInTheSpecsOrder()
    {
        using var b = new TrashBench("trash-order");
        var a = b.File("a.bin", 8192, seed: 3);
        var keep = b.File("keep.bin", 8192, seed: 3);
        var tree = await b.ScanAsync();
        var session = TrashBench.Session(LeanModule.Duplicates, TrashBench.Item(tree, "a.bin", keep: "keep.bin"));
        var journalAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var journal = b.JournalPath;
        b.Gate.AfterInUse = _ => journalAt["inuse"] = Count(journal);
        b.Recycler.AfterRecycle = (_, _) => journalAt["recycle"] = Count(journal);
        b.Gate.BeforeFacts = p =>
        {
            if (p.StartsWith(b.Recycler.BinFolder, StringComparison.OrdinalIgnoreCase))
            {
                journalAt["verify"] = Count(journal);
            }
        };

        var outcome = Single(b.Run(session));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        var calls = b.Log.All;
        var check = b.Log.IndexOf("check", a);
        var inUse = calls.ToList().FindIndex(c => c.StartsWith("inuse ", StringComparison.Ordinal));
        var recycle = calls.ToList().FindIndex(c => c.StartsWith("recycle ", StringComparison.Ordinal));
        var verify = calls.ToList().FindIndex(c => c.StartsWith("facts " + b.Recycler.BinFolder, StringComparison.OrdinalIgnoreCase));
        Check.True(check >= 0, "the gate was asked about the item: " + b.Log);
        Check.True(inUse > check, "in use (step 4) after the gate (step 3): " + b.Log);
        Check.True(recycle > inUse, "the move (step 6) after in use: " + b.Log);
        Check.True(verify > recycle, "post-verify (step 7) reads the reported item's facts after the move: " + b.Log);
        var keptFacts = b.Log.IndexOf("facts", keep);
        Check.True(keptFacts < 0 || keptFacts < check, "the kept copy (step 1) is checked before the gate: " + b.Log);
        Assert.AreEqual(1, b.Log.Of("check").Count, "one gate call for one item");
        Assert.AreEqual(1, b.Log.Of("recycle").Count, "one shell move for one item");
        Assert.AreEqual(0, journalAt["inuse"], "no entry before the move");
        Assert.AreEqual(0, journalAt["recycle"], "no entry right after the move, before post-verify");
        Assert.AreEqual(0, journalAt["verify"], "no entry during post-verify");
        Assert.AreEqual(1, b.Entries().Count, "step 8: one entry after the item");
    }

    private static int Count(string journal) => File.Exists(journal) ? JournalStore.Open(journal).Read().Count : 0;

    // Step 3: the gate gets the path the scan showed and the scan's stamp as the shown stamp.
    [TestMethod]
    public async Task TheGateGetsTheScannedPathAndStamp()
    {
        using var b = new TrashBench("trash-request");
        b.File(@"sub\a.bin", 5000);
        var tree = await b.ScanAsync();
        var node = Trees.Node(tree, @"sub\a.bin");
        Single(b.Run(TrashBench.Session(LeanModule.LargeOld, new SessionItem(node, null))));

        var request = b.Gate.Requests.Single();
        Assert.AreEqual(node.Path, request.Path, "the scanned path");
        Assert.AreEqual(TrashBench.ShownStamp(node), request.ShownStamp, "the scan's stamp is the shown stamp");
    }

    // Step 6: "using the exact real path the gate approved" (not the scanned spelling).
    [TestMethod]
    public async Task TheMovedPathIsTheGatesApprovedRealPath()
    {
        using var b = new TrashBench("trash-approved-path");
        var a = b.File("a.bin");
        var tree = await b.ScanAsync();
        var approved = a.ToUpperInvariant();
        Assert.AreNotEqual(a, approved, "the test needs a second spelling of the same path");
        b.Gate.OnCheck = r => KitObjects.ApproveNow(r.Path, approvedPath: approved);

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"))));

        Assert.AreEqual(approved, b.Recycler.Paths.Single(), "the shell got the approved real path, exactly");
        Assert.AreEqual(TrashResult.Trashed, outcome.Result, outcome.Reason);
        Assert.AreEqual(approved, outcome.Entry!.ApprovedRealPath, "the journal records the approved real path");
    }

    // Step 2: sizes from the scan are never reused: the journal's bytes are measured just before the move.
    [TestMethod]
    public async Task TheBytesAreMeasuredNowNotTakenFromTheScan()
    {
        using var b = new TrashBench("trash-remeasure");
        var a = b.File("a.bin", 4096);
        var tree = await b.ScanAsync();
        TempFolder.WriteBytes(a, 64 * 1024, seed: 2); // grows after the scan; the double gate approves it as it is now

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"))));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, outcome.Reason);
        Check.AtLeast(64 * 1024, outcome.Entry!.Bytes ?? -1, "bytes measured immediately before the move");
    }

    // --- Step 1: the kept copy ----------------------------------------------------------------------------------

    // "The copy you are keeping changed or is gone": skipped, before the gate.
    [TestMethod]
    [DataRow("changed")]
    [DataRow("gone")]
    [DataRow("replaced")]
    public async Task AKeptCopyThatChangedOrIsGoneSkipsBeforeTheGate(string how)
    {
        using var b = new TrashBench("trash-kept-" + how);
        var a = b.File("a.bin", 8192, seed: 4);
        var keep = b.File("keep.bin", 8192, seed: 4);
        var tree = await b.ScanAsync();
        switch (how)
        {
            case "changed":
                File.AppendAllText(keep, "x");
                break;
            case "gone":
                File.Delete(keep);
                break;
            default:
                // Same name, same bytes, another file: the identity changed.
                File.Delete(keep);
                TempFolder.WriteBytes(keep, 8192, seed: 4);
                break;
        }

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.Duplicates, TrashBench.Item(tree, "a.bin", keep: "keep.bin"))));

        Assert.AreEqual(TrashResult.Skipped, outcome.Result);
        Assert.AreEqual(Text.KeptCopyChanged, outcome.Reason);
        Assert.AreEqual(0, b.Log.Of("check").Count, "no gate call: " + b.Log);
        Assert.AreEqual(0, b.Log.Of("recycle").Count, "no move");
        InPlace(a, "the duplicate stays");
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Skipped, entry.Result);
        Assert.AreEqual(Text.KeptCopyChanged, entry.Reason);
    }

    // --- Step 3: the gate's refusal --------------------------------------------------------------------------------

    // A refusal skips with the Kit's message (GateResult.Message), and the session goes on.
    [TestMethod]
    [DataRow(RefusalReason.Protected, null, "Protected location")]
    [DataRow(RefusalReason.ChangedSinceScan, null, "Changed since the scan")]
    [DataRow(RefusalReason.CloudFile, null, null)]
    // The Recycle Bin rows of intent criterion 1 (bin off or the NoRecycleFiles policy, purge needed, over quota): the
    // Kit's reason is passed through unchanged, and nothing is moved.
    [DataRow(RefusalReason.RecycleBinOff, null, null)]
    [DataRow(RefusalReason.RecycleBinFull, null, null)]
    [DataRow(RefusalReason.TooLargeForRecycleBin, null, null)]
    [DataRow(RefusalReason.NotAllowed, Text.HintOutside, Text.HintOutside)]
    [DataRow(RefusalReason.CouldNotVerify, "identity", "Could not verify: identity")]
    public async Task AGateRefusalSkipsWithTheKitsMessage(RefusalReason reason, string? detail, string? expected)
    {
        using var b = new TrashBench("trash-refused");
        var a = b.File("a.bin");
        b.File("b.bin");
        var tree = await b.ScanAsync();
        var refusal = KitObjects.Refuse(reason, detail);
        b.Gate.OnCheck = r => string.Equals(r.Path, a, StringComparison.OrdinalIgnoreCase) ? refusal : KitObjects.ApproveNow(r.Path);

        var outcomes = b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "b.bin")));

        Assert.AreEqual(2, outcomes.Count, "the session goes on after a refusal");
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result);
        Assert.AreEqual(expected ?? reason.Message(), outcomes[0].Reason, "the Kit's message");
        Assert.AreEqual(refusal.Message, outcomes[0].Reason, "GateResult.Message");
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, outcomes[1].Reason);
        Check.False(b.Log.Of("inuse").Any(c => c.EndsWith(a, StringComparison.OrdinalIgnoreCase)), "no in-use check for a refused item: " + b.Log);
        Assert.AreEqual(1, b.Recycler.Paths.Count, "only the approved item moved");
        InPlace(a, "the refused item stays");
        var entries = b.Entries();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(JournalResult.Skipped, entries[0].Result);
        Assert.AreEqual(refusal.Message, entries[0].Reason);
    }

    // --- Step 4: in use ------------------------------------------------------------------------------------------

    // In use → skip with the process names and ids; the session goes on.
    [TestMethod]
    public async Task AnItemInUseIsSkippedWithItsHolders()
    {
        using var b = new TrashBench("trash-inuse");
        var a = b.File("a.bin");
        b.File("b.bin");
        var tree = await b.ScanAsync();
        b.Gate.OnInUse = item => item.CanonicalPath.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase)
            ? KitObjects.InUse([new InUseHolder(4242, "notepad.exe", "file"), new InUseHolder(777, "Code.exe", "image")])
            : KitObjects.NotInUse();

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "b.bin")));

        Assert.AreEqual(2, outcomes.Count, "the session goes on");
        var reason = outcomes[0].Reason ?? "";
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result);
        Check.True(reason.StartsWith(Text.InUseBy("").TrimEnd(), StringComparison.Ordinal), "the in-use words: " + reason);
        Check.Contains(reason, "notepad.exe");
        Check.Contains(reason, "4242");
        Check.Contains(reason, "Code.exe");
        Check.Contains(reason, "777");
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, outcomes[1].Reason);
        Assert.AreEqual(1, b.Recycler.Paths.Count, "the item in use did not move");
        InPlace(a, "the item in use stays");
        Assert.AreEqual(reason, b.Entries()[0].Reason, "journaled with the same reason");
    }

    // docs/spec/02 step 4: processes that cannot be read are information, never a block.
    [TestMethod]
    public async Task UnreadableProcessesDoNotBlock()
    {
        using var b = new TrashBench("trash-unreadable");
        b.File("a.bin");
        var tree = await b.ScanAsync();
        b.Gate.OnInUse = _ => KitObjects.InUse([], unreadableProcessCount: 12);

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"))));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, outcome.Reason);
    }

    // --- Step 5: the journal check -------------------------------------------------------------------------------

    // The journal does not accept a write: nothing is moved, and the session stops (no later item is reached).
    [TestMethod]
    public async Task AJournalThatCannotBeWrittenMovesNothingAndStops()
    {
        using var b = new TrashBench("trash-journal-off");
        var a = b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        IReadOnlyList<TrashOutcome> outcomes;
        using (FileSwitches.ReadOnly(b.JournalPath))
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")));
        }

        Assert.AreEqual(0, b.Recycler.Paths.Count, "nothing moved: " + b.Log);
        InPlace(a, "first item");
        InPlace(c, "second item");
        Check.False(b.Log.Mentions(c), "the session stopped before the second item: " + b.Log);
        Check.AtMost(1, outcomes.Count, "at most the first item is reached");
        foreach (var outcome in outcomes)
        {
            Assert.AreEqual(TrashResult.Skipped, outcome.Result);
            Assert.AreEqual(Text.JournalUnavailable, outcome.Reason);
            Assert.IsNull(outcome.Entry, "no entry could be written");
        }

        Assert.AreEqual(0, b.Entries().Count, "the read-only journal is unchanged");
    }

    // Step 5 comes after step 4 and before step 6: a journal that stops accepting writes during the in-use check stops
    // the move of that very item.
    [TestMethod]
    public async Task TheJournalIsCheckedAfterInUseAndBeforeTheMove()
    {
        using var b = new TrashBench("trash-journal-late");
        var a = b.File("a.bin");
        var tree = await b.ScanAsync();
        IDisposable? readOnly = null;
        b.Gate.AfterInUse = _ => readOnly ??= FileSwitches.ReadOnly(b.JournalPath);
        try
        {
            b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin")));
        }
        finally
        {
            readOnly?.Dispose();
        }

        Check.True(b.Log.Of("inuse").Count == 1, "the in-use check ran: " + b.Log);
        Assert.AreEqual(0, b.Recycler.Paths.Count, "the journal check before the move stopped it: " + b.Log);
        InPlace(a, "the item");
    }

    // Step 8: "If that write fails after the move, the session stops".
    [TestMethod]
    public async Task AJournalWriteThatFailsAfterTheMoveStopsTheSession()
    {
        using var b = new TrashBench("trash-journal-after");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        IDisposable? readOnly = null;
        b.Recycler.AfterRecycle = (_, _) => readOnly ??= FileSwitches.ReadOnly(b.JournalPath);
        IReadOnlyList<TrashOutcome> outcomes;
        try
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")));
        }
        finally
        {
            readOnly?.Dispose();
        }

        var outcome = Single(outcomes);
        Assert.IsNull(outcome.Entry, "the entry could not be written");
        Assert.AreEqual(1, b.Recycler.Paths.Count, "one move");
        Check.False(b.Log.Mentions(c), "the session stopped: " + b.Log);
        InPlace(c, "the second item");
    }

    // --- Steps 6–7: post-verify (Windows note) ---------------------------------------------------------------------

    // The reported item has the approved volume serial, file id and logical size → trashed; every field journaled.
    [TestMethod]
    public async Task AReportedItemThatMatchesIsTrashedAndJournaled()
    {
        using var b = new TrashBench("trash-ok");
        var a = b.File(@"docs\a.bin", 10_000);
        var tree = await b.ScanAsync();
        var node = Trees.Node(tree, @"docs\a.bin");
        var stamp = KitObjects.RealStamp(a);
        var parentReal = KitObjects.RealFacts(Path.GetDirectoryName(a)!).CanonicalPath!;
        var session = TrashBench.Session(LeanModule.LargeOld, new SessionItem(node, null));

        var outcome = Single(b.Run(session));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, outcome.Reason);
        Check.False(File.Exists(a), "moved");
        var reported = b.Recycler.Paths.Count == 1 ? Directory.GetFiles(b.Recycler.BinFolder).Single() : null;
        Assert.IsNotNull(reported);
        var entry = outcome.Entry!;
        Assert.AreEqual(entry, b.Entries().Single(), "the outcome carries the entry written");
        Assert.AreEqual(JournalResult.Trashed, entry.Result);
        Assert.AreEqual(session.Id, entry.SessionId);
        Assert.AreEqual(LeanModule.LargeOld, entry.Module);
        Assert.AreEqual(node.Path, entry.OriginalPath, "canonical path at delete time");
        Check.True(string.Equals(parentReal, entry.OriginalParentRealPath, StringComparison.OrdinalIgnoreCase), $"parent real path: {entry.OriginalParentRealPath} vs {parentReal}");
        Check.True(string.Equals(a, entry.ApprovedRealPath, StringComparison.OrdinalIgnoreCase), "approved real path: " + entry.ApprovedRealPath);
        Check.True(string.Equals(reported, entry.TrashPath, StringComparison.OrdinalIgnoreCase), $"trashPath is the reported item: {entry.TrashPath} vs {reported}");
        Assert.AreEqual(new TrashedIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId, 10_000), entry.TrashedIdentity, "volume serial, file id, logical size");
        Assert.IsNotNull(entry.Bytes, "bytes measured before the move");
        Check.False(string.IsNullOrWhiteSpace(entry.AppVersion), "app version");
        Check.False(string.IsNullOrWhiteSpace(entry.Id), "entry id");
    }

    // A folder: the reported folder's identity and its logical size (Lean's scanner over the bin item) match → trashed.
    [TestMethod]
    public async Task AFolderThatMatchesIsTrashed()
    {
        using var b = new TrashBench("trash-folder");
        b.File(@"proj\a.bin", 3000);
        b.File(@"proj\deep\b.bin", 7000);
        var tree = await b.ScanAsync();
        var stamp = KitObjects.RealStamp(b.ItemPath("proj"));

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj"))));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, outcome.Reason);
        Assert.AreEqual(new TrashedIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId, 10_000), outcome.Entry!.TrashedIdentity);
    }

    // No item reported and the original still in place with its approved identity → skipped; the session goes on.
    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, unchecked((int)0x80070020))] // the shell gave up with a sharing violation
    public async Task NoReportAndTheOriginalInPlaceIsSkipped(bool performed, int errorCode)
    {
        using var b = new TrashBench("trash-left");
        var a = b.File("a.bin");
        b.File("b.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p => p.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase) ? new RecycleReport(performed, null, false, errorCode) : b.Recycler.MoveToBin(p);

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "b.bin")));

        Assert.AreEqual(2, outcomes.Count, "the session goes on");
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result, outcomes[0].Reason);
        Check.False(string.IsNullOrWhiteSpace(outcomes[0].Reason), "a skip says why");
        InPlace(a, "left in place");
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, outcomes[1].Reason);
        Assert.AreEqual(JournalResult.Skipped, b.Entries()[0].Result);
    }

    // AbortedNonRecycle (Lean's own abort) → skipped "Changed since the scan" (intent Outcome 6), original in place.
    [TestMethod]
    public async Task LeansOwnAbortIsSkippedAsChangedSinceTheScan()
    {
        using var b = new TrashBench("trash-abort");
        var a = b.File(@"tree\a.bin");
        b.File("b.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p => p.EndsWith("tree", StringComparison.OrdinalIgnoreCase) ? new RecycleReport(false, null, true, unchecked((int)0x80004004)) : b.Recycler.MoveToBin(p);

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "tree"), TrashBench.Item(tree, "b.bin")));

        Assert.AreEqual(2, outcomes.Count, "the session goes on");
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result);
        Assert.AreEqual(ChangedSinceScan, outcomes[0].Reason);
        InPlace(a, "the folder's file");
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, outcomes[1].Reason);
    }

    // A reported item with another identity → lost, and the session stops (later items are not reached).
    [TestMethod]
    public async Task AReportedItemWithAnotherIdentityIsLostAndStops()
    {
        using var b = new TrashBench("trash-lost-identity");
        var a = b.File("a.bin", 4096, seed: 5);
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            // The shell deleted the item for good and reported some other file of the same size.
            File.Delete(p);
            var other = b.Recycler.NextBinPath(p);
            TempFolder.WriteBytes(other, 4096, seed: 5);
            return new RecycleReport(true, other, false, 0);
        };

        AssertLostAndStopped(b, b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"))), c);
        Check.False(File.Exists(a), "the fixture deleted it");
    }

    // A reported item with the approved identity but another logical size → lost (a file and a folder).
    [TestMethod]
    [DataRow("a.bin")]
    [DataRow("proj")]
    public async Task AReportedItemWithAnotherSizeIsInTheBinNotAsApprovedAndStops(string item)
    {
        using var b = new TrashBench("trash-lost-size");
        b.File("a.bin", 4096);
        b.File(@"proj\x.bin", 4096);
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.AfterRecycle = (_, report) =>
        {
            // Same file id in the bin, but not the bytes that were approved.
            if (Directory.Exists(report.ReportedNewItemPath))
            {
                TempFolder.WriteBytes(Path.Combine(report.ReportedNewItemPath!, "extra.bin"), 100, 1);
            }
            else
            {
                File.AppendAllText(report.ReportedNewItemPath!, "more");
            }
        };

        AssertMismatchAndStopped(b, b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, item), TrashBench.Item(tree, "c.bin"))), c);
    }

    // Owner decision 2026-10-17 (main session, test changed with the decision): the approved item reached the Recycle Bin
    // (same identity) but not as approved. It is not called deleted: trashed, with "The moved item did not match the
    // approved item — check the Recycle Bin", journaled where it is with the size it has there; the session stops.
    internal static void AssertMismatchAndStopped(TrashBench b, IReadOnlyList<TrashOutcome> outcomes, string laterItem)
    {
        var outcome = Single(outcomes);
        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        Assert.AreEqual(Text.MovedItemDidNotMatch, outcome.Reason);
        Assert.AreEqual(1, b.Recycler.Paths.Count, "one move, then the session stopped");
        Check.False(b.Log.Mentions(laterItem), "the later item was not reached: " + b.Log);
        InPlace(laterItem, "the later item");
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Trashed, entry.Result);
        Assert.AreEqual(Text.MovedItemDidNotMatch, entry.Reason);
        Assert.IsNotNull(entry.TrashPath, "journaled where it is");
        Assert.AreEqual(Ambysto.Lean.Core.Trash.RecycledItem.Measure(entry.TrashPath!), entry.TrashedIdentity?.LogicalBytes, "the size it has in the bin");
        Assert.AreEqual(entry, outcome.Entry);
    }

    // The identity of the reported item is the one IItemGate.Facts reads (post-verify uses the gate's facts).
    [TestMethod]
    public async Task PostVerifyUsesTheFactsTheGateReads()
    {
        using var b = new TrashBench("trash-lost-facts");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.AfterRecycle = (_, report) =>
        {
            var path = report.ReportedNewItemPath!;
            b.Gate.FactsFor(path, p =>
            {
                var real = KitObjects.RealFacts(p);
                return KitObjects.FactsOk(real.CanonicalPath!, KitObjects.WithOtherIdentity(real.Stamp!.Value));
            });
        };

        AssertLostAndStopped(b, b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"))), c);
    }

    // No item reported and the original gone → lost (the shell deleted it for good while reporting success).
    [TestMethod]
    public async Task NoReportAndTheOriginalGoneIsLost()
    {
        using var b = new TrashBench("trash-lost-gone");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            File.Delete(p);
            return new RecycleReport(true, null, false, 0);
        };

        AssertLostAndStopped(b, b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"))), c);
    }

    // A reported path where nothing is → lost.
    [TestMethod]
    public async Task AReportedItemThatIsNotThereIsLost()
    {
        using var b = new TrashBench("trash-lost-missing");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            File.Delete(p);
            return new RecycleReport(true, b.Recycler.NextBinPath(p), false, 0);
        };

        AssertLostAndStopped(b, b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"))), c);
    }

    // "Left in place" needs the approved identity at the original path: another file there now (same name, same bytes)
    // means the approved item is gone → lost, never skipped.
    [TestMethod]
    public async Task NoReportAndAnotherFileAtTheOriginalPathIsLost()
    {
        using var b = new TrashBench("trash-lost-replaced");
        b.File("a.bin", 4096, seed: 6);
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            File.Delete(p);
            TempFolder.WriteBytes(p, 4096, seed: 6);
            return new RecycleReport(true, null, false, 0);
        };

        AssertLostAndStopped(b, b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin"))), c);
    }

    private static void AssertLostAndStopped(TrashBench b, IReadOnlyList<TrashOutcome> outcomes, string laterItem)
    {
        var outcome = Single(outcomes);
        Assert.AreEqual(TrashResult.Lost, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        Assert.AreEqual(Text.NotFoundIntactInRecycleBin, outcome.Reason, "the deletion report's words (docs/spec/07 Windows note)");
        Assert.AreEqual(1, b.Recycler.Paths.Count, "one move, then the session stopped");
        Check.False(b.Log.Mentions(laterItem), "the later item was not reached: " + b.Log);
        InPlace(laterItem, "the later item");
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Lost, entry.Result, "journaled lost");
        Assert.AreEqual(Text.DeletedPermanentlyByWindows, entry.Reason, "the journal's words for lost (docs/spec/05)");
        Assert.AreEqual(entry, outcome.Entry);
    }

    // --- Step 8 and the session -----------------------------------------------------------------------------------

    // Every item reached produces exactly one entry, whatever its outcome.
    [TestMethod]
    public async Task EveryItemReachedHasExactlyOneEntry()
    {
        using var b = new TrashBench("trash-one-each");
        var names = new[] { "kept-changed.bin", "refused.bin", "in-use.bin", "moved.bin", "left.bin" };
        foreach (var name in names)
        {
            b.File(name, 4096, seed: 7);
        }

        var keep = b.File("keep.bin", 4096, seed: 7);
        var keep2 = b.File("keep2.bin", 4096, seed: 7);
        var tree = await b.ScanAsync();
        b.Gate.OnCheck = r => r.Path.EndsWith("refused.bin", StringComparison.OrdinalIgnoreCase) ? KitObjects.Refuse(RefusalReason.Protected) : KitObjects.ApproveNow(r.Path);
        b.Gate.OnInUse = i => i.CanonicalPath.EndsWith("in-use.bin", StringComparison.OrdinalIgnoreCase) ? KitObjects.InUse([new InUseHolder(1, "x.exe", "file")]) : KitObjects.NotInUse();
        b.Recycler.OnRecycle = p => p.EndsWith("left.bin", StringComparison.OrdinalIgnoreCase) ? new RecycleReport(true, null, false, 0) : b.Recycler.MoveToBin(p);
        var session = TrashBench.Session(LeanModule.Duplicates,
            TrashBench.Item(tree, "kept-changed.bin", keep: "keep2.bin"),
            TrashBench.Item(tree, "refused.bin", keep: "keep.bin"),
            TrashBench.Item(tree, "in-use.bin", keep: "keep.bin"),
            TrashBench.Item(tree, "moved.bin", keep: "keep.bin"),
            TrashBench.Item(tree, "left.bin", keep: "keep.bin"));
        File.AppendAllText(keep2, "changed");

        var outcomes = b.Run(session);

        CollectionAssert.AreEqual(new[] { TrashResult.Skipped, TrashResult.Skipped, TrashResult.Skipped, TrashResult.Trashed, TrashResult.Skipped }, outcomes.Select(o => o.Result).ToArray(),
            string.Join(", ", outcomes.Select(o => o.Item.Node.Name + "=" + o.Result + " " + o.Reason)));
        var entries = b.Entries();
        Assert.AreEqual(names.Length, entries.Count, "one entry per item");
        Check.Sequence(entries.Select(e => Path.GetFileName(e.OriginalPath)), names);
        Assert.AreEqual(entries.Count, entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count(), "entry ids are unique");
        Check.True(entries.All(e => e.SessionId == session.Id), "every entry has the session id");
        Check.True(entries.All(e => e.Module == LeanModule.Duplicates), "every entry has the module");
        for (var i = 0; i < outcomes.Count; i++)
        {
            Assert.AreEqual(entries[i], outcomes[i].Entry, "outcome " + i + " carries its entry");
            if (outcomes[i].Result == TrashResult.Skipped)
            {
                Assert.AreEqual(outcomes[i].Reason, entries[i].Reason, "skip reason journaled");
            }

            Assert.AreSame(session.Items[i], outcomes[i].Item, "outcomes in session order");
        }

        InPlace(keep, "the kept copy is never moved");
    }

    // A cancel before the run reaches no item.
    [TestMethod]
    public async Task ACancelBeforeTheRunReachesNoItem()
    {
        using var b = new TrashBench("trash-cancel-first");
        var a = b.File("a.bin");
        var tree = await b.ScanAsync();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin")), cancel.Token);

        Assert.AreEqual(0, outcomes.Count);
        Assert.AreEqual(0, b.Log.All.Count, "nothing asked, nothing moved: " + b.Log);
        InPlace(a, "the item");
        Assert.AreEqual(0, b.Entries().Count);
    }

    // A cancel stops before the next item, never inside one: cancelled during item 1's gate call, item 1 completes
    // (moved, verified, journaled) and item 2 is not reached.
    [TestMethod]
    public async Task ACancelStopsBeforeTheNextItemNeverInsideOne()
    {
        using var b = new TrashBench("trash-cancel-mid");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        using var cancel = new CancellationTokenSource();
        b.Gate.AfterCheck = _ => cancel.Cancel();

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")), cancel.Token);

        var outcome = Single(outcomes);
        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "item 1 finished: " + outcome.Reason);
        Assert.AreEqual(1, b.Entries().Count, "and was journaled");
        Check.False(b.Log.Mentions(c), "item 2 not reached: " + b.Log);
        InPlace(c, "item 2");
    }
}
