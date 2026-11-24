using System.Globalization;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-15 (intent/2026-10-18-asp-15-duplicates-helpers.md, "Rules"): <see cref="DuplicateSelection.TickAllBut"/>
/// first unticks everything in the group, then ticks every copy that is not the kept one and has no hint, through the same
/// checks as Toggle. Invariants: the kept copy is never ticked; at least one copy stays unticked; a hinted copy is never
/// ticked. A kept copy from another group returns false and changes nothing. Written by the test writer before the Core.</summary>
[TestClass]
public sealed class TickAllButTests
{
    private const long Bytes = 2 * ByteUnits.MB;

    /// <summary>A group in <paramref name="folder"/>; the copies whose index is in <paramref name="hinted"/> are display-only.</summary>
    private static DuplicateGroup Group(string folder, int copies, params int[] hinted)
    {
        var list = Enumerable.Range(0, copies)
            .Select(i => new DuplicateCopy(Trees.File(folder + @"\copy" + i.ToString(CultureInfo.InvariantCulture) + ".bin", Bytes, 1000 + i), false)
            {
                Hint = hinted.Contains(i) ? Text.HintProtectedByYou : null,
            })
            .ToList();
        return new DuplicateGroup(Bytes, list, list[^1], (copies - 1) * Bytes);
    }

    private static DuplicateGroup Group(int copies, params int[] hinted) => Group(@"C:\Users\me\Documents\One", copies, hinted);

    /// <summary>The three invariants of the contract, plus "every unhinted other copy is ticked".</summary>
    private static void AssertTickedAllBut(DuplicateSelection selection, DuplicateGroup group, DuplicateCopy keep, string context)
    {
        Check.False(selection.Ticked.Contains(keep), context + ": the kept copy is ticked");
        Check.True(selection.Kept.Contains(keep), context + ": the kept copy is not in Kept");
        Check.AtLeast(1, selection.Kept.Count, context + ": copies left unticked");
        Check.False(selection.Ticked.Any(c => c.Hint is not null), context + ": a hinted copy is ticked");
        var expected = group.Copies.Where(c => !ReferenceEquals(c, keep) && c.Hint is null).ToList();
        CollectionAssert.AreEquivalent(expected, selection.Ticked.ToList(), context + ": the ticks are not exactly the unhinted copies other than the kept one");
    }

    // Rule "keep is never ticked" and "ticks every copy that is not keep and has no hint": in a group of four with no
    // hint, every keep leaves exactly that copy unticked.
    [TestMethod]
    public void TicksEveryOtherCopyAndNeverTheKeptOne()
    {
        var group = Group(4);
        foreach (var keep in group.Copies)
        {
            var selection = new DuplicateSelection(group);
            Assert.IsTrue(selection.TickAllBut(keep), "the kept copy is a copy of the group");
            AssertTickedAllBut(selection, group, keep, "keep " + keep.Node.Name);
            CollectionAssert.AreEqual(new[] { keep }, selection.Kept.ToArray());
        }
    }

