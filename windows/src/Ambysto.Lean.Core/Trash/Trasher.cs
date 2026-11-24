using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Trash;

/// <summary>What happened to one item (docs/spec/02 Windows note on steps 6–7).</summary>
public enum TrashResult
{
    /// <summary>In the Recycle Bin with the approved identity and logical size.</summary>
    Trashed,

    /// <summary>Not moved; the original is in place (reason in <see cref="TrashOutcome.Reason"/>).</summary>
    Skipped,

    /// <summary>The move was made and the item is not in the Recycle Bin intact; the session stopped.</summary>
    Lost,
}

/// <summary>One item's outcome, with the journal entry written for it (null only when that write failed).</summary>
public sealed record TrashOutcome(SessionItem Item, TrashResult Result, string? Reason, JournalEntry? Entry);

/// <summary>The only deletion point (docs/spec/02 "Order per item"; plan Design "Trasher"). For each item, in order: the
/// kept copy unchanged (duplicates); re-measure; the gate, with the scan's stamp as the shown stamp; in use; the journal
/// accepts a write; one shell move of the approved real path; post-verify (the reported item's facts: approved volume
/// serial and file id, and its logical size measured by Lean's scanner equal to the approved logical size → trashed;
/// the original still in place with its approved identity → skipped; anything else → lost, and the session stops); the
/// journal entry. Every item produces exactly one entry; a journal write that fails after a move stops the session. A
/// cancel stops before the next item, never inside one.</summary>
public sealed class Trasher
{
    private readonly IItemGate gate;
    private readonly IShellRecycler recycler;
    private readonly JournalStore journal;

    /// <summary>A Trasher over <paramref name="gate"/>, <paramref name="recycler"/> and <paramref name="journal"/>.</summary>
    public Trasher(IItemGate gate, IShellRecycler recycler, JournalStore journal)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(recycler);
        ArgumentNullException.ThrowIfNull(journal);
        this.gate = gate;
        this.recycler = recycler;
        this.journal = journal;
    }

    /// <summary>Moves the session's items; returns one outcome per item reached (items after a stop are not reached).</summary>
    public IReadOnlyList<TrashOutcome> Run(DeletionSession session, CancellationToken cancellationToken) =>
        Run(session, cancellationToken, null);

    /// <summary>As <see cref="Run(DeletionSession, CancellationToken)"/>, reporting <see cref="MoveProgress"/> before each
    /// item reached.</summary>
    public IReadOnlyList<TrashOutcome> Run(DeletionSession session, CancellationToken cancellationToken, IProgress<MoveProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(session);
        var outcomes = new List<TrashOutcome>();
        var index = 0;
        foreach (var item in session.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break; // between items only, never inside one
            }

            index++;
            progress?.Report(new MoveProgress(index, session.Items.Count, item.Node.Name));
            var step = MoveOne(session, item);
            outcomes.Add(step.Outcome);
            if (step.StopSession)
            {
                break;
            }
        }

        return outcomes;
    }

    private readonly record struct Step(TrashOutcome Outcome, bool StopSession);

    private Step MoveOne(DeletionSession session, SessionItem item)
    {
        var node = item.Node;

        // 1. A duplicate is moved only while the copy that is kept is still there, unchanged.
        if (item.KeptCopy is { } kept && !IsUnchanged(kept))
        {
            return Skip(session, item, Copy.Text.KeptCopyChanged, null);
        }

        // 2–3. Re-measure and gate: the Kit measures the item now (sizes from the scan are never reused) and checks it
        // against the scan's stamp, refusing anything changed since.
        if (node.Stamp is not { } scanned)
        {
            return Skip(session, item, Copy.Text.NoScanIdentity, null);
        }

        var result = gate.Check(new GateRequest(node.Path, ToKit(scanned)));
        if (result.Approved is not { } approved)
        {
            return Skip(session, item, result.Message ?? Copy.Text.NoScanIdentity, null);
        }

        // 4. In use.
        var inUse = gate.InUse(approved);
        if (inUse.IsInUse)
        {
            return Skip(session, item, Copy.Text.InUseBy(Holders(inUse)), approved);
        }

        // 5. The journal must accept a write now; otherwise nothing more is moved in this session.
        if (!journal.CanWrite())
        {
            return new Step(new TrashOutcome(item, TrashResult.Skipped, Copy.Text.JournalUnavailable, null), StopSession: true);
        }

        // The size post-verify compares, measured now and in the same way as the Recycle Bin item will be (every name of
        // the tree with its real length; interim review MAJOR 1: the Kit's logical size and Lean's scanner count hard
        // links differently).
        if (RecycledItem.Measure(approved.CanonicalPath) is not { } before)
        {
            return Skip(session, item, Copy.Text.CouldNotMeasureBeforeMove, approved);
        }

        // 6. One shell move of the exact real path the gate approved.
        var report = recycler.Recycle(approved.CanonicalPath);

        // 7–8. Post-verify and journal. Nothing that fails after the move may lose the item's line: any error while
        // verifying counts as "not found intact" (hard review 2026-10-18: not only the five expected kinds).
        try
        {
            return PostVerify(session, item, approved, report, before);
        }
#pragma warning disable CA1031 // after a move, every failure must still end in a journal line and a stopped session
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Lost(session, item, approved, report, before);
        }
