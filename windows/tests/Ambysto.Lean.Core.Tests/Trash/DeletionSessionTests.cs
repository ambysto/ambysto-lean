using System;
using System.Linq;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>docs/spec/02 "Selection rules" (the UI passes ids, never paths) and docs/spec/05 <c>sessionId</c> (one
/// confirmation); the <see cref="DeletionSession"/> contract: items of one module, scanned nodes only, each once, and a
/// duplicate always with a kept copy that is another node.</summary>
[TestClass]
public sealed class DeletionSessionTests
{
    private static ScanTree Tree()
    {
        var root = ScanNode.Folder(@"C:\Users\me\Data");
        root.Add(Trees.File(@"C:\Users\me\Data\a.bin", 10));
        root.Add(Trees.File(@"C:\Users\me\Data\b.bin", 10));
        root.Add(Trees.File(@"C:\Users\me\Data\c.bin", 10));
        return Trees.Tree(root, 30);
    }

    [TestMethod]
    public void ASessionKeepsItsModuleItemsInOrderAndHasAnId()
    {
        var tree = Tree();
        var items = new[] { new SessionItem(Trees.Node(tree, "c.bin"), null), new SessionItem(Trees.Node(tree, "a.bin"), null) };
        var session = new DeletionSession(LeanModule.LargeOld, items);
        Assert.AreEqual(LeanModule.LargeOld, session.Module);
        CollectionAssert.AreEqual(items, session.Items.ToArray(), "the items, in the order given");
        Check.False(string.IsNullOrWhiteSpace(session.Id), "a session id");
        Assert.AreNotEqual(session.Id, new DeletionSession(LeanModule.LargeOld, items).Id, "each confirmation has its own id");
    }

    [TestMethod]
    public void ANodeThatIsNotScannedIsRefused()
    {
        var loose = Trees.File(@"C:\Users\me\Data\loose.bin", 10);
        Assert.AreEqual(0L, loose.Id, "a node outside a tree has id 0");
        Assert.Throws<ArgumentException>(() => new DeletionSession(LeanModule.Map, [new SessionItem(loose, null)]));
    }

    [TestMethod]
    public void AnItemTwiceIsRefused()
    {
        var tree = Tree();
        var a = Trees.Node(tree, "a.bin");
        Assert.Throws<ArgumentException>(() => new DeletionSession(LeanModule.Map, [new SessionItem(a, null), new SessionItem(a, null)]));
    }

    [TestMethod]
    public void ADuplicateWithoutAKeptCopyIsRefused()
    {
        var tree = Tree();
        Assert.Throws<ArgumentException>(() => new DeletionSession(LeanModule.Duplicates, [new SessionItem(Trees.Node(tree, "a.bin"), null)]));
    }

    [TestMethod]
    public void ADuplicateThatKeepsItselfIsRefused()
    {
        var tree = Tree();
        var a = Trees.Node(tree, "a.bin");
        Assert.Throws<ArgumentException>(() => new DeletionSession(LeanModule.Duplicates, [new SessionItem(a, a)]));
    }

    [TestMethod]
    public void ADuplicateWithAnotherKeptCopyIsAccepted()
    {
        var tree = Tree();
        var session = new DeletionSession(LeanModule.Duplicates,
            [new SessionItem(Trees.Node(tree, "a.bin"), Trees.Node(tree, "c.bin")), new SessionItem(Trees.Node(tree, "b.bin"), Trees.Node(tree, "c.bin"))]);
        Assert.AreEqual(2, session.Items.Count, "one kept copy may be kept for several duplicates");
    }
}
