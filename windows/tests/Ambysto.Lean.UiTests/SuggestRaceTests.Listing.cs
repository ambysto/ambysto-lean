using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// ASP-17 phase C fix round 2 (intent/2026-10-20-asp-17-suggest.md, "Phase C review round 2" and "Fix round 2"): the tests for the MAJOR (a
// Large and old listing that is still running when Suggest ends without showing a suggestion must complete), for "a listing that began before
// Suggest must not replace the suggestion it showed", for the Temp MINORs, and for the bounds of the Debug-only delays (MINOR 4). They use the
// Debug-only --listing-delay <ms>, which holds the listing of Large and old and of Temporary files off the UI thread (after the read, before
// the result is applied), and --suggest-delay as in the first part of this class. ProDebug only, Debug build with --pro.
public sealed partial class SuggestRaceTests
{
    private const string QuietMedium = "list-medium.bin";
    private const string QuietSmall = "list-small.bin";

    /// <summary>A scratch folder whose Large and old list at the default 100 MB is the 150 MB file only, and at 50 MB the 60 MB file too;
    /// nothing in it is suggested (both are .bin files below 1 GiB). With <paramref name="zips"/> old installers, those are suggested.</summary>
    private static DownloadsFixture BuildQuiet(string name, int zips = 0, int folders = 0)
    {
        var fixture = new DownloadsFixture(name);
        fixture.File(QuietMedium, 150 * MB, 400, seed: 1);
        fixture.File(QuietSmall, 60 * MB, 400, seed: 2);
        for (var i = 0; i < zips; i++)
        {
            fixture.File($"list-setup-{i:D3}.zip", 8 * KB, 100, seed: i);
        }

        for (var d = 0; d < folders; d++)
        {
            fixture.Empty($@"n{d:D3}\f.dat");
        }

        return fixture;
    }

