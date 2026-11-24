using System;
using System.Collections.Generic;
using System.IO;
using Ambysto.Lean.Core.Map;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Find;

/// <summary>The age a temporary item must have reached (docs/spec/03 "Temporary files", owner decision 2026-10-18).</summary>
public enum TempAge
{
    /// <summary>7 days (the default).</summary>
    Week,

    /// <summary>1 calendar month.</summary>
    Month,

    /// <summary>3 calendar months.</summary>
    Quarter,
}

/// <summary>One row of Temporary files: a direct child of a temporary folder, its newest last write over its whole tree,
/// and whether it may be moved (<see cref="DisplayOnly"/> with <see cref="Hint"/> when not).</summary>
public sealed record TempItem(ScanNode Node, DateTime NewestWriteUtc, bool DisplayOnly, string? Hint);

/// <summary>Temporary files (docs/spec/03, HARD): the direct children of <c>%LOCALAPPDATA%\Temp</c> and
/// <c>%LOCALAPPDATA%\CrashDumps</c> whose newest last write over their whole tree is older than the age; a child holding
/// a link or a cloud file is display-only with the walk's hint; <c>C:\Windows\Temp</c> is one display-only row. Read with
/// Lean's walker, no file opened for data. Nothing is ever selected by this finder.</summary>
public static class TempFinder
{
    /// <summary>The rows for <paramref name="context"/> and <paramref name="age"/>, judged at <paramref name="time"/>'s now.</summary>
    public static async Task<IReadOnlyList<TempItem>> FindAsync(PathContext context, TempAge age, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = age switch
        {
            TempAge.Week => now.AddDays(-7),
            TempAge.Month => now.AddMonths(-1),
            TempAge.Quarter => now.AddMonths(-3),
            _ => throw new ArgumentOutOfRangeException(nameof(age)),
        };

        var rows = new List<TempItem>();
        foreach (var folder in new[] { Path.Join(context.LocalAppData, "Temp"), Path.Join(context.LocalAppData, "CrashDumps") })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var tree = await new Scanner().ScanAsync(folder, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in tree.Root.Children)
            {
                var newest = NewestWriteUtc(child);
                if (newest >= cutoff)
                {
                    continue;
                }

                // Never-touch first (the user's protected folders, a sync root): the general hint; then a row that is not
                // whole (a link, a cloud file or something unreadable anywhere in it) is display-only too.
                var hint = DisplayOnly.HintFor(child.Path, context) ?? (IsWhole(child) ? null : Copy.Text.HintTempNotWhole);
                rows.Add(new TempItem(child, newest, hint is not null, hint));
            }
        }

        // The machine's temporary folder needs an administrator: one display-only row, never measured.
        if (WindowsTemp(context) is { } windowsTemp && Directory.Exists(windowsTemp))
        {
            rows.Insert(0, new TempItem(ScanNode.Folder(windowsTemp, 0, null, NodeMarks.Unreadable), DateTime.MinValue, true, Copy.Text.HintSystemWide));
        }

        return rows;
    }

    // The newest last write over the node's whole tree: a folder is as recent as anything inside it.
    private static DateTime NewestWriteUtc(ScanNode node)
    {
        var newest = node.LastWriteMinutes;
        var pending = new Stack<ScanNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().Children)
            {
                newest = Math.Max(newest, child.LastWriteMinutes);
                pending.Push(child);
            }
        }

        return DateTime.UnixEpoch.AddMinutes(newest);
    }

    private static bool IsWhole(ScanNode node)
    {
        const NodeMarks notWhole = NodeMarks.Link | NodeMarks.CloudNotDownloaded | NodeMarks.Unreadable;
        var pending = new Stack<ScanNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((current.Flags & notWhole) != 0)
            {
                return false;
            }

            foreach (var child in current.Children)
            {
                pending.Push(child);
            }
        }

        return true;
    }

    // %windir%\Temp, from the context's system folders (the one named Windows), else the running system's.
    private static string? WindowsTemp(PathContext context)
    {
        foreach (var folder in context.SystemFolders)
        {
            if (string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "Windows", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Join(folder, "Temp");
            }
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 ? Path.Join(windows, "Temp") : null;
    }
}
