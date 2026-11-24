using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01 Windows note (owner decision 2026-10-18), plan Design row <c>Measure.ScanTree</c>: when a scan ends
/// or is cancelled, <see cref="ScanTree.Summary"/> says how many files and folders it read, how many entries could not be
/// read, how long it took and whether it was cancelled. Reading pinned here: the counts are the tree's nodes (every file
/// node; every folder node, the scanned root and a folder that could not be listed included) and the unreadable count is
/// the coverage's.</summary>
[TestClass]
public sealed class ScanSummaryTests
{
    private static Task<ScanTree> Scan(string root, CancellationToken token = default) => new Scanner().ScanAsync(root, null, token);

    /// <summary>a.bin, sub\b.bin, sub\deep\c.bin, sub\deep\d.bin and an empty folder: 4 files, 4 folders with the root.</summary>
    private static void MakeReadable(TempFolder temp)
    {
        temp.File("a.bin", 4096);
        temp.File(@"sub\b.bin", 4096);
        temp.File(@"sub\deep\c.bin", 4096);
        temp.File(@"sub\deep\d.bin", 10);
        temp.Folder("empty");
    }

    // A readable tree: the counts, nothing unreadable, a time, not cancelled.
    [TestMethod]
    public async Task TheSummaryCountsAReadableTree()
    {
        using var temp = new TempFolder("summary-readable");
        MakeReadable(temp);

        var tree = await Scan(temp.Path);
        var summary = tree.Summary;

        Assert.AreEqual(4L, summary.Files, "files");
        Assert.AreEqual(4L, summary.Folders, "folders: the root, sub, sub\\deep, empty");
        Assert.AreEqual(0L, summary.Unreadable);
        Check.True(summary.Elapsed > TimeSpan.Zero, "elapsed " + summary.Elapsed);
        Assert.IsFalse(summary.Cancelled);
        Assert.AreEqual(Trees.Files(tree.Root).LongCount(), summary.Files, "the tree's file nodes");
        Assert.AreEqual(Trees.All(tree.Root).LongCount(n => n.IsFolder), summary.Folders, "the tree's folder nodes");
    }

    // A folder that cannot be listed (a deny ACE on a scratch folder, undone after the scan) is counted as unreadable; the
    // file inside it was not read and is not counted; the rest is.
    [TestMethod]
    public async Task AnUnreadableFolderIsCountedAsUnreadable()
    {
        using var temp = new TempFolder("summary-unreadable");
        MakeReadable(temp);
        var locked = temp.Folder("locked");
        temp.File(@"locked\inside.bin", 4096);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        ScanTree tree;
        try
        {
            tree = await Scan(temp.Path);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }

        var summary = tree.Summary;
        Assert.AreEqual(1L, summary.Unreadable, "the locked folder");
        Assert.AreEqual(tree.Coverage.UnreadableCount, summary.Unreadable, "the same count as the coverage");
        Assert.AreEqual(4L, summary.Files, "the file inside the locked folder was not read");
        Assert.AreEqual(5L, summary.Folders, "folders: the root, sub, sub\\deep, empty and the locked one");
        Check.True(summary.Elapsed > TimeSpan.Zero, "elapsed " + summary.Elapsed);
        Assert.IsFalse(summary.Cancelled);
    }

    // The elapsed time is the scan's: a scan that took longer reports more (a scan with a wait after each folder batch).
    [TestMethod]
    public async Task TheElapsedTimeIsTheScansDuration()
    {
        using var temp = new TempFolder("summary-elapsed");
        for (var i = 0; i < 5; i++)
        {
            temp.File(Path.Combine("d" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "f.bin"), 10);
        }

        var tree = await Scanner.ScanAsync(temp.Path, null, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Check.True(tree.Summary.Elapsed >= TimeSpan.FromMilliseconds(100), "elapsed " + tree.Summary.Elapsed + " for a scan that waited at least 100 ms");
        Check.True(tree.Summary.Elapsed < TimeSpan.FromMinutes(1), "elapsed " + tree.Summary.Elapsed);
    }

    // After a cancel the summary says so (the tree is partial).
    [TestMethod]
    public async Task ACancelledScanSaysCancelled()
    {
        using var temp = new TempFolder("summary-cancel");
        MakeReadable(temp);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var tree = await Scan(temp.Path, cancel.Token);

        Assert.IsTrue(tree.Cancelled, "setup: the scan was cancelled");
        Assert.IsTrue(tree.Summary.Cancelled);
        Check.True(tree.Summary.Elapsed >= TimeSpan.Zero, "elapsed " + tree.Summary.Elapsed);
        Check.AtMost(4, tree.Summary.Files, "a partial scan counts at most what is there");
    }

    // A cancel mid-walk: cancelled, with what was read so far.
    [TestMethod]
    public async Task ACancelMidWalkSaysCancelledWithThePartialCounts()
    {
        using var temp = new TempFolder("summary-cancel-mid");
        for (var i = 0; i < 40; i++)
        {
            temp.File(Path.Combine("d" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "f.bin"), 10);
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var tree = await Scanner.ScanAsync(temp.Path, null, TimeSpan.FromMilliseconds(100), cancel.Token);

        if (!tree.Cancelled)
        {
            Assert.Inconclusive("the walk finished before the cancel landed");
        }

        Assert.IsTrue(tree.Summary.Cancelled);
        Assert.AreEqual(Trees.Files(tree.Root).LongCount(), tree.Summary.Files, "the files read so far");
        Check.True(tree.Summary.Elapsed > TimeSpan.Zero, "elapsed " + tree.Summary.Elapsed);
    }
}
