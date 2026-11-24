using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Independent ASP-28 partition contract, frozen before the summary implementation. Synthetic nodes and
/// entries only: no file IO, registry access, shell, actual move, or private app implementation is involved.</summary>
[TestClass]
public sealed class UninstallCleanupSummaryTests
{
    private static TrashOutcome Outcome(TrashResult result, bool journaled, string? reason = null)
    {
        const string original = @"C:\SyntheticProfile\AppData\Roaming\AcceptanceApp";
        var node = ScanNode.Folder(original, stamp: new ItemStamp(17, (UInt128)91, 123456789, null));
        var item = new SessionItem(node, null);
        var journalResult = result switch
        {
            TrashResult.Trashed => JournalResult.Trashed,
            TrashResult.Lost => JournalResult.Lost,
            _ => JournalResult.Skipped,
        };
        var entry = journaled ? new JournalEntry("synthetic-entry", "synthetic-session", DateTimeOffset.UnixEpoch,
            LeanModule.Uninstall, original, @"C:\SyntheticProfile\AppData\Roaming", original, 4096,
            @"C:\$Recycle.Bin\SyntheticSid\$Racceptance", new TrashedIdentity(17, (UInt128)91, 4096),
            journalResult, reason, "acceptance-test") : null;
        return new TrashOutcome(item, result, reason, entry);
    }

    private static void Counts(UninstallCleanupSummary summary, int approved, int moved, int unsuccessful,
        int unattempted, bool complete, bool recoverable)
    {
        Assert.AreEqual(approved, summary.Approved);
        Assert.AreEqual(moved, summary.Moved);
        Assert.AreEqual(unsuccessful, summary.Unsuccessful);
        Assert.AreEqual(unattempted, summary.Unattempted);
        Assert.AreEqual(complete, summary.Complete);
        Assert.AreEqual(recoverable, summary.Recoverable);
        Assert.AreEqual(summary.Approved, summary.Moved + summary.Unsuccessful + summary.Unattempted,
            "successful, attempted-unsuccessful, and unattempted partition every approved location exactly once");
    }

    [TestMethod]
    public void ZeroApprovedIsNotACompletedCleanup()
    {
        Counts(UninstallCleanupSummary.From(0, []), 0, 0, 0, 0, complete: false, recoverable: false);
    }

    [TestMethod]
    public void ApprovedButUnattemptedLocationsAreNeitherMovedNorUnsuccessful()
    {
        Counts(UninstallCleanupSummary.From(3, []), 3, 0, 0, 3, complete: false, recoverable: false);
    }

    [TestMethod]
    [DataRow(TrashResult.Trashed, true, null, 1, true)]
    [DataRow(TrashResult.Trashed, true, "", 1, true)]
    [DataRow(TrashResult.Trashed, true, "The moved item did not match the approved item — check the Recycle Bin", 0, true)]
    [DataRow(TrashResult.Trashed, true, " ", 0, true)]
    [DataRow(TrashResult.Trashed, false, null, 0, false)]
    [DataRow(TrashResult.Trashed, false, "", 0, false)]
    [DataRow(TrashResult.Trashed, false, "Could not record the move", 0, false)]
    [DataRow(TrashResult.Skipped, true, null, 0, false)]
    [DataRow(TrashResult.Skipped, false, "In use", 0, false)]
    [DataRow(TrashResult.Lost, true, "Not found intact in the Recycle Bin", 0, false)]
    [DataRow(TrashResult.Lost, false, null, 0, false)]
    public void OnlyTrashedJournaledAndReasonFreeOutcomesCountAsSuccessful(TrashResult result,
        bool journaled, string? reason, int expectedMoved, bool expectedRecoverable)
    {
        var outcome = Outcome(result, journaled, reason);
        Counts(UninstallCleanupSummary.From(1, [outcome]), 1, expectedMoved, 1 - expectedMoved, 0,
            complete: expectedMoved == 1, recoverable: expectedRecoverable);
        Assert.AreEqual(reason, outcome.Reason, "summary construction must retain physical-moved explanations for details");
        if (journaled) Assert.AreEqual(reason, outcome.Entry!.Reason, "the synthetic journal warning is unchanged");
        else Assert.IsNull(outcome.Entry, "summary does not fabricate a journal record");
    }

