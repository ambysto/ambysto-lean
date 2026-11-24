using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// Acceptance-first, a written acceptance contract. No map renderer or builder draft was inspected.
[TestClass]
[DoNotParallelize]
public sealed class MapRenderReuseTests
{
    private sealed record TileProof(string Path, string Name, string Help, string RuntimeId, Rect Bounds);

    private static string FullPath(AutomationElement tile)
    {
        var help = tile.Current.HelpText;
        var path = help.Split('\n')[0].TrimEnd('\r');
        Assert.IsTrue(Path.IsPathFullyQualified(path), "Tile HelpText begins with its complete path: " + help);
        return path;
    }

    // Aggregate/coverage tiles have no Node.Path; only actual nodes participate in exact-path identity checks.
    private static Dictionary<string, TileProof> Snapshot(AppSession app) => AppSession.FindAll(Ui.Map(app), "Tile")
        .Where(t => !t.Current.Name.StartsWith(Text.SmallerItems, StringComparison.Ordinal)
            && !t.Current.Name.StartsWith(Text.NotMeasured, StringComparison.Ordinal))
        .Select(t => new TileProof(FullPath(t), t.Current.Name, t.Current.HelpText, string.Join(".", t.GetRuntimeId()), t.Current.BoundingRectangle))
        .ToDictionary(t => t.Path, StringComparer.Ordinal);

    private static string Fingerprint(Dictionary<string, TileProof> tiles) => string.Join("|", tiles.OrderBy(t => t.Key, StringComparer.Ordinal)
        .Select(t => t.Value.Path + ":" + t.Value.RuntimeId + ":" + t.Value.Bounds));

