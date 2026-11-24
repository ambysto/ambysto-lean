using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-15 UI contract (intent/2026-10-18-asp-15-duplicates-helpers.md: "UI contract", "Rules", "What a rule touches",
/// "Preview"), written before the UI exists, from the contract and the Core only. The Duplicates view gets: type tabs
/// (<c>DuplicateTypes</c>: All files, then spec 01's seven groups, each "Label (n)" with its number of groups; All files
/// current at the start); a <c>SelectByRule</c> drop-down beside Move (entries <c>RuleNewest</c>, <c>RuleOldest</c>,
/// <c>RuleShortest</c>, <c>RuleInFolder</c> (folder picker), a separator, <c>ClearSelection</c>; nothing ticked until an entry
/// is chosen); a rule keeps one copy per group (Keep) and ticks the others, in the current tab only, never a hinted copy, and
/// the status line says <c>Text.SelectedByRule(copies, groups)</c>; alternating group backgrounds (<c>LeanPanel</c> and
/// <c>LeanGround</c>); the eye <c>PreviewCopy</c> on the rows of the eight picture types, with the row actions (hover and
/// focus), opening a flyout with the picture, its size in pixels and its path, or <c>Text.PreviewUnavailable</c> or
/// <c>Text.PreviewOnlineOnly</c>; rows one line high and no clipped control at the default window size.
/// The words the Core does not have yet (<c>SelectedByRule</c>, <c>PreviewUnavailable</c>, <c>PreviewOnlineOnly</c>) are read
/// at run time (<see cref="Ui.Copy"/>), so a missing one fails only the test that needs it. The oracle for each rule is the
/// fixture table below (modification age and path length, all distinct, no ties), not the Core. Nothing is moved: the one
/// confirmation opened is cancelled. Console session only.
/// <para>Readings the contract leaves open, settled here: all seven type tabs are always shown, an empty one with "(0)";
/// the count of All files is optional, and when it is shown it is the number of groups; the rule's status counts the copies
/// really ticked and the groups the rule was applied to (a group with no copy inside the folder is not one); after a rule,
/// the Keep choice of every group in the tab is the kept copy (a hinted display-only copy included: the view already
/// shows its Keep chosen when it is the suggestion); the pixel size in the flyout is written as the two numbers with a separator between, for example
/// "320 × 1100", and the picture is 320 px wide so that the original and the decoded size are one number.</para></summary>
[TestClass]
[DoNotParallelize]
public sealed partial class DuplicatesHelpersTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;

    // ---- the fixture table ------------------------------------------------------------------------------------------------

    /// <summary>A copy: its path below the fixture, how many days ago it was modified, and whether it lies in the protected
    /// folder (so it is display-only, never ticked).</summary>
    private sealed record Cp(string Rel, int DaysAgo, bool Hinted = false);

    /// <summary>A group of byte-identical copies, in the type tab <c>Tab</c>.</summary>
    private sealed record Grp(string Name, string Tab, int Seed, long Length, Cp[] Copies);

    private static readonly string DeepFolder = string.Join('\\', Enumerable.Range(1, 8).Select(i => $"deep-folder-segment-{i:D2}"));

    // Newest / Oldest / Shortest (by path length) / the inside-folder rule choose different copies; no ties anywhere.
    //   v1: Newest q, Oldest r, Shortest p       v2: Newest b, Oldest a, Shortest a
    //   a1: Newest y, Oldest x, Shortest x       d1: Newest a (long path), Oldest b, Shortest b
    //   o1: Newest a, Oldest b (a 250-character path), Shortest a
    //   x:  Newest out2 (long path), Oldest in, Shortest out1; the folder x\in holds only x\in\deeper\f.dat (the oldest copy)
    //   h:  guarded\h.pdf is protected (display-only) and the newest: Newest keeps it, Oldest keeps h-c, Shortest keeps h-b
    private static readonly Grp[] Groups =
    [
        new("v1", Text.TypeVideo, 1, MB + (4 * KB),
            [new(@"v1\p\clip.mp4", 10), new(@"v1\q-long-folder-name-to-make-the-path-long\clip.mp4", 2), new(@"v1\r-medium-name\clip.mp4", 30)]),
        new("v2", Text.TypeVideo, 2, MB + (8 * KB), [new(@"v2\a\take.mkv", 5), new(@"v2\b-longer-folder-name\take.mkv", 1)]),
        new("a1", Text.TypeArchive, 3, MB + (12 * KB), [new(@"a1\x\pack.zip", 20), new(@"a1\y-longer-name\pack.zip", 3)]),
        new("d1", Text.TypeDocument, 4, MB + (16 * KB), [new(@"d1\a-longer-folder-name-here\paper.pdf", 4), new(@"d1\b\paper.pdf", 40)]),
        new("h", Text.TypeDocument, 6, MB + (24 * KB), [new(@"guarded\h.pdf", 1, Hinted: true), new(@"h-b\h.pdf", 15), new(@"h-c-longer\h.pdf", 50)]),
        new("o1", Text.TypeOther, 5, MB + (20 * KB), [new(@"o1\a\blob.bin", 6), new(@"o1\b\" + DeepFolder + @"\blob.bin", 60)]),
        new("x", Text.TypeOther, 7, MB + (28 * KB), [new(@"x\in\deeper\f.dat", 90), new(@"x\out1\f.dat", 30), new(@"x\out2-longer\f.dat", 1)]),
    ];

    /// <summary>The seven type tabs after All files, in spec 01's order.</summary>
    private static readonly string[] TypeLabels =
        [Text.TypeVideo, Text.TypeDiskImage, Text.TypeArchive, Text.TypeDocument, Text.TypeApplication, Text.TypeVirtualMachineImage, Text.TypeOther];

    private sealed record RuleCase(string MenuId, string Name, Func<Grp, string?, Cp?> Keep);

    private static readonly RuleCase Newest = new("RuleNewest", "newest", (g, _) => g.Copies.MinBy(c => c.DaysAgo));

    private static readonly RuleCase Oldest = new("RuleOldest", "oldest", (g, _) => g.Copies.MaxBy(c => c.DaysAgo));

    private static readonly RuleCase Shortest = new("RuleShortest", "shortest", (g, _) => g.Copies.MinBy(c => c.Rel.Length));

    private static readonly RuleCase InFolder = new("RuleInFolder", "inside a folder", (g, folder) =>
        g.Copies.Where(c => Path.GetFullPath(fx!.Sub(c.Rel)).StartsWith(Path.TrimEndingDirectorySeparator(folder!) + "\\", StringComparison.OrdinalIgnoreCase)).MinBy(c => c.DaysAgo));

    /// <summary>The controls of a copy row that can take the keyboard focus, in the order tried.</summary>
    private static readonly string[] RowControls = ["DuplicateCopy", "DuplicateKeep"];

    private static MoveFixture? fx;
    private static MoveFixture? pics;

    private const int PictureWidth = 320;
    private const int PictureHeight = 1100;

    /// <summary>The picture types of the preview, one group of two copies each; the bmp is a real picture, the rest are
    /// bytes that no decoder reads.</summary>
    private static readonly string[] PictureExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>Non-picture groups of the picture fixture.</summary>
    private static readonly string[] OtherExtensions = [".mp4", ".pdf", ".bin"];

    private static string PictureCopy(string extension, int copy) => $@"pic{extension.TrimStart('.')}-{(copy == 0 ? 'a' : 'b')}\img{extension}";

    [ClassInitialize]
    public static void BuildTheTrees(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fx = new MoveFixture("asp15-dups");
        var now = DateTime.UtcNow;
        foreach (var group in Groups)
        {
            Assert.AreEqual(group.Copies.Length, group.Copies.Select(c => c.DaysAgo).Distinct().Count(), group.Name + ": two copies modified on one day (a tie)");
            Assert.AreEqual(group.Copies.Length, group.Copies.Select(c => c.Rel.Length).Distinct().Count(), group.Name + ": two paths of one length (a tie)");
            foreach (var copy in group.Copies)
            {
                Assert.IsTrue(fx.Sub(copy.Rel).Length < 255, "the path of " + copy.Rel + " is not under MAX_PATH");
                fx.File(copy.Rel, group.Length, group.Seed);
                fx.SetModified(copy.Rel, now.AddDays(-copy.DaysAgo));
            }
        }

        pics = new MoveFixture("asp15-pics");
        var bmp = Bmp(PictureWidth, PictureHeight, 91);
        var number = 0;
        foreach (var extension in PictureExtensions.Concat(OtherExtensions))
        {
            number++;
            for (var copy = 0; copy < 2; copy++)
            {
                var rel = PictureCopy(extension, copy);
                if (extension == ".bmp")
                {
                    pics.FileWithBytes(rel, bmp);
                }
                else
                {
                    pics.File(rel, MB + (number * 4 * KB), 100 + number);
                }

                pics.SetModified(rel, now.AddDays(-(1 + (copy * 5))));
            }
        }
    }

    [ClassCleanup]
    public static void RemoveTheTrees()
    {
        if (BigTree.IsValueCreated)
        {
            BigTree.Value.Dispose();
        }

        fx?.Dispose();
        pics?.Dispose();
        DisposeRound1Trees();
    }

    /// <summary>A 24-bit BMP of <paramref name="width"/> × <paramref name="height"/> pixels with seeded noise (a real
    /// picture, at least 1 MB, so that the duplicates search takes it).</summary>
    private static byte[] Bmp(int width, int height, int seed)
    {
        var rowSize = ((width * 3) + 3) / 4 * 4;
        var data = new byte[54 + (rowSize * height)];
        void Put(int at, int value, int bytes)
        {
            for (var i = 0; i < bytes; i++)
            {
                data[at + i] = (byte)(value >> (8 * i));
            }
        }

        data[0] = (byte)'B';
        data[1] = (byte)'M';
        Put(2, data.Length, 4);
        Put(10, 54, 4);
        Put(14, 40, 4);
        Put(18, width, 4);
        Put(22, height, 4);
        Put(26, 1, 2);
        Put(28, 24, 2);
        Put(34, rowSize * height, 4);
        Put(38, 2835, 4);
        Put(42, 2835, 4);
        new Random(seed).NextBytes(data.AsSpan(54));
        return data;
    }

    // ---- helpers: starting, reading, tabs ---------------------------------------------------------------------------------

    private static string FullPath(Cp copy) => fx!.Sub(copy.Rel);

    private static IEnumerable<string> PathsOf(IEnumerable<Grp> groups) => groups.SelectMany(g => g.Copies).Select(FullPath);

    /// <summary>Starts the app on <paramref name="fixture"/> (the protected folder <c>guarded</c> of the main fixture, or none),
    /// scans, opens Duplicates, presses FindDuplicates and waits until the search is over with groups listed.</summary>
    private static AutomationElement OpenDuplicates(AppSession app)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0
                                                 && AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout).Current.IsEnabled, Ui.DuplicatesTimeout),
            "no DuplicateGroup appeared (status: \"" + Status(app) + "\")");
        return view;
    }

    private static string Status(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout).Current.Name;

    /// <summary>One copy's row as the lists show it: its tick box, its Keep radio and its height.</summary>
    private sealed record Cell(string Row, bool Ticked, bool TickEnabled, bool Kept, double? Height, double? Spread);

    private static AutomationElement? Scroller(AutomationElement view)
    {
        try
        {
            return view.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Every copy row of the view (the virtualised list scrolled from top to bottom so each row is realised once),
    /// keyed by the row's automation name, which holds the file name and the full folder.</summary>
    /// <summary>Waits until every realised row's rectangle (and the viewport's) is the same in two samples about 150 ms apart:
    /// the view settles by a pixel or two in the first 200 ms after it opens (a row reads 34 px, then 36).</summary>
    private static void WaitForRowsToSettle(AutomationElement view)
    {
        string Sample()
        {
            var parts = new List<string> { (Scroller(view)?.Current.BoundingRectangle ?? view.Current.BoundingRectangle).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            foreach (var box in Table.Controls(view, "DuplicateCopy"))
            {
                try
                {
                    parts.Add(Table.RowOf(box)?.Current.BoundingRectangle.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
                }
                catch (ElementNotAvailableException)
                {
                    parts.Add("gone");
                }
            }

            return string.Join(";", parts);
        }

        var last = Sample();
        var settled = AppSession.WaitUntil(() =>
        {
            System.Threading.Thread.Sleep(150);
            var now = Sample();
            var same = now == last;
            last = now;
            return same;
        }, Ui.ElementTimeout);
        Assert.IsTrue(settled, "the rows of the Duplicates view never stopped changing size or place: " + last);
    }

    private static List<Cell> Cells(AutomationElement view)
    {
        var seen = new Dictionary<string, Cell>(StringComparer.Ordinal);
        var scroller = Scroller(view);
        var viewport = scroller?.Current.BoundingRectangle ?? view.Current.BoundingRectangle;

        void Take()
        {
            foreach (var box in Table.Controls(view, "DuplicateCopy"))
            {
                try
                {
                    var row = Table.RowOf(box);
                    if (row is null)
                    {
                        continue;
                    }

                    var name = row.Current.Name ?? string.Empty;
                    var keep = Table.InRow(row, "DuplicateKeep");
                    var rect = row.Current.BoundingRectangle;
                    // Wholly inside the viewport, by rectangle: UI Automation clips a row's rectangle to the viewport (a row the
                    // viewport cuts reads short, flush with the edge), so a row within 2 px of an edge is not measured here; it is
                    // measured at another scroll position, where it lies inside.
                    double? height = !rect.IsEmpty && rect.Top > viewport.Top + 2 && rect.Bottom < viewport.Bottom - 2 ? rect.Height : null;
                    double? spread = null;
                    if (height is not null)
                    {
                        var centres = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                            .Select(t => t.Current.BoundingRectangle).Where(r => !r.IsEmpty).Select(r => r.Top + (r.Height / 2)).ToList();
                        spread = centres.Count == 0 ? 0 : centres.Max() - centres.Min();
                    }

                    var cell = new Cell(name, Table.State(box) == ToggleState.On, box.Current.IsEnabled, keep is not null && Table.IsChosen(keep), height, spread);
                    if (!seen.TryGetValue(name, out var before) || (before.Height is null && cell.Height is not null))
                    {
                        seen[name] = cell;
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        Take();
        if (scroller is not null)
        {
            var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
            var step = Math.Max(5.0, scroll.Current.VerticalViewSize / 2);
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
            System.Threading.Thread.Sleep(150);
        }

        return [.. seen.Values];
    }

    private static Cell? CellOf(List<Cell> cells, string path) => cells.FirstOrDefault(c => Table.Holds(c.Row, path));

    private static string Dump(List<Cell> cells) =>
        "[" + string.Join(" | ", cells.Select(c => $"{c.Row} ({(c.Ticked ? "ticked" : "unticked")}{(c.Kept ? ", kept" : string.Empty)}{(c.TickEnabled ? string.Empty : ", disabled")})")) + "]";

    // -- the type tabs

    private sealed record Tab(string Label, int? Count, AutomationElement Element);

    private static AutomationElement TypesBar(AutomationElement view) => AppSession.Find(view, "DuplicateTypes", Ui.ElementTimeout);

    private static readonly Regex TabName = new(@"^(?<label>.*?)(?: \((?<n>\d+)\))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The tabs of <c>DuplicateTypes</c>, left to right (top to bottom when they wrap): its items that can be selected,
    /// or else invoked.</summary>
    private static List<Tab> Tabs(AutomationElement view)
    {
        var bar = TypesBar(view);
        var items = bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().ToList();
        if (items.Count == 0)
        {
            items = bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true)).Cast<AutomationElement>().ToList();
        }

        return items
            .OrderBy(e => Math.Round(e.Current.BoundingRectangle.Top / 10))
            .ThenBy(e => e.Current.BoundingRectangle.Left)
            .Select(e =>
            {
                var match = TabName.Match((e.Current.Name ?? string.Empty).Trim());
                return new Tab(match.Groups["label"].Value, match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : null, e);
            }).ToList();
    }

    private static string TabDump(List<Tab> tabs) => "[" + string.Join(" | ", tabs.Select(t => t.Element.Current.Name)) + "]";

    private static bool IsChosen(AutomationElement tab)
    {
        try
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                return ((SelectionItemPattern)select).Current.IsSelected;
            }

            return tab.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) && ((TogglePattern)toggle).Current.ToggleState == ToggleState.On;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static List<string> ChosenTabs(AutomationElement view) => Tabs(view).Where(t => IsChosen(t.Element)).Select(t => t.Label).ToList();

    private static Tab TabNamed(AutomationElement view, string label)
    {
        Tab? found = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (found = Tabs(view).FirstOrDefault(t => t.Label == label)) is not null, Ui.ElementTimeout),
            $"no type tab \"{label}\": {TabDump(Tabs(view))}");
        return found!;
    }

    private static void ChooseTab(AutomationElement view, string label)
    {
        Ui.Click(TabNamed(view, label).Element);
        Assert.IsTrue(AppSession.WaitUntil(() => ChosenTabs(view) is [var only] && only == label, Ui.ElementTimeout),
            $"the tab {label} is not the one chosen tab after a click: chosen [{string.Join(" | ", ChosenTabs(view))}]");
        System.Threading.Thread.Sleep(300); // the list is rebuilt
    }

    // -- the Select by rule drop-down

    private static AutomationElement RuleButton(AutomationElement view) => AppSession.Find(view, "SelectByRule", Ui.ElementTimeout);

    private static void OpenMenu(AppSession app, AutomationElement view)
    {
        var button = RuleButton(view);
        if (button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
        }
        else
        {
            Ui.Click(button);
        }

        Assert.IsNotNull(AppSession.WaitFor(() => app.FindInAnyWindow("RuleNewest"), Ui.ElementTimeout), "the SelectByRule drop-down opened no RuleNewest entry");
    }

    private static void CloseMenu(AppSession app)
    {
        if (app.FindInAnyWindow("RuleNewest") is null)
        {
            return;
        }

        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("RuleNewest") is not { } e || e.Current.IsOffscreen, Ui.ElementTimeout), "Escape did not close the SelectByRule drop-down");
    }

    /// <summary>Opens the drop-down and invokes the entry <paramref name="menuId"/>.</summary>
    private static void ChooseRule(AppSession app, AutomationElement view, string menuId)
    {
        OpenMenu(app, view);
        var item = app.FindInAnyWindow(menuId);
        Assert.IsNotNull(item, $"no {menuId} entry in the SelectByRule drop-down");
        Assert.IsTrue(item.Current.IsEnabled, $"{menuId} is disabled");
        Ui.Click(item);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("RuleNewest") is not { } e || e.Current.IsOffscreen, Ui.ElementTimeout) || menuId == "RuleInFolder",
            $"the drop-down stays open after choosing {menuId}");
    }

    // -- expectations

    /// <summary>The kept copy of <paramref name="group"/> under <paramref name="rule"/> (null: the group is left alone).</summary>
    private static Cp? KeptBy(RuleCase? rule, Grp group, string? folder) => rule?.Keep(group, folder);

    /// <summary>The copies <paramref name="rule"/> ticks in <paramref name="groups"/>, and the groups it touches.</summary>
    private static (HashSet<string> Ticked, int Groups) TicksOf(RuleCase rule, IEnumerable<Grp> groups, string? folder)
    {
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touched = 0;
        foreach (var group in groups)
        {
            if (KeptBy(rule, group, folder) is not { } keep)
            {
                continue;
            }

            touched++;
            foreach (var copy in group.Copies.Where(c => c != keep && !c.Hinted))
            {
                ticked.Add(FullPath(copy));
            }
        }

        return (ticked, touched);
    }

    /// <summary>Asserts, for every copy of <paramref name="groups"/> in <paramref name="cells"/>, that it is ticked exactly when
    /// <paramref name="ticked"/> holds it, and that its Keep is the kept copy of the rule (<paramref name="rule"/> given and the
    /// group touched) or else the suggested copy (the newest).</summary>
    private static void AssertGroups(List<Cell> cells, IEnumerable<Grp> groups, HashSet<string> ticked, RuleCase? rule, string? folder, string where)
    {
        foreach (var group in groups)
        {
            var keep = KeptBy(rule, group, folder) ?? Newest.Keep(group, null)!;
            foreach (var copy in group.Copies)
            {
                var cell = CellOf(cells, FullPath(copy));
                Assert.IsNotNull(cell, $"{where}: no row for {FullPath(copy)}: {Dump(cells)}");
                Assert.AreEqual(ticked.Contains(FullPath(copy)), cell.Ticked, $"{where}: {copy.Rel} should be {(ticked.Contains(FullPath(copy)) ? "ticked" : "unticked")}: {Dump(cells)}");
                Assert.AreEqual(copy == keep, cell.Kept, $"{where}: Keep of {copy.Rel} should be {(copy == keep ? "chosen" : "not chosen")}: {Dump(cells)}");
            }
        }
    }

    private static void AssertNothingTicked(AutomationElement view, string where)
    {
        var cells = Cells(view);
        Assert.IsTrue(cells.Count > 0, where + ": no copy row");
        Assert.AreEqual(0, cells.Count(c => c.Ticked), where + ": a copy is ticked: " + Dump(cells));
    }

    private static string RuleStatus(int copies, int groups) => Ui.Copy("SelectedByRule", copies, groups);

    private static void AssertStatus(AppSession app, string expected, string where) =>
        Assert.IsTrue(AppSession.WaitUntil(() => Status(app) == expected, Ui.ElementTimeout), $"{where}: the status line is \"{Status(app)}\", expected \"{expected}\"");

    private static SettingsBackup ProtectGuarded()
    {
        var backup = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders(fx!.Sub("guarded"));
        return backup;
    }

    private static AppSession StartMain() => AppSession.Start("--scan", fx!.Path);

    // ---- 1. type tabs ---------------------------------------------------------------------------------------------------

    // Contract "Type tabs": above the list, All files first, then spec 01's seven groups, each with its number of groups
    // ("Video (2)"); All files is the one current tab at the start. The fixture: Video 2 groups, Archive 1, Document 2, Other 2,
    // the other three tabs 0. All seven tabs are always shown (reading settled in the class summary). A tab with a count shows
    // it as "Label (n)"; All files may or may not carry one, and if it does it is the number of groups, 7.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TypeTabsListAllFilesThenTheSevenGroupsWithTheirGroupCounts()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var tabs = Tabs(view);
        Console.WriteLine("tabs: " + TabDump(tabs));
        Assert.AreEqual(8, tabs.Count, "expected All files and seven type tabs: " + TabDump(tabs));
        Assert.AreEqual(Text.AllFiles, tabs[0].Label, "the first tab: " + TabDump(tabs));
        if (tabs[0].Count is { } all)
        {
            Assert.AreEqual(Groups.Length, all, "the count on All files is the number of groups: " + TabDump(tabs));
        }

        for (var i = 0; i < TypeLabels.Length; i++)
        {
            var tab = tabs[i + 1];
            var expected = Groups.Count(g => g.Tab == TypeLabels[i]);
            Assert.AreEqual(TypeLabels[i], tab.Label, $"tab {i + 2} is not {TypeLabels[i]}: {TabDump(tabs)}");
            Assert.AreEqual(expected, tab.Count, $"the tab {TypeLabels[i]} should say ({expected}): {TabDump(tabs)}");
        }

        var bar = TypesBar(view).Current.BoundingRectangle;
        var firstGroup = AppSession.FindAll(view, "DuplicateGroup").Min(g => g.Current.BoundingRectangle.Top);
        Assert.IsTrue(bar.Bottom <= firstGroup + 1, $"the tabs ({bar}) are not above the list (first group top {firstGroup})");
        CollectionAssert.AreEqual(new[] { Text.AllFiles }, ChosenTabs(view), "the current tab at the start");
        AssertNothingTicked(view, "after the search");
    }

    // Contract "Type tabs": a group belongs to a tab by its files' type group, and choosing a tab shows only that type's
    // groups: Video has v1 and v2 (5 copies), Document d1 and h, Archive a1, Other o1 and x, Application none (no group, no
    // copy); All files again shows every group.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ChoosingATabShowsOnlyThatTypesGroups()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        foreach (var label in new[] { Text.TypeVideo, Text.TypeDocument, Text.TypeArchive, Text.TypeOther, Text.TypeApplication, Text.AllFiles })
        {
            ChooseTab(view, label);
            var inTab = label == Text.AllFiles ? Groups : Groups.Where(g => g.Tab == label).ToArray();
            var cells = Cells(view);
            foreach (var group in Groups)
            {
                foreach (var copy in group.Copies)
                {
                    Assert.AreEqual(inTab.Contains(group), CellOf(cells, FullPath(copy)) is not null, $"tab {label}: {copy.Rel} should be {(inTab.Contains(group) ? "listed" : "absent")}: {Dump(cells)}");
                }
            }

            if (label != Text.AllFiles)
            {
                Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count == inTab.Length, Ui.ElementTimeout),
                    $"tab {label}: {AppSession.FindAll(view, "DuplicateGroup").Count} DuplicateGroup elements, expected {inTab.Length}");
            }
        }
    }

    // ---- 2. the drop-down -----------------------------------------------------------------------------------------------

    // Contract "Select by rule": a ghost drop-down button (SelectByRule), labelled "Select by rule", beside Move; its menu has
    // RuleNewest, RuleOldest, RuleShortest, RuleInFolder, a separator, then ClearSelection, in that order. Nothing is ticked
    // after a scan, and none after opening the menu and closing it without choosing (Escape); the status line says nothing
    // was ticked.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void SelectByRuleIsADropDownBesideMoveAndNothingHappensUntilAnEntryIsChosen()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var button = RuleButton(view);
        Assert.AreEqual(ControlType.Button, button.Current.ControlType, "SelectByRule is a button: " + Ui.Describe(button));
        StringAssert.Contains(button.Current.Name, "Select by rule", StringComparison.OrdinalIgnoreCase, "the label of SelectByRule");
        Assert.IsTrue(button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _) || button.TryGetCurrentPattern(InvokePattern.Pattern, out _), "SelectByRule can neither be expanded nor invoked");
        Assert.IsTrue(button.Current.IsEnabled && !button.Current.IsOffscreen, "SelectByRule is disabled or off screen with groups listed");
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.BoundingRectangle;
        var rule = button.Current.BoundingRectangle;
        Assert.IsTrue(Math.Abs((rule.Top + (rule.Height / 2)) - (move.Top + (move.Height / 2))) <= Math.Max(rule.Height, move.Height) / 2 && (rule.Right <= move.Left + 1 || move.Right <= rule.Left + 1),
            $"SelectByRule {rule} is not beside Move {move} (same row, not overlapping)");
        var statusBefore = Status(app);
        AssertNothingTicked(view, "after the search");

        OpenMenu(app, view);
        try
        {
            var ids = new[] { "RuleNewest", "RuleOldest", "RuleShortest", "RuleInFolder", "ClearSelection" };
            var items = ids.Select(id => app.FindInAnyWindow(id)).ToList();
            for (var i = 0; i < ids.Length; i++)
            {
                Assert.IsNotNull(items[i], $"no {ids[i]} entry in the drop-down");
                Assert.IsFalse(string.IsNullOrWhiteSpace(items[i]!.Current.Name), $"{ids[i]} has no name");
            }

            var tops = items.Select(i => i!.Current.BoundingRectangle.Top).ToList();
            for (var i = 1; i < tops.Count; i++)
            {
                Assert.IsTrue(tops[i] > tops[i - 1], $"{ids[i]} is not below {ids[i - 1]} in the drop-down (tops {string.Join(", ", tops)})");
            }

            StringAssert.Contains(items[4]!.Current.Name, "Clear selection", StringComparison.OrdinalIgnoreCase, "the label of ClearSelection");
            var separators = Ui.TopWindows(app).SelectMany(t => t.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Separator)).Cast<AutomationElement>())
                .Select(s => s.Current.BoundingRectangle).Where(r => !r.IsEmpty && r.Top >= items[3]!.Current.BoundingRectangle.Top && r.Bottom <= tops[4] + 2).ToList();
            Assert.IsTrue(separators.Count > 0, "no separator between RuleInFolder and ClearSelection in the drop-down");
        }
        finally
        {
            CloseMenu(app);
        }

        System.Threading.Thread.Sleep(500);
        AssertNothingTicked(view, "after opening the drop-down without choosing");
        Assert.AreEqual(statusBefore, Status(app), "the status line changed with no entry chosen");
        Assert.IsFalse(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "Move is enabled with nothing ticked");
    }

    // ---- 3. the rules ---------------------------------------------------------------------------------------------------

    /// <summary>The Video tab: the rule keeps one copy of v1 and v2 and ticks the others (the oracle is the fixture table);
    /// the status line is Text.SelectedByRule(copies, groups); Keep is the kept copy; every other tab's copies are untouched, each
    /// group still on its suggested copy; and the ticks are still there when All files is chosen.</summary>
    private static void RuleInTheVideoTab(RuleCase rule)
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeVideo);
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var (ticked, touched) = TicksOf(rule, video, null);
        Assert.IsTrue(ticked.Count >= 3, "precondition: the rule ticks the other copies of the video groups");

        ChooseRule(app, view, rule.MenuId);
        AssertStatus(app, RuleStatus(ticked.Count, touched), rule.Name);
        var cells = Cells(view);
        AssertGroups(cells, video, ticked, rule, null, rule.Name + " in the Video tab");
        Assert.IsTrue(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "Move is disabled with copies ticked");

        ChooseTab(view, Text.TypeDocument);
        var documents = Groups.Where(g => g.Tab == Text.TypeDocument).ToArray();
        AssertGroups(Cells(view), documents, [], null, null, rule.Name + ": the Document tab is untouched");

        ChooseTab(view, Text.AllFiles);
        var everything = Cells(view);
        AssertGroups(everything, video, ticked, rule, null, rule.Name + ": the Video ticks in All files");
        AssertGroups(everything, Groups.Except(video), [], null, null, rule.Name + ": the other tabs in All files");
        Assert.AreEqual(ticked.Count, everything.Count(c => c.Ticked), rule.Name + ": ticks in All files: " + Dump(everything));
    }

    // Contract "Rules": Newest modified keeps the copy modified last and ticks the others (v1 keeps q, v2 keeps b).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void NewestModifiedKeepsTheNewestCopyOfEveryGroupInTheTab() => RuleInTheVideoTab(Newest);

    // Contract "Rules": Oldest modified keeps the copy modified first (v1 keeps r, v2 keeps a).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void OldestModifiedKeepsTheOldestCopyOfEveryGroupInTheTab() => RuleInTheVideoTab(Oldest);

    // Contract "Rules": Shortest path keeps the copy with the fewest characters in its path (v1 keeps p, v2 keeps a).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ShortestPathKeepsTheCopyWithTheShortestPathInEveryGroupOfTheTab() => RuleInTheVideoTab(Shortest);

    // Contract "Rules" and "What a rule touches": the rule applies to the groups of All files too (every tab), and a protected
    // copy (guarded\h.pdf, display-only) is never ticked: Newest keeps it (the newest) and ticks h-b and h-c; Oldest keeps h-c and
    // ticks h-b but not the protected copy; Shortest keeps h-b and ticks h-c but not the protected copy. The status counts only
    // the copies really ticked.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AProtectedCopyIsNeverTickedByAnyRule()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var protectedPath = FullPath(Groups.Single(g => g.Name == "h").Copies.Single(c => c.Hinted));
        foreach (var rule in new[] { Newest, Oldest, Shortest })
        {
            var (ticked, touched) = TicksOf(rule, Groups, null);
            Assert.IsFalse(ticked.Contains(protectedPath), "precondition: the oracle never ticks the protected copy");
            ChooseRule(app, view, rule.MenuId);
            AssertStatus(app, RuleStatus(ticked.Count, touched), rule.Name + " in All files");
            var cells = Cells(view);
            AssertGroups(cells, Groups, ticked, rule, null, rule.Name + " in All files");
            var hinted = CellOf(cells, protectedPath);
            Assert.IsNotNull(hinted, "no row for the protected copy");
            Assert.IsFalse(hinted.Ticked, rule.Name + ": the protected copy is ticked");
            Assert.IsFalse(hinted.TickEnabled, rule.Name + ": the protected copy can be ticked (its box is enabled)");
        }
    }

    // Contract "What a rule touches": a rule applies only to the groups of the current tab. Newest in Video, then Oldest in
    // Document: Video keeps the Newest ticks, Document has the Oldest ones, Archive and Other are untouched.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ARuleTouchesOnlyTheCurrentTabAndAnotherRuleInAnotherTabLeavesIt()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var documents = Groups.Where(g => g.Tab == Text.TypeDocument).ToArray();
        ChooseTab(view, Text.TypeVideo);
        ChooseRule(app, view, Newest.MenuId);
        var (videoTicks, videoGroups) = TicksOf(Newest, video, null);
        AssertStatus(app, RuleStatus(videoTicks.Count, videoGroups), "Newest in Video");

        ChooseTab(view, Text.TypeDocument);
        AssertNothingTicked(view, "the Document tab after a rule in Video");
        ChooseRule(app, view, Oldest.MenuId);
        var (docTicks, docGroups) = TicksOf(Oldest, documents, null);
        AssertStatus(app, RuleStatus(docTicks.Count, docGroups), "Oldest in Document");
        AssertGroups(Cells(view), documents, docTicks, Oldest, null, "Oldest in the Document tab");

        ChooseTab(view, Text.AllFiles);
        var cells = Cells(view);
        AssertGroups(cells, video, videoTicks, Newest, null, "the Video tab keeps its Newest ticks");
        AssertGroups(cells, documents, docTicks, Oldest, null, "the Document tab keeps its Oldest ticks");
        AssertGroups(cells, Groups.Except(video).Except(documents), [], null, null, "Archive and Other");
    }

    // Contract "What a rule touches": Clear selection unticks every group in the tab and leaves each Keep choice as it is
    // (the rule's kept copy, not the suggestion), and leaves the other tabs' ticks. Oldest in Video, Newest in Document, then Clear
    // selection in Video: Video has no tick and still keeps r and a; Document still has its Newest ticks.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ClearSelectionUnticksTheTabsGroupsAndLeavesKeepAndOtherTabs()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var documents = Groups.Where(g => g.Tab == Text.TypeDocument).ToArray();
        ChooseTab(view, Text.TypeVideo);
        ChooseRule(app, view, Oldest.MenuId);
        ChooseTab(view, Text.TypeDocument);
        ChooseRule(app, view, Newest.MenuId);
        var (docTicks, _) = TicksOf(Newest, documents, null);
        ChooseTab(view, Text.TypeVideo);
        AssertGroups(Cells(view), video, TicksOf(Oldest, video, null).Ticked, Oldest, null, "Oldest in Video before clearing");

        ChooseRule(app, view, "ClearSelection");
        Assert.IsTrue(AppSession.WaitUntil(() => Cells(view).All(c => !c.Ticked), Ui.ElementTimeout), "Clear selection left a copy of the Video tab ticked: " + Dump(Cells(view)));
        AssertGroups(Cells(view), video, [], Oldest, null, "Clear selection leaves Keep as the Oldest rule set it");

        ChooseTab(view, Text.AllFiles);
        var cells = Cells(view);
        AssertGroups(cells, documents, docTicks, Newest, null, "the Document ticks after Clear selection in Video");
        Assert.AreEqual(docTicks.Count, cells.Count(c => c.Ticked), "ticks left in All files: " + Dump(cells));
    }

    // ---- 4. inside a folder ---------------------------------------------------------------------------------------------

    // Contract "Rules" (inside a folder): RuleInFolder opens the folder picker; with x\in picked, in All files, the group x keeps
    // the one copy inside the folder (x\in\deeper\f.dat, the oldest, which no other rule would keep) and ticks the other two; every
    // other group has no copy inside the folder and is untouched (nothing ticked, still on its suggested copy); the status counts one group.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void InsideAFolderKeepsTheCopyInsideItAndLeavesGroupsWithNoCopyInsideAlone()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var folder = fx!.Sub(@"x\in");
        var (ticked, touched) = TicksOf(InFolder, Groups, folder);
        Assert.AreEqual(1, touched, "precondition: only group x has a copy inside " + folder);
        Assert.AreEqual(2, ticked.Count, "precondition: two copies of x are ticked");

        ChooseRule(app, view, InFolder.MenuId);
        FolderPicker.Choose(app, folder, Ui.ElementTimeout);
        AssertStatus(app, RuleStatus(ticked.Count, touched), "inside a folder");
        AssertGroups(Cells(view), Groups, ticked, InFolder, folder, "inside " + folder);
    }

    // Contract "Rules" (inside a folder), cancelling: the picker opens, Cancel closes it, and nothing changes: no tick, the
    // same status line, Keep unchanged.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void CancellingTheFolderPickerChangesNothing()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var statusBefore = Status(app);
        ChooseRule(app, view, InFolder.MenuId);
        FolderPicker.WaitFor(app, Ui.ElementTimeout);
        FolderPicker.CancelIfOpen(app);
        Assert.IsNull(FolderPicker.TryFind(app), "the folder picker is still open");
        System.Threading.Thread.Sleep(500);
        AssertGroups(Cells(view), Groups, [], null, null, "after cancelling the folder picker");
        Assert.AreEqual(statusBefore, Status(app), "the status line changed after cancelling the picker");
    }

    // ---- 5. Move after a rule -------------------------------------------------------------------------------------------

    // Contract "Owner decisions" 1: Move after a rule shows the confirmation with the full list: every ticked copy as a
    // ConfirmItem, and each group's kept copy as "Keeping: <path>" (v1 keeps q, v2 keeps b under Newest), never a ticked copy as
    // kept. Cancel: every copy is in place and the same file.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void MoveAfterARuleConfirmsTheWholeListWithEachKeptCopy()
    {
        using var settings = ProtectGuarded();
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var identities = PathsOf(video).ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = StartMain();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeVideo);
        ChooseRule(app, view, Newest.MenuId);
        var (ticked, _) = TicksOf(Newest, video, null);
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move stays disabled with copies ticked by a rule");
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        try
        {
            List<AutomationElement> items = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count == ticked.Count, Ui.ElementTimeout),
                $"the confirmation lists {items.Count} items, expected {ticked.Count}: " + Ui.Dump(dialog));
            foreach (var group in video)
            {
                foreach (var copy in group.Copies.Where(c => ticked.Contains(FullPath(c))))
                {
                    var expected = Text.ConfirmItem(FullPath(copy), ByteUnits.Format(group.Length));
                    Assert.IsTrue(items.Any(i => i.Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase)), $"no ConfirmItem \"{expected}\": {Ui.Dump(dialog)}");
                }
            }

            var names = Ui.Names(dialog);
            int Count(string text) => names.Count(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
            foreach (var group in video)
            {
                var keep = Newest.Keep(group, null)!;
                var movedFromGroup = group.Copies.Count(c => ticked.Contains(FullPath(c)));
                Assert.IsTrue(Count(Text.Keeping(FullPath(keep))) >= movedFromGroup, $"each item of {group.Name} should say \"{Text.Keeping(FullPath(keep))}\": {Ui.Dump(dialog)}");
                foreach (var copy in group.Copies.Where(c => ticked.Contains(FullPath(c))))
                {
                    Assert.AreEqual(0, Count(Text.Keeping(FullPath(copy))), $"the ticked copy {copy.Rel} is named as kept: {Ui.Dump(dialog)}");
                }
            }
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        }

        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, Ui.NeverWatch), "a move report appeared after Cancel");
        foreach (var path in PathsOf(video))
        {
            Assert.IsTrue(File.Exists(path), path + " was moved");
            Assert.AreEqual(identities[path], MoveFixture.Identity(path), path + " is not the same file");
        }
    }

    // Intent "What a rule touches" (settled 2026-10-19): the tabs only filter what is shown, and Move takes every ticked copy in
    // every tab. Newest in Video, one copy ticked by hand in Archive (a1\x), then Move: the confirmation lists the three video
    // copies and the archive copy, each group's kept copy as "Keeping: ..." (Video: the newest; Archive: its suggested copy, a1\y,
    // which is unticked). Cancelled: nothing moved.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void MoveTakesTheTickedCopiesOfEveryTab()
    {
        using var settings = ProtectGuarded();
        var video = Groups.Where(g => g.Tab == Text.TypeVideo).ToArray();
        var archive = Groups.Single(g => g.Tab == Text.TypeArchive);
        var byHand = FullPath(archive.Copies.Single(c => c.Rel.StartsWith(@"a1\x", StringComparison.Ordinal)));
        var archiveKeep = FullPath(Newest.Keep(archive, null)!);
        var paths = PathsOf(video).Append(byHand).ToArray();
        var identities = paths.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = StartMain();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeVideo);
        ChooseRule(app, view, Newest.MenuId);
        var (ticked, _) = TicksOf(Newest, video, null);
        ChooseTab(view, Text.TypeArchive);
        var box = Table.ControlFor(view, "DuplicateCopy", byHand);
        Assert.IsNotNull(box, "no row for " + byHand + ": " + Table.Dump(view, "DuplicateCopy"));
        Table.Tick(box, () => Table.ControlFor(view, "DuplicateCopy", byHand), "the archive copy");
        ChooseTab(view, Text.AllFiles);
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move is disabled with copies ticked");
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        try
        {
            var expectedItems = ticked.Count + 1;
            List<AutomationElement> items = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count == expectedItems, Ui.ElementTimeout),
                $"the confirmation lists {items.Count} items, expected {expectedItems} (the video copies and the archive copy): " + Ui.Dump(dialog));
            foreach (var path in ticked.Append(byHand))
            {
                Assert.IsTrue(items.Any(i => Table.Holds(i.Current.Name, path) || i.Current.Name.Contains(path, StringComparison.OrdinalIgnoreCase)), $"no ConfirmItem for {path}: {Ui.Dump(dialog)}");
            }

            var names = Ui.Names(dialog);
            int Count(string text) => names.Count(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
            foreach (var group in video)
            {
                var keep = FullPath(Newest.Keep(group, null)!);
                Assert.IsTrue(Count(Text.Keeping(keep)) >= group.Copies.Count(c => ticked.Contains(FullPath(c))), $"each video item of {group.Name} should say \"{Text.Keeping(keep)}\": {Ui.Dump(dialog)}");
            }

            Assert.IsTrue(Count(Text.Keeping(archiveKeep)) >= 1, $"the archive item should say \"{Text.Keeping(archiveKeep)}\": {Ui.Dump(dialog)}");
            Assert.AreEqual(0, Count(Text.Keeping(byHand)), "the ticked archive copy is named as kept: " + Ui.Dump(dialog));
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        }

        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, Ui.NeverWatch), "a move report appeared after Cancel");
        foreach (var path in paths)
        {
            Assert.IsTrue(File.Exists(path), path + " was moved");
            Assert.AreEqual(identities[path], MoveFixture.Identity(path), path + " is not the same file");
        }
    }

    // ---- 6. alternating group backgrounds -------------------------------------------------------------------------------

    // Contract "Groups": alternate groups get LeanPanel and LeanGround backgrounds so that they read apart. All files with seven
    // groups: the copy rows on screen are sampled (a row's pixels, mostly its group's background), each belongs to the last
    // group that starts above it; every group on screen is LeanPanel or LeanGround, neighbours differ, and both colours occur.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void NeighbouringGroupsAlternateBetweenLeanPanelAndLeanGround()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the contract hands every colour to the system");
        }

        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var window = app.Window(Ui.WindowTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, Ui.SidebarItem(app, "History").Current.BoundingRectangle);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        Console.WriteLine($"capture {capture.Left},{capture.Top} {capture.Width}x{capture.Height}, saved {capture.Save(nameof(NeighbouringGroupsAlternateBetweenLeanPanelAndLeanGround))}");

        var groups = AppSession.FindAll(view, "DuplicateGroup")
            .Select(g => g.Current.BoundingRectangle).Where(r => !r.IsEmpty).OrderBy(r => r.Top).ToList();
        var rows = Table.Controls(view, "DuplicateCopy").Select(Table.RowOf).OfType<AutomationElement>().Select(r => r.Current.BoundingRectangle)
            .Where(r => !r.IsEmpty && r.Height >= 20 && capture.Contains((int)r.Left + 4, (int)r.Top + 2) && capture.Contains((int)r.Right - 4, (int)r.Bottom - 2)).ToList();
        Assert.IsTrue(groups.Count >= 2 && rows.Count >= 2, $"fewer than two groups on screen at the default size: {groups.Count} groups, {rows.Count} rows");

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

        Assert.IsTrue(perGroup.Count >= 2, "fewer than two groups have a copy row on screen");
        var colours = new List<string>();
        foreach (var (index, samples) in perGroup)
        {
            var ground = ScreenCapture.Share(samples, Brand.Ground, Brand.NeutralTolerance);
            var panel = ScreenCapture.Share(samples, Brand.Panel, Brand.NeutralTolerance);
            Assert.IsTrue(Math.Max(ground, panel) >= 0.5, $"group {index + 1} is neither LeanGround nor LeanPanel: LeanGround {ground:P0}, LeanPanel {panel:P0}; commonest {ScreenCapture.Commonest(samples)}");
            colours.Add(panel > ground ? "LeanPanel" : "LeanGround");
        }

        Console.WriteLine("group backgrounds: " + string.Join(", ", colours));
        for (var i = 1; i < colours.Count; i++)
        {
            Assert.AreNotEqual(colours[i - 1], colours[i], $"neighbouring groups {i} and {i + 1} have one background: {string.Join(", ", colours)}");
        }

        Assert.AreEqual(2, colours.Distinct().Count(), "only one background on screen: " + string.Join(", ", colours));
    }

    // ---- 7. the preview -------------------------------------------------------------------------------------------------

    private static AppSession StartPictures() => AppSession.Start("--scan", pics!.Path);

    private static AutomationElement? ShownPreview(AutomationElement scope) =>
        Table.Controls(scope, "PreviewCopy").FirstOrDefault(b =>
        {
            try
            {
                return !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        });

    private static int ShownPreviewCount(AutomationElement scope) => Table.Controls(scope, "PreviewCopy").Count(b =>
    {
        try
        {
            return !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    });

    /// <summary>The row holding <paramref name="path"/>, scrolled into view (the list is virtualised).</summary>
    private static AutomationElement RowOf(AutomationElement view, string path)
    {
        AutomationElement? row = null;
        AutomationElement? Look()
        {
            foreach (var id in RowControls)
            {
                if (Table.ControlFor(view, id, path) is { } control && Table.RowOf(control) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        if ((row = Look()) is null && Scroller(view) is { } scroller)
        {
            var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
            var step = Math.Max(5.0, scroll.Current.VerticalViewSize / 2);
            for (var at = 0.0; at <= 100.0 && row is null; at += step)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
                System.Threading.Thread.Sleep(200);
                row = Look();
            }
        }

        Assert.IsNotNull(row, $"no row for {path}: {Table.Dump(view, "DuplicateCopy")}");
        if ((row.Current.BoundingRectangle.IsEmpty || row.Current.IsOffscreen) && row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var item))
        {
            ((ScrollItemPattern)item).ScrollIntoView();
            System.Threading.Thread.Sleep(300);
        }

        return row;
    }

    /// <summary>Gives the keyboard focus to a control in <paramref name="row"/>: its Move box, or its Keep radio when the box is disabled.</summary>
    private static void FocusRow(AutomationElement row)
    {
        var target = RowControls.Select(id => Table.InRow(row, id)).FirstOrDefault(c => c is not null && c.Current.IsEnabled);
        Assert.IsNotNull(target, "no focusable control in the row " + row.Current.Name);
        Ui.Focus(target);
    }

    /// <summary>Focus on FindDuplicates and the pointer in the sidebar's empty band: away from every row.</summary>
    private static void Away(AppSession app, AutomationElement view)
    {
        Ui.Focus(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        Mouse.HoverAt(sidebar.Left + (sidebar.Width / 2), (history.Bottom + settings.Top) / 2);
    }

    // Contract "Preview": the eye PreviewCopy appears on the rows of the picture types only (.jpg .jpeg .png .gif .bmp .webp
    // .tif .tiff), with the row actions: shown on the row that has the focus, and on the row under the pointer, and on no row
    // otherwise. Each of the eight picture copies shows a Button with a name when its row has the focus; a video, a document and
    // a .bin copy show none even when focused; with the focus and the pointer away, none is shown; hovered, only the picture row
    // under the pointer shows one.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheEyeShowsOnPictureRowsOnlyAndOnlyOnTheFocusedOrHoveredRow()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartPictures();
        var view = OpenDuplicates(app);
        Away(app, view);
        Assert.IsTrue(AppSession.WaitUntil(() => ShownPreviewCount(view) == 0, TimeSpan.FromSeconds(3)), "with the focus and the pointer away, an eye is shown");

        var problems = new List<string>();
        foreach (var extension in PictureExtensions)
        {
            var path = pics!.Sub(PictureCopy(extension, 0));
            var row = RowOf(view, path);
            FocusRow(row);
            var eye = AppSession.WaitFor(() => Table.InRow(row, "PreviewCopy") is { } b && !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty ? b : null, TimeSpan.FromSeconds(3));
            if (eye is null)
            {
                problems.Add($"no PreviewCopy on the focused row of {extension}");
                continue;
            }

            if (eye.Current.ControlType != ControlType.Button || string.IsNullOrWhiteSpace(eye.Current.Name) || !eye.Current.IsEnabled || !eye.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                problems.Add($"PreviewCopy of {extension} is not an enabled, named button that can be invoked: {Ui.Describe(eye)}");
            }

            if (ShownPreviewCount(view) != 1)
            {
                problems.Add($"with the focus in the row of {extension}, {ShownPreviewCount(view)} eyes are shown, expected 1");
            }
        }

        foreach (var extension in OtherExtensions)
        {
            var row = RowOf(view, pics!.Sub(PictureCopy(extension, 0)));
            FocusRow(row);
            System.Threading.Thread.Sleep(500);
            if (ShownPreview(view) is not null)
            {
                problems.Add($"an eye is shown with the focus in the row of a {extension} copy");
            }
        }

        Away(app, view);
        if (!AppSession.WaitUntil(() => ShownPreviewCount(view) == 0, TimeSpan.FromSeconds(3)))
        {
            problems.Add("with the focus and the pointer away again, an eye stays shown");
        }

        var hovered = RowOf(view, pics!.Sub(PictureCopy(".jpg", 1))).Current.BoundingRectangle;
        Mouse.HoverAt(hovered.Left + (hovered.Width / 3), hovered.Top + (hovered.Height / 2));
        if (!AppSession.WaitUntil(() => ShownPreviewCount(view) == 1 && Table.RowName(ShownPreview(view)!).Contains(PictureCopy(".jpg", 1).Split('\\')[0], StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(3)))
        {
            problems.Add($"with the pointer over the row of the .jpg copy, {ShownPreviewCount(view)} eyes are shown, not exactly that row's");
        }

        Away(app, view);
        var plain = RowOf(view, pics!.Sub(PictureCopy(".pdf", 1))).Current.BoundingRectangle;
        Mouse.HoverAt(plain.Left + (plain.Width / 3), plain.Top + (plain.Height / 2));
        if (ShownPreview(view) is not null)
        {
            problems.Add("an eye is shown with the pointer over the row of a .pdf copy");
        }

        Away(app, view);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    private static AutomationElement? ShownIn(AutomationElement row, string id) =>
        Table.InRow(row, id) is { } b && !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty ? b : null;

    private static int ShownCount(AutomationElement scope, string id) => Table.Controls(scope, id).Count(b =>
    {
        try
        {
            return !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    });

    // Amendment of 2026-10-19: a Duplicates row shows RevealRow (a button named "Show in File Explorer", the magnifier) on the
    // row that has the focus and on the row under the pointer, and on no other row; on a picture row PreviewCopy is beside it
    // (same line, not overlapping), on any other row there is none; Tab from the row's box reaches both on a picture row. Not
    // pressed (File Explorer is never opened by the tests).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADuplicatesRowShowsRevealRowOnFocusAndHoverWithPreviewBesideItOnPictureRows()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartPictures();
        var view = OpenDuplicates(app);
        Away(app, view);
        var problems = new List<string>();
        if (!AppSession.WaitUntil(() => ShownCount(view, "RevealRow") == 0, TimeSpan.FromSeconds(3)))
        {
            problems.Add("with the focus and the pointer away, a RevealRow is shown");
        }

        var jpg = RowOf(view, pics!.Sub(PictureCopy(".jpg", 1)));
        FocusRow(jpg);
        var reveal = AppSession.WaitFor(() => ShownIn(jpg, "RevealRow"), TimeSpan.FromSeconds(3));
        var eye = AppSession.WaitFor(() => ShownIn(jpg, "PreviewCopy"), TimeSpan.FromSeconds(3));
        if (reveal is null || eye is null)
        {
            problems.Add($"with the focus in the .jpg row, RevealRow is {(reveal is null ? "missing" : "shown")} and PreviewCopy is {(eye is null ? "missing" : "shown")}");
        }
        else
        {
            if (reveal.Current.ControlType != ControlType.Button || reveal.Current.Name != "Show in File Explorer" || !reveal.Current.IsEnabled || !reveal.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                problems.Add("RevealRow is not an enabled button named \"Show in File Explorer\" that can be invoked: " + Ui.Describe(reveal));
            }

            var r = reveal.Current.BoundingRectangle;
            var e = eye.Current.BoundingRectangle;
            var overlap = System.Windows.Rect.Intersect(r, e);
            if (Math.Abs((r.Top + (r.Height / 2)) - (e.Top + (e.Height / 2))) > 6 || (!overlap.IsEmpty && overlap.Width > 2))
            {
                problems.Add($"PreviewCopy {e} is not beside RevealRow {r} (one line, not overlapping)");
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            FocusRow(jpg);
            for (var press = 0; press < 6 && reached.Count < 2; press++)
            {
                Keys.Press(Keys.Tab);
                System.Threading.Thread.Sleep(200);
                if (AutomationElement.FocusedElement is { } focused && (focused.Current.AutomationId is "RevealRow" or "PreviewCopy") && Ui.IsWithin(focused, jpg))
                {
                    reached.Add(focused.Current.AutomationId);
                }
            }

            if (reached.Count < 2)
            {
                problems.Add($"Tab from the .jpg row's box reached only [{string.Join(", ", reached)}] of RevealRow and PreviewCopy");
            }
        }

        if (ShownCount(view, "RevealRow") != 1)
        {
            problems.Add($"with the focus in one row, {ShownCount(view, "RevealRow")} RevealRow buttons are shown, expected 1");
        }

        var pdf = RowOf(view, pics.Sub(PictureCopy(".pdf", 1)));
        FocusRow(pdf);
        if (AppSession.WaitFor(() => ShownIn(pdf, "RevealRow"), TimeSpan.FromSeconds(3)) is null)
        {
            problems.Add("no RevealRow in the focused .pdf row");
        }

        if (ShownIn(pdf, "PreviewCopy") is not null)
        {
            problems.Add("a PreviewCopy in a .pdf row");
        }

        Away(app, view);
        if (!AppSession.WaitUntil(() => ShownCount(view, "RevealRow") == 0, TimeSpan.FromSeconds(3)))
        {
            problems.Add("with the focus and the pointer away again, a RevealRow stays shown");
        }

        var hovered = RowOf(view, pics.Sub(PictureCopy(".gif", 1)));
        var box = hovered.Current.BoundingRectangle;
        Mouse.HoverAt(box.Left + (box.Width / 3), box.Top + (box.Height / 2));
        if (!AppSession.WaitUntil(() => ShownIn(hovered, "RevealRow") is not null && ShownIn(hovered, "PreviewCopy") is not null && ShownCount(view, "RevealRow") == 1, TimeSpan.FromSeconds(3)))
        {
            problems.Add($"with the pointer over the .gif row, RevealRow and PreviewCopy are not both shown and alone: {ShownCount(view, "RevealRow")} RevealRow, {ShownCount(view, "PreviewCopy")} PreviewCopy");
        }

        Away(app, view);
        var plain = RowOf(view, pics.Sub(PictureCopy(".bin", 1))).Current.BoundingRectangle;
        Mouse.HoverAt(plain.Left + (plain.Width / 3), plain.Top + (plain.Height / 2));
        if (!AppSession.WaitUntil(() => ShownCount(view, "RevealRow") == 1, TimeSpan.FromSeconds(3)) || ShownCount(view, "PreviewCopy") != 0)
        {
            problems.Add($"with the pointer over the .bin row, {ShownCount(view, "RevealRow")} RevealRow and {ShownCount(view, "PreviewCopy")} PreviewCopy are shown, expected 1 and 0");
        }

        Away(app, view);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    /// <summary>Presses PreviewCopy in the row of <paramref name="path"/> (focused first).</summary>
    private static void PressPreview(AutomationElement view, string path)
    {
        var row = RowOf(view, path);
        FocusRow(row);
        var eye = AppSession.WaitFor(() => Table.InRow(row, "PreviewCopy") is { } b && !b.Current.IsOffscreen && !b.Current.BoundingRectangle.IsEmpty ? b : null, Ui.ElementTimeout);
        Assert.IsNotNull(eye, "no PreviewCopy on the focused row of " + path);
        Ui.Click(eye);
    }

    /// <summary>The flyout's content once it shows the file's full <paramref name="path"/> and <paramref name="content"/> holds:
    /// a popup window of the app, or else the nearest ancestor of the path text that holds the content.</summary>
    private static AutomationElement WaitForFlyout(AppSession app, string path, Func<AutomationElement, bool> content, string what)
    {
        var main = app.Window(Ui.WindowTimeout);
        AutomationElement? scope = null;
        AutomationElement? Find()
        {
            foreach (var top in Ui.TopWindows(app))
            {
                var isMain = Automation.Compare(top, main);
                var texts = top.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                    .Where(t => (t.Current.Name ?? string.Empty).Contains(path, StringComparison.OrdinalIgnoreCase) && Table.RowOf(t) is null).ToList();
                foreach (var text in texts)
                {
                    if (!isMain)
                    {
                        return content(top) ? top : null;
                    }

                    var node = text;
                    for (var level = 0; level < 5 && node is not null; level++)
                    {
                        if (content(node))
                        {
                            return node;
                        }

                        node = TreeWalker.ControlViewWalker.GetParent(node);
                    }
                }
            }

            return null;
        }

        Assert.IsTrue(AppSession.WaitUntil(() => (scope = Find()) is not null, Ui.ElementTimeout),
            $"no flyout with the path {path} and {what} within {Ui.ElementTimeout.TotalSeconds:F0} s; windows: " + string.Join(" || ", Ui.TopWindows(app).Select(t => Ui.Dump(t))));
        return scope!;
    }

    private static bool HasPicture(AutomationElement scope) =>
        scope.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image), new PropertyCondition(AutomationElement.ClassNameProperty, "Image")))
            .Cast<AutomationElement>().Any(i =>
            {
                try
                {
                    var r = i.Current.BoundingRectangle;
                    return !i.Current.IsOffscreen && r.Width >= 16 && r.Height >= 16;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            });

    private static bool HasText(AutomationElement scope, string text) =>
        Ui.Names(scope).Any(n => string.Equals(n.Trim(), text, StringComparison.Ordinal));

    private static void CloseFlyout(AppSession app, AutomationElement view)
    {
        Keys.Press(Keys.Escape);
        System.Threading.Thread.Sleep(400);
        Assert.IsNotNull(AppSession.TryFind(view, "FindDuplicates"), "the view is gone after closing the flyout");
    }

    // Contract "Preview": PreviewCopy opens a flyout with the picture, its size in pixels and the file's path. The .bmp copy
    // is a real 320 x 1100 picture: the flyout holds an image on screen, the two numbers 320 and 1100 in one text, and the
    // full path; none of the two failure texts.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ThePreviewFlyoutShowsAPictureItsPixelSizeAndItsPath()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartPictures();
        var view = OpenDuplicates(app);
        var path = pics!.Sub(PictureCopy(".bmp", 0));
        PressPreview(view, path);
        try
        {
            var flyout = WaitForFlyout(app, path, HasPicture, "a picture");
            var size = new Regex($@"\b{PictureWidth}\D{{1,8}}{PictureHeight}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.IsTrue(Ui.Names(flyout).Any(n => size.IsMatch(n)), $"the flyout does not give the size {PictureWidth} x {PictureHeight} pixels: " + Ui.Dump(flyout));
            Assert.IsFalse(HasText(flyout, Ui.Copy("PreviewUnavailable")) || HasText(flyout, Ui.Copy("PreviewOnlineOnly")), "a decodable local picture shows a failure text: " + Ui.Dump(flyout));
        }
        finally
        {
            CloseFlyout(app, view);
        }
    }

    // Contract "Preview": a file that fails to decode shows Text.PreviewUnavailable (the .png copies are noise, not a PNG). The
    // flyout names the file's path and shows no picture.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ANonDecodablePngShowsPreviewUnavailable()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartPictures();
        var view = OpenDuplicates(app);
        var path = pics!.Sub(PictureCopy(".png", 0));
        PressPreview(view, path);
        var unavailable = Ui.Copy("PreviewUnavailable");
        try
        {
            var flyout = WaitForFlyout(app, path, s => HasText(s, unavailable), $"the text \"{unavailable}\"");
            Assert.IsFalse(HasPicture(flyout), "a picture is shown for a file that cannot be decoded: " + Ui.Dump(flyout));
        }
        finally
        {
            CloseFlyout(app, view);
        }
    }

    private static bool ByAppProcess(string line, int pid) =>
        line.Contains("\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal) || line.Contains("\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + "}", StringComparison.Ordinal);

    // Contract "Preview" and owner decision 3: pictures of local files only, no download of a cloud file ever. A picture in an
    // AmbyCloud sync root is hydrated, so the search takes it as a copy of a plain identical file; then it is made online-only
    // again and its eye is pressed: the flyout says Text.PreviewOnlineOnly, shows no picture, AmbyCloud's fetch log shows no fetch
    // at all since then (so none by Lean's process; a positive control shows the log does record this test process's read), and the
    // placeholder is still online-only with nothing on disk. Inconclusive only when AmbyCloud is not installed.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AnOnlineOnlyPictureShowsPreviewOnlineOnlyAndIsNeverFetched()
    {
        AmbyCloudRoot.RequireInstalled();
        const long Length = MB + (16 * KB);
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var tree = new MoveFixture("asp15-cloud");
        var plain = tree.File(@"plain\photo.jpg", Length, 0); // seed 0: byte i = (i * 31 + 7) & 0xFF, as AmbyCloud serves
        using var cloud = AmbyCloudRoot.Register(tree.Sub("cloud"), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asp15-fetch-" + Guid.NewGuid().ToString("N")[..8] + ".log"));
        cloud.Populate(("photo.jpg", Length), ("control.bin", 4 * KB));
        cloud.Serve();
        cloud.RequireOnlineOnly("photo.jpg");
        var cloudPicture = cloud.Combine("photo.jpg");
        Assert.AreEqual(Length, File.ReadAllBytes(cloudPicture).Length, "precondition: the picture is hydrated by reading it");
        Assert.IsTrue(File.ReadAllBytes(cloudPicture).AsSpan().SequenceEqual(File.ReadAllBytes(plain)), "precondition: the hydrated picture equals the plain copy");

        using var app = AppSession.Start("--scan", tree.Path);
        var view = OpenDuplicates(app);
        Assert.IsNotNull(Table.ControlFor(view, "DuplicateCopy", cloudPicture), "the hydrated cloud picture is not listed as a copy: " + Table.Dump(view, "DuplicateCopy"));

        // Online-only again; Lean has closed the file by now (retry while it does not).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                cloud.Dehydrate("photo.jpg");
                break;
            }
            catch (AssertFailedException) when (attempt < 5)
            {
                System.Threading.Thread.Sleep(1000);
            }
        }

        cloud.RequireOnlineOnly("photo.jpg");
        using (var stream = new FileStream(cloud.Combine("control.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.IsTrue(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        Assert.IsTrue(cloud.FetchesOf("control.bin").Any(l => ByAppProcess(l, Environment.ProcessId)), "the positive control failed: the fetch log did not record this process's read of the control: [" + string.Join(" | ", cloud.Fetches()) + "]");
        cloud.MarkFetchBaseline();

        PressPreview(view, cloudPicture);
        var onlineOnly = Ui.Copy("PreviewOnlineOnly");
        try
        {
            var flyout = WaitForFlyout(app, cloudPicture, s => HasText(s, onlineOnly), $"the text \"{onlineOnly}\"");
            Assert.IsFalse(HasPicture(flyout), "a picture is shown for an online-only file: " + Ui.Dump(flyout));
            System.Threading.Thread.Sleep(1500);
            Assert.AreEqual(0, cloud.Fetches().Count(l => ByAppProcess(l, app.Process.Id)), $"Lean's process (pid {app.Process.Id}) fetched the picture: [{string.Join(" | ", cloud.FetchesOf("photo.jpg"))}]");
            cloud.AssertNoFetch("the preview of an online-only picture");
            cloud.RequireOnlineOnly("photo.jpg");
        }
        finally
        {
            CloseFlyout(app, view);
        }
    }

    // ---- 9. a large rule asks first -------------------------------------------------------------------------------------

    // Intent "A large rule asks first" (owner, 2026-10-19): a rule that would tick more than 100 copies shows a confirmation with
    // Text.RuleConfirm(copies, groups, programFiles) and Tick / Cancel; 100 or fewer tick at once. The fixture (more than 100
    // copies, in the test-data folder, about 160 MB of zeros). The finder compares at most 32 copies at a time (MaxOpen), so no
    // group has more than 27 copies:
    //   o1, o2  26 copies each of blob.bin (Other)       v, v2  27 copies each of clip.mp4 (Video)
    //   d1, d2  26 copies each of lib.<ext> (Other), the extension cycling through the ten listed ones except .exe, then .dat
    //   e       2 copies of tool.exe (Application)
    // In o1, o2, v and v2 the eighth copy is in sel\<group>. Every copy is one day older than the one before, so Newest keeps c00
    // in every group. Newest in the Other tab ticks 4 x 25 = 100 (at once); in All files 153 copies in 7 groups, 47 of them program
    // files (23 in each d group, the .dat copies not counted, and the .exe); inside sel it ticks 102 copies in 4 groups, none a
    // program file.
    private static readonly string[] ProgramExtensions = [".dll", ".sys", ".ocx", ".drv", ".cpl", ".pyd", ".so", ".node", ".jar", ".winmd"];

    private sealed record BigCopy(string Rel, int DaysAgo, bool Program);

    private sealed record BigGroup(string Name, long Length, BigCopy[] Copies);

    private static BigGroup[] BigGroups()
    {
        BigCopy[] Copies(int count, Func<int, string> rel, Func<int, bool> program) =>
            [.. Enumerable.Range(0, count).Select(i => new BigCopy(rel(i), i + 1, program(i)))];
        string D(int i) => i % 11 == 10 ? ".dat" : ProgramExtensions[i % 11];
        BigGroup Plain(string name, int kb, int count, string file) =>
            new(name, MB + (kb * KB), Copies(count, i => i == 7 ? $@"sel\{name}\{file}" : $@"{name}\c{i:D2}\{file}", _ => false));
        BigGroup Programs(string name, int kb) =>
            new(name, MB + (kb * KB), Copies(26, i => $@"{name}\c{i:D2}\lib{D(i)}", i => D(i) != ".dat"));
        return
        [
            Plain("o1", 4, 26, "blob.bin"),
            Plain("o2", 8, 26, "blob.bin"),
            Programs("d1", 12),
            Programs("d2", 16),
            Plain("v", 20, 27, "clip.mp4"),
            Plain("v2", 24, 27, "clip.mp4"),
            new("e", MB + (28 * KB), Copies(2, i => $@"e\c{i}\tool.exe", _ => true)),
        ];
    }

    private static readonly Lazy<Fixture> BigTree = new(() =>
    {
        var tree = new Fixture("asp15-big", Fixture.ProfileBaseFolder());
        var now = DateTime.UtcNow;
        foreach (var group in BigGroups())
        {
            foreach (var copy in group.Copies)
            {
                tree.File(copy.Rel, group.Length);
                tree.SetModified(copy.Rel, now.AddDays(-copy.DaysAgo));
            }
        }

        return tree;
    });

    /// <summary>The copies a rule that keeps <paramref name="keep"/> ticks (no hint in this fixture, never the kept copy), the
    /// number of groups it touches and how many of the copies are program files.</summary>
    private static (HashSet<string> Ticked, int Groups, int ProgramFiles) BigTicks(Func<BigGroup, BigCopy?> keep, IEnumerable<string>? groupNames = null)
    {
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int groups = 0, program = 0;
        foreach (var group in BigGroups().Where(g => groupNames is null || groupNames.Contains(g.Name)))
        {
            if (keep(group) is not { } kept)
            {
                continue;
            }

            groups++;
            foreach (var copy in group.Copies.Where(c => c != kept))
            {
                ticked.Add(BigTree.Value.Sub(copy.Rel.Split('\\')));
                program += copy.Program ? 1 : 0;
            }
        }

        return (ticked, groups, program);
    }

    private static BigCopy NewestOf(BigGroup group) => group.Copies.MinBy(c => c.DaysAgo)!;

    private static AppSession StartBig() => AppSession.Start("--scan", BigTree.Value.Path);

    private static AutomationElement? ButtonNamed(AutomationElement scope, string name) =>
        scope.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name)));

    /// <summary>Whether a button named Tick is shown anywhere in the app now (the confirmation's).</summary>
    private static bool TickButtonShown(AppSession app) => Ui.TopWindows(app).Any(t =>
    {
        try
        {
            return ButtonNamed(t, "Tick") is { } b && !b.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    });

    private static void AssertNoRuleDialog(AppSession app, string what) =>
        Assert.IsFalse(AppSession.WaitUntil(() => TickButtonShown(app), Ui.NeverWatch), what + ": a confirmation with a Tick button appeared");

    /// <summary>The confirmation: the element named exactly <paramref name="text"/> and the nearest ancestor that holds a Tick and a
    /// Cancel button (Text.Cancel); returns that scope.</summary>
    private static AutomationElement WaitForRuleDialog(AppSession app, string text)
    {
        AutomationElement? scope = null;
        AutomationElement? Find()
        {
            foreach (var top in Ui.TopWindows(app))
            {
                AutomationElement? line;
                try
                {
                    line = top.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                        .FirstOrDefault(e => (e.Current.Name ?? string.Empty).Trim() == text && Table.RowOf(e) is null);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                for (var node = line; node is not null; node = TreeWalker.ControlViewWalker.GetParent(node))
                {
                    if (ButtonNamed(node, "Tick") is not null && ButtonNamed(node, Text.Cancel) is not null)
                    {
                        return node;
                    }

                    if (Automation.Compare(node, top))
                    {
                        break;
                    }
                }
            }

            return null;
        }

        Assert.IsTrue(AppSession.WaitUntil(() => (scope = Find()) is not null, Ui.ElementTimeout),
            $"no confirmation reading \"{text}\" with Tick and Cancel buttons; windows: " + string.Join(" || ", Ui.TopWindows(app).Select(t => Ui.Dump(t))));
        return scope!;
    }

    private static void PressInDialog(AppSession app, AutomationElement dialog, string button)
    {
        Ui.Click(ButtonNamed(dialog, button) ?? throw new AssertFailedException("no " + button + " button in the confirmation"));
        Assert.IsTrue(AppSession.WaitUntil(() => !TickButtonShown(app), Ui.ElementTimeout), $"the confirmation stays open after {button}");
    }

    private static List<string> TickedPaths(AutomationElement view) =>
        [.. Cells(view).Where(c => c.Ticked).Select(c => c.Row)];

    // A rule that ticks exactly 100 copies (the Other tab: four groups of 26) ticks them at once: no confirmation,
    // the status line says Text.SelectedByRule(100, 2), and 100 copies are ticked.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ARuleThatTicksOneHundredCopiesOrFewerTicksAtOnceWithNoConfirmation()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartBig();
        var view = OpenDuplicates(app);
        ChooseTab(view, Text.TypeOther);
        var (ticked, groups, _) = BigTicks(NewestOf, ["o1", "o2", "d1", "d2"]);
        Assert.AreEqual(100, ticked.Count, "precondition: the Other tab's rule ticks exactly 100");
        ChooseRule(app, view, Newest.MenuId);
        AssertNoRuleDialog(app, "a rule of 100 copies");
        AssertStatus(app, RuleStatus(ticked.Count, groups), "Newest in the Other tab");
        var cells = Cells(view);
        Assert.AreEqual(100, cells.Count(c => c.Ticked), "ticks in the Other tab: " + cells.Count(c => c.Ticked));
        foreach (var path in ticked)
        {
            Assert.IsTrue(CellOf(cells, path)?.Ticked == true, path + " is not ticked");
        }
    }

    // A rule that would tick 153 copies in 7 groups, 47 of them program files, asks first with Text.RuleConfirm(153, 7, 47) (the
    // copies the rule would tick, counted before anything is ticked: the one ticked by hand beforehand is among them). Cancel: no
    // tick changes (only the hand-made one is ticked), the status line is as it was. The rule again, Tick: 153 copies ticked
    // and the status line says Text.SelectedByRule(153, 7).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ALargeRuleAsksFirstCancelKeepsEveryTickAndTickTicksThem()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var app = StartBig();
        var view = OpenDuplicates(app);
        var (ticked, groups, programFiles) = BigTicks(NewestOf);
        Assert.AreEqual((153, 7, 47), (ticked.Count, groups, programFiles), "precondition: what the rule ticks");
        var byHand = BigTree.Value.Sub(@"v\c10\clip.mp4");
        ChooseTab(view, Text.TypeVideo);
        var box = Table.ControlFor(view, "DuplicateCopy", byHand);
        Assert.IsNotNull(box, "no row for " + byHand);
        Table.Tick(box, () => Table.ControlFor(view, "DuplicateCopy", byHand), "the copy ticked by hand");
        ChooseTab(view, Text.AllFiles);
        var statusBefore = Status(app);
        var text = Ui.Copy("RuleConfirm", ticked.Count, groups, programFiles);

        ChooseRule(app, view, Newest.MenuId);
        var dialog = WaitForRuleDialog(app, text);
        Assert.AreEqual(statusBefore, Status(app), "the status line changed while the confirmation is open");
        PressInDialog(app, dialog, Text.Cancel);
        System.Threading.Thread.Sleep(500);
        var after = Cells(view);
        Assert.AreEqual(1, after.Count(c => c.Ticked), "Cancel changed the ticks: " + Dump(after.Where(c => c.Ticked).ToList()));
        Assert.IsTrue(CellOf(after, byHand)?.Ticked == true, "the copy ticked by hand lost its tick after Cancel");
        Assert.AreEqual(statusBefore, Status(app), "the status line changed after Cancel");

        ChooseRule(app, view, Newest.MenuId);
        PressInDialog(app, WaitForRuleDialog(app, text), "Tick");
        AssertStatus(app, RuleStatus(ticked.Count, groups), "Tick");
        var cells = Cells(view);
        Assert.AreEqual(153, cells.Count(c => c.Ticked), "ticks after Tick");
        foreach (var path in ticked)
        {
            Assert.IsTrue(CellOf(cells, path)?.Ticked == true, path + " is not ticked after Tick");
        }
    }

    // The program-file part is left out when there is none: inside sel the rule would tick 102 copies in 4 groups (blob.bin and
    // clip.mp4, no program file): the text is Text.RuleConfirm(102, 4, 0), has no word "program" and is not the text with
    // program files. Cancel: nothing ticked.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheProgramFilePartIsLeftOutWhenNoCopyIsAProgramFile()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        var folder = BigTree.Value.Sub("sel");
        var (ticked, groups, programFiles) = BigTicks(g => g.Copies.SingleOrDefault(c => c.Rel.StartsWith(@"sel\", StringComparison.Ordinal)));
        Assert.AreEqual((102, 4, 0), (ticked.Count, groups, programFiles), "precondition: the inside-folder rule");
        var text = Ui.Copy("RuleConfirm", ticked.Count, groups, programFiles);
        Assert.IsFalse(text.Contains("program", StringComparison.OrdinalIgnoreCase), $"the confirmation with no program file still speaks of them: \"{text}\"");
        Assert.AreNotEqual(Ui.Copy("RuleConfirm", ticked.Count, groups, 5), text, "the program-file count is not in the text");
        using var app = StartBig();
        var view = OpenDuplicates(app);
        ChooseRule(app, view, InFolder.MenuId);
        FolderPicker.Choose(app, folder, Ui.ElementTimeout);
        PressInDialog(app, WaitForRuleDialog(app, text), Text.Cancel);
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(0, Cells(view).Count(c => c.Ticked), "Cancel left a copy ticked");
    }

    // ---- 8. one-line rows and whole controls ----------------------------------------------------------------------------

    // Lesson of ASP-14 (rows that wrapped, a legend that clipped): at the default window size every Duplicates row is one line
    // high (as high as every other, to 1.5 px, the 250-character path of o1\b and the protected copy included, and no
    // higher than 60 px), and the tabs and the rule button are whole: on screen, inside the window and the view, not on top of
    // each other, and the rule button not on Move.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void EveryRowIsOneLineHighAndTheTabsAndTheRuleButtonAreWhole()
    {
        using var settings = ProtectGuarded();
        using var app = StartMain();
        var view = OpenDuplicates(app);
        var window = app.Window(Ui.WindowTimeout).Current.BoundingRectangle;
        var viewRect = view.Current.BoundingRectangle;
        var problems = new List<string>();

        // Rows wholly inside the scroll viewport only (Cells leaves out a row within 2 px of its edge, which the viewport may
        // cut); every one of them is measured, so a short, wrapped or taller row fails the comparison.
        WaitForRowsToSettle(view);
        var rows = Cells(view).Where(c => c.Height is not null).ToList();
        var heights = rows.Select(c => (c.Row, Height: c.Height!.Value, Spread: c.Spread ?? 0)).ToList();
        Console.WriteLine("row heights: " + string.Join(" | ", heights.Select(h => $"{h.Height:F1}/{h.Spread:F1}")));
        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle)));
        if (heights.Count < 4)
        {
            problems.Add($"only {heights.Count} whole rows measured on screen");
        }
        else
        {
            if (heights.Max(h => h.Height) - heights.Min(h => h.Height) > 1.5)
            {
                problems.Add("rows are not all as high as each other: " + string.Join(" | ", heights.Select(h => $"{h.Height:F1} px: {h.Row}")));
            }

            if (heights.Max(h => h.Height) > 60 * scale)
            {
                problems.Add($"a row is {heights.Max(h => h.Height):F1} px high, more than one line");
            }

            foreach (var (row, _, spread) in heights.Where(h => h.Spread > 6 * scale))
            {
                problems.Add($"the texts of one row are on more than one line (their centres are {spread:F1} px apart): {row}");
            }
        }

        // The tabs and the rule button are looked up so that a missing one is one more problem in the list, not the end of it.
        var tabs = new List<Tab>();
        var whole = new List<(string Name, System.Windows.Rect Rect, bool Off)>();
        try
        {
            tabs = Tabs(view);
            whole.AddRange(tabs.Select(t => (Name: t.Element.Current.Name, Rect: t.Element.Current.BoundingRectangle, Off: t.Element.Current.IsOffscreen)));
        }
        catch (AssertFailedException missing)
        {
            problems.Add("the type tabs: " + missing.Message.ReplaceLineEndings(" "));
        }

        AutomationElement? rule = null;
        try
        {
            rule = RuleButton(view);
            whole.Add((Name: "SelectByRule", Rect: rule.Current.BoundingRectangle, Off: rule.Current.IsOffscreen));
        }
        catch (AssertFailedException missing)
        {
            problems.Add("the rule button: " + missing.Message.ReplaceLineEndings(" "));
        }

        if (tabs.Count != 8)
        {
            problems.Add($"{tabs.Count} tabs, expected 8");
        }

        foreach (var (name, rect, off) in whole)
        {
            if (off || rect.IsEmpty || rect.Width < 20 || rect.Height < 16)
            {
                problems.Add($"\"{name}\" is not whole on screen ({rect}{(off ? ", offscreen" : string.Empty)})");
            }
            else if (rect.Left < window.Left - 1 || rect.Right > window.Right + 1 || rect.Left < viewRect.Left - 1 || rect.Right > viewRect.Right + 1 || rect.Bottom > viewRect.Bottom + 1)
            {
                problems.Add($"\"{name}\" {rect} is cut off by the window {window} or the view {viewRect}");
            }
        }

        for (var i = 0; i < whole.Count; i++)
        {
            for (var j = i + 1; j < whole.Count; j++)
            {
                var overlap = System.Windows.Rect.Intersect(whole[i].Rect, whole[j].Rect);
                if (!overlap.IsEmpty && overlap.Width > 2 && overlap.Height > 2)
                {
                    problems.Add($"\"{whole[i].Name}\" {whole[i].Rect} and \"{whole[j].Name}\" {whole[j].Rect} overlap");
                }
            }
        }

        if (rule is not null)
        {
            var moveRect = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.BoundingRectangle;
            var ruleRect = rule.Current.BoundingRectangle;
            var onMove = System.Windows.Rect.Intersect(moveRect, ruleRect);
            if (!onMove.IsEmpty && onMove.Width > 2 && onMove.Height > 2)
            {
                problems.Add($"SelectByRule {ruleRect} overlaps Move {moveRect}");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }
}
