using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Trash;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>A disjoint accounting of approved leftovers, derived only from actual outcomes.</summary>
public sealed record UninstallCleanupSummary(int Approved, int Moved, int Unsuccessful, int Unattempted, bool Complete, bool Recoverable)
{
    /// <summary>Journaled, verified moves without warnings are successful; every other attempted outcome remains unsuccessful.</summary>
    public static UninstallCleanupSummary From(int approved, IReadOnlyList<TrashOutcome> outcomes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(approved);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count > approved) throw new ArgumentException("More outcomes than approved items", nameof(outcomes));
        var successful = outcomes.Count(o => o.Result == TrashResult.Trashed && o.Entry is not null && string.IsNullOrEmpty(o.Reason));
        return new(approved, successful, outcomes.Count - successful, approved - outcomes.Count,
            approved > 0 && successful == approved, outcomes.Any(o => o.Result == TrashResult.Trashed && o.Entry is not null));
    }
}