    // Rule "a hinted copy is never ticked": the hinted copies stay unticked beside the kept one; the others are ticked.
    [TestMethod]
    public void HintedCopiesAreNeverTicked()
    {
        var group = Group(5, 1, 3);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.TickAllBut(group.Copies[0]));
        AssertTickedAllBut(selection, group, group.Copies[0], "keep copy0, copies 1 and 3 hinted");
        CollectionAssert.AreEquivalent(new[] { group.Copies[2], group.Copies[4] }, selection.Ticked.ToArray());
        CollectionAssert.AreEquivalent(new[] { group.Copies[0], group.Copies[1], group.Copies[3] }, selection.Kept.ToArray());
    }

    // Rules: "The kept copy may be a display-only copy": keeping the hinted copy ticks every movable copy (the hinted copy
    // always stays, so even the last movable copy may be ticked, as Toggle allows).
    [TestMethod]
    public void AHintedKeptCopyTicksEveryMovableCopy()
    {
        var group = Group(3, 2);
        var guarded = group.Copies[2];
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.TickAllBut(guarded));
        AssertTickedAllBut(selection, group, guarded, "keep the hinted copy2");
        CollectionAssert.AreEquivalent(new[] { group.Copies[0], group.Copies[1] }, selection.Ticked.ToArray());
    }

    // Rule "first unticks everything in the group": a tick on the copy now kept is cleared, and ticks on other copies give
    // way to the new choice.
    [TestMethod]
    public void ClearsEarlierTicksFirstIncludingOneOnTheNewKeptCopy()
    {
        var group = Group(4);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[1]), "precondition: copy1 ticked by hand");
        Assert.IsTrue(selection.Toggle(group.Copies[2]), "precondition: copy2 ticked by hand");

        Assert.IsTrue(selection.TickAllBut(group.Copies[1]));
        AssertTickedAllBut(selection, group, group.Copies[1], "keep copy1 after ticking copies 1 and 2 by hand");

        // A second rule with another kept copy: the first rule's ticks are cleared, the new kept copy (ticked by the first
        // rule) is unticked.
        Assert.IsTrue(selection.TickAllBut(group.Copies[3]));
        AssertTickedAllBut(selection, group, group.Copies[3], "keep copy3 after the rule kept copy1");
        Check.True(selection.Ticked.Contains(group.Copies[1]), "copy1, kept by the first rule, is now ticked");
    }

    // Rule "first unticks everything": every movable copy ticked by hand (only the hinted copy unticked); a rule keeping
    // one of the ticked copies unticks it and ticks the other movable copy.
    [TestMethod]
    public void ClearsAHandTickOnTheKeptCopyWhenAHintedCopyStays()
    {
        var group = Group(3, 0);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[1]));
        Assert.IsTrue(selection.Toggle(group.Copies[2]), "precondition: the hinted copy stays, so both movable copies may be ticked");
        Assert.IsTrue(selection.TickAllBut(group.Copies[2]));
        AssertTickedAllBut(selection, group, group.Copies[2], "keep copy2 after ticking 1 and 2");
        CollectionAssert.AreEqual(new[] { group.Copies[1] }, selection.Ticked.ToArray());
    }

    // Rule "returns false, changing nothing, when keep is not a copy of this group": a copy of another group (even one
    // with the same path and size) is refused, and the earlier ticks stay as they were.
    [TestMethod]
    public void AKeptCopyFromAnotherGroupReturnsFalseAndChangesNothing()
    {
        var group = Group(3);
        var twin = Group(3); // same paths, other copy objects: not copies of this group
        var other = Group(@"C:\Users\me\Documents\Two", 3);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[0]), "precondition: copy0 ticked by hand");
        var before = selection.Ticked.ToArray();

        foreach (var foreign in other.Copies.Concat(twin.Copies))
        {
            Assert.IsFalse(selection.TickAllBut(foreign), "a copy of another group: " + foreign.Node.Path);
            CollectionAssert.AreEqual(before, selection.Ticked.ToArray(), "a refused rule changed the ticks: " + foreign.Node.Path);
        }
    }

    // Rules "a hinted copy is never ticked" and "at least one copy stays unticked": a group of two whose other copy is
    // hinted gets no tick at all.
    [TestMethod]
    public void AGroupOfTwoWhoseOtherCopyIsHintedGetsNoTick()
    {
        var group = Group(2, 1);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.TickAllBut(group.Copies[0]), "the kept copy is a copy of the group");
        Assert.AreEqual(0, selection.Ticked.Count, "nothing may be ticked");
        CollectionAssert.AreEqual(group.Copies.ToArray(), selection.Kept.ToArray());
    }

    // The three invariants in every case: groups of 2 to 4 copies, every set of hinted copies, every kept copy, and every
    // set of earlier hand ticks (each through Toggle, refused ones included).
    [TestMethod]
    public void InvariantsHoldInEveryCase()
    {
        var cases = 0;
        for (var size = 2; size <= 4; size++)
        {
            for (var hintMask = 0; hintMask < 1 << size; hintMask++)
            {
                var hinted = Enumerable.Range(0, size).Where(i => (hintMask & (1 << i)) != 0).ToArray();
                for (var keepIndex = 0; keepIndex < size; keepIndex++)
                {
                    for (var tickMask = 0; tickMask < 1 << size; tickMask++)
                    {
                        var group = Group(size, hinted);
                        var selection = new DuplicateSelection(group);
                        for (var i = 0; i < size; i++)
                        {
                            if ((tickMask & (1 << i)) != 0)
                            {
                                selection.Toggle(group.Copies[i]);
                            }
                        }

                        var keep = group.Copies[keepIndex];
                        var context = string.Create(CultureInfo.InvariantCulture, $"size {size}, hinted [{string.Join(",", hinted)}], keep {keepIndex}, hand ticks {tickMask}");
                        Assert.IsTrue(selection.TickAllBut(keep), context);
                        AssertTickedAllBut(selection, group, keep, context);
                        Check.AtLeast(1, group.Copies.Count - selection.Ticked.Count, context + ": every copy is ticked");
                        cases++;
                    }
                }
            }
        }

        Assert.AreEqual(4 * 2 * 4 + 8 * 3 * 8 + 16 * 4 * 16, cases);
    }

    // Settled question 4: TickAllBut(null) returns false and changes nothing, as Toggle does.
    [TestMethod]
    public void ANullKeptCopyReturnsFalseAndChangesNothing()
    {
        var group = Group(3, 2);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.Toggle(group.Copies[1]), "precondition: copy1 ticked by hand");
        Assert.IsFalse(selection.TickAllBut(null));
        CollectionAssert.AreEqual(new[] { group.Copies[1] }, selection.Ticked.ToArray(), "a refused rule changed the ticks");

        var fresh = new DuplicateSelection(group);
        Assert.IsFalse(fresh.TickAllBut(null));
        Assert.AreEqual(0, fresh.Ticked.Count, "nothing is ticked");
    }
}
