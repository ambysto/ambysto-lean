using System;
using System.Collections.Generic;
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

/// <summary>ASP-14 UI contract, "3. Scanned: the results view" (intent/2026-10-18-asp-14-start-and-sidebar.md, commit
/// d4484e2): the type column <c>TypeGroups</c> (rows <c>TypeGroup</c>: All files, then spec 01's seven groups, each with
/// its bytes; <c>FreeSpace</c> text only; exactly one chosen, All files after a scan; choosing one filters the list and dims
/// the other groups' tiles to LeanPanel, the map's layout unchanged), the heatmap (<c>Map</c>) over the largest files
/// (<c>LargestFiles</c>, an ASP-10 table: Move, Name, Location, Size, Type; the 500 largest files of the chosen group,
/// largest first, nothing ticked, display-only rows disabled; <c>MoveLargest</c> runs ConfirmMove with exactly the ticked
/// files; <c>Text.LargestFilesNote</c> under it), the map 55 % and the list the rest, at least 8 rows at the default size;
/// and the row actions (eff96d2) of LargestFiles and Large and old: <c>RevealRow</c> and <c>MoveRow</c> shown only on the
/// row under the pointer or holding the keyboard focus, MoveRow confirming exactly its row and leaving the ticks, disabled
/// on a display-only row. RevealRow is never pressed (whether Explorer opens is not checked).
/// The new strings are read at run time (<see cref="Ui.Copy"/>). The fixture is the test's own folder under
/// <c>%USERPROFILE%\LeanUiMove-*</c>, with a subfolder made protected (display-only) through the test's own settings file,
/// restored afterwards; nothing is moved: every confirmation is cancelled.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ResultsViewTests
{
    private const long KB = ByteUnits.KB;
    private const int SmallFiles = 520;

    /// <summary>The typed files, largest first, and their group's label: the list's first rows in this order.</summary>
    private static readonly (string Relative, long Bytes, string Group)[] Typed =
    [
        (BigOld, 101 * ByteUnits.MB, Text.TypeArchive),
        ("film.mp4", 1400 * KB, Text.TypeVideo),
        ("disk.iso", 1300 * KB, Text.TypeDiskImage),
        ("bundle.zip", 1200 * KB, Text.TypeArchive),
        ("paper.pdf", 1100 * KB, Text.TypeDocument),
        ("setup.exe", 1000 * KB, Text.TypeApplication),
        ("machine.vhdx", 900 * KB, Text.TypeVirtualMachineImage),
        ("blob.bin", 800 * KB, Text.TypeOther),
        (@"clips\clip2.mp4", 700 * KB, Text.TypeVideo),
        (@"guarded\locked.mp4", 600 * KB, Text.TypeVideo),
    ];

    /// <summary>A file large and old enough for Large and old's defaults (100 MB, 12 months): its row actions there.</summary>
    private const string BigOld = "big-old.zip";

    /// <summary>The label of RevealRow in the contract ("Show in File Explorer").</summary>
    private const string ShowInFileExplorer = "Show in File Explorer";

    private static MoveFixture? fixture;

    private static string[] GroupLabels() =>
    [
        Ui.Copy("AllFiles"), Text.TypeVideo, Text.TypeDiskImage, Text.TypeArchive, Text.TypeDocument, Text.TypeApplication, Text.TypeVirtualMachineImage, Text.TypeOther,
    ];

    [ClassInitialize]
    public static void BuildTheTree(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = new MoveFixture("asp14-results");
        var seed = 1;
        foreach (var (relative, bytes, _) in Typed)
        {
            fixture.File(relative, bytes, seed++);
        }

        fixture.SetModified(BigOld, DateTime.UtcNow.AddYears(-3));

        // More files than the list holds: 520 small ones (4 KB, Other), so All files has 529 files and the list 500 rows.
        for (var i = 0; i < SmallFiles; i++)
        {
            fixture.File(@"many\s" + i.ToString("D3", CultureInfo.InvariantCulture) + ".bin", 4 * KB, 100 + i);
        }
    }

    [ClassCleanup]
    public static void RemoveTheTree() => fixture?.Dispose();

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>Starts the app on the fixture with guarded\ protected (its file display-only) and waits for the scan.</summary>
    private static AppSession StartScanned()
    {
        SettingsBackup.WriteProtectedFolders(fixture!.Sub("guarded"));
        var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        return app;
    }

    private static AutomationElement Column(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "TypeGroups", Ui.ElementTimeout);

    private static List<AutomationElement> Groups(AppSession app) => AppSession.FindAll(Column(app), "TypeGroup");

    private static AutomationElement Group(AppSession app, string label)
    {
        AutomationElement? found = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (found = Groups(app).FirstOrDefault(g => g.Current.Name.StartsWith(label, StringComparison.Ordinal))) is not null, Ui.ElementTimeout),
            $"no TypeGroup named \"{label}…\": [{string.Join(" | ", Groups(app).Select(g => g.Current.Name))}]");
        return found!;
    }

    /// <summary>Chosen: selected (SelectionItem) or toggled on (Toggle).</summary>
    private static bool IsChosen(AutomationElement group)
    {
        try
        {
            if (group.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                return ((SelectionItemPattern)select).Current.IsSelected;
            }

            return group.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) && ((TogglePattern)toggle).Current.ToggleState == ToggleState.On;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static List<string> Chosen(AppSession app) => Groups(app).Where(IsChosen).Select(g => g.Current.Name).ToList();

    private static void Choose(AppSession app, string label)
    {
        Ui.Click(Group(app, label));
        Assert.IsTrue(AppSession.WaitUntil(() => IsChosen(Group(app, label)), Ui.ElementTimeout), $"the group {label} is not chosen after a click: chosen [{string.Join(" | ", Chosen(app))}]");
    }

    internal static AutomationElement List(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "LargestFiles", Ui.ElementTimeout);

    /// <summary>One row of the largest files: its automation name, and its tick box's state and enabled state.</summary>
    private sealed record Row(string Name, ToggleState? Box, bool BoxEnabled);

    private static Row? Read(AutomationElement row)
    {
        try
        {
            var box = row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            return new Row(row.Current.Name ?? string.Empty, box is null ? null : Table.State(box), box?.Current.IsEnabled ?? false);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Every row of LargestFiles, top to bottom, the virtualised list scrolled from top to bottom so each row is
    /// realised once (rows are told apart by name: the fixture's names are unique).</summary>
    private static List<Row> AllRows(AppSession app)
    {
        var table = List(app);
        var seen = new List<Row>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Take()
        {
            foreach (var row in Table.LeafRows(table).Select(Read).OfType<Row>().Where(r => r.Name.Length > 0 && names.Add(r.Name)))
            {
                seen.Add(row);
            }
        }

        Take();
        var scroller = table.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
            .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        if (scroller is not null)
        {
            var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
            var step = Math.Max(1.0, scroll.Current.VerticalViewSize / 2);
            for (var at = 0.0; at <= 100.0; at += step)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100.0, at));
                System.Threading.Thread.Sleep(120);
                Take();
            }

            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100.0);
            System.Threading.Thread.Sleep(150);
            Take();
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0.0);
            System.Threading.Thread.Sleep(150);
        }

        return seen;
    }

    /// <summary>The rows realised now (the visible ones, at the top after a scroll back).</summary>
    private static List<Row> VisibleRows(AppSession app) => Table.LeafRows(List(app)).Select(Read).OfType<Row>().Where(r => r.Name.Length > 0).ToList();

    private static string Dump(IEnumerable<Row> rows) => "[" + string.Join(" | ", rows.Select(r => r.Name)) + "]";

    private static readonly Regex SizeInName = new(@"(?<n>[0-9]+(?:[.,][0-9]+)?)\s(?<u>bytes|KB|MB|GB|TB)\b", RegexOptions.CultureInvariant);

    /// <summary>The size a row's name shows (its first "<number> <unit>"), in bytes, or null.</summary>
    private static double? SizeOf(string rowName)
    {
        var match = SizeInName.Match(rowName);
        if (!match.Success)
        {
            return null;
        }

        var value = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var unit = match.Groups["u"].Value switch { "KB" => ByteUnits.KB, "MB" => ByteUnits.MB, "GB" => ByteUnits.GB, "TB" => 1024.0 * ByteUnits.GB, _ => 1.0 };
        return value * unit;
    }

    private static string FileName(string relative) => Path.GetFileName(relative);

    /// <summary>The tick box of the row of <paramref name="relative"/> among the visible rows.</summary>
    private static AutomationElement BoxOf(AppSession app, string relative)
    {
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                var row = Table.LeafRows(List(app)).FirstOrDefault(r => Table.Holds(r.Current.Name ?? string.Empty, fixture!.Sub(relative)));
                box = row?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                return box is not null;
            }, Ui.ElementTimeout),
            $"no row with a tick box for {relative} in LargestFiles: {Dump(VisibleRows(app))}");
        return box!;
    }

    // ---- the type column -----------------------------------------------------------------------------------------------

    // Contract "A type column": TypeGroups holds one TypeGroup per group in this order: All files, Video, Disk image,
    // Archive, Document, Application, Virtual machine image, Other; each named with its label and the bytes the scan measured
    // for it (ByteUnits; All files the total of every file); then FreeSpace, a text that cannot be chosen, with a size.
    // After the scan exactly one group is chosen: All files.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheTypeGroupsAreAllFilesAndTheSevenGroupsInOrderWithTheirBytes()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var labels = GroupLabels();
        List<AutomationElement> groups = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (groups = Groups(app)).Count == labels.Length, Ui.ElementTimeout),
            $"TypeGroups holds {groups.Count} TypeGroup rows, not {labels.Length}: [{string.Join(" | ", groups.Select(g => g.Current.Name))}]");
        var names = groups.Select(g => g.Current.Name).ToList();
        for (var i = 0; i < labels.Length; i++)
        {
            Assert.IsTrue(names[i].StartsWith(labels[i], StringComparison.Ordinal), $"TypeGroup {i} is \"{names[i]}\", not \"{labels[i]}…\"; groups [{string.Join(" | ", names)}]");
        }

        for (var i = 1; i < groups.Count; i++)
        {
            Assert.IsTrue(groups[i].Current.BoundingRectangle.Top >= groups[i - 1].Current.BoundingRectangle.Bottom - 1, $"{names[i]} is not below {names[i - 1]}");
        }

        var files = Typed.Select(t => t.Relative).Concat(Enumerable.Range(0, SmallFiles).Select(i => @"many\s" + i.ToString("D3", CultureInfo.InvariantCulture) + ".bin")).ToList();
        string GroupOf(string relative) => Typed.Any(t => t.Relative == relative) ? Typed.First(t => t.Relative == relative).Group : Text.TypeOther;
        var expected = labels.Skip(1).ToDictionary(l => l, l => files.Where(f => GroupOf(f) == l).Sum(f => MoveFixture.AllocatedBytes(fixture!.Sub(f))));
        expected[labels[0]] = files.Sum(f => MoveFixture.AllocatedBytes(fixture!.Sub(f)));
        for (var i = 0; i < labels.Length; i++)
        {
            var size = ByteUnits.Format(expected[labels[i]]);
            Assert.IsTrue(names[i].Contains(size, StringComparison.Ordinal), $"the group {labels[i]} is \"{names[i]}\": it does not show its {size}");
        }

        var free = AppSession.Find(Column(app), "FreeSpace", Ui.ElementTimeout);
        Assert.AreEqual(ControlType.Text, free.Current.ControlType, "FreeSpace is text only: " + Ui.Describe(free));
        Assert.IsFalse(free.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) || free.TryGetCurrentPattern(InvokePattern.Pattern, out _), "FreeSpace can be chosen: " + Ui.Describe(free));
        Assert.IsTrue(SizeInName.IsMatch(free.Current.Name), $"FreeSpace \"{free.Current.Name}\" shows no size");
        Assert.IsTrue(free.Current.BoundingRectangle.Top >= groups[^1].Current.BoundingRectangle.Bottom - 1, "FreeSpace is not the last row, under Other");

        CollectionAssert.AreEqual(new[] { names[0] }, Chosen(app), "after the scan exactly All files is chosen");
    }

    // Contract "A type column" and "The largest files": choosing a group filters the list to that group's files, exactly one
    // group is chosen then; every row's Type is the group. Video (film.mp4, clip2.mp4 and the protected locked.mp4, largest
    // first), then Other (blob.bin first, then the small files), then All files again (the largest file of the fixture first).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ChoosingAGroupFiltersTheListToThatGroup()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        Choose(app, Text.TypeVideo);
        Assert.AreEqual(1, Chosen(app).Count, "groups chosen: " + string.Join(" | ", Chosen(app)));
        List<Row> rows = [];
        var videos = Typed.Where(t => t.Group == Text.TypeVideo).Select(t => FileName(t.Relative)).ToList();
        Assert.IsTrue(AppSession.WaitUntil(() => (rows = AllRows(app)).Count == videos.Count, Ui.ElementTimeout),
            $"with Video chosen the list has {rows.Count} rows, not the {videos.Count} videos: {Dump(rows)}");
        for (var i = 0; i < videos.Count; i++)
        {
            Assert.IsTrue(rows[i].Name.Contains(videos[i], StringComparison.OrdinalIgnoreCase), $"with Video chosen row {i} is \"{rows[i].Name}\", not {videos[i]} (the videos, largest first): {Dump(rows)}");
        }
        foreach (var row in rows)
        {
            Assert.IsTrue(row.Name.Contains(Text.TypeVideo, StringComparison.Ordinal), $"a row's Type is not Video: \"{row.Name}\"");
        }

        Choose(app, Text.TypeOther);
        Assert.AreEqual(1, Chosen(app).Count, "groups chosen: " + string.Join(" | ", Chosen(app)));
        Assert.IsTrue(AppSession.WaitUntil(() => (rows = VisibleRows(app)).Count > 0 && rows[0].Name.Contains("blob.bin", StringComparison.OrdinalIgnoreCase), Ui.ElementTimeout),
            "with Other chosen the first row is not blob.bin: " + Dump(rows));
        rows = AllRows(app);
        Assert.AreEqual(500, rows.Count, $"with Other chosen ({SmallFiles + 1} files: blob.bin and the small files) the list holds {rows.Count} rows, not the 500 largest");
        foreach (var row in rows)
        {
            Assert.IsTrue(row.Name.Contains(Text.TypeOther, StringComparison.Ordinal), $"a row's Type is not Other: \"{row.Name}\"");
        }

        // All files again: the largest file of the whole fixture leads (the typed table's largest, not the largest video).
        var largest = FileName(Typed.MaxBy(t => t.Bytes).Relative);
        Choose(app, Ui.Copy("AllFiles"));
        Assert.IsTrue(AppSession.WaitUntil(() => (rows = VisibleRows(app)).Count > 0 && rows[0].Name.Contains(largest, StringComparison.OrdinalIgnoreCase), Ui.ElementTimeout),
            $"with All files chosen again the first row is not {largest}, the largest file: " + Dump(rows));
    }

    // ---- the largest files ---------------------------------------------------------------------------------------------

    // Contract "The largest files": an ASP-10 table with the header buttons Move, Name, Location, Size and Type; with All
    // files chosen it holds the 500 largest files (529 in the fixture), largest first (the nine typed files lead in size
    // order; no row shows a size larger than the row above), nothing ticked, the protected locked.mp4 row's box disabled
    // with a hint in its name, the others enabled; MoveLargest is disabled with nothing ticked; Text.LargestFilesNote shows.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheLargestFilesAreSortedBySizeAtMost500NothingTickedAndDisplayOnlyDisabled()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var table = List(app);
        Table.AssertColumns(table, 1, Text.ColumnMove, Text.ColumnName, Ui.Copy("ColumnLocation"), Text.ColumnSize, Text.ColumnType);
        var note = Ui.Copy("LargestFilesNote");
        Assert.IsTrue(Ui.AnyNameContains(app.Window(Ui.WindowTimeout), note), $"no \"{note}\" under the list");
        var move = AppSession.Find(app.Window(Ui.WindowTimeout), "MoveLargest", Ui.ElementTimeout);
        Assert.IsFalse(move.Current.IsEnabled, "MoveLargest is enabled with nothing ticked");

        var rows = AllRows(app);
        Console.WriteLine($"{rows.Count} rows; first: {Dump(rows.Take(12))}");
        Assert.AreEqual(500, rows.Count, $"the list holds {rows.Count} rows, not the 500 largest of {Typed.Length + SmallFiles} files");
        for (var i = 0; i < Typed.Length; i++)
        {
            Assert.IsTrue(rows[i].Name.Contains(FileName(Typed[i].Relative), StringComparison.OrdinalIgnoreCase), $"row {i} is \"{rows[i].Name}\", not {FileName(Typed[i].Relative)} (largest first)");
        }

        for (var i = 1; i < rows.Count; i++)
        {
            var (above, here) = (SizeOf(rows[i - 1].Name), SizeOf(rows[i].Name));
            Assert.IsTrue(above is not null && here is not null, $"rows {i - 1}/{i} show no size: \"{rows[i - 1].Name}\", \"{rows[i].Name}\"");
            Assert.IsTrue(here <= above, $"row {i} (\"{rows[i].Name}\") is larger than row {i - 1} (\"{rows[i - 1].Name}\")");
        }

        Assert.AreEqual(0, rows.Count(r => r.Box != ToggleState.Off), "rows ticked, or without a tick box: " + Dump(rows.Where(r => r.Box != ToggleState.Off)));
        var locked = rows.Single(r => r.Name.Contains("locked.mp4", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(locked.BoxEnabled, "the display-only row (inside a protected folder) has an enabled box: " + locked.Name);
        Assert.IsNotNull(Table.HintIn(locked.Name), "the display-only row's name holds no hint: " + locked.Name);
        var disabled = rows.Where(r => !r.BoxEnabled && r != locked).ToList();
        Assert.AreEqual(0, disabled.Count, "movable rows with a disabled box: " + Dump(disabled));
    }

    // Contract "The largest files": MoveLargest runs ConfirmMove listing exactly the ticked files (disk.iso and paper.pdf
    // ticked: two ConfirmItem rows, "<path>, about <size>"); the dialog is cancelled and nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void MoveLargestConfirmsExactlyTheTickedFiles()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var ticked = new[] { "disk.iso", "paper.pdf" };
        var identities = ticked.ToDictionary(t => t, t => MoveFixture.Identity(fixture!.Sub(t)));
        foreach (var name in ticked)
        {
            Table.Tick(BoxOf(app, name), () => BoxOf(app, name), name + "'s tick box");
        }

        var move = AppSession.Find(app.Window(Ui.WindowTimeout), "MoveLargest", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "MoveLargest is disabled with two files ticked");
        Ui.Click(move);
        try
        {
            var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
            List<AutomationElement> items = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count >= 2, Ui.ElementTimeout), "the confirmation lists fewer than two items: " + Ui.Dump(dialog));
            Assert.AreEqual(2, items.Count, "the confirmation's items: " + string.Join(" | ", items.Select(i => i.Current.Name)));
            foreach (var name in ticked)
            {
                var expected = Text.ConfirmItem(fixture!.Sub(name), ByteUnits.Format(MoveFixture.AllocatedBytes(fixture.Sub(name))));
                Assert.IsTrue(items.Any(i => i.Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase)), $"no ConfirmItem \"{expected}\": [{string.Join(" | ", items.Select(i => i.Current.Name))}]");
            }
        }
        finally
        {
            if (app.FindInAnyWindow("CancelMove") is { } cancel)
            {
                Ui.Click(cancel);
            }
        }

        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is { } r && !r.Current.IsOffscreen, Ui.NeverWatch), "a move report appeared");
        foreach (var name in ticked)
        {
            Assert.AreEqual(identities[name], MoveFixture.Identity(fixture!.Sub(name)), name + " is not the same file in place");
        }
    }

    // ---- the map beside the list -------------------------------------------------------------------------------------

    // Contract "A type column": choosing a group does not change the map's layout (every tile keeps its name and its box)
    // and dims every tile of another group to LeanPanel: with Video chosen, film.mp4's centre keeps the video tone, blob.bin's
    // (Other) is LeanPanel; All files chosen again gives blob.bin its colour back.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ChoosingAGroupKeepsTheMapLayoutAndDimsTheOtherGroups()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var before = Layout(app);
        Choose(app, Text.TypeVideo);
        System.Threading.Thread.Sleep(500);
        var after = Layout(app);
        CollectionAssert.AreEquivalent(before.Keys.ToList(), after.Keys.ToList(), "choosing Video changed the tiles");
        var moved = before.Where(t => Math.Abs(t.Value.Left - after[t.Key].Left) > 1 || Math.Abs(t.Value.Top - after[t.Key].Top) > 1
            || Math.Abs(t.Value.Width - after[t.Key].Width) > 1 || Math.Abs(t.Value.Height - after[t.Key].Height) > 1).Select(t => $"{t.Key}: {t.Value} -> {after[t.Key]}").ToList();
        Assert.AreEqual(0, moved.Count, "choosing Video moved tiles: " + string.Join("; ", moved));

        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the colours are the system's, so the dimming is not checked");
        }

        var video = Rgb.Parse("#b0adff");
        var other = Rgb.Parse("#8a8a8c");
        Assert.IsTrue(CentreShare(app, "film.mp4", video, Brand.Tolerance) >= 0.8, "with Video chosen, film.mp4's tile lost its video tone");
        Assert.IsTrue(CentreShare(app, "blob.bin", Brand.Panel, Brand.NeutralTolerance) >= 0.8, "with Video chosen, blob.bin's tile (Other) is not dimmed to LeanPanel");
        Choose(app, Ui.Copy("AllFiles"));
        Assert.IsTrue(AppSession.WaitUntil(() => CentreShare(app, "blob.bin", other, Brand.Tolerance) >= 0.8, Ui.ElementTimeout), "with All files chosen again, blob.bin's tile does not get its colour back");
    }

    private static Dictionary<string, Rect> Layout(AppSession app) =>
        AppSession.FindAll(Ui.Map(app), "Tile").ToDictionary(t => t.Current.Name + " @ " + t.Current.HelpText, t => t.Current.BoundingRectangle, StringComparer.Ordinal);

    /// <summary>The share of <paramref name="colour"/> in the 9 × 9 px centre of the tile <paramref name="name"/>.</summary>
    private static double CentreShare(AppSession app, string name, Rgb colour, int tolerance)
    {
        var handle = new AutomationElementHandle(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle));
        WindowOnScreen.Settle(handle, AppSession.Find(app.Window(Ui.WindowTimeout), "VolumeCard", Ui.ElementTimeout).Current.BoundingRectangle);
        var r = Ui.TileOf(Ui.Map(app), name).Current.BoundingRectangle;
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
        var half = (int)Math.Round(4 * WindowOnScreen.Scale(handle));
        var (x, y) = ((int)(r.Left + (r.Width / 2)), (int)(r.Top + (r.Height / 2)));
        var centre = capture.Area(x - half, y - half, x + half + 1, y + half + 1);
        var share = ScreenCapture.Share(centre, colour, tolerance);
        Console.WriteLine($"{name} {r}: {colour} {share:P0}; commonest {ScreenCapture.Commonest(centre, 2)}");
        return share;
    }

    // Contract "The split" with review round 1: the map takes the top 55 % and the list the rest, the map above the list;
    // the map shows at least 8 tiles; the list shows at least 4 whole rows when the window is under 1000 px tall (about
    // 800 px on the VM's 1280 x 800 screen) and at least 8 at 1000 px or more, read from the window's actual height.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheMapTakes55PercentAndTheListShowsRowsForTheWindowHeight()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var map = Ui.Map(app).Current.BoundingRectangle;
        var list = List(app).Current.BoundingRectangle;
        Assert.IsTrue(list.Top >= map.Bottom - 1, $"the list {list} is not under the map {map}");
        var share = map.Height / (list.Bottom - map.Top);
        Assert.IsTrue(share is >= 0.5 and <= 0.6, $"the map takes {share:P0} of the height of the map and the list ({map}, {list}), not 55 %");
        Assert.IsTrue(AppSession.FindAll(Ui.Map(app), "Tile").Count(t => !t.Current.IsOffscreen) >= 8, "the map shows fewer than 8 tiles");
        AssertRowsForHeight(app, "at the default size");
    }

    // Review round 2, MINOR "the 8-row check passes when it is skipped": with the window resized to 1040 px tall
    // (TransformPattern) the list shows at least 8 whole rows. When the window cannot be resized, or the work area does not
    // allow 1000 px (the VM's 1280 x 800 screen), the test is Inconclusive, never a pass.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void TheListShowsEightRowsWhenTheWindowIsAThousandPixelsTall()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        ResizeToAThousandOrInconclusive(app);
        AssertRowsForHeight(app, "after resizing to 1040 px");
    }

    /// <summary>Resizes the window to 1040 px tall through TransformPattern; Inconclusive (never a pass) when it cannot be
    /// resized or the work area does not allow 1000 px.</summary>
    internal static void ResizeToAThousandOrInconclusive(AppSession app)
    {
        var window = app.Window(Ui.WindowTimeout);
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || !((TransformPattern)pattern).Current.CanResize)
        {
            Assert.Inconclusive("the window cannot be resized through UI Automation: the 8-row check at 1000 px was not run");
        }

        var transform = (TransformPattern)pattern;
        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(window.Current.NativeWindowHandle)));
        transform.Move(0, 0);
        transform.Resize(window.Current.BoundingRectangle.Width, 1040 * scale);
        System.Threading.Thread.Sleep(1500);
        var height = app.Window(Ui.WindowTimeout).Current.BoundingRectangle.Height / scale;
        if (height < 1000)
        {
            Assert.Inconclusive($"the work area allows a window of only {height:F0} px: the 8-row check at 1000 px was not run");
        }
    }

    /// <summary>The rows of LargestFiles that are whole on screen now, top to bottom. A row cut off at the bottom of the list
    /// has a rectangle that is only the part on screen (a 4 px sliver is still "inside" the list), so a row that ends at the
    /// list's bottom edge counts only when it is as high as the shortest row that ends above the edge, to 1.5 px (a row
    /// that ends above the edge is whole).</summary>
    internal static List<AutomationElement> WholeRows(AppSession app)
    {
        var list = List(app).Current.BoundingRectangle;
        var onScreen = Table.LeafRows(List(app))
            .Where(r =>
            {
                var b = r.Current.BoundingRectangle;
                return !r.Current.IsOffscreen && !b.IsEmpty && b.Top >= list.Top - 1 && b.Bottom <= list.Bottom + 1;
            })
            .OrderBy(r => r.Current.BoundingRectangle.Top)
            .ToList();
        var inside = onScreen.Where(r => r.Current.BoundingRectangle.Bottom < list.Bottom - 1.5).ToList();
        var reference = inside.Count == 0 ? 0 : inside.Min(r => r.Current.BoundingRectangle.Height);
        return onScreen.Where(r => r.Current.BoundingRectangle.Bottom < list.Bottom - 1.5 || r.Current.BoundingRectangle.Height >= reference - 1.5).ToList();
    }

    /// <summary>At least 8 whole rows of LargestFiles on screen when the window is 1000 px tall or more (in DIPs), at least 4
    /// otherwise (a row cut off at the bottom is not whole).</summary>
    internal static void AssertRowsForHeight(AppSession app, string when)
    {
        var window = app.Window(Ui.WindowTimeout);
        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(window.Current.NativeWindowHandle)));
        var height = window.Current.BoundingRectangle.Height / scale;
        var visible = WholeRows(app).Count;
        var needed = height >= 1000 ? 8 : 4;
        Console.WriteLine($"{when}: window {height:F0} px tall, {visible} whole rows shown, {needed} needed");
        Assert.IsTrue(visible >= needed, $"{when} the window is {height:F0} px tall and the list shows {visible} whole rows on screen, not at least {needed}");
    }

    // ---- row actions ---------------------------------------------------------------------------------------------------

    private static readonly string[] ActionIds = ["RevealRow", "MoveRow"];

    /// <summary>The row of <paramref name="file"/> in <paramref name="scope"/> among the realised rows, or null.</summary>
    private static AutomationElement? RowOf(AutomationElement scope, string file) =>
        Table.LeafRows(scope).FirstOrDefault(r => (r.Current.Name ?? string.Empty).Contains(file, StringComparison.OrdinalIgnoreCase));

    private static AutomationElement WaitRow(AutomationElement scope, string file)
    {
        AutomationElement? row = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (row = RowOf(scope, file)) is not null, Ui.ElementTimeout), $"no row for {file}: [{string.Join(" | ", Table.LeafRowNames(scope))}]");
        return row!;
    }

    /// <summary>The screen rectangle of the row of <paramref name="file"/>, scrolled into view first when the table has it
    /// below (or above) what it shows: the window's default height shows about eight rows, and a row outside the viewport
    /// has an empty rectangle, where a pointer cannot rest.</summary>
    private static System.Windows.Rect VisibleRowRect(AutomationElement scope, string file)
    {
        var row = WaitRow(scope, file);
        if (row.Current.BoundingRectangle.IsEmpty || row.Current.IsOffscreen)
        {
            if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern))
            {
                ((ScrollItemPattern)pattern).ScrollIntoView();
            }
        }

        System.Windows.Rect rect = default;
        Assert.IsTrue(AppSession.WaitUntil(() => !(rect = WaitRow(scope, file).Current.BoundingRectangle).IsEmpty && !WaitRow(scope, file).Current.IsOffscreen, Ui.ElementTimeout),
            $"the row of {file} cannot be scrolled into view (rectangle {rect}): " + Ui.Describe(row));
        return rect;
    }

    /// <summary>The row-action buttons on screen in <paramref name="scope"/>, as "<row name>: <id>".</summary>
    private static List<string> ShownActions(AutomationElement scope)
    {
        var shown = new List<string>();
        foreach (var id in ActionIds)
        {
            foreach (var button in Table.Controls(scope, id))
            {
                try
                {
                    if (!button.Current.IsOffscreen && !button.Current.BoundingRectangle.IsEmpty)
                    {
                        shown.Add(Table.RowName(button) + ": " + id);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        return shown;
    }

    /// <summary>The on-screen action <paramref name="id"/> inside the row of <paramref name="file"/>, within a few seconds, or null.</summary>
    private static AutomationElement? ActionOf(AutomationElement scope, string file, string id) =>
        AppSession.WaitFor(() => RowOf(scope, file) is { } row && Table.InRow(row, id) is { } b && !b.Current.IsOffscreen ? b : null, TimeSpan.FromSeconds(3));

    /// <summary>Focus and pointer away from the table's rows: the focus on Scan again (on Overview) or on Large and old's
    /// MinimumSize (in that view), the pointer resting in the sidebar's empty band between History and Settings.</summary>
    private static void Away(AppSession app)
    {
        var window = app.Window(Ui.WindowTimeout);
        var target = app.FindInAnyWindow("LargeOldView") is { } view && !view.Current.IsOffscreen
            ? AppSession.Find(view, "MinimumSize", Ui.ElementTimeout)
            : AppSession.Find(window, "Scan", Ui.ElementTimeout);
        Ui.Focus(target);
        var sidebar = Ui.Sidebar(app).Current.BoundingRectangle;
        var history = Ui.SidebarItem(app, "History").Current.BoundingRectangle;
        var settings = Ui.SidebarItem(app, "Settings").Current.BoundingRectangle;
        Mouse.HoverAt(sidebar.Left + (sidebar.Width / 2), (history.Bottom + settings.Top) / 2);
    }

    /// <summary>Checks the row actions of <paramref name="scope"/> (LargestFiles or Large and old's table) on two movable
    /// rows: none shown with the focus and the pointer away; with the keyboard focus on the first row's tick box, both
    /// buttons of that row only (RevealRow named "Show in File Explorer", MoveRow named Text.MoveToRecycleBin, both real
    /// buttons), Tab from the box reaching them; focus away again, none; the pointer over the second row, both buttons of
    /// that row only.</summary>
    private static List<string> RowActionProblems(AppSession app, Func<AutomationElement> scope, string where, string first, string second)
    {
        var problems = new List<string>();
        Away(app);
        if (ShownActions(scope()) is { Count: > 0 } early)
        {
            problems.Add($"{where}: with the focus and the pointer away, row actions are shown: {string.Join(" | ", early)}");
        }

        var box = WaitRow(scope(), first).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
        Assert.IsNotNull(box, $"{where}: the row of {first} has no tick box");
        Ui.Focus(box);
        var reveal = ActionOf(scope(), first, "RevealRow");
        var move = ActionOf(scope(), first, "MoveRow");
        if (reveal is null || move is null)
        {
            problems.Add($"{where}: with the focus in the row of {first}, RevealRow {(reveal is null ? "missing" : "shown")}, MoveRow {(move is null ? "missing" : "shown")}");
        }
        else
        {
            foreach (var (button, label) in new[] { (reveal, ShowInFileExplorer), (move, Text.MoveToRecycleBin) })
            {
                if (button.Current.ControlType != ControlType.Button || button.Current.Name != label || !button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                {
                    problems.Add($"{where}: {Ui.Describe(button)} is not a button named \"{label}\"");
                }
            }

            if (!move.Current.IsEnabled)
            {
                problems.Add($"{where}: MoveRow is disabled on the movable row of {first}");
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            for (var press = 0; press < 6 && reached.Count < 2; press++)
            {
                Keys.Press(Keys.Tab);
                System.Threading.Thread.Sleep(200);
                if (AutomationElement.FocusedElement is { } focused && ActionIds.Contains(focused.Current.AutomationId) && Ui.IsWithin(focused, WaitRow(scope(), first)))
                {
                    reached.Add(focused.Current.AutomationId);
                }
            }

            if (reached.Count < 2)
            {
                problems.Add($"{where}: Tab from the tick box of {first} reached only [{string.Join(", ", reached)}] of RevealRow and MoveRow in its row");
            }
        }

        var others = ShownActions(scope()).Where(a => !a.Contains(first, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            problems.Add($"{where}: with the focus in the row of {first}, other rows show actions too: {string.Join(" | ", others)}");
        }

        Away(app);
        if (!AppSession.WaitUntil(() => ShownActions(scope()).Count == 0, TimeSpan.FromSeconds(3)))
        {
            problems.Add($"{where}: with the focus and the pointer away again, row actions stay shown: {string.Join(" | ", ShownActions(scope()))}");
        }

        var r = VisibleRowRect(scope(), second);
        Mouse.HoverAt(r.Left + (r.Width / 3), r.Top + (r.Height / 2));
        if (ActionOf(scope(), second, "RevealRow") is null || ActionOf(scope(), second, "MoveRow") is null)
        {
            problems.Add($"{where}: with the pointer over the row of {second}, its RevealRow and MoveRow are not both shown: {string.Join(" | ", ShownActions(scope()))}");
        }

        var hoverOthers = ShownActions(scope()).Where(a => !a.Contains(second, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hoverOthers.Count > 0)
        {
            problems.Add($"{where}: with the pointer over the row of {second}, other rows show actions too: {string.Join(" | ", hoverOthers)}");
        }

        Away(app);
        return problems;
    }

    // Contract "Row actions": in LargestFiles and in Large and old's table, RevealRow and MoveRow show only on the row with
    // the keyboard focus (reached by Tab from inside it) or under the pointer, and are hidden otherwise.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void RowActionsShowOnTheFocusedOrHoveredRowOnly()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var problems = RowActionProblems(app, () => List(app), "LargestFiles", "film.mp4", "disk.iso");
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        WaitRow(view, BigOld);
        problems.AddRange(RowActionProblems(app, () => Ui.FindAnywhere(app, "LargeOldView", Ui.ElementTimeout), "Large and old", BigOld, BigOld));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "Row actions": MoveRow on an unticked row while other rows are ticked opens ConfirmMove for exactly that row
    // (one ConfirmItem, bundle.zip), and after Cancel the ticks are as they were (disk.iso and paper.pdf ticked, bundle.zip
    // not). Nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void MoveRowConfirmsExactlyItsRowAndLeavesTheTicks()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        var ticked = new[] { "disk.iso", "paper.pdf" };
        foreach (var name in ticked)
        {
            Table.Tick(BoxOf(app, name), () => BoxOf(app, name), name + "'s tick box");
        }

        const string Target = "bundle.zip";
        var identity = MoveFixture.Identity(fixture!.Sub(Target));
        Ui.Focus(BoxOf(app, Target));
        var move = ActionOf(List(app), Target, "MoveRow");
        Assert.IsNotNull(move, $"no MoveRow shown in the focused row of {Target}");
        Ui.Click(move);
        try
        {
            var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
            List<AutomationElement> items = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count >= 1, Ui.ElementTimeout), "the confirmation lists no item: " + Ui.Dump(dialog));
            System.Threading.Thread.Sleep(300);
            items = AppSession.FindAll(dialog, "ConfirmItem");
            var expected = Text.ConfirmItem(fixture.Sub(Target), ByteUnits.Format(MoveFixture.AllocatedBytes(fixture.Sub(Target))));
            Assert.AreEqual(1, items.Count, "MoveRow confirms more than its row: " + string.Join(" | ", items.Select(i => i.Current.Name)));
            Assert.IsTrue(items[0].Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase), $"the ConfirmItem is \"{items[0].Current.Name}\", not \"{expected}\"");
        }
        finally
        {
            if (app.FindInAnyWindow("CancelMove") is { } cancel)
            {
                Ui.Click(cancel);
            }
        }

        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        foreach (var name in ticked)
        {
            Assert.AreEqual(ToggleState.On, Table.State(BoxOf(app, name)), name + " is no longer ticked after MoveRow on another row");
        }

        Assert.AreEqual(ToggleState.Off, Table.State(BoxOf(app, Target)), Target + " became ticked by its MoveRow");
        Assert.AreEqual(identity, MoveFixture.Identity(fixture.Sub(Target)), Target + " is not the same file in place");
    }

    // Contract "Row actions": on a display-only row (locked.mp4, inside a protected folder) MoveRow is disabled; shown by
    // the pointer over the row (its tick box is disabled, so it cannot take the focus).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void MoveRowIsDisabledOnADisplayOnlyRow()
    {
        using var settings = new SettingsBackup();
        using var app = StartScanned();
        Away(app);
        var r = VisibleRowRect(List(app), "locked.mp4");
        Mouse.HoverAt(r.Left + (r.Width / 3), r.Top + (r.Height / 2));
        var move = ActionOf(List(app), "locked.mp4", "MoveRow");
        Assert.IsNotNull(move, "no MoveRow shown with the pointer over the display-only row of locked.mp4: " + string.Join(" | ", ShownActions(List(app))));
        Assert.IsFalse(move.Current.IsEnabled, "MoveRow is enabled on a display-only row");
        Assert.IsNotNull(ActionOf(List(app), "locked.mp4", "RevealRow"), "no RevealRow on the display-only row (showing a file is always allowed)");
        Away(app);
    }
}