    [TestMethod]
    public void LostWasAttemptedAndDoesNotConsumeAnUnattemptedSlot()
    {
        var outcomes = new[] { Outcome(TrashResult.Trashed, true), Outcome(TrashResult.Lost, true, "lost") };
        Counts(UninstallCleanupSummary.From(3, outcomes), 3, 1, 1, 1, complete: false, recoverable: true);
    }

    [TestMethod]
    public void PhysicalMoveWarningAndUnjournaledMoveAreBothUnsuccessfulButOnlyJournaledWarningIsRecoverable()
    {
        var warning = Outcome(TrashResult.Trashed, true, "The moved item did not match the approved item — check the Recycle Bin");
        var unjournaled = Outcome(TrashResult.Trashed, false);
        Counts(UninstallCleanupSummary.From(2, [warning, unjournaled]), 2, 0, 2, 0, complete: false, recoverable: true);
        Counts(UninstallCleanupSummary.From(1, [unjournaled]), 1, 0, 1, 0, complete: false, recoverable: false);
    }

    [TestMethod]
    public void EveryApprovedItemMustBeCleanlyMovedAndRecordedForFullSuccess()
    {
        var clean = new[] { Outcome(TrashResult.Trashed, true), Outcome(TrashResult.Trashed, true, ""), Outcome(TrashResult.Trashed, true) };
        Counts(UninstallCleanupSummary.From(3, clean), 3, 3, 0, 0, complete: true, recoverable: true);
        Counts(UninstallCleanupSummary.From(4, clean), 4, 3, 0, 1, complete: false, recoverable: true);
    }

    [TestMethod]
    public void PartitionDoesNotDependOnOutcomeOrderAndDoesNotChangeInput()
    {
        var good = Outcome(TrashResult.Trashed, true);
        var skipped = Outcome(TrashResult.Skipped, true, "In use");
        var lost = Outcome(TrashResult.Lost, false, "lost");
        var warning = Outcome(TrashResult.Trashed, true, "physical move warning");
        var input = new[] { good, skipped, lost, warning };
        Counts(UninstallCleanupSummary.From(5, input), 5, 1, 3, 1, complete: false, recoverable: true);
        Counts(UninstallCleanupSummary.From(5, [warning, lost, skipped, good]), 5, 1, 3, 1, complete: false, recoverable: true);
        CollectionAssert.AreEqual(new[] { good, skipped, lost, warning }, input);
        Assert.AreEqual(NodeMarks.None, good.Item.Node.Flags);
        Assert.AreEqual("physical move warning", warning.Reason);
    }

    [TestMethod]
    public void LargeApprovedCountDoesNotOverflowOrInventAttempts()
    {
        Counts(UninstallCleanupSummary.From(int.MaxValue, [Outcome(TrashResult.Trashed, true), Outcome(TrashResult.Lost, true)]),
            int.MaxValue, 1, 1, int.MaxValue - 2, complete: false, recoverable: true);
    }

    [TestMethod]
    public void NegativeApprovedCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UninstallCleanupSummary.From(-1, []));
    }

    [TestMethod]
    public void MoreOutcomesThanApprovedIsRejectedInsteadOfProducingNegativeUnattemptedCount()
    {
        IReadOnlyList<TrashOutcome> outcomes = [Outcome(TrashResult.Trashed, true), Outcome(TrashResult.Skipped, true, "In use")];
        Assert.Throws<ArgumentException>(() => UninstallCleanupSummary.From(1, outcomes));
        Assert.Throws<ArgumentException>(() => UninstallCleanupSummary.From(0, [Outcome(TrashResult.Lost, false)]));
    }
}
