using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>ASP-14 (intent/2026-10-18-asp-14-start-and-sidebar.md, "Core"): <c>ScanProgress.Zones</c>, one entry per
/// top-level folder with <c>(Path, AllocatedBytes, Done, Colour, Name)</c>. A zone is done when the walk has read its last
/// subfolder; its colour is computed then, from its finished subtree, and is the colour the final map gives that folder
/// (docs/spec/01: the colour of the file reached by following the largest child). Written from the contract against its
/// shape: a zone's members are read by name only, so a record or a named tuple both fit.
/// The walk is slowed by the internal seam of <c>--scan-delay</c> (a wait after each folder batch), so the zones finish
/// at different times whatever the machine: <see cref="Quick"/> is one folder, <see cref="Mid"/> a chain of
/// <see cref="MidDepth"/> folders, <see cref="Deep"/> a chain of <see cref="DeepDepth"/>, <see cref="Wide"/> fans out to
/// <see cref="WideFolders"/> sibling folders, and <see cref="Empty"/> holds nothing. Every folder but Empty holds at least
/// one file of 4 KB or more, so a zone whose bytes equal its final total has had every one of its folders read. Each
/// chain's largest file is at its bottom, so a colour computed before the zone is done is the colour of a small file
/// (Other), not the final one. Not parallel with other tests: the walk is timed.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ScanZonesTests
{
    private const string Quick = "quick";
    private const string Mid = "mid";
    private const string Deep = "deep";
    private const string Wide = "wide";
    private const string Empty = "empty";
    private const int MidDepth = 4;
    private const int DeepDepth = 12;
    private const int WideFolders = 20;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
    private static TempFolder? fixture;

    /// <summary>The colour each zone must end with (the largest file at the end of its largest-child chain).</summary>
    private static readonly (string Zone, TypeGroupKind Colour)[] ExpectedColours =
    [
        (Quick, TypeGroupKind.Video),
        (Mid, TypeGroupKind.Archive),
        (Deep, TypeGroupKind.Document),
        (Wide, TypeGroupKind.DiskImage),
        (Empty, TypeGroupKind.Other),
    ];

    [ClassInitialize]
    public static void BuildFixture(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = new TempFolder("scan-zones");

        // quick: one folder, its largest file a video.
        fixture.File(Quick + @"\clip.mp4", 64 * ByteUnits.KB, 1);
        fixture.File(Quick + @"\note.bin", 8 * ByteUnits.KB, 2);

        // mid and deep: chains; each level holds small Other files, the bottom the zone's largest file.
        Chain(Mid, MidDepth, "bottom.zip", 200 * ByteUnits.KB);
        Chain(Deep, DeepDepth, "bottom.pdf", 300 * ByteUnits.KB);

        // wide: many sibling folders, the largest holding a disk image; plus a file at its own level.
        fixture.File(Wide + @"\top.bin", 4 * ByteUnits.KB, 3);
        for (var i = 0; i < WideFolders; i++)
        {
            var name = "w" + i.ToString("D2", CultureInfo.InvariantCulture);
            if (i == 7)
            {
                fixture.File(Wide + @"\" + name + @"\image.iso", 160 * ByteUnits.KB, 4);
            }
            else
            {
                fixture.File(Wide + @"\" + name + @"\part.bin", 4 * ByteUnits.KB, 5 + i);
            }
        }

        fixture.Folder(Empty);
        fixture.File("root.bin", 4 * ByteUnits.KB, 9);
    }

    private static void Chain(string zone, int depth, string bottomName, long bottomBytes)
    {
        var relative = zone;
        for (var level = 0; level < depth; level++)
        {
            fixture!.File(relative + @"\small" + level.ToString(CultureInfo.InvariantCulture) + ".bin", 4 * ByteUnits.KB, 10 + level);
            fixture.File(relative + @"\other" + level.ToString(CultureInfo.InvariantCulture) + ".bin", 8 * ByteUnits.KB, 30 + level);
            if (level == depth - 1)
            {
                fixture.File(relative + @"\" + bottomName, bottomBytes, 50);
            }
            else
            {
                relative += @"\l" + level.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    [ClassCleanup]
    public static void RemoveFixture() => fixture?.Dispose();

    /// <summary>Every report, in the order it arrived (the scanner reports from one thread at a time).</summary>
    private sealed class Recorder : IProgress<ScanProgress>
    {
        public ConcurrentQueue<ScanProgress> Reports { get; } = new();

        public void Report(ScanProgress value) => Reports.Enqueue(value);
    }

    /// <summary>One zone of one report, by value.</summary>
    private sealed record Zone(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name);

    private sealed record Walked(ScanTree Tree, List<List<Zone>> Snapshots);

    private static async Task<Walked> Walk()
    {
        var recorder = new Recorder();
        var tree = await Scanner.ScanAsync(fixture!.Path, recorder, Delay, CancellationToken.None);
        Assert.IsFalse(tree.Cancelled, "precondition: the walk finished");
        var snapshots = recorder.Reports.Select(r => r.Zones.Select(z => new Zone(z.Path, z.AllocatedBytes, z.Done, z.Colour, z.Name)).ToList()).ToList();
        Check.AtLeast(3, snapshots.Count, "precondition: reports during the slow walk");
        return new Walked(tree, snapshots);
    }

    private static string ZonePath(string zone) => Path.Combine(fixture!.Path, zone);

    /// <summary>The final tree's node of each top-level folder, by full path.</summary>
    private static Dictionary<string, ScanNode> FinalFolders(ScanTree tree) =>
        tree.Root.Children.Where(c => c.IsFolder).ToDictionary(c => c.Path, c => c, StringComparer.OrdinalIgnoreCase);

    private static string Describe(List<Zone> snapshot) =>
        "[" + string.Join(" | ", snapshot.Select(z => $"{Path.GetFileName(z.Path)} {z.AllocatedBytes} {(z.Done ? "done " + z.Colour : "reading")}")) + "]";

    // Contract "Core": a zone is done when the walk has read its last subfolder. In every report, a zone marked done has
    // measured exactly its final total (every folder of the fixture but Empty holds bytes, so a folder of the zone still
    // unread would leave it short), and the walk is drawn zone by zone: some report has a zone done while Deep, the
    // longest chain, is still being read.
    [TestMethod]
    [Timeout(120_000)]
    public async Task AZoneIsDoneOnlyAfterItsLastSubfolderIsRead()
    {
        var walked = await Walk();
        var final = FinalFolders(walked.Tree);
        var deep = ZonePath(Deep);
        var problems = new List<string>();
        var someDoneWhileDeepReads = false;
        var deepSeenReading = false;
        for (var i = 0; i < walked.Snapshots.Count; i++)
        {
            var snapshot = walked.Snapshots[i];
            foreach (var zone in snapshot.Where(z => z.Done))
            {
                if (!final.TryGetValue(zone.Path, out var node))
                {
                    problems.Add($"report {i}: a done zone that is not a top-level folder: {zone.Path}");
                }
                else if (zone.AllocatedBytes != node.AllocatedBytes)
                {
                    problems.Add($"report {i}: {node.Name} is done with {zone.AllocatedBytes} bytes measured, but its folders hold {node.AllocatedBytes}: a deeper folder of it was still unread; {Describe(snapshot)}");
                }
            }

            var deepZone = snapshot.FirstOrDefault(z => string.Equals(z.Path, deep, StringComparison.OrdinalIgnoreCase));
            if (deepZone is { Done: false })
            {
                deepSeenReading = true;
                someDoneWhileDeepReads |= snapshot.Any(z => z.Done);
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Check.True(deepSeenReading, $"precondition: no report caught {Deep} (a chain of {DeepDepth} folders, {Delay.TotalMilliseconds} ms per batch) still being read; reports: {string.Join(" ; ", walked.Snapshots.Select(Describe))}");
        Check.True(someDoneWhileDeepReads, $"no report has a zone done while {Deep} is still being read: the zones are not done one by one as each finishes; reports: {string.Join(" ; ", walked.Snapshots.Select(Describe))}");
    }

    // Contract "Core" and "Overview, 2. Scanning": a done zone is filled with its type-group colour, from the scanner,
    // and it is the colour the final map gives that top-level folder (TypeGroup.ColourOf of its final node). Checked in
    // every report where the zone is done, and against the fixture's expected colours; a zone still being read has no
    // colour yet or one that is not claimed final (only done zones are checked).
    [TestMethod]
    [Timeout(120_000)]
    public async Task ADoneZonesColourIsTheColourTheFinalMapGivesItsFolder()
    {
        var walked = await Walk();
        var final = FinalFolders(walked.Tree);
        foreach (var (zone, colour) in ExpectedColours)
        {
            Assert.AreEqual(colour, TypeGroup.ColourOf(final[ZonePath(zone)]), "precondition: the final map's colour of " + zone);
        }

        var problems = new List<string>();
        var checkedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < walked.Snapshots.Count; i++)
        {
            foreach (var zone in walked.Snapshots[i].Where(z => z.Done))
            {
                if (!final.TryGetValue(zone.Path, out var node))
                {
                    continue; // AZoneIsDoneOnlyAfterItsLastSubfolderIsRead reports it
                }

                checkedZones.Add(zone.Path);
                var expected = TypeGroup.ColourOf(node);
                if (zone.Colour != expected)
                {
                    problems.Add($"report {i}: {node.Name} is done with colour {zone.Colour?.ToString() ?? "none"}, the final map gives it {expected}");
                }
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Check.Set(checkedZones.Select(p => Path.GetFileName(p)), ExpectedColours.Select(e => e.Zone).ToArray());
    }

    // Contract "Core" and "Overview, 2. Scanning": a tile is sized by the bytes measured so far, so between reports a
    // zone's bytes never go down, a zone once reported stays reported, a done zone stays done, and no zone ever reports
    // more than its folder's final total.
    [TestMethod]
    [Timeout(120_000)]
    public async Task AZonesBytesNeverGoDownBetweenReports()
    {
        var walked = await Walk();
        var final = FinalFolders(walked.Tree);
        var last = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        for (var i = 0; i < walked.Snapshots.Count; i++)
        {
            var snapshot = walked.Snapshots[i];
            var paths = snapshot.Select(z => z.Path).ToList();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            {
                problems.Add($"report {i}: a zone is listed twice: {Describe(snapshot)}");
            }

            foreach (var gone in last.Keys.Where(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                problems.Add($"report {i}: the zone {gone} was reported before and is missing now");
            }

            foreach (var zone in snapshot)
            {
                Check.True(Path.IsPathFullyQualified(zone.Path), "a zone is keyed by its full path: " + zone.Path);
                if (last.TryGetValue(zone.Path, out var before))
                {
                    if (zone.AllocatedBytes < before.AllocatedBytes)
                    {
                        problems.Add($"report {i}: {Path.GetFileName(zone.Path)} went down from {before.AllocatedBytes} to {zone.AllocatedBytes} bytes");
                    }

                    if (before.Done && !zone.Done)
                    {
                        problems.Add($"report {i}: {Path.GetFileName(zone.Path)} was done and is being read again");
                    }
                }

                if (final.TryGetValue(zone.Path, out var node) && zone.AllocatedBytes > node.AllocatedBytes)
                {
                    problems.Add($"report {i}: {node.Name} reports {zone.AllocatedBytes} bytes, more than its final {node.AllocatedBytes}");
                }

                last[zone.Path] = zone;
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Check.True(last.Count > 0, "no report held a zone");
    }

    // Contract "Core": the end-of-scan steps run after every zone is done, so the last report of a finished scan holds
    // every top-level folder of the final tree (and nothing else) as a done zone, with its final total and the final map's
    // colour.
    [TestMethod]
    [Timeout(120_000)]
    public async Task EveryZoneIsDoneInTheLastReportOfAFinishedScan()
    {
        var walked = await Walk();
        var final = FinalFolders(walked.Tree);
        var lastReport = walked.Snapshots[^1];
        Check.Set(lastReport.Select(z => z.Path), final.Keys.ToArray());
        var problems = new List<string>();
        foreach (var zone in lastReport)
        {
            var node = final[zone.Path];
            if (!zone.Done)
            {
                problems.Add($"{node.Name} is not done in the last report");
            }

            if (zone.AllocatedBytes != node.AllocatedBytes)
            {
                problems.Add($"{node.Name}: {zone.AllocatedBytes} bytes in the last report, {node.AllocatedBytes} in the final tree");
            }

            if (zone.Colour != TypeGroup.ColourOf(node))
            {
                problems.Add($"{node.Name}: colour {zone.Colour?.ToString() ?? "none"} in the last report, {TypeGroup.ColourOf(node)} on the final map");
            }
        }

        Assert.AreEqual(0, problems.Count, "last report " + Describe(lastReport) + ":\n" + string.Join("\n", problems));
    }
}
