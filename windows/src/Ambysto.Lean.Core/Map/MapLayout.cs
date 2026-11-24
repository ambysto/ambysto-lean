using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Map;

/// <summary>A rectangle in the map's coordinates.</summary>
public readonly record struct MapRect(double X, double Y, double Width, double Height);

/// <summary>What a tile stands for.</summary>
public enum TileKind
{
    /// <summary>A file or folder.</summary>
    Item,

    /// <summary>Children too small to draw, merged into one tile.</summary>
    SmallerItems,

    /// <summary>The volume-level "Not measured" tile (hatched), sized by used − measured.</summary>
    NotMeasured,
}

/// <summary>One tile of the map. <see cref="Node"/> is null for "smaller items" and "Not measured".</summary>
public sealed record MapTile(TileKind Kind, ScanNode? Node, MapRect Bounds, int Depth, TypeGroupKind Colour, long AllocatedBytes);

/// <summary>The squarified treemap of docs/spec/01 (Map): area proportional to allocated size, up to 3 levels deep.</summary>
public static class MapLayout
{
    /// <summary>The most tiles one map holds (plan group 5, "Tiles": when more would be drawn, the minimum tile area rises
    /// until they fit, and the rest merges into "smaller items").</summary>
    internal const int MaxTiles = 2000;

    // A folder drawn with its children inside keeps this much of its tile as a border, so its tint shows (at most a tenth
    // of the smaller side).
    private const double Border = 2;

    // Room kept at every level for the two tiles that are not items: "smaller items" and "Not measured".
    private const int MaxItemsPerLevel = MaxTiles - 2;

    // Raising the minimum area stops after this many rounds; the last round merges everything.
    private const int MaxRounds = 40;

    /// <summary>Lays out <paramref name="node"/> in <paramref name="bounds"/>, <paramref name="depth"/> levels deep.
    /// Children whose tile would be smaller than <paramref name="minimumTileArea"/> merge into one "smaller items"
    /// tile. When <paramref name="volumeCoverage"/> is given (the map shows the volume), one extra "Not measured" tile
    /// is sized by its <see cref="Coverage.NotMeasuredBytes"/>. A folder is drawn with its children inside only when at
    /// least one child can be drawn; a folder whose inside would be a single "smaller items" tile is one tile in the
    /// colour of its largest child. The map holds at most <see cref="MaxTiles"/> tiles: when more would be drawn,
    /// the minimum tile area rises until they fit. A bounds with a zero, negative, NaN or infinite side lays out nothing.</summary>
    public static IReadOnlyList<MapTile> Squarify(ScanNode node, MapRect bounds, int depth = 3, double minimumTileArea = 64, Coverage? volumeCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var notMeasured = volumeCoverage?.NotMeasuredBytes ?? 0;
        var minimum = minimumTileArea;
        for (var round = 0; ; round++)
        {
            var tiles = new List<MapTile>();
            Layout(node, bounds, 0, depth, minimum, notMeasured, tiles);
            if (tiles.Count <= MaxTiles)
            {
                return tiles;
            }

            // Too many: the smallest drawn items go. The new minimum is above the area of the (count − cap)th smallest, so
            // at least that many leave the map in this round; the merged tiles they join count again, hence the rounds.
            minimum = round + 1 >= MaxRounds ? double.MaxValue : NextMinimum(tiles, minimum);
        }
    }

    private static double NextMinimum(List<MapTile> tiles, double minimum)
    {
        var areas = new List<double>(tiles.Count);
        foreach (var tile in tiles)
        {
            if (tile.Kind == TileKind.Item)
            {
                areas.Add(tile.Bounds.Width * tile.Bounds.Height);
            }
        }

        var excess = tiles.Count - MaxTiles;
        if (excess > areas.Count)
        {
            return minimum * 2;
        }

        areas.Sort();
        return Math.Max(minimum * 1.01, Math.BitIncrement(areas[excess - 1]));
    }

    // One thing to place: an item, the merged small items, or the Not measured remainder.
    private readonly record struct Entry(TileKind Kind, ScanNode? Node, long Bytes, int Order);

