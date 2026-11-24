using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-15 (intent/2026-10-18-asp-15-duplicates-helpers.md, "Rules"): <see cref="DuplicateRules.Keep"/> chooses
/// the kept copy of a group. Newest / Oldest modified: the latest / earliest modification minutes; ties: the shortest path,
/// then the path order. Shortest path: the fewest characters; ties: the newest, then the path order. Inside a folder: among
/// the copies inside it (canonical paths, the folder itself or below), the newest; none inside: null (the group is left
/// untouched). A display-only copy may be the kept one. Each case is checked with the copies in both orders, so the
/// result never depends on the order of the list. Written by the test writer before the Core.</summary>
[TestClass]
public sealed class DuplicateKeepRuleTests
{
    private const long Bytes = 2 * ByteUnits.MB;
    private const string Photos = @"D:\Photos";

    private static DuplicateCopy Copy(string path, long minutes, string? hint = null) =>
        new(Trees.File(path, Bytes, minutes), false) { Hint = hint };

    /// <summary>A group of <paramref name="copies"/> in this order, with the suggested keep the finder would give
    /// (newest; ties: shortest path; then alphabetical), so a Core that reuses the badge for a rule is not misled.</summary>
    private static DuplicateGroup Group(IReadOnlyList<DuplicateCopy> copies)
    {
        var suggested = copies
            .OrderByDescending(c => c.Node.LastWriteMinutes)
            .ThenBy(c => c.Node.Path.Length)
            .ThenBy(c => c.Node.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Node.Path, StringComparer.Ordinal)
            .First();
        var reclaimable = copies.Where(c => !ReferenceEquals(c, suggested) && c.Hint is null).Sum(c => c.Node.AllocatedBytes);
        return new DuplicateGroup(Bytes, copies.ToList(), suggested, reclaimable);
    }

    /// <summary><paramref name="rule"/> keeps <paramref name="expected"/>, with the copies in the given order and reversed.</summary>
    private static void AssertKeeps(KeepRule rule, string? folder, DuplicateCopy expected, params DuplicateCopy[] copies)
    {
        foreach (var order in new[] { copies, Enumerable.Reverse(copies).ToArray() })
        {
            var group = Group(order);
            var kept = DuplicateRules.Keep(group, rule, folder);
            Assert.IsNotNull(kept, $"{rule} kept nothing; expected {expected.Node.Path}");
            Assert.AreSame(expected, kept, $"{rule} (folder {folder ?? "none"}) kept {kept.Node.Path}; expected {expected.Node.Path} (order: {string.Join(", ", order.Select(c => c.Node.Path))})");
        }
    }

    /// <summary><paramref name="rule"/> leaves the group untouched (null), with the copies in the given order and reversed.</summary>
    private static void AssertKeepsNothing(KeepRule rule, string? folder, params DuplicateCopy[] copies)
    {
        foreach (var order in new[] { copies, Enumerable.Reverse(copies).ToArray() })
        {
            var kept = DuplicateRules.Keep(Group(order), rule, folder);
            Assert.IsNull(kept, $"{rule} (folder {folder ?? "none"}) kept {kept?.Node.Path}; expected the group left untouched");
        }
    }

    // Rule "Newest modified": the copy with the latest modification minutes wins, even with the longest path and the last name.
    [TestMethod]
    public void NewestKeepsTheLatestModification()
    {
        var old = Copy(@"D:\a.jpg", 1000);
        var newest = Copy(@"D:\Photos\Holiday\z-long-name.jpg", 3000);
        var middle = Copy(@"D:\Photos\b.jpg", 2000);
        AssertKeeps(KeepRule.NewestModified, null, newest, old, newest, middle);
    }

    // Rule "Newest modified", first tie-break: equal minutes go to the shortest path, even when it sorts last.
    [TestMethod]
    public void NewestTieGoesToTheShortestPath()
    {
        var longer = Copy(@"D:\Photos\a-copy.jpg", 3000);
        var shorter = Copy(@"D:\Photos\z.jpg", 3000);
        var older = Copy(@"D:\x.jpg", 1000);
        AssertKeeps(KeepRule.NewestModified, null, shorter, longer, shorter, older);
    }

