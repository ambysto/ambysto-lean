using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Journal;

/// <summary>Put back behaviours of the phase B gaps (briefs P1–P6, and the decision of 438f19f): docs/spec/05 "Put back"
/// and its Windows note (the Kit's environment check first; the table's checks and their Recycle Bin words; "Put back all
/// from this session"; <c>trashPath</c> is "the <c>$R</c> item in <c>$Recycle.Bin\&lt;user SID&gt;</c>"), docs/spec/07 (the
/// Kit's refusal messages as the Kit words them; the UI states what happened), docs/spec/04 (the session restores the
/// leftovers together), plan notes "Put back order" and "A session is put back last moved first". The gate is
/// <see cref="FakeItemGate"/>; the <c>$R</c> items are real stand-ins (<see cref="PutBackBench"/>).</summary>
[TestClass]
public sealed class PutBackPhaseBGapTests
{
    private static bool Exists(string? path) => path is not null && (File.Exists(path) || Directory.Exists(path));

    private static void Refused(PutBackBench b, PutBackOutcome outcome, string? reason, JournalEntry entry, int linesBefore)
    {
        Check.False(outcome.Restored, "not restored; reason: " + outcome.Reason);
        Assert.AreEqual(reason, outcome.Reason);
        var entries = b.Entries();
        Assert.AreEqual(linesBefore + 1, entries.Count, "one line appended");
        Assert.AreEqual(JournalResult.RestoreFailed, entries[^1].Result, "journaled restoreFailed");
        Assert.AreEqual(reason, entries[^1].Reason, "with its reason");
        Check.True(string.Equals(entry.OriginalPath, entries[^1].OriginalPath, StringComparison.OrdinalIgnoreCase), "for the same item");
    }

    // P1 [normal]: only the environment refusal of the first restore check comes first. A CouldNotVerify refusal with the
    // detail "environment" (the environment query failing) is acted on there: its own message, journaled restoreFailed.
    // A CouldNotVerify refusal about the destination is not: the table's first reason ($R item deleted) is shown.
    [TestMethod]
    [DataRow("environment", true)]
    [DataRow("destination", false)]
    [DataRow("destination folder", false)]
    public void AnEnvironmentQueryFailureComesFirstButADestinationFailureDoesNot(string detail, bool environment)
    {
        using var b = new PutBackBench("putback-env-query");
        var entry = b.Stage("a.bin");
        File.Delete(entry.TrashPath!);
        var refusal = KitObjects.Refuse(RefusalReason.CouldNotVerify, detail);
        b.Gate.OnRestore = _ => refusal;
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        Refused(b, outcome, environment ? refusal.Message : Text.NoLongerInRecycleBin, entry, before);
        Check.False(File.Exists(entry.OriginalPath), "nothing at the original path");
    }

    // P2 [normal]: an entry that is not trashed gets "Nothing to put back" and no line; a lost one says it was deleted
    // permanently by Windows, and no line either.
    [TestMethod]
    [DataRow(JournalResult.Skipped)]
    [DataRow(JournalResult.Restored)]
    [DataRow(JournalResult.RestoreFailed)]
    [DataRow(JournalResult.Lost)]
    public void ANonTrashedEntryIsNothingToPutBack(JournalResult result)
    {
        using var b = new PutBackBench("putback-nothing");
        var staged = b.Stage("a.bin");
        var entry = b.Add(staged with { Id = "other-" + result, Result = result, Reason = result == JournalResult.Lost ? Text.DeletedPermanentlyByWindows : "why" });
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        Check.False(outcome.Restored, result + " entry restored");
        Assert.AreEqual(result == JournalResult.Lost ? Text.DeletedPermanentlyByWindows : Text.NothingToPutBack, outcome.Reason);
        Assert.AreEqual(before, b.Entries().Count, "no line appended");
        Check.True(File.Exists(staged.TrashPath), "the $R item stays");
        Check.False(File.Exists(staged.OriginalPath), "nothing at the original path");
    }

