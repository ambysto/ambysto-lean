using System.Linq;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01: the tree totals its folders, names each node by id (selection passes ids, never paths:
/// docs/spec/02 "Selection rules"), and after a deletion session removed items leave the tree with ancestors re-totalled
/// ("so no stale tile remains").</summary>
[TestClass]
public sealed class ScanTreeTests
{
    private const string Root = @"C:\Users\me\Documents";

    private static ScanNode SampleRoot() =>
        ScanNode.Folder(Root)
            .Add(ScanNode.Folder(Root + @"\Photos")
                .Add(Trees.File(Root + @"\Photos\a.jpg", 300))
                .Add(Trees.File(Root + @"\Photos\b.jpg", 200)))
            .Add(ScanNode.Folder(Root + @"\Work")
                .Add(Trees.File(Root + @"\Work\report.pdf", 100)))
            .Add(Trees.File(Root + @"\notes.txt", 50));

    // docs/spec/01 (ScanNode contract): a folder's size is the sum of its children; the tree totals them.
    [TestMethod]
    public void FolderTotalsAreSumsOfChildren()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        Assert.AreEqual(650L, tree.Root.AllocatedBytes);
        Assert.AreEqual(650L, tree.Root.LogicalBytes);
        Assert.AreEqual(500L, Trees.Node(tree, "Photos").AllocatedBytes);
        Assert.AreEqual(4L, tree.Root.FileCount);
        Assert.AreEqual(2L, Trees.Node(tree, "Photos").FileCount);
        Assert.AreEqual(1L, Trees.Node(tree, @"Work\report.pdf").FileCount);
    }

    // docs/spec/01 (ScanNode contract): children are ordered largest allocated size first.
    [TestMethod]
    public void ChildrenAreLargestFirst()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        Check.Sequence(tree.Root.Children.Select(c => c.Name).ToArray(), "Photos", "Work", "notes.txt");
        Check.Sequence(Trees.Node(tree, "Photos").Children.Select(c => c.Name).ToArray(), "a.jpg", "b.jpg");
    }

    // docs/spec/02 Selection rules: the UI passes ids, never paths; ids are unique within the tree and Find resolves them.
    [TestMethod]
    public void IdsAreUniqueAndFindResolvesThem()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var nodes = Trees.All(tree.Root).ToList();
        Assert.AreEqual(nodes.Count, nodes.Select(n => n.Id).Distinct().Count(), "ids are unique");
        foreach (var node in nodes)
        {
            Assert.AreSame(node, tree.Find(node.Id));
        }
    }

    // docs/spec/02 Selection rules: an id that names nothing resolves to nothing (never to another item).
    [TestMethod]
    public void UnknownIdFindsNothing()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var unknown = Trees.All(tree.Root).Max(n => n.Id) + 1000;
        Assert.IsNull(tree.Find(unknown));
    }

    // docs/spec/01 Map: after a deletion session, trashed items are removed and ancestors re-totalled.
    [TestMethod]
    public void RemovingAFileReTotalsEveryAncestor()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var a = Trees.Node(tree, @"Photos\a.jpg");
        tree.Remove([a.Id]);
        Assert.IsNull(tree.Find(a.Id));
        Assert.IsNull(Trees.Find(tree.Root, @"Photos\a.jpg"));
        Assert.AreEqual(200L, Trees.Node(tree, "Photos").AllocatedBytes);
        Assert.AreEqual(1L, Trees.Node(tree, "Photos").FileCount);
        Assert.AreEqual(350L, tree.Root.AllocatedBytes);
        Assert.AreEqual(3L, tree.Root.FileCount);
    }

    // docs/spec/01 Map: removing a folder removes its subtree, so no stale tile remains.
    [TestMethod]
    public void RemovingAFolderRemovesItsSubtree()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var photos = Trees.Node(tree, "Photos");
        var b = Trees.Node(tree, @"Photos\b.jpg");
        tree.Remove([photos.Id]);
        Assert.IsNull(tree.Find(photos.Id));
        Assert.IsNull(tree.Find(b.Id), "a descendant of a removed folder is gone too");
        Assert.AreEqual(150L, tree.Root.AllocatedBytes);
        Assert.AreEqual(2L, tree.Root.FileCount);
        Check.Sequence(tree.Root.Children.Select(c => c.Name).ToArray(), "Work", "notes.txt");
    }

    // docs/spec/01 Map: several items of one session leave together; the order of children stays largest first.
    [TestMethod]
    public void RemovingSeveralItemsKeepsChildrenLargestFirst()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var a = Trees.Node(tree, @"Photos\a.jpg");
        var b = Trees.Node(tree, @"Photos\b.jpg");
        tree.Remove([a.Id, b.Id]);
        Assert.AreEqual(0L, Trees.Node(tree, "Photos").AllocatedBytes);
        Assert.AreEqual(150L, tree.Root.AllocatedBytes);
        Check.Sequence(tree.Root.Children.Select(c => c.Name).ToArray(), "Work", "notes.txt", "Photos");
    }

    // docs/spec/01 Map: removing an id that is not in the tree changes nothing.
    [TestMethod]
    public void RemovingAnUnknownIdChangesNothing()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        tree.Remove([123_456_789]);
        Assert.AreEqual(650L, tree.Root.AllocatedBytes);
        Assert.AreEqual(4L, tree.Root.FileCount);
    }

    // docs/spec/01 Coverage and Progress: the tree carries its coverage and the cancelled flag unchanged.
    [TestMethod]
    public void TreeCarriesCoverageAndCancelledFlag()
    {
        var coverage = new Coverage(650, 1000, 2, 3);
        var tree = new ScanTree(SampleRoot(), coverage, cancelled: true);
        Assert.AreEqual(coverage, tree.Coverage);
        Assert.IsTrue(tree.Cancelled);
    }

    // docs/spec/01 walk rule 4: a link flagged "counted elsewhere" contributes 0 to its folder.
    [TestMethod]
    public void HardLinkCountedElsewhereAddsNothingToItsFolder()
    {
        var root = ScanNode.Folder(Root)
            .Add(ScanNode.File(Root + @"\a.bin", 1000, 1024, 0))
            .Add(ScanNode.File(Root + @"\b.bin", 1000, 0, 0, null, NodeMarks.HardLinkCountedElsewhere));
        var tree = Trees.Tree(root, 1024);
        Assert.AreEqual(1024L, tree.Root.AllocatedBytes);
        Assert.AreEqual(0L, Trees.Node(tree, "b.bin").AllocatedBytes);
    }

    // docs/spec/01 (canonical full paths; brief M20): the children of a volume root have one separator after the drive,
    // whether the tree is built with paths or by the walker's names.
    [TestMethod]
    public void ChildPathsOfAVolumeRootHaveOneSeparator()
    {
        var built = ScanNode.Folder(@"C:\").Add(ScanNode.Folder(@"C:\Users").Add(Trees.File(@"C:\Users\x.bin", 100)));
        var tree = Trees.Tree(built, 100);
        Check.Sequence(Trees.All(tree.Root).Select(n => n.Path), @"C:\", @"C:\Users", @"C:\Users\x.bin");

        var users = ScanNode.Entry("Users", true, 0, 0, 0, null, NodeMarks.None);
        users.AddEntry(ScanNode.Entry("x.bin", false, 100, 100, 0, null, NodeMarks.None));
        var walked = ScanNode.Folder(@"C:\");
        walked.AddEntry(users);
        var walkedTree = Trees.Tree(walked, 100);
        Check.Sequence(Trees.All(walkedTree.Root).Select(n => n.Path), @"C:\", @"C:\Users", @"C:\Users\x.bin");
        Check.Sequence(Trees.All(walkedTree.Root).Skip(1).Select(n => n.Name), "Users", "x.bin");
    }

    // ScanNode contract (brief M21): children are largest first whatever the order they were added in; equal sizes by name.
    [TestMethod]
    public void ChildrenAreLargestFirstWhateverTheOrderAdded()
    {
        var root = ScanNode.Folder(Root)
            .Add(Trees.File(Root + @"\e.bin", 100))
            .Add(Trees.File(Root + @"\d.bin", 200))
            .Add(Trees.File(Root + @"\b.bin", 250))
            .Add(Trees.File(Root + @"\a.bin", 250))
            .Add(ScanNode.Folder(Root + @"\sub")
                .Add(Trees.File(Root + @"\sub\small.bin", 10))
                .Add(Trees.File(Root + @"\sub\large.bin", 290)))
            .Add(Trees.File(Root + @"\c.bin", 280));
        var tree = Trees.Tree(root, 1380);
        Check.Sequence(tree.Root.Children.Select(c => c.Name), "sub", "c.bin", "a.bin", "b.bin", "d.bin", "e.bin");
        Check.Sequence(Trees.Node(tree, "sub").Children.Select(c => c.Name), "large.bin", "small.bin");
    }

    // ScanNode contract (brief M22): a node taken out of the tree keeps its name (its last component) and its full path,
    // and so do the nodes inside a removed folder; for a tree built with paths and for one built by the walker's names.
    [TestMethod]
    public void ARemovedNodeKeepsItsNameAndPath()
    {
        var tree = Trees.Tree(SampleRoot(), 650);
        var a = Trees.Node(tree, @"Photos\a.jpg");
        var work = Trees.Node(tree, "Work");
        var report = Trees.Node(tree, @"Work\report.pdf");
        tree.Remove([a.Id, work.Id]);
        Assert.AreEqual("a.jpg", a.Name);
        Assert.AreEqual(Root + @"\Photos\a.jpg", a.Path);
        Assert.AreEqual("Work", work.Name);
        Assert.AreEqual(Root + @"\Work", work.Path);
        Assert.AreEqual("report.pdf", report.Name);
        Assert.AreEqual(Root + @"\Work\report.pdf", report.Path);

        var photos = ScanNode.Entry("Photos", true, 0, 0, 0, null, NodeMarks.None);
        var b = ScanNode.Entry("b.jpg", false, 200, 200, 0, null, NodeMarks.None);
        photos.AddEntry(b);
        var walked = ScanNode.Folder(@"C:\");
        walked.AddEntry(photos);
        var walkedTree = Trees.Tree(walked, 200);
        walkedTree.Remove([b.Id]);
        Assert.AreEqual("b.jpg", b.Name);
        Assert.AreEqual(@"C:\Photos\b.jpg", b.Path);
        Assert.IsNull(b.Parent);
    }
}
