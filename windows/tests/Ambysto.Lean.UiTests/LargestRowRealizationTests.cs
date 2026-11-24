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

// Acceptance-first: a written acceptance contract. Product/builder implementation was not inspected.
[TestClass]
[DoNotParallelize]
public sealed class LargestRowRealizationTests
{
    private const int LogicalCount = 500;
    private const int MaxTextPeers = 200;
    private const int MaxTreePeers = 1800;
    private sealed record Expected(string Path, string Name, long Bytes, string Type);
    private sealed record Logical(string Path, string AccessibleName);
    private static readonly string[] ActionIds = ["RevealRow", "MoveRow"];
    private static readonly Regex OwnName = new(@"row-[0-9]{3}\.(?:mp4|pdf|bin)", RegexOptions.CultureInvariant);

    private static AutomationElement Scope(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "LargestFiles", Ui.ElementTimeout);

    private static AutomationElement List(AppSession app)
    {
        var list = Scope(app).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
        Assert.IsNotNull(list, "LargestFiles exposes its standard ListView to UI Automation");
        return list;
    }

    private static ItemContainerPattern Container(AppSession app)
    {
        var list = List(app);
        Assert.IsTrue(list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern), "logical offscreen rows need the standard ListView ItemContainer pattern");
        return (ItemContainerPattern)pattern;
    }

    // Read logical Name only: do not Realize/ScrollIntoView or enumerate per-row rich descendants here.
    private static List<Logical> LogicalRows(AppSession app, IReadOnlyDictionary<string, Expected> expected)
    {
        var container = Container(app);
        var rows = new List<Logical>();
        AutomationElement? previous = null;
        for (var i = 0; i <= LogicalCount; i++)
        {
            var item = container.FindItemByProperty(previous, null!, null!);
            if (item is null) return rows;
            var name = item.Current.Name;
            var match = OwnName.Match(name);
            Assert.IsTrue(match.Success && expected.ContainsKey(match.Value), "unexpected logical row: " + name);
            var info = expected[match.Value];
            Assert.IsTrue(Table.Holds(name, info.Path), "logical row retains complete path: " + name);
            StringAssert.Contains(name, ByteUnits.Format(info.Bytes), "allocated bytes remain available without rich realization");
            StringAssert.Contains(name, info.Type);
            rows.Add(new Logical(info.Path, name));
            previous = item;
        }
        Assert.Fail("LargestFiles exceeds its 500 logical row limit");
        return rows;
    }

    private static void AssertBounded(AppSession app, string phase)
    {
        var scope = Scope(app);
        int textCount = 0, treeCount = 0;
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            try
            {
                textCount = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Count;
                treeCount = scope.FindAll(TreeScope.Descendants, Condition.TrueCondition).Count;
                return textCount <= MaxTextPeers && treeCount <= MaxTreePeers;
            }
            catch (ElementNotAvailableException) { return false; }
        }, Ui.ElementTimeout), $"{phase}: LargestFiles rich UIA subtree must be bounded; text={textCount}/{MaxTextPeers}, descendants={treeCount}/{MaxTreePeers}");
        Console.WriteLine($"{phase}: text peers={textCount}; total descendants={treeCount}; UIA evidence only, no native allocation claim");
    }

    private static AutomationElement Reveal(AppSession app, Logical row)
    {
        var item = Container(app).FindItemByProperty(null, AutomationElement.NameProperty, row.AccessibleName);
        Assert.IsNotNull(item, "logical item remains reachable after sorting: " + row.Path);
        if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) ((VirtualizedItemPattern)virtualized).Realize();
        Assert.IsTrue(item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll), "offscreen row exposes ScrollItem: " + row.Path);
        ((ScrollItemPattern)scroll).ScrollIntoView();
        Assert.IsTrue(AppSession.WaitUntil(() => !item.Current.IsOffscreen && !item.Current.BoundingRectangle.IsEmpty
            && Box(item) is not null, Ui.ElementTimeout), "requested row must become visible with its checkbox: " + row.Path);
        Assert.IsTrue(Table.Holds(item.Current.Name, row.Path));
        return item;
    }

    private static AutomationElement? Box(AutomationElement row) => row.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));

    private static void AssertRichRow(AutomationElement row, Expected expected, ToggleState state)
    {
        Assert.IsTrue(Table.Holds(row.Current.Name, expected.Path));
        var box = Box(row);
        Assert.IsNotNull(box);
        Assert.AreEqual(state, Table.State(box), "persistent model tick: " + expected.Path);
        Assert.IsTrue(box.Current.IsEnabled, "test-owned ordinary file remains movable");
        var cells = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToArray();
        Assert.IsTrue(cells.Contains(expected.Name, StringComparer.Ordinal), "visible Name cell belongs to the requested row");
        Assert.IsTrue(cells.Contains(ByteUnits.Format(expected.Bytes), StringComparer.Ordinal), "visible Size cell belongs to the requested row");
        Assert.IsTrue(cells.Contains(expected.Type, StringComparer.Ordinal), "visible Type cell belongs to the requested row");
        Assert.IsTrue(Table.Texts(row).Any(t => t.Help.Contains(Path.GetDirectoryName(expected.Path)!, StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains(Path.GetDirectoryName(expected.Path)!, StringComparison.OrdinalIgnoreCase)), "full location remains accessible");
    }

    private static void VisitAll(AppSession app, IReadOnlyList<Logical> logical, IReadOnlyDictionary<string, Expected> expected, ToggleState state)
    {
        // This traversal validates every row's actual loaded cells/state; it is not used to manufacture an initial count.
        var byPath = expected.Values.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var row in logical) AssertRichRow(Reveal(app, row), byPath[row.Path], state);
        AssertBounded(app, "after scrolling all 500 rows");
    }

    private static void NameSortAndReverse(AppSession app, IReadOnlyDictionary<string, Expected> expected)
    {
        var header = Table.Header(Scope(app), Text.ColumnName);
        Table.Press(header);
        List<Logical> first = [];
        Assert.IsTrue(AppSession.WaitUntil(() =>
        {
            first = LogicalRows(app, expected);
            if (first.Count != LogicalCount) return false;
            var order = first.Select(r => Path.GetFileName(r.Path)).ToArray();
            var sorted = order.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return order.SequenceEqual(sorted) || order.SequenceEqual(sorted.Reverse());
        }, Ui.ElementTimeout), "Name sort applies to all 500 logical rows, including offscreen rows");
        Table.Press(header);
        var reversed = first.Select(r => r.Path).Reverse().ToArray();
        Assert.IsTrue(AppSession.WaitUntil(() => LogicalRows(app, expected).Select(r => r.Path).SequenceEqual(reversed), Ui.ElementTimeout), "second Name press reverses all unique names");
    }

    private static void SetAll(AppSession app, ToggleState state)
    {
        var all = AppSession.Find(Scope(app), "SelectAll", Ui.ElementTimeout);
        var toggle = (TogglePattern)all.GetCurrentPattern(TogglePattern.Pattern);
        for (var i = 0; i < 3 && toggle.Current.ToggleState != state; i++) toggle.Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => toggle.Current.ToggleState == state, Ui.ElementTimeout));
    }

    [TestMethod]
    [TestCategory("Console")]
    public void LargestRetains500LogicalRowsWhileRichContentRetiresAndOffscreenSelectionAndSortSurvive()
    {
        using var fixture = new MoveFixture("largest-realization");
        var expected = new Dictionary<string, Expected>(StringComparer.Ordinal);
        for (var i = 0; i < 520; i++)
        {
            var type = i % 3;
            var extension = type == 0 ? ".mp4" : type == 1 ? ".pdf" : ".bin";
            var name = "row-" + i.ToString("D3", CultureInfo.InvariantCulture) + extension;
            var bytes = i >= LogicalCount ? 4096 : i == 0 ? 65536 : i == LogicalCount - 1 ? 12288 : 8192;
            var path = fixture.File(name, bytes, i + 1);
            if (i < LogicalCount) expected.Add(name, new Expected(path, name, MoveFixture.AllocatedBytes(path), type == 0 ? Text.TypeVideo : type == 1 ? Text.TypeDocument : Text.TypeOther));
        }
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var logical = new List<Logical>();
        Assert.IsTrue(AppSession.WaitUntil(() => (logical = LogicalRows(app, expected)).Count == LogicalCount, Ui.ElementTimeout));
        AssertBounded(app, "initial viewport before any row scrolling");
        CollectionAssert.AreEquivalent(expected.Values.Select(e => e.Path).ToArray(), logical.Select(r => r.Path).ToArray(), "top 500 exact paths; all 20 smaller rows excluded");
        Assert.IsFalse(AppSession.Find(app.Window(Ui.WindowTimeout), "MoveLargest", Ui.ElementTimeout).Current.IsEnabled, "nothing is preselected");
        var bytesByPath = expected.Values.ToDictionary(e => e.Path, e => e.Bytes, StringComparer.Ordinal);
        for (var i = 1; i < logical.Count; i++) Assert.IsTrue(bytesByPath[logical[i - 1].Path] >= bytesByPath[logical[i].Path], "initial sort is allocated size descending");
        NameSortAndReverse(app, expected);
        logical = LogicalRows(app, expected);
        VisitAll(app, logical, expected, ToggleState.Off);
        NameSortAndReverse(app, expected); // sorting still covers all rows after every rich row has unloaded once
        logical = LogicalRows(app, expected);
        var last = logical[^1];
        var visibleLast = Reveal(app, last);
        Ui.Focus(Box(visibleLast)!);
        Assert.IsTrue(AppSession.WaitUntil(() => ActionIds.All(id => Table.InRow(visibleLast, id) is { } button
            && !button.Current.IsOffscreen && button.Current.IsEnabled), Ui.ElementTimeout), "last row restores both existing row actions when focused");
        var first = logical[0];
        var firstBox = Box(Reveal(app, first))!;
        Table.Tick(firstBox, () => Box(Reveal(app, first)), first.Path);
        Assert.AreEqual(ToggleState.Off, Table.State(Box(Reveal(app, last))!), "other offscreen row remains unticked");
        Assert.AreEqual(ToggleState.On, Table.State(Box(Reveal(app, first))!), "a tick survives offscreen unloading/reloading");
        SetAll(app, ToggleState.On);
        VisitAll(app, logical, expected, ToggleState.On);
        NameSortAndReverse(app, expected);
        logical = LogicalRows(app, expected);
        Assert.AreEqual(ToggleState.On, Table.State(Box(Reveal(app, logical[^1]))!), "sort cannot discard an offscreen selected model");
        SetAll(app, ToggleState.Off);
        Assert.AreEqual(ToggleState.Off, Table.State(Box(Reveal(app, logical[0]))!));
        Assert.AreEqual(ToggleState.Off, Table.State(Box(Reveal(app, logical[^1]))!));
        AssertBounded(app, "final viewport after sorting and selection");
        foreach (var file in expected.Values) Assert.IsTrue(File.Exists(file.Path), "read-only realization test never moves data");
    }
}
