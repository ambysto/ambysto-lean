using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

// Independent, acceptance-first: intent/2026-10-24-asp-28-uninstall-results.md. No implementation was inspected.
[TestClass]
[DoNotParallelize]
public sealed class UninstallResultTests
{
    private static AutomationElement Find(AppSession app, string id) => Ui.FindAnywhere(app, id, Ui.ElementTimeout);
    private static AutomationElement Result(AppSession app, GoneApp fixture)
    {
        var result = Ui.FindAnywhere(app, "UninstallResult", TimeSpan.FromSeconds(75));
        Assert.AreEqual(fixture.Name, AppSession.Find(result, "UninstallResultApp", Ui.ElementTimeout).Current.Name);
        return result;
    }
    private static void NoResult(AppSession app, TimeSpan? timeout = null) =>
        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("UninstallResult") is { } r && !r.Current.IsOffscreen,
            timeout ?? TimeSpan.FromSeconds(3)), "an unexpected uninstall result was shown");
    private static AutomationElement Select(AppSession app, GoneApp fixture)
    {
        // Reopening the active sidebar view starts another inventory read. Reuse its settled selection instead.
        var view = app.FindInAnyWindow("UninstallView");
        if (view is null || view.Current.IsOffscreen) view = Ui.OpenView(app, "Uninstall", "UninstallView");
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var row = AppSession.WaitFor(() =>
        {
            var found = AppSession.FindAll(list, "UninstallRow").FirstOrDefault(r => r.Current.Name == fixture.Name);
            if (found is not null) return found;
            if (!list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var container)) return null;
            var item = ((ItemContainerPattern)container).FindItemByProperty(null, AutomationElement.NameProperty, fixture.Name);
            if (item is not null && item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) ((VirtualizedItemPattern)virtualized).Realize();
            return item;
        }, Ui.ElementTimeout);
        Assert.IsNotNull(row, "no test-owned uninstall row: " + fixture.Name);
        var selection = Ui.SelectionItemOf(row);
        if (!selection.Current.IsSelected) selection.Select();
        return view;
    }
    private static AutomationElement StartWatch(AppSession app, GoneApp fixture)
    {
        var before = SettingsWindows().Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        var view = Select(app, fixture);
        var action = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => action.Current.IsEnabled, Ui.ElementTimeout));
        Ui.Click(action);
        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => instruction.Current.Name == Text.UninstallInSettings(fixture.Name), Ui.ElementTimeout), "watch handoff must be established");
        AppSession.WaitUntil(() => SettingsWindows().Any(w => !before.Contains(Ui.Key(w))), TimeSpan.FromSeconds(10));
        foreach (var window in SettingsWindows().Where(w => !before.Contains(Ui.Key(w))))
        {
            try { if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern)) ((WindowPattern)pattern).Close(); }
            catch (ElementNotAvailableException) { }
        }
        return view;
    }
    private static AutomationElement[] SettingsWindows() => AutomationElement.RootElement.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow")).Cast<AutomationElement>()
        .Where(w => w.Current.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase)).ToArray();
    private static void RemoveOwnRegistration(GoneApp fixture) => Registry.CurrentUser.DeleteSubKeyTree(fixture.KeyPath);
    private static void CloseResult(AppSession app, string id = "UninstallResultClose")
    {
        Ui.Click(Find(app, id));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("UninstallResult") is not { } r || r.Current.IsOffscreen, Ui.ElementTimeout));
    }
    private static void OwnLeftoversUnticked(AppSession app, GoneApp fixture)
    {
        var view = Find(app, "UninstallView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LeftoverItem", fixture.Install) is not null
            && Table.ControlWith(view, "LeftoverItem", fixture.Leftover) is not null, Ui.ElementTimeout));
        foreach (var path in new[] { fixture.Install, fixture.Leftover })
            Assert.AreEqual(ToggleState.Off, Table.State(Table.ControlWith(view, "LeftoverItem", path)!), "no leftover preselection: " + path);
        Assert.IsFalse(AppSession.Find(view, "MoveLeftovers", Ui.ElementTimeout).Current.IsEnabled);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Install, "app.dat")) && File.Exists(Path.Combine(fixture.Leftover, "settings.dat")));
    }
    private static int JournalCount(string path, string result)
    {
        if (!File.Exists(JournalBackup.Path)) return 0;
        using var stream = new FileStream(JournalBackup.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("originalPath", out var original) && string.Equals(original.GetString(), path, StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("result", out var value) && value.GetString() == result) count++;
            }
            catch (JsonException) { }
        }
        return count;
    }
    private static void RestoreOnlyOwnLeftovers(GoneApp fixture)
    {
        var missing = new[] { fixture.Install, fixture.Leftover }.Where(p => !Directory.Exists(p) && RecycleBin.Holding(p) is not null).ToArray();
        if (missing.Length == 0) return;
        using var app = AppSession.Start();
        var history = Ui.OpenView(app, "History", "HistoryView");
        foreach (var path in missing)
        {
            var button = AppSession.WaitFor(() => Table.ControlFor(history, "PutBackEntry", path), Ui.ElementTimeout);
            Assert.IsNotNull(button, "cleanup history entry for own fixture: " + path);
            Ui.Click(button);
            Assert.IsTrue(AppSession.WaitUntil(() => Directory.Exists(path) && RecycleBin.Holding(path) is null
                && JournalCount(path, "restored") == 1, Ui.MoveTimeout), "cleanup Put back of " + path);
        }
    }

    [TestMethod]
    [TestCategory("Console")]
    public void LaunchSelectionAndARegistrationStillPresentNeverAnnounceRemoval()
    {
        using var fixture = new GoneApp();
        using var app = AppSession.Start();
        NoResult(app);
        Select(app, fixture);
        NoResult(app);
        StartWatch(app, fixture);
        NoResult(app, TimeSpan.FromSeconds(15)); // exceeds the existing 10s absent-for interval; registration is still present
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Leftover, "settings.dat")));
    }

    [TestMethod]
    [TestCategory("Console")]
    public void GoneAppWithTwoLocationsExplainsNothingWasCleanedAndDetailsPreservesUntickedRows()
    {
        using var fixture = new GoneApp();
        using var app = AppSession.Start();
        StartWatch(app, fixture);
        RemoveOwnRegistration(fixture);
        var result = Result(app, fixture);
        Assert.AreEqual(Ui.Copy("UninstallFoundLeftovers", 2), AppSession.Find(result, "UninstallResultSummary", Ui.ElementTimeout).Current.Name);
        CloseResult(app, "UninstallResultDetails");
        OwnLeftoversUnticked(app, fixture);
        Ui.BackToOverview(app, "UninstallView");
        Ui.OpenView(app, "Uninstall", "UninstallView");
        NoResult(app); // refresh/re-entry must not repeat the once-only removal notification
        OwnLeftoversUnticked(app, fixture);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void GoneAppWithoutLocationsHasTruthfulSummaryAndCloseFallsBackToUninstallSidebar()
    {
        using var fixture = new GoneApp();
        Directory.Delete(fixture.Install, recursive: true);
        Directory.Delete(fixture.Leftover, recursive: true); // only this test's fixture; no candidates remain
        using var app = AppSession.Start();
        StartWatch(app, fixture);
        RemoveOwnRegistration(fixture);
        var result = Result(app, fixture);
        Assert.AreEqual(Ui.Copy("UninstallNoLeftovers"), AppSession.Find(result, "UninstallResultSummary", Ui.ElementTimeout).Current.Name);
        CloseResult(app);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(Ui.SidebarItem(app, "Uninstall")), TimeSpan.FromSeconds(3)),
            "the removed row and its action are unavailable, so result Close must focus the Uninstall sidebar");
        NoResult(app);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void LeavingTheViewSuppressesTheCapturedGoneNotificationWithoutPreselectingItsLaterRows()
    {
        using var fixture = new GoneApp();
        using var app = AppSession.Start();
        StartWatch(app, fixture);
        Ui.BackToOverview(app, "UninstallView");
        RemoveOwnRegistration(fixture);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(fixture.Name), TimeSpan.FromSeconds(75)), "underlying gone detection still finishes");
        NoResult(app);
        Ui.OpenView(app, "Uninstall", "UninstallView");
        NoResult(app);
        OwnLeftoversUnticked(app, fixture);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void SelectingAnotherAppSuppressesTheOldCapturedNotification()
    {
        using var first = new GoneApp();
        using var second = new GoneApp();
        using var app = AppSession.Start();
        StartWatch(app, first);
        Select(app, second);
        RemoveOwnRegistration(first);
        NoResult(app, TimeSpan.FromSeconds(15));
        Assert.IsTrue(File.Exists(Path.Combine(first.Leftover, "settings.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(second.Leftover, "settings.dat")));
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    [DataRow(false)]
    [DataRow(true)]
    public void CleanupCountsActualSuccessfulMovesAndRoutesPutBackWithoutMovingTwice(bool blockOne)
    {
        using var journal = new JournalBackup();
        JournalBackup.Write([]);
        using var fixture = new GoneApp();
        var installId = MoveFixture.Identity(fixture.Install);
        var leftoverId = MoveFixture.Identity(fixture.Leftover);
        var installHash = MoveFixture.Hash(Path.Combine(fixture.Install, "app.dat"));
        var leftoverHash = MoveFixture.Hash(Path.Combine(fixture.Leftover, "settings.dat"));
        try
        {
            using var app = AppSession.Start();
            StartWatch(app, fixture);
            RemoveOwnRegistration(fixture);
            Result(app, fixture);
            CloseResult(app, "UninstallResultDetails");
            OwnLeftoversUnticked(app, fixture);
            var view = Find(app, "UninstallView");
            foreach (var path in new[] { fixture.Install, fixture.Leftover })
                Table.Tick(Table.ControlWith(view, "LeftoverItem", path)!, () => Table.ControlWith(view, "LeftoverItem", path), path);
            using (var held = blockOne ? new FileStream(Path.Combine(fixture.Leftover, "settings.dat"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
            {
                Ui.Click(AppSession.Find(view, "MoveLeftovers", Ui.ElementTimeout));
                Find(app, "ConfirmMove");
                Ui.Click(Find(app, "ConfirmMoveButton"));
                var result = Result(app, fixture);
                var moved = blockOne ? 1 : 2;
                foreach (var pair in new[] { ("UninstallApprovedCount", "Approved: 2"), ("UninstallMovedCount", "Moved: " + moved),
                    ("UninstallUnsuccessfulCount", "Unsuccessful: " + (blockOne ? 1 : 0)), ("UninstallUnattemptedCount", "Not attempted: 0") })
                    Assert.AreEqual(pair.Item2, AppSession.Find(result, pair.Item1, Ui.ElementTimeout).Current.Name);
                Assert.AreEqual(Ui.Copy(blockOne ? "UninstallCleanupPartial" : "UninstallCleanupComplete"),
                    AppSession.Find(result, "UninstallResultSummary", Ui.ElementTimeout).Current.Name);
                Assert.AreEqual(1, JournalCount(fixture.Install, "trashed"));
                Assert.AreEqual(blockOne ? 0 : 1, JournalCount(fixture.Leftover, "trashed"));
                if (blockOne) Assert.AreEqual(1, JournalCount(fixture.Leftover, "skipped"));
                CloseResult(app, "UninstallResultPutBack");
                var putBack = Find(app, "PutBackSession");
                Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(putBack), TimeSpan.FromSeconds(3)), "result Put back only focuses the existing action");
                Assert.IsNotNull(RecycleBin.Holding(fixture.Install), "navigation must not itself perform Put back");
                Ui.Click(putBack);
                Assert.IsTrue(AppSession.WaitUntil(() => Directory.Exists(fixture.Install) && Directory.Exists(fixture.Leftover)
                    && RecycleBin.Holding(fixture.Install) is null && RecycleBin.Holding(fixture.Leftover) is null, Ui.MoveTimeout));
            }
            Assert.AreEqual(installId, MoveFixture.Identity(fixture.Install));
            Assert.AreEqual(leftoverId, MoveFixture.Identity(fixture.Leftover));
            Assert.AreEqual(installHash, MoveFixture.Hash(Path.Combine(fixture.Install, "app.dat")));
            Assert.AreEqual(leftoverHash, MoveFixture.Hash(Path.Combine(fixture.Leftover, "settings.dat")));
            NoResult(app);
        }
        finally { RestoreOnlyOwnLeftovers(fixture); }
    }
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void InitialCloseSuppressesTheCapturedCleanupResultWhileApprovedMovesStillFinish()
    {
        ProApp.RequireDebugApp();
        using var journal = new JournalBackup();
        JournalBackup.Write([]);
        using var fixture = new GoneApp();
        try
        {
            using var app = AppSession.Start("--move-delay", "3000");
            StartWatch(app, fixture);
            RemoveOwnRegistration(fixture);
            Result(app, fixture);
            CloseResult(app, "UninstallResultDetails");
            OwnLeftoversUnticked(app, fixture);
            var view = Find(app, "UninstallView");
            foreach (var path in new[] { fixture.Install, fixture.Leftover })
                Table.Tick(Table.ControlWith(view, "LeftoverItem", path)!, () => Table.ControlWith(view, "LeftoverItem", path), path);
            Ui.Click(AppSession.Find(view, "MoveLeftovers", Ui.ElementTimeout));
            Find(app, "ConfirmMove");
            Ui.Click(Find(app, "ConfirmMoveButton"));
            var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name.StartsWith(Text.MovingProgress(1, 2, string.Empty), StringComparison.Ordinal), Ui.ElementTimeout));
            Assert.AreEqual(0, JournalCount(fixture.Install, "trashed") + JournalCount(fixture.Leftover, "trashed"), "precondition: close precedes completed approved work");
            Assert.IsTrue(app.Process.CloseMainWindow());
            Assert.IsFalse(app.Process.HasExited, "ASP-24 holds destruction until the approved session ends");
            var deadline = DateTime.UtcNow.Add(Ui.MoveTimeout);
            while (!app.Process.WaitForExit(100))
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "held close never completed");
                try
                {
                    Assert.IsFalse(app.FindInAnyWindow("UninstallResult") is { } r && !r.Current.IsOffscreen,
                        "a captured cleanup result must not show after initial closing intent");
                }
                catch (ElementNotAvailableException) when (app.Process.HasExited) { }
            }
            Assert.AreEqual(0, app.Process.ExitCode);
            Assert.AreEqual(1, JournalCount(fixture.Install, "trashed"));
            Assert.AreEqual(1, JournalCount(fixture.Leftover, "trashed"));
            Assert.IsNotNull(RecycleBin.Holding(fixture.Install));
            Assert.IsNotNull(RecycleBin.Holding(fixture.Leftover));
        }
        finally { RestoreOnlyOwnLeftovers(fixture); }
    }

}
