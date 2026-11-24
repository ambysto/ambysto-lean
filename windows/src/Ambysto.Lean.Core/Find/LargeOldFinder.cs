using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Find;

/// <summary>Thresholds of docs/spec/03 (Large and old files). Both adjustable.</summary>
public sealed record LargeOldOptions(long MinimumBytes, TimeSpan MinimumAge, bool ShowSystemItems)
{
    /// <summary>The defaults: at least 100 MB, last used more than 12 months ago, system items hidden.</summary>
    public static LargeOldOptions Default { get; } = new(100 * ByteUnits.MB, TimeSpan.FromDays(365), ShowSystemItems: false);
}

/// <summary>One row. On Windows "last used" is always the modification date and the row says "last modified"
/// (<see cref="LastUsedLabel"/>). <see cref="Hint"/> is set for a display-only row.</summary>
public sealed record LargeOldRow(ScanNode Node, DateTimeOffset LastUsed, string LastUsedLabel, TypeGroupKind Type, string? Hint);

/// <summary>Large and old files from the scanned tree (docs/spec/03). Reads only; nothing is pre-selected.</summary>
public static class LargeOldFinder
{
    /// <summary>The files of <paramref name="tree"/> at least as large and as old as <paramref name="options"/> asks,
    /// judged at <paramref name="now"/>, largest first. Display-only rows are left out unless
    /// <see cref="LargeOldOptions.ShowSystemItems"/>.</summary>
    public static IReadOnlyList<LargeOldRow> Find(ScanTree tree, LargeOldOptions options, DateTimeOffset now, PathContext context)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        // "More than <age> ago": a file whose minute is before the cutoff (minutes since 1970, walk rule 8).
        var cutoff = (long)Math.Floor((now - options.MinimumAge - Epoch).TotalMinutes);
        var rows = new List<LargeOldRow>();

        // One iterative pass over the files, through Children (it takes the folder's lock when it sorts, so a map opening a
        // folder on the UI thread at the same time cannot change the list under this pass).
        var pending = new Stack<ScanNode>();
        pending.Push(tree.Root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.IsFolder)
            {
                foreach (var child in node.Children)
                {
                    pending.Push(child);
                }

                continue;
            }

            // Size is the allocated size (spec 01, walk rule 3). A link is never a row; an unreadable entry has no date to trust.
            if (node.AllocatedBytes < options.MinimumBytes
                || node.LastWriteMinutes >= cutoff
                || (node.Flags & (NodeMarks.Link | NodeMarks.Unreadable)) != 0)
            {
                continue;
            }

            // Per row, and only for rows over both thresholds: a hint means display-only.
            var hint = DisplayOnly.HintFor(node.Path, context);
            if (hint is not null && !options.ShowSystemItems)
            {
                continue;
            }

            rows.Add(new LargeOldRow(node, Epoch.AddMinutes(node.LastWriteMinutes), Text.LastModified, TypeGroup.Of(node.Name), hint));
        }

        // Paths are joined once per row, not on every comparison.
        var keyed = rows.Select(row => (Row: row, Path: row.Node.Path)).ToList();
        keyed.Sort(static (a, b) =>
        {
            var bySize = b.Row.Node.AllocatedBytes.CompareTo(a.Row.Node.AllocatedBytes);
            return bySize != 0 ? bySize : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return keyed.Select(k => k.Row).ToList();
    }

    private static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
