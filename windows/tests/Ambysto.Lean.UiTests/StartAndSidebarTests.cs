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

/// <summary>ASP-14 UI contract (intent/2026-10-18-asp-14-start-and-sidebar.md): the sidebar (<c>Sidebar</c>) with its
/// eight items in order, one current; each item opens its view; the start state (<c>Scan</c>, <c>ScanFolder</c>,
/// <c>DriveCards</c> with one <c>DriveCard</c> per fixed drive, only the profile drive's enabled); the map drawn during a
/// delayed scan (<c>--scan-delay</c>) from the zones, display-only, with the progress line <c>ScanProgress</c>; the
/// Scanned state with <c>Text.ScanAgain</c>; scan a folder through the system picker (driven like the protected-folders
/// tests do) and by the QA argument <c>--scan</c>, refused off the profile's drive with <c>Text.FolderNotOnProfileDrive</c>.
/// The new strings are read from Copy.Text at run time (<see cref="Ui.Copy"/>), so a missing one fails only its test.
/// The map keeps its AutomationId <c>Map</c> (the contract's <c>MapArea</c> is its x:Name today, as <c>CancelButton</c>
/// is Cancel's). With 623b84c, the terminal readout during a delayed scan (<c>ScanPrompt</c>, <c>ScanReading</c>,
/// <c>ScanMeter</c>, <c>ScanLog</c>, <c>ScanProgress</c> a polite live region) and, the neon switched off, no LeanTone
/// outside the meter and the cursor. Nothing is moved, revealed or opened; the only real clicks are the left button on a
/// disabled drive card.</summary>
[TestClass]
[DoNotParallelize]
public sealed class StartAndSidebarTests
{
    private const long KB = ByteUnits.KB;

    /// <summary>The sidebar's items in the contract's order: AutomationId, label (its automation name) and the view it
    /// opens (null for Overview, which shows the start screen or the map).</summary>
    private static (string Id, string Label, string? ViewId)[] Items() =>
    [
        ("Overview", Ui.Copy("Overview"), null),
        ("LargeOld", Text.LargeOld, "LargeOldView"),
        ("TemporaryFiles", Text.TemporaryFiles, "TempView"),
        ("Duplicates", Text.Duplicates, "DuplicatesView"),
        ("Uninstall", Text.Uninstall, "UninstallView"),
        ("History", Text.History, "HistoryView"),
        ("Settings", Text.Settings, "SettingsView"),
        ("About", Text.About, "AboutView"),
    ];

    private static readonly string[] ViewIds = ["LargeOldView", "TempView", "DuplicatesView", "UninstallView", "HistoryView", "SettingsView", "AboutView"];

