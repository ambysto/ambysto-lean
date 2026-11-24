using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using static Ambysto.Lean.UiTests.Support.UninstallTestApps;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-16 (intent/2026-10-20-asp-16-uninstall-table.md, "UI contract" and "Proof"): the Uninstall view as a table. The tests use
/// the test's own uninstall entries (<see cref="UninstallTestApps"/>: known values, an install folder that exists, one that does not, none)
/// and never select or press "Uninstall…" on a real app: it is pressed only on a test entry, after which the Windows Settings window it
/// opened is closed (as GoneApp does). The magnifier is pressed only on a test entry whose folder is the test's own; the File Explorer window
/// it opens is found, looked at, and closed (<see cref="ExplorerWindows"/>: the app has no launcher seam, so the window is the evidence).
/// The leftovers tests use <see cref="GoneApp"/> and press the move button only to open the confirmation, which they cancel.
/// <list type="bullet">
/// <item>Headers: Name, Publisher, Version, Size, Installed on, Last updated, left to right, at the contract widths; no tick boxes, no Select all.</item>
/// <item>Cells: the values of a test entry, an empty cell for each missing value; every text trimmed, its full text its tooltip (UIA HelpText).</item>
/// <item>Rows: one line high at the default window size, by rectangle, after the layout settles (the ASP-15 lesson).</item>
/// <item>Sort: every column sorts from its header; a missing value sorts after every real one in both directions.</item>
/// <item>Magnifier: on hover and on focus, only for a row whose InstallLocation is a rooted folder that exists; opens that folder.</item>
/// <item>Selection: a click and the arrow keys select one row; "Uninstall…" keeps working; the selection survives the ASP-9 rebuild.</item>
/// <item>Leftovers: Select all ticks only the rows that can be selected and is off while a move is confirmed.</item>
/// <item>High Contrast.</item>
/// </list></summary>
[TestClass]
[DoNotParallelize]
public sealed class UninstallTableTests
{
    private static readonly string[] SixHeaders =
        [Text.ColumnName, Text.ColumnPublisher, Text.ColumnVersion, Text.ColumnSize, Text.ColumnInstalledOn, Text.ColumnLastUpdated];

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    private static double ScaleOf(AppSession app) =>
        WindowOnScreen.Scale(new AutomationElementHandle(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle)));

    private static string Status(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout).Current.Name;

    /// <summary>Focus on the Uninstall sidebar item and the pointer in the sidebar's empty band: away from every row.</summary>
    private static void Away(AppSession app)
    {
        Ui.Focus(Ui.SidebarItem(app, "Uninstall"));
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        Mouse.HoverAt(sidebar.Left + (sidebar.Width / 2), (history.Bottom + settings.Top) / 2);
    }

    private static void HoverRow(AutomationElement row)
    {
        // The list has more rows than fit: a row below the viewport has an infinite rectangle until it is scrolled into view.
        if ((row.Current.IsOffscreen || row.Current.BoundingRectangle.IsEmpty) && row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
        {
            ((ScrollItemPattern)scroll).ScrollIntoView();
            System.Threading.Thread.Sleep(400);
        }

        var rect = row.Current.BoundingRectangle;
        Mouse.HoverAt(rect.Left + 60, rect.Top + (rect.Height / 2));
    }

    private static bool IsSelected(AutomationElement row)
    {
        try
        {
            return Ui.SelectionItemOf(row).Current.IsSelected;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static AutomationElement? ScrollerOf(AutomationElement list)
    {
        try
        {
            if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var own) && ((ScrollPattern)own).Current.VerticallyScrollable)
            {
                return list;
            }

            return list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Calls <paramref name="visit"/> with every realised row at the top of the list, then at each scroll position down to the bottom, and
    /// scrolls back to the top. The list is virtualised, so each row is realised at least once.</summary>
    private static void ScrollThrough(AutomationElement list, Action<AutomationElement> visit)
    {
        void Pass()
        {
            foreach (var row in AppSession.FindAll(list, "UninstallRow"))
            {
                try
                {
                    visit(row);
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        Pass();
        if (ScrollerOf(list) is not { } scroller)
        {
            return;
        }

        var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
        var step = Math.Max(5.0, scroll.Current.VerticalViewSize / 2);
        for (var at = step; at <= 100.0 + step; at += step)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
            System.Threading.Thread.Sleep(200);
            Pass();
        }

        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0.0);
        System.Threading.Thread.Sleep(200);
    }

    /// <summary>Every row name of the list in list order (the list scrolled from top to bottom; a row is listed the first time it is seen).</summary>
    private static List<string> OrderedNames(AutomationElement list)
    {
        var order = new List<string>();
        void Take()
        {
            foreach (var name in RowNamesNow(list))
            {
                if (!order.Contains(name))
                {
                    order.Add(name);
                }
            }
        }

        Take();
        if (ScrollerOf(list) is { } scroller)
        {
            var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
            var step = Math.Max(5.0, scroll.Current.VerticalViewSize / 3);
            for (var at = step; at <= 100.0 + step; at += step)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
                System.Threading.Thread.Sleep(200);
                Take();
            }

            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0.0);
            System.Threading.Thread.Sleep(200);
        }

        return order;
    }

    /// <summary>Waits until the list's rectangle and every realised row's rectangle are the same in two samples about 150 ms apart: the view
    /// settles by a pixel or two in the first 200 ms after it opens (the ASP-15 lesson).</summary>
    private static void WaitForRowsToSettle(AutomationElement view)
    {
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        string Sample()
        {
            var parts = new List<string> { list.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture) };
            foreach (var row in AppSession.FindAll(list, "UninstallRow"))
            {
                try
                {
                    parts.Add(row.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture));
                }
                catch (ElementNotAvailableException)
                {
                    parts.Add("gone");
                }
            }

            return string.Join(";", parts);
        }

        var last = Sample();
        var settled = AppSession.WaitUntil(() =>
        {
            System.Threading.Thread.Sleep(150);
            var now = Sample();
            var same = now == last;
            last = now;
            return same;
        }, Ui.ElementTimeout);
        Assert.IsTrue(settled, "the rows of the Uninstall view never stopped changing size or place: " + last);
    }

    private static bool WholeInside(System.Windows.Rect inner, System.Windows.Rect viewport) =>
        !inner.IsEmpty && inner.Top > viewport.Top + 2 && inner.Bottom < viewport.Bottom - 2;

    // ---- the headers ---------------------------------------------------------------------------------------------------------

    // UI contract: "UninstallList" is a LeanTable; the columns left to right are Name (takes the room that is left), Publisher (180),
    // Version (110), Size (90), Installed on (110), Last updated (110); the table has no tick boxes and no Select all.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheAppsTableHasTheSixHeadersInOrderAtTheContractWidthsAndNoTickBoxes()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Table.AssertColumns(view, 1, SixHeaders);

        var scale = ScaleOf(app);
        var columns = Columns(view);
        var wanted = new Dictionary<string, double>
        {
            [Text.ColumnPublisher] = 180,
            [Text.ColumnVersion] = 110,
            [Text.ColumnSize] = 90,
            [Text.ColumnInstalledOn] = 110,
            [Text.ColumnLastUpdated] = 110,
        };
        var problems = new List<string>();
        foreach (var (name, width) in wanted)
        {
            var got = columns.Single(c => c.Name == name).HeaderWidth / scale;
            if (Math.Abs(got - width) > 2)
            {
                problems.Add($"the column {name} is {got:F1} wide, the contract says {width}");
            }
        }

        var nameWidth = columns[0].HeaderWidth / scale;
        if (nameWidth < 120 || nameWidth <= wanted.Values.Max())
        {
            problems.Add($"the column {Text.ColumnName} is {nameWidth:F1} wide: it should take the room that is left (more than the widest fixed column)");
        }

        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        if (list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)) is { } box)
        {
            problems.Add("the apps table has a check box: " + Ui.Describe(box));
        }

        var selectAll = Ui.FindAllAnywhere(app, "SelectAll").Count(Shown);
        if (selectAll != 0)
        {
            problems.Add($"{selectAll} Select all box(es) are shown in the Uninstall view with no leftovers offered");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- the cells -----------------------------------------------------------------------------------------------------------

    // "A test entry's row shows its publisher, version, size and installed-on values, and an empty cell for each missing value."
    // "The row's automation name stays as it is today: the display name, … not the cells joined." Last updated is the key's last write:
    // every test entry has one, so even the bare entry's cell holds a date (today's, within a few minutes).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ATestAppsRowShowsItsValuesAndAnEmptyCellForEachMissingOne()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var columns = Columns(view);
        var written = new[] { DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow.AddMinutes(1) }.Select(Table.ShortDate).Distinct().ToList();
        var problems = new List<string>();
        foreach (var entry in new[] { apps.Full, apps.Second, apps.Bare, apps.Relative, apps.FileLocation, apps.Disappearing })
        {
            var row = RowOf(view, entry);
            if (row.Current.Name != entry.Name)
            {
                problems.Add($"{entry.Name}: the row's automation name is \"{row.Current.Name}\", expected the display name alone");
            }

            var expected = new[] { entry.Name, entry.Publisher ?? string.Empty, entry.Version ?? string.Empty, entry.SizeText, entry.InstalledText };
            for (var i = 0; i < expected.Length; i++)
            {
                var shown = CellText(row, columns[i]);
                if (shown != expected[i])
                {
                    problems.Add($"{entry.Name}: the {columns[i].Name} cell shows \"{shown}\", expected \"{expected[i]}\"");
                }
            }

            var updated = CellText(row, columns[5]);
            if (!written.Contains(updated))
            {
                problems.Add($"{entry.Name}: the {Text.ColumnLastUpdated} cell shows \"{updated}\", expected the date the key was written ({string.Join(" or ", written)})");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // "Every text cell trims with an ellipsis, and its full text is its tooltip." A very long name, publisher and version stay in their
    // columns (the cell's rectangle is no wider than its column, so the text is shortened) and carry the whole text as their tooltip (UIA
    // HelpText); the row stays one line high. The ellipsis itself is drawn, not a UIA property, so the trimming is read from the rectangle.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void LongTextsAreTrimmedInTheirColumnAndCarryTheFullTextAsTheirTooltip()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var columns = Columns(view);
        var scale = ScaleOf(app);
        var row = RowOf(view, apps.Long);
        var problems = new List<string>();
        var expected = new[] { (0, apps.Long.Name), (1, apps.Long.Publisher!), (2, apps.Long.Version!) };
        foreach (var (index, full) in expected)
        {
            var texts = CellTexts(row, columns[index]);
            var cell = texts.FirstOrDefault(t => t.Name == full);
            if (cell.Name is null)
            {
                problems.Add($"the {columns[index].Name} cell has no text element holding the full text ({full.Length} characters): [{string.Join(" | ", texts.Select(t => t.Name.Length > 40 ? t.Name[..40] + "…(" + t.Name.Length + ")" : t.Name))}]");
                continue;
            }

            var room = columns[index].Right - columns[index].Left;
            if (cell.Rect.Width > room + 1)
            {
                problems.Add($"the {columns[index].Name} cell is {cell.Rect.Width:F0} px wide in a column {room:F0} px wide: its text is not trimmed");
            }

            if (cell.Help != full)
            {
                problems.Add($"the {columns[index].Name} cell's tooltip is not its full text: \"{(cell.Help.Length > 40 ? cell.Help[..40] + "…" : cell.Help)}\" ({cell.Help.Length} characters, expected {full.Length})");
            }
        }

        // A short text's tooltip is its text too.
        var shortRow = RowOf(view, apps.Full);
        foreach (var (index, full) in new[] { (1, apps.Full.Publisher!), (2, apps.Full.Version!) })
        {
            var cell = CellTexts(shortRow, columns[index]).FirstOrDefault(t => t.Name == full);
            if (cell.Name is null || cell.Help != full)
            {
                problems.Add($"the {columns[index].Name} cell of {apps.Full.Name} has the tooltip \"{cell.Help}\", expected \"{full}\"");
            }
        }

        if (row.Current.BoundingRectangle.Height > 60 * scale)
        {
            problems.Add($"the row of the long entry is {row.Current.BoundingRectangle.Height:F0} px high: more than one line");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- one line per row -----------------------------------------------------------------------------------------------------

    // "One line per row at the default window size": every row of the list, scrolled through, is as high as every other (to 1.5 px), no
    // higher than 60 px (DIPs, scaled), and its texts' centres are on one line (within 6 px). Owner decision 2026-10-20: the class note of a
    // row that has one ("Needs admin…", "Store app: …") is NOT drawn in the row (the Name cell is one text element holding the name only);
    // it stays in the row's automation name (Text.WithNote, never the cells joined) and in its tooltip, and is shown by UninstallNote when the
    // app is selected. Measured by rectangle once the layout has settled; the rows with notes are the machine's real per-machine and Store apps.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void EveryRowIsOneLineHighAndTheNameCellHoldsOnlyTheName()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var scale = ScaleOf(app);
        var columns = Columns(view);
        var notes = new[] { Text.NeedsAdminApp, Text.StoreAppDisplayOnly };
        var measured = new Dictionary<string, (double Height, double Spread)>(StringComparer.Ordinal);
        var problems = new List<string>();
        var withNote = 0;
        ScrollThrough(list, row =>
        {
            var name = row.Current.Name;
            var rect = row.Current.BoundingRectangle;
            var onScreen = WholeInside(rect, list.Current.BoundingRectangle);

            // The automation name holds for every realised row; the drawn note is read only on rows wholly inside the viewport (a realised
            // row below it has an empty rectangle and no text rectangles).
            if (notes.FirstOrDefault(n => name.EndsWith(", " + n, StringComparison.Ordinal)) is { } note)
            {
                var head = name[..^(", " + note).Length];
                if (name != Text.WithNote(head, note))
                {
                    problems.Add($"the row \"{name}\" is not the display name with the note (Text.WithNote)");
                }

                if (!onScreen)
                {
                    return;
                }

                withNote++;

                // Owner decision 2026-10-20: the Name cell shows only the name. No text element of the row holds the note as visible text; the
                // note is in the row's automation name (checked above) and in its tooltip.
                var nameCell = CellTexts(row, columns[0]);
                if (nameCell.Count != 1 || !nameCell[0].Name.StartsWith(head, StringComparison.Ordinal))
                {
                    problems.Add($"the name cell of \"{head}\" is not one text element holding the name: [{string.Join(" | ", nameCell.Select(t => t.Name))}]");
                }

                var rowTexts = Table.Texts(row);
                if (rowTexts.Any(t => t.Name == note || t.Name.StartsWith(note, StringComparison.Ordinal)))
                {
                    problems.Add($"the row of \"{head}\" draws the note \"{note}\" as its own text");
                }

                if (!rowTexts.Any(t => t.Help.Contains(note, StringComparison.Ordinal)) && !(row.Current.HelpText ?? string.Empty).Contains(note, StringComparison.Ordinal))
                {
                    problems.Add($"the tooltip of the row of \"{head}\" does not carry the note \"{note}\"");
                }
            }

            if (onScreen && !measured.ContainsKey(name))
            {
                var centres = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                    .Select(t => t.Current.BoundingRectangle).Where(r => !r.IsEmpty).Select(r => r.Top + (r.Height / 2)).ToList();
                measured[name] = (rect.Height, centres.Count == 0 ? 0 : centres.Max() - centres.Min());
            }
        });

        Console.WriteLine($"{measured.Count} rows measured, {withNote} with a class note; heights {string.Join(", ", measured.Values.Select(m => m.Height.ToString("F1", CultureInfo.InvariantCulture)).Distinct())}");
        if (withNote == 0)
        {
            problems.Add("no row with a class note was wholly on screen at any scroll position: the Name cell of such a row was not checked");
        }

        if (measured.Count < 4)
        {
            problems.Add($"only {measured.Count} whole rows measured on screen");
        }
        else
        {
            var heights = measured.Values.Select(m => m.Height).ToList();
            if (heights.Max() - heights.Min() > 1.5)
            {
                problems.Add("rows are not all as high as each other: " + string.Join(" | ", measured.Select(m => $"{m.Value.Height:F1} px: {(m.Key.Length > 50 ? m.Key[..50] + "…" : m.Key)}")));
            }

            if (heights.Max() > 60 * scale)
            {
                problems.Add($"a row is {heights.Max():F1} px high, more than one line");
            }

            foreach (var (name, (_, spread)) in measured.Where(m => m.Value.Spread > 6 * scale))
            {
                problems.Add($"the texts of one row are on more than one line (their centres are {spread:F1} px apart): {(name.Length > 60 ? name[..60] + "…" : name)}");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- the note under the table (owner decision 2026-10-20) --------------------------------------------------------------------

    /// <summary>What UninstallNote shows now: its text when it is on screen, else empty (an absent or collapsed block is empty).</summary>
    private static string NoteText(AutomationElement view)
    {
        try
        {
            return AppSession.TryFind(view, "UninstallNote") is { } note && Shown(note) ? note.Current.Name ?? string.Empty : string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static bool WaitForNote(AutomationElement view, string expected) =>
        AppSession.WaitUntil(() => NoteText(view) == expected, TimeSpan.FromSeconds(3));

    /// <summary>The display name of the first row (in list order, scrolled through) whose automation name carries <paramref name="note"/>, or null.</summary>
    private static string? FirstNamedWithNote(AutomationElement list, string note)
    {
        string? found = null;
        ScrollThrough(list, row =>
        {
            var name = row.Current.Name;
            if (found is null && name.EndsWith(", " + note, StringComparison.Ordinal))
            {
                found = name;
            }
        });
        return found;
    }

    private static void SelectRowNamed(AutomationElement list, string name)
    {
        var row = AppSession.WaitFor(() => RowNamed(list, name), Ui.ElementTimeout);
        Assert.IsNotNull(row, "no row named " + name);
        Ui.SelectionItemOf(row).Select();
    }

    // "UninstallNote, a TextBlock next to Uninstall…": selecting a needs-admin app shows its note there, on the button's line and not on top of
    // it. (A real per-machine app of this machine is only selected; Uninstall… is never pressed on it.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectingANeedsAdminAppShowsItsNoteNextToUninstall()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var scale = ScaleOf(app);
        var problems = new List<string>();
        if (NoteText(view).Length != 0)
        {
            problems.Add($"with nothing selected UninstallNote shows \"{NoteText(view)}\"");
        }

        var name = FirstNamedWithNote(list, Text.NeedsAdminApp);
        Assert.IsNotNull(name, "precondition: this machine lists no per-machine (needs admin) app");
        SelectRowNamed(list, name);
        if (!WaitForNote(view, Text.NeedsAdminApp))
        {
            problems.Add($"after selecting \"{name}\" UninstallNote shows \"{NoteText(view)}\", expected \"{Text.NeedsAdminApp}\"");
        }
        else
        {
            var note = AppSession.Find(view, "UninstallNote", Ui.ElementTimeout).Current.BoundingRectangle;
            var button = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
            if (!Shown(button))
            {
                problems.Add("UninstallInSettings is not shown for the selected needs-admin app");
            }
            else
            {
                var b = button.Current.BoundingRectangle;
                if (Math.Abs((note.Top + (note.Height / 2)) - (b.Top + (b.Height / 2))) > 14 * scale)
                {
                    problems.Add($"the note ({note}) is not on the line of Uninstall… ({b})");
                }

                if (!System.Windows.Rect.Intersect(note, b).IsEmpty && System.Windows.Rect.Intersect(note, b).Width > 2)
                {
                    problems.Add($"the note ({note}) overlaps Uninstall… ({b})");
                }
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // A Store app, which has no button, shows its note alone in the same place; an app with no note, and no selection, show UninstallNote empty.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AStoreAppShowsItsNoteAloneAndAnAppWithNoNoteShowsNone()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var problems = new List<string>();
        var store = FirstNamedWithNote(list, Text.StoreAppDisplayOnly);
        Assert.IsNotNull(store, "precondition: this machine lists no Store app");
        SelectRowNamed(list, store);
        if (!WaitForNote(view, Text.StoreAppDisplayOnly))
        {
            problems.Add($"after selecting the Store app \"{store}\" UninstallNote shows \"{NoteText(view)}\", expected \"{Text.StoreAppDisplayOnly}\"");
        }

        if (Shown(AppSession.TryFind(view, "UninstallInSettings")))
        {
            problems.Add("a Store app shows an Uninstall… button");
        }

        SelectRowNamed(list, apps.Full.Name);
        if (!WaitForNote(view, string.Empty))
        {
            problems.Add($"after selecting \"{apps.Full.Name}\" (no note) UninstallNote shows \"{NoteText(view)}\"");
        }

        if (!Shown(AppSession.TryFind(view, "UninstallInSettings")))
        {
            problems.Add("a per-user app shows no Uninstall… button");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // UninstallNote follows the selection by click and by the arrow keys, and is kept when the table is rebuilt (a header sort re-lays the rows
    // and must not lose the selected app's note). A neighbouring pair is found in the list order: a row with the needs-admin note and the next row
    // without one. The ASP-9 rebuild (leaving and opening the view with a watch running) cannot be tested here: it needs Uninstall… pressed on
    // an app with a note, and a needs-admin app is never pressed.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheNoteFollowsTheSelectionByClickAndArrowKeysAndSurvivesASort()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var order = OrderedNames(list);
        string NoteOf(string name) => new[] { Text.NeedsAdminApp, Text.StoreAppDisplayOnly }.FirstOrDefault(n => name.EndsWith(", " + n, StringComparison.Ordinal)) ?? string.Empty;
        var at = Enumerable.Range(0, order.Count - 1).FirstOrDefault(i => NoteOf(order[i]) != NoteOf(order[i + 1]) && (NoteOf(order[i]).Length > 0 || NoteOf(order[i + 1]).Length > 0), -1);
        Assert.IsTrue(at >= 0, "precondition: no two neighbouring rows differ in their note: " + string.Join(" | ", order.Take(30)));
        var (upper, lower) = (order[at], order[at + 1]);
        var problems = new List<string>();
        Away(app);
        var first = AppSession.WaitFor(() => RowNamed(list, upper), Ui.ElementTimeout)!;
        if ((first.Current.IsOffscreen || first.Current.BoundingRectangle.IsEmpty) && first.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
        {
            ((ScrollItemPattern)scroll).ScrollIntoView();
            System.Threading.Thread.Sleep(400);
        }

        var rect = first.Current.BoundingRectangle;
        Mouse.LeftClickAt(rect.Left + 40, rect.Top + (rect.Height / 2));
        if (!WaitForNote(view, NoteOf(upper)))
        {
            problems.Add($"after a click on \"{upper}\" UninstallNote shows \"{NoteText(view)}\", expected \"{NoteOf(upper)}\"");
        }

        Keys.Press(Keys.Down);
        if (!WaitForNote(view, NoteOf(lower)))
        {
            problems.Add($"after Down to \"{lower}\" UninstallNote shows \"{NoteText(view)}\", expected \"{NoteOf(lower)}\"");
        }

        Keys.Press(Keys.Up);
        if (!WaitForNote(view, NoteOf(upper)))
        {
            problems.Add($"after Up to \"{upper}\" UninstallNote shows \"{NoteText(view)}\", expected \"{NoteOf(upper)}\"");
        }

        // A header sort re-lays the rows: the selected app that has a note stays selected and keeps its note.
        var noted = NoteOf(upper).Length > 0 ? upper : lower;
        SelectRowNamed(list, noted);
        if (!WaitForNote(view, NoteOf(noted)))
        {
            problems.Add($"after selecting \"{noted}\" UninstallNote shows \"{NoteText(view)}\"");
        }

        Table.Press(Table.Header(view, Text.ColumnPublisher));
        System.Threading.Thread.Sleep(700);
        if (!AppSession.WaitUntil(() => RowNamed(list, noted) is { } r && IsSelected(r), TimeSpan.FromSeconds(3)) || !WaitForNote(view, NoteOf(noted)))
        {
            problems.Add($"after a sort \"{noted}\" is not selected or UninstallNote shows \"{NoteText(view)}\", expected \"{NoteOf(noted)}\"");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // The screenshots of the real view showed nearly every name cut to "Mi…": the note took the Name column's room. At the default window size a
    // name of 24 characters in a needs-admin row is shown whole. The drawn width of the name (WPF FormattedText, Segoe UI Variable 14 DIP, with 8%
    // allowed for the font's differences) must fit the Name text's rectangle, for every needs-admin or Store row of this machine on screen
    // whose name is up to 30 characters; the Name column itself must have room for 24 realistic characters. (A needs-admin test entry cannot be
    // written: it would need the machine hive, so the machine's own per-machine apps are measured.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ANeedsAdminAppShowsItsWholeNameInTheNameCell()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var scale = ScaleOf(app);
        var columns = Columns(view);
        double Needed(string text) => new System.Windows.Media.FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface("Segoe UI Variable Text, Segoe UI"), 14, System.Windows.Media.Brushes.Black, 1.0).WidthIncludingTrailingWhitespace * 1.08;

        var problems = new List<string>();
        var room = columns[0].HeaderWidth / scale;
        const string Sample = "Visual Studio Build Tool"; // 24 characters
        if (Needed(Sample) > room - 16)
        {
            problems.Add($"the Name column is {room:F0} DIP wide, too narrow for 24 characters ({Needed(Sample):F0} DIP needed)");
        }

        var notes = new[] { Text.NeedsAdminApp, Text.StoreAppDisplayOnly };
        var measured = 0;
        ScrollThrough(list, row =>
        {
            var name = row.Current.Name;
            if (notes.FirstOrDefault(n => name.EndsWith(", " + n, StringComparison.Ordinal)) is not { } note || !WholeInside(row.Current.BoundingRectangle, list.Current.BoundingRectangle))
            {
                return;
            }

            var head = name[..^(", " + note).Length];
            if (head.Length > 30)
            {
                return;
            }

            var text = CellTexts(row, columns[0]).FirstOrDefault(t => t.Name.StartsWith(head, StringComparison.Ordinal));
            if (text.Name is null)
            {
                problems.Add($"the Name cell of \"{head}\" has no text element holding the name");
                return;
            }

            measured++;
            var available = text.Rect.Width / scale;
            if (Needed(head) > available)
            {
                problems.Add($"the name \"{head}\" ({head.Length} characters) needs {Needed(head):F0} DIP but its text has {available:F0} DIP: it is trimmed");
            }
        });

        Console.WriteLine($"{measured} needs-admin or Store rows with names up to 30 characters measured; Name column {room:F0} DIP");
        if (measured < 3)
        {
            problems.Add($"only {measured} needs-admin or Store rows with a short name were measured on screen");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- sort ----------------------------------------------------------------------------------------------------------------

    // "Sort: every column sorts from its header, as in other tables." "Empty values: a missing value is an empty cell, and it sorts after every
    // real value in both directions." The entries with every value (A, B, F) come before the entries with none (C, D, E) in both directions of
    // the Publisher, Version, Size and Installed on columns, and A and B swap places between the two presses. (Which direction the first press
    // gives is the table's own; the test reads it.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    [DataRow(Text.ColumnPublisher)]
    [DataRow(Text.ColumnVersion)]
    [DataRow(Text.ColumnSize)]
    [DataRow(Text.ColumnInstalledOn)]
    public void AMissingValueSortsAfterEveryRealValueInBothDirections(string column)
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var real = new[] { apps.Full, apps.Second, apps.Long }.Select(a => a.Name).ToList();
        var empty = apps.Empties.Select(a => a.Name).ToList();
        var problems = new List<string>();
        var positions = new List<(int A, int B)>();
        for (var press = 1; press <= 2; press++)
        {
            Table.Press(Table.Header(view, column));
            System.Threading.Thread.Sleep(600);
            List<string> order = OrderedNames(list);
            var again = OrderedNames(list);
            Assert.IsTrue(order.SequenceEqual(again), $"press {press} on {column}: the order is still changing");
            var last = real.Max(order.IndexOf);
            var first = empty.Min(order.IndexOf);
            if (real.Concat(empty).Any(n => !order.Contains(n)))
            {
                problems.Add($"press {press} on {column}: a test entry is missing from the list: [{string.Join(" | ", order.Where(n => n.StartsWith("0LeanUiTable", StringComparison.Ordinal)))}]");
                continue;
            }

            if (last > first)
            {
                problems.Add($"press {press} on {column}: an entry with no value comes before one with a value (last real at {last}, first empty at {first})");
            }

            positions.Add((order.IndexOf(apps.Full.Name), order.IndexOf(apps.Second.Name)));
        }

        if (positions.Count == 2 && (positions[0].A < positions[0].B) == (positions[1].A < positions[1].B))
        {
            problems.Add($"{column}: the second press did not reverse the order of {apps.Full.Name} and {apps.Second.Name} ({positions[0]} then {positions[1]})");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- the magnifier -------------------------------------------------------------------------------------------------------

    private static void AssertMagnifier(AutomationElement view, AutomationElement row, App entry, bool expected, string how, List<string> problems)
    {
        AutomationElement? reveal = expected
            ? AppSession.WaitFor(() => ShownReveal(row), TimeSpan.FromSeconds(3))
            : (AppSession.WaitUntil(() => ShownReveal(row) is not null, TimeSpan.FromSeconds(1.5)) ? ShownReveal(row) : null);
        if (expected != (reveal is not null))
        {
            problems.Add($"{how}: {entry.Name} (InstallLocation {entry.InstallLocation ?? "none"}) {(expected ? "shows no magnifier" : "shows a magnifier")}");
            return;
        }

        if (reveal is null)
        {
            return;
        }

        if (reveal.Current.ControlType != ControlType.Button || reveal.Current.Name != Text.ShowInFileExplorer || !reveal.Current.IsEnabled || !reveal.TryGetCurrentPattern(InvokePattern.Pattern, out _))
        {
            problems.Add($"{how}: the magnifier of {entry.Name} is not an enabled button named \"{Text.ShowInFileExplorer}\" that can be invoked: {Ui.Describe(reveal)}");
        }

        var shown = Table.Controls(view, "RevealRow").Count(Shown);
        if (shown != 1)
        {
            problems.Add($"{how}: {shown} magnifiers are shown at once, expected the one on {entry.Name}");
        }
    }

    // UI contract "The magnifier": a row action (RevealRow, "Show in File Explorer"), shown on hover and on focus as in every table, only
    // when InstallLocation is a non-empty, rooted path to a folder that exists (checked when the row is built); Store apps never show it.
    // A (existing folder) and G (existing when the row was built) show it; B (a rooted path to a folder that does not exist), C (no
    // location), D ("." : not rooted), E (a rooted path to a file), F (no location) do not; nor do the locations that are not a fully qualified local path although a folder exists there (review round 1): H ("C:name", drive-relative), I ("\\Windows", rooted without a drive), J (\\\\localhost\\ambynet\\…, the loopback share), K (the same in the \\\\?\\UNC\\ form), M (\\\\.\\UNC\\…); and N (\\\\?\\C:\\<the folder of A>) and the junction O to the folder of A do show it (review round 2). With the focus and the pointer away, no row shows it.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheMagnifierShowsOnHoverAndOnFocusOnlyForARowWithAnExistingInstallFolder()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var problems = new List<string>();
        var withFolder = apps.WithMagnifier;

        Away(app);
        var visible = Table.Controls(view, "RevealRow").Count(Shown);
        if (visible != 0)
        {
            problems.Add($"with the focus and the pointer away, {visible} magnifier(s) are shown");
        }

        foreach (var entry in apps.All)
        {
            var row = RowOf(view, entry);
            row.SetFocus();
            AssertMagnifier(view, row, entry, withFolder.Contains(entry), "focus", problems);
        }

        Away(app);
        if (!AppSession.WaitUntil(() => !Table.Controls(view, "RevealRow").Any(Shown), TimeSpan.FromSeconds(3)))
        {
            problems.Add("with the focus and the pointer away again, a magnifier stays shown");
        }

        foreach (var entry in apps.All)
        {
            var row = RowOf(view, entry);
            HoverRow(row);
            AssertMagnifier(view, row, entry, withFolder.Contains(entry), "hover", problems);
        }

        Away(app);

        // A Store app never shows it (its folder is under WindowsApps), by hover and by focus.
        var stores = 0;
        ScrollThrough(list, row =>
        {
            if (stores >= 3 || !row.Current.Name.EndsWith(", " + Text.StoreAppDisplayOnly, StringComparison.Ordinal) || !WholeInside(row.Current.BoundingRectangle, list.Current.BoundingRectangle))
            {
                return;
            }

            stores++;
            row.SetFocus();
            System.Threading.Thread.Sleep(500);
            var byFocus = ShownReveal(row) is not null;
            HoverRow(row);
            System.Threading.Thread.Sleep(500);
            var byHover = ShownReveal(row) is not null;
            if (byFocus || byHover)
            {
                problems.Add($"the Store app row \"{row.Current.Name}\" shows a magnifier (focus {byFocus}, hover {byHover})");
            }
        });
        Console.WriteLine($"{stores} Store app row(s) checked for the magnifier");
        Away(app);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // "It opens the install folder in File Explorer, showing the folder's contents." Evidence: a new File Explorer window named for the install
    // folder (the folder itself, not its parent with the folder selected) that lists the marker file the test put in it. The window is closed
    // afterwards, and so is any other new one (ExplorerWindows).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheMagnifierOpensTheInstallFolderInFileExplorerShowingItsContents()
    {
        using var apps = new UninstallTestApps();
        using var explorer = new ExplorerWindows();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var row = RowOf(view, apps.Full);
        HoverRow(row);
        var reveal = AppSession.WaitFor(() => ShownReveal(row), TimeSpan.FromSeconds(4));
        Assert.IsNotNull(reveal, "precondition: no magnifier on the row of " + apps.Full.Name);
        var leaf = Path.GetFileName(apps.InstallFolder);
        Ui.Click(reveal);

        AutomationElement? window = null;
        var opened = AppSession.WaitUntil(() => (window = explorer.New().FirstOrDefault(w => ExplorerWindows.NameOf(w).Contains(leaf, StringComparison.OrdinalIgnoreCase))) is not null, TimeSpan.FromSeconds(20));
        Assert.IsTrue(opened, $"no File Explorer window named for {leaf} opened; new windows: [{string.Join(" | ", explorer.New().Select(ExplorerWindows.NameOf))}]; status \"{Status(app)}\"");
        var marker = AppSession.WaitFor(() => window!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, apps.MarkerName)), TimeSpan.FromSeconds(10));
        Assert.IsNotNull(marker, $"the Explorer window \"{ExplorerWindows.NameOf(window!)}\" does not list {apps.MarkerName}: it does not show the folder's contents");
        Assert.AreNotEqual(Text.RevealFailed(apps.InstallFolder), Status(app), "the status line says the folder could not be shown");
    }

    // "If opening fails, the status line says Text.RevealFailed(path)." G's folder exists when its row is built, and is removed by the test
    // before the magnifier is pressed (the check is made when the row is built, so the button is still there).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AMagnifierThatCannotOpenItsFolderSaysSoInTheStatusLine()
    {
        using var apps = new UninstallTestApps();
        using var explorer = new ExplorerWindows();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var row = RowOf(view, apps.Disappearing);
        HoverRow(row);
        var reveal = AppSession.WaitFor(() => ShownReveal(row), TimeSpan.FromSeconds(4));
        Assert.IsNotNull(reveal, "precondition: no magnifier on the row of " + apps.Disappearing.Name);
        Directory.Delete(apps.DisappearingFolder, recursive: true);
        var expected = Text.RevealFailed(apps.Disappearing.InstallLocation!);
        Ui.Click(reveal);
        Assert.IsTrue(AppSession.WaitUntil(() => Status(app) == expected, TimeSpan.FromSeconds(15)), $"the status line is \"{Status(app)}\", expected \"{expected}\"");
    }

    // ---- selection -----------------------------------------------------------------------------------------------------------

    // "A click, or the arrow keys, selects a row, with the system's selected-row look. The rows keep the SelectionItemPattern. … Single
    // selection." A real click on A selects A (and only A); "Uninstall…" is offered and works on it (it names A, opens Settings, starts the
    // watch: the test's own entry; Settings is closed afterwards); a click on B selects B alone, ends A's watch (the instruction is cleared, as
    // today) and the selected row looks different from the unselected one.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AClickSelectsOneRowAndUninstallInSettingsKeepsWorking()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Away(app);
        var problems = new List<string>();
        var a = RowOf(view, apps.Full);
        var b = RowOf(view, apps.Second);
        if (!a.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
        {
            problems.Add("an UninstallRow has no SelectionItemPattern");
        }

        var rectA = a.Current.BoundingRectangle;
        Mouse.LeftClickAt(rectA.Left + 40, rectA.Top + (rectA.Height / 2));
        if (!AppSession.WaitUntil(() => IsSelected(a), TimeSpan.FromSeconds(3)) || IsSelected(b))
        {
            problems.Add($"after a click on {apps.Full.Name}: selected {IsSelected(a)}, the next row selected {IsSelected(b)}");
        }

        apps.HandOff(view, apps.Full.Name);

        b = RowOf(view, apps.Second);
        var rectB = b.Current.BoundingRectangle;
        Mouse.LeftClickAt(rectB.Left + 40, rectB.Top + (rectB.Height / 2));
        a = RowOf(view, apps.Full);
        if (!AppSession.WaitUntil(() => IsSelected(b), TimeSpan.FromSeconds(3)) || IsSelected(a))
        {
            problems.Add($"after a click on {apps.Second.Name}: it is selected {IsSelected(b)}, {apps.Full.Name} is still selected {IsSelected(a)}");
        }

        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
        if (!AppSession.WaitUntil(() => instruction.Current.Name.Length == 0, TimeSpan.FromSeconds(3)))
        {
            problems.Add($"selecting another app left the instruction \"{instruction.Current.Name}\"");
        }

        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        if (!Shown(offer) || !offer.Current.IsEnabled)
        {
            problems.Add("UninstallInSettings is not shown and enabled for the second selected app");
        }

        // The selected-row look: the selected row is drawn differently from an unselected one (the cursor is parked off the rows).
        var window = app.Window(Ui.WindowTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, Ui.SidebarItem(app, "History").Current.BoundingRectangle);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        var selectedColour = Dominant(capture, RowOf(view, apps.Second).Current.BoundingRectangle);
        var otherColour = Dominant(capture, RowOf(view, apps.Bare).Current.BoundingRectangle);
        Console.WriteLine($"capture saved {capture.Save(nameof(AClickSelectsOneRowAndUninstallInSettingsKeepsWorking))}; selected {selectedColour}, unselected {otherColour}");
        if (selectedColour is not { } s || otherColour is not { } o || s.Near(o, 6))
        {
            problems.Add($"the selected row looks like an unselected one (selected {selectedColour}, unselected {otherColour})");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    private static Rgb? Dominant(ScreenCapture capture, System.Windows.Rect row)
    {
        if (row.IsEmpty || !capture.Contains((int)row.Left + 4, (int)row.Top + 2) || !capture.Contains((int)row.Right - 4, (int)row.Bottom - 2))
        {
            return null;
        }

        var samples = capture.Area((int)row.Left + 4, (int)row.Top + 2, (int)row.Right - 4, (int)row.Bottom - 2);
        return samples.Count == 0 ? null : samples.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
    }

    // "A click, or the arrow keys, selects a row": after a click on A, Down selects B (not A), Down again C, Up B again; with B selected by the
    // keys "Uninstall…" is offered and works (its own entry again). The entries are consecutive rows (…-A, …-B, …-C sort together).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheArrowKeysSelectRowsOneAtATimeAndUninstallInSettingsKeepsWorking()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Away(app);
        var problems = new List<string>();
        var a = RowOf(view, apps.Full);
        var rectA = a.Current.BoundingRectangle;
        Mouse.LeftClickAt(rectA.Left + 40, rectA.Top + (rectA.Height / 2));
        if (!AppSession.WaitUntil(() => IsSelected(RowOf(view, apps.Full)), TimeSpan.FromSeconds(3)))
        {
            Assert.Fail("precondition: a click did not select " + apps.Full.Name);
        }

        void Expect(string step, App selected)
        {
            var ok = AppSession.WaitUntil(() => IsSelected(RowOf(view, selected)), TimeSpan.FromSeconds(3));
            var others = new[] { apps.Full, apps.Second, apps.Bare }.Where(x => x != selected && IsSelected(RowOf(view, x))).Select(x => x.Name).ToList();
            if (!ok || others.Count > 0)
            {
                problems.Add($"{step}: {selected.Name} selected {ok}; other selected rows [{string.Join(", ", others)}]");
            }
        }

        Keys.Press(Keys.Down);
        Expect("Down from the first", apps.Second);
        Keys.Press(Keys.Down);
        Expect("Down from the second", apps.Bare);
        Keys.Press(Keys.Up);
        Expect("Up from the third", apps.Second);

        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        if (!Shown(offer) || !offer.Current.IsEnabled)
        {
            problems.Add("after the arrow keys, UninstallInSettings is not shown and enabled");
        }
        else
        {
            apps.HandOff(view, apps.Second.Name);
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // The ASP-9 rule ("rebuild keeps the selection") for a quick user: press "Uninstall…", leave the view at once (no waiting for Windows
    // Settings to open) and come back. The selection, the instruction and the watch are kept: OnUninstallClick must find the watch running
    // (or the selection kept) even when the launch of Settings has not returned yet. The watch is proved by removing the test's own entry:
    // within the 10 s the watch needs, the status line says the app is gone.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheSelectionAndTheWatchSurviveLeavingTheViewAtOnceAfterUninstallInSettings()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Ui.SelectionItemOf(RowOf(view, apps.Full)).Select();
        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(offer) && offer.Current.IsEnabled, TimeSpan.FromSeconds(5)), "precondition: UninstallInSettings is not offered");

        // Quick: the two sidebar items are found beforehand, so that nothing but the two clicks lies between pressing and coming back.
        var overview = Ui.SidebarItem(app, "Overview");
        var uninstall = Ui.SidebarItem(app, "Uninstall");
        Ui.Click(offer);
        Ui.Click(overview);
        Ui.Click(uninstall);

        view = apps.OpenAndWait(app);
        var problems = new List<string>();
        if (!AppSession.WaitUntil(() => IsSelected(RowOf(view, apps.Full)), TimeSpan.FromSeconds(5)))
        {
            problems.Add($"{apps.Full.Name} is not selected after the view was opened again");
        }

        if (!AppSession.WaitUntil(() => Shown(AppSession.TryFind(view, "UninstallInSettings")), TimeSpan.FromSeconds(2)))
        {
            problems.Add("UninstallInSettings is not shown after the view was opened again");
        }

        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
        if (instruction.Current.Name != Text.UninstallInSettings(apps.Full.Name))
        {
            problems.Add($"the instruction is \"{instruction.Current.Name}\" after the view was opened again, expected the one for {apps.Full.Name}");
        }

        // The watch is running: remove the entry and the app is gone within the watch's 10 s (and the checks' 2 s steps).
        Registry.CurrentUser.DeleteSubKeyTree(apps.Full.KeyPath);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        if (!AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(apps.Full.Name), TimeSpan.FromSeconds(60)))
        {
            problems.Add($"the entry was removed and the status line never said \"{Text.AppGone(apps.Full.Name)}\" within 60 s (status \"{status.Current.Name}\"): no watch was running");
        }

        GoneApp.DismissResultIfShown(app);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // Review round 1, MINOR 3: "A header sort keeps the selected app selected, and does not end a running watch: the instruction stays."
    // Two sorts (Publisher, then Size, each pressed twice): after each press the same app is still the one selected row, "Uninstall…" is
    // offered and the instruction still names it; removing the entry afterwards ends in "gone", so the watch ran through the sorts. (After
    // the hand-off the test waits 3 s, so that the watch has started before the first sort: the sort must not be what starts it.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AHeaderSortKeepsTheSelectionAndTheRunningWatch()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Ui.SelectionItemOf(RowOf(view, apps.Full)).Select();
        apps.HandOff(view, apps.Full.Name);
        System.Threading.Thread.Sleep(3000);
        var problems = new List<string>();
        foreach (var column in new[] { Text.ColumnPublisher, Text.ColumnPublisher, Text.ColumnSize, Text.ColumnSize })
        {
            Table.Press(Table.Header(view, column));
            System.Threading.Thread.Sleep(700);
            if (!AppSession.WaitUntil(() => IsSelected(RowOf(view, apps.Full)), TimeSpan.FromSeconds(3)))
            {
                problems.Add($"after sorting by {column}, {apps.Full.Name} is not selected");
            }

            if (IsSelected(RowOf(view, apps.Second)) || IsSelected(RowOf(view, apps.Bare)))
            {
                problems.Add($"after sorting by {column}, another app is selected too");
            }

            if (!Shown(AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout)))
            {
                problems.Add($"after sorting by {column}, UninstallInSettings is not shown");
            }

            var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout).Current.Name;
            if (instruction != Text.UninstallInSettings(apps.Full.Name))
            {
                problems.Add($"after sorting by {column}, the instruction is \"{instruction}\"");
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(apps.Full.KeyPath);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        if (!AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(apps.Full.Name), TimeSpan.FromSeconds(60)))
        {
            problems.Add($"the entry was removed and the status line never said \"{Text.AppGone(apps.Full.Name)}\" (status \"{status.Current.Name}\"): the sort ended the watch");
        }

        GoneApp.DismissResultIfShown(app);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // Review round 1, MINOR 1: "A network path is never probed: no SMB traffic, no stall when the view opens." The set has an entry on a
    // share at 192.0.2.1 (TEST-NET-1, reserved: nothing answers). A probe waits for the SMB timeout, so the view must list every row
    // within a few seconds, and the unreachable entry shows no magnifier. (If the network answers such a probe at once, this cannot fail;
    // it then proves nothing, and the run's note says so.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AnUnreachableNetworkLocationDoesNotStallTheView()
    {
        using var apps = new UninstallTestApps(withUnreachable: true);
        using var app = AppSession.Start();
        app.Window(Ui.WindowTimeout);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var view = Ui.OpenView(app, "Uninstall", "UninstallView");
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var listed = AppSession.WaitUntil(() => AppSession.FindAll(list, "UninstallRow").Count > 0, TimeSpan.FromSeconds(8));
        Console.WriteLine($"the view listed its rows after {watch.Elapsed.TotalSeconds:F1} s");
        Assert.IsTrue(listed, $"the Uninstall view listed no row within 8 s ({watch.Elapsed.TotalSeconds:F1} s): an unreachable network location was probed");
        var row = RowOf(view, apps.Unreachable!);
        HoverRow(row);
        Assert.IsFalse(AppSession.WaitUntil(() => ShownReveal(row) is not null, TimeSpan.FromSeconds(1.5)), "the unreachable network location shows a magnifier");
    }

    // Review round 2: "A mapped network drive counts as a network path." The test maps a free drive letter to the loopback share with
    // `net use` as the test user (in the console session), puts an entry on it, and removes the mapping afterwards, also when the test fails.
    // Inconclusive, with the reason, when the share cannot be mapped here.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AFolderOnAMappedNetworkDriveShowsNoMagnifier()
    {
        using var map = new MappedDrive();
        using var apps = new UninstallTestApps(mappedDriveLetter: map.Letter);
        Assert.IsTrue(Directory.Exists(apps.Mapped!.InstallLocation), "precondition: the folder on the mapped drive does not exist: " + apps.Mapped.InstallLocation);
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        var row = RowOf(view, apps.Mapped);
        var problems = new List<string>();
        row.SetFocus();
        AssertMagnifier(view, row, apps.Mapped, false, "focus", problems);
        HoverRow(row);
        AssertMagnifier(view, row, apps.Mapped, false, "hover", problems);
        Away(app);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- the launch of Settings held (QA seam) --------------------------------------------------------------------------------

    // SEAM THE BUILDER MUST ADD (a test-only QA argument, like --scan-delay; it opens no window and starts no process):
    //     AmbystoLean.exe --settings-launch "<delay_ms>:<ok|fail>[,<delay_ms>:<ok|fail>...]"
    // Each call of SystemLauncher.OpenSettingsAsync takes the next step: it waits that many milliseconds, then answers true (ok) or false (fail)
    // without opening Settings. When the steps are used up, the real launch is made. (A reference patch is in the report.) Without the seam the
    // launch is not held and these tests fail on their first check, saying so.
    private static void AssertLaunchIsHeld(UninstallTestApps apps)
    {
        System.Threading.Thread.Sleep(1200);
        Assert.AreEqual(0, apps.NewSettingsWindows(), "surface gap: Windows Settings opened, so the app did not hold the launch: it ignores --settings-launch (QA seam, review round 2)");
    }

    private static void AssertSelectedWithInstruction(AutomationElement view, UninstallTestApps apps, string when, List<string> problems)
    {
        if (!AppSession.WaitUntil(() => IsSelected(RowOf(view, apps.Full)), TimeSpan.FromSeconds(3)))
        {
            problems.Add($"{when}: {apps.Full.Name} is not selected");
        }

        if (!AppSession.WaitUntil(() => Shown(AppSession.TryFind(view, "UninstallInSettings")), TimeSpan.FromSeconds(2)))
        {
            problems.Add($"{when}: UninstallInSettings is not shown");
        }

        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout).Current.Name;
        if (instruction != Text.UninstallInSettings(apps.Full.Name))
        {
            problems.Add($"{when}: the instruction is \"{instruction}\", expected the one for {apps.Full.Name}");
        }
    }

    private static void AssertWatchRuns(AppSession app, UninstallTestApps apps, List<string> problems)
    {
        Registry.CurrentUser.DeleteSubKeyTree(apps.Full.KeyPath);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        if (!AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(apps.Full.Name), TimeSpan.FromSeconds(60)))
        {
            problems.Add($"the entry was removed and the status line never said \"{Text.AppGone(apps.Full.Name)}\" within 60 s (status \"{status.Current.Name}\"): no watch was running");
        }
        GoneApp.DismissResultIfShown(app);
    }

    // Review round 2, MINOR 5 (the race made deterministic): the launch of Settings is held for 12 s. "Uninstall…" is pressed, the view is left
    // and opened again while the launch is pending: the selection and the instruction are kept (the pending launch counts as a watch). When the
    // launch ends, the watch starts for the same app: removing the entry then ends in "gone". A mutant without the pending-flag use in the
    // keep rule clears the selection every time.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheSelectionAndTheWatchSurviveLeavingTheViewWhileTheLaunchIsHeld()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start("--settings-launch", "12000:ok");
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Ui.SelectionItemOf(RowOf(view, apps.Full)).Select();
        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(offer) && offer.Current.IsEnabled, TimeSpan.FromSeconds(5)), "precondition: UninstallInSettings is not offered");
        var overview = Ui.SidebarItem(app, "Overview");
        var uninstall = Ui.SidebarItem(app, "Uninstall");
        var held = System.Diagnostics.Stopwatch.StartNew();
        Ui.Click(offer);
        AssertLaunchIsHeld(apps);
        Ui.Click(overview);
        Ui.Click(uninstall);
        view = apps.OpenAndWait(app);
        var problems = new List<string>();
        AssertSelectedWithInstruction(view, apps, "with the launch pending", problems);
        Assert.IsTrue(held.Elapsed < TimeSpan.FromSeconds(11), $"precondition: the test was too slow ({held.Elapsed.TotalSeconds:F1} s): the launch was no longer pending");

        AppSession.WaitUntil(() => held.Elapsed > TimeSpan.FromSeconds(13.5), TimeSpan.FromSeconds(15));
        AssertSelectedWithInstruction(view, apps, "after the launch ended", problems);
        AssertWatchRuns(app, apps, problems);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // Review round 2, MINOR 4: two launches. "Uninstall…" is pressed twice quickly; the first launch fails after 2 s while the second is held for
    // 20 s (re-reading the list takes several seconds). When the first has failed (the status line says so), the view is left and opened again with the second still pending: the selection
    // and the instruction are kept (a single flag cleared by the first launch's end would lose them), and the second launch's end starts the watch.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheSelectionSurvivesTwoLaunchesWhenTheFirstFailsWhileTheSecondIsPending()
    {
        using var apps = new UninstallTestApps();
        using var app = AppSession.Start("--settings-launch", "2000:fail,20000:ok");
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Ui.SelectionItemOf(RowOf(view, apps.Full)).Select();
        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(offer) && offer.Current.IsEnabled, TimeSpan.FromSeconds(5)), "precondition: UninstallInSettings is not offered");
        var overview = Ui.SidebarItem(app, "Overview");
        var uninstall = Ui.SidebarItem(app, "Uninstall");
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        var held = System.Diagnostics.Stopwatch.StartNew();
        Ui.Click(offer);
        Ui.Click(offer);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.OpenSettingsFailed, TimeSpan.FromSeconds(6)),
            $"surface gap: the first launch did not fail (status \"{status.Current.Name}\"): the app ignores --settings-launch (QA seam, review round 2)");
        AssertLaunchIsHeld(apps);
        Ui.Click(overview);
        Ui.Click(uninstall);
        view = apps.OpenAndWait(app);
        var problems = new List<string>();
        AssertSelectedWithInstruction(view, apps, "with the second launch pending", problems);
        Assert.IsTrue(held.Elapsed < TimeSpan.FromSeconds(18), $"precondition: the test was too slow ({held.Elapsed.TotalSeconds:F1} s): the second launch was no longer pending");

        AppSession.WaitUntil(() => held.Elapsed > TimeSpan.FromSeconds(21.5), TimeSpan.FromSeconds(25));
        AssertWatchRuns(app, apps, problems);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // ---- select all on the leftovers -----------------------------------------------------------------------------------------

    private static AutomationElement? ShownSelectAll(AppSession app) => Ui.FindAllAnywhere(app, "SelectAll").FirstOrDefault(Shown);

    private static bool EnabledNow(AutomationElement? element)
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

    private static ToggleState SelectAllState(AppSession app) => Table.State(ShownSelectAll(app)!);

    /// <summary>The user's use of Select all: focus, then Space.</summary>
    private static void UseSelectAll(AppSession app)
    {
        var box = AppSession.WaitFor(() => ShownSelectAll(app), Ui.ElementTimeout);
        Assert.IsNotNull(box, "no Select all is shown with the leftovers");
        Assert.IsTrue(AppSession.WaitUntil(() => EnabledNow(ShownSelectAll(app)), Ui.ElementTimeout), "Select all is disabled");
        Ui.Focus(box);
        Keys.Press(Keys.Space);
        System.Threading.Thread.Sleep(400);
    }

    // UI contract "The leftovers table": LeftoverTable.EnableSelectAll(Text.SelectAll, () => !SelectAllBlocked). "It ticks only rows that can be
    // selected. It is off while a move or a scan runs, or a move is being confirmed. Move then takes the ticked leftovers." The test's gone app
    // has two leftovers; a protected folder inside one of them (the user's setting) makes that one shown, not selectable (its box is disabled).
    // Select all ticks the other only, a second use unticks it, and with the move being confirmed Select all is disabled; Cancel brings it back.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllOnLeftoversTicksOnlyTheSelectableRowsAndIsOffWhileAMoveIsConfirmed()
    {
        using var settings = new SettingsBackup();
        using var gone = new GoneApp();
        var keep = Directory.CreateDirectory(Path.Combine(gone.Leftover, "Keep")).FullName;
        SettingsBackup.WriteProtectedFolders(keep);
        using var app = AppSession.Start();
        var view = gone.OfferLeftovers(app);

        var protectedBox = Table.ControlWith(view, "LeftoverItem", gone.Leftover)!;
        var movableBox = Table.ControlWith(view, "LeftoverItem", gone.Install)!;
        Assert.IsFalse(protectedBox.Current.IsEnabled, "precondition: the leftover holding a protected folder can be selected: " + Table.Dump(view, "LeftoverItem"));
        Assert.IsTrue(movableBox.Current.IsEnabled, "precondition: the other leftover cannot be selected: " + Table.Dump(view, "LeftoverItem"));
        var problems = new List<string>();

        var selectAll = AppSession.WaitFor(() => ShownSelectAll(app), Ui.ElementTimeout);
        if (selectAll is null)
        {
            Assert.Fail("no Select all is shown with the leftovers offered");
        }

        if (selectAll.Current.Name != Text.SelectAll)
        {
            problems.Add($"Select all is named \"{selectAll.Current.Name}\", expected \"{Text.SelectAll}\"");
        }

        if (SelectAllState(app) != ToggleState.Off)
        {
            problems.Add("Select all is not unticked when the leftovers are offered (nothing is ticked to start)");
        }

        UseSelectAll(app);
        var ticked = AppSession.WaitUntil(() => Table.State(Table.ControlWith(view, "LeftoverItem", gone.Install)!) == ToggleState.On, TimeSpan.FromSeconds(3));
        if (!ticked)
        {
            problems.Add("Select all did not tick the leftover that can be selected: " + Table.Dump(view, "LeftoverItem"));
        }

        if (Table.State(Table.ControlWith(view, "LeftoverItem", gone.Leftover)!) != ToggleState.Off)
        {
            problems.Add("Select all ticked the leftover that is shown, not selectable");
        }

        if (SelectAllState(app) != ToggleState.On)
        {
            problems.Add($"with every selectable row ticked, Select all reads {SelectAllState(app)}, expected On");
        }

        var move = AppSession.Find(view, "MoveLeftovers", Ui.ElementTimeout);
        if (!AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)))
        {
            problems.Add("Move stays disabled after Select all ticked a leftover");
        }

        UseSelectAll(app);
        if (!AppSession.WaitUntil(() => Table.Controls(view, "LeftoverItem").All(b => Table.State(b) == ToggleState.Off), TimeSpan.FromSeconds(3)))
        {
            problems.Add("a second use of Select all did not untick the rows: " + Table.Dump(view, "LeftoverItem"));
        }

        // Off while a move is confirmed: tick, press Move (it only opens the confirmation), look, Cancel.
        UseSelectAll(app);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move stays disabled after Select all");
        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        if (EnabledNow(ShownSelectAll(app)))
        {
            problems.Add("Select all is enabled while the move is being confirmed");
        }

        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        if (!AppSession.WaitUntil(() => EnabledNow(ShownSelectAll(app)), Ui.ElementTimeout))
        {
            problems.Add("Select all stays disabled after the confirmation was cancelled");
        }

        Assert.IsTrue(File.Exists(Path.Combine(gone.Install, "app.dat")) && File.Exists(Path.Combine(gone.Leftover, "settings.dat")), "a leftover was moved: the test must only open the confirmation");
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // "It ticks only rows that can be selected": with no leftover that can be selected (a protected folder inside each), Select all is off
    // and ticks nothing.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllOnLeftoversIsOffWhenNoRowCanBeSelected()
    {
        using var settings = new SettingsBackup();
        using var gone = new GoneApp();
        var keepLeftover = Directory.CreateDirectory(Path.Combine(gone.Leftover, "Keep")).FullName;
        var keepInstall = Directory.CreateDirectory(Path.Combine(gone.Install, "Keep")).FullName;
        SettingsBackup.WriteProtectedFolders(keepLeftover, keepInstall);
        using var app = AppSession.Start();
        var view = gone.OfferLeftovers(app);
        var boxes = Table.Controls(view, "LeftoverItem").Where(b => Table.RowName(b).Contains(gone.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.IsTrue(boxes.Count == 2 && boxes.All(b => !b.Current.IsEnabled), "precondition: both leftovers should be shown, not selectable: " + Table.Dump(view, "LeftoverItem"));
        var selectAll = AppSession.WaitFor(() => ShownSelectAll(app), Ui.ElementTimeout);
        Assert.IsNotNull(selectAll, "no Select all is shown with the leftovers offered");
        Assert.IsFalse(EnabledNow(selectAll), "Select all is enabled although no leftover can be selected");
        Assert.IsFalse(Table.Controls(view, "LeftoverItem").Any(b => Table.State(b) != ToggleState.Off), "a leftover is ticked: " + Table.Dump(view, "LeftoverItem"));
    }

    // ---- High Contrast -------------------------------------------------------------------------------------------------------

    private static readonly int[] SystemColourIndexes = [1, 4, 5, 15]; // desktop, menu, window, button face

    // Under High Contrast the table follows the system colours: the unselected rows are one of window, button face, menu and desktop, and not
    // a brand tone (LeanPanel, LeanGround); the selected row is drawn differently, in a system colour (the highlight is one of them); every
    // cell still shows its value and the six headers are there.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheAppsTableFollowsTheSystemColours()
    {
        using var apps = new UninstallTestApps();
        using var contrast = new HighContrastScope();
        using var app = AppSession.Start();
        var view = apps.OpenAndWait(app);
        WaitForRowsToSettle(view);
        Table.AssertColumns(view, 1, SixHeaders);
        Ui.SelectionItemOf(RowOf(view, apps.Full)).Select();
        var window = app.Window(Ui.WindowTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, Ui.SidebarItem(app, "History").Current.BoundingRectangle);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"capture saved {capture.Save(nameof(UnderHighContrastTheAppsTableFollowsTheSystemColours))}");
        var system = SystemColourIndexes.Select(HighContrastScope.System).ToList();
        var highlight = HighContrastScope.System(13);
        var problems = new List<string>();

        var selected = Dominant(capture, RowOf(view, apps.Full).Current.BoundingRectangle);
        var plain = new[] { apps.Second, apps.Bare, apps.Relative }.Select(e => (e.Name, Colour: Dominant(capture, RowOf(view, e).Current.BoundingRectangle))).ToList();
        foreach (var (name, colour) in plain)
        {
            if (colour is not { } c)
            {
                problems.Add($"the row {name} is not on screen to sample");
            }
            else if (c.Near(Brand.Panel, Brand.NeutralTolerance) || c.Near(Brand.Ground, Brand.NeutralTolerance))
            {
                problems.Add($"the row {name} keeps a brand tone under High Contrast: {c}");
            }
            else if (!system.Any(s => s.Near(c, Brand.NeutralTolerance)))
            {
                problems.Add($"the row {name} is {c}, none of the system colours [{string.Join(", ", system)}]");
            }
        }

        if (selected is not { } sel)
        {
            problems.Add("the selected row is not on screen to sample");
        }
        else
        {
            if (plain.Any(p => p.Colour is { } c && c.Near(sel, 6)))
            {
                problems.Add($"the selected row ({sel}) looks like an unselected one");
            }

            if (!system.Concat([highlight]).Any(s => s.Near(sel, Brand.NeutralTolerance)))
            {
                problems.Add($"the selected row is {sel}, none of the system colours [{string.Join(", ", system.Concat([highlight]))}]");
            }
        }

        var columns = Columns(view);
        var row = RowOf(view, apps.Full);
        if (CellText(row, columns[1]) != apps.Full.Publisher || CellText(row, columns[3]) != apps.Full.SizeText)
        {
            problems.Add($"the cells of {apps.Full.Name} do not show their values under High Contrast: \"{CellText(row, columns[1])}\" \"{CellText(row, columns[3])}\"");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }
}