#pragma warning restore CA1031
    }

    // 7. Post-verify: the item the shell reports, opened for its facts, must be the approved one with the size measured
    // just before the move; otherwise the original must still be in place; anything else is lost.
    private Step PostVerify(DeletionSession session, SessionItem item, ApprovedItem approved, RecycleReport report, long before)
    {
        if (report.ReportedNewItemPath is { } reported && RecycledItem.HasIdentity(gate, reported, approved.Stamp.Identity))
        {
            var after = RecycledItem.Measure(reported);
            if (after == before)
            {
                return Record(session, item, approved, TrashResult.Trashed, null, reported, stop: false, measured: before);
            }

            // The approved item is in the Recycle Bin, but not as it was approved (a part of it is missing, or it changed
            // during the move; owner decision 2026-10-17): it is not called deleted. The journal records it where it is,
            // with the size it has there, so Put back can return it as it is; the session stops (docs/spec/02).
            // A size that cannot be measured there is recorded as unknown, so Put back can still return the item by its
            // identity (owner decision 2026-10-18, option A).
            return Record(session, item, approved, TrashResult.Trashed, Copy.Text.MovedItemDidNotMatch, reported, stop: true,
                measured: after ?? TrashedIdentity.UnknownSize);
        }

        if (StillInPlace(approved))
        {
            // Left in place (intent Outcome 6): Lean's own abort (the bin turned off, a long path created after the gate's
            // walk) is "Changed since the scan"; otherwise a holder that appeared is named as in step 4.
            return Record(session, item, approved, TrashResult.Skipped, LeftInPlaceReason(report, approved), null, stop: false);
        }

        return Lost(session, item, approved, report, before);
    }

    // Lost: the report says "not found intact"; the journal, the history and Put back say Windows deleted it.
    private Step Lost(DeletionSession session, SessionItem item, ApprovedItem approved, RecycleReport report, long before)
    {
        var lost = Record(session, item, approved, TrashResult.Lost, Copy.Text.DeletedPermanentlyByWindows, report.ReportedNewItemPath, stop: true, measured: before);
        return lost with { Outcome = lost.Outcome with { Reason = Copy.Text.NotFoundIntactInRecycleBin } };
    }

    private string LeftInPlaceReason(RecycleReport report, ApprovedItem approved)
    {
        if (report.AbortedNonRecycle)
        {
            return RefusalReason.ChangedSinceScan.Message();
        }

        var inUse = gate.InUse(approved);
        return inUse.IsInUse ? Copy.Text.InUseBy(Holders(inUse)) : Copy.Text.MoveFailed(report.ErrorCode);
    }

    private static string Holders(InUseResult inUse) =>
        string.Join(", ", inUse.Holders.Select(h => h.ImageName is { } name ? name + " (" + h.ProcessId.ToString(CultureInfo.InvariantCulture) + ")" : h.ProcessId.ToString(CultureInfo.InvariantCulture)));

    private bool IsUnchanged(ScanNode kept)
    {
        if (kept.Stamp is not { } stamp)
        {
            return false;
        }

        var facts = gate.Facts(kept.Path);
        return facts.Stamp is { } now && now == ToKit(stamp);
    }

    private bool StillInPlace(ApprovedItem approved)
    {
        var facts = gate.Facts(approved.CanonicalPath);
        return facts.Stamp is { } stamp && stamp.Identity == approved.Stamp.Identity;
    }

    private Step Skip(DeletionSession session, SessionItem item, string reason, ApprovedItem? approved) =>
        Record(session, item, approved, TrashResult.Skipped, reason, null, stop: false);

    // 8. Every item produces exactly one entry. A write that fails after a move stops the session.
    private Step Record(DeletionSession session, SessionItem item, ApprovedItem? approved, TrashResult result, string? reason,
        string? trashPath, bool stop, long? measured = null)
    {
        TrashedIdentity? trashed = result == TrashResult.Trashed && approved is not null && measured is { } bytes
            ? new TrashedIdentity(approved.Stamp.Identity.VolumeSerial, approved.Stamp.Identity.FileId, bytes)
            : null;
        var entry = new JournalEntry(
            Guid.NewGuid().ToString("N"),
            session.Id,
            DateTimeOffset.UtcNow,
            session.Module,
            item.Node.Path,
            approved is not null ? Path.GetDirectoryName(approved.CanonicalPath) : null,
            approved?.CanonicalPath,
            approved?.AllocatedBytes,
            trashPath,
            trashed,
            result switch { TrashResult.Trashed => JournalResult.Trashed, TrashResult.Lost => JournalResult.Lost, _ => JournalResult.Skipped },
            reason,
            AppVersion);
        try
        {
            journal.Append(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The entry cannot be written: stop, so no later item goes without its line (hard review 2026-10-18: a skipped item
            // too), and the report names where a moved item went.
            var said = result == TrashResult.Trashed && trashPath is not null ? Copy.Text.MovedButNotJournaled(trashPath) : reason;
            return new Step(new TrashOutcome(item, result, said, null), StopSession: true);
        }

        return new Step(new TrashOutcome(item, result, reason, entry), stop);
    }

    private static FileStamp ToKit(ItemStamp stamp) =>
        new(new FileIdentity(stamp.VolumeSerial, stamp.FileId), stamp.LastWriteTimeUtcTicks, stamp.Length);

    private static readonly string AppVersion =
        typeof(Trasher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
}
