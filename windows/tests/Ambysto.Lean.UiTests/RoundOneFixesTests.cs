using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>The group 11 app review's round-1 fixes that had no UI test (plan, review log "Group 11 app review"): the
/// duplicates pruned at or below a folder moved from the map; no move while a duplicates search reads the tree; and Free
/// history cut by session (docs/spec/05, 06). Real moves only of the test's own items under
/// <c>%USERPROFILE%\LeanUiMove-*</c>, put back by the test; the bin is never emptied or opened. The history test writes its
/// own journal lines and puts the user's journal back byte for byte.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoundOneFixesTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const long GB = ByteUnits.GB;

    // ---- (a) pruning below a moved folder -----------------------------------------------------------------------------

    private static List<AutomationElement> Copies(AutomationElement view) => AppSession.FindAll(view, "DuplicateCopy");

    private static string CopiesDump(AutomationElement view) => Table.Dump(view, "DuplicateCopy");

    /// <summary>A copy is listed when a DuplicateCopy row holds its name and full folder (ASP-10: the row's automation name).</summary>
    private static bool Listed(AutomationElement view, string path) => Copies(view).Any(c => Table.Holds(Table.RowName(c), path));

    // Review round 1, MINOR "pruning below a moved folder": a folder moved from the map takes the duplicate copies inside it
    // (at any depth) out of the Duplicates view, without a new search. Fixture: group X has four 2 MB copies, two inside
    // holder (one in holder\inner) and two outside; group Y has two 3 MB copies, both inside holder; group Z has two 1 MB +
    // 4 KB copies, both outside. After the map move of holder: X lists only its two outside copies ("2 copies, …"), Y is
    // gone, Z is unchanged, and no listed copy lies in holder. Then the session is put back (holder back, same file id).
    [TestMethod]
    [TestCategory("Console")]
    public void AMapMoveOfAFolderTakesItsCopiesOutOfDuplicates()
    {
        using var fixture = new MoveFixture("prune");
        var x = new byte[(int)(2 * MB)];
        var y = new byte[(int)(3 * MB)];
        var z = new byte[(int)(MB + (4 * KB))];
        new Random(11_1).NextBytes(x);
        new Random(11_2).NextBytes(y);
        new Random(11_3).NextBytes(z);
        var x1 = fixture.FileWithBytes(@"holder\x-1.dat", x);
        var x2 = fixture.FileWithBytes(@"holder\inner\x-2.dat", x);
        var x3 = fixture.FileWithBytes(@"out-a\x-3.dat", x);
        var x4 = fixture.FileWithBytes(@"out-b\x-4.dat", x);
        var y1 = fixture.FileWithBytes(@"holder\y-1.dat", y);
        var y2 = fixture.FileWithBytes(@"holder\inner\deeper\y-2.dat", y);
        var z1 = fixture.FileWithBytes(@"out-a\z-1.dat", z);
        var z2 = fixture.FileWithBytes(@"out-c\z-2.dat", z);
        var holder = fixture.Sub("holder");
        var holderIdentity = MoveFixture.Identity(holder);
        var x1Hash = MoveFixture.Hash(x1);

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count == 3, Ui.DuplicatesTimeout),
            "precondition: three duplicate groups; groups: [" + string.Join(" | ", AppSession.FindAll(view, "DuplicateGroup").Select(g => g.Current.Name)) + "]");
        foreach (var path in new[] { x1, x2, x3, x4, y1, y2, z1, z2 })
        {
            Assert.IsTrue(Listed(view, path), $"precondition: {path} is listed; copies: {CopiesDump(view)}");
        }

        Ui.BackToOverview(app, "DuplicatesView");
        var map = Ui.Map(app);
        var move = Ui.MenuItemOf(app, Ui.TileOf(map, "holder"), "MoveToRecycleBin");
        Assert.IsTrue(move.Current.IsEnabled, "Move to Recycle Bin… is disabled on the holder tile");
        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        var report = Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
        Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "the move of holder skipped or lost something: " + Ui.Dump(report));
        Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(holder) is not null, TimeSpan.FromSeconds(10)), "holder is not in the Recycle Bin");

        try
        {
            view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
            // A row's automation name holds its full folder: holder itself, or a folder below it, for a copy inside holder.
            Assert.IsTrue(AppSession.WaitUntil(() => !Copies(view).Any(c => Table.RowName(c).Contains(holder, StringComparison.OrdinalIgnoreCase)), Ui.ElementTimeout),
                "copies inside the moved folder are still listed: " + CopiesDump(view));
            var groups = AppSession.FindAll(view, "DuplicateGroup").Select(g => g.Current.Name).ToList();
            Assert.AreEqual(2, groups.Count, "expected group X (pruned to 2) and group Z; groups: [" + string.Join(" | ", groups) + "]");
            Assert.IsTrue(groups.Contains(Text.CopiesEach(2, ByteUnits.Format(2 * MB))), $"group X is not \"{Text.CopiesEach(2, ByteUnits.Format(2 * MB))}\": [{string.Join(" | ", groups)}]");
            Assert.IsTrue(groups.Contains(Text.CopiesEach(2, ByteUnits.Format(MB + (4 * KB)))), "group Z changed: [" + string.Join(" | ", groups) + "]");
            Assert.IsFalse(groups.Any(g => g.Contains(ByteUnits.Format(3 * MB), StringComparison.Ordinal)), "group Y (both copies inside holder) is still listed: [" + string.Join(" | ", groups) + "]");
            foreach (var path in new[] { x3, x4, z1, z2 })
            {
                Assert.IsTrue(Listed(view, path), $"{path} (outside holder) is no longer listed: {CopiesDump(view)}");
            }

            Assert.AreEqual(4, Copies(view).Count, "copies after the move: " + CopiesDump(view));
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => Directory.Exists(holder) && RecycleBin.Holding(holder) is null, Ui.MoveTimeout), "holder was not put back");
        }

        Assert.AreEqual(holderIdentity, MoveFixture.Identity(holder), "holder came back as a different folder");
        Assert.AreEqual(x1Hash, MoveFixture.Hash(x1), "a file inside holder changed");
    }

    // ---- (b) no move while a duplicates search runs -------------------------------------------------------------------

    private const string OldName = "big-old.bin";

    /// <summary>A tree whose duplicates search takes a while: three sparse files of <paramref name="length"/> (allocated
    /// about 0, all zeros, so every byte is compared and nothing large is written), a 101 MB file modified three years ago
    /// (a Large and old row and a map tile). In the Defender-excluded profile test folder
    /// (allowed root (a), the duplicates' home-item root); nothing in it is moved.</summary>
    private static Fixture SlowSearchTree(string name, long length)
    {
        var fixture = new Fixture(name, Fixture.ProfileBaseFolder());
        fixture.SparseFile(@"s1\slow.dat", length);
        fixture.SparseFile(@"s2\slow.dat", length);
        fixture.SparseFile(@"s3\slow.dat", length);
        fixture.File(OldName, 101 * MB);
        fixture.SetModified(OldName, DateTime.UtcNow.AddYears(-3));
        return fixture;
    }

    private static AutomationElement StartSearch(AppSession app, out Stopwatch clock)
    {
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        clock = Stopwatch.StartNew();
        var cancel = AppSession.Find(view, "CancelDuplicates", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => cancel.Current.IsEnabled, TimeSpan.FromSeconds(5)), "precondition: the search did not start (CancelDuplicates stays disabled)");
        return view;
    }

    private static bool SearchRunning(AppSession app) => Ui.FindAllAnywhere(app, "CancelDuplicates").Any(c => c.Current.IsEnabled);

    /// <summary>Opens Large and old, ticks the old row's LargeOldSelect box (ASP-10: the tick boxes replace the selection;
    /// a box ticked already stays ticked) and returns MoveLargeOld (the view stays open).</summary>
    private static AutomationElement SelectOldRow(AppSession app)
    {
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        AutomationElement? Box() => Table.ControlWith(view, "LargeOldSelect", OldName);
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (box = Box()) is not null, Ui.ElementTimeout),
            "precondition: no Large and old row for " + OldName + ": " + Table.Dump(view, "LargeOldSelect"));
        Table.Tick(box!, Box, "the old row's LargeOldSelect");
        return AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout);
    }

    /// <summary>While the search runs: the tile menu's Move (on a movable tile), MoveLargeOld with a row selected and the
    /// Duplicates Move are all disabled, and stay so for <see cref="Ui.NeverWatch"/>; the search must still be running
    /// when the checks end, or they prove nothing.</summary>
    private static void AssertMoveControlsBlocked(AppSession app, Stopwatch clock)
    {
        Ui.BackToOverview(app, "DuplicatesView");
        Assert.IsFalse(Ui.TileMoveEnabled(app, Ui.Map(app), OldName), "the tile menu's Move to Recycle Bin is enabled while a duplicates search runs");

        var moveLargeOld = SelectOldRow(app);
        Assert.IsFalse(AppSession.WaitUntil(() => moveLargeOld.Current.IsEnabled, Ui.NeverWatch), "MoveLargeOld is enabled with a row selected while a duplicates search runs");
        Ui.BackToOverview(app, "LargeOldView");

        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Assert.IsFalse(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "the Duplicates Move is enabled while the search runs");
        Assert.IsTrue(SearchRunning(app),
            $"the search ended after {clock.Elapsed.TotalSeconds:F1} s, before the checks did: they prove nothing; make the sparse files longer");
        Console.WriteLine($"checks done {clock.Elapsed.TotalSeconds:F1} s into the search");
    }

    /// <summary>After the search: the tile menu's Move and MoveLargeOld (row selected) are enabled again.</summary>
    private static void AssertMoveControlsBack(AppSession app)
    {
        Ui.BackToOverview(app, "DuplicatesView");
        Assert.IsTrue(Ui.TileMoveEnabled(app, Ui.Map(app), OldName), "the tile menu's Move to Recycle Bin stays disabled after the duplicates search");
        var moveLargeOld = SelectOldRow(app);
        Assert.IsTrue(AppSession.WaitUntil(() => moveLargeOld.Current.IsEnabled, TimeSpan.FromSeconds(3)), "MoveLargeOld stays disabled with a row selected after the duplicates search");
        Ui.BackToOverview(app, "LargeOldView");
    }

    // Review round 1, MAJOR "a tree reader could run while ScanTree.Remove changes the tree" (the fix: the move controls wait
    // for a duplicates search): while a search runs, the map tile menu's Move, MoveLargeOld (with a row selected) and the
    // Duplicates Move are disabled; after CancelDuplicates the first two are enabled again. Nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    public void MoveControlsAreDisabledWhileADuplicatesSearchRunsAndComeBackAfterCancel()
    {
        using var fixture = SlowSearchTree("ui-search-cancel", 64 * GB);
        using var app = AppSession.Start("--scan", fixture.Path);
        var clock = new Stopwatch();
        Step(clock, "1 the scan finishes", () => Ui.WaitForScan(app));
        Step(clock, "2 the search starts", () => StartSearch(app, out clock));
        Step(clock, "3 Overview from Duplicates while the search runs", () => Ui.BackToOverview(app, "DuplicatesView"));
        Step(clock, "4 the tile Move is disabled while the search runs", () =>
            Assert.IsFalse(Ui.TileMoveEnabled(app, Ui.Map(app), OldName), "step 4: the tile menu's Move to Recycle Bin is enabled while a duplicates search runs"));
        AutomationElement moveLargeOld = null!;
        Step(clock, "5 the old row is selected in Large and old", () => moveLargeOld = SelectOldRow(app));
        Step(clock, "6 MoveLargeOld stays disabled while the search runs", () =>
            Assert.IsFalse(AppSession.WaitUntil(() => moveLargeOld.Current.IsEnabled, Ui.NeverWatch), "step 6: MoveLargeOld is enabled with a row selected while a duplicates search runs"));
        Step(clock, "7 Overview from Large and old while the search runs", () => Ui.BackToOverview(app, "LargeOldView"));
        AutomationElement view = null!;
        Step(clock, "8 Duplicates opens again", () => view = Ui.OpenView(app, "Duplicates", "DuplicatesView"));
        Step(clock, "9 the Duplicates Move is disabled while the search runs", () =>
            Assert.IsFalse(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "step 9: the Duplicates Move is enabled while the search runs"));
        Step(clock, "10 the search still runs when the checks end", () =>
            Assert.IsTrue(SearchRunning(app), $"step 10: the search ended after {clock.Elapsed.TotalSeconds:F1} s, before the checks did: they prove nothing; make the sparse files longer"));
        Step(clock, "11 CancelDuplicates is pressed", () => Ui.Click(Ui.FindAnywhere(app, "CancelDuplicates", Ui.ElementTimeout)));
        Step(clock, "12 the search stops after Cancel", () =>
            Assert.IsTrue(AppSession.WaitUntil(() => !SearchRunning(app), Ui.ElementTimeout), "step 12: the search did not stop after Cancel"));
        Step(clock, "13 the status says the search was cancelled", () =>
        {
            var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.DuplicatesCancelled, Ui.ElementTimeout), $"step 13: status \"{status.Current.Name}\", expected \"{Text.DuplicatesCancelled}\"");
        });
        Step(clock, "14 Overview from Duplicates after Cancel", () => Ui.BackToOverview(app, "DuplicatesView"));
        Step(clock, "15 the tile Move is enabled again after Cancel", () =>
            Assert.IsTrue(Ui.TileMoveEnabled(app, Ui.Map(app), OldName), "step 15: the tile menu's Move to Recycle Bin stays disabled after the duplicates search was cancelled"));
        Step(clock, "16 the old row is selected in Large and old again", () => moveLargeOld = SelectOldRow(app));
        Step(clock, "17 MoveLargeOld is enabled again after Cancel", () =>
            Assert.IsTrue(AppSession.WaitUntil(() => moveLargeOld.Current.IsEnabled, TimeSpan.FromSeconds(3)), "step 17: MoveLargeOld stays disabled with a row selected after the duplicates search was cancelled"));
        Step(clock, "18 Overview from Large and old after Cancel", () => Ui.BackToOverview(app, "LargeOldView"));
    }

    /// <summary>Runs one numbered step of a test and prints when it ended; any failure inside it (an assertion of the step
    /// or of a helper, or a UI Automation error) fails the test with the step's name in front, so a flaky failure names
    /// its step. Nothing is caught that would let the test pass.</summary>
    private static void Step(Stopwatch clock, string name, Action action)
    {
        try
        {
            action();
        }
        catch (AssertInconclusiveException)
        {
            throw;
        }
        catch (AssertFailedException e)
        {
            throw new AssertFailedException($"[step {name}, {clock.Elapsed.TotalSeconds:F1} s into the search] {e.Message}", e);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or NullReferenceException)
        {
            throw new AssertFailedException($"[step {name}, {clock.Elapsed.TotalSeconds:F1} s into the search] {e.GetType().Name}: {e.Message}", e);
        }

        Console.WriteLine($"step {name}: done, {clock.Elapsed.TotalSeconds:F1} s into the search");
    }

    // The same fix, for a search that ends on its own: disabled while it runs; afterwards the tile menu's Move and
    // MoveLargeOld are enabled again, and the Duplicates Move is enabled once a copy of the group found is ticked. (While a
    // search runs the view holds no group, so the Duplicates Move cannot be told apart from "nothing ticked" there.)
    [TestMethod]
    [TestCategory("Console")]
    public void MoveControlsComeBackWhenADuplicatesSearchEnds()
    {
        using var fixture = SlowSearchTree("ui-search-end", 16 * GB);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        StartSearch(app, out var clock);
        AssertMoveControlsBlocked(app, clock);

        Assert.IsTrue(AppSession.WaitUntil(() => !SearchRunning(app), TimeSpan.FromMinutes(5)), "the search did not end within 5 minutes");
        Console.WriteLine($"the search ended after {clock.Elapsed.TotalSeconds:F1} s");
        var view = Ui.FindAnywhere(app, "DuplicatesView", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count == 1, Ui.ElementTimeout), "the search found no group of the three sparse files");
        AssertMoveControlsBack(app);

        view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        var first = Copies(view).FirstOrDefault(c => c.Current.IsEnabled);
        Assert.IsNotNull(first, "no enabled DuplicateCopy: " + CopiesDump(view));
        ((TogglePattern)first.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "the Duplicates Move stays disabled with a copy ticked after the search");
        ((TogglePattern)first.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    }

    // ---- (c) Free history by session ----------------------------------------------------------------------------------

    /// <summary>The History entry rows' names (ASP-10: the innermost list rows, each joining Item, Folder, Result and Reason).</summary>
    private static List<string> EntryRows(AutomationElement history) => Table.LeafRowNames(history);

    // Review round 1, MINOR "the Free cutoff by session" (docs/spec/06: Free shows the last 30 days; the app: a session is
    // shown when its first entry is within 30 days, and then all of it). The test writes its own journal (docs/spec/05
    // lines; no item exists and none can be put back) and puts the user's journal back afterwards:
    //   old:    trashed 40 days ago, restoreFailed 1 hour ago     → not shown, not even its recent line;
    //   edge:   trashed and skipped 29 days ago, restored 3 hours ago → shown with all three entries, "…, 2 items";
    //   recent: skipped 1 hour ago                                → shown, first (newest first).
    [TestMethod]
    [TestCategory("Console")]
    public void FreeHistoryHidesASessionThatStartedOver30DaysAgoAndShowsAllOfANewerOne()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(profile, "LeanUiMove-history30-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPath = Path.Combine(root, "old", "gone-long-ago.bin");
        var edgeTrashed = Path.Combine(root, "edge", "first.bin");
        var edgeSkipped = Path.Combine(root, "edge", "second.bin");
        var recentPath = Path.Combine(root, "recent", "now.bin");
        var now = DateTimeOffset.UtcNow;
        var edgeStart = now.AddDays(-29);
        var recentTime = now.AddHours(-1);
        string NewId() => Guid.NewGuid().ToString("N");
        var oldTrashedId = NewId();
        var edgeTrashedId = NewId();
        var lines = new[]
        {
            JournalBackup.Line(oldTrashedId, "s-old", now.AddDays(-40), "map", oldPath, "trashed", bytes: 4096),
            JournalBackup.Line(edgeTrashedId, "s-edge", edgeStart, "largeOld", edgeTrashed, "trashed", bytes: 4096),
            JournalBackup.Line(NewId(), "s-edge", edgeStart.AddMinutes(1), "largeOld", edgeSkipped, "skipped", reason: "uitest reason"),
            JournalBackup.Line(NewId(), "s-edge", now.AddHours(-3), "largeOld", edgeTrashed, "restored", refersTo: edgeTrashedId),
            JournalBackup.Line(NewId(), "s-recent", recentTime, "duplicates", recentPath, "skipped", reason: "uitest reason"),
            JournalBackup.Line(NewId(), "s-old", now.AddHours(-1), "map", oldPath, "restoreFailed", reason: "uitest reason", refersTo: oldTrashedId),
        };

        using var backup = new JournalBackup();
        JournalBackup.Write(lines);
        using (var app = AppSession.Start())
        {
            var history = Ui.OpenView(app, "History", "HistoryView");
            Assert.IsTrue(AppSession.WaitUntil(() => Ui.AnyNameContains(history, Text.HistoryFreeLimit), Ui.ElementTimeout), "precondition: Free (no limit line): " + Ui.Dump(history));
            List<AutomationElement> sessions = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (sessions = AppSession.FindAll(history, "HistorySession")).Count >= 2, Ui.ElementTimeout), "fewer than two sessions: " + Ui.Dump(history));
            System.Threading.Thread.Sleep(500); // a third session, if any, is realised by then
            sessions = AppSession.FindAll(history, "HistorySession");
            var names = sessions.Select(s => s.Current.Name).ToList();
            var entries = EntryRows(history);
            var dump = "sessions [" + string.Join(" | ", names) + "], entries [" + string.Join(" | ", entries) + "]";

            Assert.IsFalse(entries.Any(e => Table.Holds(e, oldPath)), "a session that started 40 days ago is shown (by its recent line): " + dump);
            Assert.AreEqual(2, names.Count, "expected the recent and the 29-day session only: " + dump);

            var edgeName = Text.HistorySessionName(edgeStart.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Text.LargeOld, 2);
            Assert.AreEqual(edgeName, names[1], "the 29-day session (second, newest first): " + dump);
            var recentName = Text.HistorySessionName(recentTime.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Text.Duplicates, 1);
            Assert.AreEqual(recentName, names[0], "the recent session (first, newest first): " + dump);
            const string reason = "uitest reason";
            foreach (var (path, result, why) in new (string Path, string Result, string? Reason)[]
            {
                (edgeTrashed, Text.ResultTrashed, null),
                (edgeSkipped, Text.ResultSkipped, reason),
                (edgeTrashed, Text.ResultRestored, null),
                (recentPath, Text.ResultSkipped, reason),
            })
            {
                Assert.AreEqual(1, entries.Count(e => Table.IsHistoryRow(e, path, result, why, reason)),
                    $"not one entry row holds {path}, its folder, \"{result}\" and {(why is null ? "no reason" : "\"" + why + "\"")}: {dump}");
            }

            Assert.AreEqual(4, entries.Count, "entries shown: " + dump);
            Assert.AreEqual(0, Ui.FindAllAnywhere(app, "PutBackEntry").Count, "a Put back is offered for an entry that is back or was never moved: " + dump);
        }

        Assert.IsFalse(Directory.Exists(root), "the test's paths must not exist");
    }
}