    /// <summary>One session journaled in move order: <c>orig\Folder\inner.bin</c> (trashed first, its own stand-in), then
    /// <c>orig\Folder</c> (trashed later; its stand-in holds the folder's other file). Both stand-ins are same-volume moves,
    /// so they keep their file ids; their facts report them inside this user's Recycle Bin folder.</summary>
    private static (JournalEntry Inner, JournalEntry Folder) StageFolderAndInner(PutBackBench b)
    {
        var folder = Path.Combine(b.Orig, "Folder");
        Directory.CreateDirectory(folder);
        var inner = Path.Combine(folder, "inner.bin");
        TempFolder.WriteBytes(inner, 4096, 61);
        TempFolder.WriteBytes(Path.Combine(folder, "other.bin"), 3000, 62);

        var innerParent = KitObjects.RealFacts(folder).CanonicalPath!;
        var innerStandIn = Path.Combine(b.Bin, "$R0000A1.bin");
        File.Move(inner, innerStandIn);
        var innerStamp = KitObjects.RealStamp(innerStandIn);
        var innerEntry = new JournalEntry(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, LeanModule.Uninstall, inner, innerParent, inner, 4096,
            innerStandIn, new TrashedIdentity(innerStamp.Identity.VolumeSerial, innerStamp.Identity.FileId, 4096), JournalResult.Trashed, null, "test");

        var folderParent = KitObjects.RealFacts(b.Orig).CanonicalPath!;
        var (logical, _) = KitObjects.Measure(folder);
        var folderStandIn = Path.Combine(b.Bin, "$R0000A2");
        Directory.Move(folder, folderStandIn);
        var folderStamp = KitObjects.RealStamp(folderStandIn);
        var folderEntry = new JournalEntry(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, LeanModule.Uninstall, folder, folderParent, folder, logical,
            folderStandIn, new TrashedIdentity(folderStamp.Identity.VolumeSerial, folderStamp.Identity.FileId, logical), JournalResult.Trashed, null, "test");

        b.Add(innerEntry);
        b.Add(folderEntry);
        b.ReportInBin(innerStandIn);
        b.ReportInBin(folderStandIn);
        return (innerEntry, folderEntry);
    }

    // P3 [normal]: "Put back all from this session" restores last moved first, so the file finds its folder: both are
    // restored and inner.bin ends inside the restored folder. Control: the inner entry alone, before the folder, is refused
    // "The original folder no longer exists or moved".
    [TestMethod]
    public void ASessionIsPutBackLastMovedFirst()
    {
        using (var control = new PutBackBench("putback-order-control"))
        {
            var (inner, _) = StageFolderAndInner(control);
            var before = control.Entries().Count;
            Refused(control, control.PutBack().Restore(inner), Text.OriginalFolderGone, inner, before);
        }

        using var b = new PutBackBench("putback-order");
        var (innerEntry, folderEntry) = StageFolderAndInner(b);

        var outcomes = b.PutBack().RestoreSession("s1");

        Assert.AreEqual(2, outcomes.Count, string.Join(", ", outcomes.Select(o => Path.GetFileName(o.Entry.OriginalPath) + "=" + o.Restored + " " + o.Reason)));
        Check.True(outcomes.All(o => o.Restored), string.Join(", ", outcomes.Select(o => Path.GetFileName(o.Entry.OriginalPath) + ": " + o.Reason)));
        Check.True(Directory.Exists(folderEntry.OriginalPath), "the folder is back");
        Check.True(File.Exists(innerEntry.OriginalPath), "inner.bin is back inside the folder");
        Assert.AreEqual(innerEntry.TrashedIdentity!.Value.FileId, Native.IdentityOf(innerEntry.OriginalPath).FileId, "the same inner.bin");
        Check.True(File.Exists(Path.Combine(folderEntry.OriginalPath, "other.bin")), "the folder's other file came back with it");
    }

