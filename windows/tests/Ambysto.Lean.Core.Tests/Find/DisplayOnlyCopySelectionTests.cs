using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03 Windows note (2026-10-18): a display-only copy (for example "Protected by you") is never ticked;
/// the other copies follow the usual rule. Main session, with the fix (the test writer found the gap).</summary>
[TestClass]
public sealed class DisplayOnlyCopySelectionTests
{
    [TestMethod]
    public void ADisplayOnlyCopyCanNeverBeTicked()
    {
        var a = new DuplicateCopy(ScanNode.File(@"C:\Users\me\Docs\a.bin", 10, 4096, 0), false);
        var b = new DuplicateCopy(ScanNode.File(@"C:\Users\me\Docs\b.bin", 10, 4096, 0), false);
        var guarded = new DuplicateCopy(ScanNode.File(@"C:\Users\me\Guarded\c.bin", 10, 4096, 0), false) { Hint = Text.HintProtectedByYou };
        var group = new DuplicateGroup(10, [a, b, guarded], a, 4096);
        var selection = new DuplicateSelection(group);

        Assert.IsFalse(selection.CanTick(guarded), "a display-only copy cannot be ticked");
        Assert.IsFalse(selection.Toggle(guarded), "and toggling it changes nothing");
        Assert.AreEqual(0, selection.Ticked.Count);

        // The movable copies: both may be ticked, since the guarded copy always stays.
        Assert.IsTrue(selection.Toggle(a));
        Assert.IsTrue(selection.CanTick(b), "the guarded copy stays, so the last movable copy may be ticked too");
        Assert.IsTrue(selection.Toggle(b));
        Assert.IsFalse(selection.CanTick(guarded));
        CollectionAssert.AreEquivalent(new[] { a, b }, selection.Ticked.ToArray());
    }
}
