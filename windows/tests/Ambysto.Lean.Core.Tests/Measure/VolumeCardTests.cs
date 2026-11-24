using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01, "Volume card", Windows notes: used = total − free; free = GetDiskFreeSpaceEx free bytes
/// available to the caller; the Recycle Bin in logical bytes for this user. Compared with the same Windows call made
/// by the test, which is what <c>fsutil volume diskfree</c> reports too (intent criterion 4: within 0.5 GB).</summary>
[TestClass]
public sealed class VolumeCardTests
{
    private const long HalfGigabyte = ByteUnits.GB / 2;

    private static string ProfileVolumeRoot() => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;

    // docs/spec/01 Volume card, "Used by this volume", Windows note: total − free of the volume.
    [TestMethod]
    public void UsedIsExactlyTotalMinusFree()
    {
        var card = VolumeCard.Read(ProfileVolumeRoot());
        Assert.AreEqual(card.TotalBytes - card.FreeBytes, card.UsedBytes);
    }

    // docs/spec/01 Volume card, "Free right now", Windows note: GetDiskFreeSpaceEx free bytes available to caller.
    [TestMethod]
    public void FreeMatchesGetDiskFreeSpaceExForThisUser()
    {
        var root = ProfileVolumeRoot();
        var before = Native.DiskSpaceOf(root);
        var card = VolumeCard.Read(root);
        var after = Native.DiskSpaceOf(root);
        var low = Math.Min(before.FreeBytesAvailable, after.FreeBytesAvailable) - HalfGigabyte;
        var high = Math.Max(before.FreeBytesAvailable, after.FreeBytesAvailable) + HalfGigabyte;
        Check.True(card.FreeBytes >= low && card.FreeBytes <= high, $"free {card.FreeBytes} not within 0.5 GB of GetDiskFreeSpaceEx ({before.FreeBytesAvailable}..{after.FreeBytesAvailable})");
    }

    // docs/spec/01 Volume card, Windows note: the card shows the volume that holds the user's profile; total is the volume's.
    [TestMethod]
    public void TotalIsTheVolumesTotal()
    {
        var root = ProfileVolumeRoot();
        var card = VolumeCard.Read(root);
        Assert.AreEqual(Native.DiskSpaceOf(root).TotalBytes, card.TotalBytes);
        Assert.AreEqual(root, card.VolumeRoot);
    }

    // docs/spec/01 Volume card, Windows note: used within 0.5 GB of what fsutil volume diskfree reports (criterion 4).
    [TestMethod]
    public void UsedIsWithinHalfAGigabyteOfTheVolumesOwnNumbers()
    {
        var root = ProfileVolumeRoot();
        var space = Native.DiskSpaceOf(root);
        var card = VolumeCard.Read(root);
        Check.Near(space.TotalBytes - space.FreeBytesAvailable, card.UsedBytes, HalfGigabyte, "used");
    }

    // docs/spec/01 Volume card, "In the Trash", Windows note: this user's Recycle Bin on the profile's volume, logical sizes.
    [TestMethod]
    public void RecycleBinNumbersAreNonNegativeAndConsistent()
    {
        var card = VolumeCard.Read(ProfileVolumeRoot());
        Assert.IsNotNull(card.RecycleBinBytes, "the test machine's Recycle Bin on the profile's volume is readable");
        Assert.IsNotNull(card.RecycleBinItems, "the test machine's Recycle Bin on the profile's volume is readable");
        Check.AtLeast(0, card.RecycleBinBytes.Value, "Recycle Bin bytes");
        Check.AtLeast(0, card.RecycleBinItems.Value, "Recycle Bin items");
        if (card.RecycleBinItems == 0)
        {
            Assert.AreEqual(0L, card.RecycleBinBytes, "an empty Recycle Bin has no size");
        }
    }

