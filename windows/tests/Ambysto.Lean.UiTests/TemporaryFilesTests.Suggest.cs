using System;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// ASP-17 phase C (intent/2026-10-20-asp-17-suggest.md): Suggest in Temporary files, Pro, in a Debug build started with --pro
// (TestCategory ProDebug only). The fixtures are the test's own LeanUiSuggest-* children of this user's real %LOCALAPPDATA%\Temp,
// aged relative to now and to the one-month line (SuggestTempFixture); other old children of Temp may be ticked too, so every
// check is about the fixture's items, the Why of every ticked row, and the status line counting the ticked rows.
public sealed partial class TemporaryFilesTests
{
    private static void WaitForFixture(AutomationElement view, SuggestTempFixture temp)
    {
        foreach (var item in temp.Suggested.Concat(temp.NotSuggested))
        {
            WaitForItem(view, item.Name);
        }
    }

    private static AutomationElement[] Boxes(AutomationElement view) => [.. Table.Controls(view, "TempItem")];

    private static AutomationElement[] TickedBoxes(AutomationElement view) => [.. Boxes(view).Where(b => State(b) == ToggleState.On)];

    // Contract "Temporary files": Suggest first unticks every row, then ticks the rows whose newest write is older than one month
    // (a folder by its newest file); a Why column (Text.ColumnWhy) shows Text.WhyOldTemp(newest write) for each ticked row and
    // nothing for the others; display-only rows (the system-wide Windows\Temp row) are never ticked; the status line says
    // Text.SuggestTicked(count) of the ticked rows; Move is enabled. A row ticked by hand that Suggest does not suggest is unticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestTicksTheRowsOlderThanAMonthWithTheirWhyAndNoOthers()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture();
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        WaitForFixture(view, temp);
        var button = SuggestUi.AssertPro(view, "Temporary files");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column before Suggest");
        Assert.IsTrue(Boxes(view).All(b => State(b) == ToggleState.Off), "a row is ticked before Suggest: " + ItemNames(view));
        Table.Tick(ItemOf(view, temp.Under.Name)!, () => ItemOf(view, temp.Under.Name), "Under, by hand");