    // Rule "Newest modified", second tie-break: equal minutes and equal lengths go to the path that sorts first.
    [TestMethod]
    public void NewestTieOnLengthGoesToThePathThatSortsFirst()
    {
        var b = Copy(@"D:\Photos\b.jpg", 3000);
        var a = Copy(@"D:\Photos\a.jpg", 3000);
        var c = Copy(@"D:\Photos\c.jpg", 3000);
        AssertKeeps(KeepRule.NewestModified, null, a, b, a, c);
    }

    // Rule "Oldest modified": the copy with the earliest modification minutes wins, even with the longest path and the last name.
    [TestMethod]
    public void OldestKeepsTheEarliestModification()
    {
        var oldest = Copy(@"D:\Photos\Holiday\z-long-name.jpg", 1000);
        var newer = Copy(@"D:\a.jpg", 3000);
        var middle = Copy(@"D:\Photos\b.jpg", 2000);
        AssertKeeps(KeepRule.OldestModified, null, oldest, newer, oldest, middle);
    }

    // Rule "Oldest modified", first tie-break: equal minutes go to the shortest path, even when it sorts last.
    [TestMethod]
    public void OldestTieGoesToTheShortestPath()
    {
        var longer = Copy(@"D:\Photos\a-copy.jpg", 1000);
        var shorter = Copy(@"D:\Photos\z.jpg", 1000);
        var newer = Copy(@"D:\x.jpg", 3000);
        AssertKeeps(KeepRule.OldestModified, null, shorter, longer, shorter, newer);
    }

    // Rule "Oldest modified", second tie-break: equal minutes and equal lengths go to the path that sorts first.
    [TestMethod]
    public void OldestTieOnLengthGoesToThePathThatSortsFirst()
    {
        var b = Copy(@"D:\Photos\b.jpg", 1000);
        var a = Copy(@"D:\Photos\a.jpg", 1000);
        var newer = Copy(@"D:\Photos\0.jpg", 2000);
        AssertKeeps(KeepRule.OldestModified, null, a, b, a, newer);
    }

    // Rule "Shortest path": the fewest characters in the full path wins, even when it is the oldest and sorts last.
    [TestMethod]
    public void ShortestKeepsTheFewestCharacters()
    {
        var shortest = Copy(@"D:\z.jpg", 1000);
        var newest = Copy(@"D:\Photos\a.jpg", 3000);
        var deepest = Copy(@"D:\Photos\Holiday\a.jpg", 2000);
        AssertKeeps(KeepRule.ShortestPath, null, shortest, newest, shortest, deepest);
    }

    // Rule "Shortest path", first tie-break: equal lengths go to the newest, even when it sorts last.
    [TestMethod]
    public void ShortestTieGoesToTheNewest()
    {
        var older = Copy(@"D:\Photos\a.jpg", 1000);
        var newer = Copy(@"D:\Photos\z.jpg", 3000);
        var longer = Copy(@"D:\Photos\Holiday\b.jpg", 9000);
        AssertKeeps(KeepRule.ShortestPath, null, newer, older, newer, longer);
    }

    // Rule "Shortest path", second tie-break: equal lengths and equal minutes go to the path that sorts first.
    [TestMethod]
    public void ShortestTieOnTimeGoesToThePathThatSortsFirst()
    {
        var b = Copy(@"D:\Photos\b.jpg", 2000);
        var a = Copy(@"D:\Photos\a.jpg", 2000);
        var older = Copy(@"D:\Photos\0.jpg", 1000);
        AssertKeeps(KeepRule.ShortestPath, null, a, b, a, older);
    }

