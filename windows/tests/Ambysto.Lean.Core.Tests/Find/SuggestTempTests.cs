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

/// <summary>ASP-17 phase A (intent/2026-10-20-asp-17-suggest.md): <see cref="Suggest.Temp"/>. A Temporary files row is
/// suggested when its NewestWriteUtc is before <c>nowUtc.AddMonths(-1)</c> (TempFinder's Month comparison); never a row that
/// is DisplayOnly or has a Hint, nor a node marked Link, Unreadable, CloudNotDownloaded or HardLinkCountedElsewhere. A folder
/// IS suggested here (Temp rows are the direct children of the temporary folders; "files only" is Large and old's rule). The
/// input order is kept; Why is <see cref="Text.WhyOldTemp"/>. Session 0 (no Console category); written by the test writer
/// before the rule, against a stub.</summary>
[TestClass]
public sealed class SuggestTempTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private const string TempDir = @"C:\Users\me\AppData\Local\Temp";

    private static string[] Names(IEnumerable<SuggestedTemp> rows) => rows.Select(r => r.Item.Node.Name).ToArray();

    /// <summary>A direct child of the temporary folder: a folder unless <paramref name="file"/>. Its node's own time follows
    /// <paramref name="newest"/> unless <paramref name="ownMinutes"/> says otherwise.</summary>
    private static TempItem Item(string name, DateTime newest, bool file = false, NodeMarks marks = NodeMarks.None, bool displayOnly = false, string? hint = null, long? ownMinutes = null)
    {
        var path = TempDir + @"\" + name;
        var minutes = ownMinutes ?? Trees.Minutes(new DateTimeOffset(newest, TimeSpan.Zero));
        var node = file
            ? ScanNode.File(path, 4096, 4096, minutes, null, marks)
            : ScanNode.Folder(path, minutes, null, marks);
        return new TempItem(node, newest, displayOnly, hint);
    }

    // The rule: older than one month is suggested, whatever the node is (file or folder); newer is not, however old the
    // 1-week default would call it.
    [TestMethod]
    public void AnItemOlderThanOneMonthIsSuggestedAndANewerOneIsNot()
    {
        var items = new List<TempItem>
        {
            Item("old-folder", Now.AddDays(-60)),
            Item("old-file.tmp", Now.AddDays(-45), file: true),
            Item("three-weeks", Now.AddDays(-21)),
            Item("eight-days", Now.AddDays(-8)),
            Item("today", Now.AddHours(-1), file: true),
        };
        var result = Suggest.Temp(items, Now);
        Check.Sequence(Names(result), "old-folder", "old-file.tmp");
    }

    // The row handed back is the input row itself (the UI ticks by it), not a copy.
    [TestMethod]
    public void ARowCarriesTheInputItem()
    {
        var item = Item("old", Now.AddDays(-60));
        var result = Suggest.Temp([item], Now);
        Assert.AreEqual(1, result.Count);
        Assert.AreSame(item, result[0].Item);
    }

    // Boundary, TempFinder's Month comparison (newest >= cutoff is left out): exactly nowUtc.AddMonths(-1) is not
    // suggested; one tick before it is.
    [TestMethod]
    public void ExactlyOneMonthIsNotSuggestedAndOneTickEarlierIs()
    {
        var cutoff = Now.AddMonths(-1);
        var items = new List<TempItem>
        {
            Item("at-cutoff", cutoff),
            Item("one-tick-before", cutoff.AddTicks(-1)),
            Item("one-second-after", cutoff.AddSeconds(1)),
            Item("one-minute-before", cutoff.AddMinutes(-1)),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "one-tick-before", "one-minute-before");
    }

    // "The same comparison as TempFinder's Month age" is a calendar month, not 30 or 31 days. From 31 March the month
    // ago is 28 February (12:00): an item at 1 March 00:00 is only 30 days and 12 hours old, newer than that cutoff.
    [TestMethod]
    public void TheMonthIsACalendarMonthAtTheEndOfAMonth()
    {
        var now = new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc);
        var items = new List<TempItem>
        {
            Item("one-march", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            Item("feb-28-noon", new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc)),
            Item("feb-28-before-noon", new DateTime(2026, 2, 28, 11, 59, 0, DateTimeKind.Utc)),
        };
        Check.Sequence(Names(Suggest.Temp(items, now)), "feb-28-before-noon");
    }

    // And not 31 days either: on 16 October the month ago is 16 September (30 days); an item 30.25 days old is suggested.
    [TestMethod]
    public void TheMonthIsNotAlwaysThirtyOneDays()
    {
        var items = new List<TempItem>
        {
            Item("sep-16-morning", new DateTime(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc)),
            Item("sep-16-afternoon", new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc)),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "sep-16-morning");
    }

    // The decision is made at the nowUtc it is given: the same items at a later now are suggested.
    [TestMethod]
    public void TheDecisionIsMadeAtTheGivenNow()
    {
        var items = new List<TempItem> { Item("a", Now.AddDays(-10)), Item("b", Now.AddDays(-20)) };
        Assert.AreEqual(0, Suggest.Temp(items, Now).Count);
        Check.Sequence(Names(Suggest.Temp(items, Now.AddDays(25))), "a", "b");
    }

    // The decision is NewestWriteUtc (the newest write over the item's whole tree), not the node's own last write: a
    // folder whose own date is old but holds a fresh file is not suggested; the reverse is.
    [TestMethod]
    public void TheDecisionUsesNewestWriteNotTheNodesOwnDate()
    {
        var items = new List<TempItem>
        {
            Item("old-folder-fresh-file", Now.AddDays(-2), ownMinutes: Trees.Minutes(new DateTimeOffset(Now.AddDays(-400), TimeSpan.Zero))),
            Item("new-folder-old-files", Now.AddDays(-90), ownMinutes: Trees.Minutes(new DateTimeOffset(Now, TimeSpan.Zero))),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "new-folder-old-files");
    }

    // Never: a DisplayOnly row (the Windows\Temp row is one, with DateTime.MinValue) or a row with a Hint.
    [TestMethod]
    public void ADisplayOnlyRowOrARowWithAHintIsNeverSuggested()
    {
        var items = new List<TempItem>
        {
            Item("plain", Now.AddDays(-60)),
            Item("display-only", Now.AddDays(-60), displayOnly: true),
            Item("hinted", Now.AddDays(-60), hint: Text.HintTempNotWhole),
            Item("both", Now.AddDays(-60), displayOnly: true, hint: Text.HintProtectedByYou),
            new TempItem(ScanNode.Folder(@"C:\Windows\Temp", 0, null, NodeMarks.Unreadable), DateTime.MinValue, true, Text.HintSystemWide),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "plain");
    }

    // Never: a node marked Link, Unreadable, CloudNotDownloaded or HardLinkCountedElsewhere, alone or with another mark; a
    // node with only another mark (Hidden) is a normal row.
    [TestMethod]
    public void AMarkedNodeIsNeverSuggested()
    {
        var items = new List<TempItem>
        {
            Item("control", Now.AddDays(-60)),
            Item("hidden", Now.AddDays(-60), marks: NodeMarks.Hidden),
            Item("link-folder", Now.AddDays(-60), marks: NodeMarks.Link),
            Item("link-file", Now.AddDays(-60), file: true, marks: NodeMarks.Link),
            Item("unreadable-folder", Now.AddDays(-60), marks: NodeMarks.Unreadable),
            Item("unreadable-file", Now.AddDays(-60), file: true, marks: NodeMarks.Unreadable),
            Item("cloud-folder", Now.AddDays(-60), marks: NodeMarks.CloudNotDownloaded),
            Item("cloud-file", Now.AddDays(-60), file: true, marks: NodeMarks.CloudNotDownloaded),
            Item("hardlink-file", Now.AddDays(-60), file: true, marks: NodeMarks.HardLinkCountedElsewhere),
            Item("link-hidden", Now.AddDays(-60), marks: NodeMarks.Link | NodeMarks.Hidden),
            Item("hardlink-hidden", Now.AddDays(-60), file: true, marks: NodeMarks.HardLinkCountedElsewhere | NodeMarks.Hidden),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "control", "hidden");
    }

    // The input order is kept: the result is not sorted by age, name or size.
    [TestMethod]
    public void TheInputOrderIsKept()
    {
        var items = new List<TempItem>
        {
            Item("m", Now.AddDays(-50)),
            Item("skip-me", Now.AddDays(-3)),
            Item("z", Now.AddDays(-400)),
            Item("a", Now.AddDays(-35)),
            Item("q", Now.AddDays(-100)),
        };
        Check.Sequence(Names(Suggest.Temp(items, Now)), "m", "z", "a", "q");
        Check.Sequence(Names(Suggest.Temp([.. Enumerable.Reverse(items)], Now)), "q", "a", "z", "m");
    }

    // The date is the LOCAL day: at a moment whose local day differs from its UTC day, Why names the local one (a rule that
    // dropped the conversion to local time would name the UTC day).
    [TestMethod]
    public void WhyNamesTheLocalDayNotTheUtcDay()
    {
        var newest = SuggestDates.AMomentWhoseLocalDayDiffers();
        var result = Suggest.Temp([Item("old", newest)], Now);
        Assert.AreEqual(1, result.Count);
        var expected = "Unchanged since " + SuggestDates.LocalDay(newest);
        Assert.AreEqual(expected, result[0].Why);
        Assert.AreNotEqual("Unchanged since " + SuggestDates.UtcDay(newest), result[0].Why);
        Assert.AreEqual(expected, Text.WhyOldTemp(newest));
    }

    [TestMethod]
    public void NoItemsGiveNoRows()
    {
        Assert.AreEqual(0, Suggest.Temp([], Now).Count);
    }

    // Why is Text.WhyOldTemp(newest write): "Unchanged since <short date>". The short date is of the item's NewestWriteUtc
    // (not the node's own date). The test time is noon UTC; the date is accepted as the UTC day or as the local day (the
    // contract does not say which: see the questions).
    [TestMethod]
    public void WhyNamesTheNewestWriteDate()
    {
        var newest = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var item = Item("old", newest, ownMinutes: Trees.Minutes(new DateTimeOffset(Now, TimeSpan.Zero)));
        var result = Suggest.Temp([item], Now);
        Assert.AreEqual(1, result.Count);
        SuggestDates.AssertWhy(result[0].Why, "Unchanged since ", newest);
        Assert.AreEqual(Text.WhyOldTemp(newest), result[0].Why);
    }
}

