using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.App;

/// <summary>What the view needs to draw and announce one tile (docs/spec/01, Map).</summary>
/// <param name="Kind">An item, the merged "smaller items", or "Not measured".</param>
/// <param name="Colour">The file-type group, or the neutral tint of a folder drawn with its children inside.</param>
/// <param name="Bounds">The tile's rectangle in the map's coordinates.</param>
/// <param name="Node">The item, or null for the merged and "Not measured" tiles.</param>
/// <param name="Label">The visible text of the tile.</param>
/// <param name="AutomationName">"&lt;name&gt;, &lt;size&gt;": what a screen reader says.</param>
/// <param name="HelpText">The full path, then the display-only hint on a new line; empty for tiles without a path.</param>
/// <param name="ToolTip">What hover and keyboard focus show: name and size, path, hint, percentage of the parent.</param>
internal sealed record TileModel(TileKind Kind, TypeGroupKind Colour, MapRect Bounds, ScanNode? Node, string Label, string AutomationName, string HelpText, string ToolTip);

/// <summary>Turns a layout of the Core into tile models: names, help texts with the display-only hints, tooltips. Reads
/// owners for the hints, so it runs off the UI thread.</summary>
internal static class MapBuilder
{
    /// <summary>The tiles of <paramref name="folder"/> in <paramref name="bounds"/>. <paramref name="volumeCoverage"/> is given
    /// when the map shows a whole volume (it adds the "Not measured" tile).</summary>
    internal static List<TileModel> Build(ScanNode folder, MapRect bounds, Coverage? volumeCoverage, PathContext context, ConcurrentDictionary<string, string?> hints)
    {
        var layout = MapLayout.Squarify(folder, bounds, volumeCoverage: volumeCoverage);
        var whole = folder.AllocatedBytes + (volumeCoverage?.NotMeasuredBytes ?? 0);
        var models = new List<TileModel>(layout.Count);
        foreach (var tile in layout)
        {
            var size = ByteUnits.Format(tile.AllocatedBytes);
            switch (tile.Kind)
            {
                case TileKind.Item:
                    models.Add(ItemTile(tile, size, whole, context, hints));
                    break;
                case TileKind.SmallerItems:
                    models.Add(new TileModel(tile.Kind, tile.Colour, tile.Bounds, null, Text.SmallerItems, Text.NameAndSize(Text.SmallerItems, size), string.Empty,
                        Text.NameAndSize(Text.SmallerItems, size) + "\n" + Text.PercentOfParent(Percent(tile.AllocatedBytes, ParentBytes(tile, layout, whole)))));
                    break;
                default:
                    models.Add(NotMeasuredTile(tile, size, whole, volumeCoverage));
                    break;
            }
        }

        return models;
    }

