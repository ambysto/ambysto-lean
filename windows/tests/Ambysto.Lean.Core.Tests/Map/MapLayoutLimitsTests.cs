using System;
using System.Diagnostics;
using System.Linq;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Map;

/// <summary>Plan group 5, "Tiles" (decisions of 2026-10-17 after the core review): a folder whose inside would be only one
/// merged tile is one tile in the colour of its largest child; the map holds at most 2,000 tiles; the guard refuses a NaN
/// side; and the type-group changes (.dll other, .avhdx and .ova virtual machine images).</summary>
[TestClass]
public sealed class MapLayoutLimitsTests
{
    private const string Root = @"C:\Users\me\Stuff";

    // A 9 × 9 folder holding one video: its inside (7.2 × 7.2) is below the minimum area, so it is one Video tile.
    [TestMethod]
    public void AFolderWhoseInsideIsOnlyAMergedTileIsOneTileInTheColourOfItsLargestChild()
    {
        var root = ScanNode.Folder(Root).Add(ScanNode.Folder(Root + @"\clips").Add(Trees.File(Root + @"\clips\a.mp4", 1000)));
        var tree = Trees.Tree(root, 1000);
        var tiles = MapLayout.Squarify(tree.Root, new MapRect(0, 0, 9, 9), depth: 3, minimumTileArea: 64);
        Assert.AreEqual(1, tiles.Count, string.Join(", ", tiles.Select(t => t.Kind + "/" + t.Colour)));
        Assert.AreEqual(TypeGroupKind.Video, tiles[0].Colour);
        Assert.AreEqual("clips", tiles[0].Node!.Name);
    }

    // 30,000 equal files: at most 2,000 tiles, and the bytes shown still add up.
    [TestMethod]
    public void ThirtyThousandEqualFilesStayWithinTheTileCap()
    {
        var root = ScanNode.Folder(Root);
        for (var i = 0; i < 30_000; i++)
        {
            root.Add(Trees.File(Root + @"\f" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".bin", 1000));
        }

        var tree = Trees.Tree(root, 30_000_000);
        var watch = Stopwatch.StartNew();
        var tiles = MapLayout.Squarify(tree.Root, new MapRect(0, 0, 1920, 1080), depth: 3, minimumTileArea: 64);
        Console.WriteLine($"30,000 equal files: {tiles.Count} tiles in {watch.ElapsedMilliseconds} ms");
        Assert.IsTrue(tiles.Count <= MapLayout.MaxTiles, $"{tiles.Count} tiles");
        Assert.IsTrue(tiles.Count > 1, "files should still be drawn up to the cap");
        Assert.AreEqual(30_000_000L, tiles.Sum(t => t.AllocatedBytes));
        Assert.AreEqual(1, tiles.Count(t => t.Kind == TileKind.SmallerItems));
    }

