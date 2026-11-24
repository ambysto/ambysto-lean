using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan group 11, "UI contract for group 11": where moving starts (map tile menu, Large and old with tick boxes
/// (ASP-10, which replaced the extended selection), Duplicates with ticks and a chosen Keep), the confirmation (<c>ConfirmMove</c>: title, one <c>ConfirmItem</c> per item "about
/// &lt;size&gt;", "Keeping: …" for a duplicate, the hard-link note, the total, Cancel the default), the report
/// (<c>MoveReport</c>: docs/spec/07's two lines, <c>PutBackSession</c>), the moved tiles and rows gone, Put back byte for
/// byte, and History (<c>HistoryView</c>, <c>PutBackEntry</c>, <c>PutBackSessionHistory</c>, the Free limit line).
/// Real moves only of the test's own fixture items under <c>%USERPROFILE%\LeanUiMove-*</c> (root (a) of docs/spec/02);
/// each test checks its moved items in the Recycle Bin through its own reading of the <c>$I</c> records, and puts them
/// back or leaves them there. The bin is never emptied, and the Recycle Bin is never opened. Leftovers are not driven here:
/// that would need a real app uninstalled (Core tests cover the matcher).</summary>
[TestClass]
[DoNotParallelize]
public sealed partial class MoveAndPutBackTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DuplicatesTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a test watches for a change that must not happen.</summary>
    private static readonly TimeSpan NeverWatch = TimeSpan.FromSeconds(3);

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>Clicks a button-like element: Invoke, or Toggle for a check box. Used on Move and Put back only for the
    /// test's own fixture items; never on OpenRecycleBin, Reveal or UninstallInSettings.</summary>
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
            Assert.Fail($"{element.Current.AutomationId} cannot be invoked");
        }
    }

    private static AutomationElement FindAnywhere(AppSession app, string automationId, TimeSpan timeout)
    {
        var element = AppSession.WaitFor(() => app.FindInAnyWindow(automationId), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} in any window of the app within {timeout.TotalSeconds:F0} s");
        return element;
    }

    /// <summary>Every element with <paramref name="automationId"/> in any window of the app.</summary>
    private static List<AutomationElement> FindAllAnywhere(AppSession app, string automationId)
    {
        var all = new List<AutomationElement>();
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id)))
        {
            all.AddRange(AppSession.FindAll(top, automationId));
        }

        return all;
    }

    /// <summary>The Names of <paramref name="scope"/> and everything below it.</summary>
    private static List<string> Names(AutomationElement scope)
    {
        try
        {
            return scope.FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name ?? string.Empty).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    private static int CountNamesContaining(AutomationElement scope, string text) =>
        Names(scope).Count(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static bool AnyNameContains(AutomationElement scope, string text) => CountNamesContaining(scope, text) > 0;

    private static string Dump(AutomationElement scope) => "[" + string.Join(" | ", Names(scope).Where(n => n.Length > 0)) + "]";

    /// <summary>Waits until the scan started with --scan has finished and returns the map (<see cref="Ui.WaitForScan"/>).</summary>
    private static AutomationElement WaitForScan(AppSession app) => Ui.WaitForScan(app);

    private static string[] TileNames(AutomationElement map) => AppSession.FindAll(map, "Tile").Select(t => t.Current.Name).ToArray();

    private static bool HasTile(AutomationElement map, string name) => TileNames(map).Any(n => n.StartsWith(name + ", ", StringComparison.Ordinal));

    private static AutomationElement TileOf(AutomationElement map, string name)
    {
        AutomationElement? tile = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (tile = AppSession.FindAll(map, "Tile").FirstOrDefault(t => t.Current.Name.StartsWith(name + ", ", StringComparison.Ordinal))) is not null, ElementTimeout),
            $"no tile for {name}; tiles: [{string.Join(" | ", TileNames(map))}]");
        return tile!;
    }

    /// <summary>Focuses <paramref name="element"/>, opens its context menu (Shift+F10) and returns the menu item
    /// <paramref name="itemId"/>. The caller invokes it or presses Escape.</summary>
    private static AutomationElement MenuItemOf(AppSession app, AutomationElement element, string itemId)
    {
        element.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => AutomationElement.FocusedElement is { } f && (Automation.Compare(f, element) || f.Current.AutomationId == element.Current.AutomationId), TimeSpan.FromSeconds(3)),
            element.Current.Name + " did not take focus");
        Keys.PressWithShift(Keys.F10);
        var item = AppSession.WaitFor(() => app.FindInAnyWindow(itemId), TimeSpan.FromSeconds(5));
        Assert.IsNotNull(item, $"no {itemId} item in the context menu of {element.Current.Name}");
        return item;
    }

    /// <summary>Contract "Where moving starts": the tile menu's MoveToRecycleBin is enabled on a movable tile and opens
    /// the confirmation, which is returned.</summary>
    private static AutomationElement StartMoveFromTile(AppSession app, AutomationElement map, string name)
    {
        var tile = TileOf(map, name);
        var move = MenuItemOf(app, tile, "MoveToRecycleBin");
        Assert.AreEqual(Text.MoveToRecycleBin, move.Current.Name);
        if (!move.Current.IsEnabled)
        {
            Keys.Press(Keys.Escape);
            Assert.Fail($"Move to Recycle Bin… is disabled on the movable tile {name} (HelpText \"{tile.Current.HelpText}\")");
        }

        Click(move);
        return FindAnywhere(app, "ConfirmMove", ElementTimeout);
    }

    /// <summary>Contract "Confirmation": the title for <paramref name="count"/> items, the primary button
    /// <c>ConfirmMoveButton</c> (<c>Text.MoveToRecycleBinConfirm</c>), the close button <c>CancelMove</c>
    /// (<c>Text.Cancel</c>), <c>Text.CheckedAgainBeforeMove</c>, and one <c>ConfirmItem</c> per item.</summary>
    private static List<AutomationElement> AssertConfirmation(AppSession app, AutomationElement dialog, int count)
    {
        var title = Text.ConfirmMoveTitle(count);
        Assert.IsTrue(AppSession.WaitUntil(() => AnyNameContains(dialog, title), ElementTimeout), $"the confirmation has no title \"{title}\": {Dump(dialog)}");
        var confirm = FindAnywhere(app, "ConfirmMoveButton", ElementTimeout);
        var cancel = FindAnywhere(app, "CancelMove", ElementTimeout);
        Assert.AreEqual(Text.MoveToRecycleBinConfirm, confirm.Current.Name, "ConfirmMoveButton label");
        Assert.AreEqual(Text.Cancel, cancel.Current.Name, "CancelMove label");
        Assert.IsTrue(AnyNameContains(dialog, Text.CheckedAgainBeforeMove), $"the confirmation lacks \"{Text.CheckedAgainBeforeMove}\": {Dump(dialog)}");
        List<AutomationElement> items = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (items = AppSession.FindAll(dialog, "ConfirmItem")).Count == count, ElementTimeout),
            $"{items.Count} ConfirmItem texts, expected {count}: {Dump(dialog)}");
        return items;
    }

    /// <summary>The ConfirmItem for <paramref name="path"/>: its Name starts with <c>Text.ConfirmItem(path, size)</c>.</summary>
    private static void AssertConfirmItem(List<AutomationElement> items, string path, long bytes)
    {
        var expected = Text.ConfirmItem(path, ByteUnits.Format(bytes));
        Assert.IsTrue(items.Any(i => i.Current.Name.StartsWith(expected, StringComparison.OrdinalIgnoreCase)),
            $"no ConfirmItem \"{expected}\"; items: [{string.Join(" | ", items.Select(i => i.Current.Name))}]");
    }

    private static void AssertTotal(AppSession app, long bytes)
    {
        var total = FindAnywhere(app, "ConfirmTotal", ElementTimeout);
        var expected = Text.AboutTotal(ByteUnits.Format(bytes));
        Assert.IsTrue(total.Current.Name.Contains(expected, StringComparison.Ordinal), $"ConfirmTotal \"{total.Current.Name}\", expected \"{expected}\"");
    }

    private static void Cancel(AppSession app)
    {
        Click(FindAnywhere(app, "CancelMove", ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, ElementTimeout), "the confirmation stays open after Cancel");
    }

    /// <summary>Presses ConfirmMoveButton and returns the MoveReport once the session has finished (the report holds
    /// <c>Text.SpaceReturnsWhenEmptied</c> or a <c>ReportItem</c>).</summary>
    private static AutomationElement ConfirmAndWaitForReport(AppSession app)
    {
        Click(FindAnywhere(app, "ConfirmMoveButton", ElementTimeout));
        var report = FindAnywhere(app, "MoveReport", MoveTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => AnyNameContains(report, Text.SpaceReturnsWhenEmptied) || AppSession.FindAll(report, "ReportItem").Count > 0, MoveTimeout),
            "the move report did not complete: " + Dump(report));
        return report;
    }

    /// <summary>Contract "Report": <c>Text.MovedToRecycleBin(size)</c> with the sum of the trashed items' bytes, then
    /// <c>Text.SpaceReturnsWhenEmptied</c>; no ReportItem row (nothing skipped or lost); PutBackSession enabled.</summary>
    private static void AssertMovedReport(AppSession app, AutomationElement report, long? bytes)
    {
        Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "an item was skipped or lost: " + Dump(report));
        if (bytes is { } sum)
        {
            var moved = Text.MovedToRecycleBin(ByteUnits.Format(sum));
            Assert.IsTrue(AnyNameContains(report, moved), $"the report lacks \"{moved}\": {Dump(report)}");
        }

        Assert.IsTrue(AnyNameContains(report, Text.SpaceReturnsWhenEmptied), $"the report lacks \"{Text.SpaceReturnsWhenEmptied}\": {Dump(report)}");
        var putBack = FindAnywhere(app, "PutBackSession", ElementTimeout);
        Assert.AreEqual(Text.PutBackAllFromSession, putBack.Current.Name, "PutBackSession label");
        Assert.IsTrue(putBack.Current.IsEnabled, "PutBackSession is disabled although the session has a trashed item");
        Assert.IsNotNull(FindAnywhere(app, "CloseReport", ElementTimeout));
    }

    /// <summary>The item that was at <paramref name="path"/> is in this user's Recycle Bin with the same identity: gone
    /// from its place, a <c>$I</c> record names it, and its <c>$R</c> item has its file id (a move, not a copy).</summary>
    private static RecycleBin.Entry AssertInRecycleBin(string path, (ulong Volume, UInt128 FileId) identity)
    {
        RecycleBin.Entry? entry = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (entry = RecycleBin.Holding(path)) is not null, TimeSpan.FromSeconds(10)), path + " is not in the Recycle Bin");
        Assert.IsFalse(File.Exists(path) || Directory.Exists(path), path + " is still in its place");
        Assert.AreEqual(identity, MoveFixture.Identity(entry!.ItemPath), path + ": the item in the Recycle Bin is not the original (file id)");
        return entry;
    }

    private static void AssertInPlaceAndNotInRecycleBin(string path, (ulong Volume, UInt128 FileId) identity, string? hash)
    {
        Assert.IsTrue(File.Exists(path) || Directory.Exists(path), path + " was moved");
        Assert.AreEqual(identity, MoveFixture.Identity(path), path + " is not the same item");
        if (hash is not null)
        {
            Assert.AreEqual(hash, MoveFixture.Hash(path), path + " changed");
        }

        Assert.IsNull(RecycleBin.Holding(path), path + " is in the Recycle Bin");
    }

    /// <summary>The file at <paramref name="path"/> is back: same file id, same bytes, no longer in the Recycle Bin.</summary>
    private static void AssertPutBack(string path, (ulong Volume, UInt128 FileId) identity, string? hash, TimeSpan timeout)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(path) || Directory.Exists(path), timeout), path + " was not put back");
        Assert.AreEqual(identity, MoveFixture.Identity(path), path + ": put back as a different item (not one move back)");
        if (hash is not null)
        {
            Assert.AreEqual(hash, MoveFixture.Hash(path), path + ": the bytes put back differ");
        }

        Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(path) is null, TimeSpan.FromSeconds(5)), path + " is put back and still in the Recycle Bin");
    }

    /// <summary>The element <paramref name="automationId"/> that belongs to <paramref name="owner"/>: inside it, or in the
    /// nearest ancestor that holds exactly one such element.</summary>
    private static AutomationElement? OwnButton(AutomationElement owner, string automationId)
    {
        var node = owner;
        for (var level = 0; level < 4 && node is not null; level++)
        {
            var found = AppSession.FindAll(node, automationId);
            if (found.Count == 1)
            {
                return found[0];
            }

            if (found.Count > 1)
            {
                return null;
            }

            node = TreeWalker.ControlViewWalker.GetParent(node);
        }

        return null;
    }

    // ---- Map: confirmation ---------------------------------------------------------------------------------------------

    // Contract "Confirmation": Cancel moves nothing. The dialog shows the title for one item, ConfirmMoveButton
    // (Text.MoveToRecycleBinConfirm), CancelMove (Text.Cancel), Text.CheckedAgainBeforeMove; a file with one link has no
    // hard-link note. After Cancel the file is in place, unchanged, not in the Recycle Bin, its tile is still there and no
    // report appears.
    [TestMethod]
    [TestCategory("Console")]
    public void CancelInTheConfirmationMovesNothing()
    {
        using var fixture = new MoveFixture("cancel");
        var file = fixture.File("keep-me.bin", 100 * KB, seed: 1);
        fixture.File("other.bin", 60 * KB, seed: 2);
        var identity = MoveFixture.Identity(file);
        var hash = MoveFixture.Hash(file);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);
        var dialog = StartMoveFromTile(app, map, "keep-me.bin");
        var items = AssertConfirmation(app, dialog, 1);
        AssertConfirmItem(items, file, 100 * KB);
        Assert.IsFalse(AnyNameContains(dialog, Text.FreesNothingWhileLinked), "a file with one link has the hard-link note: " + Dump(dialog));
        Cancel(app);

        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, NeverWatch), "a move report appeared after Cancel");
        AssertInPlaceAndNotInRecycleBin(file, identity, hash);
        Assert.IsTrue(HasTile(map, "keep-me.bin"), "the tile is gone after Cancel: " + string.Join(" | ", TileNames(map)));
    }

    // Contract "Confirmation": one ConfirmItem per item, Text.ConfirmItem(path, size) ("<path>, about <size>"), and
    // ConfirmTotal holds Text.AboutTotal(size). A folder tile: its size is its files' (300 KB + 200 KB, whole clusters, so
    // allocated equals logical). Cancelled afterwards; nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void TheConfirmationListsTheItemWithAboutSizeAndTheTotal()
    {
        using var fixture = new MoveFixture("confirm");
        var one = fixture.File(@"alpha\one.bin", 300 * KB, seed: 3);
        var two = fixture.File(@"alpha\two.bin", 200 * KB, seed: 4);
        fixture.File("gamma.bin", 50 * KB, seed: 5);
        var alpha = fixture.Sub("alpha");
        var size = MoveFixture.AllocatedBytes(one) + MoveFixture.AllocatedBytes(two);
        Assert.AreEqual(500 * KB, size, "precondition: allocated equals the length (whole clusters)");
        var identity = MoveFixture.Identity(alpha);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);
        var dialog = StartMoveFromTile(app, map, "alpha");
        var items = AssertConfirmation(app, dialog, 1);
        AssertConfirmItem(items, alpha, size);
        AssertTotal(app, size);
        Cancel(app);
        AssertInPlaceAndNotInRecycleBin(alpha, identity, hash: null);
    }

    // Contract "Confirmation": the default button is the close button, so Enter does not move. Enter is pressed while the
    // dialog has the keyboard; afterwards the file is in place and not in the Recycle Bin, and no report appears.
    [TestMethod]
    [TestCategory("Console")]
    public void EnterInTheConfirmationDoesNotMove()
    {
        using var fixture = new MoveFixture("enter");
        var file = fixture.File("enter.bin", 100 * KB, seed: 6);
        fixture.File("other.bin", 60 * KB, seed: 7);
        var identity = MoveFixture.Identity(file);
        var hash = MoveFixture.Hash(file);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);
        var dialog = StartMoveFromTile(app, map, "enter.bin");
        AssertConfirmation(app, dialog, 1);
        System.Threading.Thread.Sleep(500); // the dialog's opening animation hands it the keyboard
        Keys.Press(Keys.Enter);

        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, NeverWatch), "Enter in the confirmation started a move");
        AssertInPlaceAndNotInRecycleBin(file, identity, hash);
        if (app.FindInAnyWindow("ConfirmMove") is not null)
        {
            Cancel(app);
        }

        Assert.IsTrue(HasTile(map, "enter.bin"), "the tile is gone after Enter");
    }

    // ---- Map: move, report, put back ----------------------------------------------------------------------------------

    // Contract "Run" and "Report", docs/spec/07: a confirmed move of a fixture file puts it in the Recycle Bin (the test's
    // $I reading: same file id, same bytes in its $R item); the report shows Text.MovedToRecycleBin(size) with the moved
    // bytes and Text.SpaceReturnsWhenEmptied, no ReportItem row, PutBackSession enabled; the moved tile disappears and the
    // other stays. The file is left in the Recycle Bin.
    [TestMethod]
    [TestCategory("Console")]
    public void AConfirmedMoveGoesToTheRecycleBinAndTheReportSaysSo()
    {
        using var fixture = new MoveFixture("move");
        var file = fixture.File("move-me.bin", 100 * KB, seed: 8);
        fixture.File("stay.bin", 60 * KB, seed: 9);
        Assert.AreEqual(100 * KB, MoveFixture.AllocatedBytes(file), "precondition: allocated equals the length");
        var identity = MoveFixture.Identity(file);
        var hash = MoveFixture.Hash(file);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);
        StartMoveFromTile(app, map, "move-me.bin");
        var report = ConfirmAndWaitForReport(app);
        AssertMovedReport(app, report, 100 * KB);

        var entry = AssertInRecycleBin(file, identity);
        Assert.AreEqual(hash, MoveFixture.Hash(entry.ItemPath), "the bytes in the Recycle Bin differ from the original's");
        Assert.IsTrue(AppSession.WaitUntil(() => !HasTile(map, "move-me.bin"), ElementTimeout), "the moved tile is still in the map: " + string.Join(" | ", TileNames(map)));
        Assert.IsTrue(HasTile(map, "stay.bin"), "the tile that was not moved is gone: " + string.Join(" | ", TileNames(map)));
    }

    // Contract "Report": PutBackSession brings the session's items back byte for byte, by one move each (same file ids:
    // never a copy); each item's ReportItem row then reads "<path>: Text.PutBackDone". A folder tile with a file inside
    // and one in a subfolder.
    [TestMethod]
    [TestCategory("Console")]
    public void PutBackSessionBringsTheFolderBackByteForByte()
    {
        using var fixture = new MoveFixture("putback");
        var one = fixture.File(@"alpha\one.bin", 300 * KB, seed: 10);
        var two = fixture.File(@"alpha\inner\two.bin", 200 * KB, seed: 11);
        fixture.File("stay.bin", 60 * KB, seed: 12);
        var alpha = fixture.Sub("alpha");
        var folderIdentity = MoveFixture.Identity(alpha);
        var files = new[] { one, two }.ToDictionary(p => p, p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);
        StartMoveFromTile(app, map, "alpha");
        var report = ConfirmAndWaitForReport(app);
        // The size line is checked on files elsewhere; a folder's measured bytes are the Kit's (plan, Kit defect 7).
        AssertMovedReport(app, report, bytes: null);
        AssertInRecycleBin(alpha, folderIdentity);

        Click(FindAnywhere(app, "PutBackSession", ElementTimeout));
        AssertPutBack(alpha, folderIdentity, hash: null, MoveTimeout);
        foreach (var (path, facts) in files)
        {
            AssertPutBack(path, facts.Identity, facts.Hash, ElementTimeout);
        }

        var done = alpha + ": " + Text.PutBackDone;
        Assert.IsTrue(AppSession.WaitUntil(() => FindAllAnywhere(app, "ReportItem").Any(r => string.Equals(r.Current.Name, done, StringComparison.OrdinalIgnoreCase)), ElementTimeout),
            $"no ReportItem \"{done}\"; rows: [{string.Join(" | ", FindAllAnywhere(app, "ReportItem").Select(r => r.Current.Name))}]");
    }

    // Contract "Where moving starts": on a display-only tile the control stays disabled, with the hint as now. The scanned
    // folder is in %LOCALAPPDATA% (inside AppData, not Temp: outside every allowed root, docs/spec/02 (a)), so its tiles
    // are display-only. Nothing is invoked.
    [TestMethod]
    [TestCategory("Console")]
    public void ADisplayOnlyTileCannotBeMoved()
    {
        using var fixture = new Fixture("ui-move-displayonly", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        fixture.File("system-ish.bin", 100 * KB);
        fixture.File(@"folder\inside.bin", 60 * KB);

        using var app = AppSession.Start("--scan", fixture.Path);
        // The map shows every item with its hint (ASP-14 moves "Show system items" into Large and old, the only list it changes).
        var map = WaitForScan(app);
        var tile = TileOf(map, "system-ish.bin");
        var path = fixture.Sub("system-ish.bin");
        var help = tile.Current.HelpText;
        Assert.IsTrue(help.StartsWith(path, StringComparison.OrdinalIgnoreCase) && help.Length > path.Length && !string.IsNullOrWhiteSpace(help[path.Length..]),
            $"precondition: the tile is display-only (HelpText path, then the hint), HelpText \"{help}\"");
        var move = MenuItemOf(app, tile, "MoveToRecycleBin");
        try
        {
            Assert.AreEqual(Text.MoveToRecycleBin, move.Current.Name);
            Assert.IsFalse(move.Current.IsEnabled, "Move to Recycle Bin… is enabled on a display-only tile");
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }

        Assert.IsTrue(File.Exists(path), "the display-only file is gone");
    }

    // Contract "Confirmation" and docs/spec/02: a file with more than one link has the line from HardLinkNote.For, Text.
    // FreesNothingWhileLinked, in the confirmation. Its other link is outside the scanned folder, so the scan sees one
    // name (plan Walker: such a link is found only at move time). Cancelled; nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void AHardLinkedFileIsNotedInTheConfirmation()
    {
        using var fixture = new MoveFixture("hardlink");
        var linked = fixture.File(@"scan\linked.bin", 100 * KB, seed: 13);
        fixture.File(@"scan\plain.bin", 60 * KB, seed: 14);
        fixture.HardLink(@"elsewhere\linked-too.bin", @"scan\linked.bin");
        var identity = MoveFixture.Identity(linked);

        using var app = AppSession.Start("--scan", fixture.Sub("scan"));
        var map = WaitForScan(app);
        var dialog = StartMoveFromTile(app, map, "linked.bin");
        var items = AssertConfirmation(app, dialog, 1);
        AssertConfirmItem(items, linked, 100 * KB);
        Assert.IsTrue(AnyNameContains(dialog, Text.FreesNothingWhileLinked), $"the confirmation lacks \"{Text.FreesNothingWhileLinked}\": {Dump(dialog)}");
        Cancel(app);
        AssertInPlaceAndNotInRecycleBin(linked, identity, hash: null);
    }

    // ---- Duplicates ----------------------------------------------------------------------------------------------------

    /// <summary>Three byte-identical 2 MB copies; copy-b is the newest, so the suggested keep (docs/spec/03).</summary>
    private static (string A, string B, string C) Duplicates(MoveFixture fixture)
    {
        var content = new byte[(int)(2 * MB)];
        new Random(11_0_3).NextBytes(content);
        var a = fixture.FileWithBytes(@"one\copy-a.dat", content);
        var b = fixture.FileWithBytes(@"two\copy-b.dat", content);
        var c = fixture.FileWithBytes(@"three\copy-c.dat", content);
        var now = DateTime.UtcNow;
        fixture.SetModified(@"one\copy-a.dat", now.AddDays(-3));
        fixture.SetModified(@"two\copy-b.dat", now.AddDays(-1));
        fixture.SetModified(@"three\copy-c.dat", now.AddDays(-2));
        return (a, b, c);
    }

    private static AutomationElement OpenDuplicatesAndFind(AppSession app)
    {
        Click(Ui.SidebarItem(app, "Duplicates"));
        var view = FindAnywhere(app, "DuplicatesView", ElementTimeout);
        Click(AppSession.Find(view, "FindDuplicates", ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateGroup").Count > 0, DuplicatesTimeout), "no DuplicateGroup appeared");
        return view;
    }

    /// <summary>The DuplicateCopy (Move) box in the row of <paramref name="path"/> (ASP-10: the row's automation name holds
    /// the name and the full folder), or null.</summary>
    private static AutomationElement? CopyOf(AutomationElement view, string path) => Table.ControlFor(view, "DuplicateCopy", path);

    private static void Tick(AutomationElement view, string path)
    {
        var copy = CopyOf(view, path);
        Assert.IsNotNull(copy, $"no DuplicateCopy for {path}; copies: {Table.Dump(view, "DuplicateCopy")}");
        Table.Tick(copy, () => CopyOf(view, path), path);
    }

    /// <summary>Chooses the Keep (DuplicateKeep) of <paramref name="path"/> and waits until it is the kept copy: chosen, its
    /// Move box disabled and not ticked (ASP-10).</summary>
    private static void ChooseKeep(AutomationElement view, string path)
    {
        var keep = Table.ControlFor(view, "DuplicateKeep", path);
        Assert.IsNotNull(keep, $"no DuplicateKeep for {path}; Keep: {Table.Dump(view, "DuplicateKeep")}");
        Table.Choose(keep);
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlFor(view, "DuplicateKeep", path) is { } k && Table.IsChosen(k)
                                                 && CopyOf(view, path) is { } c && !c.Current.IsEnabled && Table.State(c) == ToggleState.Off, TimeSpan.FromSeconds(3)),
            $"{path} did not become the kept copy: Move {Table.Dump(view, "DuplicateCopy")}, Keep {Table.Dump(view, "DuplicateKeep")}");
    }

    // Contract "Where moving starts" (Duplicates) and "Confirmation": MoveToRecycleBin is disabled with nothing ticked and
    // enabled with one copy ticked; the confirmation lists the ticked copy and "Keeping: <suggested keep>"; after the move
    // the ticked copy is in the Recycle Bin (same bytes), the kept copy and the other copy are untouched, and the moved
    // copy's check box is gone. The moved copy is left in the Recycle Bin.
    [TestMethod]
    [TestCategory("Console")]
    public void MovingATickedCopyKeepsTheSuggestedKeepAndSaysSo()
    {
        using var fixture = new MoveFixture("dups");
        var (a, b, c) = Duplicates(fixture);
        var facts = new[] { a, b, c }.ToDictionary(p => p, p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);

        using var app = AppSession.Start("--scan", fixture.Path);
        WaitForScan(app);
        var view = OpenDuplicatesAndFind(app);
        var move = AppSession.Find(view, "MoveToRecycleBin", ElementTimeout);
        Assert.IsFalse(move.Current.IsEnabled, "Move is enabled with nothing ticked");
        Tick(view, a);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move stays disabled with a copy ticked");
        Click(move);

        var dialog = FindAnywhere(app, "ConfirmMove", ElementTimeout);
        var items = AssertConfirmation(app, dialog, 1);
        AssertConfirmItem(items, a, 2 * MB);
        Assert.IsTrue(AnyNameContains(dialog, Text.Keeping(b)), $"the confirmation lacks \"{Text.Keeping(b)}\": {Dump(dialog)}");
        Assert.IsFalse(AnyNameContains(dialog, Text.Keeping(c)), "the confirmation keeps the copy that is not the suggested keep");
        AssertTotal(app, 2 * MB);

        var report = ConfirmAndWaitForReport(app);
        AssertMovedReport(app, report, 2 * MB);
        var entry = AssertInRecycleBin(a, facts[a].Identity);
        Assert.AreEqual(facts[a].Hash, MoveFixture.Hash(entry.ItemPath), "the bytes in the Recycle Bin differ");
        AssertInPlaceAndNotInRecycleBin(b, facts[b].Identity, facts[b].Hash);
        AssertInPlaceAndNotInRecycleBin(c, facts[c].Identity, facts[c].Hash);
        Assert.IsTrue(AppSession.WaitUntil(() => CopyOf(view, a) is null, ElementTimeout), "the moved copy is still listed");
    }

    // Contract "Where moving starts" (Duplicates), with ASP-10: the suggested keep can be moved once another copy is the
    // chosen Keep, and every ticked copy's kept copy is the chosen one. With copy-c's Keep chosen, ticking copy-a and copy-b
    // (the suggested keep) leaves copy-c: the confirmation lists two items, each with "Keeping: <copy-c>", the title for 2
    // and the total of both. Cancelled; nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void WithAnotherKeepChosenTheSuggestedKeepCanBeTickedAndTheChosenOneIsKept()
    {
        using var fixture = new MoveFixture("dups-keep");
        var (a, b, c) = Duplicates(fixture);
        var facts = new[] { a, b, c }.ToDictionary(p => p, p => MoveFixture.Identity(p), StringComparer.OrdinalIgnoreCase);

        using var app = AppSession.Start("--scan", fixture.Path);
        WaitForScan(app);
        var view = OpenDuplicatesAndFind(app);
        ChooseKeep(view, c);
        Tick(view, a);
        Tick(view, b);
        Click(AppSession.Find(view, "MoveToRecycleBin", ElementTimeout));

        var dialog = FindAnywhere(app, "ConfirmMove", ElementTimeout);
        var items = AssertConfirmation(app, dialog, 2);
        AssertConfirmItem(items, a, 2 * MB);
        AssertConfirmItem(items, b, 2 * MB);
        AssertTotal(app, 4 * MB);
        Assert.IsTrue(CountNamesContaining(dialog, Text.Keeping(c)) >= 2, $"each item should say \"{Text.Keeping(c)}\": {Dump(dialog)}");
        Assert.IsFalse(AnyNameContains(dialog, Text.Keeping(b)), "a ticked copy is named as the kept copy");
        Cancel(app);
        foreach (var path in new[] { a, b, c })
        {
            AssertInPlaceAndNotInRecycleBin(path, facts[path], hash: null);
        }
    }

    // ---- Large and old -------------------------------------------------------------------------------------------------

    private static readonly string[] BigNames = ["big-1.bin", "big-2.bin", "big-3.bin"];

    // Contract "Where moving starts" (Large and old), with ASP-10 (tick boxes replace the extended selection): every row
    // has a LargeOldSelect box, none ticked at first; MoveLargeOld (Text.MoveToRecycleBin) is enabled only while a row is
    // ticked and moves the ticked rows in one session: the confirmation lists both with their sizes and the total; both go
    // to the Recycle Bin; their rows disappear and the third row stays; PutBackSession brings both back byte for byte.
    [TestMethod]
    [TestCategory("Console")]
    public void TickedLargeOldRowsAreMovedTogetherAndPutBack()
    {
        using var fixture = new MoveFixture("largeold");
        var size = 101 * MB;
        var threeYearsAgo = DateTime.UtcNow.AddYears(-3);
        var paths = new string[BigNames.Length];
        for (var i = 0; i < BigNames.Length; i++)
        {
            paths[i] = fixture.File(BigNames[i], size, seed: 20 + i);
            fixture.SetModified(BigNames[i], threeYearsAgo);
        }

        Assert.IsTrue(paths.All(p => MoveFixture.AllocatedBytes(p) == size), "precondition: allocated equals the length");
        var facts = paths.ToDictionary(p => p, p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);
        string[] Rows(AutomationElement scope) => Table.Controls(scope, "LargeOldSelect").Select(Table.RowName).ToArray();
        AutomationElement? BoxOf(AutomationElement scope, string name) => Table.ControlFor(scope, "LargeOldSelect", fixture.Sub(name));
        bool HasRow(AutomationElement scope, string name) => BoxOf(scope, name) is not null;

        using var app = AppSession.Start("--scan", fixture.Path);
        WaitForScan(app);
        Click(Ui.SidebarItem(app, "LargeOld"));
        var view = FindAnywhere(app, "LargeOldView", ElementTimeout);
        var list = AppSession.Find(view, "LargeOldList", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => HasRow(list, "big-1.bin") && HasRow(list, "big-2.bin") && HasRow(list, "big-3.bin"), ElementTimeout),
            "rows: [" + string.Join(" | ", Rows(list)) + "]");

        var moveButton = AppSession.Find(view, "MoveLargeOld", ElementTimeout);
        Assert.AreEqual(Text.MoveToRecycleBin, moveButton.Current.Name, "MoveLargeOld label");
        Assert.IsFalse(moveButton.Current.IsEnabled, "MoveLargeOld is enabled with nothing ticked");
        foreach (var name in BigNames)
        {
            Assert.AreEqual(ToggleState.Off, Table.State(BoxOf(list, name)!), name + " is ticked at first");
        }

        foreach (var name in BigNames[..2])
        {
            Table.Tick(BoxOf(list, name)!, () => BoxOf(list, name), name);
        }

        Assert.AreEqual(ToggleState.Off, Table.State(BoxOf(list, BigNames[2])!), BigNames[2] + " was ticked with the others");
        Assert.IsTrue(AppSession.WaitUntil(() => moveButton.Current.IsEnabled, TimeSpan.FromSeconds(3)), "MoveLargeOld stays disabled with two rows ticked");
        Click(moveButton);
        var dialog = FindAnywhere(app, "ConfirmMove", ElementTimeout);
        var items = AssertConfirmation(app, dialog, 2);
        AssertConfirmItem(items, paths[0], size);
        AssertConfirmItem(items, paths[1], size);
        AssertTotal(app, 2 * size);

        var report = ConfirmAndWaitForReport(app);
        AssertMovedReport(app, report, 2 * size);
        AssertInRecycleBin(paths[0], facts[paths[0]].Identity);
        AssertInRecycleBin(paths[1], facts[paths[1]].Identity);
        AssertInPlaceAndNotInRecycleBin(paths[2], facts[paths[2]].Identity, hash: null);
        Assert.IsTrue(AppSession.WaitUntil(() => !HasRow(view, "big-1.bin") && !HasRow(view, "big-2.bin"), ElementTimeout), "the moved rows are still listed: [" + string.Join(" | ", Rows(view)) + "]");
        Assert.IsTrue(HasRow(view, "big-3.bin"), "the row that was not moved is gone");

        Click(FindAnywhere(app, "PutBackSession", ElementTimeout));
        AssertPutBack(paths[0], facts[paths[0]].Identity, facts[paths[0]].Hash, MoveTimeout);
        AssertPutBack(paths[1], facts[paths[1]].Identity, facts[paths[1]].Hash, MoveTimeout);
    }

    // ---- History -------------------------------------------------------------------------------------------------------

    /// <summary>The HistorySession elements in order, each with the entry rows that follow it (inside it or after it,
    /// before the next session). ASP-10: an entry is a table row (the innermost list items of the view), whose automation
    /// name joins its Item, Folder, Result and Reason cells.</summary>
    private static List<(AutomationElement Session, List<AutomationElement> Entries)> HistorySessions(AutomationElement view)
    {
        var entries = Table.LeafRows(view).Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        var condition = new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "HistorySession"),
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
        var sessions = new List<(AutomationElement Session, List<AutomationElement> Entries)>();
        foreach (AutomationElement element in view.FindAll(TreeScope.Descendants, condition))
        {
            if (element.Current.AutomationId == "HistorySession")
            {
                sessions.Add((element, []));
            }
            else if (sessions.Count > 0 && entries.Contains(Ui.Key(element)))
            {
                sessions[^1].Entries.Add(element);
            }
        }

        return sessions;
    }

    private static int SessionIndexHolding(List<(AutomationElement Session, List<AutomationElement> Entries)> sessions, string path) =>
        sessions.FindIndex(s => s.Entries.Any(e => Table.Holds(e.Current.Name, path)));

    /// <summary>The entry row of <paramref name="path"/> with <paramref name="result"/> (no reason in these tests), or null.</summary>
    private static AutomationElement? EntryOf(AutomationElement view, string path, string result) =>
        Table.LeafRows(view).FirstOrDefault(e => Table.IsHistoryRow(e.Current.Name, path, result, null));

    private static string HistoryDump(AutomationElement view) =>
        string.Join(" || ", HistorySessions(view).Select(s => s.Session.Current.Name + " { " + string.Join(" | ", s.Entries.Select(e => e.Current.Name)) + " }"));

    // Contract "History" (docs/spec/05, 06): after a map session (1 item) and a duplicates session (2 items), HistoryView
    // lists the sessions newest first, the duplicates one named "<local date and time>, <module>, 2 items", each entry a
    // row (ASP-10) holding its name, its full folder and Text.ResultTrashed; Free shows Text.HistoryFreeLimit. The
    // PutBackEntry in a row puts that entry back byte for byte (a row with Text.ResultRestored then shows it);
    // PutBackSessionHistory puts back the rest of that session. The map session's
    // file is left in the Recycle Bin.
    [TestMethod]
    [TestCategory("Console")]
    public void HistoryListsTheSessionsAndPutsBackAnEntryAndASession()
    {
        using var fixture = new MoveFixture("history");
        var solo = fixture.File("solo.bin", 100 * KB, seed: 30);
        var (a, b, c) = Duplicates(fixture);
        var facts = new[] { solo, a, b, c }.ToDictionary(p => p, p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);

        using var app = AppSession.Start("--scan", fixture.Path);
        var map = WaitForScan(app);

        // Session 1: the map.
        StartMoveFromTile(app, map, "solo.bin");
        AssertMovedReport(app, ConfirmAndWaitForReport(app), 100 * KB);
        AssertInRecycleBin(solo, facts[solo].Identity);
        Click(FindAnywhere(app, "CloseReport", ElementTimeout));

        // Session 2: duplicates, copy-a and copy-c ticked, copy-b kept.
        var view = OpenDuplicatesAndFind(app);
        Tick(view, a);
        Tick(view, c);
        Click(AppSession.Find(view, "MoveToRecycleBin", ElementTimeout));
        AssertConfirmation(app, FindAnywhere(app, "ConfirmMove", ElementTimeout), 2);
        AssertMovedReport(app, ConfirmAndWaitForReport(app), 4 * MB);
        AssertInRecycleBin(a, facts[a].Identity);
        AssertInRecycleBin(c, facts[c].Identity);
        Click(FindAnywhere(app, "CloseReport", ElementTimeout));
        Click(Ui.SidebarItem(app, "Overview"));

        Click(Ui.SidebarItem(app, "History"));
        var history = FindAnywhere(app, "HistoryView", ElementTimeout);
        Assert.IsNull(AppSession.TryFind(history, "BackToMap"), "HistoryView still has a BackToMap button (ASP-14 removes it)");
        Assert.IsTrue(AppSession.WaitUntil(() => AnyNameContains(history, Text.HistoryFreeLimit), ElementTimeout), $"Free history lacks \"{Text.HistoryFreeLimit}\": {Dump(history)}");

        List<(AutomationElement Session, List<AutomationElement> Entries)> sessions = [];
        Assert.IsTrue(AppSession.WaitUntil(() => SessionIndexHolding(sessions = HistorySessions(history), a) >= 0 && SessionIndexHolding(sessions, solo) >= 0, ElementTimeout),
            "the history does not list both sessions: " + HistoryDump(history));
        var duplicatesIndex = SessionIndexHolding(sessions, a);
        var mapIndex = SessionIndexHolding(sessions, solo);
        Assert.AreEqual(duplicatesIndex, SessionIndexHolding(sessions, c), "copy-a and copy-c are in different sessions");
        Assert.IsTrue(duplicatesIndex < mapIndex, $"sessions are not newest first: duplicates at {duplicatesIndex}, map at {mapIndex}");

        var sessionName = sessions[duplicatesIndex].Session.Current.Name;
        var today = DateTime.Now;
        Assert.IsTrue(sessionName.StartsWith(today.ToString("d", CultureInfo.CurrentCulture), StringComparison.Ordinal)
                      || sessionName.StartsWith(today.AddDays(-1).ToString("d", CultureInfo.CurrentCulture), StringComparison.Ordinal),
            $"the session name \"{sessionName}\" does not start with the local date");
        Assert.IsTrue(sessionName.EndsWith(", 2 items", StringComparison.Ordinal), $"the session name \"{sessionName}\" does not end with \", 2 items\"");
        Assert.IsTrue(sessionName.Contains("duplicates", StringComparison.OrdinalIgnoreCase), $"the session name \"{sessionName}\" does not name its module");
        foreach (var path in new[] { a, c })
        {
            Assert.IsNotNull(EntryOf(history, path, Text.ResultTrashed), $"no entry row of {path} with \"{Text.ResultTrashed}\": {HistoryDump(history)}");
        }

        Assert.IsNotNull(EntryOf(history, solo, Text.ResultTrashed), $"no entry row of {solo} with \"{Text.ResultTrashed}\": {HistoryDump(history)}");

        // PutBackEntry on copy-a's entry.
        var entryA = EntryOf(history, a, Text.ResultTrashed)!;
        var putBackA = Table.InRow(entryA, "PutBackEntry");
        Assert.IsNotNull(putBackA, "no PutBackEntry in the entry row of " + a + " (ASP-10: the button is in the row)");
        Click(putBackA);
        AssertPutBack(a, facts[a].Identity, facts[a].Hash, MoveTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => EntryOf(history, a, Text.ResultRestored) is not null, ElementTimeout),
            $"no entry row of {a} with \"{Text.ResultRestored}\" after PutBackEntry: {HistoryDump(history)}");
        Assert.IsNotNull(RecycleBin.Holding(c), "PutBackEntry put back more than its entry");

        // PutBackSessionHistory on the duplicates session puts back copy-c.
        sessions = HistorySessions(history);
        var session = sessions[SessionIndexHolding(sessions, c)].Session;
        var putBackSession = OwnButton(session, "PutBackSessionHistory");
        Assert.IsNotNull(putBackSession, "no PutBackSessionHistory belongs to the duplicates session");
        Click(putBackSession);
        AssertPutBack(c, facts[c].Identity, facts[c].Hash, MoveTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => EntryOf(history, c, Text.ResultRestored) is not null, ElementTimeout),
            $"no entry row of {c} with \"{Text.ResultRestored}\" after PutBackSessionHistory: {HistoryDump(history)}");
        AssertInPlaceAndNotInRecycleBin(b, facts[b].Identity, facts[b].Hash);
        Assert.IsNotNull(RecycleBin.Holding(solo), "the map session's file left the Recycle Bin, though only the duplicates session was put back");
    }
}
