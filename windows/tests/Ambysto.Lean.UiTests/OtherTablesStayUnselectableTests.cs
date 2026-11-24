using System;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-16 (review round 1, MINOR 3): the Uninstall table opts in to single selection; "every other table stays
/// <c>SelectionMode.None</c>". For a row of each other table (Largest files, Large and old, Temporary files, Duplicates, the leftovers,
/// History) the test tries to select it the two ways a user or a screen reader can: the UI Automation <c>SelectionItemPattern.Select</c> (when
/// the row has the pattern), and a real mouse click on a text cell of the row. Either the pattern is absent, or after both the row is not
/// selected. Nothing is ticked or moved: the click lands on a cell with no control (the History click is on the first cell, away from Put
/// back), and only a text cell is clicked.</summary>
[TestClass]
[DoNotParallelize]
public sealed class OtherTablesStayUnselectableTests
{
    private const long MB = ByteUnits.MB;

    private static SelectionItemPattern? PatternOf(AutomationElement row)
    {
        try
        {
            var node = row;
            for (var level = 0; level < 4 && node is not null; level++)
            {
                if (node.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                {
                    return (SelectionItemPattern)pattern;
                }

                node = TreeWalker.ControlViewWalker.GetParent(node);
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    private static bool Selected(AutomationElement row)
    {
        try
        {
            return PatternOf(row) is { } pattern && pattern.Current.IsSelected;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>Tries to select <paramref name="row"/> by pattern and by a click at <paramref name="across"/> (0..1) of its width; fails if it is then selected.</summary>
    private static void AssertCannotBeSelected(AutomationElement row, string what, double across)
    {
        // The row may be below the visible part of a long list (a machine's own Temp folder holds many larger files), so it is scrolled
        // into view first; scrolling selects nothing.
        if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
        {
            ((ScrollItemPattern)scroll).ScrollIntoView();
            AppSession.WaitUntil(() => row.Current.BoundingRectangle.Width > 50, Ui.ElementTimeout);
        }

        Assert.IsTrue(row.Current.BoundingRectangle.Width > 50, $"precondition: the {what} row is not on screen: {Ui.Describe(row)}");
        var pattern = PatternOf(row);
        Console.WriteLine($"{what}: SelectionItemPattern {(pattern is null ? "absent" : "present")}");
        try
        {
            pattern?.Select();
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotEnabledException or System.Runtime.InteropServices.COMException)
        {
            // refused: that is the contract
        }

        System.Threading.Thread.Sleep(400);
        Assert.IsFalse(Selected(row), $"the {what} row is selected after SelectionItemPattern.Select: {Ui.Describe(row)}");

        var rect = row.Current.BoundingRectangle;
        Mouse.LeftClickAt(rect.Left + (rect.Width * across), rect.Top + (rect.Height / 2));
        System.Threading.Thread.Sleep(400);
        Assert.IsFalse(Selected(row), $"the {what} row is selected after a click: {Ui.Describe(row)}");
    }

    // Largest files (the map's list).
    [TestMethod]
    [TestCategory("Console")]
    public void ALargestFilesRowCannotBeSelected()
    {
        using var fixture = new Fixture("ui-asp16-largest");
        fixture.File("a.bin", 2 * MB);
        fixture.File("b.bin", 3 * MB);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        System.Collections.Generic.List<AutomationElement> rows = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (rows = Table.LeafRows(ResultsViewTests.List(app)).Where(r => r.Current.Name.Length > 0 && !r.Current.BoundingRectangle.IsEmpty).ToList()).Count > 0, Ui.ElementTimeout),
            "precondition: the Largest files list has no row");
        AssertCannotBeSelected(rows[0], "Largest files", 0.45);
    }

    // Large and old.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ALargeAndOldRowCannotBeSelected()
    {
        using var fixture = new Fixture("ui-asp16-largeold", Fixture.ProfileBaseFolder());
        fixture.File("old.zip", 101 * MB);
        fixture.SetModified("old.zip", DateTime.UtcNow.AddYears(-3));
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (box = Table.ControlFor(view, "LargeOldSelect", fixture.Sub("old.zip"))) is not null, Ui.ElementTimeout),
            "precondition: no Large and old row for the fixture: " + Table.Dump(view, "LargeOldSelect"));
        AssertCannotBeSelected(Table.RowOf(box!)!, "Large and old", 0.45);
    }

    // Temporary files.
    [TestMethod]
    [TestCategory("Console")]
    public void ATemporaryFilesRowCannotBeSelected()
    {
        var name = "LeanUiTemp-asp16-" + Guid.NewGuid().ToString("N")[..8] + ".bin";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", name);
        var oldUtc = DateTime.UtcNow.AddDays(-10);
        File.WriteAllBytes(path, new byte[100 * 1024]);
        File.SetCreationTimeUtc(path, oldUtc);
        File.SetLastWriteTimeUtc(path, oldUtc);
        File.SetLastAccessTimeUtc(path, oldUtc);
        try
        {
            using var app = AppSession.Start();
            var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            AutomationElement? box = null;
            Assert.IsTrue(AppSession.WaitUntil(() => (box = Table.ControlWith(view, "TempItem", name)) is not null, TimeSpan.FromSeconds(90)),
                $"precondition: no TempItem row for {name}: {Table.Dump(view, "TempItem")}");
            AssertCannotBeSelected(Table.RowOf(box!)!, "Temporary files", 0.45);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // Duplicates.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ADuplicatesRowCannotBeSelected()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var fixture = new MoveFixture("asp16-dups");
        var content = new byte[(int)(2 * MB)];
        new Random(16_20).NextBytes(content);
        var first = fixture.FileWithBytes(@"one\copy-a.dat", content);
        fixture.FileWithBytes(@"two\copy-b.dat", content);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (box = Table.ControlFor(view, "DuplicateCopy", first)) is not null, Ui.DuplicatesTimeout),
            "precondition: no Duplicates row for the fixture: " + Table.Dump(view, "DuplicateCopy"));
        AssertCannotBeSelected(Table.RowOf(box!)!, "Duplicates", 0.5);
    }

    // The leftovers of a gone test app.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ALeftoversRowCannotBeSelected()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders();
        using var gone = new GoneApp();
        using var app = AppSession.Start();
        var view = gone.OfferLeftovers(app);
        var box = Table.ControlWith(view, "LeftoverItem", gone.Leftover)!;
        AssertCannotBeSelected(Table.RowOf(box)!, "Leftovers", 0.5);
    }

    // History (the test's own journal).
    [TestMethod]
    [TestCategory("Console")]
    public void AHistoryRowCannotBeSelected()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-asp16-history-" + Guid.NewGuid().ToString("N")[..8]);
        var trashed = Path.Combine(root, "was-here", "trashed-item.bin");
        var now = DateTimeOffset.UtcNow;
        using var backup = new JournalBackup();
        JournalBackup.Write([JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-asp16", now.AddHours(-2), "map", trashed, "trashed", bytes: 4096)]);
        using var app = AppSession.Start();
        var history = Ui.OpenView(app, "History", "HistoryView");
        System.Collections.Generic.List<AutomationElement> rows = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (rows = Table.LeafRows(history).Where(r => Table.Holds(r.Current.Name, trashed) && !r.Current.BoundingRectangle.IsEmpty).ToList()).Count == 1, Ui.ElementTimeout),
            "precondition: History does not show the entry as a row: [" + string.Join(" | ", Table.LeafRowNames(history)) + "]");
        AssertCannotBeSelected(rows[0], "History", 0.08);
        Assert.IsFalse(Directory.Exists(root), "the test's paths must not exist");
    }
}
