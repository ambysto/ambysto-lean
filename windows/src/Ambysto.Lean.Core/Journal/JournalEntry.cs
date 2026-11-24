using System;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Journal;

/// <summary>The outcome an entry records (docs/spec/05 "Entry", <c>result</c>).</summary>
public enum JournalResult
{
    /// <summary>Moved to the Recycle Bin and verified there.</summary>
    Trashed,

    /// <summary>Not moved; <see cref="JournalEntry.Reason"/> says why.</summary>
    Skipped,

    /// <summary>Put back to its original path.</summary>
    Restored,

    /// <summary>Put back was refused or failed; <see cref="JournalEntry.Reason"/> says why.</summary>
    RestoreFailed,

    /// <summary>Windows note: the move was made and the item is not in the Recycle Bin (docs/spec/05).</summary>
    Lost,
}

/// <summary>The identity of the item in the Recycle Bin: volume serial, 128-bit file id and logical size (docs/spec/05
/// Windows note on <c>trashedIdentity</c>).</summary>
public readonly record struct TrashedIdentity(ulong VolumeSerial, UInt128 FileId, long LogicalBytes)
{
    /// <summary><see cref="LogicalBytes"/> when the item reached the Recycle Bin with its identity but its size there could
    /// not be measured (owner decision 2026-10-18, option A): Put back then checks the identity only.</summary>
    public const long UnknownSize = -1;
}

/// <summary>One journal line (docs/spec/05 "Entry"): one per item per outcome. Serialised as one JSON object per line with
/// the spec's field names in camel case; <c>mode</c> is always <c>trash</c>; <c>module</c> and <c>result</c> are the
/// enum names in camel case; times are ISO 8601 UTC; the file id is written as 32 hex digits.</summary>
public sealed record JournalEntry(
    string Id,
    string SessionId,
    DateTimeOffset Time,
    LeanModule Module,
    string OriginalPath,
    string? OriginalParentRealPath,
    string? ApprovedRealPath,
    long? Bytes,
    string? TrashPath,
    TrashedIdentity? TrashedIdentity,
    JournalResult Result,
    string? Reason,
    string AppVersion)
{
    /// <summary>Always <c>trash</c> in v1 (docs/spec/05).</summary>
    public const string Mode = "trash";

    /// <summary>For a <c>restored</c> or <c>restoreFailed</c> entry: the id of the <c>trashed</c> entry it refers to (the
    /// field <c>refersTo</c>, as in the macOS journal), so Put back knows what is already back.</summary>
    public string? RefersTo { get; init; }
}
