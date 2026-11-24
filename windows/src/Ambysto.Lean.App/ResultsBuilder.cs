using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.App;

/// <summary>One row of the largest-files list (ASP-14): a file of the scan, its type group and, when the gate would refuse it, the hint
/// that makes the row display-only.</summary>
internal sealed record LargestRow(ScanNode Node, TypeGroupKind Type, string? Hint);

/// <summary>What the results view shows about one scan (ASP-14): for "All files" (slot 0) and each type group of spec 01 (slots 1 to 7, in
/// the order of <see cref="TypeGroupKind"/>), the bytes the scan measured and the 500 largest files.</summary>
internal sealed class ResultsData
{
    /// <summary>The most files one list holds.</summary>
    public const int ListSize = 500;

    /// <summary>The slots: All files, then the seven groups.</summary>
    public const int Slots = 8;

    public long[] Bytes { get; } = new long[Slots];

    public List<LargestRow>[] Rows { get; } = CreateRows();

    /// <summary>The slot of a group: 1 for video up to 7 for other; 0 is All files.</summary>
    public static int SlotOf(TypeGroupKind kind) => (int)kind + 1;

    /// <summary>The group of a slot above 0.</summary>
    public static TypeGroupKind KindOf(int slot) => (TypeGroupKind)(slot - 1);

    private static List<LargestRow>[] CreateRows()
    {
        var rows = new List<LargestRow>[Slots];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = [];
        }

        return rows;
    }
}

/// <summary>Works out <see cref="ResultsData"/> from the scanned tree in one pass over its files. Reads only; it runs off the UI thread,
/// and the caller keeps the tree unchanged while it does.</summary>
internal static class ResultsBuilder
{
    internal static ResultsData Build(ScanTree tree, PathContext context)
    {
        // A min-heap per slot, so only the 500 largest files of each are kept while the pass goes on.
        var heaps = new PriorityQueue<ScanNode, long>[ResultsData.Slots];
        for (var i = 0; i < heaps.Length; i++)
        {
            heaps[i] = new PriorityQueue<ScanNode, long>();
        }

        var data = new ResultsData();
        foreach (var node in FilesInExistingOrder(tree.Root))
        {
            var bytes = node.AllocatedBytes;
            var slot = ResultsData.SlotOf(TypeGroup.Of(node.Name));
            data.Bytes[0] += bytes;
            data.Bytes[slot] += bytes;

            // A link or an unreadable entry is counted but never listed: it has no size or date to trust.
            if (bytes <= 0 || (node.Flags & (NodeMarks.Link | NodeMarks.Unreadable)) != 0)
            {
                continue;
            }

            Offer(heaps[0], node, bytes);
            Offer(heaps[slot], node, bytes);
        }

        var hints = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var slot = 0; slot < heaps.Length; slot++)
        {
            var nodes = new List<(ScanNode Node, string Path)>(heaps[slot].Count);
            while (heaps[slot].TryDequeue(out var node, out _))
            {
                nodes.Add((node, node.Path));
            }

            // Largest first; equal sizes by path, so the order is the same on every run.
            nodes.Sort(static (a, b) =>
            {
                var bySize = b.Node.AllocatedBytes.CompareTo(a.Node.AllocatedBytes);
                return bySize != 0 ? bySize : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var (node, path) in nodes)
            {
                if (!hints.TryGetValue(path, out var hint))
                {
                    hints[path] = hint = DisplayOnly.HintFor(path, context);
                }

                data.Rows[slot].Add(new LargestRow(node, TypeGroup.Of(node.Name), hint));
            }
        }

        return data;
    }

    // Preserve the existing stack traversal's exact reverse-Children order, including equal-size heap tie membership.
    // Keep one cursor per ancestor instead of a reference for every sibling (a million-entry flat folder needs ~8 MiB).
    private static IEnumerable<ScanNode> FilesInExistingOrder(ScanNode root)
    {
        if (!root.IsFolder)
        {
            yield return root;
            yield break;
        }

        var pending = new Stack<(IReadOnlyList<ScanNode> Children, int Next)>();
        var children = root.Children;
        pending.Push((children, children.Count - 1));
        while (pending.TryPop(out var frame))
        {
            if (frame.Next < 0)
            {
                continue;
            }

            var node = frame.Children[frame.Next];
            if (frame.Next > 0)
            {
                pending.Push((frame.Children, frame.Next - 1));
            }

            if (node.IsFolder)
            {
                children = node.Children;
                pending.Push((children, children.Count - 1));
            }
            else
            {
                yield return node;
            }
        }
    }

    private static void Offer(PriorityQueue<ScanNode, long> heap, ScanNode node, long bytes)
    {
        if (heap.Count < ResultsData.ListSize)
        {
            heap.Enqueue(node, bytes);
        }
        else if (heap.TryPeek(out _, out var smallest) && bytes > smallest)
        {
            heap.DequeueEnqueue(node, bytes);
        }
    }
}
