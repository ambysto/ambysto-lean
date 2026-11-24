using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Map;

/// <summary>docs/spec/01, "Map": squarified treemap, area proportional to allocated size, up to 3 levels deep, children
/// too small merge into one "smaller items" tile, the volume-level "Not measured" tile, colour by file-type group.</summary>
[TestClass]
public sealed class MapLayoutTests
{
    private const string Root = @"C:\Users\me\Stuff";
    private static readonly MapRect Square = new(0, 0, 100, 100);

    private static double Area(MapTile tile) => tile.Bounds.Width * tile.Bounds.Height;

    private static ScanTree FlatTree(params long[] sizes)
    {
        var root = ScanNode.Folder(Root);
        for (var i = 0; i < sizes.Length; i++)
        {
            root.Add(Trees.File(Root + @"\f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin", sizes[i]));
        }

        return Trees.Tree(root, sizes.Sum());
    }

    private static ScanTree DeepTree()
    {
        var root = ScanNode.Folder(Root)
            .Add(ScanNode.Folder(Root + @"\L1")
                .Add(ScanNode.Folder(Root + @"\L1\L2")
                    .Add(ScanNode.Folder(Root + @"\L1\L2\L3")
                        .Add(ScanNode.Folder(Root + @"\L1\L2\L3\L4")
                            .Add(Trees.File(Root + @"\L1\L2\L3\L4\deep.mp4", 600)))
                        .Add(Trees.File(Root + @"\L1\L2\L3\c.zip", 100)))
                    .Add(Trees.File(Root + @"\L1\L2\b.pdf", 100)))
                .Add(Trees.File(Root + @"\L1\a.iso", 100)))
            .Add(Trees.File(Root + @"\top.exe", 100));
        return Trees.Tree(root, 1000);
    }

    // docs/spec/01 Map: area is proportional to allocated size.
    [TestMethod]
    public void AreaIsProportionalToAllocatedSize()
    {
        var tree = FlatTree(5000, 3000, 2000);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        var items = tiles.Where(t => t.Kind == TileKind.Item).ToList();
        Assert.AreEqual(3, items.Count);
        foreach (var tile in items)
        {
            var expected = 10_000.0 * tile.Node!.AllocatedBytes / 10_000;
            Check.Near(expected, Area(tile), 10_000 * 0.005, "area of " + tile.Node.Name);
        }
    }

