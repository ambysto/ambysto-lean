using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan group 5, "UI contract for phase A": the volume card at launch, the map's tiles for a scanned folder,
/// cancel, the tile context menu (with plan group 11: Move enabled on a movable tile), the system-items toggle (ASP-14:
/// inside Large and old) and keyboard reach, through the UI Automation client in the console session (docs/spec/01 Volume card, Map, Progress and
/// cancel). Nothing is ever invoked that opens the Recycle Bin, reveals a file or moves one (moves are
/// MoveAndPutBackTests').</summary>
[TestClass]
[DoNotParallelize]
public sealed class MapAndWindowTests
{
    private const long KB = ByteUnits.KB;
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The --scan-delay for the cancel tests: 20 ms after each folder batch makes the big walk take seconds.</summary>
    private const string ScanDelayMilliseconds = "20";
    private static readonly string[] ExplorerUnits = ["bytes", "KB", "MB", "GB", "TB", "PB"];
    private static Fixture? big;

    [ClassInitialize]
    public static void BuildTheBigTree(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        big = new Fixture("ui-big");
        big.ManyEmptyFiles("many", 150_000);
        big.File(@"some\a.bin", 300 * KB);
    }

    [ClassCleanup]
    public static void RemoveTheBigTree() => big?.Dispose();

    /// <summary>A small tree with known allocated sizes: alpha (300 KB + 200 KB), beta (100 KB, and a hard link of that
    /// file, counted once), gamma.bin (50 KB).</summary>
    private static Fixture SmallTree(string? baseFolder = null)
    {
        var fixture = baseFolder is null ? new Fixture("ui-small") : new Fixture("ui-small", baseFolder);
        fixture.File(@"alpha\one.bin", 300 * KB);
        fixture.File(@"alpha\two.bin", 200 * KB);
        fixture.File(@"beta\one.bin", 100 * KB);
        fixture.HardLink(@"beta\link.bin", @"beta\one.bin");
        fixture.File("gamma.bin", 50 * KB);
        return fixture;
    }

    private static string TileName(string name, long allocated) => name + ", " + ByteUnits.Format(allocated);

    /// <summary>The map once the scan has finished (ASP-14: during the scan the map shows the zones, display-only).</summary>
    private static AutomationElement WaitForMapWithTiles(AppSession app) => Ui.WaitForScan(app);

    // Contract: volume card at launch (docs/spec/01 Volume card, shown before any scan): three texts named
    // "<label>: <value>", and the OpenRecycleBin button exists (never clicked), inside the volume card beside "In the
    // Recycle Bin" (ASP-14). Criterion 11: launch to the volume card
    // ≤ 2 s on a real PC; here informative, ≤ 5 s in the VM, and the time is printed.
    [TestMethod]
    [TestCategory("Console")]
    public void VolumeCardIsShownAtLaunchWithinFiveSeconds()
    {
        using var app = AppSession.Start();
        var window = app.Window(WindowTimeout);
        var card = AppSession.Find(window, "VolumeCard", ElementTimeout);
        var shownAfter = app.SinceStart.Elapsed;
        Console.WriteLine($"launch to volume card: {shownAfter.TotalMilliseconds:F0} ms (informative in the VM; criterion 11 asks 2 s on a real PC)");

        AssertCardText(card, "UsedByThisVolume", Text.UsedByThisVolume, allowUnknown: false);
        AssertCardText(card, "FreeRightNow", Text.FreeRightNow, allowUnknown: false);
        AssertCardText(card, "InTheRecycleBin", Text.InTheRecycleBin, allowUnknown: true);
        var open = AppSession.Find(window, "OpenRecycleBin", ElementTimeout);
        Assert.AreEqual(ControlType.Button, open.Current.ControlType, "OpenRecycleBin is a button");
        Assert.AreEqual(Text.OpenRecycleBin, open.Current.Name);
        Assert.IsTrue(Ui.IsWithin(open, card), "OpenRecycleBin is not inside the volume card (ASP-14): " + Ui.Describe(open));
        var bin = AppSession.Find(card, "InTheRecycleBin", ElementTimeout).Current.BoundingRectangle;
        var o = open.Current.BoundingRectangle;
        Assert.IsTrue(o.Left >= bin.Right - 1 && o.Top < bin.Bottom && o.Bottom > bin.Top, $"OpenRecycleBin ({o}) is not beside In the Recycle Bin ({bin})");
        Assert.IsTrue(shownAfter <= TimeSpan.FromSeconds(5), $"the volume card took {shownAfter.TotalSeconds:F1} s to appear");
    }