    private static Dictionary<string, TileProof> Settled(AppSession app)
    {
        string? previous = null;
        Dictionary<string, TileProof>? result = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            try
            {
                var current = Snapshot(app);
                if (current.Count == 0) return false;
                var now = Fingerprint(current);
                var stable = now == previous;
                previous = now;
                result = current;
                return stable;
            }
            catch (ElementNotAvailableException) { previous = null; return false; }
        }, Ui.ElementTimeout), "map geometry/model never settled");
        return result!;
    }

    private static AutomationElement Tile(AppSession app, string path) => AppSession.FindAll(Ui.Map(app), "Tile")
        .Single(t => string.Equals(FullPath(t), path, StringComparison.Ordinal));

    private static void SameNodes(Dictionary<string, TileProof> before, Dictionary<string, TileProof> after, bool sameIds)
    {
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), "redraw preserves the map model paths");
        foreach (var path in before.Keys)
        {
            Assert.AreEqual(before[path].Name, after[path].Name, "unchanged label/allocated bytes: " + path);
            Assert.AreEqual(before[path].Help, after[path].Help, "unchanged full path/protection hint: " + path);
            if (sameIds) Assert.AreEqual(before[path].RuntimeId, after[path].RuntimeId, "surviving final-node Button is reused by exact path: " + path);
        }
    }

    private static void ChooseGroup(AppSession app, string label)
    {
        var groups = AppSession.Find(app.Window(Ui.WindowTimeout), "TypeGroups", Ui.ElementTimeout);
        var group = AppSession.FindAll(groups, "TypeGroup").Single(g => g.Current.Name.StartsWith(label, StringComparison.Ordinal));
        Ui.Click(group);
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            if (group.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selected)) return ((SelectionItemPattern)selected).Current.IsSelected;
            return group.TryGetCurrentPattern(TogglePattern.Pattern, out var toggled) && ((TogglePattern)toggled).Current.ToggleState == ToggleState.On;
        }, Ui.ElementTimeout), "type group must actually be selected before checking redraw: " + label);
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SurvivingFinalTilesKeepTheirRuntimeIdsFullPathsAndFocusAcrossResizeAndTypeFilter()
    {
        using var settings = new SettingsBackup();
        using var fixture = new Fixture("map-reuse", Fixture.ProfileBaseFolder());
        fixture.File(@"one\same.bin", 128 * 1024);
        fixture.File(@"one\extra.bin", 64 * 1024);
        fixture.File(@"two\same.bin", 128 * 1024);
        fixture.File("clip.mp4", 256 * 1024);
        fixture.File(@"guarded\locked.bin", 128 * 1024);
        SettingsBackup.WriteProtectedFolders(Path.Combine(fixture.Path, "guarded"));
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var before = Settled(app);
        Assert.IsTrue(AppSession.FindAll(Ui.Map(app), "Tile").Count <= 2000, "existing map tile budget includes aggregate tiles");
        Assert.IsTrue(before.Values.All(t => t.Path.StartsWith(fixture.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "only nodes in this folder scan");
        var target = Path.Combine(fixture.Path, "clip.mp4");
        var guarded = Path.Combine(fixture.Path, "guarded");
        Assert.IsTrue(before.ContainsKey(target) && before.ContainsKey(guarded), "fixture's ordinary/protected top-level items are represented");
        StringAssert.Contains(before[guarded].Help, Text.HintProtectedByYou, "protected tile retains its display-only hint");
        var window = app.Window(Ui.WindowTimeout);
        Assert.IsTrue(window.TryGetCurrentPattern(TransformPattern.Pattern, out var transformObject), "owned window exposes resize");
        var transform = (TransformPattern)transformObject;
        Assert.IsTrue(transform.Current.CanResize);
        var original = window.Current.BoundingRectangle;
        var oldMapBounds = Ui.Map(app).Current.BoundingRectangle;
        Ui.Focus(Tile(app, target));
        try
        {
            // Relative resize, not a tile coordinate or latency requirement. The test owns only this app's window.
            transform.Resize(original.Width * 0.85, original.Height);
            Assert.IsTrue(AppSession.WaitUntil(() => Math.Abs(Ui.Map(app).Current.BoundingRectangle.Width - oldMapBounds.Width) > 1, Ui.ElementTimeout), "resize must actually change the map area");
            var resized = Settled(app);
            SameNodes(before, resized, sameIds: true);
            var focused = AutomationElement.FocusedElement;
            Assert.IsNotNull(focused);
            Assert.AreEqual("Tile", focused.Current.AutomationId, "focused surviving tile must retain focus through resize");
            Assert.AreEqual(target, FullPath(focused), "resize cannot retarget tile focus to a different node");
            Assert.AreEqual(before[target].RuntimeId, string.Join(".", focused.GetRuntimeId()));

            ChooseGroup(app, Text.TypeVideo);
            SameNodes(resized, Settled(app), sameIds: true); // filtering dims other groups; it does not rebuild the model
            ChooseGroup(app, Ui.Copy("AllFiles"));
            SameNodes(resized, Settled(app), sameIds: true);
        }
        finally { transform.Resize(original.Width, original.Height); }
        SameNodes(before, Settled(app), sameIds: true);
        foreach (var relative in new[] { @"one\same.bin", @"one\extra.bin", @"two\same.bin", "clip.mp4", @"guarded\locked.bin" })
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Path, relative)), "presentation redraw never moves fixture data");
    }

    [TestMethod]
    [TestCategory("Console")]
    public void DisplayOnlyProgressZoneControlsNeverBecomeFinalNodeControls()
    {
        using var fixture = new Fixture("map-reuse-phases", Fixture.ProfileBaseFolder());
        var chain = "slow";
        for (var level = 0; level < 30; level++)
        {
            fixture.File(chain + @"\f.bin", 8192);
            chain += @"\l" + level.ToString("D2", CultureInfo.InvariantCulture);
        }
        fixture.File(@"alpha\clip.mp4", 512 * 1024);
        fixture.File(@"beta\archive.zip", 384 * 1024);
        fixture.File("last.bin", 128 * 1024);
        using var app = AppSession.Start("--scan", fixture.Path, "--scan-delay", "200");
        Dictionary<string, TileProof>? preview = null;
        HashSet<string>? previewIds = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            try
            {
                if (!Ui.ScanRunning(app)) return false;
                var current = Snapshot(app);
                if (!current.ContainsKey(Path.Combine(fixture.Path, "alpha"))) return false;
                preview = current;
                previewIds = AppSession.FindAll(Ui.Map(app), "Tile").Select(t => string.Join(".", t.GetRuntimeId())).ToHashSet(StringComparer.Ordinal);
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
        }, TimeSpan.FromSeconds(15)), "delayed scan must expose display-only zone tiles");
        var window = app.Window(Ui.WindowTimeout);
        var crumbs = string.Join("|", AppSession.FindAll(window, "Breadcrumb").Select(c => c.Current.Name));
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            try
            {
                if (!Ui.ScanRunning(app)) return false;
                Ui.Click(Tile(app, Path.Combine(fixture.Path, "alpha")));
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
        }, Ui.ElementTimeout), "read-only zone click must occur while the delayed scan runs");
        Assert.IsTrue(Ui.ScanRunning(app), "precondition: zone click was tested during the scan");
        Assert.AreEqual(crumbs, string.Join("|", AppSession.FindAll(window, "Breadcrumb").Select(c => c.Current.Name)), "read-only zone cannot drill down");
        Ui.WaitForScan(app);
        var final = Settled(app);
        Assert.IsTrue(final.ContainsKey(Path.Combine(fixture.Path, "alpha")), "final map represents the same folder as a final node");
        Assert.IsNotNull(preview);
        Assert.IsNotNull(previewIds);
        var phaseIds = previewIds!;
        Assert.IsFalse(AppSession.FindAll(Ui.Map(app), "Tile").Any(t => phaseIds.Contains(string.Join(".", t.GetRuntimeId()))), "preview-phase controls cannot acquire final-node action authority");
        Assert.IsTrue(final.ContainsKey(Path.Combine(fixture.Path, "last.bin")), "final map includes non-zone files too");
    }

    [TestMethod]
    [TestCategory("Console")]
    public void DrilldownRemovesObsoleteControlsWithoutRetargetingTheirFocusToAnotherPath()
    {
        using var fixture = new Fixture("map-reuse-remove", Fixture.ProfileBaseFolder());
        fixture.File(@"inside\first.bin", 256 * 1024);
        fixture.File(@"inside\second.bin", 128 * 1024);
        fixture.File("outside.mp4", 384 * 1024);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var before = Settled(app);
        var inside = Path.Combine(fixture.Path, "inside");
        var outside = Path.Combine(fixture.Path, "outside.mp4");
        Ui.Focus(Tile(app, outside));
        Ui.Click(Tile(app, inside));
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            try { return !Snapshot(app).ContainsKey(outside); }
            catch (ElementNotAvailableException) { return false; }
        }, Ui.ElementTimeout), "drilldown must remove the obsolete sibling tile");
        var after = Settled(app);
        Assert.IsTrue(after.Values.All(t => t.Path.StartsWith(inside + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "no obsolete root/sibling tiles remain in the map");
        var obsoleteIds = before.Values.Where(t => !after.ContainsKey(t.Path)).Select(t => t.RuntimeId).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(after.Values.Any(t => obsoleteIds.Contains(t.RuntimeId)), "an obsolete control cannot be rebound to a different full path");
        var focus = AutomationElement.FocusedElement;
        if (focus is not null && focus.Current.AutomationId == "Tile")
            Assert.IsTrue(after.ContainsKey(FullPath(focus)), "remaining tile focus belongs to a current path");
        Assert.IsTrue(File.Exists(outside), "drilldown never moves the old sibling");
    }
}
