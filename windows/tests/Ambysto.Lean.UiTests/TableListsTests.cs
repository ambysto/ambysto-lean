using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-10 (intent/2026-10-18-asp-10-table-lists.md, issue #22): the lists are tables with columns and tick boxes.
/// Each list is one table control with a header row of sort buttons (a second press reverses); the Folder column shortens
/// long paths in the middle, with the full path as the tooltip and in the row's automation name; sizes in
/// <c>ByteUnits.Format</c>, dates in the current culture's short date; a row's automation name joins its cells.
/// Duplicates: one table per <c>DuplicateGroup</c> with Keep (<c>DuplicateKeep</c>, the suggested copy at first), Move
/// (<c>DuplicateCopy</c>, none ticked, the kept copy's disabled), Name, Folder, Size, Last modified and Suggested; the
/// confirmation keeps "Keeping: &lt;path&gt;" from the chosen Keep; Space toggles a focused row's Move, Enter chooses its
/// Keep. Large and old (<c>LargeOldSelect</c>), Temporary files (<c>TempItem</c>), the leftovers (<c>LeftoverItem</c>) and
/// History (Item, Folder, Result, Reason, <c>PutBackEntry</c> in the row). Display-only rows stay disabled with their hint
/// (v1 additions, group 4). Through the UI Automation client in the console session; the column names come from
/// <c>Copy.Text</c>. Nothing is moved here (moves are MoveAndPutBackTests'); the only confirmation opened is cancelled.</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class TableListsTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;

    private static readonly string[] DuplicateColumns =
        [Text.ColumnKeep, Text.ColumnMove, Text.ColumnName, Text.ColumnFolder, Text.ColumnSize, Text.ColumnLastModified, Text.ColumnSuggested];

    // ---- Duplicates: fixtures and helpers ------------------------------------------------------------------------------

    /// <summary>The duplicates fixture: a triple of 2 MB copies (copy-b the newest, so the suggested keep, docs/spec/03)
    /// and, with <c>withPair</c>, a pair of 1 MB + 4 KB copies, one of them in a folder about 230 characters long (the
    /// suggested keep is pair-short, the newer). All under %USERPROFILE%\LeanUiMove-* (root (a)); nothing is moved.</summary>
    private sealed class Dups : IDisposable
    {
        public Dups(string name, bool withPair)
        {
            Fixture = new MoveFixture(name);
            var triple = new byte[(int)(2 * MB)];
            new Random(10_22).NextBytes(triple);
            A = Fixture.FileWithBytes(@"tri-a\copy-a.dat", triple);
            B = Fixture.FileWithBytes(@"tri-b\copy-b.dat", triple);
            C = Fixture.FileWithBytes(@"tri-c\copy-c.dat", triple);
            var now = DateTime.UtcNow;
            Modified[A] = now.AddDays(-3);
            Modified[B] = now.AddDays(-1);
            Modified[C] = now.AddDays(-2);
            if (withPair)
            {
                var pair = new byte[(int)(MB + (4 * KB))];
                new Random(10_23).NextBytes(pair);
                var deep = string.Join('\\', Enumerable.Range(1, 8).Select(i => $"deep-folder-segment-{i:D2}"));
                PairLong = Fixture.FileWithBytes(deep + @"\pair-long.dat", pair);
                PairShort = Fixture.FileWithBytes(@"pair-short\pair-short.dat", pair);
                Assert.IsTrue(PairLong.Length < 255, $"precondition: the long path ({PairLong.Length} characters) stays under MAX_PATH");
                Modified[PairLong] = now.AddDays(-5);
                Modified[PairShort] = now.AddDays(-4);
            }

            foreach (var (path, utc) in Modified)
            {
                File.SetLastWriteTimeUtc(path, utc);
            }
        }

        public MoveFixture Fixture { get; }

        public string A { get; }

        public string B { get; }

        public string C { get; }

        public string? PairLong { get; }

        public string? PairShort { get; }

        public Dictionary<string, DateTime> Modified { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string[] Triple => [A, B, C];

        public string[] All => PairLong is null ? Triple : [A, B, C, PairLong, PairShort!];

        public string SuggestedOf(string path) => Triple.Contains(path) ? B : PairShort!;

        public long BytesOf(string path) => Triple.Contains(path) ? 2 * MB : MB + (4 * KB);

        public void Dispose() => Fixture.Dispose();
    }

    /// <summary>Scans the fixture, opens Duplicates, presses FindDuplicates and waits for <paramref name="groups"/> groups
    /// and every copy of the fixture.</summary>
    private static AutomationElement FindDuplicates(AppSession app, Dups dups, int groups)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count == groups && dups.All.All(p => Move(view, p) is not null && Keep(view, p) is not null), Ui.DuplicatesTimeout),
            $"precondition: {groups} duplicate groups with every copy (a Move box and a Keep radio in its row); groups [{string.Join(" | ", AppSession.FindAll(view, "DuplicateGroup").Select(g => g.Current.Name))}], Move {Table.Dump(view, "DuplicateCopy")}, Keep {Table.Dump(view, "DuplicateKeep")}");
        return view;
    }

    private static AutomationElement? Move(AutomationElement view, string path) => Table.ControlFor(view, "DuplicateCopy", path);

    private static AutomationElement? Keep(AutomationElement view, string path) => Table.ControlFor(view, "DuplicateKeep", path);

    private static AutomationElement MoveOf(AutomationElement view, string path)
    {
        var box = Move(view, path);
        Assert.IsNotNull(box, $"no DuplicateCopy in a row of {path}: {Table.Dump(view, "DuplicateCopy")}");
        return box;
    }

    private static AutomationElement KeepOf(AutomationElement view, string path)
    {
        var radio = Keep(view, path);
        Assert.IsNotNull(radio, $"no DuplicateKeep in a row of {path}: {Table.Dump(view, "DuplicateKeep")}");
        return radio;
    }

    private static bool Ticked(AutomationElement view, string path) => Move(view, path) is { } b && Table.State(b) == ToggleState.On;

    private static bool Chosen(AutomationElement view, string path) => Keep(view, path) is { } r && Table.IsChosen(r);

    private static bool MoveEnabled(AutomationElement view, string path) => Move(view, path) is { } b && b.Current.IsEnabled;

    private static string State(AutomationElement view) => "Move " + Table.Dump(view, "DuplicateCopy") + ", Keep " + Table.Dump(view, "DuplicateKeep");

    /// <summary>Gives the keyboard focus to <paramref name="row"/> (or a control in it), or fails.</summary>
    private static void FocusRow(AutomationElement row)
    {
        row.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => AutomationElement.FocusedElement is { } f && Ui.IsWithin(f, row), TimeSpan.FromSeconds(3)),
            "the row did not take the keyboard focus: " + Ui.Describe(row));
    }

    /// <summary>The file names of the <paramref name="automationId"/> rows among <paramref name="names"/>, in the list's order.</summary>
    private static List<string> Order(AutomationElement scope, string automationId, IReadOnlyCollection<string> names) =>
        Table.Controls(scope, automationId).Select(Table.RowName)
            .Select(n => names.FirstOrDefault(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Where(f => f is not null).Select(f => f!).ToList();

    /// <summary>True when <paramref name="order"/> holds every one of <paramref name="names"/> once, sorted by
    /// <paramref name="key"/> one way or the other (strictly).</summary>
    private static bool SortedEitherWay(List<string> order, IReadOnlyCollection<string> names, Func<string, IComparable> key)
    {
        if (order.Count != names.Count || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            return false;
        }

        var up = true;
        var down = true;
        for (var i = 1; i < order.Count; i++)
        {
            var compare = key(order[i - 1]).CompareTo(key(order[i]));
            up &= compare < 0;
            down &= compare > 0;
        }

        return up || down;
    }

    /// <summary>Presses <paramref name="header"/> and waits for the rows sorted by <paramref name="key"/>; presses it again
    /// and waits for exactly the reverse order.</summary>
    private static void AssertSortAndReverse(AutomationElement scope, string automationId, string column, IReadOnlyCollection<string> names, Func<string, IComparable> key)
    {
        var header = Table.Header(scope, column);
        Table.Press(header);
        List<string> first = [];
        Assert.IsTrue(AppSession.WaitUntil(() => SortedEitherWay(first = Order(scope, automationId, names), names, key), Ui.ElementTimeout),
            $"after pressing \"{column}\" the rows are not sorted by it: [{string.Join(" | ", first)}]");
        var reversed = Enumerable.Reverse(first).ToList();
        Table.Press(Table.Header(scope, column));
        List<string> second = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (second = Order(scope, automationId, names)).SequenceEqual(reversed, StringComparer.OrdinalIgnoreCase), Ui.ElementTimeout),
            $"pressing \"{column}\" again did not reverse the order [{string.Join(" | ", first)}]: now [{string.Join(" | ", second)}]");
    }

    // ---- Duplicates ------------------------------------------------------------------------------------------------------

    // Contract "Duplicates" and "The table": FindDuplicates over the triple and the pair gives two groups, each headed by
    // Text.CopiesEach and each its own table with the header buttons Keep, Move, Name, Folder, Size, Last modified and
    // Suggested, left to right. Each copy's row holds a DuplicateKeep radio and a DuplicateCopy check box, and its automation
    // name its cells: the file name, the full folder, ByteUnits.Format(size), the short date of its modification; and
    // Text.SuggestedKeep only on the suggested copy's row.
    [TestMethod]
    [TestCategory("Console")]
    public void EachDuplicateGroupIsATableWithTheContractColumns()
    {
        using var dups = new Dups("asp10-dupcols", withPair: true);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);

        var groups = AppSession.FindAll(view, "DuplicateGroup");
        foreach (var heading in new[] { Text.CopiesEach(3, ByteUnits.Format(2 * MB)), Text.CopiesEach(2, ByteUnits.Format(MB + (4 * KB))) })
        {
            Assert.IsTrue(groups.Any(g => Ui.AnyNameContains(g, heading)), $"no DuplicateGroup headed \"{heading}\": [{string.Join(" | ", groups.Select(g => g.Current.Name))}]");
        }

        Table.AssertColumns(view, 2, DuplicateColumns);

        foreach (var path in dups.All)
        {
            var box = MoveOf(view, path);
            var radio = KeepOf(view, path);
            Assert.AreEqual(ControlType.CheckBox, box.Current.ControlType, path + ": Move is a check box");
            Assert.AreEqual(ControlType.RadioButton, radio.Current.ControlType, path + ": Keep is a radio button");
            Assert.IsTrue(Automation.Compare(Table.RowOf(box)!, Table.RowOf(radio)!), path + ": its Keep and its Move are not in one row");

            var row = Table.RowName(box);
            foreach (var cell in new[] { Path.GetFileName(path), Path.GetDirectoryName(path)!, ByteUnits.Format(dups.BytesOf(path)), Table.ShortDate(dups.Modified[path]) })
            {
                Assert.IsTrue(row.Contains(cell, StringComparison.OrdinalIgnoreCase), $"the row of {path} does not hold \"{cell}\": \"{row}\"");
            }

            var suggested = dups.SuggestedOf(path) == path;
            Assert.AreEqual(suggested, row.Contains(Text.SuggestedKeep, StringComparison.Ordinal),
                $"{path}: \"{Text.SuggestedKeep}\" belongs on the suggested copy's row only; row \"{row}\"");
        }
    }

    // Contract "Duplicates": Keep is chosen on the suggested copy at first (copy-b, the newest), on no other; no Move box is
    // ticked; the kept copy's Move box is disabled and cannot be ticked, the others are enabled; the view's Move to Recycle
    // Bin is disabled with nothing ticked.
    [TestMethod]
    [TestCategory("Console")]
    public void KeepStartsOnTheSuggestedCopyAndNoMoveBoxIsTicked()
    {
        using var dups = new Dups("asp10-dupkeep", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);

        foreach (var path in dups.Triple)
        {
            Assert.AreEqual(path == dups.B, Table.IsChosen(KeepOf(view, path)), $"{path}: Keep is chosen on the suggested copy only at first; {State(view)}");
            Assert.AreEqual(ToggleState.Off, Table.State(MoveOf(view, path)), $"{path}: a Move box is ticked at first; {State(view)}");
            Assert.AreEqual(path != dups.B, MoveOf(view, path).Current.IsEnabled, $"{path}: only the kept copy's Move box is disabled; {State(view)}");
        }

        Assert.IsFalse(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "Move to Recycle Bin is enabled with nothing ticked");
        Table.TryToTick(MoveOf(view, dups.B));
        Assert.IsFalse(AppSession.WaitUntil(() => Ticked(view, dups.B), Ui.NeverWatch), "the kept copy's Move box was ticked; " + State(view));
    }

    // Contract "Duplicates": choosing another Keep unticks and disables that copy's Move. copy-a's Move is ticked, then its
    // Keep chosen: copy-a's Move is unticked and disabled (and stays unticked when ticked again), copy-b's Keep is no
    // longer chosen and its Move is enabled but not ticked (nothing is ticked for the user), copy-c is unchanged; one Keep
    // per group. Choosing copy-b's Keep again gives copy-a's Move back, unticked.
    [TestMethod]
    [TestCategory("Console")]
    public void ChoosingAnotherKeepDisablesAndUnticksThatCopysMove()
    {
        using var dups = new Dups("asp10-dupswap", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);
        var (a, b, c) = (dups.A, dups.B, dups.C);

        Table.Tick(MoveOf(view, a), () => Move(view, a), "copy-a's Move");
        Table.Choose(KeepOf(view, a));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, a) && !Chosen(view, b) && !Chosen(view, c), Ui.ElementTimeout),
            "after choosing copy-a's Keep, it is not the one Keep of the group: " + State(view));
        Assert.IsTrue(AppSession.WaitUntil(() => !Ticked(view, a) && !MoveEnabled(view, a), Ui.ElementTimeout),
            "the new kept copy's Move is not unticked and disabled: " + State(view));
        Assert.IsTrue(AppSession.WaitUntil(() => MoveEnabled(view, b), Ui.ElementTimeout), "the copy no longer kept (copy-b) has its Move still disabled: " + State(view));
        Assert.IsFalse(Ticked(view, b), "copy-b's Move was ticked by the Keep change: " + State(view));
        Assert.IsTrue(MoveEnabled(view, c) && !Ticked(view, c), "copy-c changed: " + State(view));
        Assert.IsFalse(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout).Current.IsEnabled, "Move to Recycle Bin is enabled with nothing ticked");

        Table.TryToTick(MoveOf(view, a));
        Assert.IsFalse(AppSession.WaitUntil(() => Ticked(view, a), Ui.NeverWatch), "the kept copy-a's Move was ticked: " + State(view));

        Table.Choose(KeepOf(view, b));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, b) && !Chosen(view, a) && !MoveEnabled(view, b) && MoveEnabled(view, a) && !Ticked(view, a), Ui.ElementTimeout),
            "choosing copy-b's Keep again did not give copy-a's Move back, unticked: " + State(view));
    }

    // Contract "Duplicates": the confirmation keeps "Keeping: <path>" per group from the chosen Keep, not only from the
    // suggestion. In the triple copy-c's Keep is chosen and copy-a and copy-b (the suggested one) are ticked; in the pair the
    // suggested pair-short stays kept and pair-long is ticked. The confirmation lists the three ticked copies, says
    // Text.Keeping(copy-c) for both triple items and Text.Keeping(pair-short), and never names a ticked copy as kept.
    // Cancelled: every copy is in place, none in the Recycle Bin.
    [TestMethod]
    [TestCategory("Console")]
    public void TheConfirmationSaysKeepingTheChosenKeepOfEachGroup()
    {
        using var dups = new Dups("asp10-dupconfirm", withPair: true);
        var identities = dups.All.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);

        Table.Choose(KeepOf(view, dups.C));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, dups.C) && !MoveEnabled(view, dups.C) && MoveEnabled(view, dups.B), Ui.ElementTimeout), "copy-c's Keep did not take: " + State(view));
        Assert.IsTrue(Chosen(view, dups.PairShort!), "choosing a Keep in the triple changed the pair's Keep: " + State(view));
        foreach (var path in new[] { dups.A, dups.B, dups.PairLong! })
        {
            Table.Tick(MoveOf(view, path), () => Move(view, path), path);
        }

        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move to Recycle Bin stays disabled with three copies ticked");
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        try
        {
            List<AutomationElement> items = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count == 3, Ui.ElementTimeout), "the confirmation does not list the three ticked copies: " + Ui.Dump(dialog));
            foreach (var path in new[] { dups.A, dups.B, dups.PairLong! })
            {
                var expected = Text.ConfirmItem(path, ByteUnits.Format(dups.BytesOf(path)));
                Assert.IsTrue(items.Any(i => i.Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase)), $"no ConfirmItem \"{expected}\": {Ui.Dump(dialog)}");
            }

            var names = Ui.Names(dialog);
            int Count(string text) => names.Count(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Count(Text.Keeping(dups.C)) >= 2, $"both triple items should say \"{Text.Keeping(dups.C)}\" (the chosen Keep): {Ui.Dump(dialog)}");
            Assert.IsTrue(Count(Text.Keeping(dups.PairShort!)) >= 1, $"the pair item should say \"{Text.Keeping(dups.PairShort!)}\": {Ui.Dump(dialog)}");
            foreach (var ticked in new[] { dups.A, dups.B, dups.PairLong! })
            {
                Assert.AreEqual(0, Count(Text.Keeping(ticked)), $"the ticked copy {ticked} is named as kept (the suggestion, not the chosen Keep?): {Ui.Dump(dialog)}");
            }
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        }

        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, Ui.NeverWatch), "a move report appeared after Cancel");
        foreach (var path in dups.All)
        {
            Assert.IsTrue(File.Exists(path), path + " was moved");
            Assert.AreEqual(identities[path], MoveFixture.Identity(path), path + " is not the same file");
            Assert.IsNull(RecycleBin.Holding(path), path + " is in the Recycle Bin");
        }
    }

    // Contract "Keyboard": Space toggles the focused row's tick box (copy-a's row: ticked, then unticked); on the kept
    // copy's row (copy-b) Space ticks nothing, its box being disabled; Enter on copy-c's row chooses its Keep, which
    // disables copy-c's Move and gives copy-b's Move back, unticked.
    [TestMethod]
    [TestCategory("Console")]
    public void SpaceTogglesAFocusedRowsMoveAndEnterChoosesItsKeep()
    {
        using var dups = new Dups("asp10-dupkeys", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);
        AutomationElement RowOf(string path) => Table.RowOf(MoveOf(view, path)) ?? throw new AssertFailedException("no row holds the Move box of " + path);

        FocusRow(RowOf(dups.A));
        Keys.Press(Keys.Space);
        Assert.IsTrue(AppSession.WaitUntil(() => Ticked(view, dups.A), TimeSpan.FromSeconds(3)), "Space on copy-a's row did not tick its Move: " + State(view));
        Assert.IsTrue(Chosen(view, dups.B), "Space changed the Keep: " + State(view));
        FocusRow(RowOf(dups.A));
        Keys.Press(Keys.Space);
        Assert.IsTrue(AppSession.WaitUntil(() => !Ticked(view, dups.A), TimeSpan.FromSeconds(3)), "Space again on copy-a's row did not untick its Move: " + State(view));

        FocusRow(RowOf(dups.B));
        Keys.Press(Keys.Space);
        Assert.IsFalse(AppSession.WaitUntil(() => Ticked(view, dups.B), Ui.NeverWatch), "Space on the kept copy's row ticked its Move: " + State(view));

        FocusRow(RowOf(dups.C));
        Keys.Press(Keys.Enter);
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, dups.C) && !Chosen(view, dups.B), TimeSpan.FromSeconds(3)), "Enter on copy-c's row did not choose its Keep: " + State(view));
        Assert.IsTrue(AppSession.WaitUntil(() => !MoveEnabled(view, dups.C) && !Ticked(view, dups.C) && MoveEnabled(view, dups.B) && !Ticked(view, dups.B), TimeSpan.FromSeconds(3)),
            "after Enter chose copy-c's Keep, copy-c's Move is not disabled or copy-b's not given back unticked: " + State(view));
        Assert.IsNull(app.FindInAnyWindow("ConfirmMove"), "Enter on a row opened the confirmation");
    }

    // Contract "The table": the Folder column shortens a long path in the middle ("C:\Users\…\build\dist"). pair-long's
    // folder is about 230 characters: a text in its row is that folder with its middle cut by an ellipsis; the row's
    // automation name holds the full folder, and so does a tooltip (UI Automation HelpText) in the row.
    [TestMethod]
    [TestCategory("Console")]
    public void TheFolderCellShortensALongPathInTheMiddleAndTheRowHoldsItWhole()
    {
        using var dups = new Dups("asp10-dupfolder", withPair: true);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);
        var folder = Path.GetDirectoryName(dups.PairLong!)!;
        Assert.IsTrue(folder.Length > 200, "precondition: the folder is long: " + folder);

        var row = Table.RowOf(MoveOf(view, dups.PairLong!));
        Assert.IsNotNull(row, "no row holds pair-long's Move box");
        Assert.IsTrue(row.Current.Name.Contains(folder, StringComparison.OrdinalIgnoreCase), $"the row's automation name lacks the full folder {folder}: \"{row.Current.Name}\"");
        var texts = Table.Texts(row);
        var dump = "[" + string.Join(" | ", texts.Select(t => t.Name + (t.Help.Length > 0 ? " {help: " + t.Help + "}" : string.Empty))) + "]";
        Assert.IsTrue(texts.Any(t => Table.IsMiddleShortened(t.Name, folder)), $"no cell of the row shows {folder} shortened in the middle: {dump}");
        Assert.IsTrue(texts.Any(t => t.Help.Contains(folder, StringComparison.OrdinalIgnoreCase)), $"no tooltip (HelpText) in the row holds the full folder {folder}: {dump}");
    }

    // Contract "The table": a column header is a button that sorts by its column, and a second press reverses. Name sorts
    // the triple by file name; again reverses it. Size (all three copies are the same size) keeps every row listed.
    [TestMethod]
    [TestCategory("Console")]
    public void DuplicatesSortByNameAndPressingAgainReverses()
    {
        using var dups = new Dups("asp10-dupsort", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);
        var names = dups.Triple.Select(p => Path.GetFileName(p)).ToArray();

        AssertSortAndReverse(view, "DuplicateCopy", Text.ColumnName, names, n => n.ToUpperInvariant());
        Table.Press(Table.Header(view, Text.ColumnSize));
        Assert.IsTrue(AppSession.WaitUntil(() => Order(view, "DuplicateCopy", names).Count == 3, Ui.ElementTimeout), "sorting by Size lost a copy: " + Table.Dump(view, "DuplicateCopy"));
        Table.Press(Table.Header(view, Text.ColumnSize));
        Assert.IsTrue(AppSession.WaitUntil(() => Order(view, "DuplicateCopy", names).Count == 3, Ui.ElementTimeout), "sorting by Size again lost a copy: " + Table.Dump(view, "DuplicateCopy"));
        Assert.IsTrue(Chosen(view, dups.B) && !Ticked(view, dups.A) && !Ticked(view, dups.C), "sorting changed the Keep or a tick: " + State(view));
    }

    // ---- Large and old ---------------------------------------------------------------------------------------------------

    // Three .zip files modified three years ago, of three sizes over the 100 MB default (whole clusters: allocated equals
    // the length), in root (a). Name order alpha, beta, gamma; size order beta, gamma, alpha.
    private static readonly (string Name, long Bytes)[] OldFiles = [("alpha-old.zip", 103 * MB), ("beta-old.zip", 101 * MB), ("gamma-old.zip", 102 * MB)];
    private static MoveFixture? largeOld;
    private static DateTime threeYearsAgoUtc;

    [ClassInitialize]
    public static void BuildTheLargeAndOldFiles(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        threeYearsAgoUtc = DateTime.UtcNow.AddYears(-3);
        largeOld = new MoveFixture("asp10-largeold");
        foreach (var (name, bytes) in OldFiles)
        {
            largeOld.File(name, bytes, seed: name.Length);
            largeOld.SetModified(name, threeYearsAgoUtc);
        }
    }

    [ClassCleanup]
    public static void RemoveTheLargeAndOldFiles() => largeOld?.Dispose();

    private static AutomationElement OpenLargeOld(AppSession app)
    {
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => OldFiles.All(f => Table.ControlFor(view, "LargeOldSelect", largeOld!.Sub(f.Name)) is not null), Ui.ElementTimeout),
            "precondition: not every fixture file has a LargeOldSelect row: " + Table.Dump(view, "LargeOldSelect"));
        return view;
    }

    // Contract "Large and old": the table has the header buttons Move, Name, Folder, Size, Last modified and Type, left to
    // right. Each row holds a LargeOldSelect check box, enabled and not ticked (none is ticked at all), and its automation
    // name its cells: the name, the full folder, ByteUnits.Format(size), the short date of the modification and the type
    // group (Text.TypeArchive); no hint (root (a)). MoveLargeOld is disabled with nothing ticked.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeOldIsATableWithTheContractColumnsAndNothingTicked()
    {
        using var app = AppSession.Start("--scan", largeOld!.Path);
        var view = OpenLargeOld(app);
        Table.AssertColumns(view, 1, Text.ColumnMove, Text.ColumnName, Text.ColumnFolder, Text.ColumnSize, Text.ColumnLastModified, Text.ColumnType);

        foreach (var (name, bytes) in OldFiles)
        {
            var path = largeOld.Sub(name);
            Assert.AreEqual(bytes, MoveFixture.AllocatedBytes(path), "precondition: allocated equals the length for " + name);
            var box = Table.ControlFor(view, "LargeOldSelect", path)!;
            Assert.AreEqual(ControlType.CheckBox, box.Current.ControlType, name + ": LargeOldSelect is a check box");
            Assert.IsTrue(box.Current.IsEnabled, name + ": the box of a movable row is disabled");
            var row = Table.RowName(box);
            foreach (var cell in new[] { name, largeOld.Path, ByteUnits.Format(bytes), Table.ShortDate(threeYearsAgoUtc), Text.TypeArchive })
            {
                Assert.IsTrue(row.Contains(cell, StringComparison.OrdinalIgnoreCase), $"the row of {name} does not hold \"{cell}\": \"{row}\"");
            }

            Assert.IsNull(Table.HintIn(row), $"the movable row of {name} carries a hint: \"{row}\"");
        }

        var ticked = Table.Controls(view, "LargeOldSelect").Where(b => Table.State(b) != ToggleState.Off).Select(Table.RowName).ToList();
        Assert.AreEqual(0, ticked.Count, "ticked when the view opens: " + string.Join(" | ", ticked));
        Assert.IsFalse(AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout).Current.IsEnabled, "MoveLargeOld is enabled with nothing ticked");
    }

    // Contract "The table": Size sorts the rows by size, Name by name, and a second press reverses each.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeOldSortsBySizeAndByNameAndPressingAgainReverses()
    {
        using var app = AppSession.Start("--scan", largeOld!.Path);
        var view = OpenLargeOld(app);
        var names = OldFiles.Select(f => f.Name).ToArray();
        var sizes = OldFiles.ToDictionary(f => f.Name, f => f.Bytes, StringComparer.OrdinalIgnoreCase);

        AssertSortAndReverse(view, "LargeOldSelect", Text.ColumnSize, names, n => sizes[n]);
        AssertSortAndReverse(view, "LargeOldSelect", Text.ColumnName, names, n => n.ToUpperInvariant());
    }

    // Contract "Large and old" with the v1 additions' group 4: a display-only row (a file in %LOCALAPPDATA%, outside the
    // allowed roots, shown with Show system items) has its LargeOldSelect box disabled and unticked, a hint in its row
    // (the Name cell), and cannot be ticked.
    [TestMethod]
    [TestCategory("Console")]
    public void ADisplayOnlyLargeOldRowIsDisabledWithItsHint()
    {
        using var fixture = new Fixture("ui-asp10-displayonly", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        fixture.File("system-old.zip", 101 * MB);
        fixture.SetModified("system-old.zip", threeYearsAgoUtc);
        var path = fixture.Sub("system-old.zip");

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var toggle = Ui.ShowSystemItems(app);
        if (toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }

        var view = Ui.FindAnywhere(app, "LargeOldView", Ui.ElementTimeout);
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (box = Table.ControlFor(view, "LargeOldSelect", path)) is not null, Ui.ElementTimeout),
            "no LargeOldSelect row for the display-only file with Show system items on: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(box!.Current.IsEnabled, "the display-only row's box is enabled: " + Table.RowName(box));
        Assert.AreEqual(ToggleState.Off, Table.State(box), "the display-only row is ticked");
        Assert.IsNotNull(Table.HintIn(Table.RowName(box)), $"the display-only row carries no hint in its row: \"{Table.RowName(box)}\"");
        Table.TryToTick(box);
        Assert.IsFalse(AppSession.WaitUntil(() => Table.ControlFor(view, "LargeOldSelect", path) is { } b && Table.State(b) == ToggleState.On, Ui.NeverWatch), "the display-only row was ticked");
        Assert.IsFalse(AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout).Current.IsEnabled, "MoveLargeOld is enabled with only a display-only row");
    }

    // ---- Temporary files -------------------------------------------------------------------------------------------------

    // Contract "Temporary files": the table has the header buttons Move, Name, Size, Last changed and Note, left to right.
    // The test's own file in this user's %LOCALAPPDATA%\Temp (every time set ten days back) is a row whose TempItem check
    // box is enabled and not ticked, and whose automation name holds its name, ByteUnits.Format(100 KB) and the short date
    // of its last change; no TempItem is ticked; the Windows Temp row is disabled, with Text.HintSystemWide in its Note.
    [TestMethod]
    [TestCategory("Console")]
    public void TemporaryFilesIsATableWithTheContractColumnsAndNothingTicked()
    {
        var name = "LeanUiTemp-asp10-" + Guid.NewGuid().ToString("N")[..8] + ".bin";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", name);
        var oldUtc = DateTime.UtcNow.AddDays(-10);
        File.WriteAllBytes(path, Enumerable.Range(0, (int)(100 * KB)).Select(i => (byte)(i * 7)).ToArray());
        File.SetCreationTimeUtc(path, oldUtc);
        File.SetLastWriteTimeUtc(path, oldUtc);
        File.SetLastAccessTimeUtc(path, oldUtc);
        try
        {
            using var app = AppSession.Start();
            var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
            AutomationElement? box = null;
            Assert.IsTrue(AppSession.WaitUntil(() => (box = Table.ControlWith(view, "TempItem", name)) is not null, TimeSpan.FromSeconds(90)),
                $"no TempItem row for {name}: {Table.Dump(view, "TempItem")}");
            Table.AssertColumns(view, 1, Text.ColumnMove, Text.ColumnName, Text.ColumnSize, Text.ColumnLastChanged, Text.ColumnNote);

            Assert.AreEqual(ControlType.CheckBox, box!.Current.ControlType, "TempItem is a check box");
            Assert.IsTrue(box.Current.IsEnabled, "the old file's box is disabled: " + Table.RowName(box));
            var row = Table.RowName(box);
            foreach (var cell in new[] { name, ByteUnits.Format(100 * KB), Table.ShortDate(oldUtc) })
            {
                Assert.IsTrue(row.Contains(cell, StringComparison.OrdinalIgnoreCase), $"the row of {name} does not hold \"{cell}\": \"{row}\"");
            }

            var ticked = Table.Controls(view, "TempItem").Where(b => Table.State(b) != ToggleState.Off).Select(Table.RowName).ToList();
            Assert.AreEqual(0, ticked.Count, "ticked when the view opens: " + string.Join(" | ", ticked));

            var system = Table.Controls(view, "TempItem").Where(b => Table.RowName(b).Contains(Text.HintSystemWide, StringComparison.Ordinal)).ToList();
            Assert.AreEqual(1, system.Count, $"expected one row with \"{Text.HintSystemWide}\" in its Note (Windows Temp): {Table.Dump(view, "TempItem")}");
            Assert.IsFalse(system[0].Current.IsEnabled, "the Windows Temp row's box is enabled: " + Table.RowName(system[0]));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---- leftovers ---------------------------------------------------------------------------------------------------------

    // Contract "Leftovers": with the leftovers of the test's own gone app offered (an HKCU entry, removed by the test, as in
    // ReturnToOverviewTests' watch test), the leftovers table has the header buttons Move, Folder, Size and Note, left to right;
    // each leftover's LeftoverItem box is a check box, not ticked, in a row whose automation name holds the full folder and
    // ByteUnits.Format of its one 64 KB file. MoveLeftovers is never pressed.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void LeftoversAreATableWithTheContractColumnsAndNothingTicked()
    {
        using var gone = new GoneApp();
        using var app = AppSession.Start();
        var view = gone.OfferLeftovers(app);

        var first = Table.ControlWith(view, "LeftoverItem", gone.Leftover)!;
        var table = TableHolding(first, Text.ColumnFolder);
        Table.AssertColumns(table, 1, Text.ColumnMove, Text.ColumnFolder, Text.ColumnSize, Text.ColumnNote);
        foreach (var folder in new[] { gone.Leftover, gone.Install })
        {
            var box = Table.ControlWith(view, "LeftoverItem", folder)!;
            Assert.AreEqual(ControlType.CheckBox, box.Current.ControlType, "LeftoverItem is a check box");
            Assert.AreEqual(ToggleState.Off, Table.State(box), "a leftover is ticked: " + Table.RowName(box));
            Assert.IsTrue(Table.RowName(box).Contains(ByteUnits.Format(GoneApp.FileBytes), StringComparison.Ordinal),
                $"the row of {folder} does not hold its size {ByteUnits.Format(GoneApp.FileBytes)}: \"{Table.RowName(box)}\"");
        }

        var ticked = Table.Controls(view, "LeftoverItem").Where(b => Table.State(b) != ToggleState.Off).Select(Table.RowName).ToList();
        Assert.AreEqual(0, ticked.Count, "ticked when offered: " + string.Join(" | ", ticked));
        Ui.BackToOverview(app, "UninstallView");
    }

    /// <summary>The nearest ancestor of <paramref name="control"/>'s row that holds a column header <paramref name="column"/>:
    /// its table (a view may hold more than one list).</summary>
    private static AutomationElement TableHolding(AutomationElement control, string column)
    {
        var node = Table.RowOf(control) ?? control;
        for (var level = 0; level < 12 && node is not null; level++)
        {
            if (Table.Headers(node, column).Count > 0)
            {
                return node;
            }

            node = TreeWalker.ControlViewWalker.GetParent(node);
        }

        Assert.Fail($"no column header \"{column}\" above {Ui.Describe(control)}");
        return null!;
    }

    // ---- History -----------------------------------------------------------------------------------------------------------

    // Contract "History": entries are rows with the header buttons Item, Folder, Result and Reason, left to right. From the
    // test's own journal (one session: a trashed entry whose file does not exist, and a skipped one with a reason): the
    // trashed row holds its name, its full folder and Text.ResultTrashed, no reason; the skipped row its name, folder,
    // Text.ResultSkipped and the reason. The only PutBackEntry (never pressed) is in the trashed entry's row.
    [TestMethod]
    [TestCategory("Console")]
    public void HistoryRowsHaveItemFolderResultAndReasonWithPutBackInTheRow()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-asp10-history-" + Guid.NewGuid().ToString("N")[..8]);
        var trashed = Path.Combine(root, "was-here", "trashed-item.bin");
        var skipped = Path.Combine(root, "in-use", "skipped-item.bin");
        const string reason = "uitest reason";
        var now = DateTimeOffset.UtcNow;
        using var backup = new JournalBackup();
        JournalBackup.Write(
        [
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-asp10", now.AddHours(-2), "map", trashed, "trashed", bytes: 4096),
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-asp10", now.AddHours(-2).AddSeconds(1), "map", skipped, "skipped", reason: reason),
        ]);

        using var app = AppSession.Start();
        var history = Ui.OpenView(app, "History", "HistoryView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.LeafRowNames(history).Count(n => Table.Holds(n, trashed) || Table.Holds(n, skipped)) == 2, Ui.ElementTimeout),
            "precondition: History does not show the two entries as rows: [" + string.Join(" | ", Table.LeafRowNames(history)) + "]");
        Table.AssertColumns(history, 1, Text.ColumnItem, Text.ColumnFolder, Text.ColumnResult, Text.ColumnReason);

        var rows = Table.LeafRowNames(history);
        var dump = "[" + string.Join(" | ", rows) + "]";
        Assert.AreEqual(1, rows.Count(n => Table.IsHistoryRow(n, trashed, Text.ResultTrashed, null, reason)), $"no one row holds {trashed}, its folder and \"{Text.ResultTrashed}\" without a reason: {dump}");
        Assert.AreEqual(1, rows.Count(n => Table.IsHistoryRow(n, skipped, Text.ResultSkipped, reason)), $"no one row holds {skipped}, its folder, \"{Text.ResultSkipped}\" and \"{reason}\": {dump}");

        List<AutomationElement> putBacks = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (putBacks = AppSession.FindAll(history, "PutBackEntry")).Count == 1, Ui.ElementTimeout),
            $"expected one PutBackEntry (the trashed entry's), found {putBacks.Count}: {dump}");
        var owner = Table.RowName(putBacks[0]);
        Assert.IsTrue(Table.Holds(owner, trashed) && owner.Contains(Text.ResultTrashed, StringComparison.Ordinal),
            $"PutBackEntry is not in the trashed entry's row: its row is \"{owner}\" ({Ui.Describe(putBacks[0])})");
        Assert.IsFalse(Directory.Exists(root), "the test's paths must not exist");
    }
}
