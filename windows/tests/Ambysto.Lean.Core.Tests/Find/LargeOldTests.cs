using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03, "Large and old files" with its Windows notes: defaults 100 MB and 12 months, both adjustable;
/// "last used" is always the modification date and every row says "last modified"; type groups incl. .vhdx and .vhd;
/// rows outside the allowed roots are display-only with the hint, hidden unless "Show system items"; nothing pre-selected.</summary>
[TestClass]
public sealed class LargeOldTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TwelveMonths = Now - Now.AddMonths(-12);

    private static long MinutesAgo(TimeSpan age) => Trees.Minutes(Now - age);

    private static PathContext ContextFor(string profile) =>
        new(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"), Path.GetPathRoot(profile)!,
            Path.GetDirectoryName(profile)!, [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"], []);

    // docs/spec/03 Large and old: defaults: size ≥ 100 MB, last used more than 12 months ago; system items hidden.
    [TestMethod]
    public void DefaultsAre100MbAnd12MonthsWithSystemItemsHidden()
    {
        var options = LargeOldOptions.Default;
        Assert.AreEqual(100 * ByteUnits.MB, options.MinimumBytes);
        Check.True(options.MinimumAge >= TimeSpan.FromDays(365) && options.MinimumAge <= TimeSpan.FromDays(366), "12 months: " + options.MinimumAge);
        Assert.IsFalse(options.ShowSystemItems);
    }

    // docs/spec/03 Large and old: size ≥ 100 MB (inclusive at the threshold).
    [TestMethod]
    public void SizeThresholdIsInclusive()
    {
        using var fx = new ProfileFixture("largeold-size");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(400));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "exactly.bin"), 100 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "one-less.bin"), (100 * ByteUnits.MB) - 1, old))
                .Add(Trees.File(Path.Combine(docs, "bigger.bin"), 101 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, 301 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Set(rows.Select(r => r.Node.Name).ToArray(), "bigger.bin", "exactly.bin");
    }

    // docs/spec/03 Large and old: last used more than 12 months ago.
    [TestMethod]
    public void AgeThresholdExcludesRecentFiles()
    {
        using var fx = new ProfileFixture("largeold-age");
        var docs = fx.Documents;
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "thirteen-months.bin"), 200 * ByteUnits.MB, MinutesAgo(TwelveMonths + TimeSpan.FromDays(30))))
                .Add(Trees.File(Path.Combine(docs, "eleven-months.bin"), 200 * ByteUnits.MB, MinutesAgo(TwelveMonths - TimeSpan.FromDays(30))))
                .Add(Trees.File(Path.Combine(docs, "yesterday.bin"), 200 * ByteUnits.MB, MinutesAgo(TimeSpan.FromDays(1)))));
        var tree = Trees.Tree(root, 600 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(rows.Select(r => r.Node.Name).ToArray(), "thirteen-months.bin");
    }

    // docs/spec/03 Large and old: both adjustable.
    [TestMethod]
    public void ThresholdsAreAdjustable()
    {
        using var fx = new ProfileFixture("largeold-adjust");
        var docs = fx.Documents;
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "two-mb-last-week.bin"), 2 * ByteUnits.MB, MinutesAgo(TimeSpan.FromDays(7)))));
        var tree = Trees.Tree(root, 2 * ByteUnits.MB);
        Assert.AreEqual(0, LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context).Count);
        var loose = new LargeOldOptions(ByteUnits.MB, TimeSpan.FromDays(1), ShowSystemItems: false);
        Assert.AreEqual(1, LargeOldFinder.Find(tree, loose, Now, fx.Context).Count);
    }

    // docs/spec/03 Windows note: "last used" is always the modification date and every row says "last modified".
    [TestMethod]
    public void EveryRowShowsTheModificationDateLabelledLastModified()
    {
        using var fx = new ProfileFixture("largeold-label");
        var docs = fx.Documents;
        var written = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "a.bin"), 200 * ByteUnits.MB, written))
                .Add(Trees.File(Path.Combine(docs, "b.bin"), 300 * ByteUnits.MB, written - 10)));
        var tree = Trees.Tree(root, 500 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Assert.AreEqual(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.AreEqual(Text.LastModified, row.LastUsedLabel);
            Assert.AreEqual("last modified", row.LastUsedLabel);
            Assert.AreEqual(Trees.FromMinutes(row.Node.LastWriteMinutes), row.LastUsed);
        }
    }

    // docs/spec/03 Large and old: each row's type group; Windows note: .vhdx and .vhd are virtual machine images.
    [TestMethod]
    public void RowsCarryTheirTypeGroupIncludingVhdxAndVhd()
    {
        using var fx = new ProfileFixture("largeold-type");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "vm.vhdx"), 900 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "legacy.vhd"), 800 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "film.mp4"), 700 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "bundle.zip"), 600 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, 3000 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context).ToDictionary(r => r.Node.Name, r => r, StringComparer.Ordinal);
        Assert.AreEqual(TypeGroupKind.VirtualMachineImage, rows["vm.vhdx"].Type);
        Assert.AreEqual(TypeGroupKind.VirtualMachineImage, rows["legacy.vhd"].Type);
        Assert.AreEqual(TypeGroupKind.Video, rows["film.mp4"].Type);
        Assert.AreEqual(TypeGroupKind.Archive, rows["bundle.zip"].Type);
    }

    // docs/spec/03 Large and old: rows outside the allowed roots are display-only with the hint, hidden by default,
    // shown with "Show system items".
    [TestMethod]
    public void DisplayOnlyRowsAreHiddenUnlessShowSystemItems()
    {
        using var fx = new ProfileFixture("largeold-display-only");
        var docs = fx.Documents;
        var cache = Path.Combine(fx.LocalAppData, "Contoso", "Cache");
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs).Add(Trees.File(Path.Combine(docs, "mine.iso"), 500 * ByteUnits.MB, old)))
            .Add(ScanNode.Folder(fx.InProfile("AppData"))
                .Add(ScanNode.Folder(fx.LocalAppData)
                    .Add(ScanNode.Folder(Path.Combine(fx.LocalAppData, "Contoso"))
                        .Add(ScanNode.Folder(cache)
                            .Add(Trees.File(Path.Combine(cache, "blob.bin"), 600 * ByteUnits.MB, old))))))
            .Add(Trees.File(fx.InProfile("loose.iso"), 700 * ByteUnits.MB, old));
        var tree = Trees.Tree(root, 1800 * ByteUnits.MB);

        var hidden = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(hidden.Select(r => r.Node.Name).ToArray(), "mine.iso");
        Assert.IsNull(hidden[0].Hint);

        var shown = LargeOldFinder.Find(tree, LargeOldOptions.Default with { ShowSystemItems = true }, Now, fx.Context);
        Check.Set(shown.Select(r => r.Node.Name).ToArray(), "mine.iso", "blob.bin", "loose.iso");
        var blob = shown.Single(r => r.Node.Name == "blob.bin");
        var loose = shown.Single(r => r.Node.Name == "loose.iso");
        Assert.AreEqual(DisplayOnly.HintFor(blob.Node.Path, fx.Context), blob.Hint);
        Check.True(!string.IsNullOrWhiteSpace(blob.Hint), "a display-only row says why");
        Assert.AreEqual(Text.HintOutside, loose.Hint);
        Assert.IsNull(shown.Single(r => r.Node.Name == "mine.iso").Hint);
    }

    // docs/spec/03 Large and old: rows outside the allowed roots are display-only with the where-to-manage hint (01),
    // hidden by default, shown with "Show system items": a file in a cloud-synced folder and a device backup (never-touch,
    // docs/spec/02 Windows note; intent decisions 5 and 8) are such rows, with the hints of docs/spec/01's Windows notes.
    [TestMethod]
    public void SyncedAndDeviceBackupFilesAreDisplayOnlyRowsWithTheirHints()
    {
        using var fx = new ProfileFixture("largeold-never-touch");
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var synced = Path.Combine(fx.OneDrive, "Videos", "holiday.mp4");
        var backupFolder = fx.InProfile("Apple", "MobileSync", "Backup", "00008030-000A1B2C3D4E5F6G");
        var backup = Path.Combine(backupFolder, "Manifest.db");
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(fx.Documents).Add(Trees.File(Path.Combine(fx.Documents, "mine.iso"), 500 * ByteUnits.MB, old)))
            .Add(ScanNode.Folder(fx.OneDrive)
                .Add(ScanNode.Folder(Path.Combine(fx.OneDrive, "Videos")).Add(Trees.File(synced, 600 * ByteUnits.MB, old))))
            .Add(ScanNode.Folder(fx.InProfile("Apple"))
                .Add(ScanNode.Folder(fx.InProfile("Apple", "MobileSync"))
                    .Add(ScanNode.Folder(fx.InProfile("Apple", "MobileSync", "Backup"))
                        .Add(ScanNode.Folder(backupFolder).Add(Trees.File(backup, 700 * ByteUnits.MB, old))))));
        var tree = Trees.Tree(root, 1800 * ByteUnits.MB);

        var hidden = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(hidden.Select(r => r.Node.Name), "mine.iso");

        var shown = LargeOldFinder.Find(tree, LargeOldOptions.Default with { ShowSystemItems = true }, Now, fx.Context);
        Check.Set(shown.Select(r => r.Node.Name), "mine.iso", "holiday.mp4", "Manifest.db");
        Assert.AreEqual(Text.HintSynced("OneDrive"), shown.Single(r => r.Node.Name == "holiday.mp4").Hint);
        Assert.AreEqual(Text.HintDeviceBackups, shown.Single(r => r.Node.Name == "Manifest.db").Hint);
        Assert.IsNull(shown.Single(r => r.Node.Name == "mine.iso").Hint);
    }

    // docs/spec/03 Large and old: largest first.
    [TestMethod]
    public void RowsAreLargestFirst()
    {
        using var fx = new ProfileFixture("largeold-order");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "m.bin"), 200 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "l.bin"), 900 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "s.bin"), 150 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, 1250 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(rows.Select(r => r.Node.Name).ToArray(), "l.bin", "m.bin", "s.bin");
    }

    // docs/spec/03 Large and old: source is the scanned tree's files; a folder is never a row, nor a link.
    [TestMethod]
    public void FoldersAndLinksAreNeverRows()
    {
        using var fx = new ProfileFixture("largeold-files-only");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs, old)
                .Add(ScanNode.Folder(Path.Combine(docs, "BigFolder"), old)
                    .Add(Trees.File(Path.Combine(docs, "BigFolder", "part1.bin"), 90 * ByteUnits.MB, old))
                    .Add(Trees.File(Path.Combine(docs, "BigFolder", "part2.bin"), 90 * ByteUnits.MB, old)))
                .Add(ScanNode.File(Path.Combine(docs, "link"), 200 * ByteUnits.MB, 0, old, null, NodeMarks.Link)));
        var tree = Trees.Tree(root, 180 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Assert.AreEqual(0, rows.Count, string.Join(", ", rows.Select(r => r.Node.Name)));
    }

    // docs/spec/03 Large and old: nothing is pre-selected; a row carries no selection state at all (the UI passes ids).
    [TestMethod]
    public void RowsCarryNoSelectionState()
    {
        var selectionLike = typeof(LargeOldRow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Select", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Tick", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Check", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name).ToArray();
        Assert.AreEqual(0, selectionLike.Length, string.Join(", ", selectionLike));
    }

    // docs/spec/03 Large and old: "Size is the allocated size (01, walk rule 3)": a file whose length is large but whose
    // allocation is small is not listed; one whose allocation reaches the threshold while its length does not is listed.
    [TestMethod]
    public void ThresholdComparesAllocatedBytesNotLength()
    {
        using var fx = new ProfileFixture("largeold-allocated");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(ScanNode.File(Path.Combine(docs, "sparse.bin"), 200 * ByteUnits.MB, 64 * ByteUnits.KB, old))
                .Add(ScanNode.File(Path.Combine(docs, "rounded.bin"), (100 * ByteUnits.MB) - 1, 100 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, (100 * ByteUnits.MB) + (64 * ByteUnits.KB));
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(rows.Select(r => r.Node.Name), "rounded.bin");
    }

    // docs/spec/03 Large and old, allocated size: a real sparse file of 200 MB length and (nearly) no allocation, scanned
    // by the walker, is not listed.
    [TestMethod]
    public async Task RealSparseFileWithLargeLengthIsNotListed()
    {
        using var fx = new ProfileFixture("largeold-sparse");
        var sparse = fx.InProfile("Documents", "sparse.bin");
        Native.CreateSparseFile(sparse, 200 * ByteUnits.MB);
        File.SetLastWriteTimeUtc(sparse, Now.AddDays(-500).UtcDateTime);
        var tree = await new Scanner().ScanAsync(fx.Profile, null, System.Threading.CancellationToken.None);
        Assert.AreEqual(200 * ByteUnits.MB, Trees.Node(tree, @"Documents\sparse.bin").LogicalBytes);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Assert.AreEqual(0, rows.Count, string.Join(", ", rows.Select(r => r.Node.Name)));
    }

    // docs/spec/03 Large and old: an empty tree gives no rows and no failure.
    [TestMethod]
    public void EmptyTreeGivesNoRows()
    {
        var profile = @"C:\Users\me";
        var tree = Trees.Tree(ScanNode.Folder(profile), 0);
        Assert.AreEqual(0, LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, ContextFor(profile)).Count);
    }

    // docs/spec/03 Large and old: "last used more than 12 months ago": a file modified exactly the minimum age before now
    // (the same minute) is not more than that age, so not a row; one modified a minute earlier is (brief M03). Judged at a
    // whole minute and at half a minute past it.
    [TestMethod]
    public void FileExactlyAtTheAgeThresholdIsNotARow()
    {
        using var fx = new ProfileFixture("largeold-age-edge");
        var age = TimeSpan.FromDays(365);
        var options = new LargeOldOptions(100 * ByteUnits.MB, age, ShowSystemItems: false);
        foreach (var now in new[] { Now, Now.AddSeconds(30) })
        {
            var docs = fx.Documents;
            var atThreshold = Trees.Minutes(now - age);
            var root = ScanNode.Folder(fx.Profile)
                .Add(ScanNode.Folder(docs)
                    .Add(Trees.File(Path.Combine(docs, "exactly.bin"), 200 * ByteUnits.MB, atThreshold))
                    .Add(Trees.File(Path.Combine(docs, "minute-earlier.bin"), 200 * ByteUnits.MB, atThreshold - 1)));
            var tree = Trees.Tree(root, 400 * ByteUnits.MB);
            var rows = LargeOldFinder.Find(tree, options, now, fx.Context);
            Check.Sequence(rows.Select(r => r.Node.Name), "minute-earlier.bin");
        }
    }

    // docs/spec/01 rules 2 and 7, docs/spec/03: a link is never a row, nor an entry that could not be read (its date cannot
    // be trusted), however large and old; an unmarked twin of the same size and age is a row (brief M04).
    [TestMethod]
    public void LinksAndUnreadableFilesAreNeverRows()
    {
        using var fx = new ProfileFixture("largeold-marks");
        var docs = fx.Documents;
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(ScanNode.File(Path.Combine(docs, "unreadable.bin"), 300 * ByteUnits.MB, 300 * ByteUnits.MB, old, null, NodeMarks.Unreadable))
                .Add(ScanNode.File(Path.Combine(docs, "link.bin"), 300 * ByteUnits.MB, 300 * ByteUnits.MB, old, null, NodeMarks.Link))
                .Add(ScanNode.File(Path.Combine(docs, "twin.bin"), 300 * ByteUnits.MB, 300 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, 900 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(rows.Select(r => r.Node.Name), "twin.bin");
        var shown = LargeOldFinder.Find(tree, LargeOldOptions.Default with { ShowSystemItems = true }, Now, fx.Context);
        Check.Sequence(shown.Select(r => r.Node.Name), "twin.bin");
    }

    // Plan, finder contract: largest first; equal sizes by path, ignoring case (brief M05): z.iso (500 MB) before a.iso
    // (300 MB), whatever the order of the tree; of two 200 MB files, a.iso before B.iso (an ordinal order would put B first).
    [TestMethod]
    public void RowsAreLargestFirstThenByPath()
    {
        using var fx = new ProfileFixture("largeold-order-path");
        var docs = fx.Documents;
        var other = fx.InProfile("Videos");
        var old = MinutesAgo(TimeSpan.FromDays(500));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(docs)
                .Add(Trees.File(Path.Combine(docs, "a.iso"), 300 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(docs, "z.iso"), 500 * ByteUnits.MB, old)))
            .Add(ScanNode.Folder(other)
                .Add(Trees.File(Path.Combine(other, "B.iso"), 200 * ByteUnits.MB, old))
                .Add(Trees.File(Path.Combine(other, "a.iso"), 200 * ByteUnits.MB, old)));
        var tree = Trees.Tree(root, 1200 * ByteUnits.MB);
        var rows = LargeOldFinder.Find(tree, LargeOldOptions.Default, Now, fx.Context);
        Check.Sequence(rows.Select(r => r.Node.Path),
            Path.Combine(docs, "z.iso"), Path.Combine(docs, "a.iso"), Path.Combine(other, "a.iso"), Path.Combine(other, "B.iso"));
    }
}
