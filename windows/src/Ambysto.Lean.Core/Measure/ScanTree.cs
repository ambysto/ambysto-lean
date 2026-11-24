using System;
using System.Collections.Generic;
using System.Threading;

namespace Ambysto.Lean.Core.Measure;

/// <summary>The result of one scan: the tree, its coverage, and whether it was cancelled (docs/spec/01).</summary>
public sealed class ScanTree
{
    // Ids are positions in this list plus one; a removed node leaves a null behind, so an id never names another item.
    private readonly List<ScanNode?> nodes;
    private Coverage coverage = null!;

    /// <summary>Totals every folder of <paramref name="root"/> and gives each node its id.</summary>
    public ScanTree(ScanNode root, Coverage coverage, bool cancelled)
        : this(root, cancelled)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        this.coverage = coverage;
        TotalAll();
    }

    private ScanTree(ScanNode root, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        Cancelled = cancelled;

        // Parents come before their children in this order, so one backward pass totals every folder from its children.
        nodes = new List<ScanNode?>(CountNodes(root)) { root };
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]!;
            node.Id = i + 1;
            if (node.IsFolder)
            {
                nodes.AddRange(node.ChildList);
            }
        }
    }

    private static int CountNodes(ScanNode root)
    {
        if (!root.IsFolder)
        {
            return 1;
        }

        var total = 1;
        var folders = new Stack<ScanNode>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
        {
            var children = folder.ChildList;
            total += children.Count;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.IsFolder)
                {
                    folders.Push(child);
                }
            }
        }

        return total;
    }

    /// <summary>The scanned folder or volume root.</summary>
    public ScanNode Root { get; }

    /// <summary>Measured and not measured (docs/spec/01, Coverage). It describes the scan, and stays as the scan made
    /// it when <see cref="Remove"/> takes items out: the items moved into the Recycle Bin of the same volume, which
    /// stay used until the next scan.</summary>
    public Coverage Coverage => coverage;

    /// <summary>What the scan read (docs/spec/01 Windows note): files, folders, unreadable items, time, cancelled.</summary>
    public ScanSummary Summary => new(files, folders, coverage.UnreadableCount, Elapsed, Cancelled);

    private long files;
    private long folders;

    /// <summary>The walk's own time, set by the scanner; zero for a tree built by tests.</summary>
    internal TimeSpan Elapsed { get; set; }

    /// <summary>True after a cancel: the tree holds what was measured so far ("Scan cancelled — partial").</summary>
    public bool Cancelled { get; private set; }

    /// <summary>How many files the hard-link step opened (names of one file that disagreed); for diagnostics.</summary>
    internal int HardLinkFilesOpened { get; private set; }

    /// <summary>How many files were asked for their own allocated size because the listing said 0 (compressed files); for diagnostics.</summary>
    internal long AllocationFilesOpened { get; private set; }

    /// <summary>The node with this id, or null.</summary>
    public ScanNode? Find(long id) => id >= 1 && id <= nodes.Count ? nodes[(int)(id - 1)] : null;

    /// <summary>The tree of a finished walk: hard links resolved (walk rule 4; given up when <paramref name="token"/> is
    /// cancelled, and then the tree is marked cancelled, as it may count a hard-linked file at every path),
    /// folders totalled, coverage from the root's total.</summary>
    internal static ScanTree Build(ScanNode root, bool cancelled, long usedBytes, long unreadable, long cloud, long fileCount, long allocationsAsked, CancellationToken token)
    {
        var tree = new ScanTree(root, cancelled);
        var links = HardLinks.Resolve(tree.nodes, fileCount, token);
        tree.HardLinkFilesOpened = links.FilesOpened;
        tree.AllocationFilesOpened = allocationsAsked;
        if (!links.Completed)
        {
            // A cancel that cut the hard-link step short leaves some names counted twice: the tree is partial.
            tree.Cancelled = true;
        }

        tree.TotalAll();
        tree.coverage = new Coverage(root.AllocatedBytes, usedBytes, unreadable, cloud);
        tree.files = fileCount;
        foreach (var node in tree.nodes)
        {
            if (node is { IsFolder: true })
            {
                tree.folders++;
            }
        }

        return tree;
    }

    /// <summary>Removes these nodes (items moved to the Recycle Bin) and re-totals their ancestors, so no stale tile
    /// remains (docs/spec/01, Map). An id that names nothing, or the root, is ignored. The coverage is not changed.
    /// Not safe while another thread reads <see cref="ScanNode.Children"/> of the same tree (which sorts on first use).</summary>
    public void Remove(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            var node = Find(id);
            if (node?.Parent is not { } parent)
            {
                continue;
            }

            var logical = node.LogicalBytes;
            var allocated = node.AllocatedBytes;
            var files = node.FileCount;
            for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                ancestor.AllocatedBytes -= allocated;
                ancestor.LogicalBytes -= logical;
                ancestor.FileCount -= files;
                ancestor.MarkUnsorted();
            }

            parent.Detach(node);
            var gone = new List<ScanNode> { node };
            for (var i = 0; i < gone.Count; i++)
            {
                var member = gone[i];
                nodes[(int)(member.Id - 1)] = null;
                gone.AddRange(member.Children);
            }
        }
    }

    private void TotalAll()
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var folder = nodes[i]!;
            if (!folder.IsFolder)
            {
                continue;
            }

            long logical = 0;
            long allocated = 0;
            long files = 0;
            foreach (var child in folder.ChildList)
            {
                logical += child.LogicalBytes;
                allocated += child.AllocatedBytes;
                files += child.FileCount;
            }

            folder.LogicalBytes = logical;
            folder.AllocatedBytes = allocated;
            folder.FileCount = files;
            folder.MarkUnsorted();
        }
    }
}