    // docs/spec/01 Map: tiles fill the given rectangle and do not overlap.
    [TestMethod]
    public void TilesFillTheBoundsWithoutOverlap()
    {
        var tree = FlatTree(40, 25, 15, 10, 10);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        Check.Near(10_000, tiles.Sum(Area), 10, "total tile area");
        foreach (var tile in tiles)
        {
            Check.True(tile.Bounds.X >= -0.01 && tile.Bounds.Y >= -0.01 && tile.Bounds.X + tile.Bounds.Width <= 100.01 && tile.Bounds.Y + tile.Bounds.Height <= 100.01, "tile inside the bounds: " + tile.Bounds);
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var a = tiles[i].Bounds;
                var b = tiles[j].Bounds;
                var overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Check.True(overlapX <= 0.01 || overlapY <= 0.01, $"tiles {i} and {j} overlap");
            }
        }
    }

    // docs/spec/01 Map: squarified: tiles of equal size in a square are close to squares, not thin strips.
    [TestMethod]
    public void SquarifiedTilesAreNotThinStrips()
    {
        var tree = FlatTree(100, 100, 100, 100);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        foreach (var tile in tiles.Where(t => t.Kind == TileKind.Item))
        {
            var ratio = Math.Max(tile.Bounds.Width, tile.Bounds.Height) / Math.Min(tile.Bounds.Width, tile.Bounds.Height);
            Check.True(ratio <= 1.5, $"aspect ratio {ratio:F2} for an equal-size tile in a square (a sliced layout would give 4.0)");
        }
    }

    // docs/spec/01 Map: shows the current folder up to 3 levels deep.
    [TestMethod]
    public void LayoutGoesAtMostThreeLevelsDeep()
    {
        var tree = DeepTree();
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 3, minimumTileArea: 1);
        var depths = tiles.Select(t => t.Depth).Distinct().ToList();
        Check.AtMost(3, depths.Count, "distinct depths");
        var paths = tiles.Where(t => t.Node is not null).Select(t => t.Node!.Path).ToList();
        Assert.IsTrue(paths.Contains(Root + @"\L1", StringComparer.OrdinalIgnoreCase) || paths.Any(p => p.StartsWith(Root + @"\L1\", StringComparison.OrdinalIgnoreCase)), "level 1 is drawn");
        Assert.IsTrue(paths.Contains(Root + @"\L1\L2\L3", StringComparer.OrdinalIgnoreCase), "level 3 is drawn as a tile");
        Assert.IsFalse(paths.Any(p => p.StartsWith(Root + @"\L1\L2\L3\", StringComparison.OrdinalIgnoreCase)), "nothing below level 3 is drawn");
    }

    // docs/spec/01 Map: a nested tile lies inside its parent's tile.
    [TestMethod]
    public void NestedTilesLieInsideTheirParentsTile()
    {
        var tree = DeepTree();
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 3, minimumTileArea: 1);
        var byNode = tiles.Where(t => t.Node is not null).ToDictionary(t => t.Node!, t => t);
        foreach (var (node, tile) in byNode)
        {
            if (node.Parent is { } parent && byNode.TryGetValue(parent, out var parentTile))
            {
                var p = parentTile.Bounds;
                var c = tile.Bounds;
                Check.True(c.X >= p.X - 0.01 && c.Y >= p.Y - 0.01 && c.X + c.Width <= p.X + p.Width + 0.01 && c.Y + c.Height <= p.Y + p.Height + 0.01, $"{node.Name} {c} inside {parent.Name} {p}");
            }
        }
    }

    // docs/spec/01 Map: a depth of 1 draws only the direct children.
    [TestMethod]
    public void DepthOneDrawsOnlyDirectChildren()
    {
        var tree = DeepTree();
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        var nodes = tiles.Where(t => t.Node is not null).Select(t => t.Node!).ToList();
        Assert.AreEqual(2, nodes.Count);
        Check.Set(nodes.Select(n => n.Name).ToArray(), "L1", "top.exe");
        Assert.AreEqual(1, tiles.Select(t => t.Depth).Distinct().Count());
    }

    // docs/spec/01 Map: children too small to draw merge into one "smaller items" tile.
    [TestMethod]
    public void TinyChildrenMergeIntoOneSmallerItemsTile()
    {
        var sizes = new List<long> { 1_000_000 };
        sizes.AddRange(Enumerable.Repeat(1L, 50));
        var tree = FlatTree([.. sizes]);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 64);
        var merged = tiles.Where(t => t.Kind == TileKind.SmallerItems).ToList();
        Assert.AreEqual(1, merged.Count, "exactly one smaller-items tile");
        Assert.IsNull(merged[0].Node);
        Assert.AreEqual(50L, merged[0].AllocatedBytes, "the merged tile holds the merged sizes");
        Assert.AreEqual(1, tiles.Count(t => t.Kind == TileKind.Item), "the big file is its own tile");
        foreach (var item in tiles.Where(t => t.Kind == TileKind.Item))
        {
            Check.AtLeast(64, (long)Math.Floor(Area(item) + 0.5), "an item tile is never below the minimum area");
        }
    }

    // docs/spec/01 Map: nothing merges when every child is large enough.
    [TestMethod]
    public void NoSmallerItemsTileWhenEveryChildIsLargeEnough()
    {
        var tree = FlatTree(50, 30, 20);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 64);
        Assert.AreEqual(0, tiles.Count(t => t.Kind == TileKind.SmallerItems));
        Assert.AreEqual(3, tiles.Count(t => t.Kind == TileKind.Item));
    }

    // docs/spec/01 Map: at volume level one extra "Not measured" tile sized by used − measured.
    [TestMethod]
    public void VolumeLevelHasOneNotMeasuredTileSizedByUsedMinusMeasured()
    {
        var tree = FlatTree(600, 200);
        var coverage = new Coverage(MeasuredBytes: 800, UsedBytes: 1000, UnreadableCount: 7, CloudNotDownloadedCount: 0);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1, volumeCoverage: coverage);
        var notMeasured = tiles.Where(t => t.Kind == TileKind.NotMeasured).ToList();
        Assert.AreEqual(1, notMeasured.Count);
        Assert.IsNull(notMeasured[0].Node);
        Assert.AreEqual(200L, notMeasured[0].AllocatedBytes);
        Check.Near(10_000 * 0.2, Area(notMeasured[0]), 10_000 * 0.005, "Not measured area is 20 % of the volume");
        var big = tiles.Single(t => t.Node is { Name: "f0.bin" });
        Check.Near(10_000 * 0.6, Area(big), 10_000 * 0.005, "the measured items shrink to make room");
    }

    // docs/spec/01 Map: the "Not measured" tile exists only at volume level (no coverage given: no such tile).
    [TestMethod]
    public void NoNotMeasuredTileInsideAFolder()
    {
        var tree = FlatTree(600, 200);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        Assert.AreEqual(0, tiles.Count(t => t.Kind == TileKind.NotMeasured));
    }

    // docs/spec/01 Map: unreadable items have no known size, so they cannot have an area of their own.
    [TestMethod]
    public void UnreadableEntriesGetNoTileOfTheirOwn()
    {
        var root = ScanNode.Folder(Root)
            .Add(Trees.File(Root + @"\ok.bin", 1000))
            .Add(ScanNode.Folder(Root + @"\locked", 0, null, NodeMarks.Unreadable));
        var tree = Trees.Tree(root, 1000);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 2, minimumTileArea: 1);
        var locked = tiles.Where(t => t.Node is { Name: "locked" }).ToList();
        Check.True(locked.Count == 0 || locked.All(t => Area(t) < 0.01), "an unreadable entry draws no area");
        Check.Near(10_000, Area(tiles.Single(t => t.Node is { Name: "ok.bin" })), 10, "the readable file takes the whole map");
    }

    // docs/spec/01 Map: colour by file-type group; a file tile has its own group.
    [TestMethod]
    public void FileTilesAreColouredByTheirTypeGroup()
    {
        var root = ScanNode.Folder(Root)
            .Add(Trees.File(Root + @"\movie.mp4", 500))
            .Add(Trees.File(Root + @"\disk.vhdx", 300))
            .Add(Trees.File(Root + @"\notes.txt", 200));
        var tree = Trees.Tree(root, 1000);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        Assert.AreEqual(TypeGroupKind.Video, tiles.Single(t => t.Node is { Name: "movie.mp4" }).Colour);
        Assert.AreEqual(TypeGroupKind.VirtualMachineImage, tiles.Single(t => t.Node is { Name: "disk.vhdx" }).Colour);
        Assert.AreEqual(TypeGroup.Of("notes.txt"), tiles.Single(t => t.Node is { Name: "notes.txt" }).Colour);
    }

    // docs/spec/01 Map: a folder drawn as a single tile takes the colour of the file reached by following its largest
    // child; folders drawn with their children inside are a light neutral tint.
    [TestMethod]
    public void FolderTilesTakeTheColourOfTheirLargestDescendantOrTheNeutralTint()
    {
        var tree = DeepTree();
        var shallow = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        Assert.AreEqual(TypeGroupKind.Video, shallow.Single(t => t.Node is { Name: "L1" }).Colour, "L1 drawn alone: the largest chain ends at deep.mp4");
        var deep = MapLayout.Squarify(tree.Root, Square, depth: 3, minimumTileArea: 1);
        Assert.AreEqual(TypeGroupKind.OpenFolder, deep.Single(t => t.Node is { Name: "L1" }).Colour, "L1 drawn with its children inside");
        Assert.AreEqual(TypeGroupKind.Video, deep.Single(t => t.Node is { Name: "L3" }).Colour, "L3 is the last drawn level: coloured by deep.mp4");
    }

    // docs/spec/01 Map: every tile reports the allocated size it stands for, and the bytes shown add up.
    [TestMethod]
    public void TileSizesAddUpToTheFolder()
    {
        var tree = FlatTree(50, 30, 20);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 1, minimumTileArea: 1);
        Assert.AreEqual(100L, tiles.Sum(t => t.AllocatedBytes));
        foreach (var tile in tiles.Where(t => t.Node is not null))
        {
            Assert.AreEqual(tile.Node!.AllocatedBytes, tile.AllocatedBytes);
        }
    }

    // docs/spec/01 Map: an empty folder lays out without tiles and without failing.
    [TestMethod]
    public void EmptyFolderLaysOutToNothing()
    {
        var tree = Trees.Tree(ScanNode.Folder(Root), 0);
        var tiles = MapLayout.Squarify(tree.Root, Square, depth: 3, minimumTileArea: 64);
        Assert.AreEqual(0, tiles.Count(t => t.Kind == TileKind.Item));
    }
}
