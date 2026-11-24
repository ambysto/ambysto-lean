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

/// <summary>ASP-18 review round 1, tests first: Scan again after a folder scan with C: unticked (the MAJOR), the card's own
/// focus ring, the other WrapPanels (Duplicates type tabs, map legend) laid out as before, Enter on a card, and the busy rule.
/// Same class as <c>OverviewPolishTests</c> (partial), so it uses its helpers.</summary>
public sealed partial class OverviewPolishTests
{
    // ---- 1. Scan again keeps its ASP-14 behaviour ----------------------------------------------------------------------------

    // Contract "The Scan button": Scan again, on the results view where the cards are not shown, keeps its ASP-14 behaviour and is
    // not disabled by the ticks. With C: unticked, a folder scan (Scan a folder works whatever is ticked), then Scan again: it is
    // enabled and starts a scan of the profile drive ("Scanning C: \u2014 ...").
    [TestMethod]
    [TestCategory("Console")]
    public void ScanAgainAfterAFolderScanWithTheCardUntickedScansTheProfileDrive()
    {
        using var fixture = new Fixture("ui-polish-again", Fixture.ProfileBaseFolder());
        fixture.File(@"alpha\one.bin", 300 * KB);
        fixture.File(@"beta\one.bin", 100 * KB);
        using var app = AppSession.Start();
        try
        {
            SetTick(app, false);
            Ui.Click(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout));
            FolderPicker.Choose(app, fixture.Path, Ui.ElementTimeout);
            Ui.WaitForScan(app);
            var again = AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout);
            Assert.AreEqual(Ui.Copy("ScanAgain"), again.Current.Name, "Scan's label after the folder scan");
            Assert.IsTrue(again.Current.IsEnabled, "Scan again is disabled after a folder scan with C: unticked");
            AssertScanOfTheProfileDriveStarts(app, "Scan again after a folder scan with C: unticked");
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // ---- 2. the focus ring ---------------------------------------------------------------------------------------------------

    /// <summary>Puts the keyboard focus on the profile card with Tab (from "Scan a folder"), captures the window, moves the focus
    /// on with another Tab and captures again. The pixels that change are the focus ring (and nothing else: the pointer is
    /// parked off the card).</summary>
    private static (List<(int X, int Y)> Changed, Rect Card, ScreenCapture Focused, ScreenCapture Away) FocusRing(AppSession app, string name)
    {
        var handle = Handle(app);
        WindowOnScreen.Settle(handle, BandCard(app).Current.BoundingRectangle);
        Ui.Focus(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout));
        for (var tabs = 0; tabs < 6 && !Ui.HasFocus(ProfileCard(app)); tabs++)
        {
            Keys.Press(Keys.Tab);
        }

