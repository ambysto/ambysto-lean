using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Find;

/// <summary>ASP-15 (owner decision 2026-10-18): the rules the user may pick to choose the kept copy of every group shown.
/// Last-access time is not offered: Windows often does not update it.</summary>
public enum KeepRule
{
    /// <summary>The copy modified last (minutes, as the suggested keep); ties: the shortest path, then alphabetical.</summary>
    NewestModified,

    /// <summary>The copy modified first; ties: the shortest path, then alphabetical.</summary>
    OldestModified,

    /// <summary>The copy with the fewest characters in its full path; ties: the newest modified, then alphabetical.</summary>
    ShortestPath,

    /// <summary>Among the copies inside the chosen folder (the folder itself or below, canonical paths), the newest
    /// modified; no copy inside: the group is left untouched.</summary>
    InsideFolder,
}

/// <summary>ASP-15: which copy a rule keeps. HARD (the kept-copy rule): a rule only chooses; ticking goes through
/// <see cref="DuplicateSelection.TickAllBut"/>, which never ticks the kept copy, a hinted copy or the last copy.</summary>
public static class DuplicateRules
{
    /// <summary>The copy of <paramref name="group"/> that <paramref name="rule"/> keeps, or null to leave the group untouched
    /// (<see cref="KeepRule.InsideFolder"/> with no copy inside <paramref name="folder"/>). A display-only copy may be the kept
    /// one. <paramref name="folder"/> is required for <see cref="KeepRule.InsideFolder"/> and ignored otherwise.</summary>
    public static DuplicateCopy? Keep(DuplicateGroup group, KeepRule rule, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        switch (rule)
        {
            case KeepRule.NewestModified:
                return Best(group.Copies, Newest);
            case KeepRule.OldestModified:
                return Best(group.Copies, (a, b) => Compare(a.Node.LastWriteMinutes, b.Node.LastWriteMinutes, a, b, lengthFirst: true));
            case KeepRule.ShortestPath:
                return Best(group.Copies, (a, b) => a.Node.Path.Length != b.Node.Path.Length
                    ? a.Node.Path.Length.CompareTo(b.Node.Path.Length)
                    : Compare(b.Node.LastWriteMinutes, a.Node.LastWriteMinutes, a, b, lengthFirst: false));
            case KeepRule.InsideFolder:
                // The folder and each copy compared as canonical paths (".." resolved, no trailing separator, case and short
                // names); inside means below the folder by whole components, so D:\Photos2 is not inside D:\Photos.
                var canonicalFolder = string.IsNullOrWhiteSpace(folder)
                    ? throw new ArgumentException("A folder is required for the inside-a-folder rule.", nameof(folder))
                    : CanonicalPath.Of(folder) ?? throw new ArgumentException("The folder is not a usable path.", nameof(folder));
                var inside = group.Copies.Where(c => CanonicalPath.Of(c.Node.Path) is { } path && CanonicalPath.Depth(canonicalFolder, path) > 0).ToList();
                return inside.Count == 0 ? null : Best(inside, Newest);
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown keep rule.");
        }
    }

    // Newest modified first; ties: the shorter path, then the path order.
    private static int Newest(DuplicateCopy a, DuplicateCopy b) => Compare(b.Node.LastWriteMinutes, a.Node.LastWriteMinutes, a, b, lengthFirst: true);

    // The first copy in the order <paramref name="order"/> gives (negative: the first argument comes first).
    private static DuplicateCopy Best(IReadOnlyList<DuplicateCopy> copies, Comparison<DuplicateCopy> order)
    {
        var best = copies[0];
        foreach (var copy in copies)
        {
            if (order(copy, best) < 0)
            {
                best = copy;
            }
        }

        return best;
    }

    // Orders two copies whose primary keys are <paramref name="first"/> (for a) and <paramref name="second"/> (for b), lower
    // first; ties: the shorter path (when <paramref name="lengthFirst"/>), then the path order of the suggested keep (case
    // ignored, then ordinal).
    private static int Compare(long first, long second, DuplicateCopy a, DuplicateCopy b, bool lengthFirst)
    {
        if (first != second)
        {
            return first.CompareTo(second);
        }

        if (lengthFirst && a.Node.Path.Length != b.Node.Path.Length)
        {
            return a.Node.Path.Length.CompareTo(b.Node.Path.Length);
        }

        var byPath = string.Compare(a.Node.Path, b.Node.Path, StringComparison.OrdinalIgnoreCase);
        return byPath != 0 ? byPath : string.CompareOrdinal(a.Node.Path, b.Node.Path);
    }
}
