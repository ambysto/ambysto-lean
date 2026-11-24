using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-22 (docs/spec/01, "Windows note"): the drive cards of the start screen wrap onto a new line when the window is narrow, so
/// every card stays whole. Same class as <c>OverviewPolishTests</c> (partial), so it uses its helpers.</summary>
public sealed partial class OverviewPolishTests
{
    // The narrowest window width, in DIP: the window's least width (MainWindow.MinSize.cs, "as Steady"), where the sidebar (220), one card
    // (250) and the start screen's padding still fit. All widths here are DIP, in pixels x the window's DPI scale (Px).
    private const double NarrowestDips = 560;

    // The least size of the main window, 560 x 560 DIP, scaled by the DPI: resized far below it (300 DIP, TransformPattern), the window
    // stops at that size (within 4 px), in width and in height. Put back at the end.
    [TestMethod]
    [TestCategory("Console")]
    public void TheWindowCannotBeMadeSmallerThan560By560Dip()
    {
        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || !((TransformPattern)pattern).Current.CanResize)
        {
            Assert.Inconclusive("the window cannot be resized through UI Automation: the least size was not checked");
        }

        var transform = (TransformPattern)pattern;
        var original = window.Current.BoundingRectangle;
        var least = Px(app, NarrowestDips);
        try
        {
            transform.Resize(Px(app, 300), Px(app, 300));
            System.Threading.Thread.Sleep(1000);
            var after = app.Window(Ui.WindowTimeout).Current.BoundingRectangle;
            Assert.IsTrue(after.Width >= least - 4, $"the window was made {after.Width:F0} px wide, narrower than 560 DIP ({least} px)");
            Assert.IsTrue(after.Height >= least - 4, $"the window was made {after.Height:F0} px high, lower than 560 DIP ({least} px)");
            Assert.IsTrue(after.Width <= least + 4 && after.Height <= least + 4,
                $"the window is {after.Width:F0} x {after.Height:F0} px after a resize to 300 x 300 DIP; the least size is {least} x {least} px (560 DIP)");
        }
        finally
        {
            transform.Resize(original.Width, original.Height);
            System.Threading.Thread.Sleep(800);
        }
    }

    // Contract: at each width from the default down to NarrowestDips every drive card lies whole between the sidebar and the right edge
    // of the content (the status line's), and no two cards overlap. Where two cards no longer fit side by side, they are on separate
    // lines (their tops differ). The window is put back at the end; Inconclusive (never a pass) when it cannot be resized.
    [TestMethod]
    [TestCategory("Console")]
    public void TheDriveCardsWrapAndStayWholeDownToTheNarrowestWidth()
    {
        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || !((TransformPattern)pattern).Current.CanResize)
        {
            Assert.Inconclusive("the window cannot be resized through UI Automation: the narrow layouts were not checked");
        }

        var transform = (TransformPattern)pattern;
        Cards(app);
        var original = window.Current.BoundingRectangle;
        var problems = new List<string>();
        var checkedWidths = new List<string>();
        try
        {
            CheckCards(app, "default width", problems);
            foreach (var dips in new double[] { 900, 700, NarrowestDips })
            {
                var target = Px(app, dips);
                transform.Resize(target, original.Height);
                System.Threading.Thread.Sleep(1000);
                var actual = app.Window(Ui.WindowTimeout).Current.BoundingRectangle.Width;
                if (Math.Abs(actual - target) > 8)
                {
                    continue; // the window would not get that narrow
                }

                var label = $"{dips:F0} DIP ({actual:F0} px)";
                checkedWidths.Add(label);
                CheckCards(app, label, problems);
                if (dips == NarrowestDips && Cards(app).Count >= 2)
                {
                    // Two cards do not fit side by side here: they are on separate lines, so either their tops differ or a card is below the
                    // fold of the start screen's scroll viewer (it has no bounds until scrolled to).
                    var shown = Cards(app).Select(c => c.Current.BoundingRectangle).Where(r => !r.IsEmpty).ToList();
                    if (shown.Count == Cards(app).Count && shown.Select(r => Math.Round(r.Top)).Distinct().Count() < 2)
                    {
                        problems.Add($"{label}: {shown.Count} cards are all on one line, although two do not fit side by side");
                    }
                }
            }
        }
        finally
        {
            transform.Resize(original.Width, original.Height);
            System.Threading.Thread.Sleep(800);
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        if (checkedWidths.Count == 0)
        {
            Assert.Inconclusive("the window could not be made narrower: only the default width was checked");
        }

        Console.WriteLine("drive cards checked at: default, " + string.Join(", ", checkedWidths));
    }

    // The start screen scrolls, so a card on a lower line has no bounds until it is scrolled to. The rule is that no card is cut at the
    // right edge (or the left), wherever it is scrolled to, not that all are in view at once. Overlap is judged among the cards in view at
    // the same time, before any scrolling.
    private static void CheckCards(AppSession app, string where, List<string> problems)
    {
        var window = app.Window(Ui.WindowTimeout);
        var left = Ui.Sidebar(app).Current.BoundingRectangle.Right;
        var right = AppSession.Find(window, "Status", Ui.ElementTimeout).Current.BoundingRectangle.Right;
        var scroll = ScrollerOf(AppSession.Find(window, "DriveCards", Ui.ElementTimeout));
        scroll?.SetScrollPercent(ScrollPattern.NoScroll, 0);
        System.Threading.Thread.Sleep(200);
        var inView = new List<(string Name, System.Windows.Rect Rect)>();
        foreach (var card in Cards(app))
        {
            var rect = card.Current.BoundingRectangle;
            if (!rect.IsEmpty && rect.Width > 0 && !card.Current.IsOffscreen)
            {
                inView.Add((card.Current.Name, rect));
            }
        }

        foreach (var card in Cards(app))
        {
            var name = card.Current.Name;
            var rect = BringIntoView(card, scroll);
            if (rect.IsEmpty || rect.Width <= 0)
            {
                problems.Add($"{where}: the card {name} has no bounds, even scrolled to");
                continue;
            }

            if (rect.Left < left - 1 || rect.Right > right + 1)
            {
                problems.Add($"{where}: the card {name} {rect} is cut: the content runs from {left:F0} to {right:F0}");
            }
        }

        scroll?.SetScrollPercent(ScrollPattern.NoScroll, 0);
        for (var i = 0; i < inView.Count; i++)
        {
            for (var j = i + 1; j < inView.Count; j++)
            {
                if (inView[i].Rect.IntersectsWith(inView[j].Rect))
                {
                    problems.Add($"{where}: the cards {inView[i].Name} {inView[i].Rect} and {inView[j].Name} {inView[j].Rect} overlap");
                }
            }
        }
    }

    /// <summary>The scrolling pattern of the nearest ancestor of <paramref name="element"/> that scrolls vertically, or null.</summary>
    private static ScrollPattern? ScrollerOf(AutomationElement element)
    {
        for (var node = TreeWalker.ControlViewWalker.GetParent(element); node is not null; node = TreeWalker.ControlViewWalker.GetParent(node))
        {
            if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable)
            {
                return (ScrollPattern)pattern;
            }
        }

        return null;
    }

    /// <summary>The bounds of <paramref name="card"/>, scrolling the start screen down in steps until it is in view (they are empty when
    /// it never is).</summary>
    private static System.Windows.Rect BringIntoView(AutomationElement card, ScrollPattern? scroll)
    {
        for (var percent = 0; percent <= 100; percent += 10)
        {
            var rect = card.Current.BoundingRectangle;
            if (!rect.IsEmpty && rect.Width > 0 && !card.Current.IsOffscreen)
            {
                return rect;
            }

            if (scroll is null)
            {
                break;
            }

            scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
            System.Threading.Thread.Sleep(150);
        }

        return card.Current.BoundingRectangle;
    }
}
