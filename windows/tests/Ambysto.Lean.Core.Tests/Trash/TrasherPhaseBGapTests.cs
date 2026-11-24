using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Trasher behaviours of the phase B gaps (briefs T1–T6): docs/spec/02 "Order per item" and its Windows note on
/// steps 6–7 (four post-verify outcomes; "anything else → <c>lost</c>, shown in the report, and the session stops"),
/// step 1 (the kept copy must still exist unchanged), step 2 ("A symbolic link is never followed"), step 8 ("Every item
/// produces exactly one entry"); intent Outcome 6 ("Left in place … with the reason of its cause") and criterion 2;
/// docs/spec/05 <c>trashedIdentity</c> and <c>lost</c>; docs/spec/07 ("The UI states what happened, never more"); review
/// log, groups 10–11 interim review, MINOR 4 ("post-verify errors count as 'not found intact'") and MAJOR 1 (one measure on
/// both sides of the move). The world is <see cref="TrashBench"/>: real files, a stand-in Recycle Bin on the same volume,
/// a real journal, the gate and shell doubles.</summary>
[TestClass]
public sealed class TrasherPhaseBGapTests
{
    private static string Describe(IReadOnlyList<TrashOutcome> outcomes) =>
        string.Join(", ", outcomes.Select(o => o.Item.Node.Name + "=" + o.Result + " (" + o.Reason + ")"));

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string[] JournalLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
    }

    private static Exception Failure(string kind) => kind switch
    {
        "IOException" => new IOException("test: the facts read failed"),
        "UnauthorizedAccessException" => new UnauthorizedAccessException("test: access denied"),
        "Win32Exception" => new Win32Exception(5, "test: access denied"),
        "InvalidOperationException" => new InvalidOperationException("test: invalid state"),
#pragma warning disable CA2201 // a COM failure as the shell's interop raises it is what this row simulates
        "COMException" => new COMException("test: the shell failed", unchecked((int)0x80004005)),
#pragma warning restore CA2201
        // Hard review 2026-10-18: a kind outside the five above must end the same way (any failure after a move).
        "NotSupportedException" => new NotSupportedException("test: an unexpected kind"),
        "ArgumentException" => new ArgumentException("test: an unexpected kind"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // T1 [top]: the facts read of the reported $R item throws (each kind a post-verify can meet): Run returns without
    // throwing; the item is lost ("Not found intact in the Recycle Bin"), journaled exactly once as lost with the journal's
    // words, and the session stops before the next item.
    [TestMethod]
    [DataRow("IOException")]
    [DataRow("UnauthorizedAccessException")]
    [DataRow("Win32Exception")]
    [DataRow("InvalidOperationException")]
    [DataRow("COMException")]
    [DataRow("NotSupportedException")]
    [DataRow("ArgumentException")]
    public async Task AnErrorDuringPostVerifyIsLostJournaledOnceAndStops(string kind)
    {
        using var b = new TrashBench("trash-verify-throws");
        b.File("a.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        b.Recycler.AfterRecycle = (path, report) =>
        {
            if (path.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase) && report.ReportedNewItemPath is { } reported)
            {
                b.Gate.FactsFor(reported, _ => throw Failure(kind));
            }
        };

        IReadOnlyList<TrashOutcome> outcomes;
        try
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "c.bin")));
        }
        catch (Exception e) when (e is not AssertFailedException)
        {
            Assert.Fail($"Run threw {e.GetType().Name} from post-verify ({kind}): {e.Message}");
            return;
        }

        Assert.AreEqual(1, outcomes.Count, "the session stopped after the lost item: " + Describe(outcomes));
        Assert.AreEqual(TrashResult.Lost, outcomes[0].Result, "reason: " + outcomes[0].Reason + "; calls: " + b.Log);
        Assert.AreEqual(Text.NotFoundIntactInRecycleBin, outcomes[0].Reason);
        var entries = b.Entries();
        Assert.AreEqual(1, entries.Count, "exactly one line for a.bin");
        Assert.AreEqual(JournalResult.Lost, entries[0].Result);
        Assert.AreEqual(Text.DeletedPermanentlyByWindows, entries[0].Reason);
        Assert.AreEqual(entries[0], outcomes[0].Entry, "the outcome carries the line written");
        Check.False(b.Log.Mentions(c), "c.bin was not reached: " + b.Log);
        Check.True(File.Exists(c), "c.bin stays in place");
        Assert.AreEqual(1, b.Recycler.Paths.Count, "one move");
    }

    // T2 [normal]: the shell reports no new item and leaves a.bin in place with its approved identity → skipped, with the
    // reason of its cause. (a) The in-use check says "not in use" at step 4 and "in use by notepad.exe (4242)" when asked
    // again after the move: the in-use words. (b) Not in use either time, error code 0x80070020: exactly the move-failed
    // words with that code, not the in-use words. The skip is journaled with the same reason and the next item moves.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ALeftInPlaceItemNamesItsCause(bool inUseAfterTheMove)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        using var b = new TrashBench("trash-left-cause");
        var a = b.File("a.bin");
        b.File("b.bin");
        var tree = await b.ScanAsync();
        var callsForA = 0;
        b.Gate.OnInUse = item =>
        {
            if (!item.CanonicalPath.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase))
            {
                return KitObjects.NotInUse();
            }

            callsForA++;
            return inUseAfterTheMove && callsForA >= 2 ? KitObjects.InUse([new InUseHolder(4242, "notepad.exe", "file")]) : KitObjects.NotInUse();
        };
        b.Recycler.OnRecycle = p => p.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase) ? new RecycleReport(false, null, false, SharingViolation) : b.Recycler.MoveToBin(p);

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "b.bin")));

        Assert.AreEqual(2, outcomes.Count, "the session goes on: " + Describe(outcomes));
        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result, outcomes[0].Reason);
        Check.True(File.Exists(a), "a.bin left in place");
        var reason = outcomes[0].Reason ?? "";
        if (inUseAfterTheMove)
        {
            Check.AtLeast(2, callsForA, "the in-use check was asked again after the move; calls: " + b.Log);
            Check.True(reason.StartsWith(Text.InUseBy("").TrimEnd(), StringComparison.Ordinal), "the in-use words: " + reason);
            Check.Contains(reason, "notepad.exe");
            Check.Contains(reason, "4242");
        }
        else
        {
            Assert.AreEqual(Text.MoveFailed(SharingViolation), reason, "the move-failed words with the shell's code");
            Check.False(reason.StartsWith(Text.InUseBy("").TrimEnd(), StringComparison.Ordinal), "not the in-use words: " + reason);
        }

        var entries = b.Entries();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(JournalResult.Skipped, entries[0].Result);
        Assert.AreEqual(reason, entries[0].Reason, "journaled with the same reason");
        Assert.AreEqual(TrashResult.Trashed, outcomes[1].Result, "the next item is moved: " + outcomes[1].Reason);
    }

    // T3 [top]: docs/spec/02 step 1: the kept copy must still exist unchanged (same identity, size, modification time). A
    // kept copy whose node has no scan identity is no proof, even when the file is on disk unchanged: the duplicate is
    // skipped "The copy you are keeping changed or is gone", the gate is never asked, nothing moves, one skipped line.
    // Control: with the kept copy's scan stamp the duplicate is moved.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AKeptCopyWithoutAScanIdentityIsNoProof(bool withoutStamp)
    {
        using var b = new TrashBench("trash-kept-nostamp");
        var a = b.File("a.bin", 8192, seed: 14);
        var keep = b.File("keep.bin", 8192, seed: 14);
        var tree = await b.ScanAsync();
        var item = TrashBench.Item(tree, "a.bin", keep: "keep.bin");
        Assert.IsNotNull(item.KeptCopy!.Stamp, "precondition: the scan recorded the kept copy's identity");
        if (withoutStamp)
        {
            item.KeptCopy.Stamp = null;
        }

        var outcomes = b.Run(TrashBench.Session(LeanModule.Duplicates, item));

        Assert.AreEqual(1, outcomes.Count, Describe(outcomes));
        Check.True(File.Exists(keep), "the kept copy is never moved");
        if (!withoutStamp)
        {
            Assert.AreEqual(TrashResult.Trashed, outcomes[0].Result, "control: " + outcomes[0].Reason);
            return;
        }

        Assert.AreEqual(TrashResult.Skipped, outcomes[0].Result, "reason: " + outcomes[0].Reason + "; calls: " + b.Log);
        Assert.AreEqual(Text.KeptCopyChanged, outcomes[0].Reason);
        Assert.AreEqual(0, b.Log.Of("check").Count, "the gate was never asked: " + b.Log);
        Assert.AreEqual(0, b.Recycler.Paths.Count, "nothing moved");
        Check.True(File.Exists(a), "the duplicate stays");
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Skipped, entry.Result);
        Assert.AreEqual(Text.KeptCopyChanged, entry.Reason);
    }

    // T4 [low]: docs/spec/05 defines trashedIdentity as "device, inode and size of the item in the Trash", and lost as "the
    // move was made and the item is not in the Recycle Bin": a lost line carries no Recycle Bin identity (null when read
    // back; no field in the JSON line, whose nulls are not written). Rows: a reported item with another identity, and no
    // report with the original gone. (Reading not confirmed with the main session: this test writer cannot ask it.)
    [TestMethod]
    [DataRow("another-identity")]
    [DataRow("original-gone")]
    public async Task ALostLineCarriesNoRecycleBinIdentity(string how)
    {
        using var b = new TrashBench("trash-lost-noidentity");
        b.File("a.bin", 4096, seed: 15);
        var tree = await b.ScanAsync();
        b.Recycler.OnRecycle = p =>
        {
            File.Delete(p);
            if (how == "original-gone")
            {
                return new RecycleReport(true, null, false, 0);
            }

            var other = b.Recycler.NextBinPath(p);
            TempFolder.WriteBytes(other, 4096, 15);
            return new RecycleReport(true, other, false, 0);
        };

        var outcome = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "a.bin"))).Single();

        Assert.AreEqual(TrashResult.Lost, outcome.Result, outcome.Reason);
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Lost, entry.Result);
        Assert.IsNull(entry.TrashedIdentity, "a lost line has no Recycle Bin identity: " + entry.TrashedIdentity);
        var line = JournalLines(b.JournalPath).Single();
        using var doc = JsonDocument.Parse(line);
        Check.False(doc.RootElement.TryGetProperty("trashedIdentity", out _), "no trashedIdentity field in the lost line: " + line);
    }

    // T5 [normal]: the journal is read-only from the start; the first item is refused by the gate ("Changed since the
    // scan"). Its outcome is skipped with the gate's message, never the "Moved to the Recycle Bin as …" words, with no entry,
    // and it stays in place. The next, approved item is not moved (the journal check of step 5) and the session stops.
    [TestMethod]
    public async Task ASkipThatCannotBeJournaledNeverSaysMoved()
    {
        using var b = new TrashBench("trash-skip-nojournal");
        var a = b.File("a.bin");
        var second = b.File("b.bin");
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        var refusal = KitObjects.Refuse(RefusalReason.ChangedSinceScan);
        b.Gate.OnCheck = r => r.Path.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase) ? refusal : KitObjects.ApproveNow(r.Path);
        IReadOnlyList<TrashOutcome> outcomes;
        using (FileSwitches.ReadOnly(b.JournalPath))
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, "a.bin"), TrashBench.Item(tree, "b.bin"), TrashBench.Item(tree, "c.bin")));
        }

        Check.AtLeast(1, outcomes.Count, "the first item was reached");
        var first = outcomes[0];
        Assert.AreEqual(TrashResult.Skipped, first.Result, first.Reason);
        Assert.AreEqual(refusal.Message, first.Reason, "the gate's message");
        var movedWords = Text.MovedButNotJournaled("|")[..Text.MovedButNotJournaled("|").IndexOf('|', StringComparison.Ordinal)];
        Check.False((first.Reason ?? "").StartsWith(movedWords, StringComparison.Ordinal), "never the moved words: " + first.Reason);
        Assert.IsNull(first.Entry, "no entry could be written");
        Assert.AreEqual(0, b.Recycler.Paths.Count, "nothing moved: " + b.Log);
        Check.True(File.Exists(a) && File.Exists(second) && File.Exists(c), "every item stays");
        Check.AtMost(2, outcomes.Count, "the session stops at the latest after the next item: " + Describe(outcomes));
        if (outcomes.Count == 2)
        {
            Assert.AreEqual(TrashResult.Skipped, outcomes[1].Result, outcomes[1].Reason);
            Assert.AreEqual(Text.JournalUnavailable, outcomes[1].Reason, "the next item stops at the journal check");
            Assert.IsNull(outcomes[1].Entry);
        }

        Check.False(b.Log.Mentions(c), "the session stopped before the third item: " + b.Log);
        Assert.AreEqual(0, b.Entries().Count, "the read-only journal is unchanged");
    }

    // T6 [low]: docs/spec/02 step 2 "A symbolic link is never followed"; review MAJOR 1: one measure on both sides of the
    // move. A folder holding a plain 4 KB file, a file symbolic link with the relative target ..\outside.bin (1 MB), and a
    // dangling file symbolic link, moved into the stand-in bin (where the relative link resolves elsewhere): trashed, no
    // "did not match", journaled logical size 4 KB plus the links' own lengths (0).
    [TestMethod]
    public async Task FileSymbolicLinksInAFolderAreNotFollowedWhenMeasured()
    {
        using var b = new TrashBench("trash-symlinks");
        b.File(@"proj\plain.bin", 4096, seed: 16);
        b.File("outside.bin", 1024 * 1024, seed: 17);
        try
        {
            File.CreateSymbolicLink(b.ItemPath(@"proj\link.bin"), @"..\outside.bin");
            File.CreateSymbolicLink(b.ItemPath(@"proj\dangling.bin"), "nowhere.bin");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("this user may not create symbolic links (Developer Mode off, no admin fixture): " + e.Message);
        }

        Assert.AreEqual(1024 * 1024, new FileInfo(Path.Combine(b.Items, "outside.bin")).Length, "precondition: the link's target is 1 MB");
        Check.True(File.Exists(b.ItemPath(@"proj\link.bin")), "precondition: the link resolves before the move");
        var tree = await b.ScanAsync();

        var outcome = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj"))).Single();

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        Assert.AreNotEqual(Text.MovedItemDidNotMatch, outcome.Reason, "the links were measured the same on both sides");
        Assert.AreEqual(4096L, outcome.Entry!.TrashedIdentity!.Value.LogicalBytes, "4 KB plus the links' own lengths (0)");
        Check.True(File.Exists(Path.Combine(b.Items, "outside.bin")), "the link's target is untouched");
    }
}
