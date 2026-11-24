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

/// <summary>ASP-10, fix round 2 (issue #22): what a row menu's Move, Enter and a header press do to the selection.
/// The row menu follows File Explorer: Move on an unticked row replaces the ticks with that row alone; Move on a ticked
/// row moves the ticked rows; Move is disabled on a display-only row. Enter on a Duplicates row chooses its Keep, but not
/// with the focus on a copy's Move box. Sorting never changes what is ticked or kept, read by name, not by position.
/// Duplicates: in every group exactly one copy is kept and unticked, however many Move boxes are ticked. Nothing is moved:
/// every confirmation opened here is cancelled and every fixture file is checked in place afterwards.</summary>
public sealed partial class TableListsTests
{
    // ---- row menu helpers -----------------------------------------------------------------------------------------------

    private static bool IsMenuItem(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType == ControlType.MenuItem;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The open row menu's MoveToRecycleBin item (a menu item: Duplicates' own MoveToRecycleBin button is not it), or null.</summary>
    private static AutomationElement? MenuMove(AppSession app) => Ui.FindAllAnywhere(app, "MoveToRecycleBin").FirstOrDefault(IsMenuItem);

    /// <summary>Focuses <paramref name="row"/>, opens its menu (Shift+F10) and returns its Move item, or null when none opened.</summary>
    private static AutomationElement? OpenRowMenu(AppSession app, AutomationElement row)
    {
        FocusRow(row);
        Keys.PressWithShift(Keys.F10);
        return AppSession.WaitFor(() => MenuMove(app), TimeSpan.FromSeconds(5));
    }

    private static void CloseMenu(AppSession app)
    {
        Keys.Press(Keys.Escape);
        AppSession.WaitUntil(() => MenuMove(app) is null, TimeSpan.FromSeconds(3));
    }

    private static AutomationElement RowFor(AutomationElement view, string automationId, string path)
    {
        var control = Table.ControlFor(view, automationId, path);
        Assert.IsNotNull(control, $"no {automationId} in a row of {path}: {Table.Dump(view, automationId)}");
        var row = Table.RowOf(control);
        Assert.IsNotNull(row, "no row holds the " + automationId + " of " + path);
        return row;
    }

