using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// ASP-17 phase C (intent/2026-10-20-asp-17-suggest.md): Suggest in Duplicates. Free (Console, Release): disabled with its Pro label, a
// click does nothing. Pro (ProDebug, Debug build started with --pro): Suggest runs the Newest rule on the current tab, exactly as
// Select by rule > Newest modified (the oracle is the fixture table of this class, and the rule's own ticks in the same session), a
// display-only copy is never ticked, each ticked copy shows Text.WhyOlderCopy in a Why column, the over-100 question is the rules'
// Text.RuleConfirm, Suggest is off while a search runs or a move is confirmed, and the Why column goes with Clear selection and with a
// new search. Nothing is moved: the one confirmation opened is cancelled.
public sealed partial class DuplicatesHelpersTests
{
    private static AppSession StartProMain() => ProApp.StartPro("--scan", fx!.Path);

    private static AppSession StartProBig() => ProApp.StartPro("--scan", BigTree.Value.Path);

    private static HashSet<string> TickedSet(List<Cell> cells, IEnumerable<string> paths) =>
        paths.Where(p => CellOf(cells, p)?.Ticked == true).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The status line after Suggest is Text.SuggestTicked(copies) in every view, Duplicates included (contract, settled).</summary>
    private static void AssertSuggestStatus(AppSession app, int copies, string where) =>
        Assert.IsTrue(AppSession.WaitUntil(() => Status(app) == Text.SuggestTicked(copies), Ui.ElementTimeout),
            $"{where}: the status line is \"{Status(app)}\", expected \"{Text.SuggestTicked(copies)}\"");

