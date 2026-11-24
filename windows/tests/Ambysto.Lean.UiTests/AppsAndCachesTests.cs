using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-20 phase B (intent/2026-10-21-asp-20-suggest-catalog.plan.md, "Phase B: the Apps and caches view", owner decision 15):
/// the view <c>AppsView</c> (sidebar <c>AppsAndCaches</c>), with a fake <c>uv.exe</c> first on the app's PATH (<see cref="FakeUv"/>), so
/// no real tool's cache is ever cleaned. The machine's real tools (dotnet.exe is on PATH) give rows too: every test finds the uv row
/// by its Why text, and before any Clear it asserts that only the uv row is ticked. The texts the plan names that are not yet in
/// <see cref="Text"/> are asserted as the plan writes them. The report has no AutomationId in the plan: it is found by its text in the
/// view. The clean runs are recorded in the user's <c>commands.jsonl</c>, which <see cref="CommandLogBackup"/> puts back.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AppsAndCachesTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CleanTimeout = TimeSpan.FromSeconds(60);

    private const string ConfirmTitle = "Clear these caches?";
    private const string ConfirmWarning = "These files are deleted for good. They do not go to the Recycle Bin and cannot be put back.";

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static AppSession StartPro(FakeUv fake, params (string Name, string Value)[] extra) =>
        AppSession.StartWithEnvironment(fake.Variables(extra), "--pro");

    private static AutomationElement OpenApps(AppSession app) => Ui.OpenView(app, "AppsAndCaches", "AppsView");

    private static AutomationElement? UvTick(AutomationElement view) => Table.ControlWith(view, "AppsItem", Text.WhyUvCache);

    /// <summary>The fake uv row's tick box, once the view has read the apps.</summary>
    private static AutomationElement WaitForUvRow(AutomationElement view)
    {
        AutomationElement? tick = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (tick = UvTick(view)) is not null, ReadTimeout),
            "no uv row in Apps and caches (the fake uv.exe is first on PATH): " + Table.Dump(view, "AppsItem") + " " + Ui.Dump(view));
        return tick!;
    }

    private static bool Enabled(AutomationElement? element)
    {
        try
        {
            return element is not null && element.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static ToggleState? StateOf(AutomationElement? box)
    {
        try
        {
            return box is null ? null : Table.State(box);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Waits until the uv row's tick box is enabled (the row is Ready) and returns it.</summary>
    private static AutomationElement WaitForReadyUv(AutomationElement view)
    {
        WaitForUvRow(view);
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(UvTick(view)), Ui.ElementTimeout), "the uv row is not Ready (its tick box is disabled): " + Table.Dump(view, "AppsItem"));
        return UvTick(view)!;
    }

    /// <summary>Nothing is ticked, whatever the real tools on this machine give.</summary>
    private static void AssertNothingTicked(AutomationElement view, string when)
    {
        Assert.IsTrue(Table.Controls(view, "AppsItem").All(b => StateOf(b) == ToggleState.Off), when + ": a row is ticked: " + Table.Dump(view, "AppsItem"));
    }

    /// <summary>Ticks the uv row and unticks every other row, then asserts that ONLY the uv row is ticked: nothing but the fake tool
    /// may run when Clear is pressed.</summary>
    private static void TickOnlyUv(AutomationElement view)
    {
        WaitForReadyUv(view);
        foreach (var box in Table.Controls(view, "AppsItem").Where(b => !Table.RowName(b).Contains(Text.WhyUvCache, StringComparison.Ordinal)))
        {
            if (Enabled(box) && StateOf(box) == ToggleState.On)
            {
                ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            }
        }

        Table.Tick(UvTick(view)!, () => UvTick(view), "the uv row");
        var others = Table.Controls(view, "AppsItem").Where(b => !Table.RowName(b).Contains(Text.WhyUvCache, StringComparison.Ordinal)).ToList();
        Assert.IsTrue(others.All(b => StateOf(b) == ToggleState.Off), "precondition for Clear: a row other than uv is ticked: " + Table.Dump(view, "AppsItem"));
        Assert.AreEqual(ToggleState.On, StateOf(UvTick(view)), "precondition for Clear: the uv row is not ticked");
    }

    private static AutomationElement Button(AutomationElement view, string id) => AppSession.Find(view, id, Ui.ElementTimeout);

    private static AutomationElement OpenConfirm(AppSession app, AutomationElement view)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Button(view, "ClearApps")), Ui.ElementTimeout), "Clear stays disabled with the uv row ticked");
        Ui.Click(Button(view, "ClearApps"));
        return Ui.FindAnywhere(app, "ConfirmClear", Ui.ElementTimeout);
    }

    private static bool ConfirmOpen(AppSession app) => app.FindInAnyWindow("ConfirmClear") is not null;

    private static List<string> NamesOf(AutomationElement dialog)
    {
        var names = Ui.Names(dialog);
        try
        {
            names.Add(dialog.Current.Name ?? string.Empty);
        }
        catch (ElementNotAvailableException)
        {
        }

        return names;
    }

    /// <summary>The report line "uv: ..." in the view, once it shows (it has no AutomationId in the plan).</summary>
    private static string WaitForReportLine(AutomationElement view, string startsWith)
    {
        string? found = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (found = Ui.Names(view).FirstOrDefault(n => n.Contains(startsWith, StringComparison.Ordinal) && !n.Contains(Text.WhyUvCache, StringComparison.Ordinal))) is not null, CleanTimeout),
            $"no report line \"{startsWith}...\" in Apps and caches: " + Ui.Dump(view));
        return found!;
    }

    /// <summary>ASP-20 phase B review round 1, item 4: the guard before every press of Clear in the confirmation. The dialog holds exactly
    /// one ConfirmClearItem and it names the fake uv.exe's full path: nothing but the fake tool can run.</summary>
    private static void AssertConfirmHoldsOnlyTheFake(AppSession app, FakeUv fake)
    {
        var dialog = Ui.FindAnywhere(app, "ConfirmClear", Ui.ElementTimeout);
        var items = AppSession.FindAll(dialog, "ConfirmClearItem");
        Assert.AreEqual(1, items.Count, "the confirmation does not hold exactly one ConfirmClearItem: " + Ui.Dump(dialog));
        var text = items[0].Current.Name ?? string.Empty;
        StringAssert.Contains(text, fake.ToolPath, "the confirmation's only item does not name the fake uv.exe's full path: " + text);
    }

    /// <summary>Asserts the guard, then presses Clear in the open confirmation.</summary>
    private static void PressClear(AppSession app, FakeUv fake)
    {
        AssertConfirmHoldsOnlyTheFake(app, fake);
        Ui.Click(Ui.FindAnywhere(app, "ConfirmClearButton", Ui.ElementTimeout));
    }

    private static string CommandAsShown(FakeUv fake, params string[] arguments) =>
        string.Join(' ', new[] { "\"" + fake.ToolPath + "\"" }.Concat(arguments));

    // ---- Free -----------------------------------------------------------------------------------------------------------

    // Owner decision 15: Free sees the row with its size, Why, Source and Note (the location query ran), but its tick box, Clear
    // and Suggest are disabled, greyed with the Pro label; the fake tool was asked only "cache dir", never "cache clean".
    [TestMethod]
    [TestCategory("Console")]
    public void FreeShowsTheRowWithItsSizeAndOffersNoClear()
    {
        ProApp.RequireReleaseApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("free");
        using var app = AppSession.StartWithEnvironment(fake.Variables());
        var view = OpenApps(app);
        Assert.IsTrue(Ui.AnyNameContains(view, "Apps and caches"), "the view has no title \"Apps and caches\": " + Ui.Dump(view));
        var tick = WaitForUvRow(view);

        var row = Table.RowName(tick);
        StringAssert.Contains(row, ByteUnits.Format(fake.CacheBytes), "the uv row does not show the size of the fake cache: " + row);
        StringAssert.Contains(row, "Vendor's documentation", "the Source of the uv row: " + row);
        Table.AssertColumns(view, 1, Text.ColumnName, Text.ColumnSize, Text.ColumnWhy, "Source", Text.ColumnNote);

        Assert.IsFalse(tick.Current.IsEnabled, "the uv tick box is enabled in Free (decision 15)");
        Table.TryToTick(tick);
        Assert.AreEqual(ToggleState.Off, StateOf(UvTick(view)), "the uv row got ticked in Free");
        Assert.IsFalse(Button(view, "ClearApps").Current.IsEnabled, "Clear is enabled in Free");
        var suggest = Button(view, "SuggestApps");
        Assert.AreEqual(Text.Suggest, suggest.Current.Name, "the label of Suggest");
        Assert.IsFalse(suggest.Current.IsEnabled, "Suggest is enabled in Free");
        Assert.IsTrue(Ui.Names(view).Any(n => n == Text.ProLabel), "no \"" + Text.ProLabel + "\" label beside Clear and Suggest: " + Ui.Dump(view));

        SuggestUi.MouseClick(Button(view, "ClearApps"));
        SuggestUi.MouseClick(suggest);
        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.IsFalse(ConfirmOpen(app), "a click on the disabled Clear opened the confirmation");
        AssertNothingTicked(view, "after clicks on the disabled Clear and Suggest");

        var calls = fake.Calls();
        Assert.IsTrue(calls.Count > 0 && calls.All(c => c == "cache dir"), "the fake uv was asked for more than the location in Free: [" + string.Join(" | ", calls) + "]");
        Assert.AreEqual(0, fake.CleanCalls(), "the fake uv was told to clean in Free");
        Assert.IsFalse(fake.CacheIsEmpty(), "the fake cache was emptied in Free");
    }

    // Open folder works in Free, but a fake LocalAppData is not possible in the app: guide rows are covered by Core tests. When such a
    // row is present on this machine, its button must exist and be enabled (it is never pressed).
    [TestMethod]
    [TestCategory("Console")]
    public void AGuideRowsOpenFolderButtonExistsWhenSuchARowIsPresent()
    {
        ProApp.RequireReleaseApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("guide");
        using var app = AppSession.StartWithEnvironment(fake.Variables());
        var view = OpenApps(app);
        WaitForUvRow(view);
        var buttons = AppSession.FindAll(view, "AppsOpenFolder");
        if (buttons.Count == 0)
        {
            Assert.Inconclusive("no guide row with an Open folder button on this machine (no CapCut or electron-updater cache); covered by Core tests");
        }

        Assert.IsTrue(buttons.All(b => b.Current.IsEnabled && b.Current.Name == "Open folder"), "an Open folder button is disabled or misnamed in Free");
    }

    // ---- Pro ------------------------------------------------------------------------------------------------------------

    // The fake uv row is Ready with its measured size and nothing is ticked when the list is built; Suggest (enabled, no Pro label)
    // ticks every Ready row and says so in the status line.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProShowsTheFakeRowReadyAndNothingTickedAndSuggestTicksIt()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("suggest");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        var tick = WaitForReadyUv(view);
        StringAssert.Contains(Table.RowName(tick), ByteUnits.Format(fake.CacheBytes));
        AssertNothingTicked(view, "when the list was built");
        Assert.IsFalse(Enabled(Button(view, "ClearApps")), "Clear is enabled with nothing ticked");
        var suggest = Button(view, "SuggestApps");
        Assert.AreEqual(Text.Suggest, suggest.Current.Name, "the label of Suggest");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(suggest), Ui.ElementTimeout), "Suggest is disabled in a Pro app");
        Assert.IsFalse(Ui.Names(view).Any(n => n == Text.ProLabel), "a Pro label is shown in a Pro app: " + Ui.Dump(view));
        var ready = Table.Controls(view, "AppsItem").Count(Enabled);
        Assert.IsTrue(ready >= 1, "no Ready row");
        Ui.Click(suggest);
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.SuggestTicked(ready), Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.SuggestTicked(ready)}\"");
        Assert.AreEqual(ToggleState.On, StateOf(UvTick(view)), "Suggest did not tick the uv row");
        foreach (var box in Table.Controls(view, "AppsItem"))
        {
            Assert.AreEqual(Enabled(box) ? ToggleState.On : ToggleState.Off, StateOf(box), "Suggest ticked a row that is not Ready, or missed one: " + Table.Dump(view, "AppsItem"));
        }

        Assert.IsTrue(Enabled(Button(view, "ClearApps")), "Clear stays disabled with the uv row ticked");
        Assert.AreEqual(0, fake.CleanCalls(), "Suggest ran a clean");
    }

    // ASP-22: Suggest's status line goes with the ticks: opening the view again reads the rows afresh with nothing ticked, and the line
    // from before Suggest is back.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestsStatusLineGoesWhenTheRowsAreReadAgain()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("suggest-status");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        WaitForReadyUv(view);
        var before = SuggestUi.Status(app);
        var ready = Table.Controls(view, "AppsItem").Count(Enabled);
        Ui.Click(Button(view, "SuggestApps"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.SuggestTicked(ready), Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.SuggestTicked(ready)}\"");

        Ui.BackToOverview(app, "AppsView");
        view = OpenApps(app);
        WaitForReadyUv(view);

        AssertNothingTicked(view, "after the view was opened again");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == before, Ui.ElementTimeout),
            $"after the rows were read again the status line is \"{SuggestUi.Status(app)}\", expected the line from before Suggest, \"{before}\"");
        Assert.AreEqual(0, fake.CleanCalls(), "a clean ran");
    }

    // ASP-22: each view keeps its own Suggest status line. Suggest in Large and old (its own list stays on screen), Suggest in Apps and
    // caches, open Apps and caches again: its ticks and its line go, and the line that comes back is Large and old's, whose suggestion
    // still shows, not the scan's summary. Clear suggestion in Large and old then brings the scan's summary back.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void EachViewKeepsItsOwnSuggestStatusLine()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("suggest-two-views");
        using var downloads = new DownloadsFixture("two-views");
        downloads.File("two-views-setup.zip", 200 * ByteUnits.KB, daysAgo: 200, seed: 5);
        using var app = AppSession.StartWithEnvironment(fake.Variables(), "--pro", "--scan", downloads.Path);
        Ui.WaitForScan(app);
        var scanLine = SuggestUi.Status(app);
        var largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Ui.Click(SuggestUi.AssertPro(largeOld, "Large and old"));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(largeOld, "ClearSuggestion") is not null && SuggestUi.HasWhyColumn(largeOld), Ui.ElementTimeout),
            "Suggest did not show its own list in Large and old");
        var largeOldLine = SuggestUi.Status(app);
        Assert.AreNotEqual(scanLine, largeOldLine, "Suggest left the status line as it was");

        var view = OpenApps(app);
        WaitForReadyUv(view);
        var ready = Table.Controls(view, "AppsItem").Count(Enabled);
        Ui.Click(Button(view, "SuggestApps"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.SuggestTicked(ready), Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.SuggestTicked(ready)}\"");

        Ui.BackToOverview(app, "AppsView");
        view = OpenApps(app);
        WaitForReadyUv(view);
        AssertNothingTicked(view, "after Apps and caches was opened again");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == largeOldLine, Ui.ElementTimeout),
            $"after Apps and caches was opened again the status line is \"{SuggestUi.Status(app)}\", expected Large and old's \"{largeOldLine}\" (its suggestion still shows), not the scan's \"{scanLine}\"");

        Ui.BackToOverview(app, "AppsView");
        largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsNotNull(AppSession.TryFind(largeOld, "ClearSuggestion"), "Large and old's suggestion went when Apps and caches was opened again");
        Ui.Click(AppSession.Find(largeOld, "ClearSuggestion", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == scanLine, Ui.ElementTimeout),
            $"after Clear suggestion the status line is \"{SuggestUi.Status(app)}\", expected the scan's \"{scanLine}\"");
    }

    // ASP-22 review: ending the older suggestion while another view owns the status must remove it from that view's restoration history.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClearingLargeOldDoesNotRestoreAnAppsSuggestionWhoseRowsWereReadAgain()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("suggest-ended-view");
        using var downloads = new DownloadsFixture("ended-view");
        downloads.File("ended-view-setup.zip", 200 * ByteUnits.KB, daysAgo: 200, seed: 5);
        using var app = AppSession.StartWithEnvironment(fake.Variables(), "--pro", "--scan", downloads.Path);
        Ui.WaitForScan(app);
        var scanLine = SuggestUi.Status(app);

        var view = OpenApps(app);
        WaitForReadyUv(view);
        var ready = Table.Controls(view, "AppsItem").Count(Enabled);
        Ui.Click(Button(view, "SuggestApps"));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.SuggestTicked(ready), Ui.ElementTimeout),
            "Apps and caches did not show its Suggest status");
        Assert.AreEqual(ToggleState.On, StateOf(UvTick(view)), "Suggest did not tick the uv row");

        Ui.BackToOverview(app, "AppsView");
        var largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Ui.Click(SuggestUi.AssertPro(largeOld, "Large and old"));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(largeOld, "ClearSuggestion") is not null && SuggestUi.HasWhyColumn(largeOld), Ui.ElementTimeout),
            "Suggest did not show its own list in Large and old");
        var largeOldLine = SuggestUi.Status(app);
        Assert.AreNotEqual(scanLine, largeOldLine, "Large and old did not show its Suggest status");

        view = OpenApps(app);
        WaitForReadyUv(view);
        AssertNothingTicked(view, "after Apps and caches was opened again");
        Assert.AreEqual(largeOldLine, SuggestUi.Status(app), "ending Apps' suggestion replaced the still-active Large and old status");

        Ui.BackToOverview(app, "AppsView");
        largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Ui.Click(AppSession.Find(largeOld, "ClearSuggestion", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == scanLine, Ui.ElementTimeout),
            $"after both suggestions ended the status is \"{SuggestUi.Status(app)}\", expected the scan's \"{scanLine}\", not Apps' ended suggestion");
        Assert.AreEqual(0, fake.CleanCalls(), "Suggest ran a clean");
    }

    // Clear opens ConfirmClear: the title, one line with the app, its size and the exact command (the fake tool's full path, quoted
    // because the folder holds a space, and "cache clean"), the warning; nothing has run.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClearOpensTheConfirmationWithTheFullCommandAndTheWarning()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("confirm");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        TickOnlyUv(view);
        var dialog = OpenConfirm(app, view);
        try
        {
            var names = NamesOf(dialog);
            Assert.IsTrue(names.Any(n => n.Contains(ConfirmTitle, StringComparison.Ordinal)), $"no title \"{ConfirmTitle}\": {Ui.Dump(dialog)}");
            var line = names.FirstOrDefault(n => n.Contains(CommandAsShown(fake, "cache", "clean"), StringComparison.Ordinal));
            Assert.IsNotNull(line, $"no line holds the command {CommandAsShown(fake, "cache", "clean")}: {Ui.Dump(dialog)}");
            Assert.IsTrue(names.Any(n => n.Contains("uv", StringComparison.Ordinal) && n.Contains(ByteUnits.Format(fake.CacheBytes), StringComparison.Ordinal)) || line.Contains("uv", StringComparison.Ordinal),
                "no line names the app uv with its size " + ByteUnits.Format(fake.CacheBytes) + ": " + Ui.Dump(dialog));
            Assert.IsTrue(names.Any(n => n.Contains(ByteUnits.Format(fake.CacheBytes), StringComparison.Ordinal)), "the size is not in the confirmation: " + Ui.Dump(dialog));
            Assert.IsTrue(names.Any(n => n.Contains(ConfirmWarning, StringComparison.Ordinal)), $"no warning \"{ConfirmWarning}\": {Ui.Dump(dialog)}");
            Assert.AreEqual("Clear", AppSession.Find(dialog, "ConfirmClearButton", Ui.ElementTimeout).Current.Name);
            Assert.AreEqual("Cancel", AppSession.Find(dialog, "CancelClear", Ui.ElementTimeout).Current.Name);
            System.Threading.Thread.Sleep(Ui.NeverWatch);
            Assert.AreEqual(0, fake.CleanCalls(), "a clean ran before Clear was pressed in the confirmation");
        }
        finally
        {
            if (ConfirmOpen(app))
            {
                Ui.Click(Ui.FindAnywhere(app, "CancelClear", Ui.ElementTimeout));
            }
        }
    }

    // Cancel is the default and Escape cancels: the Cancel button, Escape and Enter each close the dialog and run nothing.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void CancelEscapeAndEnterRunNothing()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("cancel");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        TickOnlyUv(view);

        OpenConfirm(app, view);
        Ui.Click(Ui.FindAnywhere(app, "CancelClear", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => !ConfirmOpen(app), Ui.ElementTimeout), "Cancel did not close the confirmation");

        OpenConfirm(app, view);
        System.Threading.Thread.Sleep(500); // the dialog's opening animation hands it the keyboard
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => !ConfirmOpen(app), Ui.ElementTimeout), "Escape did not close the confirmation");

        OpenConfirm(app, view);
        System.Threading.Thread.Sleep(500);
        Keys.Press(Keys.Enter);
        Assert.IsTrue(AppSession.WaitUntil(() => !ConfirmOpen(app), Ui.ElementTimeout), "Enter did not close the confirmation (Cancel is the default)");

        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.AreEqual(0, fake.CleanCalls(), "a clean ran after Cancel, Escape or Enter: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsFalse(fake.CacheIsEmpty(), "the fake cache was emptied after Cancel, Escape or Enter");
        Assert.AreEqual(ToggleState.On, StateOf(UvTick(view)), "the uv row lost its tick after the confirmation was cancelled");
    }

    // Clear runs the fake tool once with "cache clean", the cache folder is empty, the report shows "uv: <before> before, <after> now",
    // and the rows are read again (the empty uv cache is no longer listed; the tool was asked for its location again).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClearRunsTheCommandShowsBeforeAndAfterAndReadsTheRowsAgain()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("run");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        var askedBefore = fake.Calls().Count(c => c == "cache dir");
        PressClear(app, fake);

        var expected = $"uv: {ByteUnits.Format(fake.CacheBytes)} before, {ByteUnits.Format(0)} now";
        var report = WaitForReportLine(view, "uv: ");
        StringAssert.Contains(report, expected, "the report line");
        Assert.AreEqual(1, fake.CleanCalls(), "the fake uv was not told to clean exactly once: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsTrue(fake.CacheIsEmpty(), "the fake cache folder is not empty after Clear");
        Assert.IsTrue(AppSession.WaitUntil(() => UvTick(view) is null, CleanTimeout), "the uv row is still listed after its cache was emptied: " + Table.Dump(view, "AppsItem"));
        Assert.IsTrue(AppSession.WaitUntil(() => fake.Calls().Count(c => c == "cache dir") > askedBefore + 1, Ui.ElementTimeout),
            "the rows were not read again after the clean: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsFalse(ConfirmOpen(app), "the confirmation is still open");
    }

    // A command that ends with a code shows its error in the report (the cache is left as it was).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void AFailingCommandShowsItsErrorInTheReport()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("fail");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_EXIT", "3"));
        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        PressClear(app, fake);

        var report = WaitForReportLine(view, "uv: ");
        StringAssert.Contains(report, "uv: " + Text.CleanFailed(3), "the report line of a failed command");
        Assert.AreEqual(1, fake.CleanCalls());
        Assert.IsFalse(fake.CacheIsEmpty(), "the failing fake tool emptied the cache");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(UvTick(view)), CleanTimeout), "the uv row is not read again as Ready after the failed clean: " + Table.Dump(view, "AppsItem"));
    }

    // A tool that runs now (the fake tool started separately as a long sleeper) is shown with the runner's busy text and cannot be ticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ARunningToolShowsCloseItFirstAndHasNoEnabledTick()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("busy");
        var sleeper = fake.StartSleeper(120_000);
        var sleeperId = sleeper.Id;
        try
        {
            Assert.IsFalse(sleeper.WaitForExit(500), "the sleeping fake uv ended at once");
            using var app = StartPro(fake);
            var view = OpenApps(app);
            var tick = WaitForUvRow(view);
            var row = Table.RowName(tick);
            StringAssert.Contains(row, Text.CleanToolRunning("uv"), "the note of the uv row while uv runs: " + row);
            Assert.IsFalse(tick.Current.IsEnabled, "the uv tick box is enabled while uv runs");
            Table.TryToTick(tick);
            Assert.AreEqual(ToggleState.Off, StateOf(UvTick(view)), "the uv row got ticked while uv runs");

            Ui.Click(Button(view, "SuggestApps"));
            System.Threading.Thread.Sleep(Ui.NeverWatch);
            Assert.AreEqual(ToggleState.Off, StateOf(UvTick(view)), "Suggest ticked a row whose tool runs");
            Assert.AreEqual(0, fake.CleanCalls());
        }
        finally
        {
            using var running = Process.GetProcesses().FirstOrDefault(p => p.Id == sleeperId);
            running?.Kill();
            sleeper.Dispose();
        }
    }

    // History lists the run as its own entry, with its command and sizes, and no Put back button in its row.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void HistoryListsTheRunWithoutAPutBackButton()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("history");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        PressClear(app, fake);
        WaitForReportLine(view, "uv: ");

        var history = Ui.OpenView(app, "History", "HistoryView");
        AutomationElement? entry = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (entry = Table.LeafRows(history).FirstOrDefault(r => RowNameOrEmpty(r).Contains(fake.ToolPath, StringComparison.OrdinalIgnoreCase))) is not null, Ui.ElementTimeout),
            "History lists no entry with the fake tool's command: " + string.Join(" || ", Table.LeafRowNames(history)));
        var name = RowNameOrEmpty(entry!);
        StringAssert.Contains(name, "uv cache cleared by its own command", "the entry's text");
        StringAssert.Contains(name, "cache clean", "the entry's command");
        StringAssert.Contains(name, ByteUnits.Format(fake.CacheBytes), "the entry's size before");
        Assert.IsNull(Table.InRow(entry!, "PutBackEntry"), "the command entry has a Put back button");
    }

    private static string RowNameOrEmpty(AutomationElement row)
    {
        try
        {
            return row.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    // While a clean runs (the fake tool waits 20 s before it cleans): the status line says which app, Clear and Suggest are blocked,
    // and a ticked item in Temporary files cannot be moved. The Move button is never pressed.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void MovesAreBlockedWhileACleanRuns()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("blocked");
        using var temp = new SuggestTempFixture("clean");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "20000"));

        var tempView = Ui.OpenView(app, "TemporaryFiles", "TempView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(tempView, "TempItem", temp.Under.Name) is not null, TimeSpan.FromSeconds(90)),
            "no TempItem for " + temp.Under.Name + ": " + Table.Dump(tempView, "TempItem"));
        Table.Tick(Table.ControlWith(tempView, "TempItem", temp.Under.Name)!, () => Table.ControlWith(tempView, "TempItem", temp.Under.Name), "the temporary item");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Button(tempView, "MoveTemp")), Ui.ElementTimeout), "precondition: MoveTemp is disabled with an item ticked");

        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        PressClear(app, fake);

        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == "Clearing uv (1 of 1)…", Ui.ElementTimeout),
            $"the status line while the clean runs is \"{SuggestUi.Status(app)}\", expected \"Clearing uv (1 of 1)…\"");
        Assert.IsFalse(Enabled(Button(view, "ClearApps")), "Clear is enabled while a clean runs");
        Assert.IsFalse(Enabled(Button(view, "SuggestApps")), "Suggest is enabled while a clean runs");

        Ui.Click(Ui.SidebarItem(app, "TemporaryFiles"));
        tempView = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);
        Assert.IsFalse(Enabled(Button(tempView, "MoveTemp")), "MoveTemp is enabled while a clean runs");

        Ui.Click(Ui.SidebarItem(app, "AppsAndCaches"));
        view = Ui.FindAnywhere(app, "AppsView", Ui.ElementTimeout);
        WaitForReportLine(view, "uv: ");
        Assert.IsTrue(fake.CacheIsEmpty());
    }

    // ---- ASP-20 phase B, review round 1 (plan "Phase B review round 1", items 1, 2, 3, 5 and the History order) -------------

    private const string ClearingUv = "Clearing uv (1 of 1)…";
    private const string ClosesWhenClearingEnds = "Lean closes when clearing ends";

    /// <summary>Ticks only the fake uv row, opens the confirmation, presses Clear (after the item 4 guard) and waits until the status
    /// line says the clean runs. Returns the Apps view.</summary>
    private static AutomationElement BeginClean(AppSession app, FakeUv fake)
    {
        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        PressClear(app, fake);
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == ClearingUv, Ui.ElementTimeout),
            $"the status line while the clean runs is \"{SuggestUi.Status(app)}\", expected \"{ClearingUv}\"");
        return view;
    }

    private static bool Alive(AppSession app)
    {
        app.Process.Refresh();
        return !app.Process.HasExited;
    }

    // Item 1: a close during a clean is held. The fake tool sleeps 8 s before it cleans. Closing the window: the app is still there 2 s
    // later, the status line says why, nothing is lost; when the fake tool ends the app closes by itself, and commands.jsonl holds
    // exactly the one run (spec 05: every run that starts is recorded).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClosingTheWindowDuringACleanIsHeldUntilItEndsAndTheRunIsRecorded()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        CommandLogBackup.Write([]);
        using var fake = new FakeUv("closehold");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "8000"));
        BeginClean(app, fake);

        Assert.IsTrue(app.Process.CloseMainWindow(), "the window could not be asked to close");
        System.Threading.Thread.Sleep(2000);
        Assert.IsTrue(Alive(app), "the app ended on a close during a clean (the run would go unrecorded)");
        var window = app.Window(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(window, "the window is gone 2 s after a close during a clean");
        StringAssert.Contains(SuggestUi.Status(app), ClosesWhenClearingEnds, "the status line after a close during a clean");
        Assert.AreEqual(0, CommandLogBackup.Lines().Count, "the run was recorded before it ended");

        Assert.IsTrue(app.Process.WaitForExit((int)CleanTimeout.TotalMilliseconds), "the app did not close by itself after the clean ended");
        Assert.AreEqual(1, fake.CleanCalls(), "the fake uv was not told to clean exactly once: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsTrue(fake.CacheIsEmpty(), "the fake cache is not empty after the clean");
        var lines = CommandLogBackup.Lines();
        Assert.AreEqual(1, lines.Count, "commands.jsonl does not hold exactly one new entry for the run: " + string.Join(" || ", lines));
        using var entry = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(fake.ToolPath, entry.RootElement.GetProperty("program").GetString(), ignoreCase: true, "the program of the logged run");
        Assert.AreEqual("cleaned", entry.RootElement.GetProperty("result").GetString(), "the result of the logged run");
        Assert.AreEqual(fake.CacheBytes, entry.RootElement.GetProperty("bytesBefore").GetInt64(), "bytesBefore of the logged run");
    }

    // Item 1, second half: a close while only the confirmation is open closes the dialog as Cancel and then the window; nothing runs.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClosingTheWindowWithTheConfirmationOpenClosesTheAppAndRunsNothing()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        CommandLogBackup.Write([]);
        using var fake = new FakeUv("closeconfirm");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        TickOnlyUv(view);
        OpenConfirm(app, view);
        AssertConfirmHoldsOnlyTheFake(app, fake);

        Assert.IsTrue(app.Process.CloseMainWindow(), "the window could not be asked to close");
        Assert.IsTrue(app.Process.WaitForExit(15_000), "the app did not close with only the confirmation open");
        Assert.AreEqual(0, fake.CleanCalls(), "a close with the confirmation open ran a clean: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsFalse(fake.CacheIsEmpty(), "the fake cache was emptied by a close with the confirmation open");
        Assert.AreEqual(0, CommandLogBackup.Lines().Count, "a run was recorded although nothing ran");
    }

    // Item 2 (UI): a damaged command-log line does not crash History; the good entries around it are listed and the damaged ones
    // are left out. Every damaged line carries "BADLINE" in an argument, so none can be shown unnoticed.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ADamagedCommandLogLineDoesNotCrashHistory()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var backup = new JournalBackup();
        JournalBackup.Write([]);
        var now = DateTimeOffset.UtcNow;
        const string goodOne = @"C:\GoodTools\one\uv.exe";
        const string goodTwo = @"C:\GoodTools\two\uv.exe";
        CommandLogBackup.Write(
        [
            CommandLogBackup.Line("good-1", now.AddMinutes(-5), "uv", goodOne, @"C:\GoodTools\one\cache"),
            CommandLogBackup.Line("bad-null-arg", now.AddMinutes(-3), "uv", @"C:\BadTools\null-arg\uv.exe", @"C:\BadTools\cache", "[\"BADLINE-null\",null]"),
            CommandLogBackup.Line("bad-no-program", now.AddMinutes(-3), "uv", string.Empty, @"C:\BadTools\cache", "[\"BADLINE-no-program\"]"),
            CommandLogBackup.Line("bad-no-entry", now.AddMinutes(-3), string.Empty, @"C:\BadTools\no-entry\uv.exe", @"C:\BadTools\cache", "[\"BADLINE-no-entry\"]"),
            CommandLogBackup.Line("bad-no-location", now.AddMinutes(-3), "uv", @"C:\BadTools\no-location\uv.exe", string.Empty, "[\"BADLINE-no-location\"]"),
            "{\"id\":\"cut off BADLINE-torn",
            CommandLogBackup.Line("good-2", now.AddMinutes(-1), "uv", goodTwo, @"C:\GoodTools\two\cache"),
        ]);
        using var app = ProApp.StartPro();
        var history = Ui.OpenView(app, "History", "HistoryView");
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                var rows = Table.LeafRowNames(history);
                return rows.Any(n => n.Contains(goodOne, StringComparison.OrdinalIgnoreCase)) && rows.Any(n => n.Contains(goodTwo, StringComparison.OrdinalIgnoreCase));
            }, Ui.ElementTimeout),
            "History does not list the two good command entries (a damaged line may have crashed it; app alive: " + Alive(app) + "): " + string.Join(" || ", Table.LeafRowNames(history)));
        Assert.IsTrue(Alive(app), "the app ended after History read a damaged command-log line");
        var all = string.Join(" || ", Table.LeafRowNames(history));
        Assert.IsFalse(all.Contains("BADLINE", StringComparison.Ordinal), "History shows a damaged command-log line: " + all);
    }

    // Item 3: Put back from History is blocked during a clean, like a move: the button is disabled, or pressing it does nothing (the
    // status line is still the clean's and the journal is as it was). The journal's entry is a trashed one that is not on this disk.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void PutBackFromHistoryDoesNothingWhileACleanRuns()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var backup = new JournalBackup();
        using var fake = new FakeUv("putback");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-asp20-putback-" + Guid.NewGuid().ToString("N")[..8]);
        var trashed = Path.Combine(root, "was-here", "trashed-item.bin");
        JournalBackup.Write([JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-asp20", DateTimeOffset.UtcNow.AddHours(-2), "map", trashed, "trashed", bytes: 4096)]);
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "20000"));
        BeginClean(app, fake);

        var history = Ui.OpenView(app, "History", "HistoryView");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(history, "PutBackEntry").Count == 1, Ui.ElementTimeout),
            "precondition: History does not offer the one Put back: " + string.Join(" || ", Table.LeafRowNames(history)));
        var journalBefore = File.ReadAllBytes(JournalBackup.Path);
        Assert.AreEqual(ClearingUv, SuggestUi.Status(app), "precondition: the clean is not running");
        foreach (var button in AppSession.FindAll(history, "PutBackEntry").Concat(AppSession.FindAll(history, "PutBackSessionHistory")))
        {
            if (Enabled(button))
            {
                Ui.Click(button);
            }
        }

        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.AreEqual(ClearingUv, SuggestUi.Status(app), "Put back from History did something while a clean runs (the status line changed)");
        CollectionAssert.AreEqual(journalBefore, File.ReadAllBytes(JournalBackup.Path), "Put back from History wrote to the journal while a clean runs");
        Assert.IsFalse(Directory.Exists(root), "the test's own path must not exist");

        Ui.Click(Ui.SidebarItem(app, "AppsAndCaches"));
        var view = Ui.FindAnywhere(app, "AppsView", Ui.ElementTimeout);
        WaitForReportLine(view, "uv: ");
    }

    // Item 5: during a clean, Refresh and every tick box are disabled too.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void RefreshAndTheTickBoxesAreDisabledWhileACleanRuns()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("refresh");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "12000"));
        var view = BeginClean(app, fake);

        Assert.IsFalse(Enabled(Button(view, "RefreshApps")), "Refresh is enabled while a clean runs");
        var boxes = Table.Controls(view, "AppsItem");
        Assert.IsTrue(boxes.Count > 0, "no tick box in the view while a clean runs: " + Ui.Dump(view));
        Assert.IsTrue(boxes.All(b => !Enabled(b)), "a tick box is enabled while a clean runs: " + Table.Dump(view, "AppsItem"));
        var callsBefore = fake.Calls().Count(c => c == "cache dir");
        SuggestUi.MouseClick(Button(view, "RefreshApps"));
        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.AreEqual(callsBefore, fake.Calls().Count(c => c == "cache dir"), "a click on Refresh read the rows again during a clean");

        WaitForReportLine(view, "uv: ");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Button(view, "RefreshApps")), CleanTimeout), "Refresh stays disabled after the clean ended");
    }

    // History's order: a command run stands among the move sessions by time, newest first, as sessions do.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void HistoryOrdersCommandRunsAmongSessionsByTimeNewestFirst()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var backup = new JournalBackup();
        var now = DateTimeOffset.UtcNow;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-asp20-order-" + Guid.NewGuid().ToString("N")[..8]);
        var newSession = Path.Combine(root, "order-session-new.bin");
        var oldSession = Path.Combine(root, "order-session-old.bin");
        const string cmdNewest = @"C:\OrderTools\newest\uv.exe";
        const string cmdMiddle = @"C:\OrderTools\middle\uv.exe";
        const string cmdOldest = @"C:\OrderTools\oldest\uv.exe";
        JournalBackup.Write(
        [
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-order-new", now.AddHours(-1), "map", newSession, "trashed", bytes: 4096),
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-order-old", now.AddHours(-3), "map", oldSession, "trashed", bytes: 4096),
        ]);
        CommandLogBackup.Write(
        [
            CommandLogBackup.Line("order-oldest", now.AddHours(-4), "uv", cmdOldest, @"C:\OrderTools\oldest\cache"),
            CommandLogBackup.Line("order-newest", now.AddMinutes(-30), "uv", cmdNewest, @"C:\OrderTools\newest\cache"),
            CommandLogBackup.Line("order-middle", now.AddHours(-2), "uv", cmdMiddle, @"C:\OrderTools\middle\cache"),
        ]);
        using var app = ProApp.StartPro();
        var history = Ui.OpenView(app, "History", "HistoryView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.LeafRowNames(history).Count == 5, Ui.ElementTimeout),
            "precondition: History does not list the 3 command runs and 2 sessions as 5 rows: " + string.Join(" || ", Table.LeafRowNames(history)));
        var rows = Table.LeafRowNames(history);
        int At(string needle) => rows.FindIndex(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase));
        var order = new[] { At(cmdNewest), At(Path.GetFileName(newSession)), At(cmdMiddle), At(Path.GetFileName(oldSession)), At(cmdOldest) };
        Assert.IsTrue(order.All(i => i >= 0), "a row is missing: " + string.Join(" || ", rows));
        CollectionAssert.AreEqual(Enumerable.Range(0, 5).ToArray(), order, "History is not newest first across sessions and command runs: " + string.Join(" || ", rows));
    }

    // ---- ASP-20 phase B, review round 2 (plan "Phase B review round 2", items 1 and 2) -----------------------------------

    private static AutomationElement? PipTick(AutomationElement view) => Table.ControlWith(view, "AppsItem", Text.WhyPipCache);

    /// <summary>Waits until both fake rows (pip and uv) are Ready, ticks them and unticks every other row, then asserts that ONLY
    /// those two are ticked.</summary>
    private static void TickOnlyUvAndPip(AutomationElement view)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(PipTick(view)) && Enabled(UvTick(view)), ReadTimeout),
            "the fake pip and uv rows are not both Ready (python.exe and uv.exe are the fakes first on PATH): " + Table.Dump(view, "AppsItem"));
        bool IsFake(AutomationElement b) =>
            Table.RowName(b).Contains(Text.WhyUvCache, StringComparison.Ordinal) || Table.RowName(b).Contains(Text.WhyPipCache, StringComparison.Ordinal);
        foreach (var box in Table.Controls(view, "AppsItem").Where(b => !IsFake(b)))
        {
            if (Enabled(box) && StateOf(box) == ToggleState.On)
            {
                ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            }
        }

        Table.Tick(PipTick(view)!, () => PipTick(view), "the pip row");
        Table.Tick(UvTick(view)!, () => UvTick(view), "the uv row");
        Assert.IsTrue(Table.Controls(view, "AppsItem").Where(b => !IsFake(b)).All(b => StateOf(b) == ToggleState.Off),
            "precondition for Clear: a row other than the fake pip and uv is ticked: " + Table.Dump(view, "AppsItem"));
        Assert.AreEqual(ToggleState.On, StateOf(PipTick(view)), "precondition for Clear: the pip row is not ticked");
        Assert.AreEqual(ToggleState.On, StateOf(UvTick(view)), "precondition for Clear: the uv row is not ticked");
    }

    /// <summary>The guard of item 4 for two rows: the confirmation holds exactly two ConfirmClearItem, one naming the fake
    /// python.exe's full path and one the fake uv.exe's; nothing but the fakes can run.</summary>
    private static void PressClearOnTheTwoFakes(AppSession app, FakeUv fake)
    {
        var dialog = Ui.FindAnywhere(app, "ConfirmClear", Ui.ElementTimeout);
        var items = AppSession.FindAll(dialog, "ConfirmClearItem").Select(i => i.Current.Name ?? string.Empty).ToList();
        Assert.AreEqual(2, items.Count, "the confirmation does not hold exactly two ConfirmClearItem: " + Ui.Dump(dialog));
        Assert.AreEqual(1, items.Count(t => t.Contains(fake.PipToolPath, StringComparison.OrdinalIgnoreCase)), "no item names the fake python.exe's full path: " + string.Join(" || ", items));
        Assert.AreEqual(1, items.Count(t => t.Contains(fake.ToolPath, StringComparison.OrdinalIgnoreCase)), "no item names the fake uv.exe's full path: " + string.Join(" || ", items));
        Ui.Click(Ui.FindAnywhere(app, "ConfirmClearButton", Ui.ElementTimeout));
    }

    // Item 1: the move bar's "Put back all from this session" is disabled while a clean runs (like History's Put back), and enabled
    // again when it ends. A temporary file is moved first, so the bar shows the session; the fake uv sleeps 15 s before it cleans.
    // The moved file is put back at the end.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void PutBackAllFromThisSessionIsDisabledWhileACleanRuns()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("putbacksession");
        using var temp = new SuggestTempFixture("putbacksession");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "15000"));

        var tempView = Ui.OpenView(app, "TemporaryFiles", "TempView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(tempView, "TempItem", temp.Under.Name) is not null, TimeSpan.FromSeconds(90)),
            "no TempItem for " + temp.Under.Name + ": " + Table.Dump(tempView, "TempItem"));
        Table.Tick(Table.ControlWith(tempView, "TempItem", temp.Under.Name)!, () => Table.ControlWith(tempView, "TempItem", temp.Under.Name), "the temporary item");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Button(tempView, "MoveTemp")), Ui.ElementTimeout), "precondition: MoveTemp is disabled with an item ticked");
        Ui.Click(Button(tempView, "MoveTemp"));
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
        Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(temp.Under.Path) is not null, TimeSpan.FromSeconds(10)), temp.Under.Path + " is not in the Recycle Bin");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout)), Ui.ElementTimeout),
            "precondition: Put back all from this session is disabled after a move, with no clean running");

        BeginClean(app, fake);
        Assert.IsTrue(AppSession.WaitUntil(() => !Enabled(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout)), TimeSpan.FromSeconds(3)),
            "Put back all from this session is enabled while a clean runs (status \"" + SuggestUi.Status(app) + "\")");
        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.AreEqual(ClearingUv, SuggestUi.Status(app), "precondition: the clean ended before the check was repeated");
        Assert.IsFalse(Enabled(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout)), "Put back all from this session became enabled while the clean still runs");

        var view = Ui.FindAnywhere(app, "AppsView", Ui.ElementTimeout);
        WaitForReportLine(view, "uv: ");
        Assert.IsTrue(AppSession.WaitUntil(() => Enabled(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout)), Ui.ElementTimeout),
            "Put back all from this session stays disabled after the clean ended");
        Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(temp.Under.Path), Ui.MoveTimeout), temp.Under.Path + " was not put back");
    }

    // Item 2, one row: once a close is held, "Lean closes when clearing ends" stays on the status line until the window closes. With
    // one row nothing overwrites it before the end, so this case also passes today; the two-row case below is the one that bites.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void TheClosesWhenClearingEndsLineStaysWhileTheOneRowRuns()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        CommandLogBackup.Write([]);
        using var fake = new FakeUv("closeline1");
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "7000"));
        BeginClean(app, fake);

        Assert.IsTrue(app.Process.CloseMainWindow(), "the window could not be asked to close");
        AssertClosesLineStaysUntilTheAppEnds(app);
        Assert.AreEqual(1, fake.CleanCalls(), "the fake uv was not told to clean exactly once: [" + string.Join(" | ", fake.Calls()) + "]");
    }

    // Item 2, two rows (a second fake tool: the same exe as python.exe plays pip): the window is closed while the first row runs; the
    // second row starts about 5 s later and its "Clearing uv (2 of 2)…" must not replace "Lean closes when clearing ends". Every
    // status line read from the close to the end of the app is the closes line.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void TheClosesWhenClearingEndsLineIsNotOverwrittenByTheNextRow()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        CommandLogBackup.Write([]);
        using var fake = new FakeUv("closeline2", withPip: true);
        using var app = StartPro(fake, ("LEAN_FAKETOOL_SLEEP_MS", "5000"));
        var view = OpenApps(app);
        TickOnlyUvAndPip(view);
        OpenConfirm(app, view);
        PressClearOnTheTwoFakes(app, fake);
        const string firstRow = "Clearing pip (1 of 2)…";
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == firstRow, Ui.ElementTimeout),
            $"the status line while the first row runs is \"{SuggestUi.Status(app)}\", expected \"{firstRow}\"");

        Assert.IsTrue(app.Process.CloseMainWindow(), "the window could not be asked to close");
        AssertClosesLineStaysUntilTheAppEnds(app);
        Assert.AreEqual(1, fake.PipCleanCalls(), "the fake pip was not told to clean exactly once: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.AreEqual(1, fake.CleanCalls(), "the fake uv was not told to clean exactly once (the second row did not run): [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsTrue(fake.PipCacheIsEmpty() && fake.CacheIsEmpty(), "a fake cache is not empty after the clean");
        Assert.AreEqual(2, CommandLogBackup.Lines().Count, "commands.jsonl does not hold the two runs: " + string.Join(" || ", CommandLogBackup.Lines()));
    }

    /// <summary>The status line now, or null when the window or the line is not there (a quick read; no waiting).</summary>
    private static string? TryStatus(AppSession app)
    {
        try
        {
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id)))
            {
                if (AppSession.TryFind(top, "Status") is { } status)
                {
                    return status.Current.Name;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    /// <summary>After the close was asked for: reads the status line every 200 ms until the app ends and asserts that, whenever it
    /// can be read, it is "Lean closes when clearing ends" (the first read must succeed), and that the app ends by itself.</summary>
    private static void AssertClosesLineStaysUntilTheAppEnds(AppSession app)
    {
        var seen = new List<string>();
        var until = DateTime.UtcNow + CleanTimeout;
        while (Alive(app) && DateTime.UtcNow < until)
        {
            if (TryStatus(app) is { } text)
            {
                seen.Add(text);
            }

            System.Threading.Thread.Sleep(200);
        }

        Assert.IsTrue(seen.Count >= 3, "the status line could not be read during the held close (" + seen.Count + " reads)");
        var wrong = seen.Where(t => !t.Contains(ClosesWhenClearingEnds, StringComparison.Ordinal)).Distinct().ToList();
        Assert.AreEqual(0, wrong.Count, "the status line left \"" + ClosesWhenClearingEnds + "\" while the held close was waiting: " + string.Join(" | ", wrong));
        Assert.IsTrue(app.Process.WaitForExit(15_000), "the app did not close by itself after the clean ended");
    }
}