    // Rule "Inside a folder I choose": the one copy inside the folder is kept, though another copy is newer and shorter.
    [TestMethod]
    public void InsideFolderKeepsTheCopyInside()
    {
        var inside = Copy(@"D:\Photos\Holiday\beach.jpg", 1000);
        var outside = Copy(@"D:\b.jpg", 3000);
        var other = Copy(@"D:\Other\c.jpg", 2000);
        AssertKeeps(KeepRule.InsideFolder, Photos, inside, outside, inside, other);
    }

    // Rule "Inside a folder I choose": of two copies inside, the newest is kept (not the shorter, not the first by name);
    // the newer copy outside is not.
    [TestMethod]
    public void InsideFolderWithTwoInsideKeepsTheNewest()
    {
        var insideOld = Copy(@"D:\Photos\a.jpg", 1000);
        var insideNew = Copy(@"D:\Photos\Holiday\z.jpg", 2000);
        var outsideNewest = Copy(@"D:\Else\b.jpg", 9000);
        AssertKeeps(KeepRule.InsideFolder, Photos, insideNew, insideOld, insideNew, outsideNewest);
    }

    // Rule "Inside a folder I choose": no copy inside: null, the group is left untouched.
    [TestMethod]
    public void InsideFolderWithNoCopyInsideLeavesTheGroupUntouched()
    {
        AssertKeepsNothing(KeepRule.InsideFolder, Photos,
            Copy(@"D:\Else\a.jpg", 1000), Copy(@"D:\b.jpg", 2000), Copy(@"E:\Photos\c.jpg", 3000));
    }

    // Rule "Inside a folder I choose", canonical paths: a sibling whose name starts with the folder's name
    // (D:\Photos2, D:\PhotosOld) is not inside D:\Photos; alone, it leaves the group untouched; with a copy really
    // inside, that copy is kept although the sibling's copy is newer.
    [TestMethod]
    public void InsideFolderDoesNotCountASiblingWithTheSamePrefix()
    {
        var sibling = Copy(@"D:\Photos2\a.jpg", 3000);
        var siblingOld = Copy(@"D:\PhotosOld\a.jpg", 2500);
        var outside = Copy(@"D:\a.jpg", 2000);
        AssertKeepsNothing(KeepRule.InsideFolder, Photos, sibling, siblingOld, outside);

        var inside = Copy(@"D:\Photos\a.jpg", 1000);
        AssertKeeps(KeepRule.InsideFolder, Photos, inside, sibling, inside, siblingOld, outside);
    }

