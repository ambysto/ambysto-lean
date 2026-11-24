using System;
using System.Collections.Generic;
using System.IO;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Settings;

/// <summary>docs/spec/02 Windows note (the user's protected folders) and plan Rules "Protected folders": the user's folders
/// join Lean's own in the Kit's context (<see cref="KitItemGate"/>'s third argument), so the real gate refuses an item
/// inside, or holding, one of them as <see cref="RefusalReason.Protected"/>, and the restore check refuses a destination
/// inside one (owner decision 2026-10-18). Each refusal has a control: the 2-argument gate approves the same item, so the
/// refusal comes from the folders passed, not from the fixture. Items are in root (a) of the real profile, made and removed
/// by <see cref="RealScratch"/>. Console session (the gate refuses everything in session 0).</summary>
[TestClass]
[DoNotParallelize]
public sealed class ProtectedFolderGateTests
{
    private static readonly IReadOnlySet<string> NoItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [ClassInitialize]
    public static void Sweep(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RealScratch.SweepStale();
    }

    /// <summary>The fixture: <c>&lt;profile&gt;\LeanGateTests-…\Outer\Keep</c> is the protected folder, with a file and a
    /// folder inside, a file next to it, and its parent <c>Outer</c> holding it.</summary>
    private sealed class Layout
    {
        public Layout(RealScratch scratch)
        {
            var top = scratch.NewFolder(RealGate.Profile);
            Outer = RealScratch.Folder(Path.Combine(top, "Outer"));
            Keep = RealScratch.Folder(Path.Combine(Outer, "Keep"));
            InsideFile = RealScratch.File(Path.Combine(Keep, "inside.bin"));
            InsideFolder = RealScratch.Folder(Path.Combine(Keep, "sub"));
            RealScratch.File(Path.Combine(InsideFolder, "deep.bin"));
            NextFile = RealScratch.File(Path.Combine(Outer, "next.bin"));
            NextFolder = RealScratch.Folder(Path.Combine(Outer, "Keep2"));
            RealScratch.File(Path.Combine(NextFolder, "x.bin"));
        }

        public string Outer { get; }

        public string Keep { get; }

        public string InsideFile { get; }

        public string InsideFolder { get; }

        public string NextFile { get; }

        /// <summary>A sibling whose name starts like the protected folder's.</summary>
        public string NextFolder { get; }

        public IReadOnlyList<string> Protected => [RealGate.Canonical(Keep)];
    }

    private static void RefusedAsProtected(GateResult result, string what)
    {
        RealGate.Refused(result, what);
        Assert.AreEqual(RefusalReason.Protected, result.Reason, $"{what}: refused with {result.Reason} ({result.Message} [{result.Detail}])");
    }

    // An item inside a user-protected folder (a file, a folder, the folder itself) is refused as Protected.
    [TestMethod]
    [TestCategory("Console")]
    public void AnItemInsideAProtectedFolderIsRefused()
    {
        using var scratch = new RealScratch();
        var layout = new Layout(scratch);
        string[] items = [layout.InsideFile, layout.InsideFolder, layout.Keep];

        var withFolders = new KitItemGate(LeanModule.Map, NoItems, layout.Protected);
        var without = new KitItemGate(LeanModule.Map, NoItems);
        foreach (var item in items)
        {
            RealGate.Approved(without.Check(RealGate.Request(item)), "control, the 2-argument gate: " + item);
            RefusedAsProtected(withFolders.Check(RealGate.Request(item)), item);
        }
    }

    // An item holding a user-protected folder is refused as Protected.
    [TestMethod]
    [TestCategory("Console")]
    public void AnItemHoldingAProtectedFolderIsRefused()
    {
        using var scratch = new RealScratch();
        var layout = new Layout(scratch);

        RealGate.Approved(new KitItemGate(LeanModule.Map, NoItems).Check(RealGate.Request(layout.Outer)), "control, the 2-argument gate");
        RefusedAsProtected(new KitItemGate(LeanModule.Map, NoItems, layout.Protected).Check(RealGate.Request(layout.Outer)), layout.Outer);
    }