    /// <summary>A small tree: alpha (two files), beta (one file), gamma.bin.</summary>
    private static Fixture SmallTree(string? baseFolder = null)
    {
        var fixture = baseFolder is null ? new Fixture("ui-start") : new Fixture("ui-start", baseFolder);
        fixture.File(@"alpha\one.bin", 300 * KB);
        fixture.File(@"alpha\two.bin", 200 * KB);
        fixture.File(@"beta\one.bin", 100 * KB);
        fixture.File("gamma.bin", 50 * KB);
        return fixture;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static bool Shown(AppSession app, string id)
    {
        try
        {
            return app.FindInAnyWindow(id) is { } element && !element.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string? ShownView(AppSession app) => ViewIds.FirstOrDefault(id => Shown(app, id));

    private static AutomationElementHandle Handle(AppSession app) => new(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle));

    private static int Px(AppSession app, double dips) => (int)Math.Round(dips * WindowOnScreen.Scale(Handle(app)));

    /// <summary>The names of the ScanProgress line and everything in it, or none when it is not shown.</summary>
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

    private static string ProgressDump(AppSession app) => "[" + string.Join(" | ", ProgressNames(app).Where(n => n.Length > 0)) + "]";

    /// <summary>The dash of the progress line, an em dash (U+2014) between the place and the counts (fix round 2).</summary>
    private const string Dash = "\u2014";

    /// <summary>The progress line of a real scan, Text.ScanningLine's format (fix round 2): "Scanning &lt;where&gt; \u2014 &lt;n&gt; files,
    /// &lt;size&gt;" (a drive scan adds " of about … (n %)"; the drive is "C:", never "C::"). The refusal "Scanning other drives comes
    /// after v1. …" also starts with "Scanning " but has no dash and no file count, so it does not match.</summary>
    private static readonly Regex ScanningLineFormat = new(@"^Scanning .+ " + Dash + @" [0-9][0-9,.\u00a0\u202f ]* files?, [0-9]", RegexOptions.CultureInvariant);

    /// <summary>True for a text that says a scan is going on: the progress line's format, or the status "Scanning &lt;path&gt;"
    /// (Text.Scanning); not for the refusals (Text.FolderNotOnProfileDrive, Text.OtherDrivesAfterV1), which start the same way.</summary>
    private static bool SaysScanning(string text) =>
        !IsRefusal(text) && (ScanningLineFormat.IsMatch(text) || text.StartsWith(Text.Scanning(string.Empty), StringComparison.Ordinal));

    private static bool IsRefusal(string text) =>
        text.Contains(FolderRefused(), StringComparison.Ordinal) || text.Contains(Text.OtherDrivesAfterV1, StringComparison.Ordinal);

    /// <summary>The Scanning line in ScanProgress now, or null.</summary>
    private static string? ScanningLine(AppSession app) => ProgressNames(app).FirstOrDefault(n => ScanningLineFormat.IsMatch(n) && !IsRefusal(n));

    /// <summary>Text.FolderNotOnProfileDrive, a constant or a method taking the profile's drive ("… Choose a folder on C:").</summary>
    private static string FolderRefused() =>
        typeof(Text).GetMethods().Any(m => m.Name == "FolderNotOnProfileDrive" && m.GetParameters().Length == 1)
            ? Ui.Copy("FolderNotOnProfileDrive", Ui.ProfileDrive())
            : Ui.Copy("FolderNotOnProfileDrive");

    /// <summary>The map's tiles now, or none when there is no map.</summary>
    private static List<AutomationElement> Tiles(AppSession app)
    {
        try
        {
            return AppSession.TryFind(app.Window(Ui.WindowTimeout), "Map") is { } map ? AppSession.FindAll(map, "Tile") : [];
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>Fails if, within <see cref="Ui.NeverWatch"/>, a scan starts: Cancel enabled, a tile drawn, the progress line in
    /// a scan's format or the status saying Scanning &lt;path&gt; (the refusal text is neither); with <paramref name="refusal"/>
    /// it then fails unless the progress line still shows the refusal, Text.FolderNotOnProfileDrive (a folder refused; a click on a
    /// disabled drive card shows none).</summary>
    private static void AssertNoScanStarts(AppSession app, string after, bool refusal = false)
    {
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        Assert.IsFalse(AppSession.WaitUntil(() => Ui.ScanRunning(app) || Tiles(app).Count > 0 || ScanningLine(app) is not null
                || SaysScanning(status.Current.Name), Ui.NeverWatch),
            $"{after}, a scan started: Cancel enabled {Ui.ScanRunning(app)}, {Tiles(app).Count} tiles, progress {ProgressDump(app)}, status \"{status.Current.Name}\"");
        if (refusal)
        {
            var refused = FolderRefused();
            Assert.IsTrue(ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)),
                $"{after}, no scan started but the progress line does not say \"{refused}\": {ProgressDump(app)}");
        }
    }

    /// <summary>The fixed drives this session sees, by letter ("C:"), with their volume label; as the start screen lists them.</summary>
    private static List<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Letter(DriveInfo drive) => drive.Name.TrimEnd('\\');

    /// <summary>"<letter>: <label>", or "<letter>: Local Disk" (Text.LocalDisk) for a drive with no label, as File Explorer
    /// names it (review round 1).</summary>
    private static string CardName(DriveInfo drive) =>
        Letter(drive) + " " + (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Text.LocalDisk : drive.VolumeLabel);

    /// <summary>The start screen's drive cards, in tree order.</summary>
    private static List<AutomationElement> Cards(AppSession app)
    {
        var cards = AppSession.Find(app.Window(Ui.WindowTimeout), "DriveCards", Ui.ElementTimeout);
        List<AutomationElement> found = [];
        AppSession.WaitUntil(() => (found = AppSession.FindAll(cards, "DriveCard")).Count >= FixedDrives().Count, Ui.ElementTimeout);
        return found;
    }

    /// <summary>True when <paramref name="element"/> or an element in it is named, or helped, with <paramref name="text"/>.</summary>
    private static bool Carries(AutomationElement element, string text) =>
        Ui.AnyNameContains(element, text) || (element.Current.HelpText ?? string.Empty).Contains(text, StringComparison.Ordinal);

    /// <summary>A fixed drive other than the profile's on which the test can make a folder, or null.</summary>
    private static DriveInfo? OtherFixedDrive()
    {
        var profile = Ui.ProfileDrive();
        foreach (var drive in FixedDrives().Where(d => !string.Equals(Letter(d), profile, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(d => d.DriveFormat == "NTFS"))
        {
            try
            {
                var probe = Path.Combine(drive.Name, "LeanUiTests");
                Directory.CreateDirectory(probe);
                return drive;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    // ---- the sidebar ---------------------------------------------------------------------------------------------------

    /// <summary>The texts of the sidebar above <paramref name="overview"/>: the app name, as one text ("Ambysto Lean", two
    /// runs) or two ("Ambysto", "Lean").</summary>
    internal static List<AutomationElement> AppName(AutomationElement sidebar, Rect overview) =>
        sidebar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
            .Where(t => !t.Current.BoundingRectangle.IsEmpty && t.Current.BoundingRectangle.Bottom <= overview.Top + 1)
            .OrderBy(t => t.Current.BoundingRectangle.Top).ThenBy(t => t.Current.BoundingRectangle.Left).ToList();

    /// <summary>What the screen shows of one sidebar item: whether some column in its left 6 px is LeanInk over the middle
    /// of its height (the 2 px current bar), and how much of the rest is LeanGround (the current item's background) against
    /// LeanPanel (the sidebar's).</summary>
    private sealed record ItemLook(string Id, bool Bar, double Ground, double Panel, string Detail)
    {
        public bool Current => Bar && Ground > Panel;

        public bool Plain => !Bar && Panel >= Ground;
    }

    private static List<ItemLook> Looks(AppSession app, string name)
    {
        var handle = Handle(app);
        var card = AppSession.TryFind(app.Window(Ui.WindowTimeout), "VolumeCard")?.Current.BoundingRectangle;
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var park = card is { IsEmpty: false } c ? new Rect(c.Left + 40, c.Top + 4, 1, 1) : new Rect(sidebar.Right + 60, sidebar.Top + 60, 1, 1);
        WindowOnScreen.Settle(handle, park);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"{name}: saved {capture.Save(name)}");
        var looks = new List<ItemLook>();
        foreach (var (id, _, _) in Items())
        {
            var r = Ui.SidebarItem(app, id).Current.BoundingRectangle;
            var (left, top, right, bottom) = ((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
            var middleTop = top + (int)(r.Height * 0.3);
            var middleBottom = top + (int)(r.Height * 0.7);
            var bestBar = Enumerable.Range(0, Px(app, 6)).Select(k => ScreenCapture.Share(capture.Area(left + k, middleTop, left + k + 1, middleBottom), Brand.Ink, Brand.Tolerance)).Max();
            var rest = capture.Area(left + Px(app, 8), top + Px(app, 2), right - Px(app, 4), bottom - Px(app, 2));
            var ground = ScreenCapture.Share(rest, Brand.Ground, Brand.NeutralTolerance);
            var panel = ScreenCapture.Share(rest, Brand.Panel, Brand.NeutralTolerance);
            looks.Add(new ItemLook(id, bestBar >= 0.8, ground, panel, $"{id} {r}: bar column {bestBar:P0} ink, ground {ground:P0}, panel {panel:P0}, commonest {ScreenCapture.Commonest(rest, 2)}"));
        }

        return looks;
    }

    /// <summary>Fails unless exactly <paramref name="expected"/> looks current (bar and ground) and every other item looks
    /// plain; where the items expose SelectionItem, exactly <paramref name="expected"/> is selected.</summary>
    private static void AssertCurrent(AppSession app, string expected, string name)
    {
        var looks = Looks(app, name);
        var detail = string.Join("\n", looks.Select(l => l.Detail));
        var current = looks.Where(l => l.Current).Select(l => l.Id).ToList();
        Assert.AreEqual(1, current.Count, $"{name}: {current.Count} sidebar items look current (a 2 px LeanInk bar on the left and the LeanGround background), not exactly one:\n{detail}");
        Assert.AreEqual(expected, current[0], $"{name}: the current item is {current[0]}, not {expected}:\n{detail}");
        var notPlain = looks.Where(l => l.Id != expected && !l.Plain).Select(l => l.Id).ToList();
        Assert.AreEqual(0, notPlain.Count, $"{name}: items other than {expected} that do not look plain (no bar, LeanPanel): {string.Join(", ", notPlain)}\n{detail}");

        var selectable = Items().Select(i => Ui.SidebarItem(app, i.Id)).Where(e => e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)).ToList();
        if (selectable.Count > 0)
        {
            var selected = selectable.Where(e => ((SelectionItemPattern)e.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected).Select(e => e.Current.AutomationId).ToList();
            CollectionAssert.AreEqual(new[] { expected }, selected, $"{name}: the sidebar items selected for UI Automation: [{string.Join(", ", selected)}]");
        }
    }

    // Contract "The sidebar": a Sidebar about 220 px wide at the window's left holds, top to bottom, the app name (texts
    // "Ambysto" and "Lean") and the items Overview, Large and old, Temporary files, Duplicates, Uninstall, History, then at
    // the bottom Settings and About, each with its AutomationId and its label as its automation name. At launch exactly one
    // item is current, Overview: a 2 px LeanInk bar on its left and the LeanGround background, the others plain on LeanPanel.
    [TestMethod]
    [TestCategory("Console")]
    public void TheSidebarHoldsTheEightItemsInOrderWithExactlyOneCurrent()
    {
        using var app = AppSession.Start();
        var sidebar = Ui.Sidebar(app);
        var box = sidebar.Current.BoundingRectangle;
        var client = WindowOnScreen.Client(Handle(app));
        Assert.IsTrue(Math.Abs(box.Width - Px(app, 220)) <= Px(app, 3), $"the sidebar is {box.Width} px wide, not 220 px at this scale ({Px(app, 220)} px)");
        Assert.IsTrue(Math.Abs(box.Left - client.X) <= Px(app, 2), $"the sidebar starts at {box.Left}, not at the window's left {client.X}");

        var problems = new List<string>();
        var rects = new List<(string Id, Rect Box)>();
        foreach (var (id, label, _) in Items())
        {
            var item = AppSession.TryFind(sidebar, id);
            if (item is null)
            {
                problems.Add($"no item {id} in the sidebar: {Ui.Dump(sidebar)}");
                continue;
            }

            var current = item.Current;
            if (current.Name != label)
            {
                problems.Add($"{id} is named \"{current.Name}\", not \"{label}\"");
            }

            if (!current.IsEnabled || current.IsOffscreen || current.BoundingRectangle.IsEmpty)
            {
                problems.Add($"{id} is not an enabled, on-screen item: {Ui.Describe(item)}");
            }

            rects.Add((id, current.BoundingRectangle));
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        for (var i = 1; i < rects.Count; i++)
        {
            Assert.IsTrue(rects[i].Box.Top >= rects[i - 1].Box.Bottom - 1, $"{rects[i].Id} ({rects[i].Box}) is not below {rects[i - 1].Id} ({rects[i - 1].Box})");
        }

        foreach (var (id, r) in rects)
        {
            Assert.IsTrue(r.Left >= box.Left - 1 && r.Right <= box.Right + 1, $"{id} {r} is not inside the sidebar {box}");
        }

        var history = rects.Single(r => r.Id == "History").Box;
        var settings = rects.Single(r => r.Id == "Settings").Box;
        var about = rects.Single(r => r.Id == "About").Box;
        var topGaps = rects.Take(6).Zip(rects.Skip(1).Take(5), (a, b) => b.Box.Top - a.Box.Bottom).ToList();
        Assert.IsTrue(settings.Top - history.Bottom >= Math.Max(history.Height, topGaps.Max() + 1),
            $"Settings ({settings}) does not sit apart at the bottom: it is {settings.Top - history.Bottom} px below History ({history}); gaps between the top items: [{string.Join(", ", topGaps)}]");
        Assert.IsTrue(box.Bottom - about.Bottom <= 2 * about.Height, $"About ({about}) is not at the bottom of the sidebar ({box})");

        var overview = rects.Single(r => r.Id == "Overview").Box;
        var name = AppName(sidebar, overview);
        var words = string.Join(" ", name.Select(t => t.Current.Name));
        Assert.IsTrue(words.Contains("Ambysto", StringComparison.Ordinal) && words.Contains("Lean", StringComparison.Ordinal) && words.IndexOf("Ambysto", StringComparison.Ordinal) < words.LastIndexOf("Lean", StringComparison.Ordinal),
            $"the sidebar's top, above Overview, does not read \"Ambysto\" then \"Lean\" (the app name): [{words}]; sidebar {Ui.Dump(sidebar)}");

        AssertCurrent(app, "Overview", nameof(TheSidebarHoldsTheEightItemsInOrderWithExactlyOneCurrent));
    }

    // Contract "The sidebar" and "The content": each item opens its view in the content, the only view shown, and becomes
    // the one current item; Overview brings back the map of the scan (the start screen's Scan when there is none). After a
    // scan of a small folder.
    [TestMethod]
    [TestCategory("Console")]
    public void EachSidebarItemOpensItsViewAndBecomesTheCurrentItem()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        foreach (var (id, _, viewId) in Items().Where(i => i.ViewId is not null))
        {
            Ui.Click(Ui.SidebarItem(app, id));
            Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, viewId!), Ui.ElementTimeout), $"the {id} item did not open {viewId}");
            var others = ViewIds.Where(v => v != viewId && Shown(app, v)).ToList();
            Assert.AreEqual(0, others.Count, $"with {viewId} open, other views are shown too: {string.Join(", ", others)}");
            var map = AppSession.TryFind(app.Window(Ui.WindowTimeout), "Map");
            Assert.IsTrue(map is null || map.Current.IsOffscreen, $"the map is still shown beside {viewId}");
            AssertCurrent(app, id, nameof(EachSidebarItemOpensItsViewAndBecomesTheCurrentItem) + "-" + id);

            Ui.Click(Ui.SidebarItem(app, "Overview"));
            Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app, viewId!), Ui.ElementTimeout), $"{viewId} is still shown after the Overview item");
            Assert.IsTrue(AppSession.WaitUntil(() => Shown(app, "Map") && Ui.HasTile(Ui.Map(app), "alpha"), Ui.ElementTimeout), $"Overview after {viewId} does not show the map of the scan");
        }

        AssertCurrent(app, "Overview", nameof(EachSidebarItemOpensItsViewAndBecomesTheCurrentItem) + "-Overview");
    }

    // ---- the start state -----------------------------------------------------------------------------------------------

    // Contract "Overview, 1. Start": before any scan, Overview shows the Scan button (a 160 px circle labelled Text.Scan, in
    // the middle of the content), under it the line Text.ScanDriveOrFolder(drive) with the link ScanFolder, and DriveCards
    // with one DriveCard per fixed drive (DriveInfo, DriveType Fixed), in drive-letter order, named "<letter>: <label>"
    // ("<letter>: Local Disk" without a label),
    // each showing "<free> free of <total>". No removable, network or CD drive has a card. No tile is drawn.
    [TestMethod]
    [TestCategory("Console")]
    public void TheStartStateHasScanScanFolderAndOneCardPerFixedDrive()
    {
        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        var scan = AppSession.Find(window, "Scan", Ui.ElementTimeout);
        var current = scan.Current;
        Assert.AreEqual(ControlType.Button, current.ControlType, "Scan is a button");
        Assert.AreEqual(Text.Scan, current.Name, "Scan's label");
        Assert.IsTrue(current.IsEnabled, "Scan is disabled at the start");
        var r = current.BoundingRectangle;
        Assert.IsTrue(Math.Abs(r.Width - Px(app, 160)) <= Px(app, 6) && Math.Abs(r.Height - Px(app, 160)) <= Px(app, 6),
            $"Scan is {r.Width} x {r.Height} px, not the 160 px circle ({Px(app, 160)} px at this scale)");
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var client = WindowOnScreen.Client(Handle(app));
        var middle = (sidebar.Right + client.X + client.Width) / 2;
        Assert.IsTrue(Math.Abs(r.Left + (r.Width / 2) - middle) <= (client.X + client.Width - sidebar.Right) * 0.05,
            $"Scan's centre {r.Left + (r.Width / 2)} is not in the middle of the content ({middle})");

        var link = AppSession.Find(window, "ScanFolder", Ui.ElementTimeout);
        Assert.IsTrue(link.Current.IsEnabled && !link.Current.IsOffscreen, "ScanFolder is not an enabled, on-screen link: " + Ui.Describe(link));
        Assert.IsTrue(link.TryGetCurrentPattern(InvokePattern.Pattern, out _), "ScanFolder cannot be invoked: " + Ui.Describe(link));
        Assert.IsTrue(link.Current.BoundingRectangle.Top >= r.Bottom - 1, $"ScanFolder ({link.Current.BoundingRectangle}) is not under Scan ({r})");
        var line = Ui.Copy("ScanDriveOrFolder", Ui.ProfileDrive());
        // The line may be one text, or a text and the link as runs ("Scan C: or " and "scan a folder"): adjacent names joined.
        var names = Ui.Names(window);
        Assert.IsTrue(names.Any(n => n.Contains(line, StringComparison.Ordinal)) || string.Concat(names).Contains(line, StringComparison.Ordinal)
            || string.Join(" ", names).Contains(line, StringComparison.Ordinal), $"no element shows the line \"{line}\": {Ui.Dump(window)}");

        var expected = FixedDrives().Select(CardName).ToList();
        var cards = Cards(app);
        CollectionAssert.AreEqual(expected, cards.Select(c => c.Current.Name.TrimEnd()).ToList(),
            $"drive cards [{string.Join(" | ", cards.Select(c => c.Current.Name))}], fixed drives [{string.Join(" | ", expected)}]");
        foreach (var (card, drive) in cards.Zip(FixedDrives()))
        {
            var freeOf = Ui.Names(card).Select(n => Regex.Match(n, @"^(?<free>.+) free of (?<total>.+)$")).FirstOrDefault(m => m.Success);
            Assert.IsNotNull(freeOf, $"the card of {Letter(drive)} has no \"<free> free of <total>\": {Ui.Dump(card)}");
            Assert.AreEqual(ByteUnits.Format(drive.TotalSize), freeOf.Groups["total"].Value, $"the total on the card of {Letter(drive)}");
            var free = new[] { drive.AvailableFreeSpace, drive.TotalFreeSpace, new DriveInfo(drive.Name).AvailableFreeSpace }.Select(b => ByteUnits.Format(b)).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(free.Contains(freeOf.Groups["free"].Value), $"the free space on the card of {Letter(drive)} is \"{freeOf.Groups["free"].Value}\", not one of [{string.Join(", ", free)}]");
        }

        Assert.AreEqual(0, Tiles(app).Count, "tiles are drawn before any scan");
        Console.WriteLine("fixed drives: " + string.Join(", ", FixedDrives().Select(d => $"{d.Name} {d.DriveFormat} \"{d.VolumeLabel}\"")) + "; others: "
            + string.Join(", ", DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Fixed).Select(d => $"{d.Name} {d.DriveType}")));
    }

    // Contract "Overview, 1. Start": only the profile drive's card is enabled and it says Text.ThisDriveIsScanned; every other
    // card is disabled and says Text.OtherDrivesAfterV1. A real left click on each other card, and Invoke where it is
    // offered, starts no scan (mutation z2 of the contract: a disabled card that starts a scan).
    [TestMethod]
    [TestCategory("Console")]
    public void OnlyTheProfileDrivesCardIsEnabledAndTheOthersComeAfterV1()
    {
        if (FixedDrives().Count < 2)
        {
            Assert.Inconclusive("this machine has one fixed drive: there is no other drive's card to check");
        }

        using var app = AppSession.Start();
        var profile = Ui.ProfileDrive();
        var scanned = Ui.Copy("ThisDriveIsScanned");
        var afterV1 = Ui.Copy("OtherDrivesAfterV1");
        var cards = Cards(app);
        var problems = new List<string>();
        foreach (var card in cards)
        {
            var name = card.Current.Name;
            var isProfile = name.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
            if (isProfile && (!card.Current.IsEnabled || !Carries(card, scanned)))
            {
                problems.Add($"the profile drive's card \"{name}\": enabled {card.Current.IsEnabled}, says \"{scanned}\" {Carries(card, scanned)}: {Ui.Dump(card)}");
            }

            if (!isProfile && (card.Current.IsEnabled || !Carries(card, afterV1) || Carries(card, scanned)))
            {
                problems.Add($"the card \"{name}\": enabled {card.Current.IsEnabled}, says \"{afterV1}\" {Carries(card, afterV1)}, says \"{scanned}\" {Carries(card, scanned)}: {Ui.Dump(card)}");
            }
        }

        Assert.AreEqual(1, cards.Count(c => c.Current.Name.StartsWith(profile, StringComparison.OrdinalIgnoreCase)), "cards of the profile's drive " + profile);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));