        Ui.Click(button);

        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), FindTimeout), "no Why column after Suggest: " + ItemNames(view));
        Assert.AreEqual(1, SuggestUi.WhyHeaders(view).Count, "more than one Why column");
        foreach (var item in temp.Suggested)
        {
            var box = ItemOf(view, item.Name);
            Assert.IsNotNull(box, "no row for " + item.Name);
            Assert.IsTrue(AppSession.WaitUntil(() => State(ItemOf(view, item.Name)!) == ToggleState.On, Ui.ElementTimeout), $"{item.Name} (newest write {item.NewestUtc:u}) is not ticked: {ItemNames(view)}");
            Assert.IsTrue(Table.RowName(box).Contains(Text.WhyOldTemp(item.NewestUtc), StringComparison.Ordinal),
                $"the row of {item.Name} does not show \"{Text.WhyOldTemp(item.NewestUtc)}\": \"{Table.RowName(box)}\"");
        }

        var prefix = SuggestUi.TempWhyPrefix();
        foreach (var item in temp.NotSuggested)
        {
            var box = ItemOf(view, item.Name)!;
            Assert.AreEqual(ToggleState.Off, State(box), $"{item.Name} (newest write {item.NewestUtc:u}) is ticked although it is newer than a month (or was ticked by hand and kept)");
            Assert.IsFalse(Table.RowName(box).Contains(prefix, StringComparison.Ordinal), $"the row of {item.Name} shows a Why: \"{Table.RowName(box)}\"");
        }

        var ticked = TickedBoxes(view);
        Assert.IsTrue(ticked.All(b => Table.RowName(b).Contains(prefix, StringComparison.Ordinal)), "a ticked row has no Why: " + ItemNames(view));
        Assert.IsTrue(Boxes(view).Where(b => State(b) == ToggleState.Off).All(b => !Table.RowName(b).Contains(prefix, StringComparison.Ordinal)), "an unticked row shows a Why: " + ItemNames(view));

        var windowsTemp = Boxes(view).FirstOrDefault(b => Table.RowName(b).Contains(@"Windows\Temp", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(windowsTemp, "no row for the system-wide Windows\\Temp: " + ItemNames(view));
        Assert.IsFalse(windowsTemp.Current.IsEnabled, "the Windows\\Temp row is not display-only");
        Assert.AreEqual(ToggleState.Off, State(windowsTemp), "a display-only row is ticked");
        Assert.IsTrue(Boxes(view).Where(b => !b.Current.IsEnabled).All(b => State(b) == ToggleState.Off), "a display-only row is ticked: " + ItemNames(view));

        var expected = Text.SuggestTicked(ticked.Length);
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\" ({ticked.Length} ticked rows)");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.Find(view, "MoveTemp", Ui.ElementTimeout).Current.IsEnabled, Ui.ElementTimeout), "Move is disabled with rows ticked");
    }

    // Contract "When the Why column goes": a change of the age shows the list again, with no Why column and no row ticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void TheWhyColumnGoesWhenTheAgeChanges()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture();
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        WaitForFixture(view, temp);
        Ui.Click(SuggestUi.AssertPro(view, "Temporary files"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), FindTimeout), "no Why column after Suggest");

        ChooseAge(app, AppSession.Find(view, "AgeChoice", Ui.ElementTimeout), Text.AgeMonth);

        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "the Why column stays after the age changed");
        WaitForItem(view, temp.Over.Name);
        Assert.IsTrue(Boxes(view).All(b => State(b) == ToggleState.Off), "a row stays ticked after the age changed: " + ItemNames(view));
        Assert.IsFalse(Boxes(view).Any(b => Table.RowName(b).Contains(SuggestUi.TempWhyPrefix(), StringComparison.Ordinal)), "a row still shows a Why after the age changed");
    }

    // ASP-22: the status line "Suggest ticked <n> items" goes with the ticks: a change of the age lists again with nothing ticked, and the
    // line from before Suggest is back.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestsStatusLineGoesWhenTheAgeChanges()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture();
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        WaitForFixture(view, temp);
        var before = SuggestUi.Status(app);
        Ui.Click(SuggestUi.AssertPro(view, "Temporary files"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), FindTimeout), "no Why column after Suggest");
        var ticked = TickedBoxes(view).Length;
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.SuggestTicked(ticked), Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.SuggestTicked(ticked)}\"");

        ChooseAge(app, AppSession.Find(view, "AgeChoice", Ui.ElementTimeout), Text.AgeMonth);

        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "the Why column stays after the age changed");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == before, Ui.ElementTimeout),
            $"after the age changed the status line is \"{SuggestUi.Status(app)}\", expected the line from before Suggest, \"{before}\"");
    }

    // Contract "One line per row" (ASP-14 lesson), at the default window size: while the reasons show, every row of Temporary files is one
    // line high by rectangle after the layout settles (the long Name of about 55 characters, the Note of a display-only row and the Why
    // trimmed with an ellipsis); each of those texts keeps its full text as tooltip. Columns: Move, Name, Size 90, Last changed 110, Note
    // 200, Why 240.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void EveryRowIsOneLineHighWhileTheReasonsShow()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture();
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        WaitForFixture(view, temp);
        Ui.Click(SuggestUi.AssertPro(view, "Temporary files"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), FindTimeout), "no Why column after Suggest");
        Assert.IsTrue(AppSession.WaitUntil(() => State(ItemOf(view, temp.Long.Name)!) == ToggleState.On, Ui.ElementTimeout), "the long-named item is not ticked");

        SuggestUi.AssertOneLineRows(app, view, "TempItem", "Temporary files with the reasons", atLeast: 4);
        var box = ItemOf(view, temp.Long.Name)!;
        SuggestUi.AssertTooltipHolds(box, temp.Long.Name, "the Name cell");
        SuggestUi.AssertTooltipHolds(box, Text.WhyOldTemp(temp.Long.NewestUtc), "the Why cell");
        var windowsTemp = Boxes(view).FirstOrDefault(b => Table.RowName(b).Contains(@"Windows\Temp", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(windowsTemp, "no Windows\\Temp row");
        SuggestUi.AssertTooltipHolds(windowsTemp, Text.HintSystemWide, "the Note cell of the display-only row");
    }

    // Contract "The confirmation": Suggest would tick more than 100 rows, so it asks first with Text.SuggestConfirm(items, program
    // files) and Tick / Cancel. The counts are worked out before anything is ticked (the program files are the rows named like
    // .dll; a folder row named like .dll is ticked but never counted: the count is exactly the files). Cancel leaves every tick (none here), the list and the status line as they were, with no Why column; Suggest again, Tick:
    // the status line says Text.SuggestTicked(items) and the Why column shows.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestAsksFirstAboveOneHundredCancelChangesNothingAndTickTicks()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("bulk");
        var bulk = temp.Bulk(bin: 96, dll: 5, days: 40, folderNamedDll: true);
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        AssertTotalShape(view); // the list is read (more than a window's worth of rows: some are not realised, so none is looked for by name)
        var button = SuggestUi.AssertPro(view, "Temporary files");
        var status = SuggestUi.Status(app);
        var rows = ItemNames(view);

        Ui.Click(button);
        var (scope, text) = SuggestDialog.Wait(app);
        var (items, program) = SuggestDialog.Counts(text);
        Assert.IsTrue(items >= bulk.Count + temp.Suggested.Length, $"the confirmation counts {items} items, expected at least {bulk.Count + temp.Suggested.Length}");
        var expectedProgram = SuggestUi.ProgramFilesOlderThanAMonth(temp.TempFolder);
        Assert.IsTrue(expectedProgram >= 5, "precondition: the five .dll files are older than a month");
        Assert.AreEqual(expectedProgram, program, $"the confirmation counts {program} program files; the files named like program files and older than a month are {expectedProgram} (the folder row named .dll is not one)");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column while the confirmation is open");
        Assert.IsTrue(Boxes(view).All(b => State(b) == ToggleState.Off), "a row is ticked while the confirmation is open");
        Assert.AreEqual(status, SuggestUi.Status(app), "the status line changed while the confirmation is open");

        SuggestDialog.Press(app, scope, Text.Cancel);
        System.Threading.Thread.Sleep(500);
        Assert.IsTrue(Boxes(view).All(b => State(b) == ToggleState.Off), "Cancel left a row ticked: " + ItemNames(view));
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "Cancel showed a Why column");
        Assert.AreEqual(status, SuggestUi.Status(app), "the status line changed after Cancel");
        Assert.AreEqual(rows, ItemNames(view), "the list changed after Cancel");

        Ui.Click(SuggestUi.Button(view));
        var (scope2, text2) = SuggestDialog.Wait(app);
        Assert.AreEqual(text, text2, "the second confirmation counts differently");
        SuggestDialog.Press(app, scope2, Text.RuleConfirmTick);
        var expected = Text.SuggestTicked(items);
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, Ui.ElementTimeout), $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\"");
        Assert.IsTrue(SuggestUi.HasWhyColumn(view), "no Why column after Tick");
        Assert.IsTrue(TickedBoxes(view).Length > 0, "nothing is ticked after Tick");
        Assert.IsTrue(TickedBoxes(view).All(b => Table.RowName(b).Contains(SuggestUi.TempWhyPrefix(), StringComparison.Ordinal)), "a ticked row has no Why");
    }

    // Contract "Busy states": Suggest is off while a move is being confirmed (MoveBlocked), on again after Cancel. A row is ticked by
    // hand, Move opens the confirmation.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestIsOffWhileAMoveIsBeingConfirmed()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture();
        using var app = ProApp.StartPro();
        var view = OpenTemp(app);
        WaitForFixture(view, temp);
        SuggestUi.AssertPro(view, "Temporary files");
        Table.Tick(ItemOf(view, temp.Under.Name)!, () => ItemOf(view, temp.Under.Name), "Under, by hand");
        var move = AppSession.Find(view, "MoveTemp", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move stays disabled with a row ticked");

        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Assert.IsFalse(SuggestUi.EnabledNow(SuggestUi.TryButton(view)), "Suggest is enabled while the move is being confirmed");

        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(view)), Ui.ElementTimeout), "Suggest stays disabled after the confirmation was cancelled");
    }
}
