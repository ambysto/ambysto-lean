using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-12, the brand theme (intent/2026-10-18-asp-12-brand-theme.md, "Theme contract" and "Proof"): the frame,
/// button and map colours sampled from the screen in the console session, and the fonts checked against the bundled
/// files. Written from the contract only. The Windows app mode is read, never changed; High Contrast is never toggled (a
/// run with it on is inconclusive, because the contract then hands every colour to the system). With ASP-14: the start
/// screen's Scan circle, Scan again and ScanFolder after a scan, and the sidebar. Nothing is invoked but the Settings
/// item; nothing is moved, revealed or opened.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ThemeTests
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The five fonts and two licences the contract bundles (Assets/Fonts, copied to the install folder).</summary>
    private static readonly string[] BundledFontFiles =
    [
        "JetBrainsMono-Regular.ttf", "JetBrainsMono-Medium.ttf", "JetBrainsMono-Bold.ttf", "Geist-Regular.ttf", "Geist-Medium.ttf",
        "OFL-JetBrainsMono.txt", "OFL-Geist.txt",
    ];

    /// <summary>One file per type group (docs/spec/01 Map and the Core's TypeGroup extensions), with the contract's
    /// tile fill, largest first so that every tile is large enough to sample its centre.</summary>
    private static readonly (string File, string Group, string Fill)[] Groups =
    [
        ("film.mp4", "video", "#b0adff"),
        ("disk.iso", "disk image", "#62c4f5"),
        ("bundle.zip", "archive", "#dfae5b"),
        ("paper.pdf", "document", "#4ecfbd"),
        ("setup.exe", "application", "#9fc675"),
        ("machine.vhdx", "virtual machine image", "#e69ad4"),
        ("blob.bin", "other", "#8a8a8c"),
    ];

    /// <summary>A folder drawn with its children inside: two children large enough to be drawn.</summary>
    private const string OpenFolder = "folder";

    private static Fixture? map;

    [ClassInitialize]
    public static void BuildTheTypeGroupTree(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        map = new Fixture("ui-theme");
        var size = 1400 * ByteUnits.KB;
        foreach (var (file, _, _) in Groups)
        {
            map.File(file, size);
            size -= 100 * ByteUnits.KB;
        }

        map.File(OpenFolder + @"\inner-film.mp4", 900 * ByteUnits.KB);
        map.File(OpenFolder + @"\inner-bundle.zip", 700 * ByteUnits.KB);
    }

    [ClassCleanup]
    public static void RemoveTheTypeGroupTree() => map?.Dispose();

    [TestInitialize]
    public void NotInHighContrast()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the contract hands every colour to the system, so the brand colours are not checked (the setting is never changed by a test)");
        }
    }

    /// <summary>A started app's main window, brought to the front and settled, with its handle and the Scan button (ASP-14:
    /// before a scan the start screen's circle in the middle of the content, ground all round it; after a scan the Scan
    /// again button at the top right).</summary>
    private sealed record Shown(AutomationElement Window, AutomationElementHandle Handle, AutomationElement Scan, Rect ScanBox);

    private static Shown Show(AppSession app)
    {
        var window = app.Window(WindowTimeout);
        var scan = AppSession.Find(window, "Scan", ElementTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        var box = scan.Current.BoundingRectangle;
        WindowOnScreen.Settle(handle, box);
        return new Shown(window, handle, scan, box);
    }

    /// <summary>The window's visible frame, captured and saved for a look at a failure.</summary>
    private static ScreenCapture Capture(Shown shown, string name)
    {
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(shown.Handle));
        Console.WriteLine($"{name}: capture {capture.Left},{capture.Top} {capture.Width}x{capture.Height}, scale {WindowOnScreen.Scale(shown.Handle).ToString("F2", CultureInfo.InvariantCulture)}, Windows app mode {DisplaySettings.AppMode()}, saved {capture.Save(name)}");
        return capture;
    }

    private static int Px(Shown shown, double dips) => (int)Math.Round(dips * WindowOnScreen.Scale(shown.Handle));

    /// <summary>The pixels of <paramref name="rect"/> shrunk by <paramref name="inset"/> on each side (and
    /// <paramref name="topInset"/> at the top when given).</summary>
    private static List<Rgb> Inside(ScreenCapture capture, Rect rect, int inset, int? topInset = null) =>
        capture.Area((int)Math.Round(rect.Left) + inset, (int)Math.Round(rect.Top) + (topInset ?? inset), (int)Math.Round(rect.Right) - inset, (int)Math.Round(rect.Bottom) - inset);

    /// <summary>The empty strip of ground just left of the Scan button (before a scan: beside the start screen's circle).</summary>
    private static List<Rgb> GroundStrip(Shown shown, ScreenCapture capture)
    {
        var scan = shown.ScanBox;
        var client = WindowOnScreen.Client(shown.Handle);
        var left = Math.Max(client.X + Px(shown, 2), (int)scan.Left - Px(shown, 13));
        return capture.Area(left, (int)scan.Top, (int)scan.Left - Px(shown, 3), (int)scan.Bottom);
    }

    private static string Percent(double share) => (100 * share).ToString("F0", CultureInfo.InvariantCulture) + " %";

    // Contract, frame colours: LeanGround #0b0b0c is the window ground. Sampled in the empty ground left of the start
    // screen's Scan circle (ASP-14).
    [TestMethod]
    [TestCategory("Console")]
    public void TheWindowGroundIsLeanGround()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var capture = Capture(shown, nameof(TheWindowGroundIsLeanGround));
        var strip = GroundStrip(shown, capture);
        var share = ScreenCapture.Share(strip, Brand.Ground, Brand.NeutralTolerance);
        Assert.IsTrue(strip.Count >= 50, $"the padding strip beside Scan has only {strip.Count} pixels");
        Assert.IsTrue(share >= 0.95, $"the window ground beside Scan is {Brand.Ground} in only {Percent(share)} of {strip.Count} pixels; commonest: {ScreenCapture.Commonest(strip)}");
    }

    // Contract, frame colours and title bar: the title bar's background is LeanGround (AppWindow.TitleBar; the content is
    // not extended into it). Sampled in the middle of the title bar, between the title and the caption buttons.
    [TestMethod]
    [TestCategory("Console")]
    public void TheTitleBarIsLeanGround()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var capture = Capture(shown, nameof(TheTitleBarIsLeanGround));
        var frame = WindowOnScreen.Frame(shown.Handle);
        var client = WindowOnScreen.Client(shown.Handle);
        var barHeight = client.Y - frame.Y;
        Assert.IsTrue(barHeight >= Px(shown, 20), $"the client area starts {barHeight} px below the frame: no system title bar above the content (the contract keeps the content out of it)");
        var bar = capture.Area(frame.X + (int)(frame.Width * 0.35), frame.Y + Px(shown, 6), frame.X + (int)(frame.Width * 0.6), client.Y - Px(shown, 6));
        var share = ScreenCapture.Share(bar, Brand.Ground, Brand.NeutralTolerance);
        Assert.IsTrue(share >= 0.95, $"the title bar is {Brand.Ground} in only {Percent(share)} of {bar.Count} pixels; commonest: {ScreenCapture.Commonest(bar)}");
    }

    // Contract, frame colours: LeanPanel #101011 is behind the volume card. Sampled inside the card, below its top
    // border (which is LeanTone); its texts are the minority of the area.
    [TestMethod]
    [TestCategory("Console")]
    public void TheVolumeCardIsOnLeanPanel()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var card = AppSession.Find(shown.Window, "VolumeCard", ElementTimeout);
        AppSession.Find(card, "UsedByThisVolume", ElementTimeout);
        System.Threading.Thread.Sleep(500);
        var capture = Capture(shown, nameof(TheVolumeCardIsOnLeanPanel));
        var inside = Inside(capture, card.Current.BoundingRectangle, Px(shown, 2), Px(shown, 4));
        var share = ScreenCapture.Share(inside, Brand.Panel, Brand.NeutralTolerance);
        Assert.IsTrue(inside.Count >= 100, $"the volume card has only {inside.Count} pixels inside it ({card.Current.BoundingRectangle})");
        Assert.IsTrue(share >= 0.5, $"inside the volume card, {Brand.Panel} (LeanPanel) is only {Percent(share)} of {inside.Count} pixels; commonest: {ScreenCapture.Commonest(inside)}");
    }

    // Contract, frame colours and type: a title is primary text, LeanInk #e8e8e6 on LeanGround. The Settings view's
    // title: some of its glyph pixels are ink (full coverage), none is brighter than ink (anti-aliasing blends ink into
    // the ground, never past it, so white text fails), and its box is mostly ground.
    [TestMethod]
    [TestCategory("Console")]
    public void ATitleIsDrawnInLeanInkOnLeanGround()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        Ui.OpenView(app, "Settings", "SettingsView");
        var view = Ui.FindAnywhere(app, "SettingsView", ElementTimeout);
        var title = AppSession.WaitFor(() => view.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, Text.Settings))), ElementTimeout);
        Assert.IsNotNull(title, "no Settings title text in the Settings view: " + Ui.Dump(view));
        WindowOnScreen.Settle(shown.Handle, shown.ScanBox);
        var capture = Capture(shown, nameof(ATitleIsDrawnInLeanInkOnLeanGround));
        var box = Inside(capture, title.Current.BoundingRectangle, 0);
        var ink = box.Count(p => p.Near(Brand.Ink, Brand.Tolerance));
        var brightest = box.Count == 0 ? default : box.MaxBy(p => p.Brightest);
        var ground = ScreenCapture.Share(box, Brand.Ground, Brand.NeutralTolerance);
        var where = $"title box {title.Current.BoundingRectangle}, {box.Count} pixels; commonest: {ScreenCapture.Commonest(box)}; brightest {brightest}";
        Assert.IsTrue(ink >= 6, $"only {ink} pixels of the title are {Brand.Ink} (LeanInk); {where}");
        Assert.IsTrue(brightest.Brightest <= Brand.Ink.Brightest + Brand.Tolerance, $"a title pixel is brighter than LeanInk; {where}");
        Assert.IsTrue(ground >= 0.4, $"the title's box is {Brand.Ground} (LeanGround) in only {Percent(ground)}; {where}");
    }

    // Contract, buttons, with ASP-14 "3. Scanned": after a scan, Scan is a normal primary button (Text.ScanAgain), filled
    // with LeanInk, its text LeanGround. Sampled inside the button, clear of its 6 px rounded corners and of a focus ring.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanAgainIsAPrimaryButtonFilledWithInk()
    {
        using var app = AppSession.Start("--scan", map!.Path);
        var (shown, _, capture) = ScannedMap(app, nameof(ScanAgainIsAPrimaryButtonFilledWithInk));
        Assert.AreEqual(Ui.Copy("ScanAgain"), shown.Scan.Current.Name, "after a scan Scan is labelled Text.ScanAgain");
        var rect = shown.ScanBox;
        var inside = Inside(capture, rect, Px(shown, 4));
        var fill = ScreenCapture.Share(inside, Brand.Ink, Brand.Tolerance);
        var text = inside.Count(p => p.Near(Brand.Ground, Brand.Tolerance));
        var where = $"Scan again {rect}, {inside.Count} pixels inside; commonest: {ScreenCapture.Commonest(inside)}";
        Assert.IsTrue(fill >= 0.6, $"Scan again is filled with {Brand.Ink} (LeanInk) in only {Percent(fill)}; {where}");
        Assert.IsTrue(text >= 4, $"Scan again's label has no {Brand.Ground} (LeanGround) pixels ({text}); {where}");
    }

    // ASP-14 "Overview, 1. Start": before a scan, Scan is a 160 px circle outlined 2 px in LeanInk and filled LeanGround,
    // its label in LeanInk. Sampled: the corners of its box (outside the circle) and its inside are ground (the label is
    // the rest of the inside); on the lines through its centre, each edge of the box holds ink within its outer 4 px (the
    // outline) while the corners hold none.
    [TestMethod]
    [TestCategory("Console")]
    public void TheStartScanIsACircleOutlinedInInkOnTheGround()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var capture = Capture(shown, nameof(TheStartScanIsACircleOutlinedInInkOnTheGround));
        var r = shown.ScanBox;
        var (left, top, right, bottom) = ((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        var (cx, cy) = ((left + right) / 2, (top + bottom) / 2);
        var corner = Px(shown, 16);
        var corners = capture.Area(left, top, left + corner, top + corner)
            .Concat(capture.Area(right - corner, top, right, top + corner))
            .Concat(capture.Area(left, bottom - corner, left + corner, bottom))
            .Concat(capture.Area(right - corner, bottom - corner, right, bottom)).ToList();
        var problems = new List<string>();
        var groundCorners = ScreenCapture.Share(corners, Brand.Ground, Brand.NeutralTolerance);
        if (groundCorners < 0.95)
        {
            problems.Add($"the corners of Scan's box are {Brand.Ground} (LeanGround) in only {Percent(groundCorners)}: not a circle; commonest {ScreenCapture.Commonest(corners)}");
        }

        var edge = Px(shown, 4);
        var band = Px(shown, 6);
        var edges = new (string Edge, List<Rgb> Pixels)[]
        {
            ("left", capture.Area(left, cy - band, left + edge, cy + band)),
            ("right", capture.Area(right - edge, cy - band, right, cy + band)),
            ("top", capture.Area(cx - band, top, cx + band, top + edge)),
            ("bottom", capture.Area(cx - band, bottom - edge, cx + band, bottom)),
        };
        foreach (var (name, pixels) in edges)
        {
            var ink = pixels.Count(p => p.Near(Brand.Ink, Brand.Tolerance));
            if (ink < band)
            {
                problems.Add($"the {name} edge of Scan's circle has {ink} {Brand.Ink} (LeanInk) pixels in its outer {edge} px: no 2 px ink outline; commonest {ScreenCapture.Commonest(pixels)}");
            }
        }

        var inside = Inside(capture, r, Px(shown, 30));
        var ground = ScreenCapture.Share(inside, Brand.Ground, Brand.NeutralTolerance);
        var label = inside.Count(p => p.Near(Brand.Ink, Brand.Tolerance));
        if (ground < 0.5 || label < 6)
        {
            problems.Add($"inside the circle, {Brand.Ground} (LeanGround) is {Percent(ground)} and {Brand.Ink} label pixels {label}: not filled with ground and labelled in ink; commonest {ScreenCapture.Commonest(inside)}");
        }

        Assert.AreEqual(0, problems.Count, $"Scan {r}:\n" + string.Join("\n", problems));
    }

    // Contract, buttons, with ASP-14 "3. Scanned": every other button is a ghost: transparent (the ground shows inside) with
    // a 1 px LeanDim #3a3a3c border. Checked on ScanFolder after a scan (Settings and About are sidebar items now): the
    // inside, and the best of the outer three rows and columns of each edge (the border is one of them, wherever the
    // control's box puts it).
    [TestMethod]
    [TestCategory("Console")]
    public void ScanFolderIsAGhostButtonAfterAScan()
    {
        using var app = AppSession.Start("--scan", map!.Path);
        var (shown, _, capture) = ScannedMap(app, nameof(ScanFolderIsAGhostButtonAfterAScan));
        var problems = new List<string>();
        foreach (var id in new[] { "ScanFolder" })
        {
            var button = AppSession.Find(shown.Window, id, ElementTimeout);
            var rect = button.Current.BoundingRectangle;
            var inside = Inside(capture, rect, Px(shown, 4));
            var ground = ScreenCapture.Share(inside, Brand.Ground, Brand.NeutralTolerance);
            if (ground < 0.6)
            {
                problems.Add($"{id}: the inside is {Brand.Ground} (LeanGround) in only {Percent(ground)}; commonest: {ScreenCapture.Commonest(inside)}");
            }

            var (left, top, right, bottom) = ((int)Math.Round(rect.Left), (int)Math.Round(rect.Top), (int)Math.Round(rect.Right), (int)Math.Round(rect.Bottom));
            var margin = Px(shown, 8);
            var edges = new (string Edge, Func<int, List<Rgb>> Line)[]
            {
                ("top", k => capture.Area(left + margin, top + k, right - margin, top + k + 1)),
                ("bottom", k => capture.Area(left + margin, bottom - 1 - k, right - margin, bottom - k)),
                ("left", k => capture.Area(left + k, top + margin, left + k + 1, bottom - margin)),
                ("right", k => capture.Area(right - 1 - k, top + margin, right - k, bottom - margin)),
            };
            foreach (var (edge, line) in edges)
            {
                var best = Enumerable.Range(0, 3).Select(k => line(k)).MaxBy(l => ScreenCapture.Share(l, Brand.Dim, Brand.Tolerance))!;
                var share = ScreenCapture.Share(best, Brand.Dim, Brand.Tolerance);
                if (share < 0.8)
                {
                    problems.Add($"{id}: the {edge} border is {Brand.Dim} (LeanDim) in only {Percent(share)} of its best line; commonest: {ScreenCapture.Commonest(best)}");
                }
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // ASP-14 "The sidebar": the sidebar is on LeanSidebar #161618 (ASP-15 amendment 2026-10-19; it was LeanPanel) with a 1 px LeanRule line on its right (sampled in its empty band
    // between History and Settings); the app name at its top is "Ambysto" in LeanMute and "Lean" in LeanTone (the accent's
    // place): some of each word's glyph pixels are its colour, and "Ambysto" has no LeanTone pixel.
    [TestMethod]
    [TestCategory("Console")]
    public void TheSidebarIsLeanSidebarWithARuleAndTheNameCarriesTheTone()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var sidebar = Ui.Sidebar(app);
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        var capture = Capture(shown, nameof(TheSidebarIsLeanSidebarWithARuleAndTheNameCarriesTheTone));
        var box = sidebar.Current.BoundingRectangle;
        var (left, right) = ((int)Math.Round(box.Left), (int)Math.Round(box.Right));
        var (bandTop, bandBottom) = ((int)Math.Round(history.Bottom) + Px(shown, 4), (int)Math.Round(settings.Top) - Px(shown, 4));
        Assert.IsTrue(bandBottom - bandTop >= Px(shown, 10), $"precondition: no empty band between History ({history}) and Settings ({settings})");
        var problems = new List<string>();
        var sidebarArea = capture.Area(left + Px(shown, 8), bandTop, right - Px(shown, 8), bandBottom);
        var panel = ScreenCapture.Share(sidebarArea, Brand.Sidebar, Brand.NeutralTolerance);
        if (panel < 0.95)
        {
            problems.Add($"the sidebar's empty band is {Brand.Sidebar} (LeanSidebar) in only {Percent(panel)}; commonest {ScreenCapture.Commonest(sidebarArea)}");
        }

        var rule = Enumerable.Range(-2, 5).Select(k => capture.Area(right - 1 + k, bandTop, right + k, bandBottom)).MaxBy(l => ScreenCapture.Share(l, Brand.Rule, Brand.NeutralTolerance))!;
        var ruleShare = ScreenCapture.Share(rule, Brand.Rule, Brand.NeutralTolerance);
        if (ruleShare < 0.8)
        {
            problems.Add($"no column at the sidebar's right edge ({right}) is {Brand.Rule} (LeanRule): best {Percent(ruleShare)}; commonest {ScreenCapture.Commonest(rule)}");
        }

        // The app name: one text with two runs, or two texts; its glyphs hold LeanMute ("Ambysto") and LeanTone ("Lean").
        var name = StartAndSidebarTests.AppName(sidebar, Ui.SidebarItem(app, "Overview").Current.BoundingRectangle);
        var glyphs = name.SelectMany(t => Inside(capture, t.Current.BoundingRectangle, 0)).ToList();
        var mute = glyphs.Count(p => p.Near(Brand.Mute, Brand.Tolerance));
        var tone = glyphs.Count(p => p.Near(Brand.Coral, Brand.Tolerance));
        if (name.Count == 0 || mute < 6 || tone < 6)
        {
            problems.Add($"the app name [{string.Join(" | ", name.Select(t => t.Current.Name))}] has {mute} {Brand.Mute} (LeanMute) and {tone} {Brand.Coral} (LeanTone) pixels; commonest {ScreenCapture.Commonest(glyphs)}");
        }

        if (name.FirstOrDefault(t => t.Current.Name == "Ambysto") is { } ambysto && Inside(capture, ambysto.Current.BoundingRectangle, 0).Any(p => p.Near(Brand.Coral, Brand.Tolerance)))
        {
            problems.Add("\"Ambysto\" has LeanTone pixels: the accent belongs to \"Lean\" only");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    /// <summary>The fixture map, scanned and settled.</summary>
    private static (Shown Shown, AutomationElement Map, ScreenCapture Capture) ScannedMap(AppSession app, string name)
    {
        var mapElement = Ui.WaitForScan(app);
        var shown = Show(app);
        return (shown, mapElement, Capture(shown, name));
    }

    // Contract, the map (BrandPalette): each type group's tile is filled with its brand tone. One file per group;
    // each tile's centre (9 × 9 px, clear of the label at its top left) is sampled.
    [TestMethod]
    [TestCategory("Console")]
    public void EachTypeGroupsTileHasItsBrandTone()
    {
        using var app = AppSession.Start("--scan", map!.Path);
        var (shown, mapElement, capture) = ScannedMap(app, nameof(EachTypeGroupsTileHasItsBrandTone));
        var half = Px(shown, 4);
        var problems = new List<string>();
        foreach (var (file, group, fill) in Groups)
        {
            var rect = Ui.TileOf(mapElement, file).Current.BoundingRectangle;
            var (x, y) = ((int)(rect.Left + (rect.Width / 2)), (int)(rect.Top + (rect.Height / 2)));
            var centre = capture.Area(x - half, y - half, x + half + 1, y + half + 1);
            var share = ScreenCapture.Share(centre, Rgb.Parse(fill), Brand.Tolerance);
            var line = $"{group} ({file}, tile {rect}): {fill} in {Percent(share)} of the centre; commonest: {ScreenCapture.Commonest(centre, 2)}";
            Console.WriteLine(line);
            if (share < 0.8)
            {
                problems.Add(line);
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract, the map: a folder drawn with its children inside is LeanPanel with a 1 px LeanRule border. Its colour
    // shows only in the band between its own box and its children's, so the band is sampled ring by ring (the four
    // innermost-but-one pixel rings, children excluded): at least one ring is LeanPanel.
    [TestMethod]
    [TestCategory("Console")]
    public void AFolderDrawnWithItsChildrenInsideIsLeanPanel()
    {
        using var app = AppSession.Start("--scan", map!.Path);
        var (_, mapElement, capture) = ScannedMap(app, nameof(AFolderDrawnWithItsChildrenInsideIsLeanPanel));
        var folder = Ui.TileOf(mapElement, OpenFolder).Current.BoundingRectangle;
        var children = AppSession.FindAll(mapElement, "Tile").Select(t => t.Current.BoundingRectangle)
            .Where(r => folder.Contains(r) && r != folder).ToList();
        Assert.IsTrue(children.Count >= 2, $"the folder tile {folder} has {children.Count} tiles inside: it is not drawn with its children inside; tiles: [{string.Join(" | ", Ui.TileNames(mapElement))}]");
        var (left, top, right, bottom) = ((int)Math.Round(folder.Left), (int)Math.Round(folder.Top), (int)Math.Round(folder.Right), (int)Math.Round(folder.Bottom));
        bool InChild(int x, int y) => children.Any(c => x >= c.Left && x < c.Right && y >= c.Top && y < c.Bottom);
        var rings = new List<(int Offset, List<Rgb> Pixels)>();
        for (var k = 0; k < 4; k++)
        {
            var ring = new List<Rgb>();
            for (var x = left + k; x < right - k; x++)
            {
                foreach (var y in new[] { top + k, bottom - 1 - k })
                {
                    if (!InChild(x, y) && capture.Contains(x, y))
                    {
                        ring.Add(capture.At(x, y));
                    }
                }
            }

            for (var y = top + k + 1; y < bottom - 1 - k; y++)
            {
                foreach (var x in new[] { left + k, right - 1 - k })
                {
                    if (!InChild(x, y) && capture.Contains(x, y))
                    {
                        ring.Add(capture.At(x, y));
                    }
                }
            }

            rings.Add((k, ring));
        }

        var report = string.Join("; ", rings.Select(r => $"ring {r.Offset}: {r.Pixels.Count} px, {Brand.Panel} {Percent(ScreenCapture.Share(r.Pixels, Brand.Panel, Brand.NeutralTolerance))}, commonest {ScreenCapture.Commonest(r.Pixels, 2)}"));
        Console.WriteLine($"folder {folder}: {report}");
        Assert.IsTrue(rings.Any(r => r.Pixels.Count >= 40 && ScreenCapture.Share(r.Pixels, Brand.Panel, Brand.NeutralTolerance) >= 0.7),
            $"no ring of the folder drawn with its children inside is {Brand.Panel} (LeanPanel); {report}");
    }

    // Contract, the map: Space Coral #f99a8f is not a map colour (spec 01: no red for ordinary files). No pixel of the
    // map or of the legend under it is Space Coral (the accent's three places, the header, the progress bar and the
    // card's top border, are all above the map).
    [TestMethod]
    [TestCategory("Console")]
    public void NoTileOrLegendSwatchIsSpaceCoral()
    {
        using var app = AppSession.Start("--scan", map!.Path);
        var (shown, mapElement, capture) = ScannedMap(app, nameof(NoTileOrLegendSwatchIsSpaceCoral));
        var area = mapElement.Current.BoundingRectangle;
        var status = AppSession.Find(shown.Window, "Status", ElementTimeout).Current.BoundingRectangle;
        var bottom = Math.Max((int)area.Bottom, (int)status.Top);
        var pixels = capture.Area((int)area.Left, (int)area.Top, (int)area.Right, bottom);
        var coral = pixels.Count(p => p.Near(Brand.Coral, Brand.Tolerance));
        var tiles = AppSession.FindAll(mapElement, "Tile");
        var coralTiles = tiles.Where(t =>
        {
            var r = t.Current.BoundingRectangle;
            var (x, y) = ((int)(r.Left + (r.Width / 2)), (int)(r.Top + (r.Height / 2)));
            return capture.Contains(x, y) && capture.At(x, y).Near(Brand.Coral, Brand.Tolerance);
        }).Select(t => t.Current.Name).ToList();
        Console.WriteLine($"map and legend {area.Left},{area.Top} to {area.Right},{bottom}: {pixels.Count} pixels, {coral} Space Coral; {tiles.Count} tiles");
        Assert.IsTrue(tiles.Count >= Groups.Length, $"only {tiles.Count} tiles were drawn");
        Assert.AreEqual(0, coralTiles.Count, "tiles whose centre is Space Coral: " + string.Join(" | ", coralTiles));
        Assert.AreEqual(0, coral, $"{coral} pixels of the map and legend are Space Coral {Brand.Coral}");
    }

    // Contract, type: the five fonts and the two OFL licences are copied to the install folder (here: the build output
    // next to AmbystoLean.exe, under Assets\Fonts), byte for byte the bundled ones.
    [TestMethod]
    [TestCategory("Console")]
    public void TheBundledFontsAndLicencesAreNextToTheApp()
    {
        var output = Path.Combine(Path.GetDirectoryName(AppSession.AppPath())!, "Assets", "Fonts");
        var source = SourceFonts();
        var problems = new List<string>();
        foreach (var name in BundledFontFiles)
        {
            var copy = Path.Combine(output, name);
            var original = Path.Combine(source, name);
            if (!File.Exists(original))
            {
                problems.Add("not in the repo: " + original);
            }
            else if (!File.Exists(copy))
            {
                problems.Add("not in the output: " + copy);
            }
            else if (!SHA256.HashData(File.ReadAllBytes(copy)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))))
            {
                problems.Add("differs from the bundled file: " + copy);
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    /// <summary>windows/src/Ambysto.Lean.App/Assets/Fonts, from the built app's path.</summary>
    private static string SourceFonts()
    {
        var exe = new FileInfo(AppSession.AppPath());
        return Path.Combine(exe.Directory!.Parent!.Parent!.Parent!.Parent!.FullName, "Assets", "Fonts");
    }

    // Contract, type: LeanMono (JetBrains Mono) is the default for what the system says or measures: here the volume
    // card's text (Regular) and the Settings view's heading (LeanTitle, Medium). Each must be drawn in the bundled font,
    // not a fallback: the character boxes UI Automation reports for the drawn text sit where the bundled file's advances
    // put them (all one width), and TextPattern's FontName names it.
    [TestMethod]
    [TestCategory("Console")]
    public void MeasuredTextAndHeadingsAreDrawnInTheBundledJetBrainsMono()
    {
        using var app = AppSession.Start();
        var shown = Show(app);
        var text = AppSession.Find(shown.Window, "UsedByThisVolume", ElementTimeout);
        AssertDrawnIn(text, "JetBrains Mono", FontFile.Read(Path.Combine(SourceFonts(), "JetBrainsMono-Regular.ttf")));
        var view = Ui.OpenView(app, "Settings", "SettingsView");
        var title = AppSession.WaitFor(() => view.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, Text.Settings))), ElementTimeout);
        Assert.IsNotNull(title, "no Settings title text in the Settings view: " + Ui.Dump(view));
        AssertDrawnIn(title, "JetBrains Mono", FontFile.Read(Path.Combine(SourceFonts(), "JetBrainsMono-Medium.ttf")), 20 * WindowOnScreen.Scale(shown.Handle));
    }

    // Contract, type: LeanSans (Geist) through LeanSentence, for explanatory sentences: here the Settings view's note on
    // protected folders. Same check as for the mono text, against the bundled Geist Regular, and the advances must fit
    // LeanSentence's 15 px (Segoe UI's advances fit Geist's within 1.3 px, but at another size).
    [TestMethod]
    [TestCategory("Console")]
    public void ASentenceIsDrawnInTheBundledGeist()
    {
        using var app = AppSession.Start();
        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(app.Window(WindowTimeout).Current.NativeWindowHandle)));
        var view = Ui.OpenView(app, "Settings", "SettingsView");
        var note = AppSession.WaitFor(() => view.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, Text.ProtectedFoldersNote))), ElementTimeout);
        Assert.IsNotNull(note, "no protected-folders note in the Settings view: " + Ui.Dump(view));
        AssertDrawnIn(note, "Geist", FontFile.Read(Path.Combine(SourceFonts(), "Geist-Regular.ttf")), 15 * scale);
    }

    /// <summary>Fails unless <paramref name="element"/>'s characters are placed as <paramref name="font"/>'s advances
    /// place them (see <see cref="Drawn"/>) and TextPattern's FontName, when the provider reports one, names
    /// <paramref name="family"/>.</summary>
    private static void AssertDrawnIn(AutomationElement element, string family, FontFile font, double? sizePixels = null)
    {
        var (residual, size, fontName, summary) = Drawn(element, font);
        Assert.IsTrue(residual <= MaxResidual, $"the text is not drawn in the bundled {family} (FontName \"{fontName}\"): {summary}");
        if (sizePixels is { } expected)
        {
            Assert.IsTrue(Math.Abs(size - expected) <= 0.4, $"the text's advances fit {family} at {size.ToString("F1", CultureInfo.InvariantCulture)} px, not the contract's {expected.ToString("F1", CultureInfo.InvariantCulture)} px (a fallback font of other proportions fits another size): {summary}");
        }

        if (fontName is string name)
        {
            Assert.IsTrue(name.Contains(family, StringComparison.OrdinalIgnoreCase), $"TextPattern FontName is \"{name}\", not {family}; {summary}");
        }
    }

    /// <summary>How far, in pixels, the drawn characters may sit from where the font's advances put them: UI Automation
    /// rounds each box to whole pixels. Probe 2026-10-18 on the default look: Segoe UI text against Segoe UI's own file
    /// 0.5–0.6 px; against JetBrains Mono 5–7 px; against Geist 1.3 px (hence also the size check for Geist).</summary>
    private const double MaxResidual = 1.0;

    /// <summary>How closely <paramref name="element"/>'s drawn characters follow <paramref name="font"/>: the left edge of each
    /// character box UI Automation reports (TextPattern, one per character, the first one left out because its box starts
    /// at the element's edge), against the running sum of the file's advances, scaled by one fitted font size. Returns the
    /// largest distance in pixels and the fitted font size.</summary>
    private static (double Residual, double Size, object FontName, string Summary) Drawn(AutomationElement element, FontFile font)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(font);
        Assert.IsTrue(element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern), $"{Ui.Describe(element)} has no TextPattern: the rendered font cannot be checked");
        var document = ((TextPattern)pattern).DocumentRange;
        var fontName = document.GetAttributeValue(TextPattern.FontNameAttribute);
        var boxes = new List<(char Char, double Left)>();
        var range = document.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        for (var i = 0; i < 80; i++)
        {
            if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) == 0)
            {
                break;
            }

            var chars = range.GetText(-1);
            var rects = range.GetBoundingRectangles();
            if (chars.Length != 1 || rects.Length != 1 || font.Advance(chars[0]) is null)
            {
                break;
            }

            boxes.Add((chars[0], rects[0].Left));
            range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
        }

        Assert.IsTrue(boxes.Count >= 7, $"UI Automation gave character boxes for only {boxes.Count} leading characters of \"{element.Current.Name}\": the rendered font cannot be checked");
        // Drawn and expected offsets from the second character on.
        var drawn = new List<double>();
        var expected = new List<double>();
        double sum = 0;
        for (var i = 1; i < boxes.Count; i++)
        {
            drawn.Add(boxes[i].Left - boxes[1].Left);
            expected.Add(sum);
            sum += font.Advance(boxes[i].Char)!.Value;
        }

        var pixelsPerUnit = drawn.Zip(expected, (d, e) => d * e).Sum() / expected.Sum(e => e * e);
        var residuals = drawn.Zip(expected, (d, e) => Math.Abs(d - (e * pixelsPerUnit))).ToList();
        var residual = residuals.Max();
        var detail = new StringBuilder();
        for (var i = 0; i < drawn.Count; i++)
        {
            detail.Append(CultureInfo.InvariantCulture, $"{boxes[i + 1].Char}:{drawn[i]:F0}/{expected[i] * pixelsPerUnit:F1} ");
        }

        var summary = $"{Ui.Describe(element)}: FontName {fontName}; {boxes.Count} characters, fitted size {(pixelsPerUnit * font.UnitsPerEm).ToString("F1", CultureInfo.InvariantCulture)} px, largest distance {residual.ToString("F1", CultureInfo.InvariantCulture)} px from {Path.GetFileName(font.Path)}'s advances; drawn/expected offsets: {detail}";
        Console.WriteLine(summary);
        return (residual, pixelsPerUnit * font.UnitsPerEm, fontName, summary);
    }

    // Contract, frame colours: the window content sets RequestedTheme="Dark" whatever the Windows app mode is. The mode
    // is read (never changed) and reported; under it, the ground is LeanGround and the text on it is light.
    [TestMethod]
    [TestCategory("Console")]
    public void TheFrameIsDarkWhateverTheWindowsAppMode()
    {
        var mode = DisplaySettings.AppMode();
        using var app = AppSession.Start();
        var shown = Show(app);
        var used = AppSession.Find(shown.Window, "UsedByThisVolume", ElementTimeout);
        System.Threading.Thread.Sleep(500);
        var capture = Capture(shown, nameof(TheFrameIsDarkWhateverTheWindowsAppMode));
        var strip = GroundStrip(shown, capture);
        var ground = ScreenCapture.Share(strip, Brand.Ground, Brand.NeutralTolerance);
        var text = Inside(capture, used.Current.BoundingRectangle, 0);
        var brightest = text.Count == 0 ? default : text.MaxBy(p => p.Brightest);
        Assert.IsTrue(ground >= 0.95, $"Windows app mode {mode}: the ground is {Brand.Ground} in only {Percent(ground)}; commonest: {ScreenCapture.Commonest(strip)}");
        Assert.IsTrue(brightest.Brightest >= 0xC0, $"Windows app mode {mode}: the volume card's text is not light (brightest pixel {brightest}; commonest: {ScreenCapture.Commonest(text)})");
    }
}
