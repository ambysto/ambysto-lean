using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>Gap brief H18. docs/spec/01 Windows note (4): "when the names of one file report different sizes or last-write
/// times, that one file is opened once, for attributes only …, and its own answer is used; names that agree are trusted as
/// listed"; walk rule 4: the size belongs to the name that sorts first, every other name shows 0 and is marked. Plan
/// Walker: the walk's stamp must equal the gate's facts, or every move fails "Changed since the scan". A scan cannot make
/// two folder entries of one file disagree on demand (the existing scan test finds them agreeing in the VM), so the
/// hard-link step is given the two names of one real file as nodes whose listed numbers the test chooses; the oracle is
/// the file's own attributes, read by the test.</summary>
[TestClass]
public sealed class HardLinkResolveTests
{
    private const long ListedLength = 50 * ByteUnits.KB;
    private const long ListedAllocated = 52 * ByteUnits.KB;
    private static readonly DateTimeOffset ListedWrite = new(2020, 1, 2, 3, 4, 0, TimeSpan.Zero);

    /// <summary>One file with two names in a scratch folder: <c>a-first.bin</c> sorts before <c>b-second.bin</c>.</summary>
    private static (string First, string Second, FileIdentity Id) TwoNames(TempFolder temp)
    {
        var first = temp.File("a-first.bin", 100 * ByteUnits.KB, seed: 61);
        var second = temp.Sub("b-second.bin");
        Native.CreateHardLink(second, first);
        File.SetLastWriteTimeUtc(first, new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        return (first, second, Native.IdentityOf(first));
    }

    /// <summary>A file node at <paramref name="path"/> with listed numbers as a folder entry would give them.</summary>
    private static ScanNode Listed(string path, FileIdentity id, long logical, long allocated, DateTimeOffset lastWrite, long stampLength) =>
        ScanNode.File(path, logical, allocated, Trees.Minutes(lastWrite),
            new ItemStamp(id.VolumeSerial, id.FileId, lastWrite.UtcDateTime.ToFileTimeUtc(), stampLength));

    // The file's own numbers disagree with what both names list, and the names disagree with each other in one field per
    // row: every name then carries the file's own size, allocated size and last write, the stamps hold the own length and
    // last write, the name that sorts later is marked with size 0, and exactly one file was opened.
    [TestMethod]
    [DataRow("logical")]
    [DataRow("allocated")]
    [DataRow("lastWrite")]
    [DataRow("stampLength")]
    public void HardLinkNamesThatDisagreeTakeTheFilesOwnNumbers(string field)
    {
        using var temp = new TempFolder("hardlink-disagree-" + field);
        var (first, second, id) = TwoNames(temp);
        var (ownAllocated, ownLength) = Native.StandardInfoOf(first);
        var ownWrite = File.GetLastWriteTimeUtc(first).ToFileTimeUtc();
        var ownMinutes = Trees.Minutes(DateTimeOffset.FromFileTime(ownWrite));
        Assert.AreNotEqual(ListedLength, ownLength, "the fixture's own length differs from the listed one");

        var a = Listed(first, id, ListedLength, ListedAllocated, ListedWrite, ListedLength);
        var b = field switch
        {
            "logical" => Listed(second, id, ListedLength + 1, ListedAllocated, ListedWrite, ListedLength),
            "allocated" => Listed(second, id, ListedLength, ListedAllocated + 4096, ListedWrite, ListedLength),
            "lastWrite" => Listed(second, id, ListedLength, ListedAllocated, ListedWrite.AddMinutes(5), ListedLength),
            _ => Listed(second, id, ListedLength, ListedAllocated, ListedWrite, ListedLength + 1),
        };

        // The later name first in the list: which name keeps the size is decided by the sort, not the listing order.
        var result = HardLinks.Resolve(new List<ScanNode?> { b, a }, 2, CancellationToken.None);

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.FilesOpened, "names that disagree: that one file is opened once");
        Assert.IsFalse(a.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "the name that sorts first carries the size");
        Assert.AreEqual(ownLength, a.LogicalBytes, "the file's own length");
        Assert.AreEqual(ownAllocated, a.AllocatedBytes, "the file's own allocated size");
        Assert.IsTrue(b.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "the later name is counted elsewhere");
        Assert.AreEqual(0L, b.LogicalBytes);
        Assert.AreEqual(0L, b.AllocatedBytes);
        foreach (var name in new[] { a, b })
        {
            Assert.AreEqual(ownMinutes, name.LastWriteMinutes, name.Path + ": the file's own last write");
            Assert.IsNotNull(name.Stamp, name.Path);
            Assert.AreEqual(ownLength, name.Stamp.Value.Length, name.Path + ": the stamp holds the file's own length");
            Assert.AreEqual(ownWrite, name.Stamp.Value.LastWriteTimeUtcTicks, name.Path + ": the stamp holds the file's own last write");
            Assert.AreEqual(id.FileId, name.Stamp.Value.FileId, name.Path + ": the identity is unchanged");
            Assert.AreEqual(id.VolumeSerial, name.Stamp.Value.VolumeSerial, name.Path);
        }
    }