    /// <summary>The ConfirmItem names of the open confirmation, once their number has held still for a second.</summary>
    private static List<string> ConfirmItemNames(AppSession app, out AutomationElement dialog)
    {
        var found = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        dialog = found;
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(found, "ConfirmItem").Count > 0, Ui.ElementTimeout), "the confirmation lists no ConfirmItem: " + Ui.Dump(found));
        Thread.Sleep(1000);
        return AppSession.FindAll(found, "ConfirmItem").Select(i => i.Current.Name ?? string.Empty).ToList();
    }

    /// <summary>The open confirmation lists exactly <paramref name="paths"/>: one ConfirmItem each, "&lt;path&gt;, about &lt;size&gt;",
    /// and no other.</summary>
    private static void AssertConfirmListsExactly(AppSession app, string what, IReadOnlyCollection<string> paths, Func<string, long> bytesOf)
    {
        var names = ConfirmItemNames(app, out var dialog);
        var dump = "[" + string.Join(" | ", names) + "]";
        foreach (var path in paths)
        {
            var expected = Text.ConfirmItem(path, ByteUnits.Format(bytesOf(path)));
            Assert.AreEqual(1, names.Count(n => n.StartsWith(expected, StringComparison.OrdinalIgnoreCase)), $"{what}: the confirmation should list \"{expected}\" once: {dump}");
        }

        Assert.AreEqual(paths.Count, names.Count, $"{what}: the confirmation should list exactly [{string.Join(" | ", paths)}]: {dump}");
        Assert.IsTrue(Ui.AnyNameContains(dialog, Text.ConfirmMoveTitle(paths.Count)), $"{what}: no title \"{Text.ConfirmMoveTitle(paths.Count)}\": {Ui.Dump(dialog)}");
    }

    private static void CancelConfirm(AppSession app)
    {
        if (app.FindInAnyWindow("ConfirmMove") is null)
        {
            return;
        }

        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
    }

    /// <summary>A cancelled confirmation keeps the move controls off until its dialog has really ended, which is after the close fade
    /// (the dialog is gone from the UI Automation tree when the fade starts): wait for the view's Move button, which the ticked rows
    /// enable, before a tick, a row menu or a click needs the controls.</summary>
    private static void WaitForMoveControls(AutomationElement view, string moveId)
    {
        var button = AppSession.Find(view, moveId, Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => { try { return button.Current.IsEnabled; } catch (ElementNotAvailableException) { return false; } }, Ui.ElementTimeout),
            "the move controls never came back after the cancelled confirmation");
    }

    private static void AssertAllInPlace(IReadOnlyDictionary<string, (ulong Volume, UInt128 FileId)> identities)
    {
        foreach (var (path, identity) in identities)
        {
            Assert.IsTrue(File.Exists(path), path + " was moved");
            Assert.AreEqual(identity, MoveFixture.Identity(path), path + " is not the same file");
            Assert.IsNull(RecycleBin.Holding(path), path + " is in the Recycle Bin");
        }
    }

    // ---- row menu: Large and old ----------------------------------------------------------------------------------------

    // Row menu rule (fix round 2, like File Explorer): alpha and beta ticked, the menu opened on the unticked gamma, Move:
    // the confirmation lists exactly gamma, and (cancelled) only gamma is ticked, alpha and beta are not. Then alpha is
    // ticked again (alpha and gamma ticked, beta not) and the menu opened on the ticked alpha: Move lists exactly alpha
    // and gamma, the ticked rows, and the ticks stay. Every confirmation is cancelled; nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeOldRowMenuMoveOnAnUntickedRowTakesThatRowAloneAndOnATickedRowTheTickedRows()
    {
        var (a, b, c) = (largeOld!.Sub("alpha-old.zip"), largeOld.Sub("beta-old.zip"), largeOld.Sub("gamma-old.zip"));
        var bytes = OldFiles.ToDictionary(f => largeOld.Sub(f.Name), f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var identities = bytes.Keys.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", largeOld.Path);
        var view = OpenLargeOld(app);
        AutomationElement? Box(string path) => Table.ControlFor(view, "LargeOldSelect", path);
        bool IsTicked(string path) => Box(path) is { } box && Table.State(box) == ToggleState.On;

        Table.Tick(Box(a)!, () => Box(a), "alpha");
        Table.Tick(Box(b)!, () => Box(b), "beta");
        var move = OpenRowMenu(app, RowFor(view, "LargeOldSelect", c));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on gamma's row");
        Assert.IsTrue(move.Current.IsEnabled, "the row menu's Move is disabled on the movable gamma");
        Ui.Click(move);
        try
        {
            AssertConfirmListsExactly(app, "menu Move on the unticked gamma, with alpha and beta ticked", [c], p => bytes[p]);
        }
        finally
        {
            CancelConfirm(app);
        }

        Assert.IsTrue(AppSession.WaitUntil(() => IsTicked(c) && !IsTicked(a) && !IsTicked(b), Ui.ElementTimeout),
            "after the menu's Move on the unticked gamma, only gamma should be ticked: " + Table.Dump(view, "LargeOldSelect"));

        Table.Tick(Box(a)!, () => Box(a), "alpha again");
        // A cancelled confirmation holds the move until its dialog has really ended (the move guard is never released early, so
        // a Put back started meanwhile stays guarded; ASP-10 review round 2): wait for the move controls before the next menu.
        var moveButton = Ui.FindAnywhere(app, "MoveLargeOld", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => moveButton.Current.IsEnabled, Ui.ElementTimeout),
            "the move controls never came back after the cancelled confirmation");
        move = OpenRowMenu(app, RowFor(view, "LargeOldSelect", a));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on alpha's row");
        Assert.IsTrue(move.Current.IsEnabled, "the row menu's Move is disabled on the ticked alpha");
        Ui.Click(move);
        try
        {
            AssertConfirmListsExactly(app, "menu Move on the ticked alpha, with alpha and gamma ticked", [a, c], p => bytes[p]);
        }
        finally
        {
            CancelConfirm(app);
        }

        Assert.IsTrue(AppSession.WaitUntil(() => IsTicked(a) && IsTicked(c) && !IsTicked(b), Ui.ElementTimeout),
            "the menu's Move on a ticked row changed the ticks: " + Table.Dump(view, "LargeOldSelect"));
        AssertAllInPlace(identities);
    }

    // Row menu rule: on a display-only row (inside a folder the user protects, shown with Show system items) the menu's Move
    // is disabled, also while another row is ticked; on the unticked movable row beside it, it is enabled. Nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void LargeOldRowMenuMoveIsDisabledOnADisplayOnlyRowEvenWithRowsTicked()
    {
        using var settings = new SettingsBackup();
        using var fixture = new MoveFixture("asp10-menu-display");
        var guarded = fixture.File(@"guarded\guarded-old.bin", 101 * MB, seed: 51);
        var free = fixture.File(@"free\free-old.bin", 101 * MB, seed: 52);
        var other = fixture.File(@"free\other-old.bin", 101 * MB, seed: 53);
        foreach (var name in new[] { @"guarded\guarded-old.bin", @"free\free-old.bin", @"free\other-old.bin" })
        {
            fixture.SetModified(name, threeYearsAgoUtc);
        }

        SettingsBackup.WriteProtectedFolders(fixture.Sub("guarded"));
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var toggle = Ui.ShowSystemItems(app);
        if (toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }

        var view = Ui.FindAnywhere(app, "LargeOldView", Ui.ElementTimeout);
        AutomationElement? Box(string path) => Table.ControlFor(view, "LargeOldSelect", path);
        Assert.IsTrue(AppSession.WaitUntil(() => Box(guarded) is not null && Box(free) is not null && Box(other) is not null, Ui.ElementTimeout),
            "precondition: rows for the protected and the two free files: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(Box(guarded)!.Current.IsEnabled, "precondition: the protected row's box is disabled: " + Table.RowName(Box(guarded)!));

        Table.Tick(Box(free)!, () => Box(free), "free-old.bin");
        var move = OpenRowMenu(app, RowFor(view, "LargeOldSelect", guarded));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on the display-only row");
        try
        {
            Assert.IsFalse(move.Current.IsEnabled, "the row menu's Move is enabled on a display-only row (with free-old.bin ticked)");
        }
        finally
        {
            CloseMenu(app);
        }

        move = OpenRowMenu(app, RowFor(view, "LargeOldSelect", other));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on other-old.bin's row");
        try
        {
            Assert.IsTrue(move.Current.IsEnabled, "the row menu's Move is disabled on the unticked movable other-old.bin");
        }
        finally
        {
            CloseMenu(app);
        }

        Assert.IsTrue(Box(free) is { } f && Table.State(f) == ToggleState.On, "free-old.bin lost its tick: " + Table.Dump(view, "LargeOldSelect"));
        Assert.AreEqual(ToggleState.Off, Table.State(Box(guarded)!), "the display-only row was ticked");
        Assert.IsTrue(File.Exists(guarded) && File.Exists(free) && File.Exists(other), "a fixture file is gone");
    }

    // ---- row menu: Duplicates -------------------------------------------------------------------------------------------

    // Row menu rule in Duplicates (when its rows have a row menu; inconclusive when Shift+F10 opens none): copy-a and
    // pair-long ticked, the menu opened on the unticked copy-c, Move: the confirmation lists exactly copy-c, and (cancelled)
    // only copy-c is ticked in the view, the Keeps unchanged. Then copy-a ticked again and the menu opened on it: Move lists
    // exactly copy-a and copy-c. On the kept copy-b's row the menu's Move is disabled.
    [TestMethod]
    [TestCategory("Console")]
    public void DuplicatesRowMenuMoveOnAnUntickedRowTakesThatRowAloneAndOnATickedRowTheTickedRows()
    {
        using var dups = new Dups("asp10-dupmenu", withPair: true);
        var identities = dups.All.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);
        var (a, b, c, pairLong, pairShort) = (dups.A, dups.B, dups.C, dups.PairLong!, dups.PairShort!);
        bool OnlyTicked(params string[] ticked) => dups.All.All(p => Ticked(view, p) == ticked.Contains(p, StringComparer.OrdinalIgnoreCase));

        Table.Tick(MoveOf(view, a), () => Move(view, a), "copy-a");
        Table.Tick(MoveOf(view, pairLong), () => Move(view, pairLong), "pair-long");
        var move = OpenRowMenu(app, RowFor(view, "DuplicateCopy", c));
        if (move is null)
        {
            Assert.Inconclusive("Duplicates rows have no row menu here: Shift+F10 on copy-c's row opened no MoveToRecycleBin menu item");
            return;
        }

        Assert.IsTrue(move.Current.IsEnabled, "the row menu's Move is disabled on the movable copy-c: " + State(view));
        Ui.Click(move);
        try
        {
            AssertConfirmListsExactly(app, "menu Move on the unticked copy-c, with copy-a and pair-long ticked", [c], dups.BytesOf);
        }
        finally
        {
            CancelConfirm(app);
        }

        WaitForMoveControls(view, "MoveToRecycleBin");
        Assert.IsTrue(AppSession.WaitUntil(() => OnlyTicked(c), Ui.ElementTimeout), "after the menu's Move on the unticked copy-c, only copy-c should be ticked: " + State(view));
        Assert.IsTrue(Chosen(view, b) && Chosen(view, pairShort), "the menu's Move changed a Keep: " + State(view));

        Table.Tick(MoveOf(view, a), () => Move(view, a), "copy-a again");
        move = OpenRowMenu(app, RowFor(view, "DuplicateCopy", a));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on copy-a's row");
        Assert.IsTrue(move.Current.IsEnabled, "the row menu's Move is disabled on the ticked copy-a");
        Ui.Click(move);
        try
        {
            AssertConfirmListsExactly(app, "menu Move on the ticked copy-a, with copy-a and copy-c ticked", [a, c], dups.BytesOf);
        }
        finally
        {
            CancelConfirm(app);
        }

        WaitForMoveControls(view, "MoveToRecycleBin");
        Assert.IsTrue(AppSession.WaitUntil(() => OnlyTicked(a, c), Ui.ElementTimeout), "the menu's Move on a ticked row changed the ticks: " + State(view));

        move = OpenRowMenu(app, RowFor(view, "DuplicateCopy", b));
        Assert.IsNotNull(move, "no row menu with MoveToRecycleBin on the kept copy-b's row");
        try
        {
            Assert.IsFalse(move.Current.IsEnabled, "the row menu's Move is enabled on the kept copy-b");
        }
        finally
        {
            CloseMenu(app);
        }

        AssertAllInPlace(identities);
    }

    // ---- Enter in Duplicates --------------------------------------------------------------------------------------------

    // Contract "Keyboard": Enter on a Duplicates row selects Keep, but the focus on a copy's Move box is not "on the row":
    // Enter there leaves the kept copy as it is (copy-b), unticked or ticked, and copy-a's Move stays enabled. Enter never
    // opens the confirmation.
    [TestMethod]
    [TestCategory("Console")]
    public void EnterOnAFocusedMoveBoxDoesNotChangeTheKeptCopy()
    {
        using var dups = new Dups("asp10-dupenterbox", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);
        var (a, b) = (dups.A, dups.B);

        foreach (var ticked in new[] { false, true })
        {
            if (ticked)
            {
                Table.Tick(MoveOf(view, a), () => Move(view, a), "copy-a");
            }

            Ui.Focus(MoveOf(view, a));
            Keys.Press(Keys.Enter);
            Assert.IsFalse(AppSession.WaitUntil(() => !Chosen(view, b) || Chosen(view, a), Ui.NeverWatch),
                $"Enter on copy-a's focused Move box ({(ticked ? "ticked" : "unticked")}) changed the kept copy: " + State(view));
            Assert.IsTrue(MoveEnabled(view, a), "copy-a's Move became disabled after Enter on it: " + State(view));
            Assert.IsNull(app.FindInAnyWindow("ConfirmMove"), "Enter on a Move box opened the confirmation");
        }
    }

    // Contract "Keyboard": Enter on a Duplicates row selects Keep. With the focus on copy-c's Keep radio, Enter chooses
    // copy-c (its Move unticked and disabled, copy-b's Move given back); with the focus on copy-a's row, Enter chooses copy-a.
    // Focusing the radio alone does not choose it (precondition, so the Enter is what chose it).
    [TestMethod]
    [TestCategory("Console")]
    public void EnterOnAFocusedKeepRadioOrRowChoosesThatKeep()
    {
        using var dups = new Dups("asp10-dupenterkeep", withPair: false);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 1);
        var (a, b, c) = (dups.A, dups.B, dups.C);

        Ui.Focus(KeepOf(view, c));
        Assert.IsFalse(AppSession.WaitUntil(() => Chosen(view, c), TimeSpan.FromSeconds(1)), "precondition: focusing copy-c's Keep radio already chose it: " + State(view));
        Keys.Press(Keys.Enter);
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, c) && !Chosen(view, b) && !Chosen(view, a), TimeSpan.FromSeconds(3)),
            "Enter on copy-c's focused Keep radio did not choose it: " + State(view));
        Assert.IsTrue(AppSession.WaitUntil(() => !MoveEnabled(view, c) && !Ticked(view, c) && MoveEnabled(view, b), TimeSpan.FromSeconds(3)),
            "after Enter chose copy-c, its Move is not disabled or copy-b's not given back: " + State(view));

        FocusRow(Table.RowOf(MoveOf(view, a))!);
        Keys.Press(Keys.Enter);
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, a) && !Chosen(view, b) && !Chosen(view, c), TimeSpan.FromSeconds(3)),
            "Enter on copy-a's focused row did not choose its Keep: " + State(view));
        Assert.IsNull(app.FindInAnyWindow("ConfirmMove"), "Enter opened the confirmation");
    }

    // ---- sorting keeps the selection --------------------------------------------------------------------------------------

    /// <summary>After a header press: while every row is listed, the selection never differs from <paramref name="holds"/>
    /// during a short watch, and holds at its end.</summary>
    private static void AssertHeldAfterPress(string what, Func<bool> allListed, Func<bool> holds, Func<string> state)
    {
        Assert.IsFalse(AppSession.WaitUntil(() => allListed() && !holds(), TimeSpan.FromSeconds(1.5)), $"{what}: the selection changed: {state()}");
        Assert.IsTrue(AppSession.WaitUntil(() => allListed() && holds(), Ui.ElementTimeout), $"{what}: the selection is not as before: {state()}");
    }

    // Contract "The table" with the selection rules: alpha and gamma ticked, beta not; pressing Move twice and Name twice
    // keeps exactly alpha and gamma ticked after every press, read by name.
    [TestMethod]
    [TestCategory("Console")]
    public void SortingLargeOldKeepsTheTicksByName()
    {
        using var app = AppSession.Start("--scan", largeOld!.Path);
        var view = OpenLargeOld(app);
        var expected = OldFiles.ToDictionary(f => largeOld.Sub(f.Name), f => f.Name != "beta-old.zip", StringComparer.OrdinalIgnoreCase);
        AutomationElement? Box(string path) => Table.ControlFor(view, "LargeOldSelect", path);
        foreach (var path in expected.Where(e => e.Value).Select(e => e.Key))
        {
            Table.Tick(Box(path)!, () => Box(path), Path.GetFileName(path));
        }

        bool AllListed() => expected.Keys.All(p => Box(p) is not null);
        bool Holds() => expected.All(e => Box(e.Key) is { } box && (Table.State(box) == ToggleState.On) == e.Value)
                        && Table.Controls(view, "LargeOldSelect").Count(x => Table.State(x) == ToggleState.On) == 2;
        foreach (var column in new[] { Text.ColumnMove, Text.ColumnName })
        {
            for (var press = 1; press <= 2; press++)
            {
                Table.Press(Table.Header(view, column));
                AssertHeldAfterPress($"after pressing \"{column}\" ({press})", AllListed, Holds, () => Table.Dump(view, "LargeOldSelect"));
            }
        }
    }

    // Contract "The table" and "Duplicates": in the triple copy-c's Keep chosen (not the suggested copy-b) and copy-a
    // ticked; in the pair pair-long's Keep chosen (not the suggested pair-short) and pair-short ticked. Pressing Keep,
    // Move and Name twice each, in each group's table, keeps after every press the same Keep per group and the same ticks.
    [TestMethod]
    [TestCategory("Console")]
    public void SortingDuplicatesKeepsTheTicksAndTheKeptCopyByName()
    {
        using var dups = new Dups("asp10-dupsortsel", withPair: true);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);
        var (a, c, pairLong, pairShort) = (dups.A, dups.C, dups.PairLong!, dups.PairShort!);

        Table.Choose(KeepOf(view, c));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, c) && !MoveEnabled(view, c), Ui.ElementTimeout), "copy-c's Keep did not take: " + State(view));
        Table.Choose(KeepOf(view, pairLong));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, pairLong) && !MoveEnabled(view, pairLong) && MoveEnabled(view, pairShort), Ui.ElementTimeout), "pair-long's Keep did not take: " + State(view));
        Table.Tick(MoveOf(view, a), () => Move(view, a), "copy-a");
        Table.Tick(MoveOf(view, pairShort), () => Move(view, pairShort), "pair-short");

        string[] kept = [c, pairLong];
        string[] ticked = [a, pairShort];
        bool AllListed() => dups.All.All(p => Move(view, p) is not null && Keep(view, p) is not null);
        bool Holds() => dups.All.All(p => Chosen(view, p) == kept.Contains(p) && Ticked(view, p) == ticked.Contains(p));
        foreach (var column in new[] { Text.ColumnKeep, Text.ColumnMove, Text.ColumnName })
        {
            for (var press = 1; press <= 2; press++)
            {
                var headers = Table.Headers(view, column);
                Assert.AreEqual(2, headers.Count, $"precondition: one \"{column}\" header per group's table");
                for (var group = 0; group < 2; group++)
                {
                    Table.Press(Table.Headers(view, column)[group]);
                    AssertHeldAfterPress($"after pressing \"{column}\" in table {group + 1} ({press})", AllListed, Holds, () => State(view));
                }
            }
        }
    }

    // ---- Duplicates: one kept, unticked copy per group --------------------------------------------------------------------

    // Contract "Duplicates" and the selection rules (the kept copy is never moved): ticking every Move box that can be
    // reached, in both groups, leaves in each group exactly one copy unticked, the kept one, whose box is disabled and
    // refuses a tick (pattern and Space). The same after choosing other Keeps (copy-a, pair-long; both were ticked) and
    // ticking all again. Move to Recycle Bin then lists 2 of the triple and 1 of the pair, never a whole group, and never
    // the kept copies; cancelled, nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void TickingEveryReachableMoveBoxLeavesExactlyOneKeptUntickedCopyPerGroup()
    {
        using var dups = new Dups("asp10-dupall", withPair: true);
        var identities = dups.All.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", dups.Fixture.Path);
        var view = FindDuplicates(app, dups, groups: 2);
        string[][] groups = [dups.Triple, [dups.PairLong!, dups.PairShort!]];

        void AssertOneKeptUnticked(string when, bool allReachableTicked)
        {
            foreach (var group in groups)
            {
                bool Good()
                {
                    var chosen = group.Where(p => Chosen(view, p)).ToList();
                    var unticked = group.Where(p => !Ticked(view, p)).ToList();
                    return chosen.Count == 1 && !Ticked(view, chosen[0]) && !MoveEnabled(view, chosen[0]) && unticked.Count >= 1
                           && (!allReachableTicked || (unticked.Count == 1 && unticked[0] == chosen[0]));
                }

                Assert.IsTrue(AppSession.WaitUntil(Good, Ui.ElementTimeout),
                    $"{when}: the group of {Path.GetFileName(group[0])} does not have exactly one kept copy, unticked and disabled{(allReachableTicked ? ", with every other copy ticked" : string.Empty)}: {State(view)}");
            }
        }

        void TickEveryReachable(string round)
        {
            foreach (var group in groups)
            {
                foreach (var path in group)
                {
                    if (Move(view, path) is { } box && box.Current.IsEnabled && Table.State(box) != ToggleState.On)
                    {
                        Table.TryToTick(box);
                        AppSession.WaitUntil(() => Ticked(view, path), TimeSpan.FromSeconds(2));
                    }

                    AssertOneKeptUnticked($"{round}, after ticking {Path.GetFileName(path)}", allReachableTicked: false);
                }

                var kept = group.First(p => Chosen(view, p));
                Table.TryToTick(MoveOf(view, kept));
                Assert.IsFalse(AppSession.WaitUntil(() => Ticked(view, kept), Ui.NeverWatch), $"{round}: the kept {kept} was ticked through its box: " + State(view));
                FocusRow(Table.RowOf(MoveOf(view, kept))!);
                Keys.Press(Keys.Space);
                Assert.IsFalse(AppSession.WaitUntil(() => Ticked(view, kept), Ui.NeverWatch), $"{round}: the kept {kept} was ticked with Space on its row: " + State(view));
            }

            AssertOneKeptUnticked(round + ", every reachable box ticked", allReachableTicked: true);
        }

        TickEveryReachable("suggested Keeps");
        Assert.IsTrue(Chosen(view, dups.B) && Chosen(view, dups.PairShort!), "the Keeps moved while ticking: " + State(view));

        Table.Choose(KeepOf(view, dups.A));
        Table.Choose(KeepOf(view, dups.PairLong!));
        Assert.IsTrue(AppSession.WaitUntil(() => Chosen(view, dups.A) && Chosen(view, dups.PairLong!), Ui.ElementTimeout), "the new Keeps did not take: " + State(view));
        AssertOneKeptUnticked("after choosing copy-a and pair-long as kept", allReachableTicked: false);
        TickEveryReachable("chosen Keeps");

        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move to Recycle Bin is disabled with copies ticked");
        Ui.Click(move);
        try
        {
            var names = ConfirmItemNames(app, out var dialog);
            var dump = "[" + string.Join(" | ", names) + "]";
            bool Listed(string path) => names.Any(n => n.StartsWith(Text.ConfirmItem(path, ByteUnits.Format(dups.BytesOf(path))), StringComparison.OrdinalIgnoreCase));
            foreach (var group in groups)
            {
                Assert.IsTrue(group.Count(Listed) < group.Length, $"the confirmation lists every copy of the group of {Path.GetFileName(group[0])}: {dump}");
            }

            Assert.IsFalse(Listed(dups.A) || Listed(dups.PairLong!), "the confirmation lists a kept copy: " + dump);
            Assert.IsTrue(Listed(dups.B) && Listed(dups.C) && Listed(dups.PairShort!), "the confirmation does not list the three ticked copies: " + dump);
            Assert.AreEqual(3, names.Count, "the confirmation should list exactly the three ticked copies: " + dump);
            Assert.IsTrue(Ui.AnyNameContains(dialog, Text.ConfirmMoveTitle(3)), $"no title \"{Text.ConfirmMoveTitle(3)}\": {Ui.Dump(dialog)}");
        }
        finally
        {
            CancelConfirm(app);
        }

        AssertAllInPlace(identities);
    }
}
