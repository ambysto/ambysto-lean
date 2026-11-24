using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// ASP-15 UI review round 1 (intent/2026-10-18-asp-15-duplicates-helpers.md, the last Review entry): tests for what the review found
// wrong or untested, written before the fixes: the tab of a mixed-type group, Select by rule off while a scan, a search or a move runs,
// Select all off while a move is confirmed and runs, the over-100 count leaving out hinted copies and other tabs, the rule's status
// line gone from other views, and High Contrast for the sidebar and the groups.
public sealed partial class DuplicatesHelpersTests
{
    private static MoveFixture? mixed;
    private static MoveFixture? hintTree;

    private static void DisposeRound1Trees()
    {
        mixed?.Dispose();
        hintTree?.Dispose();
    }

    // ---- 1. the tab of a mixed-type group ----------------------------------------------------------------------------------

    /// <summary>Groups of byte-identical files with different names, so of different types. The suggested copy is the newest.</summary>
    private static readonly (string Name, string Tab, int Kb, (string Rel, int DaysAgo)[] Copies)[] MixedGroups =
    [
        // suggested mid.iso (Disk image), the first copy by name is notes.txt (Document): Disk image
        ("g1", Text.TypeDiskImage, 4, [(@"g1\notes.txt", 10), (@"g1\mid.iso", 1)]),
        // suggested report.pdf (Document), the other copy is a disk image: Document
        ("g2", Text.TypeDocument, 8, [(@"g2\report.pdf", 1), (@"g2\disk.iso", 10)]),
        // suggested a.bin (Other): the one copy that is not Other is c.mp4: Video
        ("g3", Text.TypeVideo, 12, [(@"g3\a.bin", 1), (@"g3\b.dat", 5), (@"g3\c.mp4", 10)]),
        // nothing but Other: Other
        ("g4", Text.TypeOther, 16, [(@"g4\x.bin", 1), (@"g4\y.dat", 2)]),
    ];

    private static MoveFixture MixedTree()
    {
        if (mixed is not null)
        {
            return mixed;
        }

        mixed = new MoveFixture("asp15-mixed");
        var now = DateTime.UtcNow;
        foreach (var (name, _, kb, copies) in MixedGroups)
        {
            foreach (var (rel, days) in copies)
            {
                mixed.File(rel, MB + (kb * KB), 10 + kb);
                mixed.SetModified(rel, now.AddDays(-days));
            }
        }

        return mixed;
    }

    // Spec 03 and the review: a group belongs to the tab of its suggested copy's type; when that type is Other, to the first copy
    // that is not Other, else Other. A byte-identical notes.txt and mid.iso, mid.iso suggested (the newer), is under Disk image and
    // not under Document; report.pdf (suggested) with disk.iso is under Document; a.bin (suggested) with b.dat and c.mp4 is under Video;
    // x.bin with y.dat is under Other. The tab counts say so, and each tab lists exactly its group's copies.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AMixedTypeGroupGoesToTheTabOfItsSuggestedCopy()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = AppSession.Start("--scan", MixedTree().Path);
        var view = OpenDuplicates(app);
        var tabs = Tabs(view);
        Console.WriteLine("tabs: " + TabDump(tabs));
        foreach (var label in TypeLabels)
        {
            var expected = MixedGroups.Count(g => g.Tab == label);
            Assert.AreEqual(expected, tabs.FirstOrDefault(t => t.Label == label)?.Count, $"the tab {label} should say ({expected}): {TabDump(tabs)}");
        }