    // Names that agree are trusted as listed: nothing is opened, the listed numbers stay (even though the file's own
    // differ), and walk rule 4 still applies.
    [TestMethod]
    public void HardLinkNamesThatAgreeAreNotOpened()
    {
        using var temp = new TempFolder("hardlink-agree");
        var (first, second, id) = TwoNames(temp);
        Assert.AreNotEqual(ListedLength, Native.StandardInfoOf(first).EndOfFile, "the fixture's own length differs from the listed one");
        var a = Listed(first, id, ListedLength, ListedAllocated, ListedWrite, ListedLength);
        var b = Listed(second, id, ListedLength, ListedAllocated, ListedWrite, ListedLength);

        var result = HardLinks.Resolve(new List<ScanNode?> { b, a }, 2, CancellationToken.None);

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, result.FilesOpened, "names that agree are not opened");
        Assert.AreEqual(ListedLength, a.LogicalBytes, "the listed length is kept");
        Assert.AreEqual(ListedAllocated, a.AllocatedBytes, "the listed allocated size is kept");
        Assert.AreEqual(Trees.Minutes(ListedWrite), a.LastWriteMinutes);
        Assert.AreEqual(ListedLength, a.Stamp!.Value.Length);
        Assert.AreEqual(ListedWrite.UtcDateTime.ToFileTimeUtc(), a.Stamp.Value.LastWriteTimeUtcTicks);
        Assert.IsTrue(b.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(0L, b.LogicalBytes);
        Assert.AreEqual(0L, b.AllocatedBytes);
    }

    // Names that disagree but whose file no longer exists: nothing can be opened, so nothing counts as opened and the
    // listed numbers stay (never zeros: a stale stamp fails closed at move time, "Changed since the scan").
    [TestMethod]
    public void HardLinkNamesThatDisagreeButAreGoneKeepTheirListedNumbers()
    {
        using var temp = new TempFolder("hardlink-gone");
        var (first, second, id) = TwoNames(temp);
        File.Delete(second);
        File.Delete(first);
        Assert.IsFalse(File.Exists(first) || File.Exists(second), "both names are gone");
        var a = Listed(first, id, ListedLength, ListedAllocated, ListedWrite, ListedLength);
        var b = Listed(second, id, ListedLength + 1, ListedAllocated, ListedWrite, ListedLength + 1);

        var result = HardLinks.Resolve(new List<ScanNode?> { a, b }, 2, CancellationToken.None);

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, result.FilesOpened, "a file that cannot be opened is not counted as opened");
        Assert.AreEqual(ListedLength, a.LogicalBytes, "the listed length stays, not 0");
        Assert.AreEqual(ListedAllocated, a.AllocatedBytes, "the listed allocated size stays, not 0");
        Assert.AreEqual(Trees.Minutes(ListedWrite), a.LastWriteMinutes, "the listed last write stays");
        Assert.AreEqual(ListedLength, a.Stamp!.Value.Length, "the listed stamp length stays");
        Assert.AreEqual(ListedWrite.UtcDateTime.ToFileTimeUtc(), a.Stamp.Value.LastWriteTimeUtcTicks, "the listed stamp time stays");
        Assert.AreEqual(ListedLength + 1, b.Stamp!.Value.Length, "the later name's listed stamp stays too");
        Assert.IsTrue(b.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "walk rule 4 still applies");
        Assert.AreEqual(0L, b.LogicalBytes);
    }
}