    /// <summary>The tiles of the map while a scan runs (ASP-14): one per top-level zone of a progress report, sized by the bytes measured so
    /// far. A zone that is done is an item tile in its type-group colour; a zone still being read is drawn like the "Not measured" tile (the
    /// panel colour, hatched). A zone not reached yet has no tile. None of them holds a node: the map is display-only until the scan ends.</summary>
    internal static List<TileModel> BuildZones(IReadOnlyList<(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)> zones, MapRect bounds)
    {
        var drawn = new List<(string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)>();
        foreach (var zone in zones)
        {
            if (zone.AllocatedBytes > 0)
            {
                drawn.Add(zone);
            }
        }

        // Largest first, as the layout expects; equal sizes by path, so the order is the same on every report.
        drawn.Sort(static (a, b) => b.AllocatedBytes != a.AllocatedBytes ? b.AllocatedBytes.CompareTo(a.AllocatedBytes) : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        var holder = ScanNode.Folder(@"\zones");
        var byPath = new Dictionary<string, (string Path, long AllocatedBytes, bool Done, TypeGroupKind? Colour, string? Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in drawn)
        {
            holder.Add(ScanNode.File(zone.Path, zone.AllocatedBytes, zone.AllocatedBytes, 0));
            byPath[zone.Path] = zone;
        }

        var whole = 0L;
        foreach (var zone in drawn)
        {
            whole += zone.AllocatedBytes;
        }

        var layout = MapLayout.Squarify(holder, bounds, depth: 1, minimumTileArea: 16);
        var models = new List<TileModel>(layout.Count);
        foreach (var tile in layout)
        {
            var size = ByteUnits.Format(tile.AllocatedBytes);
            if (tile.Kind == TileKind.SmallerItems)
            {
                var merged = Text.NameAndSize(Text.SmallerItems, size);
                models.Add(new TileModel(TileKind.SmallerItems, TypeGroupKind.Other, tile.Bounds, null, Text.SmallerItems, merged, string.Empty,
                    merged + "\n" + Text.PercentOfParent(Percent(tile.AllocatedBytes, whole))));
            }
            else if (tile.Node is { } node && byPath.TryGetValue(node.Path, out var zone))
            {
                var name = zone.Name ?? node.Name;
                var named = Text.NameAndSize(name, size);
                var kind = zone.Done ? TileKind.Item : TileKind.NotMeasured;
                models.Add(new TileModel(kind, zone.Done ? zone.Colour ?? TypeGroupKind.Other : TypeGroupKind.Other, tile.Bounds, null, named, named, zone.Path,
                    named + "\n" + zone.Path + "\n" + Text.PercentOfParent(Percent(tile.AllocatedBytes, whole))));
            }
        }

        return models;
    }

    private static TileModel ItemTile(MapTile tile, string size, long whole, PathContext context, ConcurrentDictionary<string, string?> hints)
    {
        var node = tile.Node!;
        var path = node.Path;
        var hint = hints.GetOrAdd(path, p => DisplayOnly.HintFor(p, context));
        var help = hint is null ? path : path + "\n" + hint;
        var parentBytes = tile.Depth == 0 || node.Parent is null ? whole : node.Parent.AllocatedBytes;
        var name = Text.NameAndSize(node.Name, size);
        var tip = name + "\n" + help + "\n" + Text.PercentOfParent(Percent(tile.AllocatedBytes, parentBytes));
        return new TileModel(TileKind.Item, tile.Colour, tile.Bounds, node, node.Name, name, help, tip);
    }

    private static TileModel NotMeasuredTile(MapTile tile, string size, long whole, Coverage? coverage)
    {
        // What the remainder holds, with the counts of what could not be read or was not downloaded (docs/spec/01, Coverage).
        var lines = new List<string> { Text.NotMeasuredRemainder };
        if (coverage is { UnreadableCount: > 0 })
        {
            lines.Add(Text.UnreadableItems(coverage.UnreadableCount));
        }

        if (coverage is { CloudNotDownloadedCount: > 0 })
        {
            lines.Add(Text.CloudFilesNotDownloaded(coverage.CloudNotDownloadedCount));
        }

        var help = string.Join("\n", lines);
        var name = Text.NameAndSize(Text.NotMeasured, size);
        return new TileModel(TileKind.NotMeasured, tile.Colour, tile.Bounds, null, Text.NotMeasured, name, help,
            name + "\n" + help + "\n" + Text.PercentOfParent(Percent(tile.AllocatedBytes, whole)));
    }

    // The total a tile is a share of: for a tile at the top of the map, everything the map shows (at volume level the used
    // total, measured plus not measured); for a merged tile inside a nested folder, that folder, the smallest drawn folder
    // tile one level up that holds it.
    private static long ParentBytes(MapTile tile, IReadOnlyList<MapTile> layout, long whole)
    {
        if (tile.Depth == 0)
        {
            return whole;
        }

        var x = tile.Bounds.X + tile.Bounds.Width / 2;
        var y = tile.Bounds.Y + tile.Bounds.Height / 2;
        MapTile? holder = null;
        foreach (var candidate in layout)
        {
            if (candidate is { Kind: TileKind.Item, Node.IsFolder: true } && candidate.Depth == tile.Depth - 1
                && x >= candidate.Bounds.X && x <= candidate.Bounds.X + candidate.Bounds.Width && y >= candidate.Bounds.Y && y <= candidate.Bounds.Y + candidate.Bounds.Height
                && (holder is null || candidate.Bounds.Width * candidate.Bounds.Height < holder.Bounds.Width * holder.Bounds.Height))
            {
                holder = candidate;
            }
        }

        return holder?.Node?.AllocatedBytes ?? whole;
    }

    private static double Percent(long part, long whole) => whole <= 0 ? 0 : 100.0 * part / whole;
}
