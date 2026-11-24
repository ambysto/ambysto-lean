using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-15 contract amendments of 2026-10-19 (intent/2026-10-18-asp-15-duplicates-helpers.md, commits 8af7221 and
/// 4165461), written before the UI, from the contract only:
/// <list type="bullet">
/// <item>the row action <c>RevealRow</c> (Show in File Explorer) is a magnifier, Segoe Fluent Icons "Search" U+E721, in every
/// table with row actions: the largest files, Large and old and Duplicates (the Duplicates row actions themselves are in
/// <see cref="DuplicatesHelpersTests"/>);</item>
/// <item><c>SelectAll</c>, a three-state tick box in the Move column header of the largest files, Large and old and Temporary
/// files: it ticks every movable row of the list as shown and never a display-only row, a second use unticks them all, it
/// reads partly ticked when only some movable rows are ticked, nothing is ticked before the user uses it, Move still shows the
/// whole list, it is off while a scan runs and on a list with no movable row, and Duplicates has none;</item>
/// <item>the sidebar is <c>LeanSidebar</c> #161618, the content stays <c>LeanGround</c> #0b0b0c, with a 1 px <c>LeanRule</c>
/// line between them.</item></list>
/// Readings settled here: the glyph cannot be read through UI Automation (a WinUI font icon exposes no glyph, and the button's
/// content is not part of the contract), so it is read from the screen: the button's pixels are matched against U+E721 rendered
/// by WPF from the installed Segoe Fluent Icons, and must match it better than six other glyphs; RevealRow is never pressed
/// (the existing support never opens File Explorer; a test that did would leave a window of another program), so "opens File
/// Explorer with the file selected" is checked as: a named, enabled button that can be invoked, in the row of the file, with the
/// tooltip of the other tables. SelectAll is used with the keyboard (focus, Space), as a user would; Move being off while a
/// move runs is not tested (a move is too short to catch). Nothing is moved; the one confirmation is cancelled.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TablesAndSidebarAmendmentTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const string ShowInFileExplorer = "Show in File Explorer";

    // ---- the magnifier ----------------------------------------------------------------------------------------------------

    private const char Magnifier = '';

    /// <summary>Glyphs that must not match as well as the magnifier: Open in new window, Folder open, Page, Accept, Add, Folder.</summary>
    private static readonly char[] Decoys = ['', '', '', '', '', ''];

    private const int Grid = 20;

    /// <summary>A glyph rendered white on black by WPF from Segoe Fluent Icons, as a Grid x Grid mask of its bounding box.</summary>
    private static bool[,] Reference(char glyph)
    {
        if (!System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source == "Segoe Fluent Icons"))
        {
            Assert.Inconclusive("the font Segoe Fluent Icons is not installed on this machine");
        }

        bool[,]? mask = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var typeface = new System.Windows.Media.Typeface("Segoe Fluent Icons");
                var text = new System.Windows.Media.FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, 48, System.Windows.Media.Brushes.White, 1.0);
                var visual = new System.Windows.Media.DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, 96, 96));
                    context.DrawText(text, new System.Windows.Point(10, 10));
                }

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[96 * 96 * 4];
                bitmap.CopyPixels(pixels, 96 * 4, 0);
                var ink = new bool[96, 96];
                for (var y = 0; y < 96; y++)
                {
                    for (var x = 0; x < 96; x++)
                    {
                        ink[x, y] = pixels[(((y * 96) + x) * 4) + 2] > 110;
                    }
                }

                mask = Normalise(ink);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.IsNull(error, "rendering the reference glyph failed: " + error);
        Assert.IsNotNull(mask, "no reference glyph");
        return mask;
    }

    /// <summary>The bounding box of the ink, area-resampled to Grid x Grid cells (a cell is ink when a third of it is).</summary>
    private static bool[,] Normalise(bool[,] ink)
    {
        int width = ink.GetLength(0), height = ink.GetLength(1);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (ink[x, y])
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        var result = new bool[Grid, Grid];
        if (right < 0)
        {
            return result;
        }

        double boxWidth = right - left + 1, boxHeight = bottom - top + 1;
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                int x0 = left + (int)Math.Floor(gx * boxWidth / Grid), x1 = left + (int)Math.Max(Math.Ceiling((gx + 1) * boxWidth / Grid), Math.Floor(gx * boxWidth / Grid) + 1);
                int y0 = top + (int)Math.Floor(gy * boxHeight / Grid), y1 = top + (int)Math.Max(Math.Ceiling((gy + 1) * boxHeight / Grid), Math.Floor(gy * boxHeight / Grid) + 1);
                int on = 0, all = 0;
                for (var y = y0; y < Math.Min(y1, height); y++)
                {
                    for (var x = x0; x < Math.Min(x1, width); x++)
                    {
                        all++;
                        if (ink[x, y])
                        {
                            on++;
                        }
                    }
                }

                result[gx, gy] = all > 0 && on / (double)all >= 0.33;
            }
        }

        return result;
    }

    private static double Overlap(bool[,] a, bool[,] b)
    {
        int both = 0, either = 0;
        for (var y = 0; y < Grid; y++)
        {
            for (var x = 0; x < Grid; x++)
            {
                both += a[x, y] && b[x, y] ? 1 : 0;
                either += a[x, y] || b[x, y] ? 1 : 0;
            }
        }

        return either == 0 ? 0 : both / (double)either;
    }

    /// <summary>The glyph drawn on the button, from the screen: the button's pixels, the commonest colour as the background,
    /// everything far from it as ink; null when there is no ink.</summary>
    private static bool[,]? OnScreen(AutomationElement button, string name)
    {
        var box = button.Current.BoundingRectangle;
        var capture = ScreenCapture.Of(new System.Windows.Int32Rect((int)Math.Round(box.Left) + 1, (int)Math.Round(box.Top) + 1, Math.Max(4, (int)Math.Round(box.Width) - 2), Math.Max(4, (int)Math.Round(box.Height) - 2)));
        Console.WriteLine($"{name}: RevealRow capture {capture.Width}x{capture.Height}, saved {capture.Save("magnifier-" + name)}");
        var samples = capture.Area(capture.Left, capture.Top, capture.Right, capture.Bottom);
        var background = samples.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key;
        var ink = new bool[capture.Width, capture.Height];
        var any = false;
        for (var y = 0; y < capture.Height; y++)
        {
            for (var x = 0; x < capture.Width; x++)
            {
                var p = capture.At(capture.Left + x, capture.Top + y);
                var distance = Math.Abs(p.R - background.R) + Math.Abs(p.G - background.G) + Math.Abs(p.B - background.B);
                ink[x, y] = distance > 100;
                any |= ink[x, y];
            }
        }

        return any ? Normalise(ink) : null;
    }

    /// <summary>What a RevealRow button was, read while its row was hovered.</summary>
    private sealed record Reveal(string Table, string Name, string Help, bool Enabled, bool Invokable, ControlType Type, bool[,]? Glyph, string? Missing = null);

    /// <summary>The RevealRow of the row <paramref name="find"/> gives; a table without one is a <see cref="Reveal.Missing"/> record, so
    /// that the other tables are still read.</summary>
    private static Reveal ReadReveal(string table, Func<AutomationElement> find)
    {
        try
        {
            return ReadReveal(table, find());
        }
        catch (AssertFailedException missing)
        {
            return new Reveal(table, string.Empty, string.Empty, false, false, ControlType.Custom, null, missing.Message.ReplaceLineEndings(" "));
        }
    }

    private static Reveal ReadReveal(string table, AutomationElement row)
    {
        var rect = row.Current.BoundingRectangle;
        Mouse.HoverAt(rect.Left + (rect.Width / 3), rect.Top + (rect.Height / 2));
        var button = AppSession.WaitFor(() => Table.InRow(row, "RevealRow") is { } b && !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty ? b : null, TimeSpan.FromSeconds(4));
        Assert.IsNotNull(button, $"{table}: no RevealRow shown with the pointer over the row {row.Current.Name}");
        return new Reveal(table, button.Current.Name ?? string.Empty, button.Current.HelpText ?? string.Empty, button.Current.IsEnabled,
            button.TryGetCurrentPattern(InvokePattern.Pattern, out _), button.Current.ControlType, OnScreen(button, table));
    }

    private static AutomationElement RowHolding(AutomationElement scope, string file)
    {
        AutomationElement? row = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (row = Table.LeafRows(scope).FirstOrDefault(r => (r.Current.Name ?? string.Empty).Contains(file, StringComparison.OrdinalIgnoreCase))) is not null, Ui.ElementTimeout),
            $"no row for {file}: [{string.Join(" | ", Table.LeafRowNames(scope))}]");
        return row!;
    }

    /// <summary>One fixture for the three tables: film.mp4 (the largest files), big-old.zip (Large and old: 101 MB, three years
    /// old) and a pair of identical files (Duplicates). Returns the RevealRow of one row in each table.</summary>
    private static List<Reveal> RevealButtons()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-reveal");
        tree.File("film.mp4", 1400 * KB, 1);
        tree.File("big-old.zip", 101 * MB, 2);
        tree.SetModified("big-old.zip", DateTime.UtcNow.AddYears(-3));
        tree.File(@"dupa\d.dat", MB + (4 * KB), 3);
        tree.File(@"dupb\d.dat", MB + (4 * KB), 3);
        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var window = app.Window(Ui.WindowTimeout);
        WindowOnScreen.Settle(new AutomationElementHandle(new nint(window.Current.NativeWindowHandle)), Ui.SidebarItem(app, "History").Current.BoundingRectangle);
        var found = new List<Reveal>
        {
            ReadReveal("LargestFiles", () => RowHolding(ResultsViewTests.List(app), "film.mp4")),
        };
        var largeOld = Ui.OpenView(app, "LargeOld", "LargeOldView");
        found.Add(ReadReveal("LargeOld", () => RowHolding(largeOld, "big-old.zip")));
        var duplicates = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(duplicates, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlFor(duplicates, "DuplicateCopy", tree.Sub(@"dupb\d.dat")) is not null, Ui.DuplicatesTimeout), "no duplicate group appeared");
        found.Add(ReadReveal("Duplicates", () => Table.RowOf(Table.ControlFor(duplicates, "DuplicateCopy", tree.Sub(@"dupb\d.dat"))!)!));
        return found;
    }

    // Amendment 1: RevealRow's glyph is the magnifier U+E721 in all three tables. Read from the screen (class summary).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void RevealRowIsAMagnifierInEveryTableWithRowActions()
    {
        var reference = Reference(Magnifier);
        var decoys = Decoys.ToDictionary(d => d, Reference);
        var problems = new List<string>();
        var masks = new List<(string Table, bool[,] Mask)>();
        foreach (var reveal in RevealButtons())
        {
            if (reveal.Missing is not null)
            {
                problems.Add($"{reveal.Table}: {reveal.Missing}");
                continue;
            }

            if (reveal.Glyph is null)
            {
                problems.Add($"{reveal.Table}: no glyph ink on the RevealRow button");
                continue;
            }

            masks.Add((reveal.Table, reveal.Glyph));
            var score = Overlap(reveal.Glyph, reference);
            var best = decoys.Select(d => (Glyph: d.Key, Score: Overlap(reveal.Glyph, d.Value))).MaxBy(d => d.Score);
            Console.WriteLine($"{reveal.Table}: overlap with U+E721 {score:F2}; best decoy U+{(int)best.Glyph:X4} {best.Score:F2}");
            if (score < 0.45 || score < best.Score + 0.05)
            {
                problems.Add($"{reveal.Table}: the RevealRow glyph is not the magnifier U+E721: overlap {score:F2}, best other glyph U+{(int)best.Glyph:X4} {best.Score:F2}");
            }
        }

        for (var i = 1; i < masks.Count; i++)
        {
            var same = Overlap(masks[0].Mask, masks[i].Mask);
            if (same < 0.6)
            {
                problems.Add($"{masks[0].Table} and {masks[i].Table} draw different glyphs on RevealRow (overlap {same:F2})");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Amendment 1: RevealRow is the same action in the three tables: an enabled button that can be invoked, named "Show in File
    // Explorer", with one tooltip as UI Automation shows it (the existing tables expose none, so "the same" may be empty). (Not pressed: see the class summary.)
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void RevealRowIsTheSameNamedButtonInEveryTable()
    {
        var buttons = RevealButtons();
        var problems = new List<string>();
        foreach (var reveal in buttons)
        {
            if (reveal.Missing is not null)
            {
                problems.Add($"{reveal.Table}: {reveal.Missing}");
                continue;
            }

            if (reveal.Type != ControlType.Button || reveal.Name != ShowInFileExplorer || !reveal.Enabled || !reveal.Invokable)
            {
                problems.Add($"{reveal.Table}: RevealRow is {reveal.Type.ProgrammaticName} \"{reveal.Name}\", enabled {reveal.Enabled}, invokable {reveal.Invokable}");
            }

            if (reveal.Help != buttons[0].Help)
            {
                problems.Add($"{reveal.Table}: tooltip (HelpText) \"{reveal.Help}\" is not the largest files' \"{buttons[0].Help}\"");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // ---- select all -------------------------------------------------------------------------------------------------------

    private static readonly string[] MovableFiles = ["film.mp4", "disk.iso", "bundle.zip", "paper.pdf"];

    private static readonly string[] OtherGroups = ["disk.iso", "bundle.zip", "paper.pdf"];

    private sealed record Box(string Row, ToggleState State, bool Enabled);

    private static AutomationElement? ShownSelectAll(AppSession app)
    {
        var shown = Ui.FindAllAnywhere(app, "SelectAll").Where(e =>
        {
            try
            {
                return !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }).ToList();
        Assert.IsTrue(shown.Count <= 1, $"{shown.Count} SelectAll boxes are shown at once");
        return shown.FirstOrDefault();
    }

    private static AutomationElement SelectAllOf(AppSession app)
    {
        var box = AppSession.WaitFor(() => ShownSelectAll(app), Ui.ElementTimeout);
        Assert.IsNotNull(box, "no SelectAll box is shown");
        return box;
    }

    private static ToggleState StateOf(AppSession app) => ((TogglePattern)SelectAllOf(app).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

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

    /// <summary>The user's use of SelectAll: focus, then Space. Every caller wants a box that takes the key, so this waits until it is
    /// enabled: the app keeps it off until a confirmation's <c>ShowAsync</c> has returned, which is after the dialog's close fade
    /// (the dialog is gone from the UI Automation tree at the start of the fade). It fails if the box never comes back on.</summary>
    private static void UseSelectAll(AppSession app)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => EnabledNow(ShownSelectAll(app)), Ui.ElementTimeout), "SelectAll stays disabled after the confirmation was cancelled");
        Ui.Focus(SelectAllOf(app));
        Keys.Press(Keys.Space);
        System.Threading.Thread.Sleep(400);
    }

    private static AutomationElement? ScrollerOf(AutomationElement scope)
    {
        try
        {
            return scope.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The tick boxes of the rows of <paramref name="scope"/> (not SelectAll, which is in a header), the list scrolled
    /// from top to bottom so that each row is realised once.</summary>
    private static List<Box> Boxes(AutomationElement scope)
    {
        var seen = new Dictionary<string, Box>(StringComparer.Ordinal);
        void Take()
        {
            List<AutomationElement> boxes;
            try
            {
                boxes = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Cast<AutomationElement>().ToList();
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            foreach (var box in boxes)
            {
                try
                {
                    if (box.Current.AutomationId == "SelectAll" || Table.RowOf(box) is not { } row)
                    {
                        continue;
                    }

                    seen[row.Current.Name ?? string.Empty] = new Box(row.Current.Name ?? string.Empty, Table.State(box), box.Current.IsEnabled);
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        Take();
        // The list is rebuilt by a group click or a rule; a UI Automation call on the scroller that is replaced under it throws
        // InvalidOperationException (seen 2026-10-19). The pass is then dropped and the scroller read again, a few times; what was seen
        // before is kept, so a list that is still changing is only read once it has settled (see SettleList).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (ScrollerOf(scope) is not { } scroller)
                {
                    break;
                }

                var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
                var step = Math.Max(5.0, scroll.Current.VerticalViewSize / 2);
                for (var at = 0.0; at <= 100.0; at += step)
                {
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
                    System.Threading.Thread.Sleep(150);
                    Take();
                }

                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0.0);
                System.Threading.Thread.Sleep(150);
                break;
            }
            catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
            {
                System.Threading.Thread.Sleep(300);
            }
        }

        return [.. seen.Values];
    }

    /// <summary>Waits until the list reads the same twice, 400 ms apart: a group click or a rule has finished rebuilding it.</summary>
    private static List<Box> SettleList(Func<List<Box>> read)
    {
        var previous = Dump(read());
        List<Box> now = [];
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                System.Threading.Thread.Sleep(400);
                now = read();
                var same = Dump(now) == previous;
                previous = Dump(now);
                return same;
            }, Ui.ElementTimeout), "the list does not settle: " + Dump(now));
        return now;
    }

    /// <summary>Clicks the type group and waits for the list to settle.</summary>
    private static void ChooseGroup(AppSession app, string label, Func<List<Box>> read)
    {
        Ui.Click(TypeGroup(app, label));
        System.Threading.Thread.Sleep(500);
        SettleList(read);
    }

    private static string Dump(List<Box> boxes) => "[" + string.Join(" | ", boxes.Select(b => $"{b.Row} ({b.State}{(b.Enabled ? string.Empty : ", disabled")})")) + "]";

    private static bool Holds(Box box, string name) => box.Row.Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>SelectAll is a check box in the table's Move column: above the first row, over the column of the rows' boxes.</summary>
    private static void AssertInTheMoveHeader(AutomationElement selectAll, AutomationElement scope)
    {
        Assert.AreEqual(ControlType.CheckBox, selectAll.Current.ControlType, "SelectAll is a check box: " + Ui.Describe(selectAll));
        Assert.IsTrue(selectAll.Current.IsEnabled, "SelectAll is disabled on a list with movable rows");
        var rowBox = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Cast<AutomationElement>()
            .FirstOrDefault(c => c.Current.AutomationId != "SelectAll" && Table.RowOf(c) is not null && !c.Current.IsOffscreen);
        Assert.IsNotNull(rowBox, "no row tick box on screen to align SelectAll with");
        var all = selectAll.Current.BoundingRectangle;
        var row = rowBox.Current.BoundingRectangle;
        Assert.IsTrue(all.Bottom <= row.Top + 2, $"SelectAll {all} is not above the first row's box {row} (it belongs in the header)");
        Assert.IsTrue(Math.Abs((all.Left + (all.Width / 2)) - (row.Left + (row.Width / 2))) <= 24, $"SelectAll {all} is not over the Move column of the rows' boxes {row}");
    }

    /// <summary>The tick box of the row holding <paramref name="name"/> (not SelectAll).</summary>
    private static AutomationElement? RowBox(AutomationElement scope, string name) =>
        scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Cast<AutomationElement>()
            .FirstOrDefault(c => c.Current.AutomationId != "SelectAll" && Table.RowName(c).Contains(name, StringComparison.OrdinalIgnoreCase));

    private static void WaitForBoxes(Func<List<Box>> read, Func<List<Box>, bool> condition, string what)
    {
        List<Box> now = [];
        Assert.IsTrue(AppSession.WaitUntil(() => condition(now = read()), Ui.ElementTimeout), what + ": " + Dump(now));
    }

    private static void AssertAllMovableTicked(List<Box> boxes, int movable, string where)
    {
        Assert.AreEqual(movable, boxes.Count(b => b.Enabled), $"{where}: movable rows: {Dump(boxes)}");
        Assert.IsTrue(boxes.Where(b => b.Enabled).All(b => b.State == ToggleState.On), $"{where}: a movable row is not ticked: {Dump(boxes)}");
        Assert.IsTrue(boxes.Where(b => !b.Enabled).All(b => b.State == ToggleState.Off), $"{where}: a display-only row is ticked: {Dump(boxes)}");
    }

    private static AutomationElement TypeGroup(AppSession app, string label)
    {
        AutomationElement? found = null;
        var column = AppSession.Find(app.Window(Ui.WindowTimeout), "TypeGroups", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => (found = AppSession.FindAll(column, "TypeGroup").FirstOrDefault(g => g.Current.Name.StartsWith(label, StringComparison.Ordinal))) is not null, Ui.ElementTimeout), "no type group " + label);
        return found!;
    }

    // Amendment 2, the largest files: SelectAll is in the Move header; nothing is ticked before it is used; it reads unticked;
    // used, it ticks every movable row and never the display-only locked.mp4, reads ticked, and Move opens the confirmation
    // with all four items (cancelled); used again it unticks them all; one tick by hand reads partly ticked, the others by hand
    // read ticked; it follows the current group (Video: film.mp4 only, the other groups' rows stay unticked) and is disabled on a
    // group with no row (Application).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllInTheLargestFilesTicksTheMovableRowsOfTheCurrentGroupOnly()
    {
        using var settings = new SettingsBackup();
        using var tree = new MoveFixture("asp15-selall");
        tree.File("film.mp4", 1400 * KB, 1);
        tree.File("disk.iso", 1300 * KB, 2);
        tree.File("bundle.zip", 1200 * KB, 3);
        tree.File("paper.pdf", 1100 * KB, 4);
        tree.File(@"guarded\locked.mp4", 600 * KB, 5);
        SettingsBackup.WriteProtectedFolders(tree.Sub("guarded"));
        var identities = MovableFiles.ToDictionary(f => f, f => MoveFixture.Identity(tree.Sub(f)));
        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var list = ResultsViewTests.List(app);
        List<Box> Read() => Boxes(list);

        AssertInTheMoveHeader(SelectAllOf(app), list);
        Assert.AreEqual(ToggleState.Off, StateOf(app), "SelectAll reads ticked or partly ticked before it is used: " + Dump(Read()));
        Assert.IsTrue(Read().All(b => b.State == ToggleState.Off), "a row is ticked before SelectAll is used: " + Dump(Read()));

        UseSelectAll(app);
        WaitForBoxes(Read, b => b.Count(x => x.Enabled && x.State == ToggleState.On) == 4, "SelectAll did not tick the four movable rows");
        AssertAllMovableTicked(Read(), 4, "after SelectAll");
        Assert.AreEqual(ToggleState.On, StateOf(app), "SelectAll does not read ticked with every movable row ticked");

        var move = AppSession.Find(app.Window(Ui.WindowTimeout), "MoveLargest", Ui.ElementTimeout);
        Assert.IsTrue(move.Current.IsEnabled, "Move is disabled with rows ticked");
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        try
        {
            Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(dialog, "ConfirmItem").Count == 4, Ui.ElementTimeout), "the confirmation does not list the four ticked files: " + Ui.Dump(dialog));
            Assert.IsFalse(Ui.AnyNameContains(dialog, "locked.mp4"), "the display-only file is in the confirmation");
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        }

        UseSelectAll(app);
        WaitForBoxes(Read, b => b.All(x => x.State == ToggleState.Off), "a second use of SelectAll did not untick every row");
        Assert.AreEqual(ToggleState.Off, StateOf(app), "SelectAll does not read unticked with no row ticked");

        // By hand: one movable row ticked reads partly ticked, all four read ticked.
        Table.Tick(RowBox(list, "film.mp4")!, () => RowBox(list, "film.mp4"), "film.mp4");
        Assert.IsTrue(AppSession.WaitUntil(() => StateOf(app) == ToggleState.Indeterminate, Ui.ElementTimeout), "SelectAll does not read partly ticked with one of four movable rows ticked: " + StateOf(app));
        foreach (var name in OtherGroups)
        {
            Table.Tick(RowBox(list, name)!, () => RowBox(list, name), name);
        }

        Assert.IsTrue(AppSession.WaitUntil(() => StateOf(app) == ToggleState.On, Ui.ElementTimeout), "SelectAll does not read ticked with every movable row ticked by hand: " + StateOf(app));
        UseSelectAll(app);
        WaitForBoxes(Read, b => b.All(x => x.State == ToggleState.Off), "SelectAll did not untick every row when every movable row was ticked");

        // The current group.
        ChooseGroup(app, Text.TypeVideo, Read);
        UseSelectAll(app);
        WaitForBoxes(Read, b => b.Any(x => Holds(x, "film.mp4") && x.State == ToggleState.On), "SelectAll in the Video group did not tick film.mp4");
        var video = Read();
        Assert.IsFalse(video.Any(b => Holds(b, "locked.mp4") && b.State == ToggleState.On), "the display-only locked.mp4 is ticked: " + Dump(video));
        Assert.IsFalse(video.Any(b => Holds(b, "disk.iso") || Holds(b, "bundle.zip") || Holds(b, "paper.pdf")), "the Video group lists other groups' files: " + Dump(video));
        ChooseGroup(app, Text.AllFiles, Read);
        var all = Read();
        foreach (var name in OtherGroups)
        {
            Assert.AreEqual(ToggleState.Off, all.First(b => Holds(b, name)).State, $"SelectAll in the Video group ticked {name}: {Dump(all)}");
        }

        ChooseGroup(app, Text.TypeApplication, Read);
        Assert.IsTrue(AppSession.WaitUntil(() => Read().Count == 0, Ui.ElementTimeout), "the Application group lists rows: " + Dump(Read()));
        Assert.IsTrue(AppSession.WaitUntil(() => ShownSelectAll(app) is { } s && !s.Current.IsEnabled, Ui.ElementTimeout), "SelectAll is enabled on a list with no row");

        foreach (var (file, identity) in identities)
        {
            Assert.AreEqual(identity, MoveFixture.Identity(tree.Sub(file)), file + " is not the same file in place");
        }
    }

    private static void ChooseOption(AppSession app, AutomationElement combo, Func<string, bool> wanted, string what)
    {
        var expandable = combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand);
        if (expandable)
        {
            ((ExpandCollapsePattern)expand).Expand();
            System.Threading.Thread.Sleep(300);
        }

        var condition = new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true);
        AutomationElement? Look() => combo.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Concat(Ui.TopWindows(app).SelectMany(t => t.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()))
            .FirstOrDefault(e => wanted(e.Current.Name ?? string.Empty));
        var option = AppSession.WaitFor(Look, Ui.ElementTimeout);
        Assert.IsNotNull(option, $"no option {what} in {combo.Current.AutomationId}");
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expandable && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
        {
            ((ExpandCollapsePattern)expand).Collapse();
        }
    }

    // Amendment 2, Large and old: used, SelectAll ticks old1.zip and old2.zip and never guarded\old3.zip, which "Show system
    // items" lists as a display-only row; it reads ticked; used again it unticks; with a size choice that leaves no row (1 GB)
    // it is disabled, although the display-only row is still listed.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllInLargeAndOldTicksTheMovableRowsAsShownAndNeverADisplayOnlyRow()
    {
        using var settings = new SettingsBackup();
        using var tree = new MoveFixture("asp15-selall-lo");
        var old = DateTime.UtcNow.AddYears(-3);
        foreach (var (relative, seed) in new[] { ("old1.zip", 1), ("old2.zip", 2), (@"guarded\old3.zip", 3) })
        {
            tree.File(relative, 101 * MB, seed);
            tree.SetModified(relative, old);
        }

        SettingsBackup.WriteProtectedFolders(tree.Sub("guarded"));
        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        List<Box> Read() => Boxes(view);
        WaitForBoxes(Read, b => b.Count == 2, "Large and old does not list its two movable rows");
        AssertInTheMoveHeader(SelectAllOf(app), view);
        Assert.AreEqual(ToggleState.Off, StateOf(app), "SelectAll is not unticked before it is used");
        Assert.IsTrue(Read().All(b => b.State == ToggleState.Off), "a row is ticked before SelectAll is used");

        UseSelectAll(app);
        WaitForBoxes(Read, b => b.Count == 2 && b.All(x => x.State == ToggleState.On), "SelectAll did not tick the two rows");
        Assert.AreEqual(ToggleState.On, StateOf(app), "SelectAll does not read ticked");
        UseSelectAll(app);
        WaitForBoxes(Read, b => b.All(x => x.State == ToggleState.Off), "a second use did not untick the rows");

        var system = (TogglePattern)AppSession.Find(view, "ShowSystemItems", Ui.ElementTimeout).GetCurrentPattern(TogglePattern.Pattern);
        system.Toggle();
        WaitForBoxes(Read, b => b.Count == 3, "Show system items does not list the display-only row");
        UseSelectAll(app);
        WaitForBoxes(Read, b => b.Count(x => x.Enabled && x.State == ToggleState.On) == 2, "SelectAll did not tick the two movable rows with a display-only row listed");
        AssertAllMovableTicked(Read(), 2, "with the display-only row listed");
        Assert.AreEqual(ToggleState.On, StateOf(app), "SelectAll does not read ticked: a display-only row must not count against it");

        ChooseOption(app, AppSession.Find(view, "MinimumSize", Ui.ElementTimeout), n => System.Text.RegularExpressions.Regex.IsMatch(n.Trim(), @"^1([.,]0+)?\s*GB$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "1 GB");
        WaitForBoxes(Read, b => b.All(x => !x.Enabled), "a 1 GB threshold still lists a movable row");
        Assert.IsTrue(AppSession.WaitUntil(() => ShownSelectAll(app) is { } s && !s.Current.IsEnabled, Ui.ElementTimeout), "SelectAll is enabled on a list with no movable row");
    }

    // Amendment 2, Temporary files: three old files of the test's own in %LOCALAPPDATA%\Temp (and whatever else the list
    // holds): SelectAll reads unticked and nothing is ticked at first; used, it ticks every movable row and no display-only
    // row (the system-wide Windows Temp row among them); used again it unticks. Nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllInTemporaryFilesTicksEveryMovableRowAndNoDisplayOnlyRow()
    {
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        var id = Guid.NewGuid().ToString("N")[..8];
        var files = Enumerable.Range(1, 3).Select(i => Path.Combine(temp, $"LeanUiTemp-selall-{i}-{id}.bin")).ToList();
        try
        {
            var old = DateTime.UtcNow.AddDays(-10);
            foreach (var file in files)
            {
                File.WriteAllBytes(file, new byte[100 * KB]);
                File.SetCreationTimeUtc(file, old);
                File.SetLastWriteTimeUtc(file, old);
                File.SetLastAccessTimeUtc(file, old);
            }

            using var app = AppSession.Start();
            var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            List<Box> Read() => Boxes(view);
            WaitForBoxes(Read, b => files.All(f => b.Any(x => Holds(x, Path.GetFileName(f)))), "the Temporary files view does not list the test's three files");
            AssertInTheMoveHeader(SelectAllOf(app), view);
            Assert.AreEqual(ToggleState.Off, StateOf(app), "SelectAll is not unticked before it is used");
            Assert.IsTrue(Read().All(b => b.State == ToggleState.Off), "a row is ticked before SelectAll is used: " + Dump(Read()));

            UseSelectAll(app);
            WaitForBoxes(Read, b => b.Where(x => x.Enabled).All(x => x.State == ToggleState.On) && b.Count(x => x.Enabled) >= 3, "SelectAll did not tick every movable row");
            var ticked = Read();
            Assert.IsTrue(ticked.Where(b => !b.Enabled).All(b => b.State == ToggleState.Off), "a display-only row is ticked: " + Dump(ticked));
            foreach (var file in files)
            {
                Assert.AreEqual(ToggleState.On, ticked.First(b => Holds(b, Path.GetFileName(file))).State, file + " is not ticked");
            }

            Assert.AreEqual(ToggleState.On, StateOf(app), "SelectAll does not read ticked");
            UseSelectAll(app);
            WaitForBoxes(Read, b => b.All(x => x.State == ToggleState.Off), "a second use did not untick the rows");
            Assert.AreEqual(ToggleState.Off, StateOf(app), "SelectAll does not read unticked");
        }
        finally
        {
            foreach (var file in files)
            {
                for (var attempt = 0; attempt < 5 && File.Exists(file); attempt++)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                }
            }
        }
    }

    // Amendment 2, scan: SelectAll is off while a scan runs. A walk made slow with --scan-delay over 200 folders: any SelectAll
    // shown while Cancel is enabled is disabled; once the scan is over it is shown and enabled.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllIsOffWhileAScanRuns()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-selall-scan");
        for (var i = 0; i < 200; i++)
        {
            tree.File($@"d{i:D3}\f.bin", 4 * KB, i);
        }

        using var app = AppSession.Start("--scan", tree.Path, "--scan-delay", "20");
        var enabledWhileRunning = new List<string>();
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), Ui.ElementTimeout), "the scan did not start");
        while (Ui.ScanRunning(app))
        {
            if (ShownSelectAll(app) is { } selectAll && selectAll.Current.IsEnabled)
            {
                enabledWhileRunning.Add(Ui.Describe(selectAll));
            }

            System.Threading.Thread.Sleep(100);
        }

        Assert.AreEqual(0, enabledWhileRunning.Count, "SelectAll was enabled while the scan ran: " + string.Join(" | ", enabledWhileRunning.Distinct()));
        Ui.WaitForScan(app);
        Assert.IsTrue(AppSession.WaitUntil(() => ShownSelectAll(app) is { } s && s.Current.IsEnabled, Ui.ElementTimeout), "SelectAll is not shown and enabled once the scan is over");
    }

    // Amendment 2: Duplicates has no SelectAll (it would tick every copy of a group; Select by rule does that job).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void DuplicatesHasNoSelectAll()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-selall-dups");
        tree.File(@"a\d.dat", MB + (4 * KB), 1);
        tree.File(@"b\d.dat", MB + (4 * KB), 1);
        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0, Ui.DuplicatesTimeout), "no duplicate group appeared");
        Assert.IsNotNull(Table.ControlFor(view, "DuplicateCopy", tree.Sub(@"a\d.dat")), "precondition: the copies are listed");
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(0, AppSession.FindAll(view, "SelectAll").Count, "the Duplicates view has a SelectAll");
        Assert.IsNull(ShownSelectAll(app), "a SelectAll is shown beside the Duplicates view");
    }

    // ---- the sidebar's own tone -------------------------------------------------------------------------------------------

    // Amendment 2: the sidebar is LeanSidebar #161618 (sampled in its empty band between History and Settings), a 1 px
    // LeanRule line on its right edge separates it from the content, and the content just right of the line keeps LeanGround
    // #0b0b0c. Not under High Contrast (the system colours apply then). Sampled as ThemeTests does.
    [TestMethod]
    [TestCategory("Console")]
    public void TheSidebarIsLeanSidebarWithALeanRuleLineAndTheContentStaysLeanGround()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the system colours apply, so the brand colours are not checked");
        }

        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        var scanBox = AppSession.Find(window, "Scan", Ui.ElementTimeout).Current.BoundingRectangle;
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, scanBox);
        var scale = WindowOnScreen.Scale(handle);
        int Px(double dips) => (int)Math.Round(dips * scale);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"capture {capture.Width}x{capture.Height}, saved {capture.Save(nameof(TheSidebarIsLeanSidebarWithALeanRuleLineAndTheContentStaysLeanGround))}");
        var box = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        var (left, right) = ((int)Math.Round(box.Left), (int)Math.Round(box.Right));
        var (bandTop, bandBottom) = ((int)Math.Round(history.Bottom) + Px(4), (int)Math.Round(settings.Top) - Px(4));
        Assert.IsTrue(bandBottom - bandTop >= Px(10), $"precondition: no empty band between History ({history}) and Settings ({settings})");
        var problems = new List<string>();

        var sidebar = capture.Area(left + Px(8), bandTop, right - Px(8), bandBottom);
        var tone = ScreenCapture.Share(sidebar, Brand.Sidebar, Brand.NeutralTolerance);
        if (tone < 0.95)
        {
            problems.Add($"the sidebar's empty band is {Brand.Sidebar} (LeanSidebar) in only {tone:P0}; commonest {ScreenCapture.Commonest(sidebar)}");
        }

        var rule = Enumerable.Range(-2, 5).Select(k => capture.Area(right - 1 + k, bandTop, right + k, bandBottom)).MaxBy(l => ScreenCapture.Share(l, Brand.Rule, Brand.NeutralTolerance))!;
        var ruleShare = ScreenCapture.Share(rule, Brand.Rule, Brand.NeutralTolerance);
        if (ruleShare < 0.8)
        {
            problems.Add($"no column at the sidebar's right edge ({right}) is {Brand.Rule} (LeanRule): best {ruleShare:P0}; commonest {ScreenCapture.Commonest(rule)}");
        }

        var content = capture.Area(right + Px(6), bandTop, right + Px(30), bandBottom);
        var ground = ScreenCapture.Share(content, Brand.Ground, Brand.NeutralTolerance);
        if (ground < 0.95)
        {
            problems.Add($"the content right of the rule is {Brand.Ground} (LeanGround) in only {ground:P0}; commonest {ScreenCapture.Commonest(content)}");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }
}
