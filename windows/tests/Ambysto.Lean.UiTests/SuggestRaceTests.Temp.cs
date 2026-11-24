using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// ASP-17 phase C fix round 3 (intent/2026-10-20-asp-17-suggest.md, "Phase C review round 3" and "Owner decision (2026-10-21)"): the MINORs
// 1 and 2 of Temporary files' Suggest, written before the fixes. (1) Only a suggestion just made from fresh rows skips the re-read of a
// deferred Temporary files click, not an older one. (2) An exception in Suggest's Temp walk or re-read is caught as a failed Temp listing is:
// the view shows Text.TempReadFailed, no Why, no ticks. (MINOR 3, a re-read that finds no row, is not testable: Windows\Temp is always listed.) Debug build with --pro,
// ProDebug only; the walk is held by --suggest-delay.
public sealed partial class SuggestRaceTests
{
    private static AutomationElement WaitForTempRows(AppSession app, string openingItem = "TemporaryFiles")
    {
        var view = Ui.OpenView(app, openingItem, "TempView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.Controls(view, "TempItem").Count > 0 && SuggestUi.EnabledNow(SuggestUi.TryButton(view)), TimeSpan.FromSeconds(90)),
            "the Temporary files list was not read: " + Table.Dump(view, "TempItem"));
        return view;
    }

