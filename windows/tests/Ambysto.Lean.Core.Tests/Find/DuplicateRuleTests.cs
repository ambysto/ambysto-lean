using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03, "Duplicates", on real files: identity is byte-for-byte equal (a hash or a size is never proof;
/// a read error means "not identical"); candidates are regular files in the home-item root of at least 1 MB, excluding
/// AppData, hidden folders (attribute or leading dot), node_modules, cloud placeholders and hard links counted elsewhere;
/// suggested keep (newest; ties shortest path; then alphabetical) is a badge only; "May share storage" never on NTFS;
/// the reclaimable total is an estimate.</summary>
[TestClass]
public sealed class DuplicateRuleTests
{
    private const long TwoMb = 2 * ByteUnits.MB;

    private static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(ProfileFixture fx, DuplicateOptions? options = null)
    {
        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        return await DuplicateFinder.FindAsync(tree, options ?? DuplicateOptions.Default, fx.Context, CancellationToken.None);
    }

    private static string[] Names(DuplicateGroup group) => group.Copies.Select(c => c.Node.Name).Order(StringComparer.Ordinal).ToArray();

    // docs/spec/03 Duplicates: the minimum size is 1 MB by default.
    [TestMethod]
    public void DefaultMinimumIsOneMegabyte() => Assert.AreEqual(ByteUnits.MB, DuplicateOptions.Default.MinimumBytes);

    // docs/spec/03 Duplicates: byte-for-byte equal files form one group with every copy.
    [TestMethod]
    public async Task IdenticalFilesFormOneGroupWithEveryCopy()
    {
        using var fx = new ProfileFixture("dup-identical");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 7);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "Sub", "b.bin"), TwoMb, seed: 7);
        ProfileFixture.MakeFile(fx.InProfile("Pictures", "c.bin"), TwoMb, seed: 7);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "other.bin"), TwoMb, seed: 8);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Check.Sequence(Names(groups[0]), "a.bin", "b.bin", "c.bin");
        Assert.AreEqual(TwoMb, groups[0].LogicalBytes);
    }

    // docs/spec/03 Duplicates: a size is never proof; same first and last 4 KB with a different byte in the middle are not duplicates.
    [TestMethod]
    public async Task SameSizeAndSameEndsWithOneMiddleByteDifferentAreNotDuplicates()
    {
        using var fx = new ProfileFixture("dup-middle-byte");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 3);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 3);
        using (var stream = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = TwoMb / 2;
            var original = stream.ReadByte();
            stream.Position = TwoMb / 2;
            stream.WriteByte((byte)(original ^ 0x01));
        }

        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count, "a hash or a size is never proof on its own; every byte is compared");
    }

    // docs/spec/03 Duplicates: a difference in the last 4 KB is caught; a difference in the first 4 KB is caught.
    [TestMethod]
    public async Task DifferenceInTheFirstOrLastBlockIsNotADuplicate()
    {
        using var fx = new ProfileFixture("dup-ends");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 4);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 4);
        var c = ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), TwoMb, seed: 4);
        using (var stream = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = TwoMb - 1;
            stream.WriteByte(0xEE);
        }

        using (var stream = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 0;
            stream.WriteByte(0xEE);
        }

        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates: different lengths are never duplicates (step 1 groups by logical length).
    [TestMethod]
    public async Task DifferentLengthsAreNeverDuplicates()
    {
        using var fx = new ProfileFixture("dup-length");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 5);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb + 1, seed: 5);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates: a read error means "not identical".
    [TestMethod]
    public async Task ReadErrorMeansNotIdentical()
    {
        using var fx = new ProfileFixture("dup-read-error");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 6);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 6);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), TwoMb, seed: 6);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new FileInfo(b);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var groups = await FindAsync(fx);
            Assert.AreEqual(1, groups.Count, "the two readable copies still form a group");
            Check.Sequence(Names(groups[0]), "a.bin", "c.bin");
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    private const long ThreeMb = 3 * ByteUnits.MB;

    /// <summary>Holds a byte-range lock on the middle of <paramref name="path"/> (1 MB, 64 KB long) through a handle opened
    /// with FileShare.ReadWrite: the first and last 4 KB stay readable through any other handle, the middle does not
    /// (lock violation), so the file fails only in the full byte compare. Proved here against a second handle.</summary>
    private static FileStream LockMiddle(string path)
    {
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        holder.Lock(ByteUnits.MB, 64 * ByteUnits.KB);
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[4096];
        probe.Position = 0;
        Assert.AreEqual(4096, probe.Read(buffer, 0, 4096), "the first 4 KB are readable");
        probe.Position = ThreeMb - 4096;
        Assert.AreEqual(4096, probe.Read(buffer, 0, 4096), "the last 4 KB are readable");
        probe.Position = ByteUnits.MB;
        Assert.Throws<IOException>(() => probe.Read(buffer, 0, 4096), "a read of the locked middle through another handle must fail");
        return holder;
    }

    private static void Unlock(FileStream holder)
    {
        holder.Unlock(ByteUnits.MB, 64 * ByteUnits.KB);
        holder.Dispose();
    }

    // docs/spec/03 Duplicates: a read error means "not identical", in the full byte compare too (step 3): b's ends match
    // but its middle cannot be read, so b joins no group while a and c still form theirs.
    [TestMethod]
    public async Task ReadErrorDuringTheFullCompareMeansNotIdentical()
    {
        using var fx = new ProfileFixture("dup-lock-second");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), ThreeMb, seed: 29);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), ThreeMb, seed: 29);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), ThreeMb, seed: 29);
        var holder = LockMiddle(b);
        try
        {
            var groups = await FindAsync(fx);
            Assert.AreEqual(1, groups.Count, "a and c form one group; b (unreadable in the middle) joins none");
            Check.Sequence(Names(groups[0]), "a.bin", "c.bin");
        }
        finally
        {
            Unlock(holder);
        }
    }

    // docs/spec/03 Duplicates: a read error means "not identical" when it strikes the first file of a comparison (the
    // reference, the name that sorts first): a joins no group; b and c still form theirs.
    [TestMethod]
    public async Task ReadErrorOnTheFirstFileOfTheCompareMeansNotIdentical()
    {
        using var fx = new ProfileFixture("dup-lock-first");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), ThreeMb, seed: 30);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), ThreeMb, seed: 30);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), ThreeMb, seed: 30);
        var holder = LockMiddle(a);
        try
        {
            var groups = await FindAsync(fx);
            Assert.AreEqual(1, groups.Count, "b and c form one group; a (unreadable in the middle) joins none");
            Check.Sequence(Names(groups[0]), "b.bin", "c.bin");
        }
        finally
        {
            Unlock(holder);
        }
    }

    // docs/spec/03 Duplicates: at least 1 MB (inclusive), adjustable.
    [TestMethod]
    public async Task MinimumSizeIsInclusiveAndAdjustable()
    {
        using var fx = new ProfileFixture("dup-minimum");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "exact1.bin"), ByteUnits.MB, seed: 9);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "exact2.bin"), ByteUnits.MB, seed: 9);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small1.bin"), ByteUnits.MB - 1, seed: 10);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small2.bin"), ByteUnits.MB - 1, seed: 10);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Check.Sequence(Names(groups[0]), "exact1.bin", "exact2.bin");

        var lowered = await FindAsync(fx, new DuplicateOptions(512 * ByteUnits.KB));
        Assert.AreEqual(2, lowered.Count);
    }

    // docs/spec/03 Duplicates: candidates are files in the home-item root (docs/spec/02 (a): at least 2 components below
    // the profile); a copy directly in the profile is not a candidate.
    [TestMethod]
    public async Task CopyDirectlyInTheProfileIsNotACandidate()
    {
        using var fx = new ProfileFixture("dup-profile-root");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 11);
        ProfileFixture.MakeFile(fx.InProfile("a-copy.bin"), TwoMb, seed: 11);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates, Windows note: anything inside AppData is excluded.
    [TestMethod]
    public async Task CopiesInsideAppDataAreExcluded()
    {
        using var fx = new ProfileFixture("dup-appdata");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 12);
        ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Contoso", "a.bin"), TwoMb, seed: 12);
        ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Temp", "a.bin"), TwoMb, seed: 12);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count, "the only other copies are in AppData (Temp included: the duplicates rule, not the allowed-root exception)");
    }

    // docs/spec/03 Duplicates: hidden folders and their contents (names starting with '.', e.g. .git) are excluded.
    [TestMethod]
    public async Task CopiesInsideLeadingDotFoldersAreExcluded()
    {
        using var fx = new ProfileFixture("dup-dot");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 13);
        ProfileFixture.MakeFile(fx.InProfile("Documents", ".git", "objects", "a.bin"), TwoMb, seed: 13);
        ProfileFixture.MakeFile(fx.InProfile(".cache", "a.bin"), TwoMb, seed: 13);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates, Windows note: also folders with the hidden attribute.
    [TestMethod]
    public async Task CopiesInsideHiddenAttributeFoldersAreExcluded()
    {
        using var fx = new ProfileFixture("dup-hidden-attr");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 14);
        var hidden = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Stash"));
        ProfileFixture.MakeFile(Path.Combine(hidden, "deeper", "a.bin"), TwoMb, seed: 14);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates: node_modules is excluded.
    [TestMethod]
    public async Task CopiesInsideNodeModulesAreExcluded()
    {
        using var fx = new ProfileFixture("dup-node-modules");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "lib.bin"), TwoMb, seed: 15);
        ProfileFixture.MakeFile(fx.InProfile("Projects", "app", "node_modules", "lib", "lib.bin"), TwoMb, seed: 15);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates: hard links counted elsewhere (two links to one file are not duplicates).
    [TestMethod]
    public async Task HardLinksOfOneFileAreNotDuplicates()
    {
        using var fx = new ProfileFixture("dup-hardlink");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 16);
        Native.CreateHardLink(fx.InProfile("Documents", "b.bin"), a);
        Native.CreateHardLink(Path.Combine(ProfileFixture.MakeFolder(fx.InProfile("Pictures")), "c.bin"), a);
        var groups = await FindAsync(fx);
        Assert.AreEqual(0, groups.Count);
    }

    // docs/spec/03 Duplicates: two real copies plus a hard link of one of them: the group holds the two files, not the link.
    [TestMethod]
    public async Task HardLinkIsLeftOutOfAGroupOfRealCopies()
    {
        using var fx = new ProfileFixture("dup-hardlink-mixed");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 17);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 17);
        Native.CreateHardLink(fx.InProfile("Documents", "z-link.bin"), a);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Check.Sequence(Names(groups[0]), "a.bin", "b.bin");
    }

    // docs/spec/03 Duplicates: cloud placeholders are excluded: a node the walk flagged as a cloud file is in no group,
    // even when the bytes behind it would match (an in-memory tree over real identical files; no provider needed).
    [TestMethod]
    public async Task NodeFlaggedCloudNotDownloadedIsInNoGroup()
    {
        using var fx = new ProfileFixture("dup-cloud-flag");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 28);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 28);
        var c = ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), TwoMb, seed: 28);
        var minutes = Trees.Minutes(File.GetLastWriteTimeUtc(a));
        var root = ScanNode.Folder(fx.Profile)
            .Add(ScanNode.Folder(fx.Documents)
                .Add(Trees.File(a, TwoMb, minutes))
                .Add(Trees.File(b, TwoMb, minutes))
                .Add(ScanNode.File(c, TwoMb, 0, minutes, null, NodeMarks.CloudNotDownloaded)));
        var tree = Trees.Tree(root, 2 * TwoMb);
        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(1, groups.Count);
        Check.Sequence(Names(groups[0]), "a.bin", "b.bin");
    }

    // docs/spec/03 Duplicates: suggested keep: newest modification.
    [TestMethod]
    public async Task SuggestedKeepIsTheNewestCopy()
    {
        using var fx = new ProfileFixture("dup-keep-newest");
        var older = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 18);
        var newest = ProfileFixture.MakeFile(fx.InProfile("Documents", "longer-name-but-newer.bin"), TwoMb, seed: 18);
        File.SetLastWriteTimeUtc(older, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("longer-name-but-newer.bin", groups[0].SuggestedKeep.Node.Name);
        Assert.IsTrue(groups[0].Copies.Contains(groups[0].SuggestedKeep), "the badge names one of the copies");
    }

    // docs/spec/03 Duplicates: suggested keep: ties → shortest path.
    [TestMethod]
    public async Task SuggestedKeepTieBreaksOnShortestPath()
    {
        using var fx = new ProfileFixture("dup-keep-shortest");
        var stamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var shallow = ProfileFixture.MakeFile(fx.InProfile("Documents", "z.bin"), TwoMb, seed: 19);
        var deep = ProfileFixture.MakeFile(fx.InProfile("Documents", "Deep", "Deeper", "a.bin"), TwoMb, seed: 19);
        File.SetLastWriteTimeUtc(shallow, stamp);
        File.SetLastWriteTimeUtc(deep, stamp);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("z.bin", groups[0].SuggestedKeep.Node.Name);
    }

    // docs/spec/03 Duplicates: suggested keep: then alphabetical.
    [TestMethod]
    public async Task SuggestedKeepTieBreaksAlphabeticallyLast()
    {
        using var fx = new ProfileFixture("dup-keep-alpha");
        var stamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 20);
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 20);
        File.SetLastWriteTimeUtc(a, stamp);
        File.SetLastWriteTimeUtc(b, stamp);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("a.bin", groups[0].SuggestedKeep.Node.Name);
    }

    // docs/spec/03 Duplicates: largest first; each copy's size and last used come with it.
    [TestMethod]
    public async Task GroupsAreLargestFirstAndCopiesCarrySizeAndDate()
    {
        using var fx = new ProfileFixture("dup-order");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small1.bin"), TwoMb, seed: 21);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small2.bin"), TwoMb, seed: 21);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "big1.bin"), 3 * ByteUnits.MB, seed: 22);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "big2.bin"), 3 * ByteUnits.MB, seed: 22);
        var groups = await FindAsync(fx);
        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual(3 * ByteUnits.MB, groups[0].LogicalBytes);
        Assert.AreEqual(TwoMb, groups[1].LogicalBytes);
        foreach (var copy in groups.SelectMany(g => g.Copies))
        {
            Assert.AreEqual(Trees.Minutes(File.GetLastWriteTimeUtc(copy.Node.Path)), copy.Node.LastWriteMinutes);
        }
    }

    // docs/spec/03 Duplicates, Windows note: a volume that does not report block reference counting has no shared
    // blocks; NTFS does not report it (measured 2026-10-16), so "May share storage" is never set here.
    [TestMethod]
    public async Task MayShareStorageIsNeverSetOnNtfs()
    {
        using var fx = new ProfileFixture("dup-share");
        if (!string.Equals(new DriveInfo(Path.GetPathRoot(fx.Profile)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("the scratch volume is not NTFS");
        }

        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 23);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 23);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Assert.IsFalse(groups[0].Copies.Any(c => c.MayShareStorage));
    }

    // docs/spec/03 Duplicates: the reclaimable total is an estimate: what the copies other than the kept one occupy.
    [TestMethod]
    public async Task ReclaimableEstimateIsTheNonKeptCopies()
    {
        using var fx = new ProfileFixture("dup-reclaimable");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 24);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 24);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), TwoMb, seed: 24);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        var group = groups[0];
        var expected = group.Copies.Where(c => c != group.SuggestedKeep).Sum(c => c.Node.AllocatedBytes);
        Assert.AreEqual(expected, group.ReclaimableEstimateBytes);
        Check.AtLeast(2 * TwoMb, group.ReclaimableEstimateBytes, "two 2 MB copies could go");
    }

    // docs/spec/03 Duplicates: the inside of packages is excluded on macOS; Windows has none, so a folder named *.app is searched.
    [TestMethod]
    public async Task FolderNamedLikeAPackageIsSearched()
    {
        using var fx = new ProfileFixture("dup-package-name");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 25);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "Tool.app", "Contents", "a.bin"), TwoMb, seed: 25);
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
    }

    // docs/spec/03 Duplicates: cancel stops the search (a cancelled token gives no result, not a wrong one).
    [TestMethod]
    public async Task CancelledSearchDoesNotReportGroups()
    {
        using var fx = new ProfileFixture("dup-cancel");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 26);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 26);
        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, cts.Token);
            Assert.AreEqual(0, groups.Count, "a cancelled search reports nothing");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable: the cancel surfaces as the usual exception.
        }
    }

    // Gap brief H15. docs/spec/03 "Identity rule: byte-for-byte equal. A hash or a size is never proof"; the finder's
    // contract: a file whose length or identity changed since the scan is "not identical". Three identical copies are
    // scanned; then B grows by one byte (the same file, now longer: its first 2 MB still equal A's) and/or C is replaced by
    // a new file with the same length and the same bytes (a new file id). The search on the scanned tree puts neither in
    // a group. Control: on the same tree before the change, the three copies form one group.
    [TestMethod]
    [DataRow("grown")]
    [DataRow("replaced")]
    [DataRow("both")]
    public async Task CopyChangedSinceTheScanJoinsNoGroup(string change)
    {
        using var fx = new ProfileFixture("dup-changed-" + change);
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 41);
        var b = ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 41);
        var c = ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"), TwoMb, seed: 41);
        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);

        var before = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(1, before.Count, "control: the unchanged copies form one group");
        Check.Sequence(Names(before[0]), "a.bin", "b.bin", "c.bin");

        var grow = change is "grown" or "both";
        var replace = change is "replaced" or "both";
        if (grow)
        {
            using var stream = new FileStream(b, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(0x5A);
        }

        if (replace)
        {
            var oldId = Native.IdentityOf(c);
            var fresh = fx.InProfile("Documents", "c.new");
            TempFolder.WriteBytes(fresh, TwoMb, 41);
            File.Move(fresh, c, overwrite: true);
            Assert.AreNotEqual(oldId, Native.IdentityOf(c), "the replacement is a new file");
            Assert.AreEqual(TwoMb, new FileInfo(c).Length);
        }

        var after = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        var grouped = after.SelectMany(g => g.Copies).Select(copy => copy.Node.Path).ToList();
        if (grow)
        {
            Check.False(grouped.Contains(b, StringComparer.OrdinalIgnoreCase), "b.bin grew since the scan and still joined a group");
        }

        if (replace)
        {
            Check.False(grouped.Contains(c, StringComparer.OrdinalIgnoreCase), "c.bin is another file than the scanned one and still joined a group");
        }

        if (change == "both")
        {
            Assert.AreEqual(0, after.Count, "a.bin alone forms no group");
        }
        else
        {
            Assert.AreEqual(1, after.Count, "the two unchanged copies still form a group");
            string[] unchanged = grow ? ["a.bin", "c.bin"] : ["a.bin", "b.bin"];
            Check.Sequence(Names(after[0]), unchanged);
        }

        Assert.IsTrue(File.Exists(a), "a.bin is untouched");
    }

    // Gap brief H16. docs/spec/03 steps 2-3: the first and last 4 KB, then every byte in 1 MB chunks: identical files of
    // any length are grouped, a length over 1 MB that is not a multiple of it, and (minimum lowered to 1 KB) one shorter
    // than the 4 KB blocks. Control: a 3,000-byte pair differing in its last byte forms none.
    [TestMethod]
    public async Task IdenticalFilesOfAnyLengthAreGrouped()
    {
        const long twoAndAHalfMb = (5 * ByteUnits.MB) / 2;
        const long threeThousand = 3_000;
        using var fx = new ProfileFixture("dup-any-length");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "big-a.bin"), twoAndAHalfMb, seed: 51);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "big-b.bin"), twoAndAHalfMb, seed: 51);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small-a.bin"), threeThousand, seed: 52);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "small-b.bin"), threeThousand, seed: 52);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "other-a.bin"), threeThousand, seed: 53);
        var otherB = ProfileFixture.MakeFile(fx.InProfile("Documents", "other-b.bin"), threeThousand, seed: 53);
        using (var stream = new FileStream(otherB, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = threeThousand - 1;
            var last = stream.ReadByte();
            stream.Position = threeThousand - 1;
            stream.WriteByte((byte)(last ^ 0x01));
        }

        var byDefault = await FindAsync(fx);
        Assert.AreEqual(1, byDefault.Count, "with the default 1 MB minimum, only the 2.5 MB pair is a candidate");
        Check.Sequence(Names(byDefault[0]), "big-a.bin", "big-b.bin");
        Assert.AreEqual(twoAndAHalfMb, byDefault[0].LogicalBytes);

        var lowered = await FindAsync(fx, new DuplicateOptions(ByteUnits.KB));
        Assert.AreEqual(2, lowered.Count, "the 2.5 MB pair and the 3,000-byte pair; the pair that differs in its last byte forms none");
        Check.Sequence(Names(lowered[0]), "big-a.bin", "big-b.bin");
        Check.Sequence(Names(lowered[1]), "small-a.bin", "small-b.bin");
        Assert.AreEqual(threeThousand, lowered[1].LogicalBytes);
    }

    /// <summary>A file node for a real file with the stamp the walk would read (volume serial, file id, last write as a
    /// FILETIME, length) and no mark.</summary>
    private static ScanNode StampedFile(string path)
    {
        var identity = Native.IdentityOf(path);
        var length = new FileInfo(path).Length;
        var lastWrite = File.GetLastWriteTimeUtc(path);
        return ScanNode.File(path, length, length, Trees.Minutes(lastWrite), new ItemStamp(identity.VolumeSerial, identity.FileId, lastWrite.ToFileTimeUtc(), length));
    }

    // docs/spec/03 Duplicates: "two links to one file are not duplicates". A built tree holds both names of one real
    // hard-linked file without the walker's "counted elsewhere" mark (both carry the file's stamp) and a real third copy:
    // the group holds the third copy and only the name of the linked file that sorts first (brief M06). Two sets, so the
    // first-sorting name is once the original and once the link.
    [TestMethod]
    public async Task TwoNamesOfOneFileAreNeverBothInAGroup()
    {
        using var fx = new ProfileFixture("dup-two-names");
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 41);
        var zLink = fx.InProfile("Documents", "z-link.bin");
        Native.CreateHardLink(zLink, a);
        var m = ProfileFixture.MakeFile(fx.InProfile("Documents", "m.bin"), TwoMb, seed: 41);
        var y = ProfileFixture.MakeFile(fx.InProfile("Documents", "y.dat"), ThreeMb, seed: 42);
        var bLink = fx.InProfile("Documents", "b-link.dat");
        Native.CreateHardLink(bLink, y);
        var k = ProfileFixture.MakeFile(fx.InProfile("Documents", "k.dat"), ThreeMb, seed: 42);
        Assert.AreEqual(Native.IdentityOf(a), Native.IdentityOf(zLink), "precondition: one file, two names");

        var docs = ScanNode.Folder(fx.Documents);
        foreach (var path in new[] { a, zLink, m, y, bLink, k })
        {
            docs.Add(StampedFile(path));
        }

        var tree = Trees.Tree(ScanNode.Folder(fx.Profile).Add(docs), (2 * TwoMb) + (2 * ThreeMb));
        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(2, groups.Count, string.Join(" | ", groups.Select(g => string.Join(", ", Names(g)))));
        Check.Sequence(Names(groups.Single(g => g.LogicalBytes == ThreeMb)), "b-link.dat", "k.dat");
        Check.Sequence(Names(groups.Single(g => g.LogicalBytes == TwoMb)), "a.bin", "m.bin");
    }

    private const uint FileSupportsBlockRefcounting = 0x08000000;

    // docs/spec/03 Duplicates, Windows note: on a volume that reports block reference counting (ReFS, Dev Drives), copies
    // may share storage, and each is labelled so (brief M07; NTFS, which reports none: MayShareStorageIsNeverSetOnNtfs).
    // Needs such a volume on the test machine (an owner/admin fixture); Inconclusive without one.
    [TestMethod]
    public async Task CopiesOnABlockCloningVolumeMayShareStorage()
    {
        var volume = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .FirstOrDefault(r => Native.VolumeFlagsOf(r) is { } flags && (flags & FileSupportsBlockRefcounting) != 0);
        if (volume is null)
        {
            Assert.Inconclusive("no fixed volume on this machine reports block reference counting (a Dev Drive or ReFS volume is an owner/admin fixture)");
            return;
        }

        var scratch = Path.Combine(volume, "LeanCoreTests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var profile = Directory.CreateDirectory(Path.Combine(scratch, "Users", "me")).FullName;
            var roaming = Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming")).FullName;
            var local = Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local")).FullName;
            var context = new Ambysto.Lean.Core.Paths.PathContext(profile, roaming, local, scratch, Path.Combine(scratch, "Users"), [Path.Combine(scratch, "Windows")], []);
            ProfileFixture.MakeFile(Path.Combine(profile, "Documents", "a.bin"), TwoMb, seed: 43);
            ProfileFixture.MakeFile(Path.Combine(profile, "Documents", "b.bin"), TwoMb, seed: 43);
            var tree = await new Scanner().ScanAsync(profile, null, CancellationToken.None);
            var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, context, CancellationToken.None);
            Assert.AreEqual(1, groups.Count);
            Check.True(groups[0].Copies.All(c => c.MayShareStorage), "every copy on " + volume + " is labelled \"May share storage\"");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    // docs/spec/03 Duplicates: suggested keep "ties → shortest path; then alphabetical": same modification time and same
    // path length, ...\a\x.bin and ...\B\x.bin: the badge is on ...\a\x.bin, alphabetical ignoring case (an ordinal order
    // would pick B; brief M09).
    [TestMethod]
    public async Task SuggestedKeepTieIsAlphabeticalIgnoringCase()
    {
        using var fx = new ProfileFixture("dup-keep-case");
        var stamp = new DateTime(2025, 3, 3, 3, 3, 0, DateTimeKind.Utc);
        var upper = ProfileFixture.MakeFile(fx.InProfile("Documents", "B", "x.bin"), TwoMb, seed: 44);
        var lower = ProfileFixture.MakeFile(fx.InProfile("Documents", "a", "x.bin"), TwoMb, seed: 44);
        File.SetLastWriteTimeUtc(upper, stamp);
        File.SetLastWriteTimeUtc(lower, stamp);
        Assert.AreEqual(upper.Length, lower.Length, "precondition: same path length");
        var groups = await FindAsync(fx);
        Assert.AreEqual(1, groups.Count);
        Assert.IsTrue(string.Equals(lower, groups[0].SuggestedKeep.Node.Path, StringComparison.OrdinalIgnoreCase), "suggested keep: " + groups[0].SuggestedKeep.Node.Path);
    }
}
