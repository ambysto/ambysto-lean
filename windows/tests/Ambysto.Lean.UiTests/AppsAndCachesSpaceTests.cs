using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-20 phase C2 (intent/2026-10-21-asp-20-suggest-catalog.plan.md, "C2 UI", owner decision 15): the Apps and caches view
/// gets a selector and two read-only tables. The plan names no AutomationIds for C2; these tests use (the builder follows them):
/// selector <c>AppsSection</c> with the choices <c>AppsSectionCaches</c> ("Caches", the default, the phase B table),
/// <c>AppsSectionSpace</c> ("Space by app") and <c>AppsSectionDisks</c> ("Virtual disks"); table <c>SpaceTable</c> (list
/// <c>SpaceList</c>, row <c>SpaceRow</c>, message <c>SpaceMessage</c>), columns Name, Publisher, Install folder (the header starts with
/// "Install folder"; a cell says "estimated" when the size is the uninstall entry's), Caches, Total; table <c>DisksTable</c> (list
/// <c>DisksList</c>, row <c>DiskRow</c>, message <c>DisksMessage</c>, per-row button <c>DiskCopyAdvice</c>), columns Name, Kind, Size,
/// Advice. A section that is not chosen is not shown (absent from the UI tree or off screen), and in the two new sections there are no
/// tick boxes, no Clear and no Suggest and no Pro label. A fake uv.exe is first on PATH as in <see cref="AppsAndCachesTests"/>: no real
/// cache is cleaned, and the log must stay free of <c>cache clean</c> while the tables are browsed. A row's cells are not separate
/// elements in the plan: the row's automation name joins them, so sizes are read from the texts in the row.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AppsAndCachesSpaceTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);

    private static readonly Regex SizeText = new(@"\d+(?:[.,]\d+)?\s?(?:bytes|KB|MB|GB|TB)\b", RegexOptions.CultureInvariant);

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static AppSession StartPro(FakeUv fake) => AppSession.StartWithEnvironment(fake.Variables(), "--pro");

    private static AppSession StartFree(FakeUv fake)
    {
        ProApp.RequireReleaseApp();
        return AppSession.StartWithEnvironment(fake.Variables());
    }

    private static AutomationElement OpenApps(AppSession app) => Ui.OpenView(app, "AppsAndCaches", "AppsView");

    private static bool ShownNow(AutomationElement view, string id)
    {
        try
        {
            return AppSession.FindAll(view, id).Any(e => !e.Current.IsOffscreen);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool CachesShown(AutomationElement view) => ShownNow(view, "AppsItem") || ShownNow(view, "AppsOpenFolder");

    private static AutomationElement Choice(AutomationElement view, string id) => AppSession.Find(AppSession.Find(view, "AppsSection", Ui.ElementTimeout), id, Ui.ElementTimeout);

    private static bool Chosen(AutomationElement choice)
    {
        try
        {
            if (choice.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                return ((SelectionItemPattern)select).Current.IsSelected;
            }

            return choice.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) && ((TogglePattern)toggle).Current.ToggleState == ToggleState.On;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void Choose(AutomationElement view, string id)
    {
        var choice = Choice(view, id);
        if (choice.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (choice.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
        }
        else if (choice.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
        }
        else
        {
            Assert.Fail(id + " can be neither invoked, selected nor toggled");
        }

        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(Choice(view, id)), Ui.ElementTimeout), id + " is not chosen after choosing it");
    }

    /// <summary>Waits until exactly the wanted section is shown: "caches", "space" or "disks".</summary>
    private static void AssertOnlyShown(AutomationElement view, string section)
    {
        bool Matches() => (section == "caches" || !CachesShown(view)) && ShownNow(view, "SpaceTable") == (section == "space") && ShownNow(view, "DisksTable") == (section == "disks");
        Assert.IsTrue(AppSession.WaitUntil(Matches, Ui.ElementTimeout),
            $"section {section}: caches table shown {CachesShown(view)}, SpaceTable shown {ShownNow(view, "SpaceTable")}, DisksTable shown {ShownNow(view, "DisksTable")}: " + Ui.Dump(view));
        if (section == "caches")
        {
            Assert.IsTrue(AppSession.WaitUntil(() => CachesShown(view), ReadTimeout), "the phase B table shows no row in the Caches section: " + Ui.Dump(view));
        }
    }

    private static bool Enabled(AutomationElement? element)
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

    private static string NameOf(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    /// <summary>The size texts of a row, left to right: the row's own cells when they are exposed as elements, else the row's name.</summary>
    private static List<string> Sizes(AutomationElement row)
    {
        var cells = Table.Texts(row).Select(t => t.Name).Where(n => SizeText.IsMatch(n) && SizeText.Match(n).Value == n.Trim()).ToList();
        return cells.Count > 0 ? cells : SizeText.Matches(NameOf(row)).Select(m => m.Value).ToList();
    }

    private static double Bytes(string size)
    {
        var parts = Regex.Match(size, @"^(\d+(?:[.,]\d+)?)\s?(bytes|KB|MB|GB|TB)$");
        Assert.IsTrue(parts.Success, "not a size text: " + size);
        var value = double.Parse(parts.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return value * parts.Groups[2].Value switch { "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, "TB" => 1024d * 1024 * 1024 * 1024, _ => 1d };
    }

    private static List<AutomationElement> Rows(AutomationElement view, string tableId, string rowId)
    {
        var table = AppSession.TryFind(view, tableId);
        return table is null ? [] : AppSession.FindAll(table, rowId).Where(r => !SafeOffscreen(r)).ToList();
    }

    private static bool SafeOffscreen(AutomationElement element)
    {
        try
        {
            return element.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return true;
        }
    }

    /// <summary>The header buttons of <paramref name="table"/> whose name is or starts with <paramref name="text"/>, outside every row.</summary>
    private static AutomationElement HeaderStartingWith(AutomationElement table, string text)
    {
        AutomationElement? found = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            var buttons = table.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true)).Cast<AutomationElement>();
            found = buttons.FirstOrDefault(b => NameOf(b).StartsWith(text, StringComparison.Ordinal) && Table.RowOf(b) is null);
            return found is not null;
        }, Ui.ElementTimeout), $"no column header button starting with \"{text}\" in {table.Current.AutomationId}: {Ui.Dump(table)}");
        return found!;
    }

    private static void AssertHeadersInOrder(AutomationElement table, params string[] startingWith)
    {
        var lefts = startingWith.Select(s => HeaderStartingWith(table, s).Current.BoundingRectangle.Left).ToList();
        for (var i = 1; i < lefts.Count; i++)
        {
            Assert.IsTrue(lefts[i] > lefts[i - 1], $"the column \"{startingWith[i]}\" (left {lefts[i]:F0}) is not right of \"{startingWith[i - 1]}\" (left {lefts[i - 1]:F0}); expected {string.Join(", ", startingWith)}");
        }
    }

    private static int TicksIn(AutomationElement scope)
    {
        var boxes = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true));
        return boxes.Count;
    }

    private static void AssertNothingToTickOrClear(AutomationElement view, string section)
    {
        var table = AppSession.Find(view, section == "space" ? "SpaceTable" : "DisksTable", Ui.ElementTimeout);
        Assert.AreEqual(0, TicksIn(table), $"the {section} table holds a tick box or toggle: " + Ui.Dump(table));
        Assert.IsFalse(ShownNow(view, "AppsItem"), $"a Caches tick box is shown in the {section} section");
        Assert.IsFalse(ShownNow(view, "ClearApps"), $"Clear is shown in the {section} section");
        Assert.IsFalse(ShownNow(view, "SuggestApps"), $"Suggest is shown in the {section} section");
        Assert.IsFalse(Ui.Names(view).Any(n => n == Text.ProLabel), $"a \"{Text.ProLabel}\" label is shown in the {section} section (rows are free, decision 15): " + Ui.Dump(view));
        Assert.IsFalse(Ui.Names(table).Any(n => n == Text.ProLabel), $"a \"{Text.ProLabel}\" label is in the {section} table");
    }

    private static void AssertNoCleanAndNothingTicked(AutomationElement view, FakeUv fake, string when)
    {
        System.Threading.Thread.Sleep(Ui.NeverWatch);
        Assert.AreEqual(0, fake.CleanCalls(), when + ": the fake uv was told to clean: [" + string.Join(" | ", fake.Calls()) + "]");
        Assert.IsFalse(fake.CacheIsEmpty(), when + ": the fake cache was emptied");
        Assert.IsTrue(Table.Controls(view, "AppsItem").All(b => !Table.State(b).Equals(ToggleState.On)), when + ": a Caches row got ticked: " + Table.Dump(view, "AppsItem"));
    }

    // ---- the selector ---------------------------------------------------------------------------------------------------

    private static void SelectorBody(AppSession app, FakeUv fake)
    {
        var view = OpenApps(app);
        var selector = AppSession.Find(view, "AppsSection", Ui.ElementTimeout);
        Assert.IsFalse(selector.Current.IsOffscreen, "AppsSection is not shown");
        Assert.AreEqual("Caches", NameOf(Choice(view, "AppsSectionCaches")), "the name of the first choice");
        Assert.AreEqual("Space by app", NameOf(Choice(view, "AppsSectionSpace")), "the name of the second choice");
        Assert.AreEqual("Virtual disks", NameOf(Choice(view, "AppsSectionDisks")), "the name of the third choice");
        Assert.IsTrue(Chosen(Choice(view, "AppsSectionCaches")), "Caches is not chosen by default");
        Assert.IsFalse(Chosen(Choice(view, "AppsSectionSpace")), "Space by app is chosen by default");
        Assert.IsFalse(Chosen(Choice(view, "AppsSectionDisks")), "Virtual disks is chosen by default");
        AssertOnlyShown(view, "caches");
        Assert.IsNotNull(Table.ControlWith(view, "AppsItem", Text.WhyUvCache), "the phase B table lacks the fake uv row: " + Table.Dump(view, "AppsItem"));

        Choose(view, "AppsSectionSpace");
        AssertOnlyShown(view, "space");
        Assert.IsFalse(Chosen(Choice(view, "AppsSectionCaches")), "Caches stays chosen with Space by app");

        Choose(view, "AppsSectionDisks");
        AssertOnlyShown(view, "disks");
        Assert.IsFalse(Chosen(Choice(view, "AppsSectionSpace")), "Space by app stays chosen with Virtual disks");

        Choose(view, "AppsSectionCaches");
        AssertOnlyShown(view, "caches");
        Assert.IsNotNull(Table.ControlWith(view, "AppsItem", Text.WhyUvCache), "the uv row is gone after coming back to Caches: " + Table.Dump(view, "AppsItem"));
        AssertNoCleanAndNothingTicked(view, fake, "after switching through the three sections");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProSelectorHasThreeChoicesCachesIsDefaultAndEachChoiceShowsOnlyItsTable()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("sel-pro");
        using var app = StartPro(fake);
        SelectorBody(app, fake);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void FreeSelectorHasThreeChoicesCachesIsDefaultAndEachChoiceShowsOnlyItsTable()
    {
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("sel-free");
        using var app = StartFree(fake);
        SelectorBody(app, fake);
    }

    // ---- Space by app ---------------------------------------------------------------------------------------------------

    private static bool EdgeIsInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge");
            if (key is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void SpaceBody(AppSession app, FakeUv fake)
    {
        var view = OpenApps(app);
        // Edge's Guide row in the phase B table, read first: its size must come back in Edge's Caches cell. A browser Guide row has no
        // Open folder button and no tick box (decision 14: a text note only), so it is found by its row id, AppsRow.
        AppSession.WaitUntil(() => Table.Controls(view, "AppsRow").Count > 0, ReadTimeout);
        var edgeCacheRows = Table.Controls(view, "AppsRow")
            .Where(r => NameOf(r).Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase)).DistinctBy(r => NameOf(r)).ToList();
        var edgeCacheSizes = edgeCacheRows.Select(r => Sizes(r).FirstOrDefault()).Where(s => s is not null).Select(s => s!).ToList();

        Choose(view, "AppsSectionSpace");
        AssertOnlyShown(view, "space");
        var table = AppSession.Find(view, "SpaceTable", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Rows(view, "SpaceTable", "SpaceRow").Count > 0, ReadTimeout),
            "Space by app lists no row on a machine with installed apps: " + Ui.Dump(table));
        AssertNothingToTickOrClear(view, "space");

        // The plan's columns, left to right.
        AssertHeadersInOrder(table, Text.ColumnName, "Publisher", "Install folder", "Caches", "Total");

        // Largest total first: the last size text of a row is its Total.
        var rows = Rows(view, "SpaceTable", "SpaceRow");
        var totals = rows.Select(r => (Name: NameOf(r), Sizes: Sizes(r))).ToList();
        Assert.IsTrue(totals.All(t => t.Sizes.Count > 0), "a Space by app row shows no size at all: " + string.Join(" | ", totals.Where(t => t.Sizes.Count == 0).Select(t => t.Name)));
        var last = totals.Select(t => Bytes(t.Sizes[^1])).ToList();
        for (var i = 1; i < last.Count; i++)
        {
            Assert.IsTrue(last[i] <= last[i - 1] * 1.02, $"rows are not largest total first: \"{totals[i - 1].Name}\" ({totals[i - 1].Sizes[^1]}) comes before \"{totals[i].Name}\" ({totals[i].Sizes[^1]})");
        }

        // "estimated" appears only in a row's install cell: nothing else in the row may say it other than as the install cell's word.
        foreach (var t in totals.Where(t => t.Name.Contains("estimated", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.IsTrue(t.Sizes.Count >= 2, "a row marked estimated shows fewer than two sizes (install and total): " + t.Name);
        }

        // Microsoft Edge: attached to its Guide row by the exact uninstall key.
        if (!EdgeIsInstalled())
        {
            Assert.Inconclusive("Microsoft Edge has no uninstall key \"Microsoft Edge\" on this machine: the Guide row attachment cannot be checked here");
        }

        if (edgeCacheSizes.Count == 0)
        {
            Assert.Inconclusive("Edge is installed but has no Guide row (AppsRow) in the Caches table on this machine, so there is no Caches value to look for");
        }

        AutomationElement? edge = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (edge = Rows(view, "SpaceTable", "SpaceRow").FirstOrDefault(r => NameOf(r).StartsWith("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
            && !NameOf(r).Contains("WebView2", StringComparison.OrdinalIgnoreCase) && !NameOf(r).StartsWith("Microsoft Edge Update", StringComparison.OrdinalIgnoreCase))) is not null, Ui.ElementTimeout),
            "Microsoft Edge is installed and has a cache Guide row but is not in Space by app: " + Ui.Dump(table));
        var edgeSizes = Sizes(edge!);
        Assert.IsTrue(edgeSizes.Count >= 2, "Edge's row shows no Caches value besides its Total: " + NameOf(edge!));
        if (edgeCacheSizes.Count == 1)
        {
            Assert.IsTrue(edgeSizes.Contains(edgeCacheSizes[0]), $"Edge's Caches cell does not show the Guide row's {edgeCacheSizes[0]}: " + NameOf(edge!));
        }

        Assert.IsTrue(Bytes(edgeSizes[^1]) >= Bytes(edgeCacheSizes.Select(s => (s, Bytes(s))).OrderBy(p => p.Item2).Last().s) * 0.98, "Edge's Total is smaller than its Caches: " + NameOf(edge!));
        AssertNoCleanAndNothingTicked(view, fake, "after browsing Space by app");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProSpaceByAppListsAppsLargestFirstAndEdgeShowsItsCaches()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("space-pro");
        using var app = StartPro(fake);
        SpaceBody(app, fake);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void FreeSpaceByAppListsAppsLargestFirstAndEdgeShowsItsCaches()
    {
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("space-free");
        using var app = StartFree(fake);
        SpaceBody(app, fake);
    }

    // ---- Virtual disks --------------------------------------------------------------------------------------------------

    private static void DisksEmptyBody(AppSession app, FakeUv fake)
    {
        var view = OpenApps(app);
        Choose(view, "AppsSectionDisks");
        AssertOnlyShown(view, "disks");
        var table = AppSession.Find(view, "DisksTable", Ui.ElementTimeout);
        AssertNothingToTickOrClear(view, "disks");
        // Either the read has ended with no row (a message) or there are rows (a machine with WSL, Docker Desktop or VirtualBox).
        Assert.IsTrue(AppSession.WaitUntil(() => Rows(view, "DisksTable", "DiskRow").Count > 0 || AppSession.TryFind(view, "DisksMessage") is not null, ReadTimeout),
            "Virtual disks shows neither a row nor a DisksMessage: " + Ui.Dump(table));
        if (Rows(view, "DisksTable", "DiskRow").Count > 0)
        {
            Assert.Inconclusive("this machine has virtual disks (" + Rows(view, "DisksTable", "DiskRow").Count + " row(s)): the empty message cannot be checked here; the row test covers them");
        }

        var message = AppSession.Find(view, "DisksMessage", Ui.ElementTimeout);
        Assert.IsTrue(NameOf(message).Trim().Length > 0, "DisksMessage is empty while the table is empty");
        AssertNoCleanAndNothingTicked(view, fake, "after browsing Virtual disks");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProVirtualDisksWithNoDiskShowsAMessage()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("disks-pro");
        using var app = StartPro(fake);
        DisksEmptyBody(app, fake);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void FreeVirtualDisksWithNoDiskShowsAMessage()
    {
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("disks-free");
        using var app = StartFree(fake);
        DisksEmptyBody(app, fake);
    }

    // A machine with a WSL distribution, Docker Desktop or VirtualBox gives rows: the real profile is never changed to make one, so on
    // the VM (none of them installed) this is Inconclusive and the Core tests cover the rows.
    private static void DisksRowsBody(AppSession app)
    {
        var view = OpenApps(app);
        Choose(view, "AppsSectionDisks");
        AssertOnlyShown(view, "disks");
        var table = AppSession.Find(view, "DisksTable", Ui.ElementTimeout);
        AppSession.WaitUntil(() => Rows(view, "DisksTable", "DiskRow").Count > 0 || AppSession.TryFind(view, "DisksMessage") is not null, ReadTimeout);
        var rows = Rows(view, "DisksTable", "DiskRow");
        if (rows.Count == 0)
        {
            Assert.Inconclusive("no virtual disk (WSL, Docker Desktop, VirtualBox) on this machine, and the real profile is not changed to make one; rows are covered by the Core tests");
        }

        AssertHeadersInOrder(table, Text.ColumnName, "Kind", Text.ColumnSize, "Advice");
        foreach (var row in rows)
        {
            Assert.IsTrue(SizeText.IsMatch(NameOf(row)), "a disk row shows no size: " + NameOf(row));
            var copy = Table.InRow(row, "DiskCopyAdvice");
            if (copy is not null)
            {
                Assert.IsTrue(Enabled(copy), "the Copy button of a disk row is disabled: " + NameOf(row));
            }
        }

        AssertNothingToTickOrClear(view, "disks");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProVirtualDiskRowsHaveTheirColumnsAndAnEnabledCopyButton()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("diskrows-pro");
        using var app = StartPro(fake);
        DisksRowsBody(app);
    }

    [TestMethod]
    [TestCategory("Console")]
    public void FreeVirtualDiskRowsHaveTheirColumnsAndAnEnabledCopyButton()
    {
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("diskrows-free");
        using var app = StartFree(fake);
        DisksRowsBody(app);
    }

    // The Copy button puts the row's advice on the clipboard and the status line says so. The VM has no virtual disk, so a stand-in
    // Docker Desktop disk (a small plain file) is made in %LOCALAPPDATA%\Docker, and only when that folder is not there yet. Afterwards
    // only the stand-in file and the folders the test made are removed, each folder only when empty, so anything Docker Desktop wrote
    // meanwhile stays. When the folder exists the real profile is left alone and the test is Inconclusive.
    private static void CopyAdviceBody(Func<FakeUv, AppSession> start, string fakeName)
    {
        var docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker");
        if (Directory.Exists(docker))
        {
            Assert.Inconclusive(docker + " exists on this machine: the real profile is not changed to make a stand-in virtual disk");
        }

        var disk = Path.Combine(docker, "wsl", "disk", "docker_data.vhdx");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            File.WriteAllBytes(disk, new byte[64 * 1024]);
            using var log = new CommandLogBackup();
            using var fake = new FakeUv(fakeName);
            using var app = start(fake);
            var view = OpenApps(app);
            Choose(view, "AppsSectionDisks");
            AssertOnlyShown(view, "disks");
            // A real disk (a WSL distribution on the WSL snapshot layer) may sort above the small stand-in, so its row is picked by kind.
            AutomationElement? row = null;
            Assert.IsTrue(AppSession.WaitUntil(() => (row = Rows(view, "DisksTable", "DiskRow").FirstOrDefault(r => NameOf(r).Contains(Text.KindDockerDesktop, StringComparison.Ordinal))) is not null, ReadTimeout),
                "the stand-in Docker Desktop disk gives no row: " + Ui.Dump(AppSession.Find(view, "DisksTable", Ui.ElementTimeout)));
            var copy = Table.InRow(row!, "DiskCopyAdvice");
            Assert.IsNotNull(copy, "the disk row has no Copy button: " + NameOf(row!));

            SetClipboard("sentinel before Copy");
            ((InvokePattern)copy.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => NameOf(status) == Text.AdviceCopied, Ui.ElementTimeout),
                $"the status line is \"{NameOf(status)}\", expected \"{Text.AdviceCopied}\"");
            Assert.IsTrue(AppSession.WaitUntil(() => GetClipboard() == Text.AdviceDockerDesktop, Ui.ElementTimeout),
                $"the clipboard holds \"{GetClipboard()}\", expected the row's advice \"{Text.AdviceDockerDesktop}\"");
            Assert.IsTrue(NameOf(row!).Contains(Text.AdviceDockerDesktop, StringComparison.Ordinal), "the row does not show the advice that was copied: " + NameOf(row!));
        }
        finally
        {
            // Best effort: a failing cleanup must not hide the test's own failure.
            try
            {
                File.Delete(disk);
                foreach (var folder in new[] { Path.GetDirectoryName(disk)!, Path.Combine(docker, "wsl"), docker })
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // The clipboard is an STA object: each access runs on its own STA thread and retries while another process holds it.
    private static void SetClipboard(string text) => OnSta(() =>
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return string.Empty;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    });

    private static string GetClipboard() => OnSta(() =>
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    });

    private static string OnSta(Func<string> action)
    {
        var result = string.Empty;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.IsNull(error, "the clipboard could not be used: " + error);
        return result;
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProCopyButtonPutsTheRowsAdviceOnTheClipboardAndSaysSo()
    {
        ProApp.RequireDebugApp();
        CopyAdviceBody(StartPro, "copy-pro");
    }

    [TestMethod]
    [TestCategory("Console")]
    public void FreeCopyButtonPutsTheRowsAdviceOnTheClipboardAndSaysSo() => CopyAdviceBody(StartFree, "copy-free");

    // ---- Free sees both tables, and browsing runs nothing ---------------------------------------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void FreeShowsBothNewTablesWithNothingGreyedForPro()
    {
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("free-both");
        using var app = StartFree(fake);
        var view = OpenApps(app);
        // In the Caches section Free still has Clear and Suggest greyed with the Pro label (phase B, decision 15) ...
        Assert.IsTrue(AppSession.WaitUntil(() => CachesShown(view), ReadTimeout), "no Caches row in Free: " + Ui.Dump(view));
        Assert.IsFalse(Enabled(AppSession.Find(view, "ClearApps", Ui.ElementTimeout)), "Clear is enabled in Free");

        // ... and in the two new sections nothing is greyed: they are plain read-only tables.
        Choose(view, "AppsSectionSpace");
        AssertOnlyShown(view, "space");
        Assert.IsTrue(AppSession.WaitUntil(() => Rows(view, "SpaceTable", "SpaceRow").Count > 0, ReadTimeout), "Free's Space by app lists no row: " + Ui.Dump(view));
        AssertNothingToTickOrClear(view, "space");
        Assert.IsTrue(Rows(view, "SpaceTable", "SpaceRow").All(Enabled), "a Space by app row is disabled in Free");

        Choose(view, "AppsSectionDisks");
        AssertOnlyShown(view, "disks");
        AssertNothingToTickOrClear(view, "disks");
        Assert.IsTrue(AppSession.WaitUntil(() => Rows(view, "DisksTable", "DiskRow").Count > 0 || AppSession.TryFind(view, "DisksMessage") is not null, ReadTimeout),
            "Free's Virtual disks shows neither a row nor a message: " + Ui.Dump(view));
        foreach (var copy in Table.Controls(view, "DiskCopyAdvice"))
        {
            Assert.IsTrue(Enabled(copy), "a Copy button is disabled in Free");
        }

        AssertNoCleanAndNothingTicked(view, fake, "Free browsing the new tables");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ProBrowsingTheNewTablesNeverTicksClearsOrStartsAnything()
    {
        ProApp.RequireDebugApp();
        using var log = new CommandLogBackup();
        using var fake = new FakeUv("browse-pro");
        using var app = StartPro(fake);
        var view = OpenApps(app);
        Assert.IsTrue(AppSession.WaitUntil(() => CachesShown(view), ReadTimeout), "no Caches row: " + Ui.Dump(view));
        Choose(view, "AppsSectionSpace");
        AssertOnlyShown(view, "space");
        AppSession.WaitUntil(() => Rows(view, "SpaceTable", "SpaceRow").Count > 0, ReadTimeout);
        AssertNothingToTickOrClear(view, "space");
        Choose(view, "AppsSectionDisks");
        AssertOnlyShown(view, "disks");
        AssertNothingToTickOrClear(view, "disks");
        Choose(view, "AppsSectionCaches");
        AssertOnlyShown(view, "caches");
        Assert.IsTrue(Table.Controls(view, "AppsItem").All(b => Table.State(b) == ToggleState.Off), "a Caches row is ticked after browsing: " + Table.Dump(view, "AppsItem"));
        var calls = fake.Calls();
        Assert.IsTrue(calls.Count > 0 && calls.All(c => c == "cache dir"), "the fake uv was asked for more than the location while browsing: [" + string.Join(" | ", calls) + "]");
        AssertNoCleanAndNothingTicked(view, fake, "Pro browsing the new tables");
    }
}
