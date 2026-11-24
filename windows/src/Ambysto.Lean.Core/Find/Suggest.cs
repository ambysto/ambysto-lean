using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Find;

/// <summary>Why a file was suggested in Large and old (ASP-17).</summary>
public enum SuggestionKind
{
    /// <summary>A setup file or disk image in Downloads, unchanged for more than 90 days.</summary>
    OldInstaller,

    /// <summary>1 GiB or more, unchanged for more than 365 days.</summary>
    LargeUnused,
}

/// <summary>A Temporary files row that Suggest ticks, with the reason.</summary>
public sealed record SuggestedTemp(TempItem Item, string Why);

/// <summary>A Large and old file that Suggest ticks, with its kind and the reason.</summary>
public sealed record SuggestedFile(ScanNode Node, DateTimeOffset LastModified, TypeGroupKind Type, SuggestionKind Kind, string Why);

/// <summary>The Suggest rules (ASP-17, intent/2026-10-20-asp-17-suggest.md, phase A). They read only and tick nothing: they name
/// the rows Suggest may tick, each with its reason, and the gate still decides at move time. A row Lean may not move (a hint, a
/// display-only Temp row) and a node whose move would free nothing or could not be checked (a link, an unreadable entry, a cloud
/// file not on this disk, a hard link counted elsewhere) is never suggested. Duplicates has no rule here: Suggest there is the
/// Newest keep rule (<see cref="DuplicateRules.Keep"/>, then <see cref="DuplicateSelection.TickAllBut"/>).</summary>
public static class Suggest
{
    /// <summary>Temporary rows unchanged for one month or more (owner, 2026-10-20), whatever age the view shows.</summary>
    public static IReadOnlyList<SuggestedTemp> Temp(IReadOnlyList<TempItem> items, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);

        // TempFinder's Month age: a row is listed when its newest write is before now minus one calendar month.
        var cutoff = nowUtc.AddMonths(-1);
        var result = new List<SuggestedTemp>();
        foreach (var item in items)
        {
            // TempFinder already makes a row display-only when anything in it is a link, a cloud file or unreadable; the row's own
            // marks are checked here as well, as every rule checks them.
            if (item.DisplayOnly || item.Hint is not null || (item.Node.Flags & NeverSuggested) != 0 || item.NewestWriteUtc >= cutoff)
            {
                continue;
            }

            result.Add(new SuggestedTemp(item, Text.WhyOldTemp(item.NewestWriteUtc)));
        }

        return result;
    }

    /// <summary>The setup files and disk images Downloads holds, and the files of 1 GiB or more, that have not changed for long
    /// enough; files only, from the whole scan. <paramref name="downloads"/> is the Downloads known folder (null: no installer
    /// rule). Largest first, then by path.</summary>
    public static IReadOnlyList<SuggestedFile> LargeOld(ScanTree tree, DateTimeOffset now, PathContext context, string? downloads)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(context);

        // "More than <age> before now", on whole minutes since 1970 as LargeOldFinder judges it (walk rule 8).
        var installerCutoff = CutoffMinutes(now, InstallerAge);
        var largeCutoff = CutoffMinutes(now, LargeAge);
        var canonicalDownloads = downloads is null ? null : CanonicalPath.Of(downloads);

        var rows = new List<SuggestedFile>();
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

            if ((node.Flags & NeverSuggested) != 0)
            {
                continue;
            }

            // The cheap tests first; the path is joined and made canonical only for a file that passes them.
            var installerCandidate = canonicalDownloads is not null && node.LastWriteMinutes < installerCutoff && IsInstallerName(node.Name);
            var largeCandidate = node.AllocatedBytes >= ByteUnits.GB && node.LastWriteMinutes < largeCutoff;
            if (!installerCandidate && !largeCandidate)
            {
                continue;
            }

            var path = node.Path;
            if (installerCandidate && !StrictlyInside(canonicalDownloads!, path))
            {
                installerCandidate = false;
                if (!largeCandidate)
                {
                    continue;
                }
            }

            if (DisplayOnly.HintFor(path, context) is not null)
            {
                continue;
            }

            var modified = Epoch.AddMinutes(node.LastWriteMinutes);
            rows.Add(installerCandidate
                ? new SuggestedFile(node, modified, TypeGroup.Of(node.Name), SuggestionKind.OldInstaller, Text.WhyOldInstaller(modified))
                : new SuggestedFile(node, modified, TypeGroup.Of(node.Name), SuggestionKind.LargeUnused, Text.WhyLargeUnused(modified)));
        }

        var keyed = rows.Select(row => (Row: row, Path: row.Node.Path)).ToList();
        keyed.Sort(static (a, b) =>
        {
            var bySize = b.Row.Node.AllocatedBytes.CompareTo(a.Row.Node.AllocatedBytes);
            if (bySize != 0)
            {
                return bySize;
            }

            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.Path, b.Path);
        });
        return keyed.Select(k => k.Row).ToList();
    }

    // A link is never followed or moved by a suggestion, an unreadable entry has no date to trust, a cloud file not on this disk
    // frees nothing here (and the gate refuses it), and a hard link counted at another path frees nothing either.
    private const NodeMarks NeverSuggested = NodeMarks.Link | NodeMarks.Unreadable | NodeMarks.CloudNotDownloaded | NodeMarks.HardLinkCountedElsewhere;

    private static readonly TimeSpan InstallerAge = TimeSpan.FromDays(90);
    private static readonly TimeSpan LargeAge = TimeSpan.FromDays(365);

    // Setup files and disk images (the main session's reading of the owner's ".exe, .msi, .iso, .zip and similar").
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso", ".img", ".zip", ".7z", ".rar",
    };

    private static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static long CutoffMinutes(DateTimeOffset now, TimeSpan age) => (long)Math.Floor((now - age - Epoch).TotalMinutes);

    private static bool IsInstallerName(string name) => InstallerExtensions.Contains(Path.GetExtension(name));

    // Below the folder on a component boundary, never the folder itself; a path that cannot be made canonical is not inside. The
    // walk spells its paths in long names under the scan root, so a file in Downloads already matches on its text, and only those
    // few files are made canonical (on the disk) to confirm it. A path that might still name Downloads another way (a short 8.3
    // component, a \\?\ or UNC form) is made canonical as well; any other old .exe or .zip on the volume costs no disk access
    // (hard review round 1). A miss only loses a suggestion, never adds one.
    private static bool StrictlyInside(string canonicalFolder, string path)
    {
        if (CanonicalPath.Depth(canonicalFolder, path) <= 0 && !path.Contains('~', StringComparison.Ordinal) && !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return CanonicalPath.Of(path) is { } item && CanonicalPath.Depth(canonicalFolder, item) > 0;
    }
}