    // P4 [low]: the $R item's facts cannot be read (CouldNotVerify): "The item in the Recycle Bin is a different file",
    // restoreFailed, nothing moved. Control: the Kit's "already gone" gives "No longer in the Recycle Bin".
    [TestMethod]
    [DataRow(RefusalReason.CouldNotVerify)]
    [DataRow(RefusalReason.AlreadyGone)]
    public void AnUnreadableRecycleBinItemIsADifferentFile(RefusalReason failure)
    {
        using var b = new PutBackBench("putback-unreadable");
        var entry = b.Stage("a.bin");
        b.Gate.FactsFor(entry.TrashPath!, _ => KitObjects.FactsFailed(failure));
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        Refused(b, outcome, failure == RefusalReason.AlreadyGone ? Text.NoLongerInRecycleBin : Text.DifferentFileInRecycleBin, entry, before);
        Check.True(File.Exists(entry.TrashPath), "the $R item stays");
        Check.False(File.Exists(entry.OriginalPath), "nothing at the original path");
    }

    // P5 [low]: a destination refusal outside the table (the restore check refuses on both calls) shows the Kit's own
    // words, not "Cannot restore to a protected location".
    [TestMethod]
    [DataRow(RefusalReason.UnsupportedVolume, null)]
    [DataRow(RefusalReason.LinkOrSpecialFile, null)]
    [DataRow(RefusalReason.CouldNotVerify, "destination")]
    public void ADestinationRefusalOutsideTheTableShowsTheKitsWords(RefusalReason reason, string? detail)
    {
        using var b = new PutBackBench("putback-kit-words");
        var entry = b.Stage("a.bin");
        var refusal = KitObjects.Refuse(reason, detail);
        b.Gate.OnRestore = _ => refusal;
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        Assert.AreNotEqual(Text.CannotRestoreProtected, outcome.Reason, "not the table's protected words");
        Refused(b, outcome, refusal.Message, entry, before);
        Check.True(File.Exists(entry.TrashPath), "the $R item stays");
        Check.False(File.Exists(entry.OriginalPath), "nothing at the original path");
    }

    // P6 [normal]: only a $R item directly inside <volume root>\$Recycle.Bin\<this user's SID> is put back. The stand-in's
    // facts report a canonical path, one row each: a $I record, desktop.ini, another user's SID, a folder named Recycler,
    // and a $R item one folder deeper; each is "Not in a Recycle Bin folder", nothing moved. Control: the $R item directly
    // in this user's folder is restored.
    [TestMethod]
    [DataRow("dollar-i")]
    [DataRow("desktop-ini")]
    [DataRow("other-sid")]
    [DataRow("recycler")]
    [DataRow("not-directly-inside")]
    [DataRow("control")]
    public void OnlyADollarRItemDirectlyInThisUsersRecycleBinIsPutBack(string row)
    {
        using var b = new PutBackBench("putback-binpath-" + row);
        var entry = b.Stage("a.bin", reportInBin: false);
        var standIn = entry.TrashPath!;
        var root = Path.GetPathRoot(Path.GetFullPath(standIn))!;
        var name = Path.GetFileName(standIn);
        var sid = PutBackBench.Sid;
        var reported = row switch
        {
            "dollar-i" => Path.Combine(root, "$Recycle.Bin", sid, "$I" + name[2..]),
            "desktop-ini" => Path.Combine(root, "$Recycle.Bin", sid, "desktop.ini"),
            "other-sid" => Path.Combine(root, "$Recycle.Bin", "S-1-5-21-1-2-3-1001", name),
            "recycler" => Path.Combine(root, "Recycler", sid, name),
            "not-directly-inside" => Path.Combine(root, "$Recycle.Bin", sid, "$Router", name),
            _ => Path.Combine(root, "$Recycle.Bin", sid, name),
        };
        b.Gate.FactsFor(standIn, p =>
        {
            var real = FileFacts.Read(p);
            return real.Succeeded ? KitObjects.FactsOk(reported, real.Stamp!.Value) : real;
        });
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        if (row == "control")
        {
            Check.True(outcome.Restored, "control: restored; reason: " + outcome.Reason);
            Check.True(File.Exists(entry.OriginalPath), "back at the original path");
            return;
        }

        Refused(b, outcome, Text.NotInRecycleBinFolder, entry, before);
        Check.True(File.Exists(standIn), "the $R stand-in stays: " + reported);
        Check.False(File.Exists(entry.OriginalPath), "nothing at the original path");
    }

