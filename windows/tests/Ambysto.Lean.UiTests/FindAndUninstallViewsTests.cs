using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan group 8b, "Find and Uninstall views" UI contract: the Large and old view (docs/spec/03: allocated size
/// against the threshold, "last modified", the size and age choices, display-only rows behind "Show system items", the
/// row context menu), the Duplicates view (byte-identical groups, the estimate, the suggested-keep badge, nothing ticked,
/// the last unticked copy; with plan group 11, Move enabled only while a copy is ticked) and the Uninstall view (docs/spec/04 Windows note: the registered uninstallers,
/// per-machine rows "needs admin"). Through the UI Automation client in the console session. Nothing is ever invoked
/// that reveals a file, moves one, opens the Recycle Bin, presses UninstallInSettings or opens Windows Settings (moves are
/// MoveAndPutBackTests').</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class FindAndUninstallViewsTests
{
    private const long MB = ByteUnits.MB;
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DuplicatesTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a test watches for a change that must not happen.</summary>
    private static readonly TimeSpan NeverWatch = TimeSpan.FromSeconds(3);

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // The large-and-old fixture, built once (about 400 MB of real data), in the allowed root (docs/spec/02 (a)), so no
    // row is display-only:
    //   old-video.mp4   120 MB written, modified 3 years ago: over every size choice but 500 MB and 1 GB, older than every age choice;
    //   mid-age.iso     110 MB written, modified 9 months ago: shown only with the 6-month choice;
    //   recent.zip      110 MB written, modified now: never old enough;
    //   small-old.zip    60 MB written, modified 3 years ago: shown only with the 50 MB choice;
    //   sparse-old.mp4  300 MB long but sparse (allocated about 0), modified 3 years ago: never shown, because the
    //                   threshold is on the allocated size (docs/spec/03, 01 walk rule 3), not the length.
    private const string OldVideo = "old-video.mp4";
    private const string MidAge = "mid-age.iso";
    private const string Recent = "recent.zip";
    private const string SmallOld = "small-old.zip";
    private const string SparseOld = "sparse-old.mp4";
    private static Fixture? large;
    private static DateTime threeYearsAgoUtc;
    private static DateTime nineMonthsAgoUtc;

    [ClassInitialize]
    public static void BuildTheLargeAndOldTree(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = DateTime.UtcNow;
        threeYearsAgoUtc = now.AddYears(-3);
        nineMonthsAgoUtc = now.AddMonths(-9);
        large = new Fixture("ui-largeold", Fixture.ProfileBaseFolder());
        large.File(OldVideo, 120 * MB);
        large.File(MidAge, 110 * MB);
        large.File(Recent, 110 * MB);
        large.File(SmallOld, 60 * MB);
        large.SparseFile(SparseOld, 300 * MB);
        large.SetModified(OldVideo, threeYearsAgoUtc);
        large.SetModified(MidAge, nineMonthsAgoUtc);
        large.SetModified(SmallOld, threeYearsAgoUtc);
        large.SetModified(SparseOld, threeYearsAgoUtc);
    }

    [ClassCleanup]
    public static void RemoveTheLargeAndOldTree() => large?.Dispose();

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>A Copy.Text constant the 8b contract needs, read at run time, so a missing one fails this test only (a
    /// surface gap) and does not break the build of the other UI tests.</summary>
    private static string TextConstant(string name)
    {
        var field = typeof(Text).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(field, $"surface gap: Copy.Text has no public constant {name} (plan group 8b UI contract)");
        return (string)field.GetValue(null)!;
    }

    /// <summary>Clicks a button-like element: Invoke, or Select for a navigation item. Never used on Reveal, Move,
    /// OpenRecycleBin or UninstallInSettings.</summary>
    private static void Click(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
        }
        else
        {
            Assert.Fail($"{element.Current.AutomationId} can be neither invoked nor selected");
        }
    }

    /// <summary>The element with <paramref name="automationId"/> in any window of the app (a view may be its own window
    /// or a dialog), within <paramref name="timeout"/>.</summary>
    private static AutomationElement FindAnywhere(AppSession app, string automationId, TimeSpan timeout)
    {
        var element = AppSession.WaitFor(() => app.FindInAnyWindow(automationId), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} in any window of the app within {timeout.TotalSeconds:F0} s");
        return element;
    }

    /// <summary>Clicks the sidebar item <paramref name="item"/> (ASP-14: the ids of the old view buttons) and returns the
    /// view <paramref name="viewId"/>.</summary>
    private static AutomationElement OpenView(AppSession app, string item, string viewId)
    {
        Click(Ui.SidebarItem(app, item));
        return FindAnywhere(app, viewId, ElementTimeout);
    }

    /// <summary>Waits until the scan started with --scan has finished (<see cref="Ui.WaitForScan"/>).</summary>
    private static void WaitForScanToFinish(AppSession app) => Ui.WaitForScan(app);

    /// <summary>The view's way back to the map (ASP-14, in place of ASP-9's BackToMap button): the sidebar's Overview item,
    /// enabled beside the view; the view itself has no BackToMap button.</summary>
    private static AutomationElement WayBack(AppSession app, AutomationElement view)
    {
        Assert.IsNull(AppSession.TryFind(view, "BackToMap"), $"the view {view.Current.AutomationId} still has a BackToMap button (ASP-14 removes it)");
        var overview = Ui.SidebarItem(app, "Overview");
        Assert.IsTrue(overview.Current.IsEnabled && !overview.Current.IsOffscreen, "the sidebar's Overview item is not shown, enabled, beside " + view.Current.AutomationId);
        return overview;
    }

    /// <summary>Every element with <paramref name="automationId"/> in <paramref name="list"/>, scrolling a virtualised list
    /// from top to bottom so that every row is realised once. Returns (Name, HelpText) pairs, without repeats.</summary>
    private static List<(string Name, string Help)> CollectRows(AutomationElement list, string automationId)
    {
        var seen = new List<(string Name, string Help)>();
        void Take()
        {
            foreach (var row in AppSession.FindAll(list, automationId))
            {
                var item = (row.Current.Name, row.Current.HelpText ?? string.Empty);
                if (!seen.Contains(item))
                {
                    seen.Add(item);
                }
            }
        }

        Take();
        if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable)
        {
            var scroll = (ScrollPattern)pattern;
            var step = Math.Max(5.0, scroll.Current.VerticalViewSize);
            for (var at = 0.0; at <= 100.0; at += step)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
                System.Threading.Thread.Sleep(150);
                Take();
            }

            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100.0);
            System.Threading.Thread.Sleep(150);
            Take();
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0.0);
        }

        return seen;
    }

    /// <summary>A size or age option label, "50 MB", "1 GB", "6 months", "2 years" (a decimal point allowed), as
    /// (value, unit) with the unit "MB", "GB", "month" or "year"; null if the label is not one.</summary>
    private static (double Value, string Unit)? ParseOption(string label)
    {
        var match = OptionLabel().Match(label.Trim());
        if (!match.Success)
        {
            return null;
        }

        var number = double.Parse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "MB" => "MB",
            "GB" => "GB",
            "MONTH" or "MONTHS" => "month",
            _ => "year",
        };
        return (number, unit);
    }

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*(MB|GB|months?|years?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionLabel();

    private static bool IsOption(string label, double value, string unit) =>
        ParseOption(label) is { } parsed && Math.Abs(parsed.Value - value) < 1e-9 && parsed.Unit == unit;

    /// <summary>The selectable options of a choice (a combo box, a list or a group of radio buttons): the items with a
    /// SelectionItem pattern below it or, for an expanded combo box, in its popup.</summary>
    private static List<AutomationElement> Options(AppSession app, AutomationElement choice)
    {
        var condition = new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true);
        var inside = choice.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().ToList();
        if (inside.Count > 0)
        {
            return inside;
        }

        var all = new List<AutomationElement>();
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id)))
        {
            all.AddRange(top.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Where(e => ParseOption(e.Current.Name) is not null));
        }

        return all;
    }

    /// <summary>The label of the selected option of <paramref name="choice"/>.</summary>
    private static string SelectedOption(AppSession app, AutomationElement choice)
    {
        if (choice.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection) && ((SelectionPattern)selection).Current.GetSelection() is { Length: > 0 } selected)
        {
            return selected[0].Current.Name;
        }

        if (choice.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ParseOption(((ValuePattern)value).Current.Value) is not null)
        {
            return ((ValuePattern)value).Current.Value;
        }

        var ticked = Options(app, choice).FirstOrDefault(o => ((SelectionItemPattern)o.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
        Assert.IsNotNull(ticked, $"{choice.Current.AutomationId}: no option is selected");
        return ticked.Current.Name;
    }

    /// <summary>Selects the option of <paramref name="choice"/> that is <paramref name="value"/> <paramref name="unit"/>.</summary>
    private static void Choose(AppSession app, AutomationElement choice, double value, string unit)
    {
        var expandable = choice.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern);
        if (expandable)
        {
            ((ExpandCollapsePattern)expandPattern).Expand();
            System.Threading.Thread.Sleep(300);
        }

        AutomationElement? option = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (option = Options(app, choice).FirstOrDefault(o => IsOption(o.Current.Name, value, unit))) is not null, ElementTimeout),
            $"{choice.Current.AutomationId} has no option {value.ToString(CultureInfo.InvariantCulture)} {unit}; options: [" + string.Join(" | ", Options(app, choice).Select(o => o.Current.Name)) + "]");
        ((SelectionItemPattern)option!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expandable && ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
        {
            ((ExpandCollapsePattern)expandPattern).Collapse();
        }

        Assert.IsTrue(AppSession.WaitUntil(() => IsOption(SelectedOption(app, choice), value, unit), ElementTimeout),
            $"{choice.Current.AutomationId}: \"{SelectedOption(app, choice)}\" is selected after choosing {value.ToString(CultureInfo.InvariantCulture)} {unit}");
    }

    /// <summary>The type-group word the legend uses for a fixture file (docs/spec/03 type groups; the Map legend's words).</summary>
    private static string TypeText(string fileName) => Path.GetExtension(fileName).ToUpperInvariant() switch
    {
        ".MP4" => Text.TypeVideo,
        ".ISO" => Text.TypeDiskImage,
        ".ZIP" => Text.TypeArchive,
        _ => Text.TypeOther,
    };

    /// <summary>The file names of the Large and old rows in the view now (ASP-10: a row holds a LargeOldSelect box, and its
    /// automation name joins its cells, the file name among them).</summary>
    private static string[] LargeOldFileNames(AutomationElement view, params string[] candidates)
    {
        var names = Table.Controls(view, "LargeOldSelect").Select(Table.RowName).ToList();
        return candidates.Where(c => names.Any(n => n.Contains(c, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    /// <summary>The LargeOldSelect box whose row holds <paramref name="fileName"/>, or null.</summary>
    private static AutomationElement? LargeOldBox(AutomationElement view, string fileName) => Table.ControlWith(view, "LargeOldSelect", fileName);

    /// <summary>Focuses the row that holds <paramref name="box"/> and returns it.</summary>
    private static AutomationElement FocusRowOf(AutomationElement box)
    {
        var row = Table.RowOf(box);
        Assert.IsNotNull(row, "no list row holds " + Ui.Describe(box));
        row.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => AutomationElement.FocusedElement is { } f && Ui.IsWithin(f, row), TimeSpan.FromSeconds(3)), "the row did not take focus: " + Ui.Describe(row));
        return row;
    }

    private static readonly string[] AllLarge = [OldVideo, MidAge, Recent, SmallOld, SparseOld];

    private static void AssertLargeOldRows(AutomationElement view, string what, params string[] expected)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => LargeOldFileNames(view, AllLarge).SequenceEqual(expected.OrderBy(e => Array.IndexOf(AllLarge, e))), ElementTimeout),
            $"{what}: rows {Table.Dump(view, "LargeOldSelect")}, expected the files {{{string.Join(", ", expected)}}}");
    }

    // ---- Large and old -----------------------------------------------------------------------------------------------

    // Contract: with no scan yet, the Large and old view shows the text Text.ScanFirst, and no row.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeOldShowsScanFirstBeforeAnyScan()
    {
        var scanFirst = TextConstant("ScanFirst");
        using var app = AppSession.Start();
        var view = OpenView(app, "LargeOld", "LargeOldView");
        var text = AppSession.WaitFor(() => view.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, scanFirst)), ElementTimeout);
        Assert.IsNotNull(text, $"the Large and old view shows no \"{scanFirst}\" before a scan");
        Assert.AreEqual(0, AppSession.FindAll(view, "LargeOldSelect").Count, "rows before any scan");
        WayBack(app, view);
    }

    // Contract and docs/spec/03: after --scan, with the defaults (100 MB, 12 months), exactly the rows over the threshold
    // by allocated size and older than 12 months: old-video.mp4. Not the sparse 300 MB file (allocated about 0), not the
    // 110 MB file of 9 months ago, not the recent one, not the 60 MB one. Each row (ASP-10: its automation name joins its
    // cells) holds the name, the full folder, ByteUnits.Format(size) (size from an independent allocation oracle), the
    // short date of the modification and the type group; a tooltip in it holds the full folder; no hint (the fixture is
    // in the allowed root). The defaults are selected in MinimumSize and MinimumAge.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeOldListsTheRowsOverTheDefaultsByAllocatedSize()
    {
        // Preconditions, from the oracle: the threshold is crossed by the allocated size only.
        Assert.IsTrue(large!.AllocatedBytes(OldVideo) >= 100 * MB, "old-video.mp4 is not allocated over 100 MB");
        Assert.IsTrue(large.AllocatedBytes(SmallOld) is >= 50 * MB and < 100 * MB, "small-old.zip is not allocated between 50 and 100 MB");
        Assert.IsTrue(large.AllocatedBytes(SparseOld) < 50 * MB, $"sparse-old.mp4 is allocated {large.AllocatedBytes(SparseOld)} bytes: not sparse");
        Assert.AreEqual(300 * MB, new FileInfo(large.Sub(SparseOld)).Length, "sparse-old.mp4 length");

        using var app = AppSession.Start("--scan", large.Path);
        WaitForScanToFinish(app);
        var view = OpenView(app, "LargeOld", "LargeOldView");
        var list = AppSession.Find(view, "LargeOldList", ElementTimeout);
        Assert.IsTrue(IsOption(SelectedOption(app, AppSession.Find(view, "MinimumSize", ElementTimeout)), 100, "MB"), "MinimumSize default is not 100 MB");
        Assert.IsTrue(IsOption(SelectedOption(app, AppSession.Find(view, "MinimumAge", ElementTimeout)), 12, "month"), "MinimumAge default is not 12 months");
        AssertLargeOldRows(view, "defaults", OldVideo);

        var box = Table.ControlFor(list, "LargeOldSelect", large.Sub(OldVideo));
        Assert.IsNotNull(box, "no LargeOldSelect row for " + large.Sub(OldVideo) + ": " + Table.Dump(view, "LargeOldSelect"));
        var row = Table.RowOf(box)!;
        var name = row.Current.Name;
        foreach (var cell in new[] { OldVideo, large.Path, ByteUnits.Format(large.AllocatedBytes(OldVideo)), Table.ShortDate(threeYearsAgoUtc), TypeText(OldVideo) })
        {
            Assert.IsTrue(name.Contains(cell, StringComparison.OrdinalIgnoreCase), $"row Name \"{name}\" does not hold \"{cell}\"");
        }

        Assert.IsNull(Table.HintIn(name), $"a row in the allowed root carries a hint: \"{name}\"");
        Assert.IsTrue(Table.Texts(row).Any(t => t.Help.Contains(large.Path, StringComparison.OrdinalIgnoreCase)), "no tooltip (HelpText) in the row holds the full folder " + large.Path);
    }

    // Contract: the MinimumSize (50 MB, 100 MB, 500 MB, 1 GB) and MinimumAge (6 months, 12 months, 2 years) choices change
    // the list.
    [TestMethod]
    [TestCategory("Console")]
    public void TheSizeAndAgeChoicesChangeTheList()
    {
        using var app = AppSession.Start("--scan", large!.Path);
        WaitForScanToFinish(app);
        var view = OpenView(app, "LargeOld", "LargeOldView");
        var size = AppSession.Find(view, "MinimumSize", ElementTimeout);
        var age = AppSession.Find(view, "MinimumAge", ElementTimeout);
        AssertLargeOldRows(view, "100 MB, 12 months", OldVideo);

        Choose(app, size, 50, "MB");
        AssertLargeOldRows(view, "50 MB, 12 months", OldVideo, SmallOld);
        Choose(app, size, 500, "MB");
        AssertLargeOldRows(view, "500 MB, 12 months");
        Choose(app, size, 1, "GB");
        AssertLargeOldRows(view, "1 GB, 12 months");
        Choose(app, size, 100, "MB");
        AssertLargeOldRows(view, "100 MB, 12 months again", OldVideo);

        Choose(app, age, 6, "month");
        AssertLargeOldRows(view, "100 MB, 6 months", OldVideo, MidAge);
        Choose(app, age, 2, "year");
        AssertLargeOldRows(view, "100 MB, 2 years", OldVideo);
        Choose(app, size, 50, "MB");
        AssertLargeOldRows(view, "50 MB, 2 years", OldVideo, SmallOld);
        Choose(app, age, 12, "month");
        AssertLargeOldRows(view, "50 MB, 12 months", OldVideo, SmallOld);
    }

    // Contract: a row's context menu has RevealInExplorer (Text.RevealInExplorer) and MoveToRecycleBin
    // (Text.MoveToRecycleBin), enabled on a movable row (plan group 11; the fixture is in root (a) of docs/spec/02).
    // Neither is invoked; Escape closes the menu.
    [TestMethod]
    [TestCategory("Console")]
    public void ALargeOldRowContextMenuHasRevealAndAnEnabledMove()
    {
        using var app = AppSession.Start("--scan", large!.Path);
        WaitForScanToFinish(app);
        var view = OpenView(app, "LargeOld", "LargeOldView");
        AssertLargeOldRows(view, "defaults", OldVideo);
        FocusRowOf(AppSession.FindAll(view, "LargeOldSelect")[0]);
        Keys.PressWithShift(Keys.F10);
        try
        {
            var reveal = AppSession.WaitFor(() => app.FindInAnyWindow("RevealInExplorer"), TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reveal, "no RevealInExplorer item in the row's context menu");
            Assert.AreEqual(Text.RevealInExplorer, reveal.Current.Name);
            var move = app.FindInAnyWindow("MoveToRecycleBin");
            Assert.IsNotNull(move, "no MoveToRecycleBin item in the row's context menu");
            Assert.AreEqual(Text.MoveToRecycleBin, move.Current.Name);
            Assert.IsTrue(move.Current.IsEnabled, "Move to Recycle Bin… is disabled on a movable row (plan group 11)");
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }
    }

    // Contract and docs/spec/03: rows outside the allowed roots are display-only, hidden by default and shown with
    // ShowSystemItems (ASP-14: inside Large and old, the only list it changes); a display-only row (ASP-10 and the v1
    // additions' group 4) holds its full path and its hint, its LargeOldSelect box is disabled and not ticked, and (plan
    // group 11) its context menu's MoveToRecycleBin stays disabled. The fixture is in %LOCALAPPDATA% (inside AppData, not
    // Temp: outside the allowed roots, docs/spec/02 (a)).
    [TestMethod]
    [TestCategory("Console")]
    public void DisplayOnlyRowsShowOnlyWithShowSystemItems()
    {
        using var fixture = new Fixture("ui-displayonly", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        fixture.File(OldVideo, 120 * MB);
        fixture.SetModified(OldVideo, threeYearsAgoUtc);
        var fullPath = fixture.Sub(OldVideo);

        using var app = AppSession.Start("--scan", fixture.Path);
        WaitForScanToFinish(app);
        var view = OpenView(app, "LargeOld", "LargeOldView");
        var toggle = (TogglePattern)AppSession.Find(view, "ShowSystemItems", ElementTimeout).GetCurrentPattern(TogglePattern.Pattern);
        Assert.AreEqual(ToggleState.Off, toggle.Current.ToggleState, "Show system items is off by default");
        AppSession.Find(view, "LargeOldList", ElementTimeout);
        Assert.IsFalse(AppSession.WaitUntil(() => LargeOldFileNames(view, OldVideo).Length > 0, NeverWatch), "a display-only row is shown with Show system items off");
        toggle.Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => LargeOldFileNames(view, OldVideo).Length == 1, ElementTimeout), "the display-only row is not shown with Show system items on");
        var box = Table.ControlFor(view, "LargeOldSelect", fullPath);
        Assert.IsNotNull(box, "no LargeOldSelect row holds " + fullPath + ": " + Table.Dump(view, "LargeOldSelect"));
        var name = Table.RowName(box);
        Assert.IsNotNull(Table.HintIn(name), $"a display-only row has no hint: \"{name}\"");
        Assert.IsFalse(box.Current.IsEnabled, "a display-only row's LargeOldSelect box is enabled: " + name);
        Assert.AreEqual(ToggleState.Off, Table.State(box), "a display-only row is ticked: " + name);

        FocusRowOf(box);
        Keys.PressWithShift(Keys.F10);
        try
        {
            var move = AppSession.WaitFor(() => app.FindInAnyWindow("MoveToRecycleBin"), TimeSpan.FromSeconds(5));
            Assert.IsNotNull(move, "no MoveToRecycleBin item in the display-only row's context menu");
            Assert.IsFalse(move.Current.IsEnabled, "Move to Recycle Bin… is enabled on a display-only row");
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }
    }

    // ---- Duplicates --------------------------------------------------------------------------------------------------

    /// <summary>Three byte-identical 2 MB copies (copy-b newest, so the suggested keep, docs/spec/03) and one 2 MB file of
    /// the same length whose first and last 4 KB match but whose middle differs, so only the full comparison tells it apart.</summary>
    private static Fixture DuplicatesTree(out string[] copies, out string keep, out string different)
    {
        var fixture = new Fixture("ui-dups", Fixture.ProfileBaseFolder());
        var content = new byte[(int)(2 * MB)];
        new Random(8_0_2).NextBytes(content);
        var other = (byte[])content.Clone();
        other[content.Length / 2] ^= 0xFF;
        copies = [fixture.FileWithBytes(@"one\copy-a.dat", content), fixture.FileWithBytes(@"two\copy-b.dat", content), fixture.FileWithBytes(@"three\copy-c.dat", content)];
        different = fixture.FileWithBytes(@"four\other.dat", other);
        var now = DateTime.UtcNow;
        fixture.SetModified(@"one\copy-a.dat", now.AddDays(-3));
        fixture.SetModified(@"two\copy-b.dat", now.AddDays(-1));
        fixture.SetModified(@"three\copy-c.dat", now.AddDays(-2));
        fixture.SetModified(@"four\other.dat", now.AddDays(-1));
        keep = copies[1];
        return fixture;
    }

    /// <summary>Opens the Duplicates view after the scan, presses FindDuplicates and waits for the groups.</summary>
    private static AutomationElement FindDuplicates(AppSession app)
    {
        WaitForScanToFinish(app);
        var view = OpenView(app, "Duplicates", "DuplicatesView");
        Assert.IsNotNull(AppSession.Find(view, "CancelDuplicates", ElementTimeout), "no CancelDuplicates button");
        Click(AppSession.Find(view, "FindDuplicates", ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0, DuplicatesTimeout),
            "no DuplicateGroup appeared (status: \"" + AppSession.Find(app.Window(WindowTimeout), "Status", ElementTimeout).Current.Name + "\")");
        return view;
    }

    /// <summary>The DuplicateCopy (Move) box in the one row that holds <paramref name="path"/> (ASP-10: the row's automation
    /// name holds the name and the full folder).</summary>
    private static AutomationElement CopyOf(AutomationElement view, string path)
    {
        var copies = AppSession.FindAll(view, "DuplicateCopy").Where(c => Table.Holds(Table.RowName(c), path)).ToList();
        Assert.AreEqual(1, copies.Count, $"not one DuplicateCopy row holds {path}; copies: {Table.Dump(view, "DuplicateCopy")}");
        return copies[0];
    }

    private static ToggleState State(AutomationElement copy) => ((TogglePattern)copy.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    // Contract and docs/spec/03: FindDuplicates over the scan gives one group of three copies, Name "3 copies,
    // <ByteUnits.Format size> each"; a ReclaimableEstimate text holds Text.ReclaimableEstimate and the allocated bytes of
    // every copy but the suggested keep (oracle: the other two copies' allocation). The different file is in no group.
    // Each copy's row (ASP-10) holds a DuplicateCopy check box and the copy's path; the suggested keep's row (newest
    // modification) carries Text.SuggestedKeep, the others do not. MoveToRecycleBin in the view is disabled while nothing
    // is ticked (plan group 11).
    [TestMethod]
    [TestCategory("Console")]
    public void DuplicatesShowOneGroupOfThreeWithTheEstimateAndTheBadge()
    {
        using var fixture = DuplicatesTree(out var copies, out var keep, out var different);
        using var app = AppSession.Start("--scan", fixture.Path);
        var view = FindDuplicates(app);

        var groups = AppSession.FindAll(view, "DuplicateGroup");
        Assert.AreEqual(1, groups.Count, "groups: [" + string.Join(" | ", groups.Select(g => g.Current.Name)) + "]");
        var size = ByteUnits.Format(2 * MB);
        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, "3 copies, {0} each", size), groups[0].Current.Name, "group Name");

        var estimate = AppSession.TryFind(groups[0], "ReclaimableEstimate") ?? AppSession.Find(view, "ReclaimableEstimate", ElementTimeout);
        var reclaimable = copies.Where(c => c != keep).Sum(c => fixture.AllocatedBytes(Path.GetRelativePath(fixture.Path, c)));
        var estimateName = estimate.Current.Name;
        Assert.IsTrue(estimateName.Contains(Text.ReclaimableEstimate, StringComparison.Ordinal), $"ReclaimableEstimate \"{estimateName}\" lacks \"{Text.ReclaimableEstimate}\"");
        Assert.IsTrue(estimateName.Contains(ByteUnits.Format(reclaimable), StringComparison.Ordinal), $"ReclaimableEstimate \"{estimateName}\" lacks {ByteUnits.Format(reclaimable)}");

        var all = AppSession.FindAll(view, "DuplicateCopy");
        Assert.AreEqual(3, all.Count, "copies: " + Table.Dump(view, "DuplicateCopy"));
        Assert.IsFalse(all.Any(c => Table.Holds(Table.RowName(c), different)), "the file that differs in its middle is listed as a copy");
        foreach (var path in copies)
        {
            var copy = CopyOf(view, path);
            Assert.AreEqual(ControlType.CheckBox, copy.Current.ControlType, path + " is a check box");
            var row = Table.RowName(copy);
            var badged = row.Contains(Text.SuggestedKeep, StringComparison.Ordinal);
            Assert.AreEqual(path == keep, badged, $"{path}: row \"{row}\"; only the newest copy's row carries \"{Text.SuggestedKeep}\"");
        }

        var move = AppSession.Find(view, "MoveToRecycleBin", ElementTimeout);
        Assert.IsFalse(move.Current.IsEnabled, "Move to Recycle Bin… in the Duplicates view is enabled with nothing ticked");
    }

    // Contract and docs/spec/03: nothing is ticked at first, the suggested keep included; the kept copy is never moved
    // (ASP-10: Keep starts on the suggested copy, whose Move box is disabled): after ticking the two other copies, the
    // kept one, the last unticked, cannot be ticked. Plan group 11: Move is enabled while a copy is ticked; it is never
    // invoked here, so nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    public void NothingIsTickedAtFirstAndTheKeptCopyCannotBeTicked()
    {
        using var fixture = DuplicatesTree(out var copies, out var keep, out _);
        using var app = AppSession.Start("--scan", fixture.Path);
        var view = FindDuplicates(app);
        foreach (var path in copies)
        {
            Assert.AreEqual(ToggleState.Off, State(CopyOf(view, path)), path + " is ticked at first");
        }

        var others = copies.Where(c => c != keep).ToArray();
        foreach (var path in others)
        {
            var copy = CopyOf(view, path);
            Assert.IsTrue(copy.Current.IsEnabled, path + " cannot be ticked while two copies are unticked");
            ((TogglePattern)copy.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            Assert.IsTrue(AppSession.WaitUntil(() => State(CopyOf(view, path)) == ToggleState.On, TimeSpan.FromSeconds(3)), path + " did not tick");
        }

        var last = CopyOf(view, keep);
        Assert.IsFalse(last.Current.IsEnabled, "the kept copy's Move box is enabled: " + Table.Dump(view, "DuplicateCopy"));
        Table.TryToTick(last);

        Assert.IsFalse(AppSession.WaitUntil(() => State(CopyOf(view, keep)) != ToggleState.Off, NeverWatch), "the last unticked copy of the group was ticked");
        foreach (var path in others)
        {
            Assert.AreEqual(ToggleState.On, State(CopyOf(view, path)), path + " lost its tick");
        }

        Assert.IsTrue(AppSession.Find(view, "MoveToRecycleBin", ElementTimeout).Current.IsEnabled, "Move is disabled with two copies ticked (plan group 11)");
    }

    // ---- Uninstall ---------------------------------------------------------------------------------------------------

    /// <summary>The display names a registry hive lists under Uninstall in both views, as docs/spec/04's Windows note
    /// shows them: with a display name, not a system component. Read only.</summary>
    private static HashSet<string> ListedNames(RegistryHive hive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey, writable: false);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(sub, writable: false);
                if (entry?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (entry.GetValue("SystemComponent") is int system && system == 1)
                {
                    continue;
                }

                names.Add(name.Trim());
            }
        }

        return names;
    }

    /// <summary>Names of <paramref name="own"/> that are not in <paramref name="others"/> and are no prefix of any other
    /// listed name, so a row whose Name starts with one belongs to it.</summary>
    private static List<string> Unambiguous(HashSet<string> own, HashSet<string> others)
    {
        var every = own.Concat(others).ToList();
        return own.Where(n => !others.Contains(n) && !every.Any(o => !o.Equals(n, StringComparison.OrdinalIgnoreCase) && o.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int[] SettingsProcessIds() => Process.GetProcessesByName("SystemSettings").Select(p =>
    {
        using (p)
        {
            return p.Id;
        }
    }).ToArray();

    // Contract and docs/spec/04 Windows note: the Uninstall view lists the registered uninstallers, at least one row;
    // per-machine rows add Text.NeedsAdmin (oracle: HKLM's Uninstall entries with a display name, not system components,
    // read by the test), per-user rows do not. A selected row offers UninstallInSettings; it is never pressed, and no
    // Settings process starts during the test.
    [TestMethod]
    [TestCategory("Console")]
    public void UninstallListsTheAppsAndMarksPerMachineOnesNeedsAdmin()
    {
        var settingsBefore = SettingsProcessIds();
        var machine = ListedNames(RegistryHive.LocalMachine);
        var user = ListedNames(RegistryHive.CurrentUser);
        var machineOnly = Unambiguous(machine, user);
        var userOnly = Unambiguous(user, machine);
        Console.WriteLine($"oracle: {machine.Count} per-machine names ({machineOnly.Count} unambiguous), {user.Count} per-user names ({userOnly.Count} unambiguous)");

        using (var app = AppSession.Start())
        {
            var view = OpenView(app, "Uninstall", "UninstallView");
            var list = AppSession.Find(view, "UninstallList", ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(list, "UninstallRow").Count > 0, ElementTimeout), "the Uninstall list shows no row");
            var rows = CollectRows(list, "UninstallRow");
            Console.WriteLine($"{rows.Count} rows: " + string.Join(" | ", rows.Select(r => r.Name)));

            foreach (var name in userOnly)
            {
                foreach (var row in rows.Where(r => r.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.IsFalse(row.Name.Contains(Text.NeedsAdmin, StringComparison.Ordinal), $"the per-user row \"{row.Name}\" says \"{Text.NeedsAdmin}\"");
                }
            }

            // Select a row of a registered entry (a Store row is display-only and may offer no button); never press it.
            var registered = machine.Concat(user).ToList();
            var pick = AppSession.FindAll(list, "UninstallRow").FirstOrDefault(r => registered.Any(n => r.Current.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)));
            Assert.IsNotNull(pick, "no visible row belongs to a registered uninstall entry");
            if (pick.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                ((SelectionItemPattern)select).Select();
            }
            else
            {
                // Never Invoke a row: an invoked row could be taken as the hand-off. Focus selects it in a list.
                pick.SetFocus();
            }

            var offer = FindAnywhere(app, "UninstallInSettings", ElementTimeout);
            Assert.AreEqual(ControlType.Button, offer.Current.ControlType, "UninstallInSettings is a button");
            WayBack(app, view);

            if (machineOnly.Count > 0)
            {
                var matched = machineOnly.Where(n => rows.Any(r => r.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
                Assert.IsTrue(matched.Count > 0, "no row for any per-machine entry: [" + string.Join(", ", machineOnly) + "]");
                foreach (var name in matched)
                {
                    foreach (var row in rows.Where(r => r.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Assert.IsTrue(row.Name.Contains(Text.NeedsAdmin, StringComparison.Ordinal), $"the per-machine row \"{row.Name}\" lacks \"{Text.NeedsAdmin}\"");
                    }
                }
            }
        }

        var started = SettingsProcessIds().Except(settingsBefore).ToArray();
        Assert.AreEqual(0, started.Length, "a Windows Settings process started during the Uninstall test (pid " + string.Join(", ", started) + ")");
        if (machineOnly.Count == 0)
        {
            Assert.Inconclusive("this machine lists no per-machine uninstall entry (HKLM, either view, with a display name, not a system component, not also per-user): the \"needs admin\" row cannot be checked here");
        }
    }

    // Contract (8b, with ASP-9 and ASP-14): each of the three views opens from its sidebar item, and the Overview item
    // returns to the map of the scan.
    [TestMethod]
    [TestCategory("Console")]
    public void EachViewOpensFromItsSidebarItemAndOverviewGoesBackToTheMap()
    {
        using var fixture = new Fixture("ui-views-back");
        fixture.File(@"folder\a.bin", 64 * 1024);
        using var app = AppSession.Start("--scan", fixture.Path);
        WaitForScanToFinish(app);
        foreach (var (item, viewId) in new[] { ("LargeOld", "LargeOldView"), ("Duplicates", "DuplicatesView"), ("Uninstall", "UninstallView") })
        {
            var view = OpenView(app, item, viewId);
            Click(WayBack(app, view));
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow(viewId) is not { } v || v.Current.IsOffscreen, ElementTimeout), viewId + " is still shown after the Overview item");
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("Map") is { } m && !m.Current.IsOffscreen, ElementTimeout), "the map is not shown after the Overview item in " + viewId);
        }
    }
}