    private static bool AnyName(AutomationElement scope, string text)
    {
        try
        {
            return Ui.AnyNameContains(scope, text);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    // MINOR 1: a Temp suggestion is shown (Tick after the question: 101 old rows). Suggest runs again (held 3 s), Temporary files is
    // clicked during the walk, then the question is answered with Cancel. The click was deferred, and the walk showed nothing new, so the
    // list is read again: no Why column, no old Why in a row, and the ticks are a plain listing's (none). Fails while the click is dropped
    // because an older suggestion exists (the old Why stays).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ATemporaryFilesClickDeferredDuringASecondSuggestThatIsCancelledReadsTheRowsAgain()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("again");
        temp.Bulk(bin: 101, dll: 0, days: 40);
        using var app = StartHeld(3000);
        var view = WaitForTempRows(app);

        Invoke(SuggestUi.Button(view));
        var first = SuggestDialog.Wait(app);
        SuggestDialog.Press(app, first.Scope, Text.RuleConfirmTick);
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "precondition: the first suggestion shows its Why column");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(view)), Ui.ElementTimeout), "precondition: Suggest is enabled again");

        var clock = Stopwatch.StartNew();
        Invoke(SuggestUi.Button(view));
        Ui.Click(Ui.SidebarItem(app, "Overview"));
        Ui.Click(Ui.SidebarItem(app, "TemporaryFiles"));
        var inTime = clock.Elapsed < TimeSpan.FromMilliseconds(2500);
        var second = SuggestDialog.Wait(app);
        if (!inTime)
        {
            Assert.Inconclusive($"Temporary files was clicked {clock.Elapsed.TotalSeconds:F1} s into a 3 s hold: too late to be during the walk");
        }

        SuggestDialog.Press(app, second.Scope, Text.Cancel);

        view = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(view) && Table.Controls(view, "TempItem").Count > 0, TimeSpan.FromSeconds(30)),
            "the Temporary files click deferred during the cancelled Suggest was dropped: the older Why column stays: " + Table.Dump(view, "TempItem"));
        Assert.IsTrue(Table.Controls(view, "TempItem").All(b => Table.State(b) == ToggleState.Off), "a row stays ticked after the rows were read again");
        Assert.IsFalse(Table.Controls(view, "TempItem").Any(b => Table.RowName(b).Contains(SuggestUi.TempWhyPrefix(), StringComparison.Ordinal)), "a row still shows an older Why");
    }

    // MINOR 2: Suggest's re-read throws. No natural trigger exists (TempFinder and the scan of Temp catch what the disk gives them; the
    // context and the cancellation token cannot be made to fail from outside), so this test uses the Debug-only switch
    // --fail-temp-reread (MainWindow.DebugQa.cs): the first re-read inside Suggest's Temp loop throws once. A folder is protected during the
    // walk to force the re-read. Afterwards the view shows Text.TempReadFailed, no Why column and no tick; opening Temporary files again
    // lists the rows, and Suggest works again (the switch fails once): the suggestion shows.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void AFailingReReadInsideSuggestShowsTheReadErrorAndSuggestWorksAgain()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("fail");
        var guarded = MakeGuarded(temp, "fail");
        try
        {
            using var settings = new SettingsBackup();
            SettingsBackup.WriteProtectedFolders();
            using var app = StartHeld(10000, "--fail-temp-reread");
            var view = WaitForTempRows(app);
            using var watch = new StatusWatch(app, Prefix(Text.SuggestTicked(1)));
            Invoke(SuggestUi.Button(view));

            ProtectThroughSettings(app, guarded);
            if (watch.Seen)
            {
                Assert.Inconclusive("the suggestion appeared before the folder was protected: the 10 s hold did not last");
            }

            view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            Assert.IsTrue(AppSession.WaitUntil(() => AnyName(view, Text.TempReadFailed), TimeSpan.FromSeconds(40)),
                $"the failed re-read inside Suggest did not show \"{Text.TempReadFailed}\" (status \"{SuggestUi.Status(app)}\"): " + Ui.Dump(view));
            Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column after the re-read failed");
            Assert.IsTrue(Table.Controls(view, "TempItem").All(b => Table.State(b) == ToggleState.Off), "a row is ticked after the re-read failed");
            Assert.IsFalse(SuggestUi.Status(app).StartsWith(Prefix(Text.SuggestTicked(1)), StringComparison.Ordinal), "the status line still says Suggest ticked rows");

            // Suggest is usable again: the list is read again by opening the view, and the next Suggest (the switch fails once) shows.
            Ui.Click(Ui.SidebarItem(app, "Overview"));
            view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "TempItem", temp.Old.Name) is not null && SuggestUi.EnabledNow(SuggestUi.TryButton(view)), TimeSpan.FromSeconds(60)),
                "after the failure the list cannot be read again or Suggest stays off: " + Table.Dump(view, "TempItem"));
            Assert.IsFalse(AnyName(view, Text.TempReadFailed), "the read error stays after the list was read again");
            Invoke(SuggestUi.Button(view));
            Assert.IsNull(TempSettled(app, view, temp, TimeSpan.FromSeconds(40)), "Suggest does not work again after the failed re-read");
        }
        finally
        {
            DeleteFolder(guarded);
        }
    }

    // Regression for the fix of MINOR 1: a suggestion just made from fresh rows still skips the re-read. A folder is protected during Temp's
    // own walk (so the walk reads the rows again itself), and Temporary files is clicked during the same walk (deferred): when the walk
    // ends, that walk's suggestion is on screen (Why column, the suggested rows ticked, the protected row off and disabled), not a plain
    // listing that dropped it.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void ATemporaryFilesClickDeferredDuringTempsOwnWalkShowsThatWalksSuggestion()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("fresh");
        var guarded = MakeGuarded(temp, "fresh");
        try
        {
            using var settings = new SettingsBackup();
            SettingsBackup.WriteProtectedFolders();
            using var app = StartHeld(10000);
            var view = WaitForTempRows(app);
            using var watch = new StatusWatch(app, Prefix(Text.SuggestTicked(1)));
            Invoke(SuggestUi.Button(view));

            Ui.Click(Ui.SidebarItem(app, "Overview"));
            Ui.Click(Ui.SidebarItem(app, "TemporaryFiles")); // deferred: the walk is running
            ProtectThroughSettings(app, guarded);
            if (watch.Seen)
            {
                Assert.Inconclusive("the suggestion appeared before the folder was protected: the 10 s hold did not last");
            }

            view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            var problem = TempSettled(app, view, temp, TimeSpan.FromSeconds(40));
            Assert.IsNull(problem, "the suggestion of the walk was replaced by a re-read: " + problem);
            var box = Table.ControlWith(view, "TempItem", Path.GetFileName(guarded));
            Assert.IsNotNull(box, "the protected row is not listed");
            Assert.AreEqual(ToggleState.Off, Table.State(box), "the protected folder is ticked");
            Assert.IsFalse(box.Current.IsEnabled, "the protected folder's row is tickable");
        }
        finally
        {
            DeleteFolder(guarded);
        }
    }

    private static string MakeGuarded(SuggestTempFixture temp, string tag)
    {
        var guarded = Path.Combine(temp.TempFolder, $"LeanUiSuggest-guarded-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(guarded);
        var inside = Path.Combine(guarded, "inside.bin");
        File.WriteAllBytes(inside, new byte[100 * 1024]);
        var old = SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(inside, old);
        Directory.SetLastWriteTimeUtc(guarded, old);
        return guarded;
    }

    private static void DeleteFolder(string path)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
