using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Helpers around the scan tree: minutes since 1970 (walk rule 8), node lookup by relative path, and small
/// in-memory trees built with <see cref="ScanNode.File"/> and <see cref="ScanNode.Folder"/>.</summary>
internal static class Trees
{
    public static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Signed minutes since 1970-01-01 UTC (docs/spec/01, walk rule 8).</summary>
    public static long Minutes(DateTimeOffset time) => (long)Math.Floor((time - Epoch).TotalMinutes);

    public static DateTimeOffset FromMinutes(long minutes) => Epoch.AddMinutes(minutes);

    /// <summary>A file node whose allocated size equals its logical size.</summary>
    public static ScanNode File(string path, long bytes, long lastWriteMinutes = 0, NodeMarks flags = NodeMarks.None) =>
        ScanNode.File(path, bytes, bytes, lastWriteMinutes, null, flags);

    /// <summary>A tree whose coverage says the volume uses exactly what the tree measures (nothing not measured).</summary>
    public static ScanTree Tree(ScanNode root, long measuredBytes) =>
        new(root, new Coverage(measuredBytes, measuredBytes, 0, 0), cancelled: false);

    /// <summary>The node at <paramref name="relativePath"/> below <paramref name="root"/> (components by name, case
    /// as Windows compares names), or null.</summary>
    public static ScanNode? Find(ScanNode root, string relativePath)
    {
        var node = root;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>The node at <paramref name="relativePath"/>, or a failed assertion naming it.</summary>
    public static ScanNode Node(ScanTree tree, string relativePath)
    {
        var node = Find(tree.Root, relativePath);
        if (node is null)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Fail($"no node at {relativePath} below {tree.Root.Path}; children: {string.Join(", ", tree.Root.Children.Select(c => c.Name))}");
        }

        return node!;
    }

    /// <summary>Every node of the tree, depth first.</summary>
    public static IEnumerable<ScanNode> All(ScanNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var node in All(child))
            {
                yield return node;
            }
        }
    }

    /// <summary>Every file node (not a folder) of the tree.</summary>
    public static IEnumerable<ScanNode> Files(ScanNode root) => All(root).Where(n => !n.IsFolder);

    /// <summary>The full path of a node relative to <paramref name="root"/>'s path.</summary>
    public static string Relative(ScanNode root, ScanNode node) => Path.GetRelativePath(root.Path, node.Path);
}
