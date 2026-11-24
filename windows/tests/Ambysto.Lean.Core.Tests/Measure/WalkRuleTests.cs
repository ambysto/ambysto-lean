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

/// <summary>docs/spec/01, "Walk rules" 1–8 with their Windows notes, on real files in a scratch folder.</summary>
[TestClass]
public sealed class WalkRuleTests
{
    private const long OneHundredKb = 100 * ByteUnits.KB;

    private static Task<ScanTree> Scan(string root) => new Scanner().ScanAsync(root, null, CancellationToken.None);

    // docs/spec/01 walk rule 3 (Windows note 3): size = allocated bytes; a sparse file counts what it uses.
    [TestMethod]
    public async Task SparseFileCountsItsAllocatedBytesNotItsLength()
    {
        using var temp = new TempFolder("walk-sparse");
        Native.CreateSparseFile(temp.Sub("sparse.bin"), 64 * ByteUnits.MB);
        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "sparse.bin");
        Assert.AreEqual(64 * ByteUnits.MB, node.LogicalBytes);
        Check.AtMost(ByteUnits.MB, node.AllocatedBytes, "allocated bytes of a 64 MB sparse file");
        Assert.AreEqual(node.AllocatedBytes, tree.Root.AllocatedBytes, "the folder total sums allocated sizes");
    }

    // docs/spec/01 walk rule 3: allocated bytes on disk (blocks × block size), not the logical length.
    [TestMethod]
    public async Task OrdinaryFileIsCountedInWholeClusters()
    {
        using var temp = new TempFolder("walk-cluster");
        var cluster = Native.ClusterSizeOf(Path.GetPathRoot(temp.Path)!);
        var length = OneHundredKb + 1;
        temp.File("plain.bin", length);
        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "plain.bin");
        Assert.AreEqual(length, node.LogicalBytes);
        Check.AtLeast(length, node.AllocatedBytes, "allocated is never below the length of an ordinary file");
        Assert.AreEqual(0L, node.AllocatedBytes % cluster, "allocated size is whole clusters");
        Check.AtMost(length + cluster, node.AllocatedBytes, "allocated size is at most one cluster above the length");
    }

    // docs/spec/01 walk rule 4 (Windows note 4): hard links recognised by file id; counted once at the path that sorts
    // first (byte-wise); every other link shows size 0 and is flagged.
    [TestMethod]
    public async Task HardLinksAreCountedOnceAtThePathThatSortsFirst()
    {
        using var temp = new TempFolder("walk-hardlink");
        var first = temp.File(@"aaa\link.bin", OneHundredKb);
        temp.Folder("zzz");
        Native.CreateHardLink(temp.Sub("mmm.bin"), first);
        Native.CreateHardLink(temp.Sub("zzz", "link.bin"), first);

        var tree = await Scan(temp.Path);
        var counted = Trees.Node(tree, @"aaa\link.bin");
        var second = Trees.Node(tree, "mmm.bin");
        var third = Trees.Node(tree, @"zzz\link.bin");

        Assert.AreEqual(OneHundredKb, counted.LogicalBytes);
        Check.AtLeast(OneHundredKb, counted.AllocatedBytes, "the counted link carries the size");
        Assert.IsFalse(counted.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "the first path is the counted one");
        Assert.AreEqual(0L, second.AllocatedBytes);
        Assert.IsTrue(second.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(0L, third.AllocatedBytes);
        Assert.IsTrue(third.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(counted.AllocatedBytes, tree.Root.AllocatedBytes, "the tree counts the file once");
    }

    // docs/spec/01 walk rule 4: "sorts first (byte-wise)": 'B' (0x42) sorts before 'a' (0x61), whatever the culture says.
    [TestMethod]
    public async Task HardLinkOrderIsByteWiseNotCaseInsensitive()
    {
        using var temp = new TempFolder("walk-hardlink-order");
        var lower = temp.File("a.bin", OneHundredKb);
        Native.CreateHardLink(temp.Sub("B.bin"), lower);

        var tree = await Scan(temp.Path);
        var upper = Trees.Node(tree, "B.bin");
        var lowerNode = Trees.Node(tree, "a.bin");
        Assert.IsFalse(upper.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "B.bin sorts first byte-wise and carries the size");
        Check.AtLeast(OneHundredKb, upper.AllocatedBytes, "B.bin allocated");
        Assert.IsTrue(lowerNode.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(0L, lowerNode.AllocatedBytes);
    }

    // docs/spec/01 walk rule 4 (Windows note 4): hard links are recognised by file id; the stamp carries that id.
    [TestMethod]
    public async Task HardLinksShareOneFileIdInTheirStamps()
    {
        using var temp = new TempFolder("walk-hardlink-id");
        var one = temp.File("one.bin", OneHundredKb);
        Native.CreateHardLink(temp.Sub("two.bin"), one);
        var expected = Native.IdentityOf(one);

        var tree = await Scan(temp.Path);
        var a = Trees.Node(tree, "one.bin").Stamp;
        var b = Trees.Node(tree, "two.bin").Stamp;
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreEqual(expected.FileId, a.Value.FileId);
        Assert.AreEqual(expected.FileId, b.Value.FileId);
        Assert.AreEqual(expected.VolumeSerial, a.Value.VolumeSerial);
    }

    // Plan "Walker": each node's stamp has the fields of the Kit's FileStamp (volume serial, 128-bit file id, last write,
    // length; null length for folders), read from the folder batches.
    [TestMethod]
    public async Task StampHoldsVolumeSerialFileIdLastWriteAndLength()
    {
        using var temp = new TempFolder("walk-stamp");
        var file = temp.File(@"sub\data.bin", OneHundredKb);
        var folder = temp.Sub("sub");
        var fileIdentity = Native.IdentityOf(file);
        var folderIdentity = Native.IdentityOf(folder);
        var lastWrite = File.GetLastWriteTimeUtc(file);

        var tree = await Scan(temp.Path);
        var fileStamp = Trees.Node(tree, @"sub\data.bin").Stamp;
        var folderStamp = Trees.Node(tree, "sub").Stamp;
        Assert.IsNotNull(fileStamp);
        Assert.IsNotNull(folderStamp);
        Assert.AreEqual(fileIdentity.VolumeSerial, fileStamp.Value.VolumeSerial);
        Assert.AreEqual(fileIdentity.FileId, fileStamp.Value.FileId);
        Assert.AreEqual(OneHundredKb, fileStamp.Value.Length);
        Assert.AreEqual(lastWrite.ToFileTimeUtc(), fileStamp.Value.LastWriteTimeUtcTicks, "a FILETIME, as the Kit's FileStamp holds it");
        Assert.AreEqual(folderIdentity.FileId, folderStamp.Value.FileId);
        Assert.IsNull(folderStamp.Value.Length, "a folder has no length");
    }

    // docs/spec/01 walk rule 2 (Windows note 2): a junction is a link: never entered, costs its own size only.
    [TestMethod]
    public async Task JunctionIsNeverEnteredAndCostsOnlyItself()
    {
        using var temp = new TempFolder("walk-junction");
        var target = temp.Folder("target");
        temp.File(@"target\big.bin", 8 * ByteUnits.MB);
        Native.CreateJunction(temp.Sub("scanned", "jump"), target);
        var scanned = temp.Sub("scanned");

        var tree = await Scan(scanned);
        var link = Trees.Node(tree, "jump");
        Assert.IsTrue(link.Flags.HasFlag(NodeMarks.Link), "a junction is flagged as a link");
        Assert.IsFalse(link.IsFolder, "a link is never a folder here");
        Assert.AreEqual(0, link.Children.Count, "never entered");
        Check.AtMost(ByteUnits.MB, link.AllocatedBytes, "a junction costs its own size only");
        Check.AtMost(ByteUnits.MB, tree.Root.AllocatedBytes, "the target's 8 MB are not counted through the junction");
        Assert.IsTrue(File.Exists(temp.Sub("target", "big.bin")), "the target is untouched");
    }

    // docs/spec/01 walk rule 1 (Windows note 1): stay on one volume; a junction to another volume is never entered.
    [TestMethod]
    public async Task JunctionToAnotherVolumeIsNeverEntered()
    {
        const string otherVolume = @"E:\";
        using var temp = new TempFolder("walk-junction-volume");
        if (!Directory.Exists(otherVolume) || string.Equals(Path.GetPathRoot(temp.Path), otherVolume, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("needs a second volume at E:, other than the one the scratch folder is on (the test machine has one)");
        }

        var otherFolder = Path.Combine(otherVolume, "LeanCoreTests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherFolder);
        try
        {
            TempFolder.WriteBytes(Path.Combine(otherFolder, "big.bin"), 8 * ByteUnits.MB, 1);
            Native.CreateJunction(temp.Sub("jump"), otherFolder);

            var tree = await Scan(temp.Path);
            var link = Trees.Node(tree, "jump");
            Assert.IsTrue(link.Flags.HasFlag(NodeMarks.Link));
            Assert.AreEqual(0, link.Children.Count);
            Check.AtMost(ByteUnits.MB, tree.Root.AllocatedBytes, "nothing on E: is counted");
        }
        finally
        {
            Directory.Delete(otherFolder, recursive: true);
        }
    }

    // docs/spec/01 walk rule 2: never follow symbolic links; a link costs its own size only.
    [TestMethod]
    public async Task SymbolicLinkToAFileIsNeverFollowed()
    {
        using var temp = new TempFolder("walk-symlink-file");
        var target = temp.File(@"elsewhere\big.bin", 8 * ByteUnits.MB);
        var scanned = temp.Folder("scanned");
        try
        {
            File.CreateSymbolicLink(Path.Combine(scanned, "big.lnk.bin"), target);
        }
        catch (IOException e)
        {
            Assert.Inconclusive("symbolic links need developer mode or a privilege the standard user lacks: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Assert.Inconclusive("symbolic links need developer mode or a privilege the standard user lacks: " + e.Message);
        }

        var tree = await Scan(scanned);
        var link = Trees.Node(tree, "big.lnk.bin");
        Assert.IsTrue(link.Flags.HasFlag(NodeMarks.Link));
        Check.AtMost(ByteUnits.MB, link.AllocatedBytes, "a symbolic link costs its own size only");
        Check.AtMost(ByteUnits.MB, tree.Root.AllocatedBytes, "the target is not counted");
    }

    // docs/spec/01 walk rule 2: never follow symbolic links (to a folder: never entered).
    [TestMethod]
    public async Task SymbolicLinkToAFolderIsNeverEntered()
    {
        using var temp = new TempFolder("walk-symlink-folder");
        var target = temp.Folder("elsewhere");
        temp.File(@"elsewhere\big.bin", 8 * ByteUnits.MB);
        var scanned = temp.Folder("scanned");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(scanned, "jump"), target);
        }
        catch (IOException e)
        {
            Assert.Inconclusive("symbolic links need developer mode or a privilege the standard user lacks: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Assert.Inconclusive("symbolic links need developer mode or a privilege the standard user lacks: " + e.Message);
        }

        var tree = await Scan(scanned);
        var link = Trees.Node(tree, "jump");
        Assert.IsTrue(link.Flags.HasFlag(NodeMarks.Link));
        Assert.IsFalse(link.IsFolder);
        Assert.AreEqual(0, link.Children.Count);
        Check.AtMost(ByteUnits.MB, tree.Root.AllocatedBytes, "the target is not counted");
    }

    // docs/spec/01 walk rule 5 (Windows note 5): Windows has no packages; a folder named like one is an ordinary folder.
    [TestMethod]
    public async Task FolderNamedLikeAPackageIsAnOrdinaryFolder()
    {
        using var temp = new TempFolder("walk-package");
        temp.File(@"Thing.app\Contents\data.bin", OneHundredKb);
        var tree = await Scan(temp.Path);
        var folder = Trees.Node(tree, "Thing.app");
        Assert.IsTrue(folder.IsFolder);
        Assert.AreEqual(1, folder.Children.Count);
        Assert.IsNotNull(Trees.Find(tree.Root, @"Thing.app\Contents\data.bin"));
    }

    // docs/spec/01 walk rule 7: unreadable entries are counted, never treated as empty, never silently skipped.
    [TestMethod]
    public async Task UnreadableFolderIsCountedAndFlagged()
    {
        using var temp = new TempFolder("walk-unreadable");
        var locked = temp.Folder("locked");
        temp.File(@"locked\inside.bin", OneHundredKb);
        temp.File("readable.bin", OneHundredKb);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var tree = await Scan(temp.Path);
            var node = Trees.Node(tree, "locked");
            Assert.IsTrue(node.Flags.HasFlag(NodeMarks.Unreadable), "a folder that cannot be listed is flagged unreadable");
            Assert.AreEqual(1L, tree.Coverage.UnreadableCount);
            Assert.IsNotNull(Trees.Find(tree.Root, "readable.bin"), "the rest of the walk goes on");
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    // docs/spec/01 walk rule 7: a readable tree has no unreadable entries.
    [TestMethod]
    public async Task ReadableTreeHasNoUnreadableCount()
    {
        using var temp = new TempFolder("walk-readable");
        temp.File(@"a\b\c.bin", OneHundredKb);
        var tree = await Scan(temp.Path);
        Assert.AreEqual(0L, tree.Coverage.UnreadableCount);
        Assert.IsFalse(Trees.All(tree.Root).Any(n => n.Flags.HasFlag(NodeMarks.Unreadable)));
    }

    // docs/spec/01 walk rule 8: dates are signed minutes since 1970-01-01 UTC; a file dated before 1970 must not break anything.
    [TestMethod]
    public async Task DateBefore1970IsNegativeMinutes()
    {
        using var temp = new TempFolder("walk-date-1969");
        var file = temp.File("moon.bin", 4096);
        var moon = new DateTime(1969, 7, 20, 20, 17, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, moon);
        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "moon.bin");
        Assert.AreEqual(Trees.Minutes(moon), node.LastWriteMinutes);
        Check.True(node.LastWriteMinutes < 0, "1969 is before the epoch");
    }

    // docs/spec/01 walk rule 8: files dated 1970 or before 2001 exist and must not break anything.
    [TestMethod]
    public async Task DatesIn1970And1980AreExactMinutes()
    {
        using var temp = new TempFolder("walk-date-old");
        var epochFile = temp.File("epoch.bin", 4096);
        var oldFile = temp.File("dos.bin", 4096);
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dos = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(epochFile, epoch);
        File.SetLastWriteTimeUtc(oldFile, dos);
        var tree = await Scan(temp.Path);
        Assert.AreEqual(0L, Trees.Node(tree, "epoch.bin").LastWriteMinutes);
        Assert.AreEqual(Trees.Minutes(dos), Trees.Node(tree, "dos.bin").LastWriteMinutes);
    }

    // docs/spec/01 walk rule 8: a current date round-trips to the minute.
    [TestMethod]
    public async Task CurrentDateIsMinutesSince1970()
    {
        using var temp = new TempFolder("walk-date-now");
        var file = temp.File("now.bin", 4096);
        var written = File.GetLastWriteTimeUtc(file);
        var tree = await Scan(temp.Path);
        Assert.AreEqual(Trees.Minutes(written), Trees.Node(tree, "now.bin").LastWriteMinutes);
    }

    // docs/spec/01 Windows note (7): alternate data streams are not measured and go to "Not measured".
    [TestMethod]
    public async Task AlternateDataStreamIsNotMeasured()
    {
        using var temp = new TempFolder("walk-ads");
        var file = temp.File("main.txt", 4096);
        TempFolder.WriteBytes(file + ":hidden", 512 * ByteUnits.KB, 2);
        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "main.txt");
        Assert.AreEqual(4096L, node.LogicalBytes, "the stream's bytes are not in the file's size");
        Check.AtMost(ByteUnits.KB * 64, node.AllocatedBytes, "the stream's 512 KB are not in the allocated size");
    }

    // docs/spec/01 walk rules: a hidden file is marked (used by Duplicates' exclusions, docs/spec/03 Windows note).
    [TestMethod]
    public async Task HiddenAttributeIsRecorded()
    {
        using var temp = new TempFolder("walk-hidden");
        var file = temp.File("secret.bin", 4096);
        File.SetAttributes(file, FileAttributes.Hidden);
        var hiddenFolder = temp.Folder("shy");
        temp.File(@"shy\x.bin", 4096);
        File.SetAttributes(hiddenFolder, FileAttributes.Directory | FileAttributes.Hidden);
        var tree = await Scan(temp.Path);
        Assert.IsTrue(Trees.Node(tree, "secret.bin").Flags.HasFlag(NodeMarks.Hidden));
        Assert.IsTrue(Trees.Node(tree, "shy").Flags.HasFlag(NodeMarks.Hidden));
        Assert.IsFalse(Trees.Node(tree, @"shy\x.bin").Flags.HasFlag(NodeMarks.Hidden), "the flag is the entry's own attribute");
    }

    // docs/spec/01 (ScanNode contract): folder totals are the sums of their children; children largest first; FileCount.
    [TestMethod]
    public async Task FolderTotalsSumChildrenAndChildrenAreLargestFirst()
    {
        using var temp = new TempFolder("walk-totals");
        temp.File(@"a\small.bin", 4 * ByteUnits.KB);
        temp.File(@"a\large.bin", 400 * ByteUnits.KB);
        temp.File(@"a\medium.bin", 40 * ByteUnits.KB);
        temp.File("top.bin", 8 * ByteUnits.KB);
        var tree = await Scan(temp.Path);
        var a = Trees.Node(tree, "a");
        Assert.AreEqual(3L, a.FileCount);
        Assert.AreEqual(4L, tree.Root.FileCount);
        Assert.AreEqual(a.Children.Sum(c => c.AllocatedBytes), a.AllocatedBytes);
        Assert.AreEqual(a.Children.Sum(c => c.LogicalBytes), a.LogicalBytes);
        Assert.AreEqual(a.AllocatedBytes + Trees.Node(tree, "top.bin").AllocatedBytes, tree.Root.AllocatedBytes);
        Check.Sequence(a.Children.Select(c => c.Name).ToArray(), "large.bin", "medium.bin", "small.bin");
        Assert.AreEqual("a", tree.Root.Children[0].Name, "the folder (444 KB) sorts before top.bin (8 KB)");
    }

    // docs/spec/01 (ScanNode contract): the canonical full path of each node, and parents.
    [TestMethod]
    public async Task NodesCarryCanonicalFullPathsAndParents()
    {
        using var temp = new TempFolder("walk-paths");
        var file = temp.File(@"Docs\Report.bin", 4096);
        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, @"Docs\Report.bin");
        Assert.IsTrue(string.Equals(Path.GetFullPath(file), node.Path, StringComparison.OrdinalIgnoreCase), node.Path);
        Assert.IsTrue(Path.IsPathFullyQualified(node.Path));
        Assert.AreEqual("Report.bin", node.Name);
        Assert.IsNotNull(node.Parent);
        Assert.AreEqual("Docs", node.Parent.Name);
        Assert.IsNull(tree.Root.Parent);
        Assert.IsTrue(string.Equals(Path.GetFullPath(temp.Path), tree.Root.Path, StringComparison.OrdinalIgnoreCase), tree.Root.Path);
    }

    // docs/spec/01 Scanner contract: the walk never opens a file's data, so a file open without sharing is still measured.
    [TestMethod]
    public async Task FileOpenWithoutSharingIsStillMeasured()
    {
        using var temp = new TempFolder("walk-locked-file");
        var path = temp.File("busy.bin", OneHundredKb);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var tree = await Scan(temp.Path);
            var node = Trees.Node(tree, "busy.bin");
            Assert.AreEqual(OneHundredKb, node.LogicalBytes);
            Assert.IsFalse(node.Flags.HasFlag(NodeMarks.Unreadable), "sizes come from the folder batch, not from opening the file");
            Assert.AreEqual(0L, tree.Coverage.UnreadableCount);
        }
    }

    // docs/spec/01 Coverage: a scan of a folder measures exactly the sum of the files it found.
    [TestMethod]
    public async Task CoverageMeasuredEqualsTheRootTotal()
    {
        using var temp = new TempFolder("walk-coverage");
        temp.File(@"x\one.bin", OneHundredKb);
        temp.File("two.bin", 2 * OneHundredKb);
        var tree = await Scan(temp.Path);
        Assert.AreEqual(tree.Root.AllocatedBytes, tree.Coverage.MeasuredBytes);
        Assert.IsFalse(tree.Cancelled);
    }

    // Scanner contract, "a file gives a tree of that one node" (brief M26): one node, a file, readable, with its logical and
    // allocated sizes and its stamp (volume serial and file id as the file system reports them), no parent, nothing unreadable.
    [TestMethod]
    public async Task AFileAsTheRootIsAOneNodeTree()
    {
        using var temp = new TempFolder("walk-file-root");
        var file = temp.File("one.bin", 70_000);
        var identity = Native.IdentityOf(file);
        var allocated = Native.AllocationSizeOf(file);
        var lastWrite = File.GetLastWriteTimeUtc(file);

        var tree = await Scan(file);
        var root = tree.Root;
        Assert.AreEqual(1, Trees.All(root).Count(), "one node");
        Assert.IsFalse(root.IsFolder, "the root is the file, not a folder");
        Assert.IsFalse(root.Flags.HasFlag(NodeMarks.Unreadable), "the file is readable");
        Assert.IsNull(root.Parent);
        Assert.IsTrue(string.Equals(Path.GetFullPath(file), root.Path, StringComparison.OrdinalIgnoreCase), root.Path);
        Assert.AreEqual(70_000L, root.LogicalBytes);
        Assert.AreEqual(allocated, root.AllocatedBytes, "the file's own allocated size");
        Assert.AreEqual(1L, root.FileCount);
        Assert.AreEqual(Trees.Minutes(lastWrite), root.LastWriteMinutes);
        Assert.IsNotNull(root.Stamp, "the file's stamp");
        Assert.AreEqual(identity.VolumeSerial, root.Stamp.Value.VolumeSerial);
        Assert.AreEqual(identity.FileId, root.Stamp.Value.FileId);
        Assert.AreEqual(70_000L, root.Stamp.Value.Length);
        Assert.AreEqual(0L, tree.Coverage.UnreadableCount);
        Assert.AreEqual(allocated, tree.Coverage.MeasuredBytes);
    }

    // docs/spec/01 walk rule 7 (brief M27): a root that does not exist, or a folder the user cannot open, is counted
    // unreadable (never treated as empty), without an exception.
    [TestMethod]
    public async Task AMissingOrUnopenableRootIsCountedUnreadable()
    {
        using var temp = new TempFolder("walk-root-unreadable");
        var missing = temp.Sub("missing");
        var tree = await Scan(missing);
        Assert.IsTrue(tree.Root.Flags.HasFlag(NodeMarks.Unreadable), "a missing root is marked unreadable");
        Assert.AreEqual(1L, tree.Coverage.UnreadableCount, "a missing root");

        var locked = temp.Folder("locked");
        temp.File(@"locked\inside.bin", OneHundredKb);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var lockedTree = await Scan(locked);
            Assert.IsTrue(lockedTree.Root.Flags.HasFlag(NodeMarks.Unreadable), "a root folder the user cannot list is marked unreadable");
            Assert.AreEqual(1L, lockedTree.Coverage.UnreadableCount, "a root folder the user cannot list");
            Assert.AreEqual(0, lockedTree.Root.Children.Count, "nothing is listed inside it");
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    // docs/spec/01 (dates, hidden), plan "Walker" (stamp) (brief M28): the root node carries its own last-write minute, its
    // own hidden mark, and its own stamp (volume serial, file id, last write; no length for a folder).
    [TestMethod]
    public async Task TheRootNodeCarriesItsOwnFacts()
    {
        using var temp = new TempFolder("walk-root-facts");
        var plain = temp.Folder("plain");
        temp.File(@"plain\x.bin", 4096);
        var hidden = temp.Folder("hidden");
        temp.File(@"hidden\x.bin", 4096);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        var written = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(plain, written);
        Directory.SetLastWriteTimeUtc(hidden, written.AddDays(-40));

        foreach (var (folder, isHidden) in new[] { (plain, false), (hidden, true) })
        {
            var identity = Native.IdentityOf(folder);
            var lastWrite = Directory.GetLastWriteTimeUtc(folder);
            var tree = await Scan(folder);
            var root = tree.Root;
            Assert.IsTrue(root.IsFolder, folder);
            Assert.AreEqual(Trees.Minutes(lastWrite), root.LastWriteMinutes, "last-write minute of " + folder);
            Assert.AreEqual(isHidden, root.Flags.HasFlag(NodeMarks.Hidden), "hidden mark of " + folder);
            Assert.IsNotNull(root.Stamp, "stamp of " + folder);
            Assert.AreEqual(identity.VolumeSerial, root.Stamp.Value.VolumeSerial, "volume serial of " + folder);
            Assert.AreEqual(identity.FileId, root.Stamp.Value.FileId, "file id of " + folder);
            Assert.AreEqual(lastWrite.ToFileTimeUtc(), root.Stamp.Value.LastWriteTimeUtcTicks, "stamp last write of " + folder);
            Assert.IsNull(root.Stamp.Value.Length, "a folder's stamp has no length");
        }
    }

    // docs/spec/01 walk rule 2 (brief M29): a junction given as the root is a link: marked so, never entered, not unreadable,
    // its target not counted. Control: the target folder given as the root is walked.
    [TestMethod]
    public async Task AJunctionAsTheRootIsNeverEntered()
    {
        using var temp = new TempFolder("walk-junction-root");
        var target = temp.Folder("target");
        temp.File(@"target\big.bin", 8 * ByteUnits.MB);
        var jump = temp.Sub("jump");
        Native.CreateJunction(jump, target);

        var tree = await Scan(jump);
        Assert.IsTrue(tree.Root.Flags.HasFlag(NodeMarks.Link), "a junction root is marked as a link");
        Assert.AreEqual(0, tree.Root.Children.Count, "never entered");
        Assert.IsFalse(tree.Root.Flags.HasFlag(NodeMarks.Unreadable), "a link is not unreadable");
        Assert.AreEqual(0L, tree.Coverage.UnreadableCount);
        Check.AtMost(ByteUnits.MB, tree.Root.AllocatedBytes, "the target's 8 MB are not counted through the junction");

        var control = await Scan(target);
        Assert.IsTrue(control.Root.IsFolder);
        Assert.IsFalse(control.Root.Flags.HasFlag(NodeMarks.Link));
        Assert.IsNotNull(Trees.Find(control.Root, "big.bin"), "the plain folder is walked");
    }

    // docs/spec/01 walk rule 3; intent criterion 4 (per-file allocation as the oracle) (brief M30): every file of a folder
    // has its own allocated size, whatever its size and the length of its name.
    [TestMethod]
    public async Task EveryFileIsMeasuredAtItsOwnAllocatedSize()
    {
        using var temp = new TempFolder("walk-own-allocation");
        var files = new (string Name, long Length)[]
        {
            ("a.bin", 5_000),
            (new string('n', 120) + ".bin", 70_001),
            ("mid-length-name.dat", ByteUnits.MB + 3),
            ("x", 12_345),
            ("another file with spaces.txt", 200_000),
            ("b" + new string('q', 40), 1_500),
        };
        foreach (var (name, length) in files)
        {
            temp.File(name, length);
        }

        var tree = await Scan(temp.Path);
        long sum = 0;
        foreach (var (name, length) in files)
        {
            var node = Trees.Node(tree, name);
            var oracle = Native.AllocationSizeOf(temp.Sub(name));
            Assert.AreEqual(length, node.LogicalBytes, "length of " + name);
            Assert.AreEqual(oracle, node.AllocatedBytes, "allocated size of " + name);
            sum += oracle;
        }

        Assert.AreEqual(sum, tree.Root.AllocatedBytes, "the folder sums its files' own sizes");
    }

    // docs/spec/01 walk rule 8, "signed minutes since 1970" (brief M33): a time before 1970 floors to the earlier minute:
    // 1969-12-31 23:59:30 UTC is minute −1 (not 0), 1960-01-01 00:00:30 UTC is minute −5,260,320 (not −5,260,319).
    [TestMethod]
    public async Task PreEpochSecondsFloorToTheEarlierMinute()
    {
        using var temp = new TempFolder("walk-date-floor");
        var justBefore = temp.File("just-before.bin", 4096);
        var sixties = temp.File("sixties.bin", 4096);
        File.SetLastWriteTimeUtc(justBefore, new DateTime(1969, 12, 31, 23, 59, 30, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(sixties, new DateTime(1960, 1, 1, 0, 0, 30, DateTimeKind.Utc));
        var tree = await Scan(temp.Path);
        Assert.AreEqual(-1L, Trees.Node(tree, "just-before.bin").LastWriteMinutes);
        Assert.AreEqual(-5_260_320L, Trees.Node(tree, "sixties.bin").LastWriteMinutes);
    }
}