    private static void Layout(ScanNode node, MapRect bounds, int level, int depth, double minimumTileArea, long notMeasuredBytes, List<MapTile> tiles)
    {
        // Written so that a NaN side fails it too (every comparison with NaN is false).
        if (level >= depth || !(bounds.Width > 0 && bounds.Height > 0) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return;
        }

        // Sizes of what is drawn. Entries without a known size (unreadable ones count 0) have no area of their own.
        var total = notMeasuredBytes;
        foreach (var child in node.Children)
        {
            if (child.AllocatedBytes > 0)
            {
                total += child.AllocatedBytes;
            }
        }

        if (total <= 0)
        {
            return;
        }

        var areaPerByte = bounds.Width * bounds.Height / total;
        var entries = new List<Entry>();
        var smallBytes = 0L;
        var order = 0;
        var kept = 0;
        foreach (var child in node.Children)
        {
            if (child.AllocatedBytes <= 0)
            {
                continue;
            }

            // Children come largest first, so beyond the per-level limit only the smallest merge.
            if (child.AllocatedBytes * areaPerByte < minimumTileArea || kept >= MaxItemsPerLevel)
            {
                smallBytes += child.AllocatedBytes;
            }
            else
            {
                entries.Add(new Entry(TileKind.Item, child, child.AllocatedBytes, order++));
                kept++;
            }
        }

        // The merged tile and the Not measured tile are kept even when their area is below the minimum: each carries a
        // fact the user must see (plan group 5, "Tiles": decision of 2026-10-17).
        if (smallBytes > 0)
        {
            entries.Add(new Entry(TileKind.SmallerItems, null, smallBytes, order++));
        }

        if (notMeasuredBytes > 0)
        {
            entries.Add(new Entry(TileKind.NotMeasured, null, notMeasuredBytes, order));
        }

        entries.Sort(static (a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : a.Order.CompareTo(b.Order));
        foreach (var (entry, rect) in PlaceRows(entries, bounds, areaPerByte))
        {
            Place(entry, rect, level, depth, minimumTileArea, tiles);
        }
    }

    private static void Place(Entry entry, MapRect rect, int level, int depth, double minimumTileArea, List<MapTile> tiles)
    {
        if (entry.Node is not { } item)
        {
            tiles.Add(new MapTile(entry.Kind, null, rect, level, TypeGroupKind.Other, entry.Bytes));
            return;
        }

        if (!item.IsFolder || level + 1 >= depth)
        {
            tiles.Add(new MapTile(TileKind.Item, item, rect, level, TypeGroup.ColourOf(item), entry.Bytes));
            return;
        }

        // A folder with levels left is drawn with its children inside, unless nothing inside can be drawn: then (nothing, or
        // only one merged tile) it is one tile, in the colour of its largest child (plan group 5, "Tiles").
        var border = Math.Min(Border, Math.Min(rect.Width, rect.Height) / 10);
        var inner = new MapRect(rect.X + border, rect.Y + border, rect.Width - 2 * border, rect.Height - 2 * border);
        var inside = new List<MapTile>();
        Layout(item, inner, level + 1, depth, minimumTileArea, 0, inside);
        var drawsChildren = inside.Count > 0 && !(inside.Count == 1 && inside[0].Kind == TileKind.SmallerItems);
        tiles.Add(new MapTile(TileKind.Item, item, rect, level, drawsChildren ? TypeGroupKind.OpenFolder : TypeGroup.ColourOf(item), entry.Bytes));
        if (drawsChildren)
        {
            tiles.AddRange(inside);
        }
    }

    // Squarified treemap (Bruls, Huizing, van Wijk): rows along the shorter side of the free rectangle, a row growing
    // while its worst aspect ratio improves. entries are largest first; each gets an area of Bytes × areaPerByte.
    private static List<(Entry Entry, MapRect Rect)> PlaceRows(List<Entry> entries, MapRect bounds, double areaPerByte)
    {
        var placed = new List<(Entry, MapRect)>(entries.Count);
        var free = bounds;
        var start = 0;
        while (start < entries.Count)
        {
            var shorter = Math.Min(free.Width, free.Height);
            var end = start + 1;
            var rowArea = entries[start].Bytes * areaPerByte;
            var worst = Worst(entries, start, end, rowArea, shorter, areaPerByte);
            while (end < entries.Count)
            {
                var nextArea = rowArea + entries[end].Bytes * areaPerByte;
                var nextWorst = Worst(entries, start, end + 1, nextArea, shorter, areaPerByte);
                if (nextWorst > worst)
                {
                    break;
                }

                rowArea = nextArea;
                worst = nextWorst;
                end++;
            }

            // The last row takes what is left, so rounding never leaves a sliver.
            var last = end == entries.Count;
            var wide = free.Width >= free.Height;
            var thickness = last ? (wide ? free.Width : free.Height) : rowArea / shorter;

            var position = wide ? free.Y : free.X;
            for (var i = start; i < end; i++)
            {
                var share = entries[i].Bytes * areaPerByte / rowArea;
                var length = shorter * share;
                placed.Add((entries[i], wide ? new MapRect(free.X, position, thickness, length) : new MapRect(position, free.Y, length, thickness)));
                position += length;
            }

            free = wide
                ? new MapRect(free.X + thickness, free.Y, free.Width - thickness, free.Height)
                : new MapRect(free.X, free.Y + thickness, free.Width, free.Height - thickness);
            start = end;
        }

        return placed;
    }

    // The worst aspect ratio of the row entries[start..end] laid along a side of length shorter.
    private static double Worst(List<Entry> entries, int start, int end, double rowArea, double shorter, double areaPerByte)
    {
        var thickness = rowArea / shorter;
        var worst = 1.0;
        for (var i = start; i < end; i++)
        {
            var length = shorter * (entries[i].Bytes * areaPerByte / rowArea);
            var ratio = Math.Max(thickness / length, length / thickness);
            worst = Math.Max(worst, ratio);
        }

        return worst;
    }
}
