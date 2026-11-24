using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03 Duplicates: nothing is pre-selected; the user can never tick every copy of a group (the last
/// unticked copy cannot be ticked); the confirmation lists "Keeping: &lt;path&gt;" per group.</summary>
[TestClass]
public sealed class DuplicateSelectionTests
{
    private const string Docs = @"C:\Users\me\Documents";

    private static DuplicateGroup Group(int copies)
    {
        var root = ScanNode.Folder(Docs);
        var nodes = Enumerable.Range(0, copies).Select(i => Trees.File(Docs + @"\copy" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin", 2 * ByteUnits.MB, 1000 + i)).ToList();
        foreach (var node in nodes)
        {
            root.Add(node);
        }

        Trees.Tree(root, copies * 2 * ByteUnits.MB);
        var list = nodes.Select(n => new DuplicateCopy(n, false)).ToList();
        return new DuplicateGroup(2 * ByteUnits.MB, list, list[^1], (copies - 1) * 2 * ByteUnits.MB);
    }

    // docs/spec/03 Duplicates: nothing is pre-selected.
    [TestMethod]
    public void NothingIsTickedAtFirst()
    {
        var group = Group(3);
        var selection = new DuplicateSelection(group);
        Assert.AreEqual(0, selection.Ticked.Count);
        Assert.AreEqual(3, selection.Kept.Count);
    }

    // docs/spec/03 Duplicates: the suggested keep is a badge only; it is not ticked and not protected from ticking.
    [TestMethod]
    public void SuggestedKeepIsABadgeNotASelection()
    {
        var group = Group(3);
        var selection = new DuplicateSelection(group);
        Assert.IsFalse(selection.Ticked.Contains(group.SuggestedKeep));
        Assert.IsTrue(selection.CanTick(group.SuggestedKeep), "the user may tick the suggested copy and keep another");
        Assert.IsTrue(selection.Toggle(group.SuggestedKeep));
        Assert.IsTrue(selection.Ticked.Contains(group.SuggestedKeep));
    }

    // docs/spec/03 Duplicates: the last unticked copy cannot be ticked.
    [TestMethod]
    public void LastUntickedCopyCannotBeTicked()
    {
        var group = Group(3);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[0]));
        Assert.IsTrue(selection.Toggle(group.Copies[1]));
        Assert.IsFalse(selection.CanTick(group.Copies[2]));
        Assert.IsFalse(selection.Toggle(group.Copies[2]), "the tick is refused");
        Assert.AreEqual(2, selection.Ticked.Count);
        CollectionAssert.AreEqual(new[] { group.Copies[2] }, selection.Kept.ToArray());
    }

    // docs/spec/03 Duplicates: a group of two: ticking one leaves the other untickable; unticking frees it again.
    [TestMethod]
    public void UntickingFreesTheOtherCopy()
    {
        var group = Group(2);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[0]));
        Assert.IsFalse(selection.CanTick(group.Copies[1]));
        Assert.IsTrue(selection.Toggle(group.Copies[0]), "unticking is always allowed");
        Assert.AreEqual(0, selection.Ticked.Count);
        Assert.IsTrue(selection.CanTick(group.Copies[1]));
        Assert.IsTrue(selection.Toggle(group.Copies[1]));
        CollectionAssert.AreEqual(new[] { group.Copies[0] }, selection.Kept.ToArray());
    }

    // docs/spec/03 Duplicates: Ticked and Kept partition the group at every moment.
    [TestMethod]
    public void TickedAndKeptPartitionTheGroup()
    {
        var group = Group(4);
        var selection = new DuplicateSelection(group);
        selection.Toggle(group.Copies[1]);
        selection.Toggle(group.Copies[3]);
        CollectionAssert.AreEquivalent(group.Copies.ToArray(), selection.Ticked.Concat(selection.Kept).ToArray());
        Assert.AreEqual(0, selection.Ticked.Intersect(selection.Kept).Count());
    }

    // docs/spec/03 Duplicates: a refused tick changes nothing.
    [TestMethod]
    public void RefusedTickChangesNothing()
    {
        var group = Group(2);
        var selection = new DuplicateSelection(group);
        selection.Toggle(group.Copies[1]);
        var before = selection.Ticked.ToArray();
        Assert.IsFalse(selection.Toggle(group.Copies[0]));
        CollectionAssert.AreEqual(before, selection.Ticked.ToArray());
        CollectionAssert.AreEqual(new[] { group.Copies[0] }, selection.Kept.ToArray());
    }

    // docs/spec/03 Duplicates: the confirmation lists "Keeping: <path>" per group, from the kept copies.
    [TestMethod]
    public void KeepingLineNamesTheKeptPath()
    {
        var group = Group(2);
        var selection = new DuplicateSelection(group);
        selection.Toggle(group.Copies[0]);
        var kept = selection.Kept.Single();
        Assert.AreEqual("Keeping: " + kept.Node.Path, Text.Keeping(kept.Node.Path));
    }

    /// <summary>A group of <paramref name="copies"/> copies in its own folder <paramref name="folder"/>.</summary>
    private static DuplicateGroup GroupIn(string folder, int copies, long bytes)
    {
        var root = ScanNode.Folder(folder);
        var nodes = Enumerable.Range(0, copies).Select(i => Trees.File(folder + @"\copy" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin", bytes, 2000 + i)).ToList();
        foreach (var node in nodes)
        {
            root.Add(node);
        }

        Trees.Tree(root, copies * bytes);
        var list = nodes.Select(n => new DuplicateCopy(n, false)).ToList();
        return new DuplicateGroup(bytes, list, list[0], (copies - 1) * bytes);
    }

    // Gap brief H17. docs/spec/03 "The user can never tick every copy of a group"; docs/spec/02 "Only items the scanner
    // or a module produced can be selected". A selection of one group: a copy of another group, or nothing, can never be
    // ticked and changes nothing (each tick of a foreign copy would otherwise count toward this group and let its last
    // copy go); afterwards every copy of the group but one can be ticked, and the last cannot.
    [TestMethod]
    public void CopyFromAnotherGroupCannotBeTicked()
    {
        var g1 = GroupIn(@"C:\Users\me\Documents\One", 3, 2 * ByteUnits.MB);
        var g2 = GroupIn(@"C:\Users\me\Documents\Two", 3, 3 * ByteUnits.MB);
        var selection = new DuplicateSelection(g1);
        foreach (var foreign in g2.Copies)
        {
            Assert.IsFalse(selection.CanTick(foreign), "a copy of another group: " + foreign.Node.Path);
            Assert.IsFalse(selection.Toggle(foreign), "toggling a copy of another group is refused: " + foreign.Node.Path);
            Assert.AreEqual(0, selection.Ticked.Count, "a refused tick changes nothing");
        }

        Assert.IsFalse(selection.CanTick(null!), "nothing is not a copy of the group");
        Assert.IsFalse(selection.Toggle(null!), "toggling nothing is refused");
        Assert.AreEqual(0, selection.Ticked.Count);
        Assert.AreEqual(g1.Copies.Count, selection.Kept.Count, "every copy is still kept");

        for (var i = 0; i < g1.Copies.Count - 1; i++)
        {
            Assert.IsTrue(selection.CanTick(g1.Copies[i]), "copy " + i + " of the group can be ticked");
            Assert.IsTrue(selection.Toggle(g1.Copies[i]), "copy " + i + " of the group is ticked");
        }

        var last = g1.Copies[^1];
        Assert.IsFalse(selection.CanTick(last), "the last unticked copy cannot be ticked");
        Assert.IsFalse(selection.Toggle(last));
        Assert.AreEqual(g1.Copies.Count - 1, selection.Ticked.Count);
        CollectionAssert.AreEqual(new[] { last }, selection.Kept.ToArray());
        Check.False(selection.Ticked.Any(t => g2.Copies.Contains(t)), "no copy of the other group was ticked");
    }
}