    // The million-file tree (100 × 100 folders × 100 files) on 1920 × 1080: at most 2,000 tiles, in well under a second.
    [TestMethod]
    public void AMillionFilesStayWithinTheTileCapQuickly()
    {
        var root = ScanNode.Entry("big", true, 0, 0, 0, null, NodeMarks.None);
        for (var a = 0; a < 100; a++)
        {
            var outer = ScanNode.Entry("a" + a, true, 0, 0, 0, null, NodeMarks.None);
            for (var b = 0; b < 100; b++)
            {
                var inner = ScanNode.Entry("b" + b, true, 0, 0, 0, null, NodeMarks.None);
                for (var c = 0; c < 100; c++)
                {
                    inner.AddEntry(ScanNode.Entry("f" + c + ".mp4", false, 4096, 4096, 0, null, NodeMarks.None));
                }

                outer.AddEntry(inner);
            }

            root.AddEntry(outer);
        }

        var tree = Trees.Tree(root, 1_000_000L * 4096);
        var bounds = new MapRect(0, 0, 1920, 1080);
        MapLayout.Squarify(tree.Root, bounds);
        var watch = Stopwatch.StartNew();
        var tiles = MapLayout.Squarify(tree.Root, bounds, depth: 3, minimumTileArea: 64);
        Console.WriteLine($"1,000,000 files: {tiles.Count} tiles in {watch.ElapsedMilliseconds} ms");
        Assert.IsTrue(tiles.Count <= MapLayout.MaxTiles, $"{tiles.Count} tiles");
        Assert.IsTrue(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.AreEqual(1_000_000L * 4096, tiles.Where(t => t.Depth == 0).Sum(t => t.AllocatedBytes));
        Assert.IsFalse(tiles.Any(t => t.Kind == TileKind.Item && t.Colour == TypeGroupKind.OpenFolder && t.Depth == 1), "no grey folder holding only a merged tile");
    }

    // A NaN (or infinite, zero, negative) side is refused by the guard: nothing is laid out.
    [TestMethod]
    [DataRow(double.NaN, 100.0)]
    [DataRow(100.0, double.NaN)]
    [DataRow(double.PositiveInfinity, 100.0)]
    [DataRow(0.0, 100.0)]
    [DataRow(100.0, -1.0)]
    [DataRow(1920.0, 0.0)]
    [DataRow(1920.0, -1.0)]
    public void ABadSideLaysOutNothing(double width, double height)
    {
        var root = ScanNode.Folder(Root).Add(Trees.File(Root + @"\a.bin", 1000));
        var tree = Trees.Tree(root, 1000);
        Assert.AreEqual(0, MapLayout.Squarify(tree.Root, new MapRect(0, 0, width, height), depth: 1, minimumTileArea: 1).Count);
    }

    [TestMethod]
    [DataRow(@"C:\x\lib.dll", TypeGroupKind.Other)]
    [DataRow(@"C:\x\disk.avhdx", TypeGroupKind.VirtualMachineImage)]
    [DataRow(@"C:\x\machine.ova", TypeGroupKind.VirtualMachineImage)]
    [DataRow(@"C:\x\setup.exe", TypeGroupKind.Application)]
    public void TypeGroupsOfThePlansDecisions(string path, TypeGroupKind expected) => Assert.AreEqual(expected, TypeGroup.Of(path));

    // Plan group 5, "Tiles" (brief M12): when more than 2,000 tiles would be drawn, the minimum tile area rises until they
    // fit and the rest merges into "smaller items": the largest items stay drawn. A tree of 40 × 40 × 40 files whose
    // top-level folders differ in size (folder i holds files of (i + 1) KB) on 1920 × 1080 with a minimum area of 1 would
    // draw far more than 2,000 tiles; the map holds at most 2,000, more than one item at depth 0, and the items at depth 0
    // are the largest top-level folders.
    [TestMethod]
    public void OverTheCapTheLargestItemsStayDrawn()
    {
        const int Fanout = 40;
        var root = ScanNode.Entry("big", true, 0, 0, 0, null, NodeMarks.None);
        for (var a = 0; a < Fanout; a++)
        {
            var size = (a + 1) * 1024L;
            var outer = ScanNode.Entry("t" + a.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), true, 0, 0, 0, null, NodeMarks.None);
            for (var b = 0; b < Fanout; b++)
            {
                var inner = ScanNode.Entry("s" + b, true, 0, 0, 0, null, NodeMarks.None);
                for (var c = 0; c < Fanout; c++)
                {
                    inner.AddEntry(ScanNode.Entry("f" + c + ".bin", false, size, size, 0, null, NodeMarks.None));
                }

                outer.AddEntry(inner);
            }

            root.AddEntry(outer);
        }

        var total = Enumerable.Range(1, Fanout).Sum(i => i * 1024L) * Fanout * Fanout;
        var tree = Trees.Tree(root, total);
        var bounds = new MapRect(0, 0, 1920, 1080);

        // Precondition, by area alone: the files whose share of the map is at least 4 (four times the minimum) are far
        // more than the cap.
        var areaPerByte = bounds.Width * bounds.Height / total;
        var drawable = Trees.Files(tree.Root).Count(f => f.AllocatedBytes * areaPerByte >= 4);
        Check.AtLeast(MapLayout.MaxTiles * 2L, drawable, "files with an area of at least 4 (the first layout exceeds the cap)");

        var tiles = MapLayout.Squarify(tree.Root, bounds, depth: 3, minimumTileArea: 1);
        Check.AtMost(MapLayout.MaxTiles, tiles.Count, "tiles");
        var topItems = tiles.Where(t => t.Depth == 0 && t.Kind == TileKind.Item).Select(t => t.Node!).ToList();
        Check.AtLeast(2, topItems.Count, "item tiles at depth 0");
        var largest = tree.Root.Children.OrderByDescending(n => n.AllocatedBytes).Take(topItems.Count).Select(n => n.Name).ToArray();
        Check.Set(topItems.Select(n => n.Name), largest);
        Assert.IsTrue(topItems.Any(n => n.Name == "t39"), "the largest top-level folder is drawn");
        Assert.AreEqual(total, tiles.Where(t => t.Depth == 0).Sum(t => t.AllocatedBytes), "the depth-0 tiles still add up to the whole");
    }
}