    // Decision of 438f19f: if the restored line cannot be written after a move back, "Put back all from this session"
    // stops (as on a journal that cannot be written before the move). The bench makes the journal fail after the move and
    // before the line: the journal lives inside the last moved item's $R folder, so the move back takes it along, and the
    // stand-in Recycle Bin folder denies creating a folder, so the journal cannot be made again at its path. That item's
    // outcome says "Put back, but the journal could not be written", and the earlier item is not put back.
    [TestMethod]
    public void ARestoredLineThatCannotBeWrittenStopsTheSession()
    {
        using var b = new PutBackBench("putback-not-journaled");
        var earlier = b.Stage("b.bin", journal: false);

        var folder = Path.Combine(b.Orig, "Folder");
        Directory.CreateDirectory(folder);
        TempFolder.WriteBytes(Path.Combine(folder, "one.bin"), 3000, 71);
        var parentReal = KitObjects.RealFacts(b.Orig).CanonicalPath!;
        var standIn = Path.Combine(b.Bin, "$R0000F1");
        Directory.Move(folder, standIn);
        var stamp = KitObjects.RealStamp(standIn);
        const long Logical = 1_000_000; // the stand-in's measure once its journal and padding are written (fixed below)
        var last = new JournalEntry(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, LeanModule.Map, folder, parentReal, folder, Logical,
            standIn, new TrashedIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId, Logical), JournalResult.Trashed, null, "test");
        var journalPath = Path.Combine(standIn, "journal.jsonl");
        var journal = JournalStore.Open(journalPath);
        journal.Append(earlier);
        journal.Append(last);
        var pad = Path.Combine(standIn, "pad.bin");
        TempFolder.WriteBytes(pad, Logical - new FileInfo(journalPath).Length - 3000, 72);
        Assert.AreEqual(Logical, RecycledItem.Measure(standIn), "precondition: the stand-in measures what its line says");
        b.ReportInBin(standIn);

        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var bin = new DirectoryInfo(b.Bin);
        var security = bin.GetAccessControl();
        security.AddAccessRule(rule);
        bin.SetAccessControl(security);
        IReadOnlyList<PutBackOutcome> outcomes;
        try
        {
            outcomes = new PutBack(JournalStore.Open(journalPath), b.Gate).RestoreSession("s1");
        }
        finally
        {
            var current = bin.GetAccessControl();
            current.RemoveAccessRuleSpecific(rule);
            bin.SetAccessControl(current);
        }

        var what = string.Join(", ", outcomes.Select(o => Path.GetFileName(o.Entry.OriginalPath) + "=" + o.Restored + " " + o.Reason));
        Check.True(Directory.Exists(folder), "precondition: the folder was moved back (with its journal): " + what);
        Check.False(Directory.Exists(standIn) && File.Exists(journalPath), "precondition: the journal is no longer at its path: " + what);
        Assert.IsTrue(outcomes.Count >= 1 && outcomes[0].Entry.Id == last.Id, "the last moved item came first: " + what);
        Assert.AreEqual(Text.PutBackNotJournaled, outcomes[0].Reason, "the report says the journal could not be written: " + what);
        Assert.AreEqual(1, outcomes.Count, "the session stopped after the line could not be written: " + what);
        Check.True(File.Exists(earlier.TrashPath), "the earlier item stays in the bin");
        Check.False(File.Exists(earlier.OriginalPath), "the earlier item was not put back");
    }
}