    /// <summary>Waits for the Why column and for the rows to show it.</summary>
    private static void WaitForWhy(AutomationElement view, string where) =>
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), where + ": no Why column");

    /// <summary>Every ticked copy shows Text.WhyOlderCopy in its row, no unticked copy does.</summary>
    private static void AssertWhyOnTickedCopies(List<Cell> cells, string where)
    {
        foreach (var cell in cells)
        {
            Assert.AreEqual(cell.Ticked, cell.Row.Contains(Text.WhyOlderCopy, StringComparison.Ordinal),
                $"{where}: the row \"{cell.Row}\" is {(cell.Ticked ? "ticked and should" : "unticked and should not")} show \"{Text.WhyOlderCopy}\"");
        }
    }

    // ---- Free ----------------------------------------------------------------------------------------------------------------

    // In Free Suggest (beside Select by rule, after it) is disabled with the quiet Pro label; a mouse click does nothing: nothing is ticked,
    // no confirmation opens, no Why column, the status line is as it was.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SuggestIsDisabledWithItsProLabelAndAClickDoesNothing()
    {
        ProApp.RequireReleaseApp();
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var button = SuggestUi.AssertFree(view, "Duplicates");
        var rule = RuleButton(view).Current.BoundingRectangle;
        var suggest = button.Current.BoundingRectangle;
        Assert.IsTrue(suggest.Left >= rule.Right - 1, $"Suggest {suggest} is not after Select by rule {rule}");
        var status = Status(app);
        var windows = SuggestUi.Windows(app);
        AssertNothingTicked(view, "before the click");

        SuggestUi.MouseClick(button);
        System.Threading.Thread.Sleep(Ui.NeverWatch);

        AssertNothingTicked(view, "after a click on the disabled Suggest");
        AssertNoRuleDialog(app, "a click on the disabled Suggest");
        Assert.IsFalse(SuggestDialog.Shown(app), "a click on the disabled Suggest opened a Suggest confirmation");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a click on the disabled Suggest showed a Why column");
        Assert.AreEqual(status, Status(app), "a click on the disabled Suggest changed the status line");
        Assert.AreEqual(windows, SuggestUi.Windows(app), "a click on the disabled Suggest opened or changed a window");
        Assert.IsFalse(SuggestUi.Button(view).Current.IsEnabled, "Suggest got enabled by a click");
    }

    // ---- Pro -----------------------------------------------------------------------------------------------------------------

    // Contract "Duplicates": Suggest runs the Newest rule on the current tab (All files here), as Select by rule > Newest modified: the
    // fixture table gives the ticks (the newest copy of each group is kept, the protected h-copy being the newest of its group); each ticked
    // copy shows Text.WhyOlderCopy; and the rule itself, run after Clear selection in the same session, ticks the same copies.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void SuggestKeepsTheNewestCopyOfEveryGroupAndTicksTheOthersLikeTheNewestRule()
    {
        ProApp.RequireDebugApp();
        using var settings = ProtectGuarded();
        using var app = StartProMain();
        var view = OpenDuplicates(app);
        var (ticked, touched) = TicksOf(Newest, Groups, null);
        Assert.IsTrue(ticked.Count >= 8, "precondition: the rule ticks the other copies");
        var button = SuggestUi.AssertPro(view, "Duplicates");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column before Suggest");
        AssertNothingTicked(view, "before Suggest");

        Ui.Click(button);

        WaitForWhy(view, "Suggest");
        AssertSuggestStatus(app, ticked.Count, "Suggest");
        var cells = Cells(view);
        AssertGroups(cells, Groups, ticked, Newest, null, "Suggest in All files");
        AssertWhyOnTickedCopies(cells, "Suggest in All files");
        var bySuggest = TickedSet(cells, PathsOf(Groups));
        Assert.AreEqual(ticked.Count, bySuggest.Count, "ticks after Suggest: " + Dump(cells));
        Assert.IsTrue(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "Move is disabled with copies ticked");

        ChooseRule(app, view, "ClearSelection");
        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "the Why column stays after Clear selection");
        AssertNothingTicked(view, "after Clear selection");
        ChooseRule(app, view, Newest.MenuId);
        AssertStatus(app, RuleStatus(ticked.Count, touched), "Select by rule > Newest modified");
        var byRule = TickedSet(Cells(view), PathsOf(Groups));
        CollectionAssert.AreEquivalent(bySuggest.ToList(), byRule.ToList(), "Suggest and Select by rule > Newest modified tick different copies");
    }

    // Contract: Suggest works on the current tab only: in the Video tab it ticks the video groups' older copies and shows their Why; the
    // Document tab is untouched; All files then shows the same ticks.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void SuggestWorksOnTheCurrentTabOnly()
    {
        ProApp.RequireDebugApp();
        using var settings = ProtectGuarded();
        using var app = StartProMain();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeVideo);
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var (ticked, touched) = TicksOf(Newest, video, null);

        Ui.Click(SuggestUi.AssertPro(view, "Duplicates"));

        WaitForWhy(view, "Suggest in the Video tab");
        AssertSuggestStatus(app, ticked.Count, "Suggest in the Video tab");
        var cells = Cells(view);
        AssertGroups(cells, video, ticked, Newest, null, "Suggest in the Video tab");
        AssertWhyOnTickedCopies(cells, "Suggest in the Video tab");

        ChooseTab(view, Text.TypeDocument);
        var documents = Groups.Where(g => g.Tab == Text.TypeDocument).ToArray();
        AssertGroups(Cells(view), documents, [], null, null, "the Document tab is untouched");

        ChooseTab(view, Text.AllFiles);
        var everything = Cells(view);
        AssertGroups(everything, video, ticked, Newest, null, "the Video ticks in All files");
        AssertGroups(everything, Groups.Except(video), [], null, null, "the other tabs in All files");
    }

    // Contract "Display-only rows are never ticked": an OLDER copy in a protected folder stays unticked (its box disabled) and shows no
    // Why; the copy older than the kept one beside it is ticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void AProtectedOlderCopyIsNeverTickedByASuggest()
    {
        ProApp.RequireDebugApp();
        using var tree = new MoveFixture("suggest-protected");
        var now = DateTime.UtcNow;
        foreach (var (rel, days) in new[] { (@"fresh\f.bin", 1), (@"guarded\f.bin", 20), (@"stale\f.bin", 40) })
        {
            tree.File(rel, MB + (4 * KB), 5);
            tree.SetModified(rel, now.AddDays(-days));
        }

        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders(tree.Sub("guarded"));
        using var app = ProApp.StartPro("--scan", tree.Path);
        var view = OpenDuplicates(app);

        Ui.Click(SuggestUi.AssertPro(view, "Duplicates"));

        WaitForWhy(view, "Suggest");
        var cells = Cells(view);
        var stale = CellOf(cells, tree.Sub(@"stale\f.bin"));
        var guarded = CellOf(cells, tree.Sub(@"guarded\f.bin"));
        var fresh = CellOf(cells, tree.Sub(@"fresh\f.bin"));
        Assert.IsTrue(stale?.Ticked == true, "the oldest, unprotected copy is not ticked: " + Dump(cells));
        Assert.IsTrue(guarded is { Ticked: false, TickEnabled: false }, "the protected copy is ticked or its box is enabled: " + Dump(cells));
        Assert.IsTrue(fresh?.Ticked == false, "the newest copy is ticked: " + Dump(cells));
        AssertWhyOnTickedCopies(cells, "protected copy");
    }

    // Contract "The confirmation" (Duplicates keeps the rules' own text): Suggest would tick 153 copies in 7 groups, 47 of them program
    // files, so it asks first with Text.RuleConfirm(153, 7, 47) and Tick / Cancel. Cancel leaves every tick (none), no Why column and the
    // status line as it was; again, Tick: 153 copies ticked, each with its Why.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void SuggestAsksFirstAboveOneHundredCopiesCancelChangesNothingAndTickTicksThemWithTheirWhy()
    {
        ProApp.RequireDebugApp();
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartProBig();
        var view = OpenDuplicates(app);
        var (ticked, groups, programFiles) = BigTicks(NewestOf);
        Assert.AreEqual((153, 7, 47), (ticked.Count, groups, programFiles), "precondition: what the Newest rule ticks");
        var button = SuggestUi.AssertPro(view, "Duplicates");
        var statusBefore = Status(app);
        var text = Ui.Copy("RuleConfirm", ticked.Count, groups, programFiles);

        Ui.Click(button);
        var dialog = WaitForRuleDialog(app, text);
        Assert.AreEqual(statusBefore, Status(app), "the status line changed while the confirmation is open");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column while the confirmation is open");
        PressInDialog(app, dialog, Text.Cancel);
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(0, Cells(view).Count(c => c.Ticked), "Cancel left a copy ticked");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "Cancel showed a Why column");
        Assert.AreEqual(statusBefore, Status(app), "the status line changed after Cancel");

        Ui.Click(SuggestUi.Button(view));
        PressInDialog(app, WaitForRuleDialog(app, text), "Tick");
        WaitForWhy(view, "Tick");
        AssertSuggestStatus(app, ticked.Count, "Tick");
        var cells = Cells(view);
        Assert.AreEqual(153, cells.Count(c => c.Ticked), "ticks after Tick");
        foreach (var path in ticked)
        {
            Assert.IsTrue(CellOf(cells, path)?.Ticked == true, path + " is not ticked after Tick");
        }

        AssertWhyOnTickedCopies(cells, "after Tick");
    }

    // Contract "When the Why column goes": Clear selection, and a new search. Suggest, then Clear selection: no Why, nothing ticked;
    // Suggest again, then FindDuplicates: the groups are listed again with no Why column and nothing ticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void TheWhyColumnGoesWithClearSelectionAndWithANewSearch()
    {
        ProApp.RequireDebugApp();
        using var settings = ProtectGuarded();
        using var app = StartProMain();
        var view = OpenDuplicates(app);
        Ui.Click(SuggestUi.AssertPro(view, "Duplicates"));
        WaitForWhy(view, "Suggest");

        ChooseRule(app, view, "ClearSelection");
        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "the Why column stays after Clear selection");
        AssertNothingTicked(view, "after Clear selection");

        Ui.Click(SuggestUi.Button(view));
        WaitForWhy(view, "Suggest again");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "the Why column stays after a new search");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0 && AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout).Current.IsEnabled, Ui.DuplicatesTimeout),
            "the new search listed no group");
        AssertNothingTicked(view, "after a new search");
    }

    // Contract "Busy states": Suggest is off while a move is being confirmed. One copy is ticked by hand, Move opens the confirmation (it
    // is cancelled); Suggest is disabled while it is open and enabled again after.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void SuggestIsOffWhileAMoveIsBeingConfirmed()
    {
        ProApp.RequireDebugApp();
        using var settings = ProtectGuarded();
        using var app = StartProMain();
        var view = OpenDuplicates(app);
        SuggestUi.AssertPro(view, "Duplicates");
        // A copy that is realised on screen (the groups are sorted largest first, so a fixed group may be below the fold): the first
        // enabled tick box of the list, found again by its row name.
        var first = Table.Controls(view, "DuplicateCopy").First(b => b.Current.IsEnabled && !b.Current.IsOffscreen);
        var rowName = Table.RowName(first);
        Table.Tick(first, () => Table.Controls(view, "DuplicateCopy").FirstOrDefault(b => Table.RowName(b) == rowName), "a copy, by hand");
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move stays disabled with a copy ticked");

        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Assert.IsFalse(SuggestUi.EnabledNow(SuggestUi.TryButton(view)), "Suggest is enabled while the move is being confirmed");

        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(view)), Ui.ElementTimeout), "Suggest stays disabled after the confirmation was cancelled");
    }

    // Contract "One line per row" (ASP-14 lesson), at the default window size: while the reasons show, every copy row is one line high by
    // rectangle after the layout settles; the long Name (about 50 characters), the long Folder and the Why are trimmed with an ellipsis, each
    // with its full text as tooltip. Four groups of two copies with long names.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void EveryCopyRowIsOneLineHighWhileTheReasonsShow()
    {
        ProApp.RequireDebugApp();
        using var tree = new MoveFixture("suggest-lines");
        const string Name = "a-duplicate-file-with-a-rather-long-name-for-the-test";
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var folder = $"group-{i}-with-a-long-folder-name-for-wrapping";
            tree.File($@"{folder}\new\{Name}-{i}.bin", MB + (i * 8 * KB) + (4 * KB), 20 + i);
            tree.File($@"{folder}\old\{Name}-{i}.bin", MB + (i * 8 * KB) + (4 * KB), 20 + i);
            tree.SetModified($@"{folder}\new\{Name}-{i}.bin", now.AddDays(-1));
            tree.SetModified($@"{folder}\old\{Name}-{i}.bin", now.AddDays(-40));
        }

        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = ProApp.StartPro("--scan", tree.Path);
        var view = OpenDuplicates(app);
        Ui.Click(SuggestUi.AssertPro(view, "Duplicates"));
        WaitForWhy(view, "Suggest");

        SuggestUi.AssertOneLineRows(app, view, "DuplicateCopy", "Duplicates with the reasons", atLeast: 3);
        var oldPath = tree.Sub($@"group-0-with-a-long-folder-name-for-wrapping\old\{Name}-0.bin");
        var box = Table.ControlFor(view, "DuplicateCopy", oldPath);
        Assert.IsNotNull(box, "no row for " + oldPath);
        SuggestUi.AssertTooltipHolds(box, Name + "-0.bin", "the Name cell");
        SuggestUi.AssertTooltipHolds(box, Text.WhyOlderCopy, "the Why cell");
    }

    // Contract "Busy states": Suggest is off while a duplicates search runs. Held by comparing three identical 300 MB files; Suggest is
    // sampled every 50 ms while CancelDuplicates is enabled. Inconclusive when the button is never there to be sampled or the search
    // ends too soon.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void SuggestIsOffWhileTheDuplicatesSearchRuns()
    {
        ProApp.RequireDebugApp();
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("suggest-search");
        foreach (var name in new[] { "a", "b", "c" })
        {
            tree.File($@"{name}\big.dat", 300 * MB, 7);
        }

        using var app = ProApp.StartPro("--scan", tree.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        var cancel = AppSession.Find(view, "CancelDuplicates", Ui.ElementTimeout);
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        int samples = 0, enabled = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < Ui.DuplicatesTimeout)
        {
            if (SuggestUi.EnabledNow(cancel))
            {
                if (SuggestUi.TryButton(view) is { } button)
                {
                    samples++;
                    enabled += SuggestUi.EnabledNow(button) ? 1 : 0;
                }
            }
            else if (samples > 0 || watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                break;
            }

            System.Threading.Thread.Sleep(50);
        }

        Assert.AreEqual(0, enabled, $"Suggest was enabled in {enabled} of {samples} samples while the search ran");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0 && SuggestUi.EnabledNow(SuggestUi.TryButton(view)), Ui.DuplicatesTimeout),
            "Suggest is not enabled once the search is over");
        if (samples == 0)
        {
            Assert.Inconclusive("Suggest is not in the view's UI Automation tree while the search runs, or the search ended before it could be sampled");
        }
    }
}
