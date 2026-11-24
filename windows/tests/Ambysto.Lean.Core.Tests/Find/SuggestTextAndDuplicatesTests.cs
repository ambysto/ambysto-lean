using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-17 phase A (intent/2026-10-20-asp-17-suggest.md): the Suggest texts of the intent (the Why texts with their
/// date, the confirmation with and without program files, the button, column and status-line texts), and the Duplicates rule:
/// Suggest in Duplicates is the existing Newest keep rule and <see cref="DuplicateSelection.TickAllBut"/>, so each ticked
/// copy's Why is <see cref="Text.WhyOlderCopy"/>. Session 0; written by the test writer before the code, against stubs.</summary>
[TestClass]
public sealed class SuggestTextAndDuplicatesTests
{
    // ---- texts ----

    [TestMethod]
    public void TheButtonColumnAndStatusTextsAreTheIntentsWords()
    {
        Assert.AreEqual("Suggest", Check.Constant<string>(typeof(Text), "Suggest"));
        Assert.AreEqual("Why", Check.Constant<string>(typeof(Text), "ColumnWhy"));
        Assert.AreEqual("Clear suggestion", Check.Constant<string>(typeof(Text), "ClearSuggestion"));
        Assert.AreEqual("Nothing to suggest in this scan", Check.Constant<string>(typeof(Text), "NothingToSuggest"));
        Assert.AreEqual("Suggest ticked 12 items", Text.SuggestTicked(12));
        Assert.AreEqual("Suggested 12 files, 18.4 GB", Text.SuggestedFiles(12, "18.4 GB"));
        Assert.AreEqual("Older copy; the newest is kept", Check.Constant<string>(typeof(Text), "WhyOlderCopy"));
        Assert.AreEqual("Pro", Check.Constant<string>(typeof(Text), "ProLabel"), "the quiet label already exists (spec 06)");
        Assert.AreEqual("Tick", Check.Constant<string>(typeof(Text), "RuleConfirmTick"), "the Tick button of the over-100 question is the rules' own");
    }

    [TestMethod]
    public void TheWhyTextsNameTheKindAndCarryTheShortDate()
    {
        var utc = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var offset = new DateTimeOffset(utc);
        SuggestDates.AssertWhy(Text.WhyOldTemp(utc), "Unchanged since ", utc);
        SuggestDates.AssertWhy(Text.WhyOldInstaller(offset), "Old download, unchanged since ", utc);
        SuggestDates.AssertWhy(Text.WhyLargeUnused(offset), "1 GB or more, unchanged since ", utc);
    }

    // Different dates give different texts (the date is in the text, not a constant).
    [TestMethod]
    public void TheWhyTextsChangeWithTheDate()
    {
        var a = new DateTime(2024, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var b = new DateTime(2025, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreNotEqual(Text.WhyOldTemp(a), Text.WhyOldTemp(b));
        Assert.AreNotEqual(Text.WhyOldInstaller(new DateTimeOffset(a)), Text.WhyOldInstaller(new DateTimeOffset(b)));
        Assert.AreNotEqual(Text.WhyLargeUnused(new DateTimeOffset(a)), Text.WhyLargeUnused(new DateTimeOffset(b)));
    }

    // The confirmation with program files: the intent's example, word for word.
    [TestMethod]
    public void SuggestConfirmNamesTheCountAndTheProgramFiles()
    {
        Assert.AreEqual("Suggest ticks 340 items, 12 of them program files (.dll, .exe and similar). Tick them?", Text.SuggestConfirm(340, 12));
    }

    // Without program files the program-file part is left out (as RuleConfirm does), and the question stays.
    [TestMethod]
    public void SuggestConfirmLeavesTheProgramFilePartOutWhenThereAreNone()
    {
        var text = Text.SuggestConfirm(340, 0);
        Check.Contains(text, "340");
        Check.DoesNotContain(text, "program");
        Check.DoesNotContain(text, ".dll");
        Assert.IsTrue(text.StartsWith("Suggest ticks 340 items", StringComparison.Ordinal), text);
        Assert.IsTrue(text.EndsWith("Tick them?", StringComparison.Ordinal), text);
        Assert.AreNotEqual(Text.SuggestConfirm(340, 12), text);
    }

    [TestMethod]
    public void SuggestConfirmCountsAreTheOnesGiven()
    {
        var text = Text.SuggestConfirm(101, 3);
        Check.Contains(text, "101");
        Check.Contains(text, "3 of them program files");
        Check.DoesNotContain(Text.SuggestConfirm(101, 3), "340");
    }

    // ---- Duplicates: Suggest = the Newest keep rule + TickAllBut ----

    private const long Bytes = 2 * ByteUnits.MB;

    private static DuplicateCopy Copy(string path, long minutes, string? hint = null) =>
        new(Trees.File(path, Bytes, minutes), false) { Hint = hint };

    private static DuplicateGroup Group(params DuplicateCopy[] copies)
    {
        var list = copies.ToList();
        var suggested = list.OrderByDescending(c => c.Node.LastWriteMinutes).ThenBy(c => c.Node.Path.Length).ThenBy(c => c.Node.Path, StringComparer.Ordinal).First();
        return new DuplicateGroup(Bytes, list, suggested, list.Where(c => !ReferenceEquals(c, suggested) && c.Hint is null).Sum(c => c.Node.AllocatedBytes));
    }

    /// <summary>What Suggest does for one group in Duplicates, by the intent: keep the Newest, then TickAllBut.</summary>
    private static (DuplicateCopy? Kept, IReadOnlyList<DuplicateCopy> Ticked) SuggestFor(DuplicateGroup group)
    {
        var keep = DuplicateRules.Keep(group, KeepRule.NewestModified, null);
        var selection = new DuplicateSelection(group);
        Assert.IsTrue(selection.TickAllBut(keep), "TickAllBut accepts the kept copy");
        return (keep, selection.Ticked);
    }

    [TestMethod]
    public void SuggestInDuplicatesKeepsTheNewestCopyAndTicksTheOlderOnes()
    {
        var oldest = Copy(@"D:\Photos\a.jpg", 1000);
        var middle = Copy(@"D:\Photos\Backup\a.jpg", 2000);
        var newest = Copy(@"D:\Photos\Edited\a.jpg", 3000);
        var (kept, ticked) = SuggestFor(Group(middle, newest, oldest));
        Assert.AreSame(newest, kept);
        CollectionAssert.AreEquivalent(new[] { oldest, middle }, ticked.ToArray());
        Check.False(ticked.Contains(newest), "the newest copy is never ticked");
    }

    // A tie on the minute: the shortest path is kept; the rest are ticked.
    [TestMethod]
    public void ATieKeepsTheShortestPath()
    {
        var shortest = Copy(@"D:\P\a.jpg", 5000);
        var longer = Copy(@"D:\Photos\a.jpg", 5000);
        var older = Copy(@"D:\Old\a.jpg", 4000);
        var (kept, ticked) = SuggestFor(Group(longer, shortest, older));
        Assert.AreSame(shortest, kept);
        CollectionAssert.AreEquivalent(new[] { longer, older }, ticked.ToArray());
    }

    // A display-only copy is never ticked, and it may be the kept (newest) one: then every movable copy is ticked.
    [TestMethod]
    public void ADisplayOnlyCopyIsNeverTickedAndMayBeTheNewest()
    {
        var protectedOlder = Copy(@"D:\Keep\a.jpg", 1000, Text.HintProtectedByYou);
        var plain = Copy(@"D:\Photos\a.jpg", 2000);
        var newest = Copy(@"D:\Photos\New\a.jpg", 3000);
        var (kept, ticked) = SuggestFor(Group(protectedOlder, plain, newest));
        Assert.AreSame(newest, kept);
        CollectionAssert.AreEquivalent(new[] { plain }, ticked.ToArray());

        var protectedNewest = Copy(@"D:\Keep\b.jpg", 9000, Text.HintProtectedByYou);
        var one = Copy(@"D:\Photos\b.jpg", 2000);
        var two = Copy(@"D:\Photos\Old\b.jpg", 1000);
        var (kept2, ticked2) = SuggestFor(Group(one, two, protectedNewest));
        Assert.AreSame(protectedNewest, kept2);
        CollectionAssert.AreEquivalent(new[] { one, two }, ticked2.ToArray());
    }
}
