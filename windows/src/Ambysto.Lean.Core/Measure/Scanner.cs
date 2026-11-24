using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Map;

namespace Ambysto.Lean.Core.Measure;

/// <summary>One progress report: top-level folder totals so far (docs/spec/01, Progress and cancel: at most 4 per
/// second, from the first second). <c>TopLevelAllocatedBytes</c> is keyed by the full path of each folder directly in the
/// root (files directly in the root have no folder total). <c>Zones</c> holds the same folders as values only (ASP-14, the map
/// drawn during the scan): <c>Path</c>, the bytes measured so far, <c>Done</c> when the walk has read the zone's last subfolder,
/// the <c>Colour</c> of its type group (null until done) and its <c>Name</c>. No <see cref="ScanNode"/> crosses threads during a scan.
/// <c>MeasuredBytes</c> counts every file seen so far, files directly in the root included (the progress line's percent).
/// <c>Reading</c> is the path of the folder a worker began reading last (the readout's "reading" line), or null before the first.</summary>
public sealed record ScanProgress(
    TimeSpan Elapsed,
    long FilesSeen,
    IReadOnlyList<KeyValuePair<string, long>> TopLevelAllocatedBytes,
    IReadOnlyList<(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)> Zones,
    long MeasuredBytes = 0,
    string? Reading = null);

/// <summary>The walker of docs/spec/01 (walk rules and their Windows notes). Reads only: never opens a file's data,
/// never opens a cloud file, never enters a link, never leaves the volume of its root.</summary>
public sealed class Scanner
{
    /// <summary>Walks <paramref name="root"/> (a folder or a volume root; a file gives a tree of that one node). On cancel
    /// it returns within 1 second with what was measured so far and <see cref="ScanTree.Cancelled"/> set; it does not
    /// throw for a cancel. A volume whose file system cannot list file ids (<c>FileIdExtdDirectoryInfo</c>, so not FAT
    /// or exFAT) reports the root as unreadable: Lean scans the profile's volume. A <see cref="Progress{T}"/> sink posts
    /// each report asynchronously, so a report may arrive after this method has returned; a caller that needs none after
    /// the return passes a synchronous <see cref="IProgress{T}"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The public contract is an instance method (plan: Scanner.ScanAsync).")]
    public async Task<ScanTree> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        return await ScanAsync(root, progress, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The walk with a wait after each folder batch: for the app's QA argument <c>--scan-delay</c> only, so a test can
    /// cancel a scan mid-walk. Internal on purpose: it is not part of the public surface.</summary>
    internal static async Task<ScanTree> ScanAsync(string root, IProgress<ScanProgress>? progress, TimeSpan batchDelay, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        using var run = new ScanRun(root, progress, cancellationToken, batchDelay);
        return await run.RunAsync().ConfigureAwait(false);
    }
}
