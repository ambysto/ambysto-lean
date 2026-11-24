using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Journal;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Intent criterion 1 (and decisions 1, 2 and 6): every probe row of the intent's facts (the Recycle Bin deletes
/// these for good while reporting success) is refused before any move, through the real Trasher and the real
/// <see cref="KitItemGate"/>, with the Kit's reason. The shell move is a <see cref="FailingRecycler"/>: reaching it fails the
/// test. Rows built here: removable FAT32 (F:), removable NTFS (H:, <c>AMBYSTO_REMOVABLE_NTFS</c>), the network share
/// <c>\\localhost\ambynet</c>, a 260+ path (as the item and inside a short folder), an online-only cloud file and a folder
/// holding one (AmbyCloud), and Lean's own journal folder. The rows that need an administrator (bin off, policy, quota,
/// purge, elevated) are not built here. Console session: the gate refuses everything in session 0 (that row is
/// <see cref="Session0Tests"/>). One at a time: the cloud fixtures must not run in parallel.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RealGateRefusalTests
{
    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRoots();
        }
    }

    /// <summary>Makes the real gate (so the test reaches it in any session), then Inconclusive in session 0.</summary>
    internal static void RequireConsoleSession()
    {
        _ = new KitItemGate(LeanModule.Map, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (Process.GetCurrentProcess().SessionId == 0)
        {
            Assert.Inconclusive("the real gate refuses every move in session 0 (not interactive); run in the console session");
        }
    }

    /// <summary>A tree holding one item at <paramref name="path"/>: the scan's node when Lean's scanner can read it, else
    /// a node with the Kit's stamp, else with a made-up stamp (the gate refuses these rows before it compares stamps).</summary>
    internal static async Task<(ScanTree Tree, ScanNode Node)> TreeOfAsync(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        WaitForListingToSettle(parent, path);
        try
        {
            var scanned = await new Scanner().ScanAsync(parent, null, CancellationToken.None);
            var node = Trees.Find(scanned.Root, Path.GetFileName(path));
            if (node is not null && node.Stamp is not null && node.Id != 0)
            {
                return (scanned, node);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Lean scans the profile's volume; a row on another kind of volume gets a node made here.
        }

        var facts = FileFacts.Read(path);
        var folder = Directory.Exists(path);
        var stamp = facts.Succeeded
            ? new ItemStamp(facts.Stamp!.Value.Identity.VolumeSerial, facts.Stamp.Value.Identity.FileId, facts.Stamp.Value.LastWriteTimeUtcTicks, facts.Stamp.Value.Length)
            : new ItemStamp(1, 1, File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), folder ? null : new FileInfo(path).Length);
        var root = ScanNode.Folder(parent);
        var item = folder ? ScanNode.Folder(path, 0, stamp) : ScanNode.File(path, 4096, 4096, 0, stamp);
        root.Add(item);
        var tree = Trees.Tree(root, 4096);
        return (tree, item);
    }

    // The walker stamps a child from its parent's listing, and NTFS updates a folder's entry there a few seconds after the
    // folder itself (probe 2026-10-18 in the VM: 61 of 600 new folders differed at once, none after 5 s; a file added to an
    // old folder: 300 of 300 at once). A fixture built just before the scan would then carry a stale stamp, and the gate
    // would rightly refuse it as "Changed since the scan" (the flaky APathOf260OrMoreIsRefused, 2 of about 6 console runs).
    // Wait until the listing shows the item's own last-write time, as it would for anything not changed seconds ago.
    private static void WaitForListingToSettle(string parent, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var listed = new DirectoryInfo(parent).EnumerateFileSystemInfos(Path.GetFileName(path)).FirstOrDefault();
            if (listed is null || listed.LastWriteTimeUtc == (Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)))
            {
                return;
            }

            Thread.Sleep(100);
        }

        Assert.Fail("the parent's listing never showed the fixture's own last-write time: " + path);
    }

    /// <summary>Runs the real Trasher over the one item and checks it was refused with one of <paramref name="reasons"/>,
    /// journaled as skipped, never moved.</summary>
    internal static async Task AssertRefusedBeforeAnyMoveAsync(string path, LeanModule module, params RefusalReason[] reasons)
    {
        using var journalFolder = new TempFolder("realgate-journal");
        var journal = JournalStore.Open(journalFolder.Sub("journal.jsonl"));
        var (tree, node) = await TreeOfAsync(path);
        var recycler = new FailingRecycler();
        var gate = new KitItemGate(module, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var session = new DeletionSession(module, [new SessionItem(node, null)]);

        var outcomes = new Trasher(gate, recycler, journal).Run(session, CancellationToken.None);

        Assert.AreEqual(0, recycler.Calls, "the shell move was reached for " + path);
        Assert.AreEqual(1, outcomes.Count);
        var outcome = outcomes[0];
        var expected = reasons.Select(r => r.Message()).ToArray();
        Assert.AreEqual(TrashResult.Skipped, outcome.Result, $"{path}: {outcome.Result} {outcome.Reason}");
        Check.True(expected.Contains(outcome.Reason), $"{path}: refused with \"{outcome.Reason}\", expected one of [{string.Join(" | ", expected)}]");
        Check.True(File.Exists(path) || Directory.Exists(path), "the item is in place: " + path);
        var entry = journal.Read().Single();
        Assert.AreEqual(JournalResult.Skipped, entry.Result);
        Assert.AreEqual(outcome.Reason, entry.Reason);
    }

    private static string FixtureFolderOn(string root, string name)
    {
        if (!Directory.Exists(root))
        {
            Assert.Inconclusive(root + " is not on this machine (the test-machine notes, Volumes)");
        }

        var folder = Path.Combine(root, "LeanCoreTests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("cannot write a fixture at " + folder + ": " + e.Message);
        }

        return folder;
    }

    private static async Task OnVolumeAsync(string root, string name, DriveType? driveType, params RefusalReason[] reasons)
    {
        if (driveType is not null)
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != driveType)
            {
                Assert.Inconclusive($"{root} is not a ready {driveType} drive here");
            }
        }

        var folder = FixtureFolderOn(root, name);
        try
        {
            var file = Path.Combine(folder, "old.iso");
            TempFolder.WriteBytes(file, 64 * 1024, 1);
            await AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, reasons);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            var parent = Path.GetDirectoryName(folder)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    // Removable FAT32 (F:): no Recycle Bin, "send to Recycle Bin" deletes for good.
    [TestMethod]
    [TestCategory("Console")]
    public async Task AFileOnARemovableFatDriveIsRefused()
    {
        RequireConsoleSession();
        await OnVolumeAsync(@"F:\", "removable-fat", DriveType.Removable, RefusalReason.UnsupportedVolume);
    }

    // Removable NTFS (H:): refused by drive type, not by file system.
    [TestMethod]
    [TestCategory("Console")]
    public async Task AFileOnARemovableNtfsDriveIsRefused()
    {
        RequireConsoleSession();
        var root = Environment.GetEnvironmentVariable("AMBYSTO_REMOVABLE_NTFS");
        if (string.IsNullOrEmpty(root))
        {
            Assert.Inconclusive("AMBYSTO_REMOVABLE_NTFS is not set (the test machine's H:)");
        }

        Assert.AreEqual("NTFS", new DriveInfo(root!).DriveFormat, "the fixture drive is NTFS");
        await OnVolumeAsync(root!, "removable-ntfs", DriveType.Removable, RefusalReason.UnsupportedVolume);
    }

    // The network share: no Recycle Bin either.
    [TestMethod]
    [TestCategory("Console")]
    public async Task AFileOnTheNetworkShareIsRefused()
    {
        RequireConsoleSession();
        await OnVolumeAsync(@"\\localhost\ambynet\", "network", null, RefusalReason.UnsupportedPathForm, RefusalReason.UnsupportedVolume);
    }

    // A path of 260+ characters: deleted for good. As the item itself, and inside a short folder (the whole tree would be
    // deleted for good, its short files included).
    [TestMethod]
    [TestCategory("Console")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task APathOf260OrMoreIsRefused(bool insideAShortFolder)
    {
        RequireConsoleSession();
        using var temp = new TempFolder("realgate-long");
        var top = temp.Folder("short");
        TempFolder.WriteBytes(Path.Combine(top, "small.txt"), 100, 1);
        var deep = top;
        while (deep.Length < 270)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        var longFile = Path.Combine(deep, "long-name-file.bin");
        TempFolder.WriteBytes(longFile, 4096, 2);
        Check.AtLeast(260, longFile.Length, "the fixture path length");

        await AssertRefusedBeforeAnyMoveAsync(insideAShortFolder ? top : longFile, LeanModule.Map, RefusalReason.PathTooLong);
        Check.True(File.Exists(Path.Combine(top, "small.txt")), "the short sibling is in place");
    }

    // An online-only cloud file (decision 1) and a folder holding one (decision 4): refused, never fetched.
    [TestMethod]
    [TestCategory("Console")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnOnlineOnlyCloudFileOrAFolderHoldingOneIsRefusedAndNeverFetched(bool folder)
    {
        RequireConsoleSession();
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-trash");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate(("a.bin", 64 * 1024), (@"docs\", 0), (@"docs\b.bin", 64 * 1024), ("control.bin", 4096));
        cloud.Serve();
        cloud.RequireOnlineOnly("a.bin");
        cloud.RequireOnlineOnly(@"docs\b.bin");
        ProveTheFetchLogCanFail(cloud);

        await AssertRefusedBeforeAnyMoveAsync(cloud.Combine(folder ? "docs" : "a.bin"), LeanModule.Map, RefusalReason.CloudFile, RefusalReason.CloudSyncFolder);

        cloud.AssertNoFetch("the gate and the Trasher");
    }

    // ASP-7 criterion 1 includes partial placeholders separately from online-only ones. Assert the physical
    // state, then use the real Trasher and Kit gate; neither metadata inspection nor refusal may fetch the missing tail.
    [TestMethod]
    [TestCategory("Console")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task APartiallyHydratedCloudFileOrFolderIsRefusedWithoutFetchingItsTail(bool folder)
    {
        RequireConsoleSession();
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-trash-partial");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((@"docs\", 0), (@"docs\partial.bin", 4 * 1024 * 1024), ("control.bin", 4096));
        cloud.Serve();
        ProveTheFetchLogCanFail(cloud);
        CloudPartial.KeepPrefix(cloud, @"docs\partial.bin", 65536);
        var before = cloud.Status(@"docs\partial.bin");

        await AssertRefusedBeforeAnyMoveAsync(cloud.Combine(folder ? "docs" : @"docs\partial.bin"), LeanModule.Map,
            RefusalReason.CloudFile, RefusalReason.CloudSyncFolder);

        cloud.AssertNoFetch("real Trasher refusal of a partially hydrated placeholder");
        var after = cloud.Status(@"docs\partial.bin");
        Assert.AreEqual(before.OnDisk, after.OnDisk, "the missing tail was not hydrated");
        Assert.AreEqual(before.Size, after.Size);
        Assert.IsTrue(after.IsPlaceholder && after.RecallOnDataAccess);
    }

    private static void ProveTheFetchLogCanFail(CloudRoot cloud)
    {
        cloud.RequireOnlineOnly("control.bin");
        using (var stream = new FileStream(cloud.Combine("control.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.True(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check.True(cloud.FetchesOf("control.bin").Any(l => l.Contains("\"pid\":" + pid + ",", StringComparison.Ordinal) || l.Contains("\"pid\":" + pid + "}", StringComparison.Ordinal)),
            "the fetch log did not record this process's read of control.bin");
        cloud.MarkFetchBaseline();
    }

    // Lean's journal folder is never-touch (plan Design "Journal": an app-protected folder of the Kit's context).
    [TestMethod]
    [TestCategory("Console")]
    public async Task AnItemInLeansJournalFolderIsRefusedAsProtected()
    {
        RequireConsoleSession();
        var folder = Path.GetDirectoryName(JournalStore.DefaultPath)!;
        var created = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "lean-test-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        try
        {
            TempFolder.WriteBytes(file, 4096, 1);
            await AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, RefusalReason.Protected);
        }
        finally
        {
            File.Delete(file);
            if (created && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
    }
}

/// <summary>Intent criterion 1, row "session 0", and criterion 2's "Put back while elevated is refused" in the form a
/// standard user can build: outside an interactive session the real gate refuses every move and every put-back with the
/// Kit's environment reason, before anything else. Runs only in session 0 (the gates' <c>core</c> suite).</summary>
[TestClass]
public sealed class Session0Tests
{
    private static void RequireSession0()
    {
        if (Process.GetCurrentProcess().SessionId != 0)
        {
            Assert.Inconclusive("this test is for session 0 (the gates' core suite over SSH)");
        }
    }

    [TestMethod]
    public async Task InSession0TheRealGateRefusesEveryMove()
    {
        RequireSession0();
        using var temp = new TempFolder("session0-trash");
        var file = temp.File(@"items\old.iso", 4096);
        await RealGateRefusalTests.AssertRefusedBeforeAnyMoveAsync(file, LeanModule.LargeOld, RefusalReason.NotInteractive);
    }

    [TestMethod]
    public void InSession0PutBackIsRefusedFirstByTheEnvironment()
    {
        RequireSession0();
        using var b = new PutBackBench("session0-putback");
        var entry = b.Stage("a.bin");
        var putBack = new PutBack(b.Journal, new KitItemGate(LeanModule.Map, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var outcome = putBack.Restore(entry);

        Check.False(outcome.Restored, "restored in session 0");
        Assert.AreEqual(RefusalReason.NotInteractive.Message(), outcome.Reason);
        Check.True(File.Exists(entry.TrashPath), "the $R item stays");
        Check.False(File.Exists(entry.OriginalPath), "nothing at the original path");
        Assert.AreEqual(JournalResult.RestoreFailed, b.Entries()[^1].Result);
    }
}
