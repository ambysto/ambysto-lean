using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Hand mutation check (a) (plan "Hand mutation checks"; interim review 7): the shell move is aborted whenever a
/// <c>PreDeleteItem</c> (the item's or a descendant's) does not carry <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c> (docs/spec/02
/// Windows note on steps 6–7; plan "Groups 10 and 11: implementation notes", Shell recycler). The real gate refuses every
/// tree that would produce such a callback, so only a unit test with injected flags can see the answer. The progress sink
/// is driven directly, without COM or a real file operation.</summary>
[TestClass]
public sealed class PreDeleteAnswerTests
{
    private const uint RecycleIfPossible = 0x0080; // TSF_DELETE_RECYCLE_IF_POSSIBLE
    private const int SOk = 0;
    private const int EAbort = unchecked((int)0x80004004);

    // With the flag (alone or among others): S_OK, the shell may go on.
    [TestMethod]
    [DataRow(0x0080u)]
    [DataRow(0x0081u)]
    [DataRow(0x0480u)]
    [DataRow(0xFFFFFFFFu)]
    public void WithRecycleIfPossibleTheAnswerIsSOk(uint flags) =>
        Assert.AreEqual(SOk, ShellRecycler.PreDeleteAnswer(flags), $"flags 0x{flags:X8}");

    // Without the flag (no bits, other bits, every bit but 0x80): E_ABORT.
    [TestMethod]
    [DataRow(0x00000000u)]
    [DataRow(0x00000001u)]
    [DataRow(0x00000040u)]
    [DataRow(0x00000100u)]
    [DataRow(0x00000400u)]
    [DataRow(0x00000800u)]
    [DataRow(0x7FFFFF7Fu)]
    [DataRow(0xFFFFFF7Fu)]
    public void WithoutRecycleIfPossibleTheAnswerIsEAbort(uint flags)
    {
        Assert.AreEqual(0u, flags & RecycleIfPossible, "precondition: the row has no recycle flag");
        Assert.AreEqual(EAbort, ShellRecycler.PreDeleteAnswer(flags), $"flags 0x{flags:X8}");
    }

    // The sink answers each callback the same way and remembers an abort: a callback without the flag sets
    // AbortedNonRecycle; callbacks with it leave it unset.
    [TestMethod]
    public void TheSinkMarksAnAbortOnlyForACallbackWithoutTheFlag()
    {
        var clean = new ShellRecycler.Sink(@"C:\x\item");
        Assert.AreEqual(SOk, clean.PreDeleteItem(RecycleIfPossible, null), "the item");
        Assert.AreEqual(SOk, clean.PreDeleteItem(RecycleIfPossible | 0x1, null), "a descendant");
        Check.False(clean.AbortedNonRecycle, "every callback said recycle: no abort");

        var aborted = new ShellRecycler.Sink(@"C:\x\item");
        Assert.AreEqual(SOk, aborted.PreDeleteItem(RecycleIfPossible, null), "the item");
        Assert.AreEqual(EAbort, aborted.PreDeleteItem(0, null), "a descendant the shell would delete for good");
        Check.True(aborted.AbortedNonRecycle, "a callback without the flag marks the abort");
        Assert.AreEqual(SOk, aborted.PreDeleteItem(RecycleIfPossible, null), "a later recycle callback");
        Check.True(aborted.AbortedNonRecycle, "and the mark stays");
    }

    // The very first callback without the flag (the item itself) marks the abort too.
    [TestMethod]
    [DataRow(0x00000000u)]
    [DataRow(0xFFFFFF7Fu)]
    public void TheSinkMarksAnAbortForTheItemItself(uint flags)
    {
        var sink = new ShellRecycler.Sink(@"C:\x\item");
        Assert.AreEqual(EAbort, sink.PreDeleteItem(flags, null));
        Check.True(sink.AbortedNonRecycle, $"flags 0x{flags:X8}");
    }
}
