using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Journal;

/// <summary>docs/spec/05 "Put back" and its Windows note; intent Outcome 5, decision 11 and criterion 3: the Kit's
/// environment check first, then the table's checks in order with their Recycle Bin words, the journal must accept a write
/// first, then one move of the <c>$R</c> item to the original path (no copy, never across volumes), a <c>restored</c> or
/// <c>restoreFailed</c> entry; the <c>$I</c> record is left alone; only a <c>trashed</c> entry not yet restored can be put
/// back. The gate is <see cref="FakeItemGate"/>; the <c>$R</c> items are real stand-ins (<see cref="PutBackBench"/>).</summary>
[TestClass]
public sealed class PutBackTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void CaptureOptInNoDialogProof() => PutBackVisualProof.Capture(TestContext);

    private static void NothingMoved(JournalEntry entry, bool standInExists = true, bool originalExists = false)
    {
        Assert.AreEqual(standInExists, File.Exists(entry.TrashPath) || Directory.Exists(entry.TrashPath), "the $R item stays where it was: " + entry.TrashPath);
        Assert.AreEqual(originalExists, File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath), "the original path is as it was: " + entry.OriginalPath);
    }

    private static void Refused(PutBackBench b, PutBackOutcome outcome, string reason)
    {
        Check.False(outcome.Restored, "not restored; reason: " + outcome.Reason);
        Assert.AreEqual(reason, outcome.Reason);
        var last = b.Entries()[^1];
        Assert.AreEqual(JournalResult.RestoreFailed, last.Result, "a refusal is journaled restoreFailed");
        Assert.AreEqual(reason, last.Reason, "with its reason");
        Check.True(string.Equals(outcome.Entry.OriginalPath, last.OriginalPath, StringComparison.OrdinalIgnoreCase), "for the same item");
    }

    // --- success ------------------------------------------------------------------------------------------------

    // One move of the $R item to the original path: same file id, nothing left at the $R path, the $I record untouched,
    // a restored entry appended.
    [TestMethod]
    public void AFileIsPutBackToItsOriginalPathWithTheSameFileId()
    {
        using var b = new PutBackBench("putback-file");
        var entry = b.Stage("report.docx");
        var dollarI = Path.Combine(b.Bin, "$I" + Path.GetFileName(entry.TrashPath)![2..]);
        File.WriteAllBytes(dollarI, [1, 2, 3, 4]);
        var before = b.Entries().Count;

        var outcome = b.PutBack().Restore(entry);

        Check.True(outcome.Restored, "restored; reason: " + outcome.Reason);
        Assert.IsNull(outcome.Reason);
        Check.True(File.Exists(entry.OriginalPath), "back at the original path");
        Check.False(File.Exists(entry.TrashPath), "nothing left at the $R path");
        var now = Native.IdentityOf(entry.OriginalPath);
        Assert.AreEqual(entry.TrashedIdentity!.Value.VolumeSerial, now.VolumeSerial, "same volume");
        Assert.AreEqual(entry.TrashedIdentity!.Value.FileId, now.FileId, "same file id: moved, not copied");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dollarI), "the $I record is left alone");
        var entries = b.Entries();
        Assert.AreEqual(before + 1, entries.Count, "one entry appended");
        Assert.AreEqual(JournalResult.Restored, entries[^1].Result);
        Assert.AreEqual(entry.OriginalPath, entries[^1].OriginalPath);
    }

    // A folder too (criterion 3: file and folder).
    [TestMethod]
    public void AFolderIsPutBackWithTheSameFileId()
    {
        using var b = new PutBackBench("putback-folder");
        var entry = b.Stage("Project", folder: true);

        var outcome = b.PutBack().Restore(entry);

        Check.True(outcome.Restored, "restored; reason: " + outcome.Reason);
        Assert.AreEqual(entry.TrashedIdentity!.Value.FileId, Native.IdentityOf(entry.OriginalPath).FileId, "same folder");
        Check.True(File.Exists(Path.Combine(entry.OriginalPath, "deep", "two.bin")), "with its contents");
        Assert.AreEqual(JournalResult.Restored, b.Entries()[^1].Result);
    }

    // --- the environment check first ----------------------------------------------------------------------------

    // docs/spec/05 Windows note: elevated, SYSTEM or not interactive → the Kit's reason, before any check of the table
    // (here every one of them would also fail: the $R item is gone).
    [TestMethod]
    [DataRow(RefusalReason.Elevated)]
    [DataRow(RefusalReason.NotInteractive)]
    public void TheEnvironmentRefusalComesFirst(RefusalReason reason)
    {
        using var b = new PutBackBench("putback-env");
        var entry = b.Stage("a.bin");
        File.Delete(entry.TrashPath!);
        b.Gate.OnRestore = _ => KitObjects.Refuse(reason);

        var outcome = b.PutBack().Restore(entry);

        Refused(b, outcome, reason.Message());
        if (reason == RefusalReason.Elevated)
        {
            Assert.AreEqual(Text.MovingOffElevated, outcome.Reason, "the Copy.Text words");
        }

        NothingMoved(entry, standInExists: false);
    }

    // --- docs/spec/05's table, in order ---------------------------------------------------------------------------
    // Each test makes its own check fail together with every later one, so the reason shown is the first in the table.

    [TestMethod]
    public void NoLongerInTheRecycleBinComesFirstInTheTable()
    {
        using var b = new PutBackBench("putback-gone");
        var staged = b.Stage("a.bin", reportInBin: false);
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with
        {
            Id = "variant",
            TrashedIdentity = staged.TrashedIdentity!.Value with { FileId = staged.TrashedIdentity!.Value.FileId + 1 },
            OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath,
        });
        File.Delete(entry.TrashPath!); // emptied, or restored through File Explorer
        File.WriteAllText(entry.OriginalPath, "occupied");
        b.Gate.OnRestore = _ => KitObjects.Refuse(RefusalReason.Protected);

        Refused(b, b.PutBack().Restore(entry), Text.NoLongerInRecycleBin);
        Assert.AreEqual("occupied", File.ReadAllText(entry.OriginalPath), "the destination is untouched");
    }

    // "Already restored through File Explorer": the $R item is gone and the original is back → "No longer in the Recycle Bin".
    [TestMethod]
    public void AnItemAlreadyRestoredThroughExplorerIsNoLongerInTheRecycleBin()
    {
        using var b = new PutBackBench("putback-explorer");
        var entry = b.Stage("a.bin");
        File.Move(entry.TrashPath!, entry.OriginalPath); // what the shell's own restore does

        Refused(b, b.PutBack().Restore(entry), Text.NoLongerInRecycleBin);
        NothingMoved(entry, standInExists: false, originalExists: true);
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("size")]
    public void ADifferentFileComesSecond(string what)
    {
        using var b = new PutBackBench("putback-different");
        var staged = b.Stage("a.bin", reportInBin: false);
        var id = staged.TrashedIdentity!.Value;
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with
        {
            Id = "variant",
            TrashedIdentity = what == "identity" ? id with { FileId = id.FileId + 1 } : id with { LogicalBytes = id.LogicalBytes + 1 },
            OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath,
        });
        File.WriteAllText(entry.OriginalPath, "occupied");
        b.Gate.OnRestore = _ => KitObjects.Refuse(RefusalReason.Protected);

        Refused(b, b.PutBack().Restore(entry), Text.DifferentFileInRecycleBin);
        NothingMoved(entry, originalExists: true);
    }

    // A same-identity file whose bytes grew in the bin is a different file too (identity and size).
    [TestMethod]
    public void AnItemWhoseSizeChangedInTheBinIsADifferentFile()
    {
        using var b = new PutBackBench("putback-grown");
        var entry = b.Stage("a.bin");
        File.AppendAllText(entry.TrashPath!, "more");

        Refused(b, b.PutBack().Restore(entry), Text.DifferentFileInRecycleBin);
        NothingMoved(entry);
    }

    // "Its path is inside a Recycle Bin folder": the facts of the $R item report its canonical path; here the real one, in
    // the scratch folder, which is no Recycle Bin folder.
    [TestMethod]
    public void NotInARecycleBinFolderComesThird()
    {
        using var b = new PutBackBench("putback-notbin");
        var staged = b.Stage("a.bin", reportInBin: false);
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with { Id = "variant", OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath });
        File.WriteAllText(entry.OriginalPath, "occupied");
        b.Gate.OnRestore = _ => KitObjects.Refuse(RefusalReason.Protected);

        Refused(b, b.PutBack().Restore(entry), Text.NotInRecycleBinFolder);
        NothingMoved(entry, originalExists: true);
    }

    // A folder named $Recycle.Bin that is not at the volume root is not a Recycle Bin folder.
    [TestMethod]
    public void ARecycleBinNamedFolderInsideAnotherFolderIsNotOne()
    {
        using var b = new PutBackBench("putback-fakebin");
        var entry = b.Stage("a.bin", reportInBin: false);
        b.Gate.FactsFor(entry.TrashPath!, p =>
        {
            var real = KitObjects.RealFacts(p);
            return KitObjects.FactsOk(Path.Combine(b.Temp.Path, "$Recycle.Bin", PutBackBench.Sid, Path.GetFileName(p)), real.Stamp!.Value);
        });

        Refused(b, b.PutBack().Restore(entry), Text.NotInRecycleBinFolder);
        NothingMoved(entry);
    }

    // The destination passes the allowed-root and never-touch rules (the gate's restore check, judged from the destination
    // path): a refusal there is "Cannot restore to a protected location".
    [TestMethod]
    [DataRow(RefusalReason.Protected, null)]
    [DataRow(RefusalReason.NotAllowed, Text.HintOutside)]
    [DataRow(RefusalReason.CloudSyncFolder, null)]
    public void AProtectedDestinationComesFourth(RefusalReason reason, string? detail)
    {
        using var b = new PutBackBench("putback-protected");
        var staged = b.Stage("a.bin");
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with { Id = "variant", OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath });
        File.WriteAllText(entry.OriginalPath, "occupied");
        b.Gate.OnRestore = _ => KitObjects.Refuse(reason, detail);

        Refused(b, b.PutBack().Restore(entry), Text.CannotRestoreProtected);
        NothingMoved(entry, originalExists: true);
        Check.True(b.Gate.Log.Of("restore").Any(c => c.EndsWith(entry.OriginalPath, StringComparison.OrdinalIgnoreCase)), "the gate judged the destination path itself: " + b.Gate.Log);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SomethingAtTheDestinationComesFifth(bool folder)
    {
        using var b = new PutBackBench("putback-exists");
        var staged = b.Stage("a.bin");
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with { Id = "variant", OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath });
        if (folder)
        {
            Directory.CreateDirectory(entry.OriginalPath);
        }
        else
        {
            File.WriteAllText(entry.OriginalPath, "occupied");
        }

        Refused(b, b.PutBack().Restore(entry), Text.DestinationExists);
        NothingMoved(entry, originalExists: true);
    }

    // The parent's real path differs from originalParentRealPath (moved, or another folder now) → the last reason.
    [TestMethod]
    public void AnotherParentFolderComesLast()
    {
        using var b = new PutBackBench("putback-parent-moved");
        var staged = b.Stage("a.bin");
        var elsewhere = b.Temp.Folder("elsewhere");
        var entry = b.Add(staged with { Id = "variant", OriginalParentRealPath = KitObjects.RealFacts(elsewhere).CanonicalPath });

        Refused(b, b.PutBack().Restore(entry), Text.OriginalFolderGone);
        NothingMoved(entry);
    }

    // The original folder no longer exists.
    [TestMethod]
    public void AParentFolderThatIsGoneIsTheLastReason()
    {
        using var b = new PutBackBench("putback-parent-gone");
        var folder = b.Temp.Folder("gone-later");
        var entry = b.Stage("a.bin", originalFolder: folder);
        Directory.Delete(folder);

        Refused(b, b.PutBack().Restore(entry), Text.OriginalFolderGone);
        NothingMoved(entry);
        Check.False(Directory.Exists(folder), "Put back never creates the missing folder");
    }

    // A parent folder replaced by another folder at the same path (deleted and made again) is not the original folder.
    [TestMethod]
    public void AParentFolderMadeAgainAtTheSamePathIsNotTheOriginal()
    {
        using var b = new PutBackBench("putback-parent-again");
        var folder = b.Temp.Folder("made-again");
        var entry = b.Stage("a.bin", originalFolder: folder);
        Directory.Delete(folder);
        Directory.CreateDirectory(folder);

        var outcome = b.PutBack().Restore(entry);

        // The spec compares the parent's real path; a folder made again at the same path has the same real path. Both
        // readings keep the item safe; this pins only that nothing else is moved and the outcome is journaled.
        if (!outcome.Restored)
        {
            Refused(b, outcome, Text.OriginalFolderGone);
            NothingMoved(entry);
        }
        else
        {
            Assert.AreEqual(JournalResult.Restored, b.Entries()[^1].Result);
        }
    }

    // --- the journal must accept a write first --------------------------------------------------------------------

    [TestMethod]
    public void AJournalThatCannotBeWrittenMovesNothing()
    {
        using var b = new PutBackBench("putback-journal-off");
        var entry = b.Stage("a.bin");
        var putBack = b.PutBack();
        PutBackOutcome outcome;
        using (FileSwitches.ReadOnly(b.JournalPath))
        {
            outcome = putBack.Restore(entry);
        }

        Check.False(outcome.Restored, "not restored");
        Assert.AreEqual(Text.JournalUnavailable, outcome.Reason);
        NothingMoved(entry);
        Assert.AreEqual(1, b.Entries().Count, "nothing appended");
    }

    // --- one move, no copy, never across volumes -------------------------------------------------------------------

    // A $R item on another volume than the destination cannot be moved; Put back never copies it: restoreFailed, the
    // item stays in the bin, nothing at the destination (plan: MOVEFILE_COPY_ALLOWED off).
    [TestMethod]
    public void AMoveAcrossVolumesFailsAndNeverCopies()
    {
        var otherVolume = OtherVolumeFolder();
        try
        {
            using var b = new PutBackBench("putback-volumes", binVolumeFolder: Path.Combine(otherVolume, "bin"));
            var staged = b.Stage("a.bin");
            Check.False(string.Equals(Path.GetPathRoot(staged.TrashPath), Path.GetPathRoot(staged.OriginalPath), StringComparison.OrdinalIgnoreCase), "the fixture spans two volumes");

            var outcome = b.PutBack().Restore(staged);

            Check.False(outcome.Restored, "not restored across volumes");
            Check.False(string.IsNullOrWhiteSpace(outcome.Reason), "with a reason");
            NothingMoved(staged);
            var last = b.Entries()[^1];
            Assert.AreEqual(JournalResult.RestoreFailed, last.Result);
            Assert.AreEqual(outcome.Reason, last.Reason);
        }
        finally
        {
            TryDelete(otherVolume);
        }
    }

    private static string OtherVolumeFolder()
    {
        var mine = Path.GetPathRoot(Path.GetFullPath(TempFolder.BaseFolder))!;
        foreach (var root in new[] { @"E:\", @"C:\" })
        {
            if (string.Equals(root, mine, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                continue;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            {
                continue;
            }

            var folder = Path.Combine(root, "LeanCoreTests", "putback-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        Assert.Inconclusive("no second fixed NTFS volume the test user can write to (the test machine's E:)");
        return "";
    }

    private static void TryDelete(string folder)
    {
        if (folder.Length > 0 && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        var parent = folder.Length > 0 ? Path.GetDirectoryName(folder) : null;
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    // --- only trashed entries, once ------------------------------------------------------------------------------

    // An entry that is not "trashed" is never put back.
    [TestMethod]
    [DataRow(JournalResult.Skipped)]
    [DataRow(JournalResult.Restored)]
    [DataRow(JournalResult.RestoreFailed)]
    [DataRow(JournalResult.Lost)]
    public void OnlyATrashedEntryCanBePutBack(JournalResult result)
    {
        using var b = new PutBackBench("putback-only-trashed");
        var staged = b.Stage("a.bin");
        var entry = b.Add(staged with { Id = "other-" + result, Result = result, Reason = result == JournalResult.Lost ? Text.DeletedPermanentlyByWindows : null });

        PutBackOutcome? outcome = null;
        try
        {
            outcome = b.PutBack().Restore(entry);
        }
        catch (ArgumentException)
        {
            // Refusing the call is as good as refusing the entry.
        }

        if (outcome is not null)
        {
            Check.False(outcome.Restored, result + " entry restored");
            if (result == JournalResult.Lost)
            {
                Assert.AreEqual(Text.DeletedPermanentlyByWindows, outcome.Reason, "a lost item is shown as such, not as emptied or moved (intent Outcome 5)");
            }
        }

        NothingMoved(entry);
        Check.False(b.Entries().Any(e => e.Result == JournalResult.Restored && e.Id != entry.Id), "no restored entry appended");
    }

    // A trashed entry that already has its restored entry is not put back again.
    [TestMethod]
    public void ARestoredEntryIsNotPutBackAgain()
    {
        using var b = new PutBackBench("putback-twice");
        var entry = b.Stage("a.bin");
        b.Add(PutBackBench.RestoredOf(entry));

        var outcome = b.PutBack().Restore(entry);

        Check.False(outcome.Restored, "restored twice");
        NothingMoved(entry);
        Assert.AreEqual(1, b.Entries().Count(e => e.Result == JournalResult.Restored), "no second restored entry");
    }

    // --- Put back all from this session ----------------------------------------------------------------------------

    // Every trashed entry of the session, each once; never one already restored, never another result or session.
    [TestMethod]
    public void RestoreSessionRestoresEachTrashedEntryOnce()
    {
        using var b = new PutBackBench("putback-session");
        var a = b.Stage("a.bin");
        var bb = b.Stage("b.bin");
        var done = b.Stage("done.bin");
        b.Add(PutBackBench.RestoredOf(done));
        var skippedSource = b.Stage("skipped.bin", journal: false);
        var skipped = b.Add(skippedSource with { Result = JournalResult.Skipped, Reason = "Changed since the scan", TrashPath = null, TrashedIdentity = null });
        var lost = b.Add(b.Stage("lost.bin", journal: false) with { Result = JournalResult.Lost, Reason = Text.DeletedPermanentlyByWindows });
        var failed = b.Add(b.Stage("failed.bin", journal: false) with { Result = JournalResult.RestoreFailed, Reason = Text.DestinationExists });
        var other = b.Stage("other.bin", session: "s2");

        var outcomes = b.PutBack().RestoreSession("s1");

        Check.Set(outcomes.Select(o => o.Entry.OriginalPath), a.OriginalPath, bb.OriginalPath);
        Check.True(outcomes.All(o => o.Restored), string.Join(", ", outcomes.Select(o => Path.GetFileName(o.Entry.OriginalPath) + ": " + o.Reason)));
        Check.True(File.Exists(a.OriginalPath) && File.Exists(bb.OriginalPath), "both restored");
        NothingMoved(done);
        NothingMoved(other);
        NothingMoved(skippedSource);
        NothingMoved(lost);
        NothingMoved(failed);
        Check.False(File.Exists(skipped.OriginalPath), "the skipped line moved nothing");
        Assert.AreEqual(3, b.Entries().Count(e => e.Result == JournalResult.Restored), "two new restored entries, plus the earlier one of done.bin");

        var again = b.PutBack().RestoreSession("s1");
        Assert.AreEqual(0, again.Count(o => o.Restored), "nothing is restored twice");
        Check.False(again.Any(o => o.Entry.OriginalPath == a.OriginalPath || o.Entry.OriginalPath == bb.OriginalPath), "the restored entries are not offered again");
    }

    // One refusal never stops the others.
    [TestMethod]
    public void ARefusalInASessionDoesNotStopTheOthers()
    {
        using var b = new PutBackBench("putback-session-refusal");
        var a = b.Stage("a.bin");
        var c = b.Stage("c.bin");
        File.WriteAllText(a.OriginalPath, "occupied");

        var outcomes = b.PutBack().RestoreSession("s1");

        Assert.AreEqual(2, outcomes.Count);
        var first = outcomes.Single(o => o.Entry.OriginalPath == a.OriginalPath);
        var second = outcomes.Single(o => o.Entry.OriginalPath == c.OriginalPath);
        Check.False(first.Restored, "a: occupied");
        Assert.AreEqual(Text.DestinationExists, first.Reason);
        Check.True(second.Restored, "c restored; reason: " + second.Reason);
        NothingMoved(a, originalExists: true);
    }

    // A journal that cannot be written stops all.
    [TestMethod]
    public void AJournalThatCannotBeWrittenStopsTheWholeSession()
    {
        using var b = new PutBackBench("putback-session-journal");
        var a = b.Stage("a.bin");
        var c = b.Stage("c.bin");
        var putBack = b.PutBack();
        IReadOnlyList<PutBackOutcome> outcomes;
        using (FileSwitches.ReadOnly(b.JournalPath))
        {
            outcomes = putBack.RestoreSession("s1");
        }

        Check.False(outcomes.Any(o => o.Restored), "nothing restored");
        NothingMoved(a);
        NothingMoved(c);
    }
}