    // An item next to a user-protected folder (a file, and a folder whose name starts the same) is approved.
    [TestMethod]
    [TestCategory("Console")]
    public void AnItemNextToAProtectedFolderIsApproved()
    {
        using var scratch = new RealScratch();
        var layout = new Layout(scratch);
        var gate = new KitItemGate(LeanModule.Map, NoItems, layout.Protected);

        RealGate.Approved(gate.Check(RealGate.Request(layout.NextFile)), layout.NextFile);
        RealGate.Approved(gate.Check(RealGate.Request(layout.NextFolder)), layout.NextFolder);
    }

    // Every module's gate gets the folders (the Temporary files module included).
    [TestMethod]
    [TestCategory("Console")]
    public void EveryModulesGateRefusesTheProtectedFolders()
    {
        using var scratch = new RealScratch();
        scratch.EnsureFolder(RealGate.LocalTemp);
        var tempKeep = scratch.NewFolder(RealGate.LocalTemp);
        var tempItem = RealScratch.File(Path.Combine(tempKeep, "old.tmp"));
        IReadOnlyList<string> folders = [RealGate.Canonical(tempKeep)];

        RealGate.Approved(new KitItemGate(LeanModule.Temp, NoItems).Check(RealGate.Request(tempKeep)), "control: the Temp module may move a child of %LOCALAPPDATA%\\Temp");
        RefusedAsProtected(new KitItemGate(LeanModule.Temp, NoItems, folders).Check(RealGate.Request(tempKeep)), "the Temp child itself");
        RefusedAsProtected(new KitItemGate(LeanModule.Temp, NoItems, folders).Check(RealGate.Request(tempItem)), "inside the Temp child");
        RefusedAsProtected(new KitItemGate(LeanModule.LargeOld, NoItems, folders).Check(RealGate.Request(tempItem)), "Large and old");
        RefusedAsProtected(new KitItemGate(LeanModule.Duplicates, NoItems, folders).Check(RealGate.Request(tempItem)), "Duplicates");
    }

    // The restore check refuses a put-back destination inside a user-protected folder (also one protected after the move:
    // the folders are read when the gate is made) and approves one next to it.
    [TestMethod]
    [TestCategory("Console")]
    public void APutBackIntoAProtectedFolderIsRefused()
    {
        using var scratch = new RealScratch();
        var layout = new Layout(scratch);
        var intoKeep = Path.Combine(layout.Keep, "back.bin");
        var deeper = Path.Combine(layout.InsideFolder, "back.bin");
        var nextTo = Path.Combine(layout.Outer, "back.bin");

        var without = new KitItemGate(LeanModule.Map, NoItems);
        RealGate.Approved(without.CheckRestoreDestination(intoKeep), "control, the 2-argument gate: " + intoKeep);
        RealGate.Approved(without.CheckRestoreDestination(deeper), "control, the 2-argument gate: " + deeper);

        var withFolders = new KitItemGate(LeanModule.Map, NoItems, layout.Protected);
        RefusedAsProtected(withFolders.CheckRestoreDestination(intoKeep), intoKeep);
        RefusedAsProtected(withFolders.CheckRestoreDestination(deeper), deeper);
        RealGate.Approved(withFolders.CheckRestoreDestination(nextTo), nextTo);
    }

    // With an empty list the 3-argument gate answers as the 2-argument one.
    [TestMethod]
    [TestCategory("Console")]
    public void AnEmptyListProtectsNothingMore()
    {
        using var scratch = new RealScratch();
        var layout = new Layout(scratch);

        RealGate.Approved(new KitItemGate(LeanModule.Map, NoItems, []).Check(RealGate.Request(layout.InsideFile)), "an empty list");
        RealGate.Approved(new KitItemGate(LeanModule.Map, NoItems, []).CheckRestoreDestination(Path.Combine(layout.Keep, "back.bin")), "an empty list, restore");
    }
}
