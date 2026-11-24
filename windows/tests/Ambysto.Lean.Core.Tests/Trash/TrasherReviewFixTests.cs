using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Journal;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NativeIdentity = Ambysto.Lean.Core.Tests.Support.FileIdentity;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>The Trasher cases of the groups 10–11 interim review (plan "Review log", fixes 1 and 4; docs/spec/02 Windows
/// note on steps 6–7; docs/spec/05 <c>trashedIdentity</c>): a folder holding hard links is <c>trashed</c>, not
/// <c>lost</c>, and comes back by Put back; a descendant the shell lost makes the item <c>lost</c>; a journal write that
/// fails after the move names where the item went; a size that cannot be measured just before the move moves nothing. The
/// world is <see cref="TrashBench"/>: real files, a stand-in Recycle Bin on the same volume, a real journal.</summary>
[TestClass]
public sealed class TrasherReviewFixTests
{
    private static TrashOutcome Single(IReadOnlyList<TrashOutcome> outcomes)
    {
        Assert.AreEqual(1, outcomes.Count, "outcomes: " + string.Join(", ", outcomes.Select(o => o.Item.Node.Name + "=" + o.Result + " " + o.Reason)));
        return outcomes[0];
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>The gate's facts for the <c>$R</c> stand-in report it inside this user's Recycle Bin folder (as
    /// <see cref="PutBackBench.ReportInBin"/>), so Put back accepts it.</summary>
    private static void ReportInBin(TrashBench b, string standIn) => b.Gate.FactsFor(standIn, p =>
    {
        var real = FileFacts.Read(p);
        return real.Succeeded ? KitObjects.FactsOk(PutBackBench.InRecycleBin(p), real.Stamp!.Value) : real;
    });

    // --- Fix 1: hard links ------------------------------------------------------------------------------------------

    // A folder holding a file and a second hard link to it: the Recycle Bin item has every name (each with its length),
    // the same as the folder just before the move → trashed, not lost. Put back then restores the folder with both names,
    // still one file.
    [TestMethod]
    public async Task AFolderHoldingTwoLinksToOneFileIsTrashedAndPutBack()
    {
        using var b = new TrashBench("trash-links-inside");
        var a = b.File(@"proj\a.bin", 24 * 1024, seed: 8);
        var link = b.ItemPath(@"proj\b.bin");
        Native.CreateHardLink(link, a);
        var fileId = Native.IdentityOf(a);
        Assert.AreEqual(fileId, Native.IdentityOf(link), "precondition: one file, two names");
        var folder = b.ItemPath("proj");
        var folderId = Native.IdentityOf(folder);
        var tree = await b.ScanAsync();
        var session = TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj"));

        var outcome = Single(b.Run(session));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        var entry = outcome.Entry!;
        Assert.AreEqual(JournalResult.Trashed, entry.Result);
        Check.False(Exists(folder), "moved");
        var standIn = entry.TrashPath!;
        Check.True(File.Exists(Path.Combine(standIn, "a.bin")) && File.Exists(Path.Combine(standIn, "b.bin")), "both names are in the bin item");
        Assert.AreEqual(folderId.FileId, entry.TrashedIdentity!.Value.FileId, "the folder's file id");

        ReportInBin(b, standIn);
        var back = new PutBack(b.Journal, b.Gate).Restore(entry);

        Check.True(back.Restored, "put back; reason: " + back.Reason);
        Assert.AreEqual(folderId, Native.IdentityOf(folder), "the same folder, moved back");
        Assert.AreEqual(fileId, Native.IdentityOf(a), "a.bin is the same file");
        Assert.AreEqual(fileId, Native.IdentityOf(link), "b.bin is still a link of it");
        Assert.AreEqual(24 * 1024, new FileInfo(link).Length);
        Check.False(Exists(standIn), "nothing left at the $R path");
        Assert.AreEqual(JournalResult.Restored, b.Entries()[^1].Result);
    }

    // A file whose second link is outside the moved folder (outside the scan, or inside it at a path that sorts first, so
    // the scan counted it there), or a file item with a link elsewhere: trashed; the other link stays where it is.
    [TestMethod]
    [DataRow("folder", "outside-scan")]
    [DataRow("folder", "in-scan-first")]
    [DataRow("file", "outside-scan")]
    public async Task ALinkOutsideTheMovedItemStillTrashes(string kind, string where)
    {
        using var b = new TrashBench("trash-links-outside");
        var x = kind == "folder" ? b.File(@"proj\x.bin", 16 * 1024, seed: 9) : b.File("x.bin", 16 * 1024, seed: 9);
        b.File(@"proj\plain.bin", 3000, seed: 10);
        var other = where == "outside-scan" ? b.Temp.Sub("elsewhere.bin") : b.ItemPath("0first.bin");
        Native.CreateHardLink(other, x);
        var fileId = Native.IdentityOf(x);
        var tree = await b.ScanAsync();
        var item = kind == "folder" ? "proj" : "x.bin";

        var outcome = Single(b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, item))));

        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        Assert.AreEqual(JournalResult.Trashed, b.Entries().Single().Result);
        Check.False(Exists(b.ItemPath(item)), "moved");
        Assert.AreEqual(fileId, Native.IdentityOf(other), "the other link stays, the same file");
        var inBin = kind == "folder" ? Path.Combine(outcome.Entry!.TrashPath!, "x.bin") : outcome.Entry!.TrashPath!;
        Assert.AreEqual(fileId, Native.IdentityOf(inBin), "the moved name is in the bin");
    }

    // A reported item with the approved identity, but a file inside it is gone after the move (the shell lost a
    // descendant): in the bin, not as approved, and the session stops.
    [TestMethod]
    [DataRow("y.bin")]
    [DataRow(@"deep\z.bin")]
    public async Task ADescendantMissingFromTheReportedItemIsInTheBinNotAsApprovedAndStops(string missing)
    {
        using var b = new TrashBench("trash-lost-descendant");
        b.File(@"proj\x.bin", 4096, seed: 11);
        b.File(@"proj\y.bin", 4096, seed: 12);
        b.File(@"proj\deep\z.bin", 4096, seed: 13);
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        var folderId = Native.IdentityOf(b.ItemPath("proj"));
        NativeIdentity? reportedId = null;
        b.Recycler.AfterRecycle = (_, report) =>
        {
            reportedId = Native.IdentityOf(report.ReportedNewItemPath!);
            File.Delete(Path.Combine(report.ReportedNewItemPath!, missing));
        };

        var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj"), TrashBench.Item(tree, "c.bin")));

        Assert.AreEqual(folderId, reportedId, "precondition: the reported folder has the approved identity");
        // Owner decision 2026-10-17 (main session, test changed with the decision): in the bin, not as approved.
        TrasherTests.AssertMismatchAndStopped(b, outcomes, c);

        // Put back returns what is in the bin, as it is.
        var mismatched = b.Entries().Single();
        ReportInBin(b, mismatched.TrashPath!);
        var putBack = new PutBack(b.Journal, b.Gate).Restore(mismatched);
        Check.True(putBack.Restored, "put back: " + putBack.Reason);
        Check.True(Directory.Exists(b.ItemPath("proj")), "the folder is back");
    }

    // --- Fix 4: after the journal check ---------------------------------------------------------------------------

    // The journal accepts the preflight, then cannot be written once the item has moved: the outcome is trashed with
    // the words that name the $R path, nothing is journaled, and the session stops.
    [TestMethod]
    [DataRow("a.bin")]
    [DataRow("proj")]
    public async Task AJournalLostDuringTheMoveNamesWhereTheItemWent(string item)
    {
        using var b = new TrashBench("trash-not-journaled");
        b.File("a.bin", 6000);
        b.File(@"proj\p.bin", 5000);
        var c = b.File("c.bin");
        var tree = await b.ScanAsync();
        var id = Native.IdentityOf(b.ItemPath(item));
        IDisposable? readOnly = null;
        string? reported = null;
        b.Recycler.OnRecycle = p =>
        {
            readOnly ??= FileSwitches.ReadOnly(b.JournalPath);
            var report = b.Recycler.MoveToBin(p);
            reported = report.ReportedNewItemPath;
            return report;
        };
        IReadOnlyList<TrashOutcome> outcomes;
        try
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.LargeOld, TrashBench.Item(tree, item), TrashBench.Item(tree, "c.bin")));
        }
        finally
        {
            readOnly?.Dispose();
        }

        var outcome = Single(outcomes);
        Assert.IsNotNull(reported, "the move was reached: " + b.Log);
        Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason);
        Assert.AreEqual(Text.MovedButNotJournaled(reported!), outcome.Reason, "the report names the $R path");
        Assert.IsNull(outcome.Entry, "no entry could be written");
        Check.False(Exists(b.ItemPath(item)), "the item moved");
        Assert.AreEqual(id, Native.IdentityOf(reported!), "it is in the bin");
        Check.False(b.Log.Mentions(c), "the session stopped: " + b.Log);
        Check.True(File.Exists(c), "the later item stays");
        Assert.AreEqual(0, b.Entries().Count, "nothing was journaled");
    }

    // The size cannot be measured just before the move (a folder inside the item stops listing after the gate and the
    // in-use check): skipped with its reason, nothing moves.
    [TestMethod]
    public async Task ASizeThatCannotBeMeasuredBeforeTheMoveMovesNothing()
    {
        using var b = new TrashBench("trash-no-measure");
        b.File(@"proj\a.bin", 4096);
        b.File(@"proj\sub\b.bin", 4096);
        var sub = b.ItemPath(@"proj\sub");
        var tree = await b.ScanAsync();
        Undo? denied = null;
        b.Gate.AfterInUse = _ => denied ??= DenyListing(sub);
        IReadOnlyList<TrashOutcome> outcomes;
        try
        {
            outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj")));
        }
        finally
        {
            denied?.Dispose();
        }

        Assert.IsNotNull(denied, "precondition: the in-use check was reached: " + b.Log);
        var outcome = Single(outcomes);
        Assert.AreEqual(TrashResult.Skipped, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
        Assert.AreEqual(Text.CouldNotMeasureBeforeMove, outcome.Reason);
        Assert.AreEqual(0, b.Recycler.Paths.Count, "no move: " + b.Log);
        Check.True(File.Exists(b.ItemPath(@"proj\sub\b.bin")) && File.Exists(b.ItemPath(@"proj\a.bin")), "the item stays whole");
        var entry = b.Entries().Single();
        Assert.AreEqual(JournalResult.Skipped, entry.Result, "one entry for the item (docs/spec/02 step 8)");
        Assert.AreEqual(Text.CouldNotMeasureBeforeMove, entry.Reason);
    }

    /// <summary>Denies the current user listing <paramref name="folder"/>; dispose removes that one rule (the user owns
    /// the folder, so it can always undo it).</summary>
    private static Undo DenyListing(string folder)
    {
        var info = new DirectoryInfo(folder);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Undo(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(current);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? pending = undo;

        public void Dispose() => System.Threading.Interlocked.Exchange(ref pending, null)?.Invoke();
    }
}
