using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>Independent representation-change contract: full identity, exact eligibility, stable first probe and cancellation.
/// Synthetic agreeing entries require no file opens; the failed-first-probe case owns its real NTFS fixture.</summary>
[TestClass]
public sealed class HardLinkIndexContractTests
{
    private static ScanNode Listed(string path, UInt128 id, long bytes = 8192, NodeMarks flags = NodeMarks.None) =>
        ScanNode.File(path, bytes, bytes, 123, new ItemStamp(17, id, 123456, bytes), flags);

    [TestMethod]
    public void EveryByteOfThe128BitIdSeparatesIndependentHardLinkGroupsWithoutReorderingTheInput()
    {
        var owners = new List<ScanNode>();
        var duplicates = new List<ScanNode>();
        for (var i = 0; i < 16; i++)
        {
            // Low-half groups collide in high64; high-half groups collide in low64. Every byte is significant.
            var id = (UInt128)1 << (i * 8);
            owners.Add(Listed(@"C:\Synthetic\B-owner-" + i + ".bin", id, (i + 1) * 4096));
            duplicates.Add(Listed(@"C:\Synthetic\a-duplicate-" + i + ".bin", id, (i + 1) * 4096));
        }
        var nodes = new List<ScanNode?>();
        for (var i = 15; i >= 0; i--) nodes.Add(duplicates[i]);
        for (var i = 0; i < 16; i++) nodes.Add(owners[i]);
        var originalOrder = nodes.ToArray();
        var stamps = owners.Concat(duplicates).Select(n => n.Stamp).ToArray();
        var result = HardLinks.Resolve(nodes, fileCount: 0, CancellationToken.None); // fileCount is a capacity hint, never a truncation limit
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, result.FilesOpened, "every true group agrees; nothing is reread");
        CollectionAssert.AreEqual(originalOrder, nodes.ToArray(), "an index representation must not reorder the caller's node table");
        for (var i = 0; i < 16; i++)
        {
            Assert.AreEqual((i + 1) * 4096L, owners[i].AllocatedBytes, "full-ID independent owner " + i);
            Assert.AreEqual((i + 1) * 4096L, owners[i].LogicalBytes);
            Assert.AreEqual(NodeMarks.None, owners[i].Flags, "ordinal B precedes lowercase a even though owner appeared later");
            Assert.AreEqual(0L, duplicates[i].AllocatedBytes);
            Assert.AreEqual(0L, duplicates[i].LogicalBytes);
            Assert.AreEqual(NodeMarks.HardLinkCountedElsewhere, duplicates[i].Flags);
        }
        CollectionAssert.AreEqual(stamps, owners.Concat(duplicates).Select(n => n.Stamp).ToArray(), "agreeing metadata stays untouched");
    }

    [TestMethod]
    public void OnlyPlainStampedNonzeroIdFilesParticipateWhileHiddenFilesRemainEligible()
    {
        const long bytes = 8192;
        var invalid = new[]
        {
            Listed(@"C:\Synthetic\0-link", 41, flags: NodeMarks.Link),
            Listed(@"C:\Synthetic\0-cloud", 41, flags: NodeMarks.CloudNotDownloaded),
            Listed(@"C:\Synthetic\0-unreadable", 41, flags: NodeMarks.Unreadable),
            ScanNode.Folder(@"C:\Synthetic\0-folder", stamp: new ItemStamp(17, 41, 123456, null)),
            ScanNode.File(@"C:\Synthetic\0-missing-a", bytes, bytes, 123),
            ScanNode.File(@"C:\Synthetic\0-missing-b", bytes, bytes, 123),
            Listed(@"C:\Synthetic\0-zero-a", 0),
            Listed(@"C:\Synthetic\0-zero-b", 0),
        };
        var snapshots = invalid.Select(n => (n.AllocatedBytes, n.LogicalBytes, n.LastWriteMinutes, n.Stamp, n.Flags)).ToArray();
        var owner = Listed(@"C:\Synthetic\B-owner", 41, flags: NodeMarks.Hidden);
        var duplicate = Listed(@"C:\Synthetic\a-duplicate", 41);
        var nodes = invalid.Cast<ScanNode?>().ToList();
        nodes.Insert(0, duplicate);
        nodes.Add(owner);
        var result = HardLinks.Resolve(nodes, nodes.Count, CancellationToken.None);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, result.FilesOpened, "excluded disagreements cannot trigger a metadata open");
        CollectionAssert.AreEqual(snapshots, invalid.Select(n => (n.AllocatedBytes, n.LogicalBytes, n.LastWriteMinutes, n.Stamp, n.Flags)).ToArray(),
            "folders, links, cloud, unreadable, absent stamps and zero IDs are not changed by deduplication");
        Assert.AreEqual(bytes, owner.AllocatedBytes);
        Assert.AreEqual(bytes, owner.LogicalBytes);
        Assert.AreEqual(NodeMarks.Hidden, owner.Flags, "Hidden is not an exclusion flag");
        Assert.AreEqual(0L, duplicate.AllocatedBytes);
        Assert.AreEqual(NodeMarks.HardLinkCountedElsewhere, duplicate.Flags);
    }

    [TestMethod]
    public void ADisagreeingGroupsFirstListedProbeRemainsFirstEvenWhenAnotherNameStillExists()
    {
        using var temp = new TempFolder("hardlink-stable-first-probe");
        var ownerPath = temp.File("B-owner.bin", 32768, seed: 92);
        var firstListedPath = temp.Sub("a-first-listed.bin");
        Native.CreateHardLink(firstListedPath, ownerPath);
        var id = Native.IdentityOf(ownerPath);
        var stamp = new ItemStamp(id.VolumeSerial, id.FileId, 123456, 4096);
        var owner = ScanNode.File(ownerPath, 4096, 4096, 123, stamp);
        var firstListed = ScanNode.File(firstListedPath, 8192, 8192, 124, stamp with { Length = 8192 });
        File.Delete(firstListedPath);
        Assert.IsFalse(File.Exists(firstListedPath));
        Assert.AreEqual(32768L, new FileInfo(ownerPath).Length, "an owner-first reread would observably replace the listed size");
        var content = File.ReadAllBytes(ownerPath);
        var result = HardLinks.Resolve(new List<ScanNode?> { firstListed, owner }, 2, CancellationToken.None);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, result.FilesOpened, "the original group's first name is the one failed probe; no fallback probe is invented");
        Assert.AreEqual(4096L, owner.AllocatedBytes);
        Assert.AreEqual(4096L, owner.LogicalBytes);
        Assert.AreEqual(stamp, owner.Stamp!.Value, "failed probe keeps the stale listed stamp for the gate to reject later");
        Assert.AreEqual(123L, owner.LastWriteMinutes);
        Assert.AreEqual(NodeMarks.None, owner.Flags);
        Assert.AreEqual(0L, firstListed.AllocatedBytes);
        Assert.AreEqual(0L, firstListed.LogicalBytes);
        Assert.AreEqual(stamp with { Length = 8192 }, firstListed.Stamp!.Value);
        Assert.AreEqual(NodeMarks.HardLinkCountedElsewhere, firstListed.Flags);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(ownerPath));
    }

    [TestMethod]
    public void PreCancelledResolutionReturnsIncompleteWithoutChangingAnyNodeOrInputOrder()
    {
        var nodes = new List<ScanNode?> { Listed(@"C:\Synthetic\z", 71), Listed(@"C:\Synthetic\B", 71) };
        var originalOrder = nodes.ToArray();
        var snapshots = nodes.Select(n => (n!.AllocatedBytes, n.LogicalBytes, n.LastWriteMinutes, n.Stamp, n.Flags)).ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = HardLinks.Resolve(nodes, 2, cancellation.Token);
        Assert.IsFalse(result.Completed);
        Assert.AreEqual(0, result.FilesOpened);
        CollectionAssert.AreEqual(originalOrder, nodes.ToArray());
        CollectionAssert.AreEqual(snapshots, nodes.Select(n => (n!.AllocatedBytes, n.LogicalBytes, n.LastWriteMinutes, n.Stamp, n.Flags)).ToArray());
    }
}
