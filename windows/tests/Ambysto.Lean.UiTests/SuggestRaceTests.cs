using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-17 phase C fix round 1 (intent/2026-10-20-asp-17-suggest.md, "Fix round 1 plan"): the two races of Suggest, held open by
/// the Debug-only QA argument <c>--suggest-delay &lt;ms&gt;</c> (MainWindow.DebugQa.cs: the walk of Temporary files or of Large and old waits
/// that long off the UI thread before its answer is applied), so the test can change the UI during the walk. ProDebug only.
/// Race 1: a listing started during the walk (the view opened again, a filter changed) must not leave a half answer: the suggestion is
/// what shows (Why texts, ticks, status). In Temporary files the read of the list waits for the walk. Race 2: a folder protected during
/// the walk (through the Settings view and the system folder picker) must have its suggested file not ticked, in Large and old and in
/// Temporary files. The fixtures are scratch folders in the real Downloads known folder and LeanUiSuggest-* children of the real Temp.</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class SuggestRaceTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const int Installers = 10;
    private const string Medium = "race-medium-0.bin";
    private const string Delay = "--suggest-delay";

    private static long InstallerBytes => Installers * 200 * KB;

    private static AppSession StartHeld(int milliseconds, params string[] arguments) =>
        ProApp.StartPro([.. arguments, Delay, milliseconds.ToString(CultureInfo.InvariantCulture)]);

    private static DownloadsFixture BuildTree(string name, int installers = Installers)
    {
        var fixture = new DownloadsFixture(name);
        for (var i = 0; i < installers; i++)
        {
            fixture.File($"race-setup-{i:D2}.zip", 200 * KB, 200, seed: i);
        }

        for (var i = 0; i < 2; i++)
        {
            fixture.File($"race-medium-{i}.bin", 150 * MB, 400, seed: 50 + i);
        }

        return fixture;
    }

    private static AutomationElement OpenLargeOld(AppSession app)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", Medium) is not null, Ui.ElementTimeout), "no row for " + Medium + ": " + Table.Dump(view, "LargeOldSelect"));
        return view;
    }

    private static void Invoke(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static string Prefix(string withOne) => withOne[..withOne.IndexOf('1', StringComparison.Ordinal)];

    // ---- what a settled answer looks like ---------------------------------------------------------------------------------

    /// <summary>Waits for the Large and old answer to settle and returns what is wrong with it (null: it is the suggestion of
    /// <paramref name="count"/> installers): the status line is <see cref="Text.SuggestedFiles"/>, the Why column and ClearSuggestion are
    /// there, every row is ticked and shows its Why. Two seconds are given to a late listing to overwrite it.</summary>
    private static string? LargeOldSettled(AppSession app, AutomationElement view, int count, long bytes, TimeSpan timeout)
    {
        var expected = Text.SuggestedFiles(count, ByteUnits.Format(bytes));
        if (!AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, timeout))
        {
            return $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\"";
        }

        Thread.Sleep(2000);
        var problems = new List<string>();
        if (SuggestUi.Status(app) != expected)
        {
            problems.Add($"the status line changed to \"{SuggestUi.Status(app)}\"");
        }

        if (AppSession.TryFind(view, "ClearSuggestion") is null)
        {
            problems.Add("no ClearSuggestion");
        }

        if (!SuggestUi.HasWhyColumn(view))
        {
            problems.Add("no Why column");
        }

        var boxes = Table.Controls(view, "LargeOldSelect");
        var prefix = SuggestUi.InstallerWhyPrefix();
        if (boxes.Count != count)
        {
            problems.Add($"{boxes.Count} rows, expected {count}: {Table.Dump(view, "LargeOldSelect")}");
        }

        if (boxes.Any(b => Table.State(b) != ToggleState.On))
        {
            problems.Add("a row is not ticked");
        }

        if (boxes.Any(b => !Table.RowName(b).Contains(prefix, StringComparison.Ordinal)))
        {
            problems.Add("a row shows no Why (a plain row of the filtered list under the Why header)");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Waits for the Temporary files answer to settle and returns what is wrong with it (null: it is the suggestion): the Why
    /// column shows, the fixture's suggested rows are ticked with their Why, the others are not, and the status line says
    /// <see cref="Text.SuggestTicked"/> of the ticked rows.</summary>
    private static string? TempSettled(AppSession app, AutomationElement view, SuggestTempFixture temp, TimeSpan timeout)
    {
        if (!AppSession.WaitUntil(() => SuggestUi.HasWhyColumn(view) && Table.ControlWith(view, "TempItem", temp.Old.Name) is { } b && Table.State(b) == ToggleState.On, timeout))
        {
            return $"no suggestion on screen (status \"{SuggestUi.Status(app)}\"): " + Table.Dump(view, "TempItem");
        }

        Thread.Sleep(2000);
        var problems = new List<string>();
        var prefix = SuggestUi.TempWhyPrefix();
        if (!SuggestUi.HasWhyColumn(view))
        {
            problems.Add("no Why column");
        }

        foreach (var item in temp.Suggested)
        {
            var box = Table.ControlWith(view, "TempItem", item.Name);
            if (box is null || Table.State(box) != ToggleState.On || !Table.RowName(box).Contains(Text.WhyOldTemp(item.NewestUtc), StringComparison.Ordinal))
            {
                problems.Add($"{item.Name} is not ticked with its Why");
            }
        }

        foreach (var item in temp.NotSuggested)
        {
            if (Table.ControlWith(view, "TempItem", item.Name) is { } box && Table.State(box) == ToggleState.On)
            {
                problems.Add($"{item.Name} is ticked");
            }
        }

        var ticked = Table.Controls(view, "TempItem").Count(b => Table.State(b) == ToggleState.On);
        if (Table.Controls(view, "TempItem").Any(b => Table.State(b) == ToggleState.On && !Table.RowName(b).Contains(prefix, StringComparison.Ordinal)))
        {
            problems.Add("a ticked row shows no Why");
        }

        if (SuggestUi.Status(app) != Text.SuggestTicked(ticked))
        {
            problems.Add($"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.SuggestTicked(ticked)}\"");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // ---- driving the app --------------------------------------------------------------------------------------------------

    /// <summary>Protects <paramref name="folder"/> through the Settings view and the system folder picker (as ProtectedFoldersTests does)
    /// and waits until the settings file holds it.</summary>
    private static void ProtectThroughSettings(AppSession app, string folder)
    {
        var settings = Ui.OpenView(app, "Settings", "SettingsView");
        Ui.Click(AppSession.Find(settings, "AddProtectedFolder", Ui.ElementTimeout));
        FolderPicker.Choose(app, folder, Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => (SettingsBackup.LastProtectedFolders() ?? []).Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)), Ui.ElementTimeout),
            "the settings file does not hold the protected folder " + folder);
    }

    /// <summary>Watches the status line from a second thread until stopped; true afterwards when it began with <paramref name="prefix"/>.</summary>
    private sealed class StatusWatch : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task sampler;
        private volatile bool seen;

        public StatusWatch(AppSession app, string prefix)
        {
            sampler = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (SuggestUi.Status(app).StartsWith(prefix, StringComparison.Ordinal))
                        {
                            seen = true;
                            return;
                        }
                    }
                    catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or AssertFailedException or System.Runtime.InteropServices.COMException)
                    {
                    }

                    Thread.Sleep(15);
                }
            });
        }

        public bool Seen
        {
            get
            {
                stop.Cancel();
                sampler.Wait(TimeSpan.FromSeconds(5));
                return seen;
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            sampler.Wait(TimeSpan.FromSeconds(5));
            stop.Dispose();
        }
    }

    private static void ChooseSize(AppSession app, AutomationElement view, long bytes)
    {
        var choice = AppSession.Find(view, "MinimumSize", Ui.ElementTimeout);
        if (choice.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
            Thread.Sleep(300);
        }

        var name = ByteUnits.Format(bytes);
        var condition = new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true);
        AutomationElement? option = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (option = Ui.TopWindows(app).SelectMany(t => t.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()).FirstOrDefault(o => o.Current.Name == name)) is not null, Ui.ElementTimeout),
            $"MinimumSize has no option \"{name}\"");
        ((SelectionItemPattern)option!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expand is not null && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
        {
            ((ExpandCollapsePattern)expand).Collapse();
        }
    }

    // ---- race 1 -------------------------------------------------------------------------------------------------------------

    // Race 1, Large and old: Suggest is pressed (the walk is held for 3 s), and during the hold Large and old is opened again (Overview,
    // then Large and old). The listing that opening asks for waits for the walk and never replaces a suggestion: once it settles the
    // suggestion is what shows, the ten installers ticked with their Why, the status line Text.SuggestedFiles(10, size).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void OpeningLargeAndOldAgainDuringTheWalkLeavesTheSuggestionOnScreen()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildTree("race-reopen");
        using var app = StartHeld(3000, "--scan", fixture.Path);
        var view = OpenLargeOld(app);
        var clock = Stopwatch.StartNew();
        Invoke(SuggestUi.Button(view));

        Ui.Click(Ui.SidebarItem(app, "Overview"));
        Ui.Click(Ui.SidebarItem(app, "LargeOld"));
        var inTime = clock.Elapsed < TimeSpan.FromMilliseconds(2500);
        view = Ui.FindAnywhere(app, "LargeOldView", Ui.ElementTimeout);

        var problem = LargeOldSettled(app, view, Installers, InstallerBytes, TimeSpan.FromSeconds(20));
        if (problem is not null && !inTime)
        {
            Assert.Inconclusive($"the view was opened again {clock.Elapsed.TotalSeconds:F1} s into a 3 s hold: too late to be during the walk");
        }

        Assert.IsNull(problem, "after opening Large and old again during the walk: " + problem);
    }

    // Race 1, Large and old, a filter: during the hold the size filter is changed (a listing of the filtered rows starts at once). Either
    // listing may win, but never a mix: afterwards the screen is wholly the suggestion (as above) or wholly the filtered list (no Why
    // column, no ClearSuggestion, nothing ticked, the filters on).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ChangingAFilterDuringTheWalkNeverLeavesAMixOfTheSuggestionAndTheList()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildTree("race-filter");
        using var app = StartHeld(3000, "--scan", fixture.Path);
        var view = OpenLargeOld(app);
        Invoke(SuggestUi.Button(view));

        ChooseSize(app, view, 50 * MB);

        Thread.Sleep(4000);
        if (AppSession.TryFind(view, "ClearSuggestion") is not null || SuggestUi.HasWhyColumn(view))
        {
            Assert.IsNull(LargeOldSettled(app, view, Installers, InstallerBytes, TimeSpan.FromSeconds(10)), "the suggestion shows but is not whole");
        }
        else
        {
            Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), "the filtered list shows with a row ticked: " + Table.Dump(view, "LargeOldSelect"));
            Assert.IsTrue(AppSession.TryFind(view, "MinimumSize") is { } size && size.Current.IsEnabled, "the filters are off with no suggestion");
        }
    }

    // Race 1, Temporary files (contract: the read of the list waits for the walk): Suggest is pressed (held for 3 s), and during the hold
    // Temporary files is opened again. The read waits for the walk and, as the suggestion showed, does not happen: once it settles the
    // Why column shows, the suggested rows are ticked with their Why, the others are not, and the status line is Text.SuggestTicked.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void OpeningTemporaryFilesAgainDuringTheWalkLeavesTheSuggestionOnScreen()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("race");
        using var app = StartHeld(3000);
        var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
        foreach (var item in temp.Suggested.Concat(temp.NotSuggested))
        {
            Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "TempItem", item.Name) is not null, TimeSpan.FromSeconds(90)), "no TempItem for " + item.Name);
        }

        var clock = Stopwatch.StartNew();
        Invoke(SuggestUi.Button(view));
        Ui.Click(Ui.SidebarItem(app, "Overview"));
        Ui.Click(Ui.SidebarItem(app, "TemporaryFiles"));
        var inTime = clock.Elapsed < TimeSpan.FromMilliseconds(2500);
        view = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);

        var problem = TempSettled(app, view, temp, TimeSpan.FromSeconds(20));
        if (problem is not null && !inTime)
        {
            Assert.Inconclusive($"the view was opened again {clock.Elapsed.TotalSeconds:F1} s into a 3 s hold: too late to be during the walk");
        }

        Assert.IsNull(problem, "after opening Temporary files again during the walk: " + problem);
    }

    // ---- race 2 -------------------------------------------------------------------------------------------------------------

    // Race 2, Large and old: Suggest is pressed (the walk is held for 12 s), and during the hold the folder <fixture>\keep, which holds
    // prot-setup.zip (an old installer like the twenty others), is protected through the Settings view. The suggestion that then appears
    // is judged again against the protected folders as they are now: it holds the twenty, all ticked, and not prot-setup.zip.
    // Inconclusive when the suggestion appeared before the folder was protected (the hold did not last).
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void AFolderProtectedDuringTheLargeAndOldWalkHasItsSuggestedFileNotTicked()
    {
        ProApp.RequireDebugApp();
        using var fixture = BuildTree("race-protect", installers: 20);
        fixture.File(@"keep\prot-setup.zip", 200 * KB, 200, seed: 99);
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartHeld(12000, "--scan", fixture.Path);
        var view = OpenLargeOld(app);
        using var watch = new StatusWatch(app, Prefix(Text.SuggestedFiles(1, "x")));
        Invoke(SuggestUi.Button(view));

        ProtectThroughSettings(app, fixture.Sub("keep"));

        if (watch.Seen)
        {
            Assert.Inconclusive("the suggestion appeared (the status line said \"Suggested ...\") before the folder was protected: the 12 s hold did not last");
        }

        view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        var problem = LargeOldSettled(app, view, 20, 20 * 200 * KB, TimeSpan.FromSeconds(30));
        Assert.IsNull(problem, "after protecting a folder during the walk: " + problem);
        Assert.IsNull(Table.ControlWith(view, "LargeOldSelect", "prot-setup.zip"), "the file in the folder protected during the walk is in the suggestion: " + Table.Dump(view, "LargeOldSelect"));
    }

    // Race 2, Temporary files: Suggest is pressed (held for 12 s), and during the hold a folder row of the list, a 60-day-old child of
    // Temp, is protected through the Settings view. After the walk that row is not ticked and shows no Why; the others are as ever.
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void AFolderProtectedDuringTheTempWalkIsNotTickedAndHasFreshHints()
    {
        ProApp.RequireDebugApp();
        using var temp = new SuggestTempFixture("race2");
        var guarded = Path.Combine(temp.TempFolder, "LeanUiSuggest-guarded-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(guarded);
        var inside = Path.Combine(guarded, "inside.bin");
        File.WriteAllBytes(inside, new byte[100 * 1024]);
        var old = SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(inside, old);
        Directory.SetLastWriteTimeUtc(guarded, old);
        try
        {
            using var settings = new SettingsBackup();
            SettingsBackup.WriteProtectedFolders();
            using var app = StartHeld(12000);
            var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            foreach (var name in temp.Suggested.Concat(temp.NotSuggested).Select(i => i.Name).Append(Path.GetFileName(guarded)))
            {
                Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "TempItem", name) is not null, TimeSpan.FromSeconds(90)), "no TempItem for " + name);
            }

            using var watch = new StatusWatch(app, Prefix(Text.SuggestTicked(1)));
            Invoke(SuggestUi.Button(view));

            ProtectThroughSettings(app, guarded);

            if (watch.Seen)
            {
                Assert.Inconclusive("the suggestion appeared (the status line said \"Suggest ticked ...\") before the folder was protected: the 12 s hold did not last");
            }

            view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            var problem = TempSettled(app, view, temp, TimeSpan.FromSeconds(30));
            Assert.IsNull(problem, "after protecting a folder during the walk: " + problem);
            var box = Table.ControlWith(view, "TempItem", Path.GetFileName(guarded));
            Assert.IsNotNull(box, "the protected row is not listed: " + Table.Dump(view, "TempItem"));
            Assert.AreEqual(ToggleState.Off, Table.State(box), "the folder protected during the walk is ticked");
            Assert.IsFalse(Table.RowName(box).Contains(SuggestUi.TempWhyPrefix(), StringComparison.Ordinal), "the folder protected during the walk shows a Why");

            // Fix round 2 (review MINOR 2): the rows shown are read again after the protection (fresh hints): the folder's row is display-only,
            // with its hint, and cannot be ticked by hand either.
            Assert.IsFalse(box.Current.IsEnabled, "the folder protected during the walk is still tickable by hand (its row has stale hints)");
            Assert.IsTrue(Table.RowName(box).Contains(Text.HintProtectedByYou, StringComparison.Ordinal), "the row of the protected folder does not carry its hint: " + Table.RowName(box));
            Table.TryToTick(box);
            Assert.AreEqual(ToggleState.Off, Table.State(Table.ControlWith(view, "TempItem", Path.GetFileName(guarded))!), "the protected folder was ticked by hand");
        }
        finally
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(guarded); attempt++)
            {
                try
                {
                    Directory.Delete(guarded, recursive: true);
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
}
