using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-14 fix round 2 (intent/2026-10-18-asp-14-start-and-sidebar.md, review log "Round 2" and its amendments), the
/// two defects the screenshots of a scan of C: showed that are about the list and the legend: every row of the largest files
/// list is one line high, a display-only row included (its hint is in its automation name and its tooltip, not text under
/// the name), and the row counts hold with display-only rows among the first rows; and the type legend under the map shows
/// every entry whole at the default window size. The progress line, the announced refusal and the profile volume are in
/// <see cref="StartAndSidebarTests"/>. Read only: nothing is moved.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoundTwoFixesTests
{
    private const long KB = ByteUnits.KB;

    private const int DisplayOnly = 3;

    private const int Plain = 14;

    private static MoveFixture? fixture;

    /// <summary>The names of the display-only files: the three largest files of the tree, inside the protected folder
    /// guarded\, so they head the list (as pagefile.sys and swapfile.sys head the list of C:).</summary>
    private static readonly string[] LockedNames = ["locked-1.mp4", "locked-2.iso", "locked-3.zip"];

    [ClassInitialize]
    public static void BuildTheTree(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = new MoveFixture("asp14-round2");
        var seed = 1;
        for (var i = 0; i < DisplayOnly; i++)
        {
            fixture.File(@"guarded\" + LockedNames[i], (3000 - (i * 100)) * KB, seed++);
        }

        for (var i = 0; i < Plain; i++)
        {
            fixture.File(@"plain\plain-" + i.ToString("D2", CultureInfo.InvariantCulture) + ".bin", (1000 - (i * 20)) * KB, seed++);
        }
    }

    [ClassCleanup]
    public static void RemoveTheTree() => fixture?.Dispose();

    /// <summary>Starts the app on the fixture with guarded\ protected (its three files display-only) and waits for the scan.</summary>
    private static AppSession StartScanned()
    {
        SettingsBackup.WriteProtectedFolders(fixture!.Sub("guarded"));
        var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        return app;
    }

    private static List<AutomationElement> WholeRows(AppSession app) => ResultsViewTests.WholeRows(app);

    private static string Heights(IEnumerable<AutomationElement> rows) =>
        "[" + string.Join(" | ", rows.Select(r => $"{r.Current.BoundingRectangle.Height:F1} px: {r.Current.Name}")) + "]";

    private static AutomationElement? RowNamed(AppSession app, string name) =>
        Table.LeafRows(ResultsViewTests.List(app)).FirstOrDefault(r => (r.Current.Name ?? string.Empty) == name);

    /// <summary>The height, in pixels, of each of the first <paramref name="count"/> rows of LargestFiles, as "name: height".
    /// Each row is scrolled into view first and read whole: the rectangle of a row cut off by the list's edge is only the part
    /// on screen. The list is scrolled back to the top afterwards.</summary>
    private static List<(string Name, double Height)> HeightsOfTheFirstRows(AppSession app, int count)
    {
        var names = Table.LeafRows(ResultsViewTests.List(app))
            .Where(r => !r.Current.BoundingRectangle.IsEmpty)
            .OrderBy(r => r.Current.BoundingRectangle.Top)
            .Select(r => r.Current.Name ?? string.Empty)
            .Take(count)
            .ToList();
        var heights = new List<(string, double)>();
        foreach (var name in names)
        {
            var row = RowNamed(app, name);
            if (row is not null && row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern))
            {
                ((ScrollItemPattern)pattern).ScrollIntoView();
                System.Threading.Thread.Sleep(250);
                row = RowNamed(app, name);
            }

            Assert.IsNotNull(row, $"the row \"{name}\" is gone after scrolling it into view");
            heights.Add((name, row.Current.BoundingRectangle.Height));
        }

        var scroller = ResultsViewTests.List(app).FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
            .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        if (scroller is not null)
        {
            ((ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern)).SetScrollPercent(ScrollPattern.NoScroll, 0);
            System.Threading.Thread.Sleep(250);
        }

        return heights;
    }

    private static string Dump(IEnumerable<(string Name, double Height)> heights) =>
        "[" + string.Join(" | ", heights.Select(h => $"{h.Height:F1} px: {h.Name}")) + "]";

    // Fix round 2, "every row of the largest files list is one line high": the first rows, read whole, are as high as each
    // other (to 1.5 px), the three display-only rows that head the list included, plain rows too. The screenshot of C: had the
    // hint of pagefile.sys and swapfile.sys wrapped to three lines under the name, so those rows were three times the others.
    // (Here the hint is "Protected by you", which is short: the long hints are in the next test.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void EveryRowOfTheLargestFilesIsOneLineHighDisplayOnlyRowsToo()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var heights = HeightsOfTheFirstRows(app, DisplayOnly + 2);
        Console.WriteLine(Dump(heights));
        Assert.AreEqual(DisplayOnly + 2, heights.Count, "fewer rows than expected in the list: " + Dump(heights));
        for (var i = 0; i < DisplayOnly; i++)
        {
            Assert.IsTrue(heights[i].Name.Contains(LockedNames[i], StringComparison.OrdinalIgnoreCase), $"row {i} is not the display-only {LockedNames[i]} (largest first): {Dump(heights)}");
        }

        Assert.IsTrue(heights.Max(h => h.Height) - heights.Min(h => h.Height) <= 1.5, $"the rows are not all as high as each other: {Dump(heights)}");
    }

    /// <summary>A tree under C:\Users\Public\Documents: every item in it is display-only with the long hint "System-wide: needs
    /// an administrator; Lean does not change it" (every other user's folder is system-wide), as pagefile.sys is on C:.
    /// Eight files, the largest 3000 KB; Inconclusive when there is no such folder to build in.</summary>
    private static Fixture SystemWideTree()
    {
        var publicFolder = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "Documents");
        if (!System.IO.Directory.Exists(publicFolder))
        {
            Assert.Inconclusive("no " + publicFolder + " to build a system-wide fixture in");
        }

        var tree = new Fixture("ui-round2-public", publicFolder);
        for (var i = 0; i < 8; i++)
        {
            tree.File(@"sys\pub-" + i.ToString("D2", CultureInfo.InvariantCulture) + ".bin", (3000 - (i * 100)) * KB);
        }

        return tree;
    }

    // Fix round 2, the long hints: a display-only row of an item under C:\Users\Public carries the hint "System-wide: needs an
    // administrator; Lean does not change it", as long as the hint of pagefile.sys on C:. No such row is higher than a plain
    // row, which a first session on the movable-and-protected tree measures; the hint is in the row's name, not in lines of
    // its own under it. (A scanned folder is one root: here every row is display-only.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADisplayOnlyRowWithALongHintIsNoHigherThanAPlainRow()
    {
        using var settings = new SettingsBackup();
        double plain;
        using (var first = StartScanned())
        {
            var heights = HeightsOfTheFirstRows(first, DisplayOnly + 2);
            plain = heights.Skip(DisplayOnly).Min(h => h.Height);
            Console.WriteLine("the plain rows: " + Dump(heights.Skip(DisplayOnly)));
        }

        using var system = SystemWideTree();
        SettingsBackup.WriteProtectedFolders();
        using var app = AppSession.Start("--scan", system.Path);
        Ui.WaitForScan(app);
        var rows = HeightsOfTheFirstRows(app, 5);
        Console.WriteLine("the long-hint rows: " + Dump(rows) + $"; a plain row is {plain:F1} px");
        Assert.IsTrue(rows.Count > 0, "no row in the list");
        var hint = Text.HintSystemWide;
        foreach (var (name, _) in rows)
        {
            Assert.IsTrue(name.Contains(hint, StringComparison.Ordinal), $"the row \"{name}\" does not carry the hint \"{hint}\" (the fixture is not display-only for this reason)");
        }

        Assert.IsTrue(rows.Max(r => r.Height) <= plain + 1.5, $"a display-only row with a long hint is higher than a plain row ({plain:F1} px): {Dump(rows)}");
    }

    // Fix round 2: the row counts hold with long-hint display-only rows first (the screenshot of C: showed 2 rows): at the
    // default size at least 4 whole rows, at 1000 px or more 8.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheListShowsItsRowsAtTheDefaultSizeWithLongHintRowsFirst()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var system = SystemWideTree();
        using var app = AppSession.Start("--scan", system.Path);
        Ui.WaitForScan(app);
        ResultsViewTests.AssertRowsForHeight(app, "with long-hint display-only rows first, at the default size");
    }

    // Fix round 2: a display-only row shows its hint in its tooltip and its name only (the contract: "disabled, with their hint
    // in the row's name and tooltip"), not as text under the name. The hint is in the row's automation name, and the row, or
    // something in it, carries it as its tooltip (HelpText). The tick box is disabled.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADisplayOnlyRowCarriesItsHintInTheNameAndTheTooltip()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var rows = WholeRows(app);
        Assert.IsTrue(rows.Count >= DisplayOnly, $"only {rows.Count} whole rows on screen: {Heights(rows)}");
        for (var i = 0; i < DisplayOnly; i++)
        {
            var row = rows[i];
            var name = row.Current.Name;
            Assert.IsTrue(name.Contains(LockedNames[i], StringComparison.OrdinalIgnoreCase), $"row {i} is not the display-only {LockedNames[i]}: {Heights(rows)}");
            var hint = Table.HintIn(name);
            Assert.IsNotNull(hint, $"the display-only row's automation name holds no hint: \"{name}\"");
            var tips = new List<string> { row.Current.HelpText ?? string.Empty };
            tips.AddRange(row.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.HelpText ?? string.Empty));
            Assert.IsTrue(tips.Any(t => t.Contains(hint!, StringComparison.Ordinal)),
                $"the display-only row {LockedNames[i]} has no tooltip with its hint \"{hint}\": tooltips [{string.Join(" | ", tips.Where(t => t.Length > 0))}]");
            var box = row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            Assert.IsTrue(box is not null && !box.Current.IsEnabled, $"the tick box of the display-only row {LockedNames[i]} is {(box is null ? "missing" : "enabled")}");
        }
    }

    // ---- fix round 3: the reason of a display-only row is shown when the row has keyboard focus ----------------------------

    /// <summary>The first screen point outside the app's window that the pointer can be moved to, tried left, right, below and
    /// above, and the pointer parked there: no hover can open a tooltip. Inconclusive on a screen the window fills.</summary>
    private static void ParkThePointerOutsideTheWindow(AppSession app)
    {
        var w = app.Window(Ui.WindowTimeout).Current.BoundingRectangle;
        foreach (var (x, y) in new[] { (w.Right + 40, w.Top + 40), (w.Left - 40, w.Top + 40), (w.Left + 40, w.Bottom + 40), (w.Left + 40, w.Top - 40) })
        {
            if (x < 0 || y < 0)
            {
                continue;
            }

            try
            {
                Mouse.HoverAt(x, y);
                return;
            }
            catch (InvalidOperationException)
            {
                // The point is off the desktop (the move was clamped): the next one.
            }
        }

        Assert.Inconclusive($"the window {w} leaves no room on the screen to park the pointer outside it");
    }

    /// <summary>What shows <paramref name="hint"/> now in any top-level window of the app (a tooltip is its own popup window): a
    /// ToolTip element holding it, or a text whose name is exactly the hint (a visible line); the row's own automation name
    /// holds the hint inside a longer name and does not count. Null when none.</summary>
    private static string? ShownHint(AppSession app, string hint)
    {
        var condition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip),
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        try
        {
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id)))
            {
                foreach (AutomationElement e in top.FindAll(TreeScope.Subtree, condition))
                {
                    var current = e.Current;
                    if (current.IsOffscreen)
                    {
                        continue;
                    }

                    var name = (current.Name ?? string.Empty).Trim();
                    if (current.ControlType == ControlType.ToolTip
                        && (name.Contains(hint, StringComparison.Ordinal) || e.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, hint)) is not null))
                    {
                        return $"a tooltip \"{name}\"";
                    }

                    if (current.ControlType == ControlType.Text && name == hint)
                    {
                        return $"a line \"{name}\"";
                    }
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    private static string FocusNow()
    {
        try
        {
            return Ui.Describe(AutomationElement.FocusedElement);
        }
        catch (ElementNotAvailableException)
        {
            return "(gone)";
        }
    }

    /// <summary>The first <paramref name="count"/> rows of LargestFiles on screen, top to bottom: the three display-only rows
    /// (locked-1..3) and then plain rows.</summary>
    private static List<AutomationElement> TopRows(AppSession app, int count) =>
        Table.LeafRows(ResultsViewTests.List(app)).Where(r => !r.Current.BoundingRectangle.IsEmpty).OrderBy(r => r.Current.BoundingRectangle.Top).Take(count).ToList();

    private static bool FocusIsWithin(AutomationElement row)
    {
        try
        {
            return AutomationElement.FocusedElement is { } focused && Ui.IsWithin(focused, row);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The scene of both keyboard tests: the app scanned, the pointer parked outside the window, the keyboard focus on
    /// the first plain row (below the three display-only ones); nothing shows the hint yet. Returns the rows.</summary>
    private static List<AutomationElement> FocusOnTheFirstPlainRow(AppSession app, string hint)
    {
        ParkThePointerOutsideTheWindow(app);
        var rows = TopRows(app, DisplayOnly + 1);
        Assert.AreEqual(DisplayOnly + 1, rows.Count, "fewer rows than expected in the list: " + Heights(rows));
        for (var i = 0; i < DisplayOnly; i++)
        {
            Assert.IsTrue(rows[i].Current.Name.Contains(LockedNames[i], StringComparison.OrdinalIgnoreCase), $"row {i} is not the display-only {LockedNames[i]}: {Heights(rows)}");
        }

        Assert.IsFalse(rows[DisplayOnly].Current.Name.Contains(hint, StringComparison.Ordinal), "precondition: the row after the display-only ones carries the hint: " + rows[DisplayOnly].Current.Name);
        Ui.Focus(rows[DisplayOnly]);
        System.Threading.Thread.Sleep(1500);
        Assert.IsNull(ShownHint(app, hint), "precondition: the hint is shown before any display-only row has the focus: " + ShownHint(app, hint));
        return rows;
    }

    // The instrument of the two tests below, on a case that works today: with the pointer resting on the display-only row's name
    // (the hover tooltip of fix round 2), ShownHint finds the hint. A failure here is the detector's or the hover's, not the
    // keyboard focus's. The pointer is tried at a few places along the row's left half; the focus is not in the list.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheHintDetectorSeesTheHoverTooltipOfADisplayOnlyRow()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var hint = Text.HintProtectedByYou;
        ParkThePointerOutsideTheWindow(app);
        Assert.IsNull(ShownHint(app, hint), "precondition: the hint is shown with the pointer outside the window");
        var row = TopRows(app, 1)[0].Current.BoundingRectangle;
        string? shown = null;
        foreach (var fraction in new[] { 0.08, 0.14, 0.2, 0.3, 0.4 })
        {
            Mouse.HoverAt(row.Left + (row.Width * fraction), row.Top + (row.Height / 2));
            Mouse.HoverAt(row.Left + (row.Width * fraction) + 3, row.Top + (row.Height / 2));
            if (AppSession.WaitUntil(() => (shown = ShownHint(app, hint)) is not null, TimeSpan.FromSeconds(3)))
            {
                break;
            }
        }

        Assert.IsNotNull(shown, "with the pointer on the display-only row's name, no tooltip or line shows the hint \"" + hint + "\": the detector cannot see it");
        Console.WriteLine("the hover shows " + shown);
    }

    // Fix round 3 (owner amendment 2026-10-19), arrow keys: a display-only row shows its reason when it gets keyboard focus, not
    // only on pointer hover. The pointer is parked outside the window; the focus starts on the first plain row; Up moves it to
    // the last display-only row (locked-3); a tooltip, or a visible line, with the row's hint appears within 3 s.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADisplayOnlyRowShowsItsHintWhenAnArrowKeyMovesTheFocusToIt()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var hint = Text.HintProtectedByYou;
        var rows = FocusOnTheFirstPlainRow(app, hint);
        var target = rows[DisplayOnly - 1];
        Assert.AreEqual(hint, Table.HintIn(target.Current.Name), "precondition: the display-only row's hint is not the protected-folder hint: " + target.Current.Name);

        Keys.Press(Keys.Up);
        Assert.IsTrue(AppSession.WaitUntil(() => FocusIsWithin(target), TimeSpan.FromSeconds(3)),
            $"the Up arrow did not move the keyboard focus into the display-only row {LockedNames[DisplayOnly - 1]}; the focus is on {FocusNow()}");
        Assert.IsTrue(AppSession.WaitUntil(() => ShownHint(app, hint) is not null, TimeSpan.FromSeconds(3)),
            $"the keyboard focus is on {FocusNow()} but no tooltip or line shows the hint \"{hint}\" (the pointer is outside the window)");
    }

    // Fix round 3, Tab: Shift+Tab from the first plain row until the focus first enters the last display-only row (at most 8
    // presses); at that first stop the reason is shown, whichever control of the row it is.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADisplayOnlyRowShowsItsHintWhenTabReachesIt()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var hint = Text.HintProtectedByYou;
        var rows = FocusOnTheFirstPlainRow(app, hint);
        var target = rows[DisplayOnly - 1];
        var path = new List<string>();
        for (var press = 0; press < 8 && !FocusIsWithin(target); press++)
        {
            Keys.PressWithShift(Keys.Tab);
            path.Add(FocusNow());
        }

        Assert.IsTrue(FocusIsWithin(target), "Shift+Tab did not reach the display-only row " + LockedNames[DisplayOnly - 1] + " in 8 presses; focus path [" + string.Join(" > ", path) + "]");
        Assert.IsTrue(AppSession.WaitUntil(() => ShownHint(app, hint) is not null, TimeSpan.FromSeconds(3)),
            $"the focus entered the display-only row at {FocusNow()} but no tooltip or line shows the hint \"{hint}\" (the pointer is outside the window); focus path [{string.Join(" > ", path)}]");
    }

    // Fix round 2: "the row counts above hold with display-only rows among the first rows": at the window's default size
    // (the VM's is about 705 px tall) at least 4 whole rows are on screen, as with the plain rows of the other tests, though
    // the first three rows are display-only (the height decides: at 1000 px or more it is 8).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheListShowsItsRowsAtTheDefaultSizeWithDisplayOnlyRowsFirst()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var first = Table.LeafRows(ResultsViewTests.List(app)).Select(r => r.Current.Name).Take(DisplayOnly).ToList();
        Assert.IsTrue(first.Count == DisplayOnly && first.Select((n, i) => n.Contains(LockedNames[i], StringComparison.OrdinalIgnoreCase)).All(x => x),
            "precondition: the first rows are not the display-only ones: [" + string.Join(" | ", first) + "]");
        ResultsViewTests.AssertRowsForHeight(app, "with display-only rows first, at the default size");
    }

    // ...and at 1000 px or taller at least 8; Inconclusive on a screen that cannot give the window that height.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheListShowsEightRowsAThousandPixelsTallWithDisplayOnlyRowsFirst()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        ResultsViewTests.ResizeToAThousandOrInconclusive(app);
        ResultsViewTests.AssertRowsForHeight(app, "with display-only rows first, after resizing to 1040 px");
    }

    // ---- the legend ------------------------------------------------------------------------------------------------------

    private static string[] LegendWords() =>
        [Text.TypeVideo, Text.TypeDiskImage, Text.TypeArchive, Text.TypeDocument, Text.TypeApplication, Text.TypeVirtualMachineImage, Text.TypeOther, Text.TypeFolder];

    // Fix round 2, "the legend shows every entry whole at the default window size: it wraps to a second line rather than
    // clipping": after a scan, each of the eight legend words (one entry per file-type group, the swatch left of it) is on
    // screen and its rectangle lies inside the map's pane, left to right. The legend is the text between the map and the list.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void EveryLegendEntryLiesInsideTheMapPaneAtTheDefaultSize()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var map = Ui.Map(app).Current.BoundingRectangle;
        var list = ResultsViewTests.List(app).Current.BoundingRectangle;
        var window = app.Window(Ui.WindowTimeout);
        var between = new List<(string Word, Rect Rect, bool Offscreen)>();
        foreach (AutomationElement text in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
        {
            try
            {
                var current = text.Current;
                var r = current.BoundingRectangle;
                if (LegendWords().Contains(current.Name, StringComparer.Ordinal) && !r.IsEmpty && r.Top >= map.Bottom - 2 && r.Bottom <= list.Top + 2)
                {
                    between.Add((current.Name, r, current.IsOffscreen));
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        var dump = "map " + map + "; legend texts [" + string.Join(" | ", between.Select(b => $"{b.Word} {b.Rect}{(b.Offscreen ? " offscreen" : string.Empty)}")) + "]";
        Console.WriteLine(dump);
        foreach (var word in LegendWords())
        {
            var entry = between.FirstOrDefault(b => b.Word == word);
            Assert.IsNotNull(entry.Word, $"no legend entry \"{word}\" between the map and the list (clipped away or missing): {dump}");
            Assert.IsFalse(entry.Offscreen, $"the legend entry \"{word}\" is off screen: {dump}");
            Assert.IsTrue(entry.Rect.Left >= map.Left - 1 && entry.Rect.Right <= map.Right + 1,
                $"the legend entry \"{word}\" {entry.Rect} is cut off by the map pane {map.Left:F0} to {map.Right:F0}: {dump}");
        }
    }
}