        var handle = Handle(app);
        foreach (var card in cards.Where(c => !c.Current.Name.StartsWith(profile, StringComparison.OrdinalIgnoreCase)))
        {
            var name = card.Current.Name;
            var r = card.Current.BoundingRectangle;
            WindowOnScreen.Settle(handle, AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
            Mouse.LeftClickAt(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
            AssertNoScanStarts(app, $"a left click on the disabled card \"{name}\"");
            if (card.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                try
                {
                    ((InvokePattern)invoke).Invoke();
                }
                catch (ElementNotEnabledException)
                {
                    // A disabled card refuses Invoke: what the contract wants.
                }

                AssertNoScanStarts(app, $"Invoke on the disabled card \"{name}\"");
            }
        }
    }

    // ---- the scan and the map ------------------------------------------------------------------------------------------

    /// <summary>A tree whose walk lasts seconds with --scan-delay: slow (a chain of 30 nested folders, a file in each), and
    /// three folders read at once: alpha (a video), beta (an archive), gamma; plus delta.bin at the root (a file has no
    /// zone).</summary>
    private static Fixture SlowTree()
    {
        var fixture = new Fixture("ui-zones", Fixture.ProfileBaseFolder());
        var chain = "slow";
        for (var level = 0; level < 30; level++)
        {
            fixture.File(chain + @"\f.bin", 8 * KB);
            chain += @"\l" + level.ToString("D2", CultureInfo.InvariantCulture);
        }

        fixture.File(@"alpha\a.mp4", 400 * KB);
        fixture.File(@"beta\b.zip", 300 * KB);
        fixture.File(@"gamma\c.bin", 200 * KB);
        fixture.File("delta.bin", 100 * KB);
        return fixture;
    }

    private static readonly string[] SlowZones = ["slow", "alpha", "beta", "gamma"];

    /// <summary>A tile of the map now, retried while the map is redrawn (the zones are redrawn up to 4 times a second).</summary>
    private static T OnATile<T>(AppSession app, Func<AutomationElement, T> act)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var tile = Tiles(app).FirstOrDefault(t => t.Current.Name.StartsWith("alpha, ", StringComparison.Ordinal)) ?? Tiles(app).First();
                return act(tile);
            }
            catch (Exception e) when (attempt < 10 && e is ElementNotAvailableException or InvalidOperationException)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }

    // Contract "Overview, 2. Scanning" with the QA argument --scan-delay: while the scan runs, the map (Map) already shows
    // tiles, one per top-level folder reached (named "<name>, <size>"; no tile for a file or a folder not reached), the
    // progress line ScanProgress reads "Scanning <folder> \u2014 <n> files, <size>" (a folder scan: no total, no percent), there is
    // no text list of folder totals ("<name>: <size>"), and the map is display-only: clicking a tile opens no view and does
    // not go into it, and its menu offers no enabled Move. Then the scan ends on its own: "3. Scanned" (the full map with the
    // file tile too, Scan labelled Text.ScanAgain).
    [TestMethod]
    [TestCategory("Console")]
    public void DuringADelayedScanTheMapShowsTheZonesAndIsDisplayOnly()
    {
        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        var clock = Stopwatch.StartNew();
        var window = app.Window(Ui.WindowTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Tiles(app).Count > 0, TimeSpan.FromSeconds(15)),
            $"no tile in the map while the delayed scan runs (running {Ui.ScanRunning(app)}, {Tiles(app).Count} tiles, progress {ProgressDump(app)})");
        Console.WriteLine($"first tiles {clock.Elapsed.TotalSeconds:F1} s after launch");

        // The tiles are zones: top-level folders only, one each, named "<name>, <size>".
        var names = OnATile(app, _ => Tiles(app).Select(t => t.Current.Name).ToList());
        foreach (var name in names)
        {
            Assert.IsTrue(SlowZones.Any(z => name.StartsWith(z + ", ", StringComparison.Ordinal)), $"during the scan a tile is not a top-level folder's zone: \"{name}\"; tiles [{string.Join(" | ", names)}]");
        }

        Assert.AreEqual(names.Count, names.Select(n => n[..n.IndexOf(',', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Count(), "two tiles for one zone: " + string.Join(" | ", names));

        // The progress line of a folder scan.
        string? line = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (line = ScanningLine(app)) is not null, TimeSpan.FromSeconds(5)), "ScanProgress shows no Scanning line: " + ProgressDump(app));
        var pattern = "^Scanning " + Regex.Escape(fixture.Path) + " " + Dash + @" [0-9][0-9,.\u00a0\u202f ]* files?, [0-9][0-9,.]* (bytes|KB|MB|GB|TB)$";
        Assert.IsTrue(Regex.IsMatch(line!, pattern), $"the progress line of a folder scan is \"{line}\", not \"Scanning <folder> {Dash} <n> files, <size>\" (no total, no percent)");
        Assert.IsFalse(line!.Contains("::", StringComparison.Ordinal), $"the progress line of a folder scan holds \"::\": \"{line}\"");
        var totals = Ui.Names(window).Where(n => SlowZones.Any(z => n.StartsWith(z + ": ", StringComparison.Ordinal))).ToList();
        Assert.AreEqual(0, totals.Count, "a text list of folder totals is shown during the scan: " + string.Join(" | ", totals));

        // Display-only: a click opens no view and goes into nothing.
        var crumbsBefore = string.Join(" > ", AppSession.FindAll(window, "Breadcrumb").Select(c => c.Current.Name));
        var clicked = OnATile(app, tile =>
        {
            var name = tile.Current.Name;
            Ui.Click(tile);
            return name;
        });
        Assert.IsFalse(AppSession.WaitUntil(() => ShownView(app) is not null || string.Join(" > ", AppSession.FindAll(app.Window(Ui.WindowTimeout), "Breadcrumb").Select(c => c.Current.Name)) != crumbsBefore, TimeSpan.FromSeconds(2)),
            $"clicking the tile \"{clicked}\" during the scan did something: view {ShownView(app) ?? "none"}, breadcrumbs [{string.Join(" > ", AppSession.FindAll(app.Window(Ui.WindowTimeout), "Breadcrumb").Select(c => c.Current.Name))}] (were [{crumbsBefore}])");

        // Its menu offers no enabled Move.
        var moveEnabled = OnATile(app, tile =>
        {
            Ui.Focus(tile);
            Keys.PressWithShift(Keys.F10);
            try
            {
                var move = AppSession.WaitFor(() => app.FindInAnyWindow("MoveToRecycleBin"), TimeSpan.FromSeconds(2));
                return move is not null && move.Current.IsEnabled;
            }
            finally
            {
                Keys.Press(Keys.Escape);
                AppSession.WaitUntil(() => app.FindInAnyWindow("MoveToRecycleBin") is null, TimeSpan.FromSeconds(3));
            }
        });
        Assert.IsFalse(moveEnabled, "during the scan a tile's menu offers an enabled Move to Recycle Bin");
        Assert.IsTrue(Ui.ScanRunning(app), $"precondition: the scan ended ({clock.Elapsed.TotalSeconds:F1} s) before the display-only checks were done: slow it down more");

        // 3. Scanned.
        var map = Ui.WaitForScan(app);
        foreach (var name in SlowZones.Append("delta.bin"))
        {
            Assert.IsTrue(Ui.HasTile(map, name), $"the full map after the scan has no tile for {name}: [{string.Join(" | ", Ui.TileNames(map))}]");
        }

        Assert.AreEqual(Ui.Copy("ScanAgain"), AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout).Current.Name, "Scan's label after the scan");
    }

    // Contract "Overview, 2. Scanning": during a delayed scan, a zone that is done is filled with its type-group colour
    // (alpha, a folder holding one video: the video tone #b0adff of ASP-12, sampled at its tile's centre) and a zone still
    // being read (slow, a chain of 30 folders) is LeanPanel hatched with LeanDim lines.
    [TestMethod]
    [TestCategory("Console")]
    public void DuringTheScanADoneZoneHasItsColourAndAZoneBeingReadIsHatched()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the contract hands every colour to the system");
        }

        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        AutomationElement? Tile(string zone) => Tiles(app).FirstOrDefault(t => t.Current.Name.StartsWith(zone + ", ", StringComparison.Ordinal));
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Tile("alpha") is not null && Tile("slow") is not null, TimeSpan.FromSeconds(15)),
            $"no tiles for alpha and slow while the delayed scan runs: [{string.Join(" | ", Tiles(app).Select(t => t.Current.Name))}]");
        var handle = Handle(app);
        WindowOnScreen.Settle(handle, AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
        Rect alpha = Rect.Empty, slow = Rect.Empty;
        ScreenCapture? capture = null;
        for (var attempt = 0; attempt < 10 && capture is null; attempt++)
        {
            try
            {
                alpha = Tile("alpha")!.Current.BoundingRectangle;
                slow = Tile("slow")!.Current.BoundingRectangle;
                capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
            }
            catch (Exception e) when (e is ElementNotAvailableException or NullReferenceException)
            {
                System.Threading.Thread.Sleep(100);
            }
        }

        Assert.IsNotNull(capture, "the zone tiles could not be read while the map was redrawn");
        Console.WriteLine($"zones during the scan: saved {capture.Save(nameof(DuringTheScanADoneZoneHasItsColourAndAZoneBeingReadIsHatched))}; alpha {alpha}, slow {slow}");
        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended before the capture");
        var half = Px(app, 4);
        var (x, y) = ((int)(alpha.Left + (alpha.Width / 2)), (int)(alpha.Top + (alpha.Height / 2)));
        var centre = capture.Area(x - half, y - half, x + half + 1, y + half + 1);
        var video = Rgb.Parse("#b0adff");
        var fill = ScreenCapture.Share(centre, video, Brand.Tolerance);
        var inside = capture.Area((int)slow.Left + Px(app, 3), (int)slow.Top + Px(app, 18), (int)slow.Right - Px(app, 3), (int)slow.Bottom - Px(app, 3));
        var panel = ScreenCapture.Share(inside, Brand.Panel, Brand.NeutralTolerance);
        var dim = inside.Count(p => p.Near(Brand.Dim, Brand.Tolerance));
        var problems = new List<string>();
        if (fill < 0.8)
        {
            problems.Add($"the done zone alpha (a video) is {video} at its centre in only {fill:P0}; commonest {ScreenCapture.Commonest(centre, 2)}");
        }

        if (panel < 0.3 || dim < 20)
        {
            problems.Add($"the zone being read, slow, is {Brand.Panel} (LeanPanel) in {panel:P0} with {dim} {Brand.Dim} (LeanDim) pixels: not hatched panel; commonest {ScreenCapture.Commonest(inside)}");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "Overview, 3. Scanned": after a scan, the map with breadcrumbs (a folder scan's root is the folder), the
    // Scan button labelled Text.ScanAgain at the top right beside the volume card, ScanFolder beside it, the progress line
    // gone (no Scanning line, no Cancel running), and no Not measured tile for a folder scan.
    [TestMethod]
    [TestCategory("Console")]
    public void AfterAScanTheMapIsShownAndScanReadsScanAgainBesideTheVolumeCard()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        var map = Ui.WaitForScan(app);
        var window = app.Window(Ui.WindowTimeout);
        foreach (var name in new[] { "alpha", "beta", "gamma.bin" })
        {
            Assert.IsTrue(Ui.HasTile(map, name), $"no tile for {name}: [{string.Join(" | ", Ui.TileNames(map))}]");
        }

        Assert.IsFalse(Ui.TileNames(map).Any(n => n.StartsWith(Text.NotMeasured, StringComparison.Ordinal)), "a folder scan has a Not measured tile");
        var crumbs = AppSession.FindAll(window, "Breadcrumb").Select(c => c.Current.Name).ToList();
        Assert.IsTrue(crumbs.Count > 0 && string.Equals(crumbs[0], fixture.Path, StringComparison.OrdinalIgnoreCase), $"the map's root is not the folder: breadcrumbs [{string.Join(" > ", crumbs)}]");

        var scan = AppSession.Find(window, "Scan", Ui.ElementTimeout);
        Assert.AreEqual(Ui.Copy("ScanAgain"), scan.Current.Name, "Scan's label after a scan");
        Assert.IsTrue(scan.Current.IsEnabled, "Scan again is disabled after the scan");
        var card = AppSession.Find(window, "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle;
        var s = scan.Current.BoundingRectangle;
        Assert.IsTrue(s.Left >= card.Right - 1 && s.Top < card.Bottom && s.Bottom > card.Top, $"Scan again ({s}) is not beside the volume card ({card}), to its right");
        Assert.IsTrue(s.Height < Px(app, 100), $"Scan again ({s}) is still the start screen's circle, not a normal button");
        var folder = AppSession.Find(window, "ScanFolder", Ui.ElementTimeout).Current.BoundingRectangle;
        Assert.IsTrue(folder.Top < s.Bottom && folder.Bottom > s.Top, $"ScanFolder ({folder}) is not beside Scan again ({s})");
        Assert.IsNull(ScanningLine(app), "a Scanning line is still shown after the scan: " + ProgressDump(app));
        Assert.IsFalse(Ui.ScanRunning(app), "Cancel is enabled after the scan");
    }

    // Contract "Overview, 1. Start" and "2. Scanning": Scan scans the profile's drive (here slowed by --scan-delay and
    // cancelled after a few seconds; it only reads): the progress line reads "Scanning C: \u2014 <n> files, <size> of about
    // <used> (<p> %)" (fix round 2: one colon, never "C::"), the percent never above 99 while the scan runs.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanScansTheProfileDriveWithAnAboutPercentLine()
    {
        using var app = AppSession.Start("--scan-delay", "30");
        var window = app.Window(Ui.WindowTimeout);
        Ui.Click(AppSession.Find(window, "Scan", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(10)), "Scan did not start a scan");
        var drive = Ui.ProfileDrive();
        var pattern = "^Scanning " + Regex.Escape(drive) + " " + Dash + @" [0-9][0-9,.\u00a0\u202f ]* files?, .+ of about .+ \((?<p>[0-9]+) %\)$";
        var seen = new List<string>();
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(4) && Ui.ScanRunning(app))
            {
                if (ScanningLine(app) is { } line && (seen.Count == 0 || seen[^1] != line))
                {
                    seen.Add(line);
                }

                System.Threading.Thread.Sleep(200);
            }
        }
        finally
        {
            if (AppSession.TryFind(app.Window(Ui.WindowTimeout), "Cancel") is { } cancel && cancel.Current.IsEnabled)
            {
                Ui.Click(cancel);
            }
        }

        Assert.IsTrue(seen.Count > 0, "no Scanning line during the drive scan: " + ProgressDump(app));
        foreach (var line in seen)
        {
            var match = Regex.Match(line, pattern);
            Assert.IsTrue(match.Success, $"the progress line of the drive scan is \"{line}\", not \"Scanning {drive} {Dash} <n> files, <size> of about <used> (<p> %)\"");
            Assert.IsFalse(line.Contains("::", StringComparison.Ordinal), $"the progress line of the drive scan holds \"::\": \"{line}\"");
            Assert.IsTrue(int.Parse(match.Groups["p"].Value, CultureInfo.InvariantCulture) <= 99, $"the percent is above 99 while the scan runs: \"{line}\"");
        }

        Assert.IsTrue(AppSession.WaitUntil(() => !Ui.ScanRunning(app), TimeSpan.FromSeconds(5)), "the drive scan did not stop on Cancel");
    }

    // ---- the terminal readout (623b84c) ---------------------------------------------------------------------------------

    private static readonly string[] ReadoutIds = ["ScanPrompt", "ScanReading", "ScanMeter", "ScanLog", "ScanProgress", "Map"];

    private static readonly Regex Percent = new(@"(?<p>[0-9]+)\s?%", RegexOptions.CultureInvariant);

    /// <summary>The element <paramref name="id"/> of the readout, on screen, or null now.</summary>
    private static AutomationElement? Readout(AppSession app, string id)
    {
        try
        {
            return AppSession.TryFind(app.Window(Ui.WindowTimeout), id) is { } e && !e.Current.IsOffscreen ? e : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The text of a readout element: its name and those of everything in it, joined by new lines.</summary>
    private static string ReadoutText(AppSession app, string id) => Readout(app, id) is { } e ? string.Join("\n", Ui.Names(e).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)) : string.Empty;

    /// <summary>The meter's percent now, or null when it shows none.</summary>
    private static int? MeterPercent(AppSession app) =>
        Percent.Match(ReadoutText(app, "ScanMeter")) is { Success: true } m ? int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture) : null;

    // Contract "2. Scanning", the terminal readout, during a delayed folder scan: ScanPrompt reads "$ lean scan <folder>",
    // ScanReading "reading <path>", ScanMeter is there with no percent (a folder scan claims no total), ScanLog holds at most
    // 6 lines, each "done …" (a done zone) or ".... …" (a zone being read), and ScanProgress is a polite live region.
    // Sampled every 200 ms while the scan runs. The LiveSetting is read through native UI Automation (NativeUia).
    [TestMethod]
    [TestCategory("Console")]
    public void TheTerminalReadoutDuringAFolderScan()
    {
        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Readout(app, "ScanPrompt") is not null, TimeSpan.FromSeconds(15)),
            $"no ScanPrompt while the delayed scan runs (running {Ui.ScanRunning(app)})");
        var progress = Readout(app, "ScanProgress");
        Assert.IsNotNull(progress, "no ScanProgress while the scan runs");
        var live = NativeUia.LiveSettingOf(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle), "ScanProgress");

        var problems = new List<string>();
        var samples = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(4) && Ui.ScanRunning(app))
        {
            samples++;
            var prompt = ReadoutText(app, "ScanPrompt");
            if (!prompt.Split('\n').Any(l => l.TrimEnd().StartsWith("$ lean scan " + fixture.Path, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"ScanPrompt reads \"{prompt}\", not \"$ lean scan {fixture.Path}\"");
            }

            var reading = ReadoutText(app, "ScanReading");
            if (!reading.StartsWith("reading ", StringComparison.Ordinal))
            {
                problems.Add($"ScanReading reads \"{reading}\", not \"reading <path>\"");
            }

            if (Readout(app, "ScanMeter") is null)
            {
                problems.Add("no ScanMeter on screen");
            }
            else if (MeterPercent(app) is { } p)
            {
                problems.Add($"the meter of a folder scan shows a percent ({p} %): \"{ReadoutText(app, "ScanMeter")}\"");
            }

            var log = ReadoutText(app, "ScanLog").Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(l => l.Split('\r', StringSplitOptions.RemoveEmptyEntries)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (Readout(app, "ScanLog") is null)
            {
                problems.Add("no ScanLog on screen");
            }
            else if (log.Count > 6 || log.Any(l => !l.StartsWith("done", StringComparison.Ordinal) && !l.StartsWith("....", StringComparison.Ordinal)))
            {
                problems.Add($"ScanLog holds {log.Count} lines, not at most 6 of \"done …\" or \".... …\": [{string.Join(" | ", log)}]");
            }

            System.Threading.Thread.Sleep(200);
        }

        Assert.IsTrue(samples >= 3, $"precondition: only {samples} samples before the scan ended");
        if (live != 1)
        {
            problems.Add($"ScanProgress is not a polite live region (LiveSetting {live?.ToString(CultureInfo.InvariantCulture) ?? "none"}, polite is 1)");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Distinct()));
    }

    // Contract "2. Scanning", the meter of a drive scan (Scan, slowed by --scan-delay, cancelled after a few seconds; it only
    // reads): ScanMeter shows a percent that never goes down and never exceeds 99 while the scan runs.
    [TestMethod]
    [TestCategory("Console")]
    public void TheMeterOfADriveScanRisesAndNeverExceeds99()
    {
        using var app = AppSession.Start("--scan-delay", "30");
        Ui.Click(AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(10)), "Scan did not start a scan");
        var seen = new List<int>();
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(6) && Ui.ScanRunning(app))
            {
                if (MeterPercent(app) is { } p)
                {
                    seen.Add(p);
                }

                System.Threading.Thread.Sleep(200);
            }
        }
        finally
        {
            if (AppSession.TryFind(app.Window(Ui.WindowTimeout), "Cancel") is { } cancel && cancel.Current.IsEnabled)
            {
                Ui.Click(cancel);
            }
        }

        Assert.IsTrue(seen.Count > 0, "the meter of the drive scan showed no percent: \"" + ReadoutText(app, "ScanMeter") + "\"");
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.IsTrue(seen[i] >= seen[i - 1], $"the meter's percent went down: {string.Join(", ", seen)}");
        }

        Assert.IsTrue(seen.Max() <= 99, $"the meter's percent exceeds 99 while the scan runs: {string.Join(", ", seen)}");
        Console.WriteLine("meter percents: " + string.Join(", ", seen.Distinct()));
    }

    // Contract "The neon" is behind a build switch that is off until the brand PR is merged: with it off, during a delayed
    // folder scan no LeanTone pixel is on the map (no sweep line) or in the readout outside the meter (its lit cells) and the
    // prompt line (its cursor); the meter does show lit LeanTone cells. The glow itself is not tested.
    [TestMethod]
    [TestCategory("Console")]
    public void WithTheNeonOffTheToneIsOnlyInTheMetersCellsAndTheCursor()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the contract hands every colour to the system");
        }

        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Readout(app, "ScanMeter") is not null && Tiles(app).Count > 0, TimeSpan.FromSeconds(15)),
            "no meter and tiles while the delayed scan runs");
        var handle = Handle(app);
        WindowOnScreen.Settle(handle, AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
        var rects = ReadoutIds.ToDictionary(id => id, id => Readout(app, id)?.Current.BoundingRectangle ?? Rect.Empty);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"neon off: saved {capture.Save(nameof(WithTheNeonOffTheToneIsOnlyInTheMetersCellsAndTheCursor))}; " + string.Join(", ", rects.Select(r => $"{r.Key} {r.Value}")));
        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended before the capture");
        var allowed = new[] { rects["ScanMeter"], rects["ScanPrompt"] }.Where(r => !r.IsEmpty).ToList();
        var problems = new List<string>();
        foreach (var (id, r) in rects.Where(r => !r.Value.IsEmpty && r.Key is not ("ScanMeter" or "ScanPrompt")))
        {
            var pad = Px(app, 4);
            var tone = 0;
            for (var y = (int)r.Top - pad; y < (int)r.Bottom + pad; y++)
            {
                for (var x = (int)r.Left - pad; x < (int)r.Right + pad; x++)
                {
                    if (capture.Contains(x, y) && !allowed.Any(a => a.Contains(new Point(x, y))) && capture.At(x, y).Near(Brand.Coral, Brand.Tolerance))
                    {
                        tone++;
                    }
                }
            }

            if (tone > 0)
            {
                problems.Add($"{tone} LeanTone pixels in or around {id} ({r}), outside the meter and the prompt line");
            }
        }

        var meter = Inside(capture, rects["ScanMeter"]);
        if (meter.Count(p => p.Near(Brand.Coral, Brand.Tolerance)) < 4)
        {
            problems.Add($"the meter shows no lit LeanTone cell; commonest {ScreenCapture.Commonest(meter)}");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Review round 1, "With Windows animations off": the folder-scan meter shows its 6 lit cells still: two reads of the
    // meter 1 s apart light the same columns. Animations are turned off for this test only, through
    // SPI_SETCLIENTAREAANIMATION for the session (sent, not saved to the profile), before the app starts, and restored in
    // finally (Animations.Off).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void WithAnimationsOffTheFolderMetersLitCellsStayStill()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the contract hands every colour to the system");
        }

        using var animations = Animations.Off();
        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Readout(app, "ScanMeter") is not null, TimeSpan.FromSeconds(15)), "no meter while the delayed scan runs");
        var handle = Handle(app);
        WindowOnScreen.Settle(handle, AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
        string Lit()
        {
            var r = Readout(app, "ScanMeter")!.Current.BoundingRectangle;
            var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
            var columns = new List<int>();
            for (var x = (int)r.Left; x < (int)r.Right; x++)
            {
                var column = capture.Area(x, (int)r.Top, x + 1, (int)r.Bottom);
                if (column.Any(p => p.Near(Brand.Coral, Brand.Tolerance)))
                {
                    columns.Add(x - (int)r.Left);
                }
            }

            return string.Join(",", columns);
        }

        var first = Lit();
        System.Threading.Thread.Sleep(1000);
        var second = Lit();
        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended between the two reads");
        Assert.IsTrue(first.Length > 0, "precondition: the meter shows no lit LeanTone cell");
        Assert.AreEqual(first, second, "with animations off the folder meter's lit cells moved between two reads 1 s apart (lit columns, from the meter's left)");
    }

    // Review round 1: ScanProgress raises LiveRegionChanged while the scan runs (observed through native UI Automation over
    // 3 s of a delayed folder scan); polite and at most once a second are the readout test's and the contract's.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanProgressRaisesLiveRegionChangedDuringTheScan()
    {
        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Readout(app, "ScanProgress") is not null, TimeSpan.FromSeconds(15)), "no ScanProgress while the delayed scan runs");
        var window = new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle);
        var changes = NativeUia.LiveRegionChanges(window, "ScanProgress", () => System.Threading.Thread.Sleep(3000));
        if (changes is null)
        {
            Assert.Inconclusive("UI Automation would not take a LiveRegionChanged handler on ScanProgress here: the event cannot be observed");
        }

        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended during the 3 s watch");
        Assert.IsTrue(changes >= 1, "ScanProgress raised no LiveRegionChanged in 3 s of a running scan");
        Assert.IsTrue(changes <= 4, $"ScanProgress raised {changes} LiveRegionChanged events in 3 s: more than once a second");
    }

    private static List<Rgb> Inside(ScreenCapture capture, Rect r) =>
        r.IsEmpty ? [] : capture.Area((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));

    // ---- scan a folder -------------------------------------------------------------------------------------------------

    // Contract "Scan a folder": ScanFolder opens the system folder picker; a folder chosen on the profile's drive is
    // scanned with the same scanner: the map's root is the folder (its first breadcrumb), its children are tiles, and there
    // is no Not measured tile (volume level only).
    [TestMethod]
    [TestCategory("Console")]
    public void ScanFolderScansAFolderChosenOnTheProfileDrive()
    {
        using var fixture = SmallTree(Fixture.ProfileBaseFolder());
        using var app = AppSession.Start();
        try
        {
            Ui.Click(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout));
            FolderPicker.Choose(app, fixture.Path, Ui.ElementTimeout);
            var map = Ui.WaitForScan(app);
            foreach (var name in new[] { "alpha", "beta", "gamma.bin" })
            {
                Assert.IsTrue(Ui.HasTile(map, name), $"no tile for {name} after scanning the chosen folder: [{string.Join(" | ", Ui.TileNames(map))}]");
            }

            Assert.IsFalse(Ui.TileNames(map).Any(n => n.StartsWith(Text.NotMeasured, StringComparison.Ordinal)), "a folder scan has a Not measured tile");
            var crumbs = AppSession.FindAll(app.Window(Ui.WindowTimeout), "Breadcrumb").Select(c => c.Current.Name).ToList();
            Assert.IsTrue(crumbs.Count > 0 && string.Equals(crumbs[0], fixture.Path, StringComparison.OrdinalIgnoreCase), $"the map's root is not the chosen folder {fixture.Path}: breadcrumbs [{string.Join(" > ", crumbs)}]");
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // The no-scan check's own proof (pure strings): a real scan's progress and status texts say "scanning", the refusal texts
    // do not, though they start with "Scanning ".
    [TestMethod]
    public void TheNoScanCheckTellsAScanFromTheRefusalText()
    {
        Assert.IsTrue(SaysScanning(Text.ScanningLine(@"C:\Users\x", 1234, "56.7 MB")), "a folder scan's progress line is not recognised");
        Assert.IsTrue(SaysScanning(Text.ScanningLine("C:", 1234, "56.7 MB", "1.2 GB", 4)), "a drive scan's progress line is not recognised");
        Assert.IsTrue(SaysScanning("Scanning C: " + Dash + " 47,931 files, 6.86 GB of about 161.7 GB (4 %)"), "the contract's drive example is not recognised");
        Assert.IsTrue(SaysScanning(@"Scanning C:\Users\me\Videos " + Dash + " 47,931 files, 6.86 GB"), "the contract's folder example is not recognised");
        Assert.IsTrue(ScanningLineFormat.IsMatch("Scanning C: " + Dash + " 1 file, 4 KB"), "a line with one file is not recognised");
        Assert.IsFalse(ScanningLineFormat.IsMatch("Scanning C:: 24,075 files, 6.86 GB"), "the old line with \"::\" is taken for a scan's progress line");
        Assert.IsTrue(SaysScanning(Text.Scanning(@"C:\Users\x")), "the status \"Scanning <path>\" is not recognised");
        Assert.IsFalse(SaysScanning(FolderRefused()), "the refusal text is taken for a scan: " + FolderRefused());
        Assert.IsFalse(SaysScanning(Text.OtherDrivesAfterV1), "the drive card's note is taken for a scan");
        Assert.IsTrue(FolderRefused().StartsWith(Text.Scanning(string.Empty), StringComparison.Ordinal), "precondition: the refusal starts like a scan's status");
    }

    // Fix round 2, "the progress line": the copy itself, pinned once with literals (Text.ScanningLine builds what the app
    // shows): a drive "Scanning C: \u2014 <n> files, <size> of about <used> (<p> %)" and a folder "Scanning <folder> \u2014 <n> files,
    // <size>", the dash an em dash with a space each side, and never "::" (the contract's own first example had the double
    // colon). 123 files keeps the count free of a thousands separator, whatever the culture.
    [TestMethod]
    public void TheProgressLineCopyHasTheContractsShape()
    {
        var drive = Text.ScanningLine("C:", 123, "6.86 GB", "161.7 GB", 4);
        var folder = Text.ScanningLine(@"C:\Users\me\Videos", 123, "6.86 GB");
        Assert.AreEqual("Scanning C: \u2014 123 files, 6.86 GB of about 161.7 GB (4 %)", drive, "the drive scan's progress line");
        Assert.AreEqual(@"Scanning C:\Users\me\Videos " + Dash + " 123 files, 6.86 GB", folder, "the folder scan's progress line");
        Assert.IsFalse(drive.Contains("::", StringComparison.Ordinal) || folder.Contains("::", StringComparison.Ordinal), $"a progress line holds \"::\": \"{drive}\", \"{folder}\"");
    }

    // ...and on the app: after a real scan (a folder on the profile's drive) the same check does fail.
    [TestMethod]
    [TestCategory("Console")]
    public void TheNoScanCheckFailsWhenAScanDoesStart()
    {
        using var fixture = new Fixture("ui-noscan", Fixture.ProfileBaseFolder());
        fixture.File(@"inner\x.bin", 64 * KB);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        Assert.ThrowsExactly<AssertFailedException>(() => AssertNoScanStarts(app, "after a real scan"));
    }

    /// <summary>A small folder on another fixed drive (E: on the test machine), or Inconclusive.</summary>
    private static Fixture OtherDriveFolder()
    {
        var drive = OtherFixedDrive();
        if (drive is null)
        {
            Assert.Inconclusive("no fixed drive other than the profile's where the test can make a folder");
        }

        var fixture = new Fixture("ui-otherdrive", drive.Name);
        fixture.File(@"inner\x.bin", 64 * KB);
        return fixture;
    }

    // Contract "Scan a folder": a folder chosen in the picker that is not on the profile's drive is not scanned: no scan
    // starts and the progress line shows Text.FolderNotOnProfileDrive.
    [TestMethod]
    [TestCategory("Console")]
    public void ScanFolderRefusesAFolderOnAnotherDrive()
    {
        using var fixture = OtherDriveFolder();
        using var app = AppSession.Start();
        try
        {
            Ui.Click(AppSession.Find(app.Window(Ui.WindowTimeout), "ScanFolder", Ui.ElementTimeout));
            FolderPicker.Choose(app, fixture.Path, Ui.ElementTimeout);
            var refused = FolderRefused();
            Assert.IsTrue(AppSession.WaitUntil(() => ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)), Ui.ElementTimeout),
                $"after choosing {fixture.Path}, the progress line does not say \"{refused}\": {ProgressDump(app)}");
            AssertNoScanStarts(app, "after choosing " + fixture.Path, refusal: true);
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // Contract "Scan a folder", by the QA argument (--scan <folder>, the same way in as the picker's): a folder on another
    // drive is refused the same way.
    [TestMethod]
    [TestCategory("Console")]
    public void TheScanArgumentRefusesAFolderOnAnotherDrive()
    {
        using var fixture = OtherDriveFolder();
        using var app = AppSession.Start("--scan", fixture.Path);
        var refused = FolderRefused();
        Assert.IsTrue(AppSession.WaitUntil(() => ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)), Ui.ElementTimeout),
            $"--scan {fixture.Path}: the progress line does not say \"{refused}\": {ProgressDump(app)}");
        AssertNoScanStarts(app, "--scan " + fixture.Path, refusal: true);
    }

    // Fix round 2 (review round 2, MINOR "the refusal is not announced"): the refusal text is announced as the progress line
    // is, so ScanProgress raises LiveRegionChanged when a folder on another drive is refused. The progress row is collapsed
    // until it has text, so the handler listens on the window and counts the events whose sender is ScanProgress.
    [TestMethod]
    [TestCategory("Console")]
    public void RefusingAFolderOnAnotherDriveRaisesLiveRegionChangedOnTheProgressLine()
    {
        using var fixture = OtherDriveFolder();
        using var app = AppSession.Start();
        try
        {
            var window = app.Window(Ui.WindowTimeout);
            var scanFolder = AppSession.Find(window, "ScanFolder", Ui.ElementTimeout);
            var refused = FolderRefused();

            // Fix round 3, the precondition made explicit: the refusal is the very first action after start. The progress row has
            // never shown text (it is collapsed, so UI Automation gives it no element to raise an event from until it has text),
            // and no scan has run. A refusal that is announced only when the row already exists would pass any other start.
            System.Threading.Thread.Sleep(1000);
            Assert.AreEqual(0, ProgressNames(app).Count(n => n.Length > 0), "precondition: the progress line already shows text before the refusal: " + ProgressDump(app));
            Assert.IsFalse(Ui.ScanRunning(app), "precondition: a scan is running before the refusal");
            Assert.AreEqual(0, Tiles(app).Count, "precondition: a map is drawn before the refusal (a scan has run)");

            var changes = NativeUia.LiveRegionChangesAnywhere(new nint(window.Current.NativeWindowHandle), "ScanProgress", () =>
            {
                Ui.Click(scanFolder);
                FolderPicker.Choose(app, fixture.Path, Ui.ElementTimeout);
                Assert.IsTrue(AppSession.WaitUntil(() => ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)), Ui.ElementTimeout),
                    $"after choosing {fixture.Path}, the progress line does not say \"{refused}\": {ProgressDump(app)}");
                System.Threading.Thread.Sleep(1500);
            });
            if (changes is null)
            {
                Assert.Inconclusive("UI Automation would not take a LiveRegionChanged handler on the window here: the event cannot be observed");
            }

            Assert.IsTrue(changes >= 1, $"the refusal \"{refused}\" was shown on the progress line but ScanProgress raised no LiveRegionChanged (screen readers say nothing)");
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // The instrument of the test above, on a case that works today: the window-level watcher counts ScanProgress's
    // LiveRegionChanged events during a delayed scan (the element-level watcher of ScanProgressRaisesLiveRegionChangedDuringTheScan
    // sees them), so a zero in the refusal test is the app's, not the watcher's.
    [TestMethod]
    [TestCategory("Console")]
    public void TheWindowLevelWatcherHearsTheProgressLineDuringAScan()
    {
        using var fixture = SlowTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app) && Readout(app, "ScanProgress") is not null, TimeSpan.FromSeconds(15)), "no ScanProgress while the delayed scan runs");
        var window = new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle);
        var changes = NativeUia.LiveRegionChangesAnywhere(window, "ScanProgress", () => System.Threading.Thread.Sleep(3000));
        if (changes is null)
        {
            Assert.Inconclusive("UI Automation would not take a LiveRegionChanged handler on the window here: the event cannot be observed");
        }

        Assert.IsTrue(Ui.ScanRunning(app), "precondition: the scan ended during the 3 s watch");
        Assert.IsTrue(changes >= 1, "the window-level watcher heard no LiveRegionChanged from ScanProgress in 3 s of a running scan");
    }

    // Fix round 2, "every scan start refuses a folder whose volume is not the profile's volume; \\?\ paths on the profile's
    // volume are accepted": --scan with the extended-length spelling of a folder on the profile's drive starts a scan (the
    // refusal of round 1 compared the path root only and turned "\\?\C:\" away). The mount-point case (a folder on C: whose
    // volume is another one, and a mount point on another drive that is C:'s volume) needs an administrator to build; it is not
    // covered here.
    [TestMethod]
    [TestCategory("Console")]
    public void TheScanArgumentAcceptsAnExtendedLengthPathOnTheProfileDrive()
    {
        using var fixture = SmallTree(Fixture.ProfileBaseFolder());
        var extended = @"\\?\" + fixture.Path;
        using var app = AppSession.Start("--scan", extended);
        var refused = FolderRefused();
        Assert.IsTrue(AppSession.WaitUntil(() => ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)) || Ui.ScanRunning(app) || Tiles(app).Count > 0, Ui.ElementTimeout),
            $"--scan {extended}: nothing happened (no scan, no refusal): {ProgressDump(app)}");
        Assert.IsFalse(ProgressNames(app).Any(n => n.Contains(refused, StringComparison.Ordinal)), $"--scan {extended} (a folder on the profile's drive) is refused: {ProgressDump(app)}");
        var map = Ui.WaitForScan(app);
        Assert.IsTrue(Ui.HasTile(map, "alpha"), $"no tile for alpha after --scan {extended}: [{string.Join(" | ", Ui.TileNames(map))}]");
    }
}