    // docs/spec/01 Volume card, Windows note: the numbers are SHQueryRecycleBin's for this user's bin, read here
    // independently (the natural x64 layout of SHQUERYRBINFO, 24 bytes). A failed query never shows as 0: it is null.
    [TestMethod]
    public void RecycleBinNumbersEqualTheShellsOwnAnswer()
    {
        var root = ProfileVolumeRoot();
        var (hr, bytes, items) = Native.QueryRecycleBin(root);
        Assert.AreEqual(0, hr, "precondition: the shell reads this user's Recycle Bin on " + root);
        var card = VolumeCard.Read(root);
        Assert.AreEqual(bytes, card.RecycleBinBytes, "Recycle Bin bytes");
        Assert.AreEqual(items, card.RecycleBinItems, "Recycle Bin items");
    }

    // docs/spec/01 Volume card: shown at launch, before any scan; reading it changes nothing on the volume.
    [TestMethod]
    [DoNotParallelize] // a timing check: not measured while other tests load the machine
    public void ReadingTheCardTwiceGivesTheSameTotalAndIsCheap()
    {
        var root = ProfileVolumeRoot();
        var started = DateTime.UtcNow;
        var first = VolumeCard.Read(root);
        var second = VolumeCard.Read(root);
        Assert.AreEqual(first.TotalBytes, second.TotalBytes);
        Check.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "two card reads took 2 s or more (intent criterion 11: launch to the volume card ≤ 2 s)");
    }

    // docs/spec/01 Coverage, "measured + not measured = used by this volume (exactly)" (brief M23): the coverage of a scan of
    // a temp folder is measured against the used bytes of the folder's volume, the volume card's number (read before and
    // after the scan; within 64 MB for other writes on the volume meanwhile).
    [TestMethod]
    [DoNotParallelize] // other tests' fixtures would move the volume's used bytes during the scan
    public async Task ScanCoverageUsesTheVolumesUsedBytes()
    {
        using var temp = new TempFolder("coverage-used");
        temp.File(@"a\one.bin", 100 * ByteUnits.KB);
        var root = Path.GetPathRoot(temp.Path)!;
        var before = VolumeCard.Read(root).UsedBytes;
        var tree = await new Scanner().ScanAsync(temp.Path, null, CancellationToken.None);
        var after = VolumeCard.Read(root).UsedBytes;
        const long slack = 64 * ByteUnits.MB;
        var used = tree.Coverage.UsedBytes;
        Check.True(used >= Math.Min(before, after) - slack && used <= Math.Max(before, after) + slack,
            $"coverage used {used} is not the volume's used bytes ({before} before, {after} after the scan, ± 64 MB)");
        Assert.AreEqual(tree.Root.AllocatedBytes, tree.Coverage.MeasuredBytes);
    }

    // docs/spec/01 / plan UI contract (brief M24): when the shell cannot read a volume's Recycle Bin, both bin numbers are
    // unknown (null), never 0. Volumes of the test machine where the shell's own answer is an error (probe 2026-10-18:
    // F:, H: and the loopback share answer E_FAIL or a path error) while their space can be read.
    [TestMethod]
    public void UnreadableRecycleBinIsUnknownNotZero()
    {
        var tested = 0;
        foreach (var root in new[] { @"\\localhost\ambynet\", @"F:\", @"H:\" })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var (hr, _, _) = Native.QueryRecycleBin(root);
            if (hr >= 0)
            {
                continue;
            }

            try
            {
                Native.DiskSpaceOf(root);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }

            var card = VolumeCard.Read(root);
            Assert.IsNull(card.RecycleBinBytes, $"{root}: the shell answered 0x{hr:X8}, the card shows {card.RecycleBinBytes} bytes");
            Assert.IsNull(card.RecycleBinItems, $"{root}: the shell answered 0x{hr:X8}, the card shows {card.RecycleBinItems} items");
            tested++;
        }

        if (tested == 0)
        {
            Assert.Inconclusive("no volume on this machine where the shell cannot read the Recycle Bin and the space can be read");
        }
    }
}