    private static AutomationElement OpenLargeOldWaiting(AppSession app, string rowName)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", rowName) is not null, Ui.ElementTimeout), $"no row for {rowName}: " + Table.Dump(view, "LargeOldSelect"));
        return view;
    }

    /// <summary>The listing that was still running when Suggest ended must have completed: the 60 MB file's row is there (the 50 MB
    /// filter), the 150 MB one too, no Why column, no ClearSuggestion, nothing ticked.</summary>
    private static void AssertTheListingCompleted(AutomationElement view, string what)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", QuietSmall) is not null, TimeSpan.FromSeconds(25)),
            $"{what}: the listing that was running never completed (the view holds: {Table.Dump(view, "LargeOldSelect")})");
        Assert.IsNotNull(Table.ControlWith(view, "LargeOldSelect", QuietMedium), $"{what}: the 150 MB file's row is gone: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), $"{what}: a Why column with no suggestion");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), $"{what}: ClearSuggestion with no suggestion");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), $"{what}: a row is ticked");
    }

    // ---- the MAJOR: a listing still running when Suggest ends without a suggestion ----------------------------------------------

    // The size filter is set to 50 MB (the listing it starts is held for 5 s), and Suggest is pressed at once: there is nothing to suggest.
    // The listing must still complete and show the 50 MB filter's rows; the view must not keep the old rows (the version bump dropped it).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void AFilterListingStillRunningWhenSuggestFindsNothingCompletes()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildQuiet("list-nothing");
        using var app = ProApp.StartPro("--scan", fixture.Path, "--listing-delay", "5000");
        var view = OpenLargeOldWaiting(app, QuietMedium);
        Assert.IsNull(Table.ControlWith(view, "LargeOldSelect", QuietSmall), "precondition: the 60 MB file is not in the 100 MB list");

        ChooseSize(app, view, 50 * MB);
        Invoke(SuggestUi.Button(view));

        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.NothingToSuggest, TimeSpan.FromSeconds(4)),
            $"precondition: Suggest says \"{Text.NothingToSuggest}\" while the listing runs (status \"{SuggestUi.Status(app)}\")");
        AssertTheListingCompleted(view, "Suggest found nothing");
    }

    // The same, with the Suggest ending by Cancel: 101 old installers ask first; Cancel; the listing under way must complete.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void AFilterListingStillRunningWhenSuggestIsCancelledCompletes()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildQuiet("list-cancel", zips: 101);
        using var app = ProApp.StartPro("--scan", fixture.Path, "--listing-delay", "5000");
        var view = OpenLargeOldWaiting(app, QuietMedium);

        ChooseSize(app, view, 50 * MB);
        Invoke(SuggestUi.Button(view));
        var (scope, text) = SuggestDialog.Wait(app);
        Assert.AreEqual(Text.SuggestConfirm(101, 0), text, "the confirmation");
        SuggestDialog.Press(app, scope, Text.Cancel);

        AssertTheListingCompleted(view, "Suggest was cancelled");
    }

    // Opening Large and old starts a listing (held 5 s); Suggest pressed at once finds nothing; the listing must complete (the view must
    // not stay empty).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void OpeningLargeAndOldThenSuggestingAtOnceWithNothingToSuggestLeavesTheListing()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildQuiet("list-open");
        using var app = ProApp.StartPro("--scan", fixture.Path, "--listing-delay", "5000");
        Ui.WaitForScan(app);
        var item = Ui.SidebarItem(app, "LargeOld");
        Invoke(item);
        var view = Ui.FindAnywhere(app, "LargeOldView", Ui.ElementTimeout);
        Invoke(SuggestUi.Button(view));

        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", QuietMedium) is not null, TimeSpan.FromSeconds(25)),
            "the view stayed empty: the listing that was running when Suggest found nothing never completed: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column with no suggestion");
    }

    // The scan case: Large and old is open while the scan runs; when the scan ends the view is listed (held 5 s) and Suggest, pressed the
    // moment it is enabled, finds nothing. The view must not stay empty. (A walk slowed by --scan-delay over 400 folders.)
    [TestMethod]
    [TestCategory("ProDebug")]
    public void AScanThatEndsWithLargeAndOldOpenAndSuggestFindingNothingDoesNotLeaveTheViewEmpty()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildQuiet("list-scan", folders: 400);
        using var app = ProApp.StartPro("--scan", fixture.Path, "--scan-delay", "150", "--listing-delay", "5000");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), Ui.ElementTimeout), "the scan did not start");
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(view)), TimeSpan.FromSeconds(90)), "Suggest never became enabled after the scan");

        Invoke(SuggestUi.Button(view));

        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", QuietMedium) is not null, TimeSpan.FromSeconds(25)),
            "the view stayed empty after the scan ended: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column with no suggestion");
    }

    // ---- a listing that began before Suggest must not replace the suggestion Suggest showed ------------------------------------

    // The size filter is set to 50 MB (the listing it starts is held 6 s) and Suggest is pressed at once; its walk is quick and shows the ten
    // installers. When the listing finishes it must not replace them: after 8 s the suggestion is whole (status, Why column, ticks).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void AListingThatBeganBeforeSuggestDoesNotReplaceTheSuggestionItShowed()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildTree("list-keep");
        using var app = ProApp.StartPro("--scan", fixture.Path, "--listing-delay", "6000");
        var view = OpenLargeOld(app);

        ChooseSize(app, view, 50 * MB);
        Invoke(SuggestUi.Button(view));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "ClearSuggestion") is not null, TimeSpan.FromSeconds(4)), "precondition: Suggest showed its list before the listing ended");
        Thread.Sleep(8000);

        Assert.IsNull(LargeOldSettled(app, view, Installers, InstallerBytes, TimeSpan.FromSeconds(10)), "after the held listing ended");
    }

    // ---- Temporary files (MINOR 2): a click deferred during a Large and old walk ------------------------------------------------

    // Temporary files shows a suggestion (Why column). Large and old's Suggest is pressed (its walk held for 3 s) and during the hold the
    // Temporary files item is clicked: the click is deferred, and when the walk is over it runs as a normal open would: the list is read
    // again and the older Why is dropped (no Why column, nothing ticked).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ATemporaryFilesClickDeferredDuringALargeAndOldWalkReadsAgainAndDropsTheOlderWhy()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("defer");
        using var fixture = BuildTree("list-defer");
        using var app = StartHeld(3000, "--scan", fixture.Path);
        Ui.WaitForScan(app);
        var tempView = Ui.OpenView(app, "TemporaryFiles", "TempView");
        foreach (var item in temp.Suggested.Concat(temp.NotSuggested))
        {
            Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(tempView, "TempItem", item.Name) is not null, TimeSpan.FromSeconds(90)), "no TempItem for " + item.Name);
        }

        Invoke(SuggestUi.Button(tempView));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(tempView), TimeSpan.FromSeconds(20)), "precondition: Temporary files shows its suggestion");
        var large = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(large, "LargeOldSelect", Medium) is not null, Ui.ElementTimeout), "no row for " + Medium);
        Invoke(SuggestUi.Button(large));
        Ui.Click(Ui.SidebarItem(app, "TemporaryFiles"));

        tempView = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => !SuggestUi.HasWhyColumn(tempView) && Table.ControlWith(tempView, "TempItem", temp.Old.Name) is not null, TimeSpan.FromSeconds(30)),
            "the Temporary files click deferred during the Large and old walk did not read the list again: the older Why stays: " + Table.Dump(tempView, "TempItem"));
        Assert.IsTrue(Table.Controls(tempView, "TempItem").All(b => Table.State(b) == ToggleState.Off), "a Temporary files row stays ticked: " + Table.Dump(tempView, "TempItem"));
    }

    // ---- MINOR 4: the bounds of the Debug-only delays ---------------------------------------------------------------------------

    // --suggest-delay and --listing-delay given "Infinity", -5, 999999999 or "abc" start the app normally (no throw at start-up) and the delay
    // is 0: the list is there at once and Suggest answers at once. Four sessions.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void UnusableDelayValuesStartTheAppNormallyAndMeanNoDelay()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildTree("list-bounds", installers: 3);
        var failures = new List<string>();
        foreach (var value in new[] { "Infinity", "-5", "999999999", "abc" })
        {
            using var app = ProApp.StartPro("--scan", fixture.Path, "--suggest-delay", value, "--listing-delay", value);
            try
            {
                var view = OpenLargeOld(app);
                if (app.Process.HasExited)
                {
                    failures.Add($"{value}: the app exited");
                    continue;
                }

                Invoke(SuggestUi.Button(view));
                var expected = Text.SuggestedFiles(3, ByteUnits.Format(3 * 200 * KB));
                if (!AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, TimeSpan.FromSeconds(8)))
                {
                    failures.Add($"{value}: no suggestion within 8 s (status \"{SuggestUi.Status(app)}\"): the delay was not 0");
                }
            }
            catch (AssertFailedException e)
            {
                failures.Add($"{value}: {e.Message.ReplaceLineEndings(" ")}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(" || ", failures));
    }
}
