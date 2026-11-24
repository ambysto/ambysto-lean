using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-22 (docs/spec/07, "Tables, the look of their columns"): neighbouring table columns tell apart. Large and old has six columns
/// (Move, Name, Folder, Size, Last modified, Type); its own fixture holds two old files of just over 100 MB, so two rows are listed. Normal
/// theme: the empty ground of a row (the strip above its text) differs between adjacent columns and is the same in every second one. High
/// Contrast (HighContrastScope): a line in the system GrayText colour lies in the gap between two columns, in the header and in every row.
/// Only pixels are read; nothing is moved.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TableColumnBandTests
{
    private const long MB = ByteUnits.MB;
    private static readonly string[] Names = ["band-one.bin", "band-two.bin"];

    private static Fixture OldFiles()
    {
        var fixture = new Fixture("ui-bands", Fixture.ProfileBaseFolder());
        try
        {
            fixture.File(Names[0], 101 * MB);
            fixture.File(Names[1], 102 * MB);
            foreach (var name in Names)
            {
                fixture.SetModified(name, DateTime.UtcNow.AddYears(-3));
            }

            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>The listed view, the window handle and the headers of the columns Folder, Size and Last modified.</summary>
    private sealed record Table3(AutomationElement View, AutomationElementHandle Handle, Rect List, Rect Folder, Rect Size, Rect Date, List<Rect> Rows);

    private static Table3 Open(AppSession app)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Names.All(n => Table.ControlWith(view, "LargeOldSelect", n) is not null), Ui.ElementTimeout),
            "the two rows are not listed: " + Table.Dump(view, "LargeOldSelect"));
        var window = app.Window(Ui.WindowTimeout);
        var handle = new AutomationElementHandle(new nint(window.Current.NativeWindowHandle));
        var list = AppSession.Find(view, "LargeOldList", Ui.ElementTimeout).Current.BoundingRectangle;
        WindowOnScreen.Settle(handle, list);
        var rows = Names.Select(n => Table.RowOf(Table.ControlWith(view, "LargeOldSelect", n)!)!.Current.BoundingRectangle).ToList();
        return new Table3(view, handle, list, Header(view, Text.ColumnFolder), Header(view, Text.ColumnSize), Header(view, Text.ColumnLastModified), rows);
    }

    private static Rect Header(AutomationElement view, string name) => Table.Header(view, name).Current.BoundingRectangle;

    private static int Px(Table3 table, double dips) => (int)Math.Round(dips * WindowOnScreen.Scale(table.Handle));

    /// <summary>The average colour of the strip just under the top of a row, across a column: above the text, so only the ground (and a band)
    /// is there.</summary>
    private static Rgb Ground(ScreenCapture capture, Table3 table, Rect row, Rect column)
    {
        var top = (int)Math.Round(row.Top) + Px(table, 4);
        var cells = capture.Area((int)Math.Round(column.Left) + Px(table, 2), top, (int)Math.Round(column.Right) - Px(table, 2), top + Px(table, 3));
        Assert.IsTrue(cells.Count > 0, "no pixels in the strip of the row " + row + " at the column " + column);
        return new Rgb((byte)cells.Average(c => c.R), (byte)cells.Average(c => c.G), (byte)cells.Average(c => c.B));
    }

    private static int Apart(Rgb a, Rgb b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    // Contract "Normal theme": in a table of six columns the empty ground of Folder (2nd after Move, an even column) and Last modified
    // differs from the ground of Size between them, in each row, by at least the tolerance, while Folder and Last modified (both bands)
    // are alike. The band does not make the row unreadable: the difference stays small (under 40 per channel).
    [TestMethod]
    [TestCategory("Console")]
    public void AdjacentColumnsOfATableHaveDifferentGroundInTheNormalTheme()
    {
        if (DisplaySettings.HighContrastOn())
        {
            Assert.Inconclusive("Windows High Contrast is on: the bands are not drawn (their own test)");
        }

        using var fixture = OldFiles();
        using var app = AppSession.Start("--scan", fixture.Path);
        var table = Open(app);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(table.Handle));
        Console.WriteLine($"bands: saved {capture.Save(nameof(AdjacentColumnsOfATableHaveDifferentGroundInTheNormalTheme))}");
        var problems = new List<string>();
        foreach (var row in table.Rows)
        {
            var folder = Ground(capture, table, row, table.Folder);
            var size = Ground(capture, table, row, table.Size);
            var date = Ground(capture, table, row, table.Date);
            Console.WriteLine($"row {row}: Folder {folder}, Size {size}, Last modified {date}");
            if (Apart(folder, size) < Brand.Tolerance || Apart(size, date) < Brand.Tolerance)
            {
                problems.Add($"row {row}: adjacent columns look alike: Folder {folder}, Size {size}, Last modified {date}");
            }

            if (Apart(folder, date) > Brand.NeutralTolerance)
            {
                problems.Add($"row {row}: the two banded columns differ: Folder {folder}, Last modified {date}");
            }

            if (Apart(folder, size) > 40)
            {
                problems.Add($"row {row}: the band is too strong: Folder {folder}, Size {size}");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }

    // Contract "High Contrast": no band, but a vertical line of the system GrayText colour lies in the gap before Size (and before Last
    // modified), through the header and every listed row.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void UnderHighContrastAGrayTextLineLiesBetweenColumns()
    {
        using var contrast = new HighContrastScope();
        var grayText = HighContrastScope.System(17); // COLOR_GRAYTEXT
        using var fixture = OldFiles();
        using var app = AppSession.Start("--scan", fixture.Path);
        var table = Open(app);
        var capture = ScreenCapture.Of(WindowOnScreen.Frame(table.Handle));
        Console.WriteLine($"divider: saved {capture.Save(nameof(UnderHighContrastAGrayTextLineLiesBetweenColumns))}, GrayText {grayText}");
        var problems = new List<string>();
        foreach (var (name, column) in new[] { (Text.ColumnSize, table.Size), (Text.ColumnLastModified, table.Date) })
        {
            // The gap before a column is 8 DIP wide and the line is in its middle; look 3 DIP either side of that for it.
            var middle = (int)Math.Round(column.Left) - Px(table, 4);
            var heights = new List<(string What, int Y)> { ("the header", (int)Math.Round(table.Folder.Top + (table.Folder.Height / 2))) };
            heights.AddRange(table.Rows.Select((r, i) => ($"row {i + 1}", (int)Math.Round(r.Top + (r.Height / 2)))));
            foreach (var (what, y) in heights)
            {
                var found = Enumerable.Range(middle - Px(table, 3), (2 * Px(table, 3)) + 1)
                    .Any(x => capture.Contains(x, y) && capture.At(x, y).Near(grayText, Brand.Tolerance));
                if (!found)
                {
                    var seen = capture.Area(middle - Px(table, 3), y, middle + Px(table, 3) + 1, y + 1);
                    problems.Add($"no line of {grayText} in the gap before {name} at {what} (y {y}, x {middle} +- {Px(table, 3)}): " + ScreenCapture.Commonest(seen));
                }
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
    }
}