    // Rule "Inside a folder I choose", canonical paths: the folder given with or without a trailing separator, and in
    // another letter case, chooses the same copy; a sibling with the folder's name as prefix stays outside in every spelling.
    [TestMethod]
    public void InsideFolderIgnoresATrailingSeparatorAndLetterCase()
    {
        var inside = Copy(@"D:\Photos\Holiday\a.jpg", 1000);
        var sibling = Copy(@"D:\Photos2\a.jpg", 3000);
        var outside = Copy(@"D:\b.jpg", 2000);
        foreach (var folder in new[] { @"D:\Photos", @"D:\Photos\", @"d:\PHOTOS", @"d:\photos\", @"D:\pHoToS\hOlIdAy" })
        {
            AssertKeeps(KeepRule.InsideFolder, folder, inside, sibling, inside, outside);
        }
    }

    // Rule "Inside a folder I choose", "the folder itself or below": a copy directly in the folder and one several levels
    // below are both inside; the newer wins.
    [TestMethod]
    public void InsideFolderCountsCopiesDirectlyInItAndDeeplyBelow()
    {
        var direct = Copy(@"D:\Photos\a.jpg", 1000);
        var deep = Copy(@"D:\Photos\2026\10\18\a.jpg", 2000);
        var outside = Copy(@"D:\a.jpg", 3000);
        AssertKeeps(KeepRule.InsideFolder, Photos, deep, direct, deep, outside);
        var deepOlder = Copy(@"D:\Photos\2026\10\18\a.jpg", 500);
        AssertKeeps(KeepRule.InsideFolder, Photos, direct, direct, deepOlder, outside);
    }

    // Rules: "The kept copy may be a display-only copy, for example one in a protected folder: it stays anyway." Every
    // rule may keep a hinted copy when it is the one the rule picks.
    [TestMethod]
    public void AHintedCopyMayBeTheKeptOne()
    {
        var guardedNewestShortest = Copy(@"D:\G\a.jpg", 3000, Text.HintProtectedByYou);
        var plainOld = Copy(@"D:\Photos\b.jpg", 1000);
        var plainMiddle = Copy(@"D:\Photos\Sub\c.jpg", 2000);
        AssertKeeps(KeepRule.NewestModified, null, guardedNewestShortest, plainOld, guardedNewestShortest, plainMiddle);
        AssertKeeps(KeepRule.ShortestPath, null, guardedNewestShortest, plainOld, guardedNewestShortest, plainMiddle);

        var guardedOldest = Copy(@"D:\Photos\Guarded\a.jpg", 500, Text.HintProtectedByYou);
        AssertKeeps(KeepRule.OldestModified, null, guardedOldest, plainOld, guardedOldest, plainMiddle);

        var guardedInside = Copy(@"D:\Photos\Guarded\d.jpg", 100, Text.HintProtectedByYou);
        var newerOutside = Copy(@"D:\e.jpg", 9000);
        AssertKeeps(KeepRule.InsideFolder, @"D:\Photos\Guarded", guardedInside, newerOutside, guardedInside);
    }

    // Settled question 3: the inside-folder rule with no folder, an empty one, or one that is not a usable path (a device
    // spelling, an extended UNC spelling) throws ArgumentException; the group is never decided on a guess.
    [TestMethod]
    public void InsideFolderWithoutAUsableFolderThrows()
    {
        var group = Group([Copy(@"D:\Photos\a.jpg", 1000), Copy(@"D:\b.jpg", 2000)]);
        foreach (var folder in new[] { null, "", "   ", @"\\.\C:\x", @"\\?\UNC\server\share" })
        {
            Assert.Throws<ArgumentException>(() => DuplicateRules.Keep(group, KeepRule.InsideFolder, folder), "folder [" + (folder ?? "null") + "]");
        }
    }

    // Rule "Inside a folder I choose", canonical paths: a ".." spelling (D:\Photos\sub\..) and the extended spelling
    // \\?\D:\Photos are D:\Photos: the copy below it is kept, the newer sibling D:\Photos2 is not inside.
    [TestMethod]
    public void InsideFolderResolvesDotDotAndTheExtendedSpelling()
    {
        var inside = Copy(@"D:\Photos\Holiday\a.jpg", 1000);
        var sibling = Copy(@"D:\Photos2\a.jpg", 3000);
        var outside = Copy(@"D:\b.jpg", 2000);
        foreach (var folder in new[] { @"D:\Photos\sub\..", @"D:\Photos\sub\..\", @"\\?\D:\Photos", @"\\?\d:\photos\" })
        {
            AssertKeeps(KeepRule.InsideFolder, folder, inside, sibling, inside, outside);
        }

        AssertKeepsNothing(KeepRule.InsideFolder, @"D:\Photos\Holiday\..\..\Else", sibling, inside, outside);
    }

    // Rule "Inside a folder I choose" on a network share: the UNC folder \\server\share\Photos matches a copy below it
    // (the folder in either letter case), not a copy in \\server\share\Photos2 nor one on a local disk.
    [TestMethod]
    public void InsideFolderOnAUncShareMatchesACopyBelowIt()
    {
        var inside = Copy(@"\\server\share\Photos\2026\a.jpg", 1000);
        var sibling = Copy(@"\\server\share\Photos2\a.jpg", 3000);
        var local = Copy(@"D:\Photos\a.jpg", 2000);
        AssertKeeps(KeepRule.InsideFolder, @"\\server\share\Photos", inside, sibling, inside, local);
        AssertKeeps(KeepRule.InsideFolder, @"\\SERVER\Share\photos\", inside, sibling, inside, local);
    }
}
