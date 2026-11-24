using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Trash;

namespace Ambysto.Lean.Core.Journal;

/// <summary>One Put back outcome: <see cref="Restored"/>, or the reason from docs/spec/05's table (Copy.Text words).</summary>
public sealed record PutBackOutcome(JournalEntry Entry, bool Restored, string? Reason);

/// <summary>Put back (docs/spec/05 and its Windows note), per entry and per session. The Kit's environment check first;
/// then 05's checks in order (the Recycle Bin item exists; its identity and logical size equal <c>trashedIdentity</c>; it
/// is inside a Recycle Bin folder; the destination passes the allowed-root and never-touch rules (the gate's restore
/// check, judged from the destination path); nothing exists at the destination; the parent's real path equals
/// <c>originalParentRealPath</c>); the journal must accept a write first; then one move of the <c>$R</c> item to the
/// original path, with no copy fallback and never across volumes (any failure is <c>restoreFailed</c> with its reason);
/// a <c>restored</c> or <c>restoreFailed</c> entry is appended. Never the shell's restore; the <c>$I</c> record is left
/// alone. Only a <c>trashed</c> entry not yet restored can be put back.</summary>
public sealed partial class PutBack
{
    /// <summary>The one move back: no <c>MOVEFILE_COPY_ALLOWED</c> (never a copy, never across volumes) and no
    /// <c>MOVEFILE_REPLACE_EXISTING</c> (an item that appeared at the destination meanwhile makes the move fail).</summary>
    internal const uint MoveFlags = 0;

    private readonly JournalStore journal;
    private readonly IItemGate gate;

