using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan "UI contract for group 4", the Temporary files view (intent outcome 4; docs/spec/03 "Temporary files" and
/// 07 Windows notes of 2026-10-18): the sidebar's <c>TemporaryFiles</c> item (ASP-14) opens <c>TempView</c>, and the
/// Overview item, Esc and Alt+Left go back (ASP-9, ASP-14); <c>AgeChoice</c> (<c>Text.AgeWeek</c> by default, <c>Text.AgeMonth</c>,
/// <c>Text.AgeQuarter</c>); <c>TempList</c> of <c>TempItem</c> check boxes, none ticked, each in a table row (ASP-10) whose
/// automation name holds the name, the size and the short date of the last change, display-only rows disabled with their
/// hint in the Note; <c>TempTotal</c> holding
/// <c>Text.TempEstimate(size)</c>; <c>MoveTemp</c> through the same <c>ConfirmMove</c> flow. The fixtures are the test's own
/// children of this user's real <c>%LOCALAPPDATA%\Temp</c>, named <c>LeanUiTemp-*</c>: an old file (every time set ten days
/// back), a fresh file, and a folder whose own times are ten days back but which holds a fresh file (its newest write is
/// now). They are removed in <c>finally</c>; one moved to the Recycle Bin and not put back stays there (the bin is never
/// emptied). Nothing else in Temp is ticked or moved.</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class TemporaryFilesTests
{
    private const long KB = ByteUnits.KB;
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(90);
    private static readonly string[] Ages = [Text.AgeWeek, Text.AgeMonth, Text.AgeQuarter];

    /// <summary>The test's own items in this user's <c>%LOCALAPPDATA%\Temp</c>.</summary>
    private sealed class TempFixture : IDisposable
    {
        public TempFixture()
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            OldName = "LeanUiTemp-old-" + id + ".bin";
            NewName = "LeanUiTemp-new-" + id + ".bin";
            MixedName = "LeanUiTemp-mixed-" + id;
            Old = Path.Combine(temp, OldName);
            New = Path.Combine(temp, NewName);
            Mixed = Path.Combine(temp, MixedName);
            OldUtc = DateTime.UtcNow.AddDays(-10);

            Write(Old, 100 * KB, 61);
            File.SetCreationTimeUtc(Old, OldUtc);
            File.SetLastWriteTimeUtc(Old, OldUtc);
            File.SetLastAccessTimeUtc(Old, OldUtc);
            Write(New, 100 * KB, 62);
            Directory.CreateDirectory(Mixed);
            Write(Path.Combine(Mixed, "fresh.bin"), 100 * KB, 63);
            Directory.SetCreationTimeUtc(Mixed, OldUtc);
            Directory.SetLastWriteTimeUtc(Mixed, OldUtc);
            Directory.SetLastAccessTimeUtc(Mixed, OldUtc);
        }

        public string OldName { get; }

        public string NewName { get; }

        public string MixedName { get; }

        public string Old { get; }

        public string New { get; }

        public string Mixed { get; }

        public DateTime OldUtc { get; }

        private static void Write(string path, long length, int seed)
        {
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((i * 31 + seed * 131 + 7) & 0xFF);
            }

            File.WriteAllBytes(path, bytes);
        }

        public void Dispose()
        {
            foreach (var file in new[] { Old, New })
            {
                for (var attempt = 0; attempt < 5 && File.Exists(file); attempt++)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(200);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(200);
                    }
                }
            }

            for (var attempt = 0; attempt < 5 && Directory.Exists(Mixed); attempt++)
            {
                try
                {
                    Directory.Delete(Mixed, recursive: true);
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static AutomationElement OpenTemp(AppSession app) => Ui.OpenView(app, "TemporaryFiles", "TempView");

    private static List<AutomationElement> Items(AutomationElement view)
    {
        try
        {
            return AppSession.FindAll(view, "TempItem");
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    private static string ItemNames(AutomationElement view) => Table.Dump(view, "TempItem");

    /// <summary>The TempItem box whose row (ASP-10: its automation name joins its cells) holds <paramref name="name"/>.</summary>
    private static AutomationElement? ItemOf(AutomationElement view, string name) => Table.ControlWith(view, "TempItem", name);

    private static AutomationElement WaitForItem(AutomationElement view, string name)
    {
        AutomationElement? item = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (item = ItemOf(view, name)) is not null, FindTimeout), $"no TempItem for {name}: {ItemNames(view)}");
        return item!;
    }

    private static ToggleState State(AutomationElement item) => ((TogglePattern)item.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    /// <summary>The options of <paramref name="choice"/> (a combo box, a list or radio buttons): the selectable items below
    /// it or, for an expanded combo box, in its popup, named by one of the three ages.</summary>
    private static List<AutomationElement> Options(AppSession app, AutomationElement choice)
    {
        var condition = new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true);
        var inside = choice.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Where(e => Ages.Contains(e.Current.Name)).ToList();
        if (inside.Count > 0)
        {
            return inside;
        }

        var all = new List<AutomationElement>();
        foreach (var top in Ui.TopWindows(app))
        {
            all.AddRange(top.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Where(e => Ages.Contains(e.Current.Name)));
        }

        return all;
    }

    private static string? SelectedAge(AppSession app, AutomationElement choice)
    {
        if (choice.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection) && ((SelectionPattern)selection).Current.GetSelection() is { Length: > 0 } selected)
        {
            return selected[0].Current.Name;
        }

        if (choice.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && Ages.Contains(((ValuePattern)value).Current.Value))
        {
            return ((ValuePattern)value).Current.Value;
        }

        return Options(app, choice).FirstOrDefault(o => ((SelectionItemPattern)o.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected)?.Current.Name;
    }

    private static void ChooseAge(AppSession app, AutomationElement choice, string age)
    {
        var expandable = choice.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand);
        if (expandable)
        {
            ((ExpandCollapsePattern)expand).Expand();
            Thread.Sleep(300);
        }

        AutomationElement? option = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (option = Options(app, choice).FirstOrDefault(o => o.Current.Name == age)) is not null, Ui.ElementTimeout),
            $"AgeChoice has no option \"{age}\"; options: [{string.Join(" | ", Options(app, choice).Select(o => o.Current.Name))}]");
        ((SelectionItemPattern)option!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expandable && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
        {
            ((ExpandCollapsePattern)expand).Collapse();
        }

        Assert.IsTrue(AppSession.WaitUntil(() => SelectedAge(app, choice) == age, Ui.ElementTimeout), $"AgeChoice shows \"{SelectedAge(app, choice)}\" after choosing \"{age}\"");
    }

    /// <summary>The item's row (ASP-10: Name, Size, Last changed, Note) holds the name, ByteUnits.Format of the size and
    /// the short date of the last change, and no hint.</summary>
    private static void AssertItemName(AutomationElement item, string name, long bytes, DateTime changedUtc)
    {
        var text = Table.RowName(item);
        foreach (var cell in new[] { name, ByteUnits.Format(bytes), Table.ShortDate(changedUtc) })
        {
            Assert.IsTrue(text.Contains(cell, StringComparison.OrdinalIgnoreCase), $"the TempItem row \"{text}\" does not hold \"{cell}\"");
        }

        Assert.IsNull(Table.HintIn(text), $"the movable TempItem row \"{text}\" carries a hint");
    }

    /// <summary>TempTotal is <c>Text.TempEstimate(size)</c> for a size in ByteUnits' form ("123 KB", "1.50 GB").</summary>
    private static void AssertTotalShape(AutomationElement view)
    {
        var total = AppSession.Find(view, "TempTotal", Ui.ElementTimeout);
        var marker = "\u0001";
        var shape = Text.TempEstimate(marker);
        var before = shape[..shape.IndexOf(marker, StringComparison.Ordinal)];
        var after = shape[(shape.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        string text = string.Empty;
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                text = total.Current.Name;
                return text.StartsWith(before, StringComparison.Ordinal) && text.EndsWith(after, StringComparison.Ordinal) && text.Length > before.Length + after.Length
                       && SizeText().IsMatch(text[before.Length..^after.Length]);
            }, Ui.ElementTimeout),
            $"TempTotal \"{text}\" is not \"{Text.TempEstimate("<size>")}\"");
    }

    [GeneratedRegex(@"^\d[\d.,\u00a0 ]*\s?(bytes|KB|MB|GB|TB)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SizeText();

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static bool Shown(AppSession app)
    {
        try
        {
            return app.FindInAnyWindow("TempView") is { } view && !view.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    // ---- opening and going back ---------------------------------------------------------------------------------------

    // Contract, ASP-9 and ASP-14: the sidebar's TemporaryFiles item (Text.TemporaryFiles) opens TempView by keyboard
    // (Space); TempView has no BackToMap button. The Overview item, then Esc, then Alt+Left each return to Overview with the
    // keyboard focus on the Overview item.
    [TestMethod]
    [TestCategory("Console")]
    public void TemporaryFilesOpensTempViewAndOverviewEscAndAltLeftGoBack()
    {
        using var app = AppSession.Start();
        var opener = Ui.SidebarItem(app, "TemporaryFiles");
        Assert.AreEqual(Text.TemporaryFiles, opener.Current.Name, "TemporaryFiles label");

        Ui.Focus(opener);
        Keys.Press(Keys.Space);
        var view = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(app), Ui.ElementTimeout), "TempView did not open by Space on TemporaryFiles");
        Assert.IsNull(AppSession.TryFind(view, "BackToMap"), "TempView still has a BackToMap button (ASP-14 removes it)");

        foreach (var way in new[] { "Overview", "Esc", "Alt+Left" })
        {
            if (way == "Overview")
            {
                Ui.Click(Ui.SidebarItem(app, "Overview"));
            }
            else
            {
                OpenTemp(app);
                Assert.IsTrue(AppSession.WaitUntil(() => Shown(app), Ui.ElementTimeout), "TempView did not open again");
                Ui.Focus(Ui.SidebarItem(app, "TemporaryFiles"));
                if (way == "Esc")
                {
                    Keys.Press(Keys.Escape);
                }
                else
                {
                    Keys.PressWithAlt(Keys.Left);
                }
            }

            Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app), Ui.ElementTimeout), $"TempView is still shown after {way}");
            var overview = Ui.SidebarItem(app, "Overview");
            Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(overview), Ui.ElementTimeout),
                $"after {way} the focus is on {(Focused() is { } f ? Ui.Describe(f) : "nothing")}, not the Overview item");
        }
    }

    // ---- the list ----------------------------------------------------------------------------------------------------

    // Contract and docs/spec/03: AgeChoice offers Text.AgeWeek, Text.AgeMonth and Text.AgeQuarter, with the week selected.
    // With it, the old fixture (ten days) is a TempItem, enabled and unticked, whose row holds its name, ByteUnits.Format
    // 100 KB and the short date of its last change (ASP-10); the fresh file is not listed, nor the folder whose own times are old but which holds
    // a fresh file (its age is its newest file's). TempTotal holds Text.TempEstimate(<a size>).
    [TestMethod]
    [TestCategory("Console")]
    public void TheWeekIsTheDefaultAndOnlyTheOldItemIsListedUnticked()
    {
        using var temp = new TempFixture();
        using var app = AppSession.Start();
        var view = OpenTemp(app);
        var age = AppSession.Find(view, "AgeChoice", Ui.ElementTimeout);
        Assert.AreEqual(Text.AgeWeek, SelectedAge(app, age), "AgeChoice default");
        AppSession.Find(view, "TempList", Ui.ElementTimeout);

        var old = WaitForItem(view, temp.OldName);
        AssertItemName(old, temp.OldName, 100 * KB, temp.OldUtc);
        Assert.IsTrue(old.Current.IsEnabled, "the old TempItem is disabled: " + old.Current.HelpText);
        Assert.AreEqual(ToggleState.Off, State(old), "the old TempItem is ticked");
        Assert.IsNull(ItemOf(view, temp.NewName), "the fresh file is listed: " + ItemNames(view));
        Assert.IsNull(ItemOf(view, temp.MixedName), "a folder holding a fresh file is listed (its own time was used, not its newest file's): " + ItemNames(view));
        AssertTotalShape(view);

        var expand = age.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern);
        if (expand)
        {
            ((ExpandCollapsePattern)pattern).Expand();
            Thread.Sleep(300);
        }

        var names = Options(app, age).Select(o => o.Current.Name).ToList();
        if (expand)
        {
            ((ExpandCollapsePattern)pattern).Collapse();
        }

        CollectionAssert.AreEquivalent(Ages, names.Distinct().ToList(), "AgeChoice options: [" + string.Join(" | ", names) + "]");
    }

    // Contract: choosing Text.AgeMonth hides the ten-day-old item (and Text.AgeQuarter too); choosing Text.AgeWeek again
    // shows it again, still unticked.
    [TestMethod]
    [TestCategory("Console")]
    public void AMonthHidesATenDayOldItem()
    {
        using var temp = new TempFixture();
        using var app = AppSession.Start();
        var view = OpenTemp(app);
        var age = AppSession.Find(view, "AgeChoice", Ui.ElementTimeout);
        WaitForItem(view, temp.OldName);

        ChooseAge(app, age, Text.AgeMonth);
        Assert.IsTrue(AppSession.WaitUntil(() => ItemOf(view, temp.OldName) is null, FindTimeout), "with a month, the ten-day-old item is still listed: " + ItemNames(view));
        ChooseAge(app, age, Text.AgeQuarter);
        Assert.IsFalse(AppSession.WaitUntil(() => ItemOf(view, temp.OldName) is not null, Ui.NeverWatch), "with a quarter, the ten-day-old item is listed");
        ChooseAge(app, age, Text.AgeWeek);
        var old = WaitForItem(view, temp.OldName);
        Assert.AreEqual(ToggleState.Off, State(old), "the old TempItem is ticked after changing the age");
        Assert.IsNull(ItemOf(view, temp.NewName), "the fresh file is listed: " + ItemNames(view));
    }

    // Plan group 5 hand mutation (t2), checked here: no TempItem is ticked when the view opens, whatever is in Temp (the
    // fixtures make sure there is at least one movable row).
    [TestMethod]
    [TestCategory("Console")]
    public void NoTempItemIsTickedWhenTheViewOpens()
    {
        using var temp = new TempFixture();
        using var app = AppSession.Start();
        var view = OpenTemp(app);
        WaitForItem(view, temp.OldName);
        var ticked = Items(view).Where(i => State(i) != ToggleState.Off).Select(Table.RowName).ToList();
        Assert.AreEqual(0, ticked.Count, "ticked when the view opens: " + string.Join(" | ", ticked));
    }

    // Contract and docs/spec/03: C:\Windows\Temp is one display-only row: a disabled TempItem whose row (ASP-10: the Note
    // cell, in the row's automation name) or its own Name or HelpText holds Text.HintSystemWide. It is never ticked.
    [TestMethod]
    [TestCategory("Console")]
    public void WindowsTempIsADisplayOnlyRowWithTheSystemWideHint()
    {
        using var temp = new TempFixture();
        using var app = AppSession.Start();
        var view = OpenTemp(app);
        WaitForItem(view, temp.OldName);
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        bool IsSystemRow(AutomationElement item) =>
            ((item.Current.HelpText ?? string.Empty) + "\n" + item.Current.Name + "\n" + Table.RowName(item)).Contains(Text.HintSystemWide, StringComparison.Ordinal);
        var rows = Items(view).Where(IsSystemRow).ToList();
        Assert.AreEqual(1, rows.Count, $"expected one TempItem with \"{Text.HintSystemWide}\" ({windowsTemp}): {ItemNames(view)}");
        Assert.IsFalse(rows[0].Current.IsEnabled, "the Windows Temp row is enabled: " + Table.RowName(rows[0]));
        Assert.AreEqual(ToggleState.Off, State(rows[0]), "the Windows Temp row is ticked");
    }

    // ---- the move ----------------------------------------------------------------------------------------------------

    // Contract: with the old row ticked, MoveTemp runs the ConfirmMove flow: the confirmation lists one ConfirmItem
    // Text.ConfirmItem(<path>, <size>); ConfirmMoveButton moves it to the Recycle Bin (the test's own $I reading: same file
    // id), the report says Text.MovedToRecycleBin(<100 KB>) and Text.SpaceReturnsWhenEmptied, and PutBackSession returns
    // it byte for byte. The fresh fixture is untouched.
    [TestMethod]
    [TestCategory("Console")]
    public void MovingTheOldItemGoesToTheRecycleBinAndPutBackReturnsIt()
    {
        using var temp = new TempFixture();
        var identity = MoveFixture.Identity(temp.Old);
        var hash = MoveFixture.Hash(temp.Old);
        var freshIdentity = MoveFixture.Identity(temp.New);

        using var app = AppSession.Start();
        var view = OpenTemp(app);
        var old = WaitForItem(view, temp.OldName);
        ((TogglePattern)old.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => ItemOf(view, temp.OldName) is { } i && State(i) == ToggleState.On, TimeSpan.FromSeconds(3)), "the old TempItem did not tick");
        var ticked = Items(view).Where(i => State(i) == ToggleState.On).Select(Table.RowName).ToList();
        Assert.AreEqual(1, ticked.Count, "more than the test's row is ticked: " + string.Join(" | ", ticked));

        var move = AppSession.Find(view, "MoveTemp", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "MoveTemp is disabled with a row ticked");
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        List<AutomationElement> items = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count == 1, Ui.ElementTimeout), "the confirmation does not list one item: " + Ui.Dump(dialog));
        var expected = Text.ConfirmItem(temp.Old, ByteUnits.Format(100 * KB));
        Assert.IsTrue(items[0].Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase), $"ConfirmItem \"{items[0].Current.Name}\", expected \"{expected}\"");

        var report = Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
        Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "the item was skipped or lost: " + Ui.Dump(report));
        var moved = Text.MovedToRecycleBin(ByteUnits.Format(100 * KB));
        Assert.IsTrue(Ui.AnyNameContains(report, moved), $"the report lacks \"{moved}\": {Ui.Dump(report)}");
        Assert.IsTrue(Ui.AnyNameContains(report, Text.SpaceReturnsWhenEmptied), $"the report lacks \"{Text.SpaceReturnsWhenEmptied}\": {Ui.Dump(report)}");

        RecycleBin.Entry? entry = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (entry = RecycleBin.Holding(temp.Old)) is not null, TimeSpan.FromSeconds(10)), temp.Old + " is not in the Recycle Bin");
        Assert.IsFalse(File.Exists(temp.Old), temp.Old + " is still in its place");
        Assert.AreEqual(identity, MoveFixture.Identity(entry!.ItemPath), "the item in the Recycle Bin is not the original (file id)");
        Assert.AreEqual(freshIdentity, MoveFixture.Identity(temp.New), "the fresh fixture changed");
        Assert.IsNull(RecycleBin.Holding(temp.New), "the fresh fixture is in the Recycle Bin");

        Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(temp.Old), Ui.MoveTimeout), temp.Old + " was not put back");
        Assert.AreEqual(identity, MoveFixture.Identity(temp.Old), "put back as a different file");
        Assert.AreEqual(hash, MoveFixture.Hash(temp.Old), "the bytes put back differ");
        Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(temp.Old) is null, TimeSpan.FromSeconds(5)), "put back and still in the Recycle Bin");
    }
}