    private static void AssertCardText(AutomationElement card, string automationId, string label, bool allowUnknown)
    {
        var text = AppSession.Find(card, automationId, ElementTimeout);
        var name = text.Current.Name;
        var prefix = label + ": ";
        Assert.IsTrue(name.StartsWith(prefix, StringComparison.Ordinal), $"{automationId} is named \"{name}\", expected \"{prefix}<value>\"");
        var value = name[prefix.Length..];
        Assert.IsFalse(string.IsNullOrWhiteSpace(value), automationId + " has no value");
        // Contract: the bin's value is Text.Unknown when the shell cannot read the bin; the other two are always sizes.
        if (allowUnknown && value == Text.Unknown)
        {
            return;
        }

        // A ByteUnits.Format value: "<number> <unit>", the unit one of Explorer's.
        var parts = value.Split(' ');
        Assert.AreEqual(2, parts.Length, $"{automationId}: \"{value}\" is not a formatted size");
        Assert.IsTrue(double.TryParse(parts[0], NumberStyles.Float, CultureInfo.CurrentCulture, out _) || double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"{automationId}: \"{parts[0]}\" is not a number");
        CollectionAssert.Contains(ExplorerUnits, parts[1], $"{automationId}: unit \"{parts[1]}\"");
    }

    // Contract: the Scan button and the Status text exist at launch; no scan runs, so Cancel is not enabled (ASP-14: it
    // follows the progress line during a scan, and may be absent before one). Cancel's label is checked while a scan runs
    // (CancelShowsScanCancelledWithinOneSecond).
    [TestMethod]
    [TestCategory("Console")]
    public void ScanAndStatusExistAtLaunchAndNoCancelIsEnabled()
    {
        using var app = AppSession.Start();
        var window = app.Window(WindowTimeout);
        var scan = AppSession.Find(window, "Scan", ElementTimeout);
        Assert.AreEqual(ControlType.Button, scan.Current.ControlType);
        Assert.IsTrue(scan.Current.IsEnabled);
        Assert.IsNotNull(AppSession.TryFind(window, "Status"), "no Status text");
        Assert.AreEqual(Text.Scan, scan.Current.Name, "Scan label");
        Assert.IsFalse(AppSession.WaitUntil(() => Ui.ScanRunning(app), TimeSpan.FromSeconds(2)), "Cancel is enabled while no scan runs");
    }

    // Contract: --scan <folder>; the map shows one tile per top child named "<name>, <Format(allocated)>", HelpText
    // starting with the full path (docs/spec/01 Map: area by allocated size; walk rule 4: a hard link counted once).
    [TestMethod]
    [TestCategory("Console")]
    public void MapShowsATilePerTopChildWithSizeAndPath()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForMapWithTiles(app);
        // Expected names from an independent allocation oracle (FILE_STANDARD_INFO.AllocationSize per file: whole clusters,
        // so 50 KB of data is 52.0 KB on 4 KB clusters), never from the logical sizes written.
        var alpha = fixture.AllocatedBytes(@"alpha\one.bin") + fixture.AllocatedBytes(@"alpha\two.bin");
        var beta = fixture.AllocatedBytes(@"beta\one.bin");
        var gamma = fixture.AllocatedBytes("gamma.bin");
        var expected = new[] { TileName("alpha", alpha), TileName("beta", beta), TileName("gamma.bin", gamma) };
        Assert.IsTrue(AppSession.WaitUntil(() => expected.All(e => AppSession.FindAll(map, "Tile").Any(t => t.Current.Name == e)), ElementTimeout),
            "tiles: [" + string.Join(" | ", AppSession.FindAll(map, "Tile").Select(t => t.Current.Name)) + "]; expected " + string.Join(", ", expected));