        var card = ProfileCard(app);
        Assert.IsTrue(Ui.HasFocus(card), "Tab from \"Scan a folder\" never reached the profile card; focus is on " + Ui.Describe(AutomationElement.FocusedElement));
        System.Threading.Thread.Sleep(400);
        var rect = card.Current.BoundingRectangle;
        var focused = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{name} focused: saved {focused.Save(name + "-focused")}; card {rect}");
        Keys.Press(Keys.Tab);
        Assert.IsFalse(Ui.HasFocus(ProfileCard(app)), "a second Tab left the focus on the card");
        System.Threading.Thread.Sleep(400);
        var away = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{name} away: saved {away.Save(name + "-away")}");
        var pad = Px(app, 24);
        var area = new Int32Rect((int)rect.Left - pad, (int)rect.Top - pad, (int)rect.Width + (2 * pad), (int)rect.Height + (2 * pad));
        var changed = new List<(int X, int Y)>();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                if (focused.Contains(x, y) && away.Contains(x, y))
                {
                    var (p, q) = (focused.At(x, y), away.At(x, y));
                    if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) >= 150)
                    {
                        changed.Add((x, y));
                    }
                }
            }
        }

        return (changed, rect, focused, away);
    }

    private static List<string> FocusRingProblems(List<(int X, int Y)> changed, Rect card, int minPixels)
    {
        var problems = new List<string>();
        var inside = changed.Count(p => p.X >= card.Left && p.X < card.Right && p.Y >= card.Top && p.Y < card.Bottom);
        var outside = changed.Where(p => p.X < card.Left || p.X >= card.Right || p.Y < card.Top || p.Y >= card.Bottom).ToList();
        if (inside < minPixels)
        {
            problems.Add($"only {inside} high-contrast pixels change inside the card ({card}) when it takes the keyboard focus: no visible focus ring (need {minPixels})");
        }

        if (outside.Count > 0)
        {
            problems.Add($"{outside.Count} pixels outside the card change with its focus (the ring is drawn outside the card), first at {outside[0]}");
        }

        return problems;
    }

    // Contract "Drive cards": the card draws its own focus ring inside the card (the system ring of the re-templated check box drew
    // far outside it). With the focus on the profile card (Tab), pixels inside the card's rectangle change by a lot, and none
    // outside it.
    [TestMethod]
    [TestCategory("Console")]
    public void TheFocusedCardDrawsItsOwnVisibleRingInsideItsRectangle()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on (its own test)");
        }

        using var app = AppSession.Start();
        var (changed, card, _, _) = FocusRing(app, nameof(TheFocusedCardDrawsItsOwnVisibleRingInsideItsRectangle));
        var problems = FocusRingProblems(changed, card, (int)(card.Width / 2));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // ...and in High Contrast: the same, in system colours.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheFocusedCardsRingIsVisibleInsideItsRectangleInSystemColours()
    {
        using var contrast = new HighContrastScope();
        using var app = AppSession.Start();
        var (changed, card, focused, _) = FocusRing(app, nameof(UnderHighContrastTheFocusedCardsRingIsVisibleInsideItsRectangleInSystemColours));
        var problems = FocusRingProblems(changed, card, (int)(card.Width / 2));
        var system = SystemColourIndexes.Select(HighContrastScope.System).ToList();
        var inside = changed.Where(p => p.X >= card.Left && p.X < card.Right && p.Y >= card.Top && p.Y < card.Bottom).ToList();
        var inSystem = inside.Count(p => system.Any(s => focused.At(p.X, p.Y).Near(s, 10)));
        if (inside.Count > 0 && inSystem < inside.Count * 0.6)
        {
            problems.Add($"only {inSystem} of {inside.Count} ring pixels are a system colour [{string.Join(", ", system)}]; commonest {ScreenCapture.Commonest(inside.Select(p => focused.At(p.X, p.Y)).ToList())}");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // ---- 3. the other WrapPanels lay out as before ---------------------------------------------------------------------------------

    // The panel's rules before ASP-18 (266e95c): items left to right with HorizontalSpacing between them, top-aligned on a line, a new
    // line VerticalSpacing under the tallest item of the line before. The Duplicates type tabs (4 / 4) and the map legend (16 / 4) keep
    // them, and no divider element is among their items. Positions cannot be compared with 266e95c's from here (they depend on the
    // fonts, window and fixture), so the invariants are checked: the gaps between neighbours, a shared top per line, the line pitch.
    private static void AssertWrapRules(List<Rect> items, double horizontal, double vertical, double scale, string what, double stepExtra = 0)
    {
        int Px(double dips) => (int)Math.Round(dips * scale);
        var lines = new List<List<Rect>>();
        foreach (var item in items.OrderBy(i => i.Top).ThenBy(i => i.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Top - item.Top) <= 1.5);
            if (line is null)
            {
                lines.Add([item]);
            }
            else
            {
                line.Add(item);
            }
        }

        var problems = new List<string>();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(i => i.Left).ToList();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var gap = ordered[i + 1].Left - ordered[i].Right;
                if (Math.Abs(gap - Px(horizontal) - stepExtra) > 1.5)
                {
                    problems.Add($"{what}: the gap between neighbours {ordered[i]} and {ordered[i + 1]} is {gap:F1} px, not {Px(horizontal) + stepExtra} ({horizontal} DIP spacing)");
                }
            }
        }

        for (var i = 0; i < lines.Count - 1; i++)
        {
            var pitch = lines[i + 1][0].Top - lines[i][0].Top;
            var expected = lines[i].Max(r => r.Height) + Px(vertical);
            if (Math.Abs(pitch - expected) > 1.5)
            {
                problems.Add($"{what}: line {i + 2} starts {pitch:F1} px under line {i + 1}, not its height + {Px(vertical)} ({expected:F1})");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Console.WriteLine($"{what}: {items.Count} items on {lines.Count} line(s)");
    }

    // The map legend: eight entries (a 12 px swatch, a 4 px gap, the word), 16 px apart; the words' rectangles are what UI Automation
    // shows, so neighbours' words are 16 + 12 + 4 = 32 DIP apart.
    [TestMethod]
    [TestCategory("Console")]
    public void TheMapLegendIsLaidOutByTheOldWrapRules()
    {
        using var fixture = new Fixture("ui-polish-legend", Fixture.ProfileBaseFolder());
        fixture.File(@"alpha\a.mp4", 300 * KB);
        fixture.File(@"beta\b.zip", 200 * KB);
        using var app = AppSession.Start("--scan", fixture.Path);
        var map = Ui.WaitForScan(app);
        WindowOnScreen.Settle(Handle(app), BandCard(app).Current.BoundingRectangle);
        string[] words = [Text.TypeVideo, Text.TypeDiskImage, Text.TypeArchive, Text.TypeDocument, Text.TypeApplication, Text.TypeVirtualMachineImage, Text.TypeOther, Text.TypeFolder];
        var mapRect = map.Current.BoundingRectangle;
        var texts = new List<Rect>();
        foreach (var word in words)
        {
            var found = app.Window(Ui.WindowTimeout).FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.NameProperty, word), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                .Cast<AutomationElement>().Where(e => e.Current.BoundingRectangle.Top >= mapRect.Bottom - 1 && e.Current.BoundingRectangle.Top < mapRect.Bottom + Px(app, 36) && e.Current.BoundingRectangle.Left >= mapRect.Left - 2).ToList();
            Assert.AreEqual(1, found.Count, $"the legend word \"{word}\" under the map: {found.Count} found");
            texts.Add(found[0].Current.BoundingRectangle);
        }

        AssertWrapRules(texts, 16, 4, Scale(app), "map legend", stepExtra: Px(app, 16));
    }

    // The Duplicates type tabs: 4 px apart, top-aligned, no divider among them.
    [TestMethod]
    [TestCategory("Console")]
    public void TheDuplicatesTypeTabsAreLaidOutByTheOldWrapRules()
    {
        using var fixture = new Fixture("ui-polish-tabs", Fixture.ProfileBaseFolder());
        foreach (var (name, seed) in new[] { ("v", 1), ("a", 2), ("d", 3) })
        {
            var data = new byte[1024 * 1024];
            new Random(seed).NextBytes(data);
            var ext = name switch { "v" => "mp4", "a" => "zip", _ => "pdf" };
            fixture.FileWithBytes($@"{name}1\{name}.{ext}", data);
            fixture.FileWithBytes($@"{name}2\{name}.{ext}", data);
        }

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0
                                                 && AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout).Current.IsEnabled, Ui.DuplicatesTimeout),
            "no DuplicateGroup appeared");
        var tabs = AppSession.FindAll(view, "DuplicateType");
        Assert.IsTrue(tabs.Count >= 2, $"the Duplicates view shows {tabs.Count} type tab(s); the fixture has three types of duplicates");
        WindowOnScreen.Settle(Handle(app), view.Current.BoundingRectangle);
        var rects = AppSession.FindAll(view, "DuplicateType").Select(t => t.Current.BoundingRectangle).ToList();
        AssertWrapRules(rects, 4, 4, Scale(app), "Duplicates type tabs");
        var others = Ui.Names(view).Where(n => n.Length > 0).ToList();
        Assert.AreEqual(0, AppSession.FindAll(view, "DuplicateType").Count(t => t.Current.ControlType != ControlType.RadioButton && t.Current.ControlType != ControlType.TabItem),
            "an element among the type tabs is no tab: " + string.Join(" | ", others.Take(12)));
    }

    // ---- 4. Enter on a card -----------------------------------------------------------------------------------------------------

    // Contract "Card click changes": a click or Space only ticks or unticks; Enter on a focused card starts no scan either (it toggles
    // the card or does nothing).
    [TestMethod]
    [TestCategory("Console")]
    public void EnterOnTheFocusedCardStartsNoScan()
    {
        using var app = AppSession.Start();
        var card = ProfileCard(app);
        Ui.Focus(card);
        Keys.Press(Keys.Enter);
        AssertNothingStarts(app, "after Enter on the focused card");
        Assert.IsTrue(card.Current.IsEnabled || ProfileCard(app).Current.IsEnabled, "the card is disabled after Enter");
        var state = StateOf(ProfileCard(app));
        Assert.IsTrue(state is ToggleState.On or ToggleState.Off, "the card's state after Enter is " + state);
        Ui.Focus(ProfileCard(app));
        Keys.Press(Keys.Enter);
        AssertNothingStarts(app, "after a second Enter on the focused card");
    }

    // ---- 5. the busy rule ----------------------------------------------------------------------------------------------------------

    // Contract "Unchanged": the ASP-14 busy rules apply to the tick box (off while a scan, a move or the root check runs).
    //
    // A scan: the start screen is collapsed while a scan runs (WhileAScanRunsNoCardCanBeToggled). A move confirmation: a move starts
    // from a view other than Overview, so the Overview's cards are not in the UI Automation tree while it is open; the Move buttons need
    // a scan or a list. Neither can be observed on the card, and the test below is the part that can.
    //
    // The root check: the folder named by --scan is looked up on a worker thread (checkingRoot) before anything starts. A path whose
    // volume lookup takes seconds holds it open: an SMB path to an address that never answers (192.0.2.1, TEST-NET-1). While it is
    // held the start screen is shown, so the Scan circle and the card must be off, and they must be on again once the lookup has
    // failed and the refusal is shown. Inconclusive when the lookup is too quick to hold (nothing is then claimed).
    [TestMethod]
    [TestCategory("Console")]
    public void WhileTheRootIsBeingCheckedTheScanCircleAndTheCardAreOff()
    {
        using var app = AppSession.Start("--scan", @"\\192.0.2.1\lean-asp18-root-check");
        var window = app.Window(Ui.WindowTimeout);
        var held = false;
        var problems = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                var scan = AppSession.TryFind(window, "Scan");
                var cards = AppSession.TryFind(window, "DriveCards") is { } host ? AppSession.FindAll(host, "DriveCard") : [];
                var profile = cards.FirstOrDefault(IsProfile);
                if (scan is not null && profile is not null && !Ui.ScanRunning(app))
                {
                    if (!scan.Current.IsEnabled)
                    {
                        held = true;
                        if (profile.Current.IsEnabled)
                        {
                            problems.Add($"at {clock.Elapsed.TotalSeconds:F1} s Scan is off (the root check runs) but the profile card is enabled");
                        }

                        try
                        {
                            ((TogglePattern)profile.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                            problems.Add("Toggle on the card succeeded while the root check ran");
                        }
                        catch (ElementNotEnabledException)
                        {
                            // Refused: what the rule wants.
                        }
                    }
                    else if (held)
                    {
                        break; // the check has ended and Scan is on again
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            System.Threading.Thread.Sleep(150);
        }

        if (!held)
        {
            Assert.Inconclusive("the root check ended too quickly to hold (Scan was never off on the start screen): the busy rule for the root check was not observed");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
        Assert.IsTrue(AppSession.WaitUntil(() => ProfileCard(app).Current.IsEnabled && AppSession.Find(window, "Scan", Ui.ElementTimeout).Current.IsEnabled, TimeSpan.FromSeconds(10)),
            "after the root check ended the card or Scan stays off");
    }
}
