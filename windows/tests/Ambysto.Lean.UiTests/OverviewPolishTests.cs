using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-18 UI contract (intent/2026-10-20-asp-18-overview-polish.md), written before the code.
/// The top band (<c>VolumeCard</c>: <c>UsedByThisVolume</c>, <c>FreeRightNow</c>, <c>InTheRecycleBin</c>, <c>OpenRecycleBin</c>) has a
/// thin divider in the gap between its items, none at the start or end of a line when it wraps, and the dividers are decoration
/// (not UI Automation content, not a tab stop, nothing added to the readouts' names). The dividers carry no AutomationId, so they
/// are found as the contract draws them: from the band's item rectangles (settled) and the screen's pixels. The drive cards
/// (<c>DriveCard</c>) are tick boxes: one control with <c>TogglePattern</c>, ticked at start on the profile drive only, a click or
/// Space only toggles (no scan), Scan scans the ticked drive and is disabled with none ticked. The new line is read from
/// <c>Copy.Text</c> at run time (<see cref="Ui.Copy"/>), so a missing member fails only its test. The only real input is the left
/// button on the cards and Space; nothing is moved, revealed or opened. A scan of the whole profile drive is started once and
/// cancelled at once.</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class OverviewPolishTests
{
    private const long KB = ByteUnits.KB;
    private const string Dash = "—";
    private static readonly int[] SystemColourIndexes = [1, 4, 5, 8, 13, 14, 15, 17, 18, 26]; // desktop, menu, window, text, highlight, highlight text, button face, gray text, button text, hot light
    private static readonly int[] LineColourIndexes = [8, 13, 17, 18, 26];
    private static readonly string[] BandIds = ["UsedByThisVolume", "FreeRightNow", "InTheRecycleBin", "OpenRecycleBin"];

    // ---- helpers: window, cards -------------------------------------------------------------------------------------------

    private static AutomationElementHandle Handle(AppSession app) => new(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle));

    private static double Scale(AppSession app) => WindowOnScreen.Scale(Handle(app));

    private static int Px(AppSession app, double dips) => (int)Math.Round(dips * Scale(app));

    private static List<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The start screen's drive cards, in tree order, once there is one per fixed drive.</summary>
    private static List<AutomationElement> Cards(AppSession app)
    {
        var cards = AppSession.Find(app.Window(Ui.WindowTimeout), "DriveCards", Ui.ElementTimeout);
        List<AutomationElement> found = [];
        AppSession.WaitUntil(() => (found = AppSession.FindAll(cards, "DriveCard")).Count >= FixedDrives().Count, Ui.ElementTimeout);
        return found;
    }

    private static bool IsProfile(AutomationElement card) => card.Current.Name.StartsWith(Ui.ProfileDrive(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile drive's card, found again (the cards may be built again).</summary>
    private static AutomationElement ProfileCard(AppSession app) => Cards(app).Single(IsProfile);

    private static List<AutomationElement> OtherCards(AppSession app) => Cards(app).Where(c => !IsProfile(c)).ToList();

    private static bool Carries(AutomationElement element, string text) =>
        Ui.AnyNameContains(element, text) || (element.Current.HelpText ?? string.Empty).Contains(text, StringComparison.Ordinal);

    /// <summary>The card's toggle state; fails when the card has no TogglePattern (the contract: a tick box).</summary>
    private static ToggleState StateOf(AutomationElement card)
    {
        Assert.IsTrue(card.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern), "the card has no TogglePattern: " + Ui.Describe(card));
        return ((TogglePattern)pattern).Current.ToggleState;
    }

    private static void WaitForState(AppSession app, ToggleState expected, string what) =>
        Assert.IsTrue(AppSession.WaitUntil(() => StateOf(ProfileCard(app)) == expected, TimeSpan.FromSeconds(5)),
            $"{what}: the profile card is {StateOf(ProfileCard(app))}, not {expected}");

    /// <summary>Sets the tick through UI Automation (no pointer, no focus change).</summary>
    private static void SetTick(AppSession app, bool ticked)
    {
        var card = ProfileCard(app);
        Assert.IsTrue(card.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern), "the card has no TogglePattern: " + Ui.Describe(card));
        if ((StateOf(card) == ToggleState.On) != ticked)
        {
            ((TogglePattern)pattern).Toggle();
        }

        WaitForState(app, ticked ? ToggleState.On : ToggleState.Off, "after setting the tick through UI Automation");
    }

    private static void ClickCard(AppSession app, AutomationElement card)
    {
        var r = card.Current.BoundingRectangle;
        WindowOnScreen.Settle(Handle(app), AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
        Mouse.LeftClickAt(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
    }

    // ---- helpers: no scan, a scan -------------------------------------------------------------------------------------------

    private static readonly Regex ScanningLineFormat = new(@"^Scanning .+ " + Dash + @" [0-9][0-9,.   ]* files?, [0-9]", RegexOptions.CultureInvariant);

    /// <summary>What shows that a scan started, or null: Cancel enabled, a tile, any text in the progress line, or the Scan button
    /// gone or renamed (it reads Text.Scan until a scan runs).</summary>
    private static string? ScanSigns(AppSession app)
    {
        try
        {
            var window = app.Window(Ui.WindowTimeout);
            if (Ui.ScanRunning(app))
            {
                return "Cancel is enabled";
            }

            if (AppSession.TryFind(window, "Map") is { } map && AppSession.FindAll(map, "Tile").Count > 0)
            {
                return "the map has tiles";
            }

            if (AppSession.TryFind(window, "ScanProgress") is { } line && !line.Current.IsOffscreen && Ui.Names(line).Any(n => n.Length > 0))
            {
                return "the progress line shows " + Ui.Dump(line);
            }

            var scan = AppSession.TryFind(window, "Scan");
            if (scan is null)
            {
                return "the Scan button is gone";
            }

            return scan.Current.Name == Text.Scan ? null : $"the Scan button reads \"{scan.Current.Name}\"";
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Fails if, within <paramref name="watch"/>, anything shows that a scan started.</summary>
    private static void AssertNothingStarts(AppSession app, string after, TimeSpan? watch = null)
    {
        var clock = Stopwatch.StartNew();
        var span = watch ?? Ui.NeverWatch;
        do
        {
            var signs = ScanSigns(app);
            Assert.IsNull(signs, $"{after}, a scan started: {signs}");
            System.Threading.Thread.Sleep(100);
        }
        while (clock.Elapsed < span);
    }

    /// <summary>Presses Scan, waits for the progress line of a scan of the profile drive ("Scanning C: — …"), then cancels.</summary>
    private static void AssertScanOfTheProfileDriveStarts(AppSession app, string what)
    {
        var drive = Ui.ProfileDrive();
        var scan = AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout);
        Assert.IsTrue(scan.Current.IsEnabled, what + ": Scan is disabled");
        Ui.Click(scan);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), Ui.ElementTimeout), what + ": no scan started (Cancel stays disabled)");
        string? line = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (line = ProgressNames(app).FirstOrDefault(n => ScanningLineFormat.IsMatch(n))) is not null, TimeSpan.FromSeconds(20)),
            $"{what}: no Scanning line in the progress line: {Ui.Dump(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanProgress", Ui.ElementTimeout))}");
        Assert.IsTrue(line!.StartsWith("Scanning " + drive + " " + Dash, StringComparison.Ordinal), $"{what}: the scan is of \"{line}\", not of the profile drive {drive}");
        if (AppSession.TryFind(app.Window(Ui.WindowTimeout), "Cancel") is { } cancel && cancel.Current.IsEnabled)
        {
            Ui.Click(cancel);
        }

        AppSession.WaitUntil(() => !Ui.ScanRunning(app), TimeSpan.FromSeconds(30));
    }

    private static List<string> ProgressNames(AppSession app)
    {
        try
        {
            return AppSession.TryFind(app.Window(Ui.WindowTimeout), "ScanProgress") is { } line && !line.Current.IsOffscreen ? Ui.Names(line) : [];
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>The start screen's line under the Scan circle when no drive is ticked: Text.TickADriveOrScanAFolder, with the link
    /// words at its end (the member may hold the whole line or only "Tick a drive to scan, or ").</summary>
    private static string TickLine()
    {
        var text = Ui.Copy("TickADriveOrScanAFolder");
        return text.EndsWith(Text.ScanFolderLink, StringComparison.Ordinal) ? text : text.TrimEnd() + " " + Text.ScanFolderLink;
    }

    private static bool ShowsLine(AppSession app, string line)
    {
        var names = Ui.Names(app.Window(Ui.WindowTimeout));
        return names.Any(n => n.Contains(line, StringComparison.Ordinal)) || string.Concat(names).Contains(line, StringComparison.Ordinal)
            || string.Join(" ", names).Contains(line, StringComparison.Ordinal);
    }

    // ---- helpers: the top band --------------------------------------------------------------------------------------------

    private sealed record BandItem(string Id, Rect Rect);

    private static AutomationElement BandCard(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout);

    private static List<BandItem> Band(AppSession app)
    {
        var card = BandCard(app);
        return BandIds.Select(id => new BandItem(id, AppSession.Find(card, id, Ui.ElementTimeout).Current.BoundingRectangle)).ToList();
    }

    /// <summary>The band's items once their rectangles are the same in two samples 150 ms apart (the layout settled).</summary>
    private static List<BandItem> SettledBand(AppSession app)
    {
        string Key(List<BandItem> items) => string.Join(";", items.Select(i => i.Rect.ToString(CultureInfo.InvariantCulture)));
        var last = Band(app);
        var settled = AppSession.WaitUntil(() =>
        {
            System.Threading.Thread.Sleep(150);
            var now = Band(app);
            var same = Key(now) == Key(last) && now.All(i => !i.Rect.IsEmpty);
            last = now;
            return same;
        }, Ui.ElementTimeout);
        Assert.IsTrue(settled, "the top band never stopped changing: " + string.Join("; ", last.Select(i => i.Id + " " + i.Rect)));
        return last;
    }

    private static bool SameLine(Rect a, Rect b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= 0.5 * Math.Min(a.Height, b.Height);

    private static List<List<BandItem>> Lines(List<BandItem> items)
    {
        var lines = new List<List<BandItem>>();
        foreach (var item in items)
        {
            var line = lines.FirstOrDefault(l => SameLine(l[0].Rect, item.Rect));
            if (line is null)
            {
                lines.Add([item]);
            }
            else
            {
                line.Add(item);
            }
        }

        return lines;
    }

    /// <summary>One vertical mark on the screen: columns of a colour that are next to each other, with the longest vertical run.</summary>
    private sealed record Mark(double Centre, int Width, int Top, int Length);

    /// <summary>The marks in the columns x0 &lt;= x &lt; x1, rows y0 &lt;= y &lt; y1: a column counts when it holds a vertical run of at
    /// least <paramref name="minRun"/> pixels near <paramref name="colour"/>.</summary>
    private static List<Mark> Marks(ScreenCapture capture, int x0, int x1, int y0, int y1, Rgb colour, int minRun)
    {
        var columns = new List<(int X, int Top, int Length)>();
        for (var x = x0; x < x1; x++)
        {
            int best = 0, bestTop = 0, current = 0, currentTop = 0;
            for (var y = y0; y < y1; y++)
            {
                if (capture.Contains(x, y) && capture.At(x, y).Near(colour, 8))
                {
                    if (current == 0)
                    {
                        currentTop = y;
                    }

                    current++;
                    if (current > best)
                    {
                        best = current;
                        bestTop = currentTop;
                    }
                }
                else
                {
                    current = 0;
                }
            }

            if (best >= minRun)
            {
                columns.Add((x, bestTop, best));
            }
        }

        var marks = new List<Mark>();
        var cluster = new List<(int X, int Top, int Length)>();
        foreach (var column in columns)
        {
            if (cluster.Count > 0 && column.X != cluster[^1].X + 1)
            {
                marks.Add(MarkOf(cluster));
                cluster.Clear();
            }

            cluster.Add(column);
        }

        if (cluster.Count > 0)
        {
            marks.Add(MarkOf(cluster));
        }

        return marks;
    }

    private static Mark MarkOf(List<(int X, int Top, int Length)> cluster)
    {
        var longest = cluster.OrderByDescending(c => c.Length).First();
        return new Mark(cluster.Average(c => c.X) + 0.5, cluster.Count, longest.Top, longest.Length);
    }

    /// <summary>The contract's dividers, checked on the screen: in every gap between two items of a line exactly one mark of
    /// <paramref name="colour"/>, 1 px wide, about 16 px tall, centred in the gap and on the text; none before the first item of a
    /// line and none after the last (a line that starts or ends the band's wrapped layout).</summary>
    private static List<string> DividerProblems(ScreenCapture capture, AutomationElement card, List<BandItem> band, double scale, Rgb colour, string label)
    {
        int Px(double dips) => (int)Math.Round(dips * scale);
        var cardRect = card.Current.BoundingRectangle;
        var lines = Lines(band);
        var problems = new List<string>();
        var found = 0;
        foreach (var line in lines)
        {
            var items = line.OrderBy(i => i.Rect.Left).ToList();
            var top = (int)Math.Floor(items.Min(i => i.Rect.Top)) - Px(2);
            var bottom = (int)Math.Ceiling(items.Max(i => i.Rect.Bottom)) + Px(2);
            var (first, last) = (items[0], items[^1]);
            foreach (var m in Marks(capture, (int)cardRect.Left + Px(3), (int)first.Rect.Left - 1, top, bottom, colour, Px(8)))
            {
                problems.Add($"{label}: a divider at x {m.Centre:F1} before {first.Id}, which starts a line");
            }

            foreach (var m in Marks(capture, (int)Math.Ceiling(last.Rect.Right) + 1, (int)cardRect.Right - Px(3), top, bottom, colour, Px(8)))
            {
                problems.Add($"{label}: a divider at x {m.Centre:F1} after {last.Id}, which ends a line");
            }

            for (var i = 0; i < items.Count - 1; i++)
            {
                var (a, b) = (items[i], items[i + 1]);
                var gap = Marks(capture, (int)Math.Ceiling(a.Rect.Right) + Px(2), (int)b.Rect.Left - Px(2), top, bottom, colour, Px(10));
                if (gap.Count != 1)
                {
                    problems.Add($"{label}: {gap.Count} dividers between {a.Id} and {b.Id} (gap {a.Rect.Right:F0}..{b.Rect.Left:F0}), not one");
                    continue;
                }

                found++;
                var m = gap[0];
                var middle = (a.Rect.Right + b.Rect.Left) / 2;
                var textMiddle = a.Rect.Top + (a.Rect.Height / 2);
                if (m.Width > Px(2) + 1)
                {
                    problems.Add($"{label}: the divider between {a.Id} and {b.Id} is {m.Width} px wide, not 2");
                }

                if (m.Length < Px(12) || m.Length > Px(26))
                {
                    problems.Add($"{label}: the divider between {a.Id} and {b.Id} is {m.Length} px tall, not about {Px(22)}");
                }

                if (Math.Abs(m.Centre - middle) > Px(3))
                {
                    problems.Add($"{label}: the divider between {a.Id} and {b.Id} is at x {m.Centre:F1}, not centred in the gap ({middle:F1})");
                }

                if (Math.Abs(m.Top + (m.Length / 2.0) - textMiddle) > Px(5))
                {
                    problems.Add($"{label}: the divider between {a.Id} and {b.Id} is centred at y {m.Top + (m.Length / 2.0):F1}, not on the text ({textMiddle:F1})");
                }
            }
        }

        var expected = band.Count - lines.Count;
        if (found != expected && problems.Count == 0)
        {
            problems.Add($"{label}: {found} dividers, expected {expected} ({band.Count} items on {lines.Count} line(s))");
        }

        return problems;
    }

    /// <summary>Parks the cursor, captures the window and checks the dividers of the band as it stands.</summary>
    private static List<string> CheckBand(AppSession app, Rgb colour, string label, string saveAs)
    {
        var band = SettledBand(app);
        var card = BandCard(app);
        var handle = Handle(app);
        WindowOnScreen.Settle(handle, card.Current.BoundingRectangle);
        band = SettledBand(app);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{label}: saved {capture.Save(saveAs)}; " + string.Join(", ", band.Select(i => $"{i.Id} {i.Rect}")));
        return DividerProblems(capture, card, band, Scale(app), colour, label);
    }

    // ---- the dividers ---------------------------------------------------------------------------------------------------------

    // Contract "Top band": a 1 px divider, 16 px tall, in LeanRule, centred in the gap between UsedByThisVolume, FreeRightNow,
    // InTheRecycleBin and OpenRecycleBin: three between four items at the default size, none before the first or after the last.
    [TestMethod]
    [TestCategory("Console")]
    public void TheTopBandHasThreeDividersBetweenFourItemsAtTheDefaultSize()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the dividers take a system colour (their own test)");
        }

        using var app = AppSession.Start();
        var band = SettledBand(app);
        Assert.AreEqual(1, Lines(band).Count, "precondition: the four items are not on one line at the default size: " + string.Join("; ", band.Select(i => i.Id + " " + i.Rect)));
        var problems = CheckBand(app, Brand.Divider, "default size", nameof(TheTopBandHasThreeDividersBetweenFourItemsAtTheDefaultSize));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "Top band": where the band wraps at a narrow window, an item that starts a new line has no divider before it, and no
    // divider is ever at the start or the end of a line. The window is made narrower in steps (TransformPattern) until the band has
    // wrapped, each wrapped layout is checked once it has settled, and the window is put back at the end.
    [TestMethod]
    [TestCategory("Console")]
    public void WhenTheBandWrapsNoDividerIsAtTheStartOrTheEndOfALine()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the dividers take a system colour (their own test)");
        }

        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || !((TransformPattern)pattern).Current.CanResize)
        {
            Assert.Inconclusive("the window cannot be resized through UI Automation: the wrapped layouts were not checked");
        }

        var transform = (TransformPattern)pattern;
        SettledBand(app);
        var original = window.Current.BoundingRectangle;
        var problems = new List<string>();
        var layouts = new List<string>();
        try
        {
            var width = original.Width;
            for (var step = 0; step < 14; step++)
            {
                width -= Px(app, 90);
                transform.Resize(width, original.Height);
                System.Threading.Thread.Sleep(800);
                var actual = app.Window(Ui.WindowTimeout).Current.BoundingRectangle.Width;
                var lines = Lines(SettledBand(app)).Count;
                if (lines >= 2)
                {
                    var label = $"{lines} lines at {actual:F0} px";
                    layouts.Add(label);
                    problems.AddRange(CheckBand(app, Brand.Divider, label, nameof(WhenTheBandWrapsNoDividerIsAtTheStartOrTheEndOfALine) + "-" + lines + "-lines-" + step));
                }

                if (lines >= BandIds.Length || actual > width + 4)
                {
                    break; // every item on its own line, or the window will not get narrower
                }
            }
        }
        finally
        {
            transform.Resize(original.Width, original.Height);
            System.Threading.Thread.Sleep(800);
        }

        var after = app.Window(Ui.WindowTimeout).Current.BoundingRectangle;
        Assert.IsTrue(Math.Abs(after.Width - original.Width) <= 4 && Math.Abs(after.Height - original.Height) <= 4,
            $"the window was not put back: {original.Width} x {original.Height} before, {after.Width} x {after.Height} after");
        if (layouts.Count == 0)
        {
            Assert.Inconclusive("the window could not be made narrow enough for the band to wrap: the wrapped layouts were not checked");
        }

        Console.WriteLine("wrapped layouts checked: " + string.Join(", ", layouts));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "Top band": the dividers are decoration: AccessibilityView.Raw, so no UI Automation content element stands for one
    // (every content element in the band is a readout or the button), and they add nothing to the readouts' names ("<label>: <size>").
    [TestMethod]
    [TestCategory("Console")]
    public void TheDividersAreNotUiAutomationContentAndAddNothingToTheReadoutsNames()
    {
        using var app = AppSession.Start();
        SettledBand(app);
        var card = BandCard(app);
        var names = BandIds.ToDictionary(id => id, id => AppSession.Find(card, id, Ui.ElementTimeout).Current.Name);
        var allowed = names.Values.Append(Text.OpenRecycleBin).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (AutomationElement element in card.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        {
            var current = element.Current;
            if (current.ControlType == ControlType.Separator)
            {
                problems.Add("a Separator in the band: " + Ui.Describe(element));
            }

            if ((current.IsContentElement || current.IsControlElement) && !allowed.Contains(current.Name) && current.ControlType != ControlType.Group && current.ControlType != ControlType.Pane)
            {
                problems.Add("a control or content element in the band that is no readout and not the button: " + Ui.Describe(element));
            }
        }

        foreach (var (id, label) in new[] { ("UsedByThisVolume", Text.UsedByThisVolume), ("FreeRightNow", Text.FreeRightNow), ("InTheRecycleBin", Text.InTheRecycleBin) })
        {
            var name = names[id];
            var pattern = "^" + Regex.Escape(label) + @": ([0-9][0-9,.   ]* (bytes|KB|MB|GB|TB|PB)|" + Regex.Escape(Text.Unknown) + ")$";
            if (!Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant))
            {
                problems.Add($"{id} is named \"{name}\": more than \"{label}: <size>\"");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "Top band": the dividers are never a tab stop. Nothing in the band but OpenRecycleBin can take the keyboard focus,
    // and Tab (and Shift+Tab) from that button leaves the band.
    [TestMethod]
    [TestCategory("Console")]
    public void NoDividerIsATabStop()
    {
        using var app = AppSession.Start();
        SettledBand(app);
        var card = BandCard(app);
        var focusable = new List<string>();
        foreach (AutomationElement element in card.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        {
            if (element.Current.IsKeyboardFocusable && element.Current.AutomationId != "OpenRecycleBin")
            {
                focusable.Add(Ui.Describe(element));
            }
        }

        Assert.AreEqual(0, focusable.Count, "elements in the band that can take the keyboard focus, besides OpenRecycleBin: " + string.Join(" | ", focusable));

        var open = AppSession.Find(card, "OpenRecycleBin", Ui.ElementTimeout);
        Ui.Focus(open);
        Keys.Press(Keys.Tab);
        var next = AutomationElement.FocusedElement;
        Assert.IsFalse(Ui.IsWithin(next, card), "Tab from OpenRecycleBin stays in the band: " + Ui.Describe(next));
        Ui.Focus(open);
        Keys.PressWithShift(Keys.Tab);
        var previous = AutomationElement.FocusedElement;
        Assert.IsFalse(Ui.IsWithin(previous, card), "Shift+Tab from OpenRecycleBin stays in the band: " + Ui.Describe(previous));
    }

    // Contract "Top band" and "High Contrast": the dividers use the system colour for disabled text (GrayText) in High Contrast.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheDividersAreInTheSystemDisabledTextColour()
    {
        using var contrast = new HighContrastScope();
        using var app = AppSession.Start();
        var grayText = HighContrastScope.System(17); // COLOR_GRAYTEXT
        var problems = CheckBand(app, grayText, "high contrast, disabled text " + grayText, nameof(UnderHighContrastTheDividersAreInTheSystemDisabledTextColour));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // ---- the drive cards: one tick box each ----------------------------------------------------------------------------------

    // Contract "Drive cards": the profile drive's card is enabled and ticked at start, it is one control with TogglePattern named
    // for the drive (as today, "<letter>: <label>"), and it is one tab stop: nothing inside it takes the keyboard focus on its own.
    [TestMethod]
    [TestCategory("Console")]
    public void TheProfileDrivesCardIsOneTickedEnabledTickBoxAtStart()
    {
        using var app = AppSession.Start();
        var profile = Ui.ProfileDrive();
        var drive = FixedDrives().Single(d => d.Name.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
        var card = ProfileCard(app);
        var expectedName = profile + " " + (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Text.LocalDisk : drive.VolumeLabel);
        Assert.AreEqual(expectedName, card.Current.Name.TrimEnd(), "the card's name is the drive name");
        Assert.IsTrue(card.Current.IsEnabled, "the profile drive's card is disabled at start: " + Ui.Describe(card));
        Assert.AreEqual(ToggleState.On, StateOf(card), "the profile drive's card is not ticked at start");
        Assert.IsTrue(card.Current.IsKeyboardFocusable, "the card cannot take the keyboard focus (no tab stop): " + Ui.Describe(card));
        var inside = new List<string>();
        foreach (AutomationElement element in card.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        {
            if (element.Current.IsKeyboardFocusable)
            {
                inside.Add(Ui.Describe(element));
            }
        }

        Assert.AreEqual(0, inside.Count, "elements inside the card that take the keyboard focus (more than one tab stop): " + string.Join(" | ", inside));
        Assert.IsFalse(card.TryGetCurrentPattern(InvokePattern.Pattern, out _), "the card still offers Invoke (a click that does something other than tick): " + Ui.Describe(card));
    }

    // Contract "Drive cards": every other card is disabled and unticked, keeps Text.OtherDrivesAfterV1 (and not the profile's
    // note), still has a toggle, and neither a real click nor Toggle ticks it or starts a scan.
    [TestMethod]
    [TestCategory("Console")]
    public void TheOtherCardsAreDisabledUntickedAndComeAfterV1()
    {
        if (FixedDrives().Count < 2)
        {
            Assert.Inconclusive("this machine has one fixed drive: there is no other drive's card to check");
        }

        using var app = AppSession.Start();
        var afterV1 = Ui.Copy("OtherDrivesAfterV1");
        var scanned = Ui.Copy("ThisDriveIsScanned");
        var problems = new List<string>();
        foreach (var card in OtherCards(app))
        {
            var name = card.Current.Name;
            if (card.Current.IsEnabled)
            {
                problems.Add($"the card \"{name}\" is enabled");
            }

            if (!card.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
            {
                problems.Add($"the card \"{name}\" has no TogglePattern");
                continue;
            }

            if (((TogglePattern)pattern).Current.ToggleState != ToggleState.Off)
            {
                problems.Add($"the card \"{name}\" is ticked");
            }

            if (!Carries(card, afterV1) || Carries(card, scanned))
            {
                problems.Add($"the card \"{name}\" says \"{afterV1}\" {Carries(card, afterV1)}, says \"{scanned}\" {Carries(card, scanned)}: {Ui.Dump(card)}");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));

        foreach (var card in OtherCards(app))
        {
            var name = card.Current.Name;
            ClickCard(app, card);
            AssertNothingStarts(app, $"a left click on the disabled card \"{name}\"", TimeSpan.FromSeconds(1.5));
            Assert.AreEqual(ToggleState.Off, StateOf(OtherCards(app).Single(c => c.Current.Name == name)), $"a left click ticked the disabled card \"{name}\"");
            try
            {
                ((TogglePattern)card.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            }
            catch (ElementNotEnabledException)
            {
                // A disabled card refuses Toggle: what the contract wants.
            }

            Assert.AreEqual(ToggleState.Off, StateOf(OtherCards(app).Single(c => c.Current.Name == name)), $"Toggle ticked the disabled card \"{name}\"");
            AssertNothingStarts(app, $"Toggle on the disabled card \"{name}\"", TimeSpan.FromSeconds(1.5));
        }
    }

    // Contract "Drive cards": the card is one tab stop. Tab from the profile card leaves it (nothing inside takes the focus) and
    // Shift+Tab comes back to it.
    [TestMethod]
    [TestCategory("Console")]
    public void TabLeavesTheCardInOneStopAndShiftTabComesBack()
    {
        using var app = AppSession.Start();
        var card = ProfileCard(app);
        Ui.Focus(card);
        Keys.Press(Keys.Tab);
        var next = AutomationElement.FocusedElement;
        Assert.IsFalse(Ui.IsWithin(next, card), "Tab from the card stays in the card (more than one tab stop): " + Ui.Describe(next));
        Keys.PressWithShift(Keys.Tab);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(ProfileCard(app)), TimeSpan.FromSeconds(3)),
            "Shift+Tab does not come back to the card: " + Ui.Describe(AutomationElement.FocusedElement));
    }

    // Contract "Card click changes": from this ticket a click or Space on a card only ticks or unticks it, and no scan starts.
    [TestMethod]
    [TestCategory("Console")]
    public void AClickAndSpaceToggleTheProfileCardAndStartNoScan()
    {
        using var app = AppSession.Start();
        Assert.AreEqual(ToggleState.On, StateOf(ProfileCard(app)), "precondition: the profile card is not ticked at start");

        ClickCard(app, ProfileCard(app));
        WaitForState(app, ToggleState.Off, "after a click on the ticked card");
        AssertNothingStarts(app, "after a click that unticked the card");

        ClickCard(app, ProfileCard(app));
        WaitForState(app, ToggleState.On, "after a click on the unticked card");
        AssertNothingStarts(app, "after a click that ticked the card");

        Ui.Focus(ProfileCard(app));
        Keys.Press(Keys.Space);
        WaitForState(app, ToggleState.Off, "after Space on the ticked card");
        AssertNothingStarts(app, "after Space that unticked the card");

        Keys.Press(Keys.Space);
        WaitForState(app, ToggleState.On, "after Space on the unticked card");
        AssertNothingStarts(app, "after Space that ticked the card");
    }

    // Contract "The Scan button": with no drive ticked it is disabled and the line under the circle reads Text.TickADriveOrScanAFolder
    // instead of Text.ScanDriveOrFolder; ticking the card again restores both. "Scan a folder" stays on.
    [TestMethod]
    [TestCategory("Console")]
    public void WithNoDriveTickedScanIsDisabledAndTheLineAsksToTickADrive()
    {
        using var app = AppSession.Start();
        var drive = Ui.ProfileDrive();
        var driveLine = Text.ScanDriveOrFolder(drive);
        var tickLine = TickLine();
        var scan = AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout);
        Assert.IsTrue(scan.Current.IsEnabled && ShowsLine(app, driveLine) && !ShowsLine(app, tickLine),
            $"precondition: Scan enabled {scan.Current.IsEnabled}, the line \"{driveLine}\" shown {ShowsLine(app, driveLine)}, \"{tickLine}\" shown {ShowsLine(app, tickLine)}");

        SetTick(app, false);
        Assert.IsTrue(AppSession.WaitUntil(() => !AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout).Current.IsEnabled, TimeSpan.FromSeconds(5)),
            "Scan stays enabled with no drive ticked");
        Assert.IsTrue(AppSession.WaitUntil(() => ShowsLine(app, tickLine), TimeSpan.FromSeconds(5)),
            $"the line under the circle does not read \"{tickLine}\" with no drive ticked: {Ui.Dump(app.Window(Ui.WindowTimeout))}");
        Assert.IsFalse(ShowsLine(app, driveLine), $"the old line \"{driveLine}\" is still shown with no drive ticked");
        Assert.AreEqual(Text.Scan, AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout).Current.Name, "Scan's label");
        var link = AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout);
        Assert.IsTrue(link.Current.IsEnabled && link.TryGetCurrentPattern(InvokePattern.Pattern, out _), "\"Scan a folder\" is not an enabled link with no drive ticked: " + Ui.Describe(link));
        AssertNothingStarts(app, "after unticking the card");

        SetTick(app, true);
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout).Current.IsEnabled, TimeSpan.FromSeconds(5)),
            "Scan stays disabled after the card is ticked again");
        Assert.IsTrue(AppSession.WaitUntil(() => ShowsLine(app, driveLine) && !ShowsLine(app, tickLine), TimeSpan.FromSeconds(5)),
            $"after ticking again the line is not \"{driveLine}\" alone: {Ui.Dump(app.Window(Ui.WindowTimeout))}");
    }

    // Contract "The Scan button": "Scan a folder" works whatever is ticked: with the profile card unticked, the folder picker still
    // scans a folder on the profile drive.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanFolderStillWorksWithNoDriveTicked()
    {
        using var fixture = new Fixture("ui-polish", Fixture.ProfileBaseFolder());
        fixture.File(@"alpha\one.bin", 300 * KB);
        fixture.File(@"beta\one.bin", 100 * KB);
        using var app = AppSession.Start();
        try
        {
            SetTick(app, false);
            Ui.Click(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout));
            FolderPicker.Choose(app, fixture.Path, Ui.ElementTimeout);
            var map = Ui.WaitForScan(app);
            foreach (var name in new[] { "alpha", "beta" })
            {
                Assert.IsTrue(Ui.HasTile(map, name), $"no tile for {name} after scanning the chosen folder with no drive ticked: [{string.Join(" | ", Ui.TileNames(map))}]");
            }
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // Contract "The Scan button": with the card ticked (as at start) Scan scans the profile drive, as before.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanWithTheCardTickedScansTheProfileDrive()
    {
        using var app = AppSession.Start();
        Assert.AreEqual(ToggleState.On, StateOf(ProfileCard(app)), "precondition: the profile card is not ticked at start");
        AssertScanOfTheProfileDriveStarts(app, "Scan with the profile card ticked");
    }

    // ...and after the card was unticked and ticked again.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanAfterUntickingAndTickingAgainScansTheProfileDrive()
    {
        using var app = AppSession.Start();
        SetTick(app, false);
        SetTick(app, true);
        AssertScanOfTheProfileDriveStarts(app, "Scan after unticking and ticking the profile card again");
    }

    // Contract "The profile drive's card": ticked at start, every launch; the tick is not saved.
    [TestMethod]
    [TestCategory("Console")]
    public void TheTickIsNotSavedBetweenLaunches()
    {
        using (var first = AppSession.Start())
        {
            SetTick(first, false);
        }

        using var second = AppSession.Start();
        Assert.AreEqual(ToggleState.On, StateOf(ProfileCard(second)), "the profile card starts unticked after a launch that left it unticked (the tick was saved)");
        Assert.IsTrue(AppSession.Find(second.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout).Current.IsEnabled, "Scan is disabled after a launch that left the card unticked");
    }

    // Contract "Unchanged": the ASP-14 busy rules apply to the tick box as they applied to the card (off while a scan, a move or the
    // root check runs). The start screen is not shown while a scan runs, so what can be seen is that no enabled card is on screen
    // then, and that the tick cannot be changed through the tree. (The root check and a move are too short or too far from the start
    // screen to catch from here.)
    [TestMethod]
    [TestCategory("Console")]
    public void WhileAScanRunsNoCardCanBeToggled()
    {
        using var fixture = new Fixture("ui-polish-busy", Fixture.ProfileBaseFolder());
        var chain = "slow";
        for (var level = 0; level < 30; level++)
        {
            fixture.File(chain + @"\f.bin", 4 * KB);
            chain += @"\l" + level.ToString("D2", CultureInfo.InvariantCulture);
        }

        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(15)), "no scan runs");
        var offenders = new List<string>();
        for (var sample = 0; sample < 10 && Ui.ScanRunning(app); sample++)
        {
            foreach (var card in Ui.FindAllAnywhere(app, "DriveCard"))
            {
                try
                {
                    if (card.Current.IsEnabled && !card.Current.IsOffscreen && !card.Current.BoundingRectangle.IsEmpty)
                    {
                        offenders.Add(Ui.Describe(card));
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }

            System.Threading.Thread.Sleep(200);
        }

        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended before the cards were looked at");
        Assert.AreEqual(0, offenders.Count, "an enabled, visible card while a scan runs: " + string.Join(" | ", offenders.Distinct()));
    }

    // ---- what the tick looks like ------------------------------------------------------------------------------------------------

    /// <summary>The pixels that differ between two captures of the same rectangle, as screen coordinates.</summary>
    private static List<(int X, int Y)> Changed(ScreenCapture a, ScreenCapture b, Int32Rect area)
    {
        var changed = new List<(int X, int Y)>();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                if (a.Contains(x, y) && b.Contains(x, y))
                {
                    var (p, q) = (a.At(x, y), b.At(x, y));
                    if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 24)
                    {
                        changed.Add((x, y));
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>Captures the profile card, ticked and then unticked (through UI Automation, the pointer parked off the card).</summary>
    private static (ScreenCapture Ticked, ScreenCapture Unticked, Rect Card, Int32Rect Area) CaptureBothStates(AppSession app, string name)
    {
        var handle = Handle(app);
        var card = ProfileCard(app).Current.BoundingRectangle;
        var pad = Px(app, 48);
        var area = new Int32Rect((int)card.Left - pad, (int)card.Top - pad, (int)card.Width + (2 * pad), (int)card.Height + (2 * pad));
        var band = AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle;
        SetTick(app, true);
        WindowOnScreen.Settle(handle, band);
        var ticked = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{name} ticked: saved {ticked.Save(name + "-ticked")}; card {card}");
        SetTick(app, false);
        WindowOnScreen.Settle(handle, band);
        var unticked = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{name} unticked: saved {unticked.Save(name + "-unticked")}");
        return (ticked, unticked, card, area);
    }

    // Contract "Drive cards": the tick glyph is drawn in the card's top-right corner, in the card's own style, not a default check
    // box beside the card: between the ticked and the unticked card the pixels that change lie in the card's top-right corner
    // (at least a few of them), and nothing changes outside the card.
    [TestMethod]
    [TestCategory("Console")]
    public void TheTickGlyphIsDrawnInTheCardsTopRightCornerAndNotBesideTheCard()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the tick uses system colours (its own test)");
        }

        using var app = AppSession.Start();
        var (ticked, unticked, card, area) = CaptureBothStates(app, nameof(TheTickGlyphIsDrawnInTheCardsTopRightCornerAndNotBesideTheCard));
        var changed = Changed(ticked, unticked, area);
        var corner = Px(app, 48);
        var inCorner = changed.Count(p => p.X >= card.Right - corner && p.X < card.Right && p.Y >= card.Top && p.Y < card.Top + corner);
        // The line under the Scan circle and the circle itself change with the tick by design (ASP-18: "Scan C: or scan a folder" or "Tick a
        // drive to scan, or scan a folder"; Scan off with no drive ticked), and the descenders of that line lie at the very edge of the 48 DIP
        // square around the card. Only the rectangles of those two elements (from UI Automation, 2 px more all round) are left out; any
        // other pixel outside the card that changes still fails (a check box beside the card).
        var window = app.Window(Ui.WindowTimeout);
        var masks = new List<Rect>();
        if (AppSession.TryFind(window, "Scan") is { } circle)
        {
            masks.Add(circle.Current.BoundingRectangle);
        }

        if (AppSession.TryFind(window, "ScanFolder") is { } link && TreeWalker.ControlViewWalker.GetParent(link) is { } hint)
        {
            masks.Add(hint.Current.BoundingRectangle);
            masks.Add(link.Current.BoundingRectangle);
        }

        masks = [.. masks.Where(m => !m.IsEmpty).Select(m => new Rect(m.Left - 2, m.Top - 2, m.Width + 4, m.Height + 4))];
        var outside = changed.Where(p => (p.X < card.Left || p.X >= card.Right || p.Y < card.Top || p.Y >= card.Bottom) && !masks.Any(m => m.Contains(p.X, p.Y))).ToList();
        Assert.IsTrue(inCorner >= 6, $"only {inCorner} pixels change in the card's top-right corner ({corner} px square) between ticked and unticked; {changed.Count} change in all");
        Assert.AreEqual(0, outside.Count, $"{outside.Count} pixels outside the card change between ticked and unticked (a check box beside the card?): first at {(outside.Count > 0 ? outside[0].ToString() : string.Empty)}");
    }

    // Contract "High Contrast": the tick glyph and the box border use system colours. The glyph (the pixels that change when the card
    // is ticked) is a system colour, no Lean palette colour is in the corner, and the unticked box still shows a border in a system
    // colour.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheTickGlyphAndTheBoxBorderUseSystemColours()
    {
        using var contrast = new HighContrastScope();
        using var app = AppSession.Start();
        var (ticked, unticked, card, area) = CaptureBothStates(app, nameof(UnderHighContrastTheTickGlyphAndTheBoxBorderUseSystemColours));
        var corner = Px(app, 48);
        var region = new Int32Rect((int)card.Right - corner, (int)card.Top + Px(app, 3), corner - Px(app, 3), corner);
        var system = SystemColourIndexes.Select(HighContrastScope.System).ToList();
        var lines = LineColourIndexes.Select(HighContrastScope.System).ToList();
        var problems = new List<string>();

        var glyph = Changed(ticked, unticked, region);
        if (glyph.Count < 6)
        {
            problems.Add($"only {glyph.Count} pixels change in the card's top-right corner between ticked and unticked: no tick glyph");
        }
        else
        {
            var inSystem = glyph.Count(p => system.Any(s => ticked.At(p.X, p.Y).Near(s, 10)));
            if (inSystem < glyph.Count * 0.6)
            {
                problems.Add($"only {inSystem} of {glyph.Count} glyph pixels are a system colour [{string.Join(", ", system)}]; commonest {ScreenCapture.Commonest(glyph.Select(p => ticked.At(p.X, p.Y)).ToList())}");
            }
        }

        foreach (var (capture, state) in new[] { (ticked, "ticked"), (unticked, "unticked") })
        {
            var samples = capture.Area(region.X, region.Y, region.X + region.Width, region.Y + region.Height);
            var brand = samples.Count(p => p.Near(Brand.Ink, Brand.Tolerance) || p.Near(Brand.Coral, Brand.Tolerance) || p.Near(Brand.Mute, Brand.Tolerance) || p.Near(Brand.Dim, Brand.Tolerance));
            if (brand > 0)
            {
                problems.Add($"{brand} Lean palette pixels (ink, tone, mute or dim) in the {state} card's top-right corner under High Contrast");
            }
        }

        var border = unticked.Area(region.X, region.Y, region.X + region.Width, region.Y + region.Height).Count(p => lines.Any(s => p.Near(s, 10)));
        if (border < 8)
        {
            problems.Add($"only {border} pixels of a system line colour [{string.Join(", ", lines)}] in the unticked card's top-right corner: no box border");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }
}