        foreach (var label in new[] { Text.TypeDiskImage, Text.TypeDocument, Text.TypeVideo, Text.TypeOther })
        {
            ChooseTab(view, label);
            var cells = Cells(view);
            foreach (var group in MixedGroups)
            {
                foreach (var (rel, _) in group.Copies)
                {
                    Assert.AreEqual(group.Tab == label, CellOf(cells, MixedTree().Sub(rel)) is not null,
                        $"tab {label}: the copy {rel} of group {group.Name} (tab {group.Tab}) should be {(group.Tab == label ? "listed" : "absent")}: {Dump(cells)}");
                }
            }
        }
    }

    // ---- 2. Select by rule is off while a scan, a search or a move runs ----------------------------------------------------------

    private static AutomationElement? ShownSelectAll(AppSession app) => Ui.FindAllAnywhere(app, "SelectAll").FirstOrDefault(e =>
    {
        try
        {
            return !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    });

    private static string RuleButtonState(AppSession app, AutomationElement view)
    {
        var button = AppSession.TryFind(view, "SelectByRule");
        var state = button is null ? "absent" : "enabled " + button.Current.IsEnabled;
        return "SelectByRule is not enabled once the scan and the search are over: " + AppSession.FindAll(view, "DuplicateGroup").Count + " groups, button " + state + ", status \"" + Status(app) + "\"";
    }

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

    /// <summary>The prefix every "Moving ..." status line shares.</summary>
    private static string MovingPrefix() => CommonPrefixOf(Text.MovingProgress(1, 9, "x"), Text.MovingProgress(2, 9, "x"));

    private static string CommonPrefixOf(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }

        return a[..length];
    }

    /// <summary>Presses ConfirmMoveButton and samples <paramref name="enabled"/> every 20 ms while the status line shows a
    /// "Moving ..." line, until the move report is there. Returns how many samples were taken and in how many it was enabled.</summary>
    private static (int Samples, int WhileEnabled) SampleDuringTheMove(AppSession app, Func<bool> enabled)
    {
        var prefix = MovingPrefix();
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        int samples = 0, on = 0;
        Ui.Click(Ui.FindAnywhere(app, "ConfirmMoveButton", Ui.ElementTimeout));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < Ui.MoveTimeout)
        {
            string line;
            try
            {
                line = status.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                line = string.Empty;
            }

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                samples++;
                on += enabled() ? 1 : 0;
            }
            else if (app.FindInAnyWindow("MoveReport") is not null)
            {
                break;
            }

            System.Threading.Thread.Sleep(20);
        }

        return (samples, on);
    }

    /// <summary>A scanned fixture of ten identical 2 MB copies (the newest kept), as StatusLineTests moves them; returns the paths
    /// without the kept one.</summary>
    private static (MoveFixture Tree, string[] Moved, Dictionary<string, (ulong Volume, UInt128 FileId)> Identities) TenCopies(string name)
    {
        var tree = new MoveFixture(name);
        var content = new byte[(int)(2 * MB)];
        new Random(15_3).NextBytes(content);
        var now = DateTime.UtcNow;
        var paths = new string[10];
        for (var i = 0; i < 10; i++)
        {
            var rel = $@"c{i + 1:D2}\copy-{i + 1:D2}.dat";
            paths[i] = tree.FileWithBytes(rel, content);
            tree.SetModified(rel, now.AddDays(-(10 - i)));
        }

        var moved = paths[..9];
        return (tree, moved, paths.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase));
    }

    private static void PutBackAndCheck(AppSession app, string[] moved, Dictionary<string, (ulong Volume, UInt128 FileId)> identities)
    {
        Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
        foreach (var path in moved)
        {
            Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(path), Ui.MoveTimeout), path + " was not put back");
            Assert.AreEqual(identities[path], MoveFixture.Identity(path), path + " was put back as a different file");
        }
    }

    // Review MINOR: Select by rule is off while a scan runs. A walk made slow with --scan-delay over 200 folders; the Duplicates view
    // is opened from the sidebar while the scan runs (Inconclusive if the sidebar cannot be used then); SelectByRule must be in it and
    // disabled whenever it is there while the scan runs; after the scan and a search it is enabled. Inconclusive, last, when the button is
    // not in the view while the scan runs (no groups, so it is not shown: nothing to be disabled).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectByRuleIsOffWhileAScanRuns()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-rule-scan");
        for (var i = 0; i < 200; i++)
        {
            tree.File($@"d{i:D3}\f.bin", 4 * KB, i);
        }

        tree.File(@"dup-a\d.dat", MB + (4 * KB), 1);
        tree.File(@"dup-b\d.dat", MB + (4 * KB), 1);
        using var app = AppSession.Start("--scan", tree.Path, "--scan-delay", "20");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), Ui.ElementTimeout), "the scan did not start");
        AutomationElement view;
        try
        {
            view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotEnabledException or AssertFailedException)
        {
            Assert.Inconclusive("the Duplicates item of the sidebar cannot be used while a scan runs: " + e.Message);
            return;
        }

        int samples = 0, enabled = 0;
        while (Ui.ScanRunning(app))
        {
            if (AppSession.TryFind(view, "SelectByRule") is { } button)
            {
                samples++;
                enabled += EnabledNow(button) ? 1 : 0;
            }

            System.Threading.Thread.Sleep(100);
        }

        Assert.AreEqual(0, enabled, $"SelectByRule was enabled in {enabled} of {samples} samples while the scan ran");
        Assert.IsTrue(AppSession.WaitUntil(() => EnabledNow(AppSession.TryFind(view, "FindDuplicates")), Ui.ElementTimeout), "FindDuplicates is not enabled once the scan is over");
        var pressed = 0;
        for (; pressed < 3 && AppSession.FindAll(view, "DuplicateGroup").Count == 0; pressed++)
        {
            Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
            AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0, TimeSpan.FromSeconds(15));
        }

        Console.WriteLine($"FindDuplicates pressed {pressed} time(s)");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0 && EnabledNow(AppSession.TryFind(view, "SelectByRule")), Ui.DuplicatesTimeout),
            RuleButtonState(app, view) + $" (FindDuplicates pressed {pressed} time(s))");
        if (samples == 0)
        {
            Assert.Inconclusive("SelectByRule is not in the Duplicates view's UI Automation tree while a scan runs (the view holds no groups, so the button is not shown): there is nothing to be disabled; only the state after the scan and the search was tested");
        }
    }

    // Review MINOR: Select by rule is off while a duplicates search runs. Held by comparing three identical 300 MB files (zeros, so
    // cheap to write); SelectByRule is sampled every 50 ms while CancelDuplicates is enabled. Inconclusive if the search ends before a
    // single sample was taken (it cannot be held on this machine).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectByRuleIsOffWhileTheDuplicatesSearchRuns()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-rule-search");
        foreach (var name in new[] { "a", "b", "c" })
        {
            tree.File($@"{name}\big.dat", 300 * MB, 7);
        }

        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        var cancel = AppSession.Find(view, "CancelDuplicates", Ui.ElementTimeout);
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        int samples = 0, enabled = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < Ui.DuplicatesTimeout)
        {
            if (EnabledNow(cancel))
            {
                // A sample counts only when Cancel is still on after SelectByRule was read: the search can end between the two
                // reads, and the app turns Cancel off and SelectByRule on in one step.
                if (AppSession.TryFind(view, "SelectByRule") is { } button)
                {
                    var on = EnabledNow(button);
                    if (EnabledNow(cancel))
                    {
                        samples++;
                        enabled += on ? 1 : 0;
                    }
                }
            }
            else if (samples > 0 || watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                break;
            }

            System.Threading.Thread.Sleep(50);
        }

        Assert.AreEqual(0, enabled, $"SelectByRule was enabled in {enabled} of {samples} samples while the search ran");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0 && EnabledNow(AppSession.TryFind(view, "SelectByRule")), Ui.DuplicatesTimeout),
            "SelectByRule is not enabled once the search is over");
        if (samples == 0)
        {
            Assert.Inconclusive("the search ended before it could be sampled: it cannot be held with three 300 MB files on this machine");
        }
    }

    // Review MINOR: Select by rule is off while a move runs. Nine of ten identical 2 MB copies are moved (as StatusLineTests does) and
    // SelectByRule is sampled every 20 ms while the status line shows a "Moving ..." line; the nine are put back afterwards.
    // Inconclusive if no "Moving" line could be sampled.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectByRuleIsOffWhileAMoveRuns()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        var (tree, moved, identities) = TenCopies("asp15-rule-move");
        using var _ = tree;
        using var app = AppSession.Start("--scan", tree.Path);
        var view = OpenDuplicates(app);
        foreach (var path in moved)
        {
            Table.Tick(Table.ControlFor(view, "DuplicateCopy", path)!, () => Table.ControlFor(view, "DuplicateCopy", path), path);
        }

        Ui.Click(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout));
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        var (samples, enabled) = SampleDuringTheMove(app, () => EnabledNow(app.FindInAnyWindow("SelectByRule")));
        Ui.WaitForReport(app, Text.SpaceReturnsWhenEmptied);
        PutBackAndCheck(app, moved, identities);
        Assert.AreEqual(0, enabled, $"SelectByRule was enabled in {enabled} of {samples} samples while the move ran");
        if (samples == 0)
        {
            Assert.Inconclusive("no \"Moving\" status line could be sampled: the move cannot be held with nine 2 MB copies on this machine");
        }
    }

    // ---- 3. Select all is off while a move is confirmed and while it runs ---------------------------------------------------------

    // Review MINOR: Select all (the largest files) is off while a move is being confirmed, and while it runs. Ten files are ticked by
    // SelectAll and Move opens the confirmation: with it open SelectAll is disabled (this is the reachable state); Cancel enables it
    // again. Then the move is confirmed and SelectAll is sampled while the status line shows a "Moving ..." line (Inconclusive, last,
    // when none could be sampled); the files are put back.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectAllIsOffWhileAMoveIsConfirmedAndWhileItRuns()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-selall-move");
        var paths = Enumerable.Range(1, 9).Select(i => tree.File($@"f{i}\file-{i}.dat", MB + (i * 4 * KB), 40 + i)).ToArray();

        var identities = paths.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        var window = app.Window(Ui.WindowTimeout);
        var selectAll = AppSession.WaitFor(() => ShownSelectAll(app), Ui.ElementTimeout);
        Assert.IsNotNull(selectAll, "no SelectAll is shown");
        Ui.Focus(selectAll);
        Keys.Press(Keys.Space);
        var move = AppSession.Find(window, "MoveLargest", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move stays disabled after SelectAll");
        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Assert.IsTrue(ShownSelectAll(app) is { } open && !open.Current.IsEnabled, "SelectAll is enabled while the move is being confirmed");
        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        Assert.IsTrue(AppSession.WaitUntil(() => EnabledNow(ShownSelectAll(app)), Ui.ElementTimeout), "SelectAll stays disabled after the confirmation was cancelled");

        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        var (samples, enabled) = SampleDuringTheMove(app, () => EnabledNow(ShownSelectAll(app)));
        Ui.WaitForReport(app, Text.SpaceReturnsWhenEmptied);
        PutBackAndCheck(app, paths, identities);
        Assert.AreEqual(0, enabled, $"SelectAll was enabled in {enabled} of {samples} samples while the move ran");
        if (samples == 0)
        {
            Assert.Inconclusive("no \"Moving\" status line could be sampled: the move cannot be held on this machine; only the confirmation state was tested");
        }
    }

    // ---- 4. the over-100 count: hinted copies and other tabs ----------------------------------------------------------------------

    /// <summary>o1..o5: 26 copies each of blob.bin (Other), c00 the newest and kept; v: 27 copies of clip.mp4 (Video).</summary>
    private static MoveFixture HintTree()
    {
        if (hintTree is not null)
        {
            return hintTree;
        }

        hintTree = new MoveFixture("asp15-hint");
        var now = DateTime.UtcNow;
        var number = 0;
        foreach (var (name, count, file) in new[] { ("o1", 26, "blob.bin"), ("o2", 26, "blob.bin"), ("o3", 26, "blob.bin"), ("o4", 26, "blob.bin"), ("o5", 26, "blob.bin"), ("v", 27, "clip.mp4") })
        {
            number++;
            for (var i = 0; i < count; i++)
            {
                var rel = $@"{name}\c{i:D2}\{file}";
                hintTree.File(rel, MB + (number * 4 * KB), 60 + number);
                hintTree.SetModified(rel, now.AddDays(-(i + 1)));
            }
        }

        return hintTree;
    }

    // Intent "A large rule asks first": the count is the copies the rule would tick (no hint, not the kept one), in the groups of the
    // current tab. Other tab: five groups of 26 copies, so 125 would be ticked; two copies are in protected folders (o3\c05 and
    // o5\c07), so 123 are, and the Video tab's 26 do not count: Text.RuleConfirm(123, 5, 0), with no program-file part. Cancel.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheLargeRuleCountLeavesOutHintedCopiesAndTheGroupsOfOtherTabs()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders(HintTree().Sub(@"o3\c05"), HintTree().Sub(@"o5\c07"));
        using var app = AppSession.Start("--scan", HintTree().Path);
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeOther);
        var text = Ui.Copy("RuleConfirm", 123, 5, 0);
        ChooseRule(app, view, Newest.MenuId);
        var dialog = WaitForRuleDialog(app, text);
        PressInDialog(app, dialog, Text.Cancel);
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(0, Cells(view).Count(c => c.Ticked), "Cancel left a copy ticked");
    }

    // Intent as above: hinted copies are not counted towards the 100. The Other tab again, with the 25 non-kept copies of o5 all in
    // protected folders (o5\c01 .. o5\c25): 125 raw, 100 that can be ticked, so the rule ticks at once with no confirmation.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void HintedCopiesDoNotCountTowardsTheHundredThatAsksFirst()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders([.. Enumerable.Range(1, 25).Select(i => HintTree().Sub($@"o5\c{i:D2}"))]);
        using var app = AppSession.Start("--scan", HintTree().Path);
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeOther);
        ChooseRule(app, view, Newest.MenuId);
        AssertNoRuleDialog(app, "a rule that can tick 100 of 125 copies");
        Assert.IsTrue(AppSession.WaitUntil(() => Cells(view).Count(c => c.Ticked) == 100, Ui.ElementTimeout), "the rule did not tick the 100 copies that can be ticked: " + Cells(view).Count(c => c.Ticked));
    }

    // ---- 5. the Duplicates status line does not follow the user to other views --------------------------------------------------

    // Review MINOR: the line a rule leaves ("Ticked 6 copies in 2 groups; ...") is gone once Large and old is opened, and is not back
    // when Duplicates is opened again.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheRuleStatusLineIsGoneOnOtherViewsAndWhenComingBack()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeVideo);
        ChooseRule(app, view, Newest.MenuId);
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var (ticked, touched) = TicksOf(Newest, video, null);
        var line = RuleStatus(ticked.Count, touched);
        AssertStatus(app, line, "Newest in Video");
        var prefix = CommonPrefixOf(RuleStatus(1, 1), RuleStatus(22, 33));

        Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => !Status(app).StartsWith(prefix, StringComparison.Ordinal), Ui.ElementTimeout), $"the status line on Large and old is still \"{Status(app)}\"");
        Ui.OpenView(app, "Duplicates", "DuplicatesView");
        System.Threading.Thread.Sleep(1000);
        Assert.IsFalse(Status(app).StartsWith(prefix, StringComparison.Ordinal), $"the status line is back on \"{Status(app)}\" when Duplicates is opened again");
    }

    // ---- 6. High Contrast ------------------------------------------------------------------------------------------------------

    private static readonly int[] SystemColourIndexes = [1, 4, 5, 15]; // desktop, menu, window, button face

    private static string Describe(Rgb colour) => colour.ToString();

    // Intent "Two tones": under High Contrast the sidebar follows the system colours. High Contrast Black is switched on (and put back,
    // HighContrastScope) before the app starts; the sidebar's empty band is one of the system colours (window, button face, menu,
    // desktop), and not LeanSidebar, LeanPanel or LeanGround.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheSidebarFollowsTheSystemColours()
    {
        using var contrast = new HighContrastScope();
        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        var scanBox = AppSession.Find(window, "Scan", Ui.ElementTimeout).Current.BoundingRectangle;
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, scanBox);
        var scale = WindowOnScreen.Scale(handle);
        int Px(double dips) => (int)Math.Round(dips * scale);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"capture saved {capture.Save(nameof(UnderHighContrastTheSidebarFollowsTheSystemColours))}");
        var box = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        var band = capture.Area((int)box.Left + Px(8), (int)history.Bottom + Px(4), (int)box.Right - Px(8), (int)settings.Top - Px(4));
        Assert.IsTrue(band.Count > 100, "no empty band between History and Settings to sample");
        var dominant = band.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
        var system = SystemColourIndexes.Select(HighContrastScope.System).ToList();
        var problems = new List<string>();
        if (dominant.Near(Brand.Sidebar, Brand.NeutralTolerance) || dominant.Near(Brand.Panel, Brand.NeutralTolerance) || dominant.Near(Brand.Ground, Brand.NeutralTolerance))
        {
            problems.Add($"the sidebar keeps a brand tone under High Contrast: {Describe(dominant)}");
        }

        if (!system.Any(c => c.Near(dominant, Brand.NeutralTolerance)))
        {
            problems.Add($"the sidebar's colour {Describe(dominant)} is none of the system colours [{string.Join(", ", system.Select(Describe))}]");
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Intent "Groups" and the review: under High Contrast the group backgrounds follow the system colours: every group on screen is
    // one of them (window, button face, menu, desktop), and none is LeanPanel or LeanGround.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastTheGroupBackgroundsFollowTheSystemColours()
    {
        using var settings = ProtectGuarded();
        using var contrast = new HighContrastScope();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var window = app.Window(Ui.WindowTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, Ui.SidebarItem(app, "History").Current.BoundingRectangle);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"capture saved {capture.Save(nameof(UnderHighContrastTheGroupBackgroundsFollowTheSystemColours))}");
        var groups = AppSession.FindAll(view, "DuplicateGroup").Select(g => g.Current.BoundingRectangle).Where(r => !r.IsEmpty).OrderBy(r => r.Top).ToList();
        var rows = Table.Controls(view, "DuplicateCopy").Select(Table.RowOf).OfType<AutomationElement>().Select(r => r.Current.BoundingRectangle)
            .Where(r => !r.IsEmpty && r.Height >= 20 && capture.Contains((int)r.Left + 4, (int)r.Top + 2) && capture.Contains((int)r.Right - 4, (int)r.Bottom - 2)).ToList();
        Assert.IsTrue(groups.Count >= 2 && rows.Count >= 2, $"fewer than two groups on screen: {groups.Count} groups, {rows.Count} rows");
        var system = SystemColourIndexes.Select(HighContrastScope.System).ToList();
        var perGroup = new SortedDictionary<int, List<Rgb>>();
        foreach (var row in rows)
        {
            var index = groups.FindLastIndex(g => g.Top <= row.Top + 1);
            if (index < 0)
            {
                continue;
            }

            if (!perGroup.TryGetValue(index, out var samples))
            {
                perGroup[index] = samples = [];
            }

            samples.AddRange(capture.Area((int)row.Left + 4, (int)row.Top + 2, (int)row.Right - 4, (int)row.Bottom - 2));
        }

        var problems = new List<string>();
        foreach (var (index, samples) in perGroup)
        {
            var dominant = samples.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
            if (dominant.Near(Brand.Panel, Brand.NeutralTolerance) || dominant.Near(Brand.Ground, Brand.NeutralTolerance))
            {
                problems.Add($"group {index + 1} keeps a brand tone under High Contrast: {Describe(dominant)}");
            }
            else if (!system.Any(c => c.Near(dominant, Brand.NeutralTolerance)))
            {
                problems.Add($"group {index + 1}'s background {Describe(dominant)} is none of the system colours [{string.Join(", ", system.Select(Describe))}]");
            }
        }

        Assert.IsTrue(perGroup.Count >= 2, "fewer than two groups have a row on screen");
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }
}