    /// <summary>Put back through <paramref name="gate"/> (environment, facts and destination checks) into
    /// <paramref name="journal"/>.</summary>
    public PutBack(JournalStore journal, IItemGate gate)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(gate);
        this.journal = journal;
        this.gate = gate;
    }

    /// <summary>Restores one <c>trashed</c> entry.</summary>
    public PutBackOutcome Restore(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Result != JournalResult.Trashed)
        {
            return new PutBackOutcome(entry, false, entry.Result == JournalResult.Lost ? Copy.Text.DeletedPermanentlyByWindows : Copy.Text.NothingToPutBack);
        }

        if (AlreadyRestored(journal.Read()).Contains(entry.Id))
        {
            return new PutBackOutcome(entry, false, Copy.Text.AlreadyPutBack);
        }

        return RestoreOne(entry);
    }

    /// <summary>"Put back all from this session": every <c>trashed</c> entry of <paramref name="sessionId"/> not yet
    /// restored, each judged on its own (one refusal never stops the others; a journal that cannot be written stops all).</summary>
    public IReadOnlyList<PutBackOutcome> RestoreSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var all = journal.Read();
        var restored = AlreadyRestored(all);
        var outcomes = new List<PutBackOutcome>();
        // Last moved first, so an item that was inside a folder moved later in the session finds its folder back.
        foreach (var entry in all.Where(e => e.Result == JournalResult.Trashed && e.SessionId == sessionId && !restored.Contains(e.Id)).Reverse())
        {
            var outcome = RestoreOne(entry);
            outcomes.Add(outcome);
            // A journal that cannot be written stops the rest, before a move back (the preflight) or after one (the
            // restored line was not written), as the Trasher stops (docs/spec/02 step 8).
            if (outcome.Reason == Copy.Text.JournalUnavailable || outcome.Reason == Copy.Text.PutBackNotJournaled)
            {
                break;
            }
        }

        return outcomes;
    }

    // The ids of trashed entries already put back: a restored line names its trashed line (refersTo); a restored line
    // without one (written by an older build or another tool) is matched by the Recycle Bin path and identity it carries,
    // since a $R item belongs to one move only.
    private static HashSet<string> AlreadyRestored(IReadOnlyList<JournalEntry> all)
    {
        var restored = all.Where(e => e.Result == JournalResult.Restored).ToList();
        var ids = restored.Where(e => e.RefersTo is not null).Select(e => e.RefersTo!).ToHashSet(StringComparer.Ordinal);
        foreach (var trashed in all.Where(e => e.Result == JournalResult.Trashed && e.TrashPath is not null && e.TrashedIdentity is not null))
        {
            if (restored.Any(r => r.RefersTo is null && r.TrashedIdentity == trashed.TrashedIdentity
                && string.Equals(r.TrashPath, trashed.TrashPath, StringComparison.OrdinalIgnoreCase)))
            {
                ids.Add(trashed.Id);
            }
        }

        return ids;
    }

    private PutBackOutcome RestoreOne(JournalEntry entry)
    {
        // The Kit's environment check first (elevated, SYSTEM, not interactive), before any of 05's checks.
        var first = gate.CheckRestoreDestination(entry.OriginalPath);
        if (first.Reason is RefusalReason.Elevated or RefusalReason.NotInteractive
            || (first.Reason == RefusalReason.CouldNotVerify && first.Detail == "environment"))
        {
            return Refuse(entry, first.Message ?? Copy.Text.CannotRestoreProtected);
        }

        if (entry.TrashPath is not { } trashPath || entry.TrashedIdentity is not { } expected)
        {
            return Refuse(entry, Copy.Text.NothingToPutBack);
        }

        // 1. The Recycle Bin item exists.
        var facts = gate.Facts(trashPath);
        if (facts.Stamp is null)
        {
            return Refuse(entry, facts.FailureReason == RefusalReason.AlreadyGone ? Copy.Text.NoLongerInRecycleBin : Copy.Text.DifferentFileInRecycleBin);
        }

        // 2. Its identity and logical size are the moved item's.
        // A line whose size was unknown at the move (owner decision 2026-10-18) is checked by its identity alone.
        var identity = new FileIdentity(expected.VolumeSerial, expected.FileId);
        if (expected.LogicalBytes == TrashedIdentity.UnknownSize
            ? !RecycledItem.HasIdentity(gate, trashPath, identity)
            : !RecycledItem.Matches(gate, trashPath, identity, expected.LogicalBytes))
        {
            return Refuse(entry, Copy.Text.DifferentFileInRecycleBin);
        }

        // 3. It is a $R item directly inside this user's Recycle Bin folder on its volume.
        if (facts.CanonicalPath is not { } canonical || !IsInRecycleBinFolder(canonical))
        {
            return Refuse(entry, Copy.Text.NotInRecycleBinFolder);
        }

        // 4–6. The destination, judged from its own path by the Kit and Lean's restore rule; then its parent is the
        // folder the item was moved from.
        var destination = gate.CheckRestoreDestination(entry.OriginalPath);
        if (destination.Approved is not { } approved)
        {
            return Refuse(entry, destination.Reason switch
            {
                RefusalReason.RestoreDestinationExists => Copy.Text.DestinationExists,
                RefusalReason.RestoreFolderMissing => Copy.Text.OriginalFolderGone,
                // The never-touch list holds every cloud-sync root and everything inside it (docs/spec/02 Windows note).
                RefusalReason.Protected or RefusalReason.SystemOwned or RefusalReason.RootOrProfile or RefusalReason.NotAllowed
                    or RefusalReason.CloudSyncFolder => Copy.Text.CannotRestoreProtected,
                _ => destination.Message ?? Copy.Text.CannotRestoreProtected,
            });
        }

        // The Kit refuses an existing destination already; Lean asks again, so the check never rests on one component.
        if (File.Exists(approved.CanonicalPath) || Directory.Exists(approved.CanonicalPath))
        {
            return Refuse(entry, Copy.Text.DestinationExists);
        }

        var parent = Path.GetDirectoryName(approved.CanonicalPath);
        if (entry.OriginalParentRealPath is null || parent is null || !string.Equals(parent, entry.OriginalParentRealPath, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(entry, Copy.Text.OriginalFolderGone);
        }

        // Nothing is put back unless the journal can be written first.
        if (!journal.CanWrite())
        {
            return new PutBackOutcome(entry, false, Copy.Text.JournalUnavailable);
        }

        // One move of the $R item (the journal's path, whose item was just identified and whose real path is in the bin) to
        // the approved destination: never the shell's restore, never a copy; the $I record stays.
        if (!MoveFileExW(Measure.MeasureNative.ToExtendedPath(trashPath), Measure.MeasureNative.ToExtendedPath(approved.CanonicalPath), MoveFlags))
        {
            return Refuse(entry, Copy.Text.PutBackFailed(Marshal.GetLastPInvokeError()));
        }

        return Append(entry, JournalResult.Restored, null)
            ? new PutBackOutcome(entry, true, null)
            : new PutBackOutcome(entry, true, Copy.Text.PutBackNotJournaled);
    }

    private PutBackOutcome Refuse(JournalEntry entry, string reason)
    {
        Append(entry, JournalResult.RestoreFailed, reason);
        return new PutBackOutcome(entry, false, reason);
    }

    private bool Append(JournalEntry trashed, JournalResult result, string? reason)
    {
        try
        {
            journal.Append(new JournalEntry(Guid.NewGuid().ToString("N"), trashed.SessionId, DateTimeOffset.UtcNow, trashed.Module,
                trashed.OriginalPath, trashed.OriginalParentRealPath, trashed.ApprovedRealPath, trashed.Bytes, trashed.TrashPath,
                trashed.TrashedIdentity, result, reason, AppVersion) { RefersTo = trashed.Id });
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // <volume root>\$Recycle.Bin\<this user's SID>\$R…
    internal static bool IsInRecycleBinFolder(string path)
    {
        var name = Path.GetFileName(path);
        var userFolder = Path.GetDirectoryName(path);
        var bin = userFolder is null ? null : Path.GetDirectoryName(userFolder);
        var root = bin is null ? null : Path.GetDirectoryName(bin);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        return name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)
            && userFolder is not null && bin is not null && root is not null && sid is not null
            && string.Equals(Path.GetFileName(userFolder), sid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(bin), "$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            && string.Equals(root, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string AppVersion =
        typeof(PutBack).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string destination, uint flags);
}