/// <summary>Checks a Why text that ends in a short date: the LOCAL day of the timestamp (contract, settled 2026-10-20).</summary>
internal static class SuggestDates
{
    public static string LocalDay(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("d", CultureInfo.CurrentCulture);

    public static string UtcDay(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("d", CultureInfo.CurrentCulture);

    public static void AssertWhy(string why, string prefix, DateTime utc) =>
        Assert.AreEqual(prefix + LocalDay(utc), why);

    public static void AssertWhy(string why, string prefix, DateTimeOffset when) => AssertWhy(why, prefix, when.UtcDateTime);

    /// <summary>A moment of 2025 (UTC) whose local day differs from its UTC day in this run's time zone; Inconclusive when the
    /// zone is UTC itself (no such moment).</summary>
    public static DateTime AMomentWhoseLocalDayDiffers()
    {
        foreach (var candidate in new[] { new DateTime(2025, 3, 7, 3, 30, 0, DateTimeKind.Utc), new DateTime(2025, 3, 7, 20, 30, 0, DateTimeKind.Utc) })
        {
            if (LocalDay(candidate) != UtcDay(candidate))
            {
                return candidate;
            }
        }

        Assert.Inconclusive("The time zone of this run (" + TimeZoneInfo.Local.Id + ") keeps the local day equal to the UTC day at 03:30 and 20:30 UTC, so the local day cannot be told from the UTC day.");
        return default;
    }
}