        var tiles = AppSession.FindAll(map, "Tile");
        foreach (var (name, child) in new[] { (expected[0], "alpha"), (expected[1], "beta"), (expected[2], "gamma.bin") })
        {
            var tile = tiles.Single(t => t.Current.Name == name);
            var fullPath = Path.Combine(fixture.Path, child);
            var help = tile.Current.HelpText;
            Assert.IsTrue(help.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase), $"{name}: HelpText \"{help}\" does not start with {fullPath}");
            var rest = help[fullPath.Length..];
            Assert.IsTrue(rest.Length == 0 || rest.StartsWith('\n') || rest.StartsWith("\r\n", StringComparison.Ordinal), $"{name}: after the path comes a new line, not \"{rest}\"");
        }

        Assert.IsFalse(tiles.Any(t => t.Current.Name.StartsWith(Text.NotMeasured, StringComparison.Ordinal)), "a folder scan has no Not measured tile (volume level only)");
        Assert.IsFalse(tiles.Any(t => t.Current.Name == TileName("beta", 2 * beta)), "the hard link was counted twice");
    }

    // Contract: --quit-after-scan exits with code 0 when the scan completes.
    [TestMethod]
    [TestCategory("Console")]
    public void QuitAfterScanExitsWithZeroWhenTheScanCompletes()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path, "--quit-after-scan");
        Assert.AreEqual(0, app.ExitCode(TimeSpan.FromSeconds(60)));
    }

    // Contract and docs/spec/01 Progress and cancel: Cancel stops the scan within 1 s; the status shows
    // Text.ScanCancelled; Cancel is enabled only while a scan runs.
    [TestMethod]
    [TestCategory("Console")]
    public void CancelShowsScanCancelledWithinOneSecond()
    {
        // --scan-delay (QA argument) waits after each folder batch, so the 150,000-entry walk takes several seconds and the
        // cancel always lands mid-walk: the scan must not end first.
        using var app = AppSession.Start("--scan", big!.Path, "--scan-delay", ScanDelayMilliseconds);
        var window = app.Window(WindowTimeout);
        var cancel = AppSession.Find(window, "Cancel", ElementTimeout);
        var status = AppSession.Find(window, "Status", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => cancel.Current.IsEnabled, TimeSpan.FromSeconds(10)), "Cancel never became enabled while the delayed scan ran (status: \"" + status.Current.Name + "\")");
        Assert.AreEqual(Text.Cancel, cancel.Current.Name, "Cancel label");

        var clock = Stopwatch.StartNew();
        ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var cancelled = AppSession.WaitUntil(() => status.Current.Name.StartsWith(Text.ScanCancelled, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        var took = clock.Elapsed;
        Assert.IsTrue(cancelled, "status after cancel: \"" + status.Current.Name + "\"");
        Assert.IsTrue(took <= TimeSpan.FromSeconds(1), $"the status showed {Text.ScanCancelled} after {took.TotalMilliseconds:F0} ms");
        Assert.IsTrue(AppSession.WaitUntil(() => !Ui.ScanRunning(app), TimeSpan.FromSeconds(2)), "Cancel stays enabled after the scan was cancelled");
    }

    // Contract: --quit-after-scan exits with code 3 when the scan was cancelled.
    [TestMethod]
    [TestCategory("Console")]
    public void QuitAfterScanExitsWithThreeWhenCancelled()
    {
        using var app = AppSession.Start("--scan", big!.Path, "--scan-delay", ScanDelayMilliseconds, "--quit-after-scan");
        var window = app.Window(WindowTimeout);
        var cancel = AppSession.Find(window, "Cancel", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => cancel.Current.IsEnabled, TimeSpan.FromSeconds(10)), "Cancel never became enabled while the delayed scan ran");

        ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Assert.AreEqual(3, app.ExitCode(TimeSpan.FromSeconds(30)));
    }

    // Contract: a tile's context menu holds RevealInExplorer and MoveToRecycleBin; neither is invoked. Plan group 11:
    // MoveToRecycleBin is enabled on a movable tile (the fixture is inside the profile, outside AppData: root (a) of
    // docs/spec/02) and stays disabled on a display-only one (MoveAndPutBackTests.ADisplayOnlyTileCannotBeMoved).
    [TestMethod]
    [TestCategory("Console")]
    public void TileContextMenuHasRevealAndAnEnabledMove()
    {
        using var fixture = SmallTree(Fixture.ProfileBaseFolder());
        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForMapWithTiles(app);
        var tile = AppSession.FindAll(map, "Tile")[0];
        tile.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => Automation.Compare(AutomationElement.FocusedElement, tile), TimeSpan.FromSeconds(3)), "the tile did not take focus");
        Keys.PressWithShift(Keys.F10);
        try
        {
            var reveal = AppSession.WaitFor(() => app.FindInAnyWindow("RevealInExplorer"), TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reveal, "no RevealInExplorer item in the context menu");
            Assert.AreEqual(Text.RevealInExplorer, reveal.Current.Name);
            var move = app.FindInAnyWindow("MoveToRecycleBin");
            Assert.IsNotNull(move, "no MoveToRecycleBin item in the context menu");
            Assert.AreEqual(Text.MoveToRecycleBin, move.Current.Name);
            Assert.IsTrue(move.Current.IsEnabled, "Move to Recycle Bin… is disabled on a movable tile (plan group 11); HelpText \"" + tile.Current.HelpText + "\"");
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }
    }

    // Contract: the ShowSystemItems toggle (Text.ShowSystemItems) exists and toggles; ASP-14 moves it into Large and old,
    // the only list it changes, and off the map.
    [TestMethod]
    [TestCategory("Console")]
    public void ShowSystemItemsToggles()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        Assert.IsNull(AppSession.TryFind(app.Window(WindowTimeout), "ShowSystemItems"), "ShowSystemItems is still on the map (ASP-14 moves it into Large and old)");
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        var toggle = AppSession.Find(view, "ShowSystemItems", ElementTimeout);
        Assert.AreEqual(Text.ShowSystemItems, toggle.Current.Name);
        var pattern = (TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
        var before = pattern.Current.ToggleState;
        Assert.AreEqual(ToggleState.Off, before, "hidden by default (docs/spec/03)");
        pattern.Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => pattern.Current.ToggleState == ToggleState.On, TimeSpan.FromSeconds(3)), "did not turn on");
        pattern.Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => pattern.Current.ToggleState == ToggleState.Off, TimeSpan.FromSeconds(3)), "did not turn off again");
    }

    // Contract: tiles are focusable; Tab reaches the map; the arrow keys or Tab move between tiles (intent: keyboard
    // reachable everywhere).
    [TestMethod]
    [TestCategory("Console")]
    public void TilesAreReachableAndMovableByKeyboard()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        var window = app.Window(WindowTimeout);
        var map = WaitForMapWithTiles(app);
        var tiles = AppSession.FindAll(map, "Tile");
        Assert.IsTrue(tiles.Count >= 2, "need at least two tiles");
        Assert.IsTrue(tiles.All(t => t.Current.IsKeyboardFocusable), "every tile is keyboard focusable");

        var scan = AppSession.Find(window, "Scan", ElementTimeout);
        scan.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => Automation.Compare(AutomationElement.FocusedElement, scan), TimeSpan.FromSeconds(3)), "the Scan button did not take focus");
        var reached = false;
        for (var i = 0; i < 12 && !reached; i++)
        {
            Keys.Press(Keys.Tab);
            reached = FocusedTile() is not null;
        }

        Assert.IsTrue(reached, "Tab did not reach a tile within 12 presses");
        var first = FocusedTile()!;
        // The contract allows either the arrow keys or Tab to move between tiles: Right is tried first, Tab second, and
        // the assertion below demands that one of them moved the focus.
        Keys.Press(Keys.Right);
        var next = FocusedTile();
        if (next is null || Automation.Compare(next, first))
        {
            Keys.Press(Keys.Tab);
            next = FocusedTile();
        }

        Assert.IsNotNull(next, "focus left the tiles");
        Assert.IsFalse(Automation.Compare(next, first), "neither Right nor Tab moved the focus to another tile (still " + first.Current.Name + ")");
    }

    private static AutomationElement? FocusedTile()
    {
        var focused = AutomationElement.FocusedElement;
        return focused is not null && focused.Current.AutomationId == "Tile" ? focused : null;
    }
}
