using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-9 UI contract (intent/2026-10-18-asp-9-back-to-map.md) as ASP-14 changes it
/// (intent/2026-10-18-asp-14-start-and-sidebar.md, "The content"): the views (Large and old, Duplicates, Uninstall,
/// History, Settings, About) open from their sidebar items and have no <c>BackToMap</c> button any more; the Overview
/// item, Esc, Alt+Left and the mouse's back button (XButton1) return to Overview, the map with its folder and scan
/// unchanged, and the keyboard focus goes to the <c>Overview</c> item; on the map these keys do nothing; with the
/// <c>ConfirmMove</c> dialog open, Esc closes only the dialog; going back cancels nothing that runs (a duplicates search
/// goes on and its result is there when the view is opened again; a scan goes on); the old Close buttons are gone. Keys
/// pressed: Tab, Space (on the sidebar items), Enter (on a folder tile), Shift+F10, Esc, Alt+Left; the mouse's back button
/// only over the app's own window. Nothing is ever moved: every confirmation is closed by Esc (the test's own items under
/// <c>%USERPROFILE%\LeanUiMove-*</c>), Reveal, OpenRecycleBin and UninstallInSettings are never pressed.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ReturnToOverviewTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const long GB = ByteUnits.GB;
    private const string Overview = "Overview";
    private const string Holder = "holder";

    /// <summary>The six views: the sidebar item that opens each (ASP-14: the ids of the old view buttons), and its AutomationId.</summary>
    private static readonly (string Opener, string ViewId)[] Views =
    [
        ("LargeOld", "LargeOldView"),
        ("Duplicates", "DuplicatesView"),
        ("Uninstall", "UninstallView"),
        ("History", "HistoryView"),
        ("Settings", "SettingsView"),
        ("About", "AboutView"),
    ];

    /// <summary>The bottom Close buttons that ASP-9 removed, and the BackToMap buttons that ASP-14 removes.</summary>
    private static readonly string[] OldCloseIds = ["CloseLargeOld", "CloseDuplicates", "CloseUninstall", "CloseHistory", "CloseSettings", "CloseAbout", "BackToMap"];

    /// <summary>The label of ASP-9's BackToMap button, which no button carries any more.</summary>
    private const string OldBackLabel = "Back to map";

    private enum Way
    {
        Button,
        Escape,
        AltLeft,
        MouseBack,
    }

    // ---- fixtures and helpers ------------------------------------------------------------------------------------------

    /// <summary>A small tree with a folder to drill into: holder (a.bin 300 KB, b.bin 200 KB, inner\c.bin 100 KB) and
    /// other.bin (50 KB). Nothing in it is moved.</summary>
    private static Fixture NavigationTree()
    {
        var fixture = new Fixture("ui-back");
        fixture.File(@"holder\a.bin", 300 * KB);
        fixture.File(@"holder\b.bin", 200 * KB);
        fixture.File(@"holder\inner\c.bin", 100 * KB);
        fixture.File("other.bin", 50 * KB);
        return fixture;
    }

    /// <summary>Scans <paramref name="fixture"/>, then shows its folder holder in the map (docs/spec/01 Map: a folder tile
    /// drills down; here by Enter on the focused tile), so that a jump back to the scanned folder would show.</summary>
    private static AppSession StartInHolder(Fixture fixture)
    {
        var app = AppSession.Start("--scan", fixture.Path);
        var map = Ui.WaitForScan(app);
        Ui.Focus(Ui.TileOf(map, Holder));
        Keys.Press(Keys.Enter);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasTile(Ui.Map(app), "a.bin") && !Ui.HasTile(Ui.Map(app), Holder), Ui.ElementTimeout),
            "precondition: Enter on the holder tile did not show holder; tiles: [" + string.Join(" | ", Ui.TileNames(Ui.Map(app))) + "]");
        return app;
    }

    /// <summary>What the map shows: each tile's name and path (sorted), and the breadcrumbs, in order.</summary>
    private static string MapState(AppSession app)
    {
        try
        {
            var window = app.Window(Ui.WindowTimeout);
            var map = AppSession.TryFind(window, "Map");
            if (map is null || map.Current.IsOffscreen)
            {
                return "(no map shown)";
            }

            var tiles = AppSession.FindAll(map, "Tile").Select(t => t.Current.Name + " @ " + t.Current.HelpText).OrderBy(s => s, StringComparer.Ordinal);
            var crumbs = AppSession.FindAll(window, "Breadcrumb").Select(c => c.Current.Name);
            return "tiles [" + string.Join(" | ", tiles) + "], breadcrumbs [" + string.Join(" > ", crumbs) + "]";
        }
        catch (ElementNotAvailableException)
        {
            return "(changing)";
        }
    }

    /// <summary>True while the view <paramref name="viewId"/> is in the app's windows and on screen.</summary>
    private static bool Shown(AppSession app, string viewId)
    {
        try
        {
            return app.FindInAnyWindow(viewId) is { } view && !view.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string? ShownView(AppSession app) => Views.Select(v => v.ViewId).FirstOrDefault(id => Shown(app, id));

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string FocusDump() => Focused() is { } f ? Ui.Describe(f) : "nothing";

    private static AutomationElement Opener(AppSession app, string opener) => Ui.SidebarItem(app, opener);

    /// <summary>A spot in the sidebar's empty band between History and Settings (no control there), for the mouse's back
    /// button.</summary>
    private static System.Windows.Point SidebarGap(AppSession app)
    {
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        Assert.IsFalse(sidebar.IsEmpty || history.IsEmpty || settings.IsEmpty, "precondition: the sidebar, History and Settings have bounding rectangles");
        Assert.IsTrue(settings.Top - history.Bottom >= 20, $"precondition: no empty band between History ({history}) and Settings ({settings})");
        return new System.Windows.Point(sidebar.Left + (sidebar.Width / 2), (history.Bottom + settings.Top) / 2);
    }

    /// <summary>Goes back from the view opened by <paramref name="opener"/> by <paramref name="way"/>: the Overview item
    /// clicked, or the keys with the keyboard focus on the view's own sidebar item (where a click on it leaves it), or the
    /// mouse's back button over the sidebar's empty band.</summary>
    private static void GoBack(AppSession app, string opener, Way way)
    {
        switch (way)
        {
            case Way.Button:
                Ui.Click(Ui.SidebarItem(app, Overview));
                break;
            case Way.Escape:
                Ui.Focus(Opener(app, opener));
                Keys.Press(Keys.Escape);
                break;
            case Way.AltLeft:
                Ui.Focus(Opener(app, opener));
                Keys.PressWithAlt(Keys.Left);
                break;
            case Way.MouseBack:
                Ui.Focus(Opener(app, opener));
                var spot = SidebarGap(app);
                Mouse.BackButtonAt(spot.X, spot.Y);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(way));
        }
    }

    /// <summary>After going back from <paramref name="viewId"/>: the view is gone, the map shows <paramref name="before"/>,
    /// no scan has started, and the keyboard focus is on the sidebar's Overview item (ASP-14).</summary>
    private static void AssertBackOnTheMap(AppSession app, string viewId, string before, string how)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, viewId), Ui.ElementTimeout), $"{how}: {viewId} is still shown");
        Assert.IsTrue(AppSession.WaitUntil(() => MapState(app) == before, Ui.ElementTimeout),
            $"{how}: the map changed: was {before}, now {MapState(app)}");
        Assert.IsFalse(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(1)), $"{how}: a scan started");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(Ui.SidebarItem(app, Overview)), TimeSpan.FromSeconds(5)),
            $"{how}: the keyboard focus is on {FocusDump()}, not on the Overview item");
    }

    /// <summary>For each view: open it from its sidebar item, go back by <paramref name="way"/>, check the map.</summary>
    private static void AssertEachViewGoesBack(Way way)
    {
        using var fixture = NavigationTree();
        using var app = StartInHolder(fixture);
        var before = MapState(app);
        foreach (var (opener, viewId) in Views)
        {
            Ui.OpenView(app, opener, viewId);
            Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, viewId), Ui.ElementTimeout), viewId + " did not open");
            GoBack(app, opener, way);
            AssertBackOnTheMap(app, viewId, before, $"{way} in {viewId}");
        }
    }

    // ---- the sidebar opens the views; the old buttons -----------------------------------------------------------------

    // ASP-14 "The content" (in place of ASP-9's "The back button"): each view opens by keyboard, Space on its sidebar item,
    // in the content to the right of the sidebar, which stays shown beside it with the view's item.
    [TestMethod]
    [TestCategory("Console")]
    public void EachViewOpensBySpaceOnItsSidebarItemBesideTheSidebar()
    {
        using var fixture = NavigationTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var problems = new List<string>();
        foreach (var (opener, viewId) in Views)
        {
            Ui.Focus(Opener(app, opener));
            Keys.Press(Keys.Space);
            var view = Ui.FindAnywhere(app, viewId, Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, viewId), Ui.ElementTimeout), viewId + " did not open by Space on " + opener);
            var sidebar = Ui.Sidebar(app);
            var v = view.Current.BoundingRectangle;
            var b = sidebar.Current.BoundingRectangle;
            if (sidebar.Current.IsOffscreen || b.IsEmpty)
            {
                problems.Add($"{viewId}: the sidebar is not shown beside it");
            }
            else if (v.Left < b.Right - 1)
            {
                problems.Add($"{viewId}: the view {v} is not to the right of the sidebar {b}");
            }

            if (Opener(app, opener).Current.IsOffscreen)
            {
                problems.Add($"{viewId}: its sidebar item {opener} is not shown");
            }

            Ui.Click(Opener(app, Overview));
            Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, viewId), Ui.ElementTimeout), viewId + " is still shown after the Overview item");
        }

        Assert.AreEqual(0, problems.Count, "views beside the sidebar:\n" + string.Join("\n", problems));
    }

    // Contract "The old buttons" (ASP-9) and ASP-14 "Every view loses its BackToMap button": with each view open, no
    // element with the old Close AutomationIds (CloseLargeOld, CloseDuplicates, CloseUninstall, CloseHistory, CloseSettings,
    // CloseAbout) or BackToMap is in any window of the app, and no button of the view is named Text.CloseView or "Back to
    // map": the sidebar is the way back.
    [TestMethod]
    [TestCategory("Console")]
    public void NoViewHasAnOldCloseButton()
    {
        using var fixture = NavigationTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var problems = new List<string>();
        foreach (var (opener, viewId) in Views)
        {
            var view = Ui.OpenView(app, opener, viewId);
            Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, viewId), Ui.ElementTimeout), viewId + " did not open");
            System.Threading.Thread.Sleep(300);
            foreach (var id in OldCloseIds)
            {
                problems.AddRange(Ui.FindAllAnywhere(app, id).Select(e => $"{viewId}: {Ui.Describe(e)}"));
            }

            var named = view.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, Text.CloseView)));
            problems.AddRange(named.Cast<AutomationElement>().Select(e => $"{viewId}: a button named \"{Text.CloseView}\": {Ui.Describe(e)}"));
            var back = view.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, OldBackLabel)));
            problems.AddRange(back.Cast<AutomationElement>().Select(e => $"{viewId}: a button named \"{OldBackLabel}\": {Ui.Describe(e)}"));
            Ui.Click(Opener(app, Overview));
            Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, viewId), Ui.ElementTimeout), viewId + " is still shown after the Overview item");
        }

        Assert.AreEqual(0, problems.Count, "old Close buttons:\n" + string.Join("\n", problems));
    }

    // ---- the ways back -------------------------------------------------------------------------------------------------

    // Contract "Returning" (ASP-9) with ASP-14: the Overview item, clicked in each view, returns to the map; the map still
    // shows the folder it showed (holder, drilled into before), with the same tiles and breadcrumbs; no scan starts; the
    // keyboard focus is on the Overview item.
    [TestMethod]
    [TestCategory("Console")]
    public void TheOverviewItemReturnsToTheSameMapWithTheFocusOnIt() => AssertEachViewGoesBack(Way.Button);

    // ASP-14 "The content": Esc in each view returns to Overview, exactly as the Overview item does, the focus on it.
    [TestMethod]
    [TestCategory("Console")]
    public void EscapeReturnsToTheSameMapWithTheFocusOnOverview() => AssertEachViewGoesBack(Way.Escape);

    // ASP-14 "The content": Alt+Left in each view returns to Overview, exactly as the Overview item does, the focus on it.
    [TestMethod]
    [TestCategory("Console")]
    public void AltLeftReturnsToTheSameMapWithTheFocusOnOverview() => AssertEachViewGoesBack(Way.AltLeft);

    // ASP-14 "The content": the mouse's back button (XButton1, real input through mouse_event with the cursor over the
    // sidebar's empty band) in each view returns to Overview, exactly as the Overview item does, the focus on it.
    [TestMethod]
    [TestCategory("Console")]
    public void TheMouseBackButtonReturnsToTheSameMapWithTheFocusOnOverview() => AssertEachViewGoesBack(Way.MouseBack);

    // Contract "Keys and mouse": on the map itself Esc, Alt+Left and the mouse's back button do nothing: the map keeps the
    // folder it shows (holder: none of them goes up a folder), no view opens, no scan starts, the app keeps running.
    [TestMethod]
    [TestCategory("Console")]
    public void EscapeAltLeftAndTheMouseBackButtonDoNothingOnTheMap()
    {
        using var fixture = NavigationTree();
        using var app = StartInHolder(fixture);
        var before = MapState(app);
        var window = app.Window(Ui.WindowTimeout);
        var status = AppSession.Find(window, "Status", Ui.ElementTimeout);
        var actions = new (string How, Action Press)[]
        {
            ("Esc", () => Keys.Press(Keys.Escape)),
            ("Alt+Left", () => Keys.PressWithAlt(Keys.Left)),
            ("the mouse's back button", () =>
            {
                var r = status.Current.BoundingRectangle;
                Assert.IsFalse(r.IsEmpty, "precondition: the Status text has a bounding rectangle");
                Mouse.BackButtonAt(r.Left + Math.Min(20, r.Width / 2), r.Top + (r.Height / 2));
            }),
        };

        foreach (var (how, press) in actions)
        {
            Ui.Focus(AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout));
            press();
            Assert.IsFalse(AppSession.WaitUntil(() => MapState(app) != before || ShownView(app) is not null || Ui.ScanRunning(app) || app.Process.HasExited, Ui.NeverWatch),
                $"{how} on the map did something: map {MapState(app)} (was {before}), view shown {ShownView(app) ?? "none"}, scan running {Ui.ScanRunning(app)}, exited {app.Process.HasExited}");
        }
    }

    // ---- the confirmation dialog -------------------------------------------------------------------------------------

    private static AutomationElement OpenConfirmation(AppSession app, Action start)
    {
        start();
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500); // the opening animation hands the dialog the keyboard
        return dialog;
    }

    /// <summary>Esc with the dialog's CancelMove focused: the dialog closes.</summary>
    private static void EscapeTheConfirmation(AppSession app, string where)
    {
        Ui.Focus(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), $"{where}: Esc did not close the confirmation");
    }

    private static void AssertNothingMoved(AppSession app, string path, (ulong Volume, UInt128 FileId) identity, string where)
    {
        var report = app.FindInAnyWindow("MoveReport");
        Assert.IsTrue(report is null || report.Current.IsOffscreen, $"{where}: a move report appeared");
        Assert.IsTrue(File.Exists(path), $"{where}: {path} was moved");
        Assert.AreEqual(identity, MoveFixture.Identity(path), $"{where}: {path} is not the same file");
        Assert.IsNull(RecycleBin.Holding(path), $"{where}: {path} is in the Recycle Bin");
    }

    /// <summary>For <see cref="Ui.NeverWatch"/>: <paramref name="viewId"/> stays shown and <paramref name="unchanged"/>
    /// stays true.</summary>
    private static void AssertViewStays(AppSession app, string viewId, Func<bool> unchanged, string where)
    {
        Assert.IsFalse(AppSession.WaitUntil(() => !Shown(app, viewId) || !unchanged(), Ui.NeverWatch),
            $"{where}: after Esc closed the confirmation, the view did not stay as it was (shown: {Shown(app, viewId)}, unchanged: {unchanged()}, map: {MapState(app)})");
    }

    // Contract "Keys and mouse": while ConfirmMove is open, Esc only closes the dialog. (a) From a map tile's Move on the
    // test's own file: Esc closes the dialog and the map stays as it was (same tiles, no view opened); (b) from MoveLargeOld
    // with a row ticked in Large and old (ASP-10: tick boxes): the view stays open, the row stays ticked; (c) from the
    // Duplicates Move with a copy ticked (copy-a; copy-b, the newer, is the kept copy): the view stays open, the copy stays
    // ticked. Nothing is moved; each view is left by the Overview item afterwards.
    [TestMethod]
    [TestCategory("Console")]
    public void EscapeWithTheConfirmationOpenClosesOnlyTheDialog()
    {
        using var fixture = new MoveFixture("asp9-esc");
        var single = fixture.File("move-me.bin", 100 * KB, seed: 91);
        var old = fixture.File("big-old.bin", 101 * MB, seed: 92);
        fixture.SetModified("big-old.bin", DateTime.UtcNow.AddYears(-3));
        var content = new byte[(int)(2 * MB)];
        new Random(9_3).NextBytes(content);
        var copyA = fixture.FileWithBytes(@"one\copy-a.dat", content);
        fixture.FileWithBytes(@"two\copy-b.dat", content);
        fixture.SetModified(@"one\copy-a.dat", DateTime.UtcNow.AddDays(-2));
        fixture.SetModified(@"two\copy-b.dat", DateTime.UtcNow.AddDays(-1));
        var facts = new[] { single, old, copyA }.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var mapBefore = MapState(app);

        // (a) the map
        OpenConfirmation(app, () =>
        {
            var move = Ui.MenuItemOf(app, Ui.TileOf(Ui.Map(app), "move-me.bin"), "MoveToRecycleBin");
            Assert.IsTrue(move.Current.IsEnabled, "precondition: Move to Recycle Bin… is enabled on move-me.bin");
            Ui.Click(move);
        });
        EscapeTheConfirmation(app, "map");
        Assert.IsFalse(AppSession.WaitUntil(() => MapState(app) != mapBefore || ShownView(app) is not null || app.Process.HasExited, Ui.NeverWatch),
            $"map: after Esc closed the confirmation, the map did not stay as it was: {MapState(app)} (was {mapBefore}), view shown {ShownView(app) ?? "none"}");
        AssertNothingMoved(app, single, facts[single], "map");

        // (b) Large and old
        var largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        AutomationElement? OldBox() => Table.ControlFor(largeOld, "LargeOldSelect", old);
        Assert.IsTrue(AppSession.WaitUntil(() => OldBox() is not null, Ui.ElementTimeout),
            "precondition: no Large and old row for big-old.bin: " + Table.Dump(largeOld, "LargeOldSelect"));
        Table.Tick(OldBox()!, OldBox, "precondition: big-old.bin's LargeOldSelect");
        var moveLargeOld = AppSession.Find(largeOld, "MoveLargeOld", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => moveLargeOld.Current.IsEnabled, TimeSpan.FromSeconds(3)), "precondition: MoveLargeOld is disabled with a row ticked");
        OpenConfirmation(app, () => Ui.Click(moveLargeOld));
        EscapeTheConfirmation(app, "Large and old");
        AssertViewStays(app, "LargeOldView", () =>
        {
            try
            {
                return OldBox() is { } box && Table.State(box) == ToggleState.On;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }, "Large and old");
        AssertNothingMoved(app, old, facts[old], "Large and old");
        Ui.Click(Opener(app, Overview));
        AssertBackOnTheMap(app, "LargeOldView", mapBefore, "the Overview item after the Large and old confirmation");

        // (c) Duplicates
        var duplicates = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(duplicates, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(duplicates, "DuplicateGroup").Count == 1, Ui.DuplicatesTimeout), "precondition: one duplicate group: " + Ui.Dump(duplicates));
        AutomationElement? CopyA() => Table.ControlFor(duplicates, "DuplicateCopy", copyA);
        bool Ticked() => CopyA() is { } c && ((TogglePattern)c.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
        var copy = CopyA();
        Assert.IsNotNull(copy, "precondition: no DuplicateCopy for " + copyA);
        ((TogglePattern)copy.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Assert.IsTrue(AppSession.WaitUntil(Ticked, TimeSpan.FromSeconds(3)), "precondition: copy-a did not tick");
        var move = AppSession.Find(duplicates, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "precondition: the Duplicates Move is disabled with a copy ticked");
        OpenConfirmation(app, () => Ui.Click(move));
        EscapeTheConfirmation(app, "Duplicates");
        AssertViewStays(app, "DuplicatesView", () =>
        {
            try
            {
                return Ticked() && AppSession.FindAll(duplicates, "DuplicateGroup").Count == 1;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }, "Duplicates");
        AssertNothingMoved(app, copyA, facts[copyA], "Duplicates");
        ((TogglePattern)CopyA()!.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Ui.Click(Opener(app, Overview));
        AssertBackOnTheMap(app, "DuplicatesView", mapBefore, "the Overview item after the Duplicates confirmation");
    }

    // ---- running work ------------------------------------------------------------------------------------------------

    private const string OldName = "big-old.bin";

    // Contract "Running work": going back does not cancel a duplicates search. A search over three 16 GB sparse files (all
    // zeros, allocated about 0: every byte is compared, nothing large is written) is started; the Overview item returns to the map
    // while it runs; there the tile menu's Move stays disabled (moves wait for a running search: it is still running) and
    // the status is not Text.DuplicatesCancelled; Duplicates opened again shows the search still running (CancelDuplicates
    // enabled). Esc returns to the map again; once the search has ended there (the tile Move is enabled again), Duplicates
    // opened again shows its result, the one group, without FindDuplicates being pressed again. Nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    public void ADuplicatesSearchKeepsRunningAfterGoingBackAndItsResultIsThere()
    {
        using var fixture = new Fixture("ui-back-search", Fixture.ProfileBaseFolder());
        fixture.SparseFile(@"s1\slow.dat", 16 * GB);
        fixture.SparseFile(@"s2\slow.dat", 16 * GB);
        fixture.SparseFile(@"s3\slow.dat", 16 * GB);
        fixture.File(OldName, 101 * MB);
        fixture.SetModified(OldName, DateTime.UtcNow.AddYears(-3));

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        var clock = Stopwatch.StartNew();
        var cancel = AppSession.Find(view, "CancelDuplicates", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => cancel.Current.IsEnabled, TimeSpan.FromSeconds(5)), "precondition: the search did not start (CancelDuplicates stays disabled)");

        Ui.Click(Opener(app, Overview));
        Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, "DuplicatesView"), Ui.ElementTimeout), "DuplicatesView is still shown after the Overview item");
        Assert.IsFalse(Ui.TileMoveEnabled(app, Ui.Map(app), OldName),
            $"on the map {clock.Elapsed.TotalSeconds:F1} s into the search, the tile Move is enabled: the search was cancelled by going back, or ended already (then make the sparse files longer); status \"{status.Current.Name}\"");
        Assert.AreNotEqual(Text.DuplicatesCancelled, status.Current.Name, "going back cancelled the duplicates search");

        view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "CancelDuplicates") is { } c && c.Current.IsEnabled, TimeSpan.FromSeconds(3)),
            $"Duplicates opened again {clock.Elapsed.TotalSeconds:F1} s into the search does not show it running (CancelDuplicates disabled): {Ui.Dump(view)}");
        Console.WriteLine($"still running {clock.Elapsed.TotalSeconds:F1} s into the search, after going back and opening Duplicates again");

        Ui.Focus(Opener(app, "Duplicates"));
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, "DuplicatesView"), Ui.ElementTimeout), "DuplicatesView is still shown after Esc");
        var ended = AppSession.WaitUntil(() =>
        {
            System.Threading.Thread.Sleep(2000);
            return Ui.TileMoveEnabled(app, Ui.Map(app), OldName);
        }, TimeSpan.FromMinutes(5));
        Assert.IsTrue(ended, "the search did not end within 5 minutes (the tile Move stays disabled)");
        Console.WriteLine($"the search ended (seen from the map) after {clock.Elapsed.TotalSeconds:F1} s");
        Assert.AreNotEqual(Text.DuplicatesCancelled, status.Current.Name, "the search ended cancelled");

        view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count == 1, Ui.ElementTimeout),
            "Duplicates opened again after the search ended does not show its group: " + Ui.Dump(view));
        Assert.IsFalse(AppSession.Find(view, "CancelDuplicates", Ui.ElementTimeout).Current.IsEnabled, "CancelDuplicates is enabled after the search ended");
        Ui.Click(Opener(app, Overview));
    }

    // Contract "Returning": the scan is unchanged by a view. A scan slowed by --scan-delay (150,000 empty files, 50 ms after
    // each folder batch) runs; About is opened and left by the Overview item while it runs; back on the map the scan is
    // still running (Cancel enabled) and then completes on its own (not Text.ScanCancelled), with its tiles.
    [TestMethod]
    [TestCategory("Console")]
    public void AScanThatRunsKeepsRunningAfterAViewAndBack()
    {
        using var fixture = new Fixture("ui-back-scan");
        fixture.ManyEmptyFiles("many", 150_000);
        fixture.File(@"some\a.bin", 300 * KB);

        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "50");
        var clock = Stopwatch.StartNew();
        var window = app.Window(Ui.WindowTimeout);
        var status = AppSession.Find(window, "Status", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(10)), "precondition: the delayed scan did not start");
        var about = Opener(app, "About");
        if (!about.Current.IsEnabled)
        {
            Assert.Inconclusive("the About item is disabled while a scan runs: a view cannot be opened during a scan");
        }

        Ui.OpenView(app, "About", "AboutView");
        Ui.Click(Opener(app, Overview));
        Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, "AboutView"), Ui.ElementTimeout), "AboutView is still shown after the Overview item");
        Assert.IsTrue(Ui.ScanRunning(app),
            $"back on the map {clock.Elapsed.TotalSeconds:F1} s into the scan, it no longer runs (status \"{status.Current.Name}\"): going back stopped it, or it ended already (then slow it down more)");
        Assert.IsFalse(status.Current.Name.StartsWith(Text.ScanCancelled, StringComparison.Ordinal), "going back cancelled the scan");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanFinished(app), TimeSpan.FromMinutes(3)),
            $"the scan did not complete after going back (status \"{status.Current.Name}\")");
        Assert.IsFalse(status.Current.Name.StartsWith(Text.ScanCancelled, StringComparison.Ordinal), "the scan ended cancelled");
        Assert.IsTrue(Ui.HasTile(Ui.Map(app), "some"), "the completed scan has no tile for some: [" + string.Join(" | ", Ui.TileNames(Ui.Map(app))) + "]");
    }

    // ---- keys with the focus on a view's own controls (review) -------------------------------------------------------

    /// <summary>A tree for the views' rows: a 101 MB file modified three years ago (a Large and old row) and two
    /// byte-identical 2 MB copies (a duplicate group). In the profile test folder; nothing in it is moved.</summary>
    private static Fixture RowsTree()
    {
        var fixture = new Fixture("ui-back-rows", Fixture.ProfileBaseFolder());
        fixture.File(OldName, 101 * MB);
        fixture.SetModified(OldName, DateTime.UtcNow.AddYears(-3));
        var content = new byte[(int)(2 * MB)];
        new Random(9_7).NextBytes(content);
        fixture.FileWithBytes(@"one\copy-a.dat", content);
        fixture.FileWithBytes(@"two\copy-b.dat", content);
        return fixture;
    }

    /// <summary>A journal of one recent session with a trashed entry (its path does not exist; Put back is never pressed)
    /// and a skipped one, so History has rows.</summary>
    private static void WriteHistory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-back-history-" + Guid.NewGuid().ToString("N")[..8]);
        var now = DateTimeOffset.UtcNow;
        JournalBackup.Write(
        [
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-back", now.AddHours(-2), "map", Path.Combine(root, "was-here.bin"), "trashed", bytes: 4096),
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-back", now.AddHours(-2).AddSeconds(1), "map", Path.Combine(root, "in-use.bin"), "skipped", reason: "uitest reason"),
        ]);
    }

    /// <summary>The keyboard-focusable list row that holds <paramref name="element"/>: the element itself or its nearest
    /// list item.</summary>
    private static AutomationElement FocusableRow(AutomationElement element)
    {
        var node = element;
        for (var level = 0; level < 5 && node is not null; level++)
        {
            var current = node.Current;
            if (current.ControlType == ControlType.ListItem && current.IsKeyboardFocusable)
            {
                return node;
            }

            node = TreeWalker.ControlViewWalker.GetParent(node);
        }

        Assert.IsTrue(element.Current.IsKeyboardFocusable, "no focusable list row holds " + Ui.Describe(element));
        return element;
    }

    /// <summary>The first History entry row (ASP-10: the innermost list rows of the view), as a focusable row.</summary>
    private static AutomationElement HistoryRow(AutomationElement view)
    {
        AutomationElement? first = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (first = Table.LeafRows(view).FirstOrDefault()) is not null, Ui.ElementTimeout),
            $"precondition: no entry row in {view.Current.AutomationId}: {Ui.Dump(view)}");
        return FocusableRow(first!);
    }

    /// <summary>The first <paramref name="automationId"/> element in <paramref name="view"/>, as a focusable row.</summary>
    private static AutomationElement RowIn(AutomationElement view, string automationId)
    {
        AutomationElement? first = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (first = AppSession.FindAll(view, automationId).FirstOrDefault()) is not null, Ui.DuplicatesTimeout),
            $"precondition: no {automationId} in {view.Current.AutomationId}: {Ui.Dump(view)}");
        return FocusableRow(first!);
    }

    // Review (ASP-9): the keys go back with the keyboard focus on a view's own controls too, not only on its sidebar item: a row
    // of the Large and old table (ASP-10: the row of a LargeOldSelect box), of UninstallList (focusing it selects it; UninstallInSettings is never pressed), of the Duplicates
    // list (after a search) and of the History list (the test's own journal), and the LicenseKey text box in About.
    // For each, Alt+Left returns to the map, then (the view opened again) Esc does: the view is gone, the map unchanged, no
    // scan starts, the focus is on the Overview item (ASP-14).
    [TestMethod]
    [TestCategory("Console")]
    public void AltLeftAndEscapeReturnWithTheFocusOnAViewsOwnControls()
    {
        using var fixture = RowsTree();
        using var backup = new JournalBackup();
        WriteHistory();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var before = MapState(app);
        var targets = new (string Opener, string ViewId, string What, Func<AutomationElement, AutomationElement> Target)[]
        {
            ("LargeOld", "LargeOldView", "a Large and old row", v => RowIn(v, "LargeOldSelect")),
            ("Uninstall", "UninstallView", "an UninstallList row", v => RowIn(AppSession.Find(v, "UninstallList", Ui.ElementTimeout), "UninstallRow")),
            ("Duplicates", "DuplicatesView", "a row of the Duplicates list", v =>
            {
                if (AppSession.FindAll(v, "DuplicateCopy").Count == 0)
                {
                    Ui.Click(AppSession.Find(v, "FindDuplicates", Ui.ElementTimeout));
                }

                return RowIn(v, "DuplicateCopy");
            }),
            ("History", "HistoryView", "a row of the History list", v => HistoryRow(v)),
            ("About", "AboutView", "the LicenseKey text box", v => AppSession.Find(v, "LicenseKey", Ui.ElementTimeout)),
        };

        foreach (var (opener, viewId, what, target) in targets)
        {
            foreach (var way in new[] { Way.AltLeft, Way.Escape })
            {
                var view = Ui.OpenView(app, opener, viewId);
                Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, viewId), Ui.ElementTimeout), viewId + " did not open");
                var control = target(view);
                Ui.Focus(control);
                if (way == Way.AltLeft)
                {
                    Keys.PressWithAlt(Keys.Left);
                }
                else
                {
                    Keys.Press(Keys.Escape);
                }

                AssertBackOnTheMap(app, viewId, before, $"{way} with the focus on {what} ({Ui.Describe(control)}) in {viewId}");
            }
        }
    }

    // Review (ASP-9): with a ComboBox's dropdown open (MinimumSize in Large and old), Esc closes only the dropdown: it
    // collapses, and the view stays shown; a second Esc, the dropdown closed, then returns to the map.
    [TestMethod]
    [TestCategory("Console")]
    public void EscapeWithTheMinimumSizeDropdownOpenClosesOnlyTheDropdown()
    {
        using var fixture = RowsTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var before = MapState(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, "LargeOldView"), Ui.ElementTimeout), "LargeOldView did not open");
        var combo = AppSession.Find(view, "MinimumSize", Ui.ElementTimeout);
        Ui.Focus(combo);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        Assert.IsTrue(AppSession.WaitUntil(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded, TimeSpan.FromSeconds(3)), "precondition: the MinimumSize dropdown did not open");
        System.Threading.Thread.Sleep(500); // the dropdown takes the keyboard
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed, TimeSpan.FromSeconds(3)), "Esc did not close the MinimumSize dropdown");
        Assert.IsFalse(AppSession.WaitUntil(() => !Shown(app, "LargeOldView"), Ui.NeverWatch), "Esc with the MinimumSize dropdown open left Large and old too (it must close only the dropdown)");

        Ui.Focus(AppSession.Find(view, "MinimumSize", Ui.ElementTimeout));
        Keys.Press(Keys.Escape);
        AssertBackOnTheMap(app, "LargeOldView", before, "Esc on MinimumSize with its dropdown closed");
    }

    // ---- the app-gone watch (review) ---------------------------------------------------------------------------------

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Windows Settings' top-level windows now (the hand-off opens Settings > Apps).</summary>
    private static List<AutomationElement> SettingsWindows() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"))
            .Cast<AutomationElement>().Where(w =>
            {
                try
                {
                    return w.Current.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }).ToList();

    /// <summary>Closes every Windows Settings window not in <paramref name="before"/> (by its window pattern).</summary>
    private static void CloseNewSettingsWindows(List<AutomationElement> before)
    {
        var known = before.Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var window in SettingsWindows().Where(w => !known.Contains(Ui.Key(w))))
        {
            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                {
                    ((WindowPattern)pattern).Close();
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>The UninstallRow named <paramref name="name"/> in <paramref name="list"/>, realised if the list virtualises it.</summary>
    private static AutomationElement? RowNamed(AutomationElement list, string name)
    {
        var row = AppSession.FindAll(list, "UninstallRow").FirstOrDefault(r => r.Current.Name == name);
        if (row is not null || !list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var container))
        {
            return row;
        }

        try
        {
            var item = ((ItemContainerPattern)container).FindItemByProperty(null, AutomationElement.NameProperty, name);
            if (item is not null && item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
            {
                ((VirtualizedItemPattern)virtualized).Realize();
            }

            if (item is not null && item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            {
                ((ScrollItemPattern)scroll).ScrollIntoView();
            }

            return item;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Review (ASP-9, "Running work"): going back does not end the app-gone watch. No real app is uninstalled: the test makes
    // its own per-user entry under HKCU's Uninstall key (AmbystoLeanUiTest-<guid>; display name LeanUiGoneApp-<id>, install
    // location %LOCALAPPDATA%\Programs\LeanUiGoneApp-<id>) and a leftover folder %APPDATA%\LeanUiGoneApp-<id>, each with
    // one small file. UninstallInSettings on that row starts the watch (UninstallInstruction names it) and opens Windows
    // Settings, which the test closes; the Overview item returns to the map; the test removes the entry. On the map the status then
    // says Text.AppGone(name), and Uninstall opened again lists both folders as LeftoverItem rows (ASP-10: a row's
    // automation name holds the full folder), nothing ticked. MoveLeftovers
    // is never pressed. In finally: the key and both folders are removed, any Settings window it opened is closed.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheAppGoneWatchGoesOnAfterGoingBackAndItsLeftoversAreShown()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = "LeanUiGoneApp-" + id;
        var keyPath = UninstallPath + @"\AmbystoLeanUiTest-" + Guid.NewGuid().ToString("D");
        var leftover = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);
        var settingsBefore = SettingsWindows();
        using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            Assert.IsNull(existing, "precondition: the scratch key exists already");
        }

        Assert.IsFalse(Directory.Exists(leftover) || Directory.Exists(install), "precondition: a scratch folder exists already");
        try
        {
            Directory.CreateDirectory(leftover);
            File.WriteAllBytes(Path.Combine(leftover, "settings.dat"), new byte[64 * KB]);
            Directory.CreateDirectory(install);
            File.WriteAllBytes(Path.Combine(install, "app.dat"), new byte[64 * KB]);
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayName", name);
                key.SetValue("Publisher", "LeanUiTestVendor-" + id);
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("InstallLocation", install);
                key.SetValue("UninstallString", "\"" + Path.Combine(install, "uninstall.exe") + "\"");
            }

            using var app = AppSession.Start();
            var view = Ui.OpenView(app, "Uninstall", "UninstallView");
            var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
            var row = AppSession.WaitFor(() => RowNamed(list, name), Ui.ElementTimeout);
            Assert.IsNotNull(row, $"precondition: no UninstallRow named {name}");
            Ui.SelectionItemOf(row).Select();
            var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => offer.Current.IsEnabled, TimeSpan.FromSeconds(3)), "precondition: UninstallInSettings is disabled on the test's row");
            Ui.Click(offer);
            var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => instruction.Current.Name == Text.UninstallInSettings(name), Ui.ElementTimeout),
                $"precondition: the watch did not start: UninstallInstruction \"{instruction.Current.Name}\"");
            AppSession.WaitUntil(() => SettingsWindows().Count > settingsBefore.Count, TimeSpan.FromSeconds(10));
            CloseNewSettingsWindows(settingsBefore);

            Ui.BackToOverview(app, "UninstallView");
            Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(name), TimeSpan.FromSeconds(60)),
                $"on the map, the status never said \"{Text.AppGone(name)}\" within 60 s of removing the entry (status \"{status.Current.Name}\"): going back ended the watch");

            GoneApp.DismissResultIfShown(app);
            view = Ui.OpenView(app, "Uninstall", "UninstallView");
            List<AutomationElement> items = [];
            bool Offered(string path) => items.Any(i => Table.RowName(i).Contains(path, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppSession.WaitUntil(() =>
                {
                    items = Ui.FindAllAnywhere(app, "LeftoverItem");
                    return Offered(leftover) && Offered(install);
                }, Ui.ElementTimeout),
                $"Uninstall opened again does not list both leftovers ({leftover}, {install}): [{string.Join(" | ", items.Select(Table.RowName))}]");
            foreach (var item in items)
            {
                Assert.AreNotEqual(ToggleState.On, ((TogglePattern)item.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState, "a leftover is ticked: " + Table.RowName(item));
            }

            Ui.BackToOverview(app, "UninstallView");
        }
        finally
        {
            CloseNewSettingsWindows(settingsBefore);
            if (Registry.CurrentUser.OpenSubKey(keyPath) is { } left)
            {
                left.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            }

            foreach (var folder in new[] { leftover, install })
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
    }
}
