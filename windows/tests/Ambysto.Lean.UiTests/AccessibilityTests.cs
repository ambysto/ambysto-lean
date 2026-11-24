using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan group 13, "Accessibility pass (keyboard, Narrator, High Contrast, DPI)", the part UI Automation can check:
/// every interactive element of each view has a name (what Narrator reads), Tab and Shift+Tab reach each view's controls
/// without leaving the window or getting stuck, and the <c>ConfirmMove</c> dialog is cancelled by Escape and confirmed only
/// by its primary button. Not here: High Contrast (switching it for the test user leaves values behind in the user's
/// HighContrast key that the test cannot put back without writing the registry) and DPI scaling (a new scale takes effect
/// only after a sign-out). Keys pressed are Tab, Shift+Tab, Shift+F10, Escape, and Space only on the dialog's own buttons;
/// real moves only of the test's own items under <c>%USERPROFILE%\LeanUiMove-*</c>, put back by the test.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AccessibilityTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const string OldName = "big-old.bin";

    private static readonly ControlType[] Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ListItem, ControlType.MenuItem, ControlType.ComboBox, ControlType.Edit,
        ControlType.RadioButton, ControlType.Hyperlink, ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.Slider,
    ];

    // The controls Tab must reach (list items and tiles are one tab stop each, by design; see TabProblems).
    private static readonly ControlType[] TabStops =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.RadioButton, ControlType.Hyperlink,
        ControlType.SplitButton, ControlType.Slider,
    ];

    private static readonly string[] DialogButtons = ["CancelMove", "ConfirmMoveButton"];

    /// <summary>The app's windows, or only <paramref name="scope"/> when given.</summary>
    private static List<AutomationElement> Scopes(AppSession app, AutomationElement? scope) => scope is null ? Ui.TopWindows(app) : [scope];

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    /// <summary>A tree for every view at once: one duplicate group of three 2 MB copies, a 101 MB file modified three years
    /// ago (a Large and old row) and two small files. In the Defender-excluded profile test folder; nothing in it is moved.</summary>
    private static Fixture ViewsTree()
    {
        var fixture = new Fixture("ui-a11y", Fixture.ProfileBaseFolder());
        try
        {
            var content = new byte[(int)(2 * MB)];
            new Random(13_1).NextBytes(content);
            fixture.FileWithBytes(@"one\copy-a.dat", content);
            fixture.FileWithBytes(@"two\copy-b.dat", content);
            fixture.FileWithBytes(@"three\copy-c.dat", content);
            fixture.File(OldName, 101 * MB);
            fixture.SetModified(OldName, DateTime.UtcNow.AddYears(-3));
            fixture.File("note.txt", 100 * KB);
            return fixture;
        }
        catch
        {
            fixture.Dispose(); // ASP-22: a failure while it is filled (a full disk, a locked file) must not leave the folder behind; the caller never got it to dispose
            throw;
        }
    }

    /// <summary>A journal of one recent session with a trashed entry (so History offers PutBackEntry and
    /// PutBackSessionHistory; neither is ever pressed, and the path does not exist) and a skipped one.</summary>
    private static void WriteHistory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeanUiMove-a11y-history-" + Guid.NewGuid().ToString("N")[..8]);
        var now = DateTimeOffset.UtcNow;
        JournalBackup.Write(
        [
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-a11y", now.AddHours(-2), "map", Path.Combine(root, "was-here.bin"), "trashed", bytes: 4096),
            JournalBackup.Line(Guid.NewGuid().ToString("N"), "s-a11y", now.AddHours(-2).AddSeconds(1), "map", Path.Combine(root, "in-use.bin"), "skipped", reason: "uitest reason"),
        ]);
    }

    // ---- names -----------------------------------------------------------------------------------------------------------

    /// <summary>Every interactive element in the app's windows whose Name is empty, described, prefixed with
    /// <paramref name="view"/>; and how many interactive elements there were.</summary>
    private static (List<string> Problems, int Count) UnnamedIn(AppSession app, string view)
    {
        var problems = new List<string>();
        var count = 0;
        foreach (var top in Ui.TopWindows(app))
        {
            foreach (AutomationElement element in top.FindAll(TreeScope.Subtree, Condition.TrueCondition))
            {
                try
                {
                    if (!Interactive.Contains(element.Current.ControlType))
                    {
                        continue;
                    }

                    count++;
                    if (string.IsNullOrWhiteSpace(element.Current.Name))
                    {
                        var parent = TreeWalker.ControlViewWalker.GetParent(element);
                        problems.Add(view + ": " + Ui.Describe(element) + " under " + (parent is null ? "(none)" : Ui.Describe(parent)));
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // gone while read (a list recycled its container): not counted
                }
            }
        }

        return (problems, count);
    }

    private static void CheckNames(AppSession app, string view, List<string> problems, int atLeast)
    {
        var (unnamed, count) = UnnamedIn(app, view);
        Console.WriteLine($"{view}: {count} interactive elements, {unnamed.Count} without a name");
        problems.AddRange(unnamed);
        if (count < atLeast)
        {
            problems.Add($"{view}: only {count} interactive elements found (expected at least {atLeast}); the view did not load");
        }
    }

    private static void AssertNoProblems(List<string> problems, string what) =>
        Assert.AreEqual(0, problems.Count, what + ":\n" + string.Join("\n", problems));

    // Narrator reads an element's Name: every button, check box, list item, menu item, combo box, edit and toggle in the main
    // window (after a scan: tiles, breadcrumbs, the toolbar) and in the tile menu has a non-empty Name.
    [TestMethod]
    [TestCategory("Console")]
    public void EveryInteractiveElementOfTheMainWindowAndTheTileMenuHasAName()
    {
        using var fixture = ViewsTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        var map = Ui.WaitForScan(app);
        var problems = new List<string>();
        CheckNames(app, "main", problems, atLeast: 10);

        Ui.MenuItemOf(app, Ui.TileOf(map, OldName), "MoveToRecycleBin");
        try
        {
            CheckNames(app, "tile menu", problems, atLeast: 12);
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }

        AssertNoProblems(problems, "interactive elements without a name");
    }

    // The same for Large and old (rows listed), Duplicates (one group found), Uninstall (the registered apps), History (a
    // session with a trashed and a skipped entry, from the test's own journal), Settings and About.
    [TestMethod]
    [TestCategory("Console")]
    public void EveryInteractiveElementOfEachViewHasAName()
    {
        using var fixture = ViewsTree();
        using var backup = new JournalBackup();
        WriteHistory();
        var problems = new List<string>();
        using (var app = AppSession.Start("--scan", fixture.Path))
        {
            Ui.WaitForScan(app);
            foreach (var (item, viewId, ready) in Views())
            {
                var view = Ui.OpenView(app, item, viewId);
                if (!AppSession.WaitUntil(() => ready(app, view), Ui.DuplicatesTimeout))
                {
                    problems.Add(viewId + ": did not load (" + Ui.Dump(view) + ")");
                }

                CheckNames(app, viewId, problems, atLeast: 3);
                Ui.BackToOverview(app, viewId);
            }
        }

        AssertNoProblems(problems, "interactive elements without a name");
    }

    /// <summary>Each view: its sidebar item (ASP-14, which removes the views' BackToMap buttons: the Overview item is the way
    /// back), its id, and when it has loaded (pressing FindDuplicates for Duplicates).</summary>
    private static IEnumerable<(string Item, string ViewId, Func<AppSession, AutomationElement, bool> Ready)> Views()
    {
        yield return ("LargeOld", "LargeOldView", (_, v) => AppSession.FindAll(v, "LargeOldSelect").Count > 0);
        var searched = false;
        yield return ("Duplicates", "DuplicatesView", (_, v) =>
        {
            if (!searched)
            {
                searched = true;
                Ui.Click(AppSession.Find(v, "FindDuplicates", Ui.ElementTimeout));
            }

            return AppSession.FindAll(v, "DuplicateCopy").Count == 3;
        });
        yield return ("Uninstall", "UninstallView", (_, v) => AppSession.FindAll(v, "UninstallRow").Count > 0);
        yield return ("History", "HistoryView", (_, v) => AppSession.FindAll(v, "PutBackEntry").Count > 0);
        yield return ("Settings", "SettingsView", (_, v) => AppSession.TryFind(v, "OpenAtLogin") is not null);
        yield return ("About", "AboutView", (_, v) => AppSession.TryFind(v, "ActivateKey") is not null && AppSession.TryFind(v, "CheckForUpdates") is not null);
    }

    // ---- keyboard: Tab and Shift+Tab -------------------------------------------------------------------------------------

    /// <summary>The controls Tab must reach in the app's windows: enabled, keyboard-focusable, on-screen buttons, check
    /// boxes, combo boxes and edits (inside list items too), but not map tiles (the map is one tab stop and the arrow keys
    /// move between tiles: MapAndWindowTests). Scoped to <paramref name="scope"/> when given.</summary>
    private static List<AutomationElement> RequiredStops(AppSession app, AutomationElement? scope)
    {
        var required = new List<AutomationElement>();
        foreach (var top in Scopes(app, scope))
        {
            foreach (AutomationElement element in top.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                try
                {
                    var current = element.Current;
                    if (TabStops.Contains(current.ControlType) && current.IsEnabled && current.IsKeyboardFocusable && !current.IsOffscreen && current.AutomationId != "Tile")
                    {
                        required.Add(element);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        return required;
    }

    /// <summary>Presses allowed on top of the focusable controls: rows the list realises while Tab scrolls.</summary>
    private const int TabMargin = 6;

    /// <summary>Every enabled, keyboard-focusable element in the app's windows (or in <paramref name="scope"/>), of any control
    /// type and offscreen or not, in the tree now: the table rows, their tick boxes, the type groups, the tiles, the column
    /// headers. A row's Show in File Explorer and Move buttons are in the tree only while the row is hovered or focused, so
    /// they are in this count only for a row that is (a row with the pointer on it); the other rows' two buttons are added by
    /// <see cref="RowButtonStops"/>, because the walk reaches them as it focuses each row (four stops a row). A walk that
    /// visits each stop once is round after this many presses, so the sum is the budget that decides "never comes back" (a
    /// focus trap, a loop that skips the start) without a magic number.</summary>
    private static int FocusableCount(AppSession app, AutomationElement? scope)
    {
        var count = 0;
        foreach (var top in Scopes(app, scope))
        {
            foreach (AutomationElement element in top.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                try
                {
                    var current = element.Current;
                    if (current.IsEnabled && current.IsKeyboardFocusable)
                    {
                        count++;
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        return count;
    }

    /// <summary>The Show in File Explorer and Move buttons of the table rows whose buttons are not in the tree now (they are
    /// there only for the hovered or focused row): two stops for each such row in <paramref name="scope"/> or the app's
    /// windows. A row of a table without row buttons is counted too: the budget is then a little loose, which still ends a
    /// focus trap.</summary>
    private static int RowButtonStops(AppSession app, AutomationElement? scope)
    {
        var stops = 0;
        foreach (var top in Scopes(app, scope))
        {
            foreach (var row in Table.LeafRows(top))
            {
                try
                {
                    if (Table.InRow(row, "RevealRow") is null && Table.InRow(row, "MoveRow") is null)
                    {
                        stops += 2;
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        return stops;
    }

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

    /// <summary>From <paramref name="start"/> (focused first), presses Tab (or Shift+Tab) until the focus is back on it, at
    /// most <paramref name="maxPresses"/> times. Problems: the focus leaves the app (another process or none), stays where it
    /// was after a press (stuck), or does not come back round. Returns the elements focused, in order.</summary>
    private static List<AutomationElement> Walk(AppSession app, AutomationElement start, bool shift, int maxPresses, string view, List<string> problems, Func<AutomationElement, bool>? inside = null)
    {
        bool InScope(AutomationElement e) => inside is null || inside(e);
        Ui.Focus(start);
        var startKey = Ui.Key(start);
        var previous = startKey;
        var visited = new List<AutomationElement>();
        var direction = shift ? "Shift+Tab" : "Tab";
        for (var press = 1; press <= maxPresses; press++)
        {
            if (shift)
            {
                Keys.PressWithShift(Keys.Tab);
            }
            else
            {
                Keys.Press(Keys.Tab);
            }

            AutomationElement? focused = null;
            var moved = AppSession.WaitUntil(() => (focused = Focused()) is { } f && Ui.Key(f) != previous, TimeSpan.FromSeconds(1.5));
            if (focused is null)
            {
                problems.Add($"{view}: after {direction} #{press} nothing has the keyboard focus");
                return visited;
            }

            if (focused.Current.ProcessId != app.Process.Id)
            {
                problems.Add($"{view}: {direction} #{press} moved the focus out of the app, to {Ui.Describe(focused)} (process {focused.Current.ProcessId})");
                return visited;
            }

            if (inside is not null && !inside(focused))
            {
                problems.Add($"{view}: {direction} #{press} moved the focus out of the dialog, to {Ui.Describe(focused)}");
                return visited;
            }

            if (!moved && press == 1 && previous == startKey && RequiredStops(app, null).All(e => Ui.Key(e) == startKey || !InScope(e)))
            {
                Console.WriteLine($"{view} {direction}: the only tab stop, {Ui.Describe(start)}, keeps the focus");
                return visited;
            }

            if (!moved)
            {
                problems.Add($"{view}: {direction} #{press} left the focus on {Ui.Describe(focused)} (stuck)");
                return visited;
            }

            previous = Ui.Key(focused);
            if (previous == startKey)
            {
                Console.WriteLine($"{view} {direction}: {press} presses round: " + string.Join(" → ", visited.Select(Ui.Describe)));
                return visited;
            }

            visited.Add(focused);
        }

        problems.Add($"{view}: {direction} did not come back to {Ui.Describe(start)} within {maxPresses} presses: " + string.Join(" → ", visited.Select(Ui.Describe)));
        return visited;
    }

    /// <summary>Tab and Shift+Tab from <paramref name="start"/>: each comes back round without leaving the app or getting
    /// stuck, and each reaches every required control; when the view has map tiles, a tile; and when it has list items, one
    /// of each list's items (a list is one tab stop and the arrow keys move inside it).</summary>
    private static void TabProblems(AppSession app, string view, AutomationElement start, List<string> problems, AutomationElement? scope = null, Func<AutomationElement, bool>? inside = null)
    {
        var required = RequiredStops(app, scope);
        var startKey = Ui.Key(start);
        var tiles = scope is null ? Ui.TopWindows(app).SelectMany(t => AppSession.FindAll(t, "Tile")).ToList() : [];
        var lists = Scopes(app, scope)
            .SelectMany(t => t.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)).Cast<AutomationElement>())
            .Where(l => l.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)) is not null)
            .ToList();
        var focusable = FocusableCount(app, scope);
        var rowButtons = RowButtonStops(app, scope);
        var maxPresses = focusable + rowButtons + TabMargin;
        Console.WriteLine($"{view}: {focusable} focusable controls and {rowButtons} row buttons not in the tree yet, so at most {maxPresses} presses");
        Console.WriteLine($"{view}: {required.Count} required stops: " + string.Join(", ", required.Select(Ui.Describe)));

        foreach (var shift in new[] { false, true })
        {
            var direction = shift ? "Shift+Tab" : "Tab";
            var visited = Walk(app, start, shift, maxPresses, view, problems, inside);
            var keys = visited.Select(Ui.Key).Append(startKey).ToHashSet(StringComparer.Ordinal);
            foreach (var element in required.Where(e => !keys.Contains(Ui.Key(e))))
            {
                problems.Add($"{view}: {direction} never reaches {Ui.Describe(element)}");
            }

            if (tiles.Count > 0 && !visited.Any(e => e.Current.AutomationId == "Tile"))
            {
                problems.Add($"{view}: {direction} never reaches a map tile");
            }

            foreach (var list in lists.Where(l => !visited.Any(e => Ui.IsWithin(e, l))))
            {
                problems.Add($"{view}: {direction} never reaches the list {Ui.Describe(list)} or one of its items");
            }
        }
    }

    // Keyboard: in the main window after a scan and in each view (Large and old, Duplicates with a group, Uninstall, History
    // with a session, Settings, About), Tab and Shift+Tab come back round from the first control (a view's: its sidebar item,
    // ASP-14) without leaving the window or getting stuck, and reach every enabled button, check box, combo box and edit, a
    // map tile, and each list. Only Tab and Shift+Tab are pressed: nothing is invoked.
    [TestMethod]
    [TestCategory("Console")]
    public void TabAndShiftTabReachEveryControlOfEachViewAndStayInTheWindow()
    {
        using var fixture = ViewsTree();
        using var backup = new JournalBackup();
        WriteHistory();
        var problems = new List<string>();
        using (var app = AppSession.Start("--scan", fixture.Path))
        {
            Ui.WaitForScan(app);
            var window = app.Window(Ui.WindowTimeout);
            TabProblems(app, "main", AppSession.Find(window, "Scan", Ui.ElementTimeout), problems);
            foreach (var (item, viewId, ready) in Views())
            {
                var view = Ui.OpenView(app, item, viewId);
                if (!AppSession.WaitUntil(() => ready(app, view), Ui.DuplicatesTimeout))
                {
                    problems.Add(viewId + ": did not load (" + Ui.Dump(view) + ")");
                }

                System.Threading.Thread.Sleep(500); // the search's status settles, the list realises its items
                TabProblems(app, viewId, Ui.SidebarItem(app, item), problems);
                Ui.BackToOverview(app, viewId);
            }
        }

        AssertNoProblems(problems, "keyboard navigation");
    }

    // ---- the ConfirmMove dialog and the MoveReport panel ---------------------------------------------------------------

    private static AutomationElement OpenConfirmation(AppSession app, string name)
    {
        var move = Ui.MenuItemOf(app, Ui.TileOf(Ui.Map(app), name), "MoveToRecycleBin");
        Assert.IsTrue(move.Current.IsEnabled, "precondition: Move to Recycle Bin… is enabled on " + name);
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        Ui.FindAnywhere(app, "ConfirmMoveButton", Ui.ElementTimeout);
        Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500); // the opening animation hands the dialog the keyboard
        return dialog;
    }

    private static void AssertNothingMoved(AppSession app, string path, (ulong Volume, UInt128 FileId) identity)
    {
        Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("MoveReport") is not null, Ui.NeverWatch), "a move report appeared");
        Assert.IsTrue(File.Exists(path), path + " was moved");
        Assert.AreEqual(identity, MoveFixture.Identity(path), path + " is not the same file");
        Assert.IsNull(RecycleBin.Holding(path), path + " is in the Recycle Bin");
    }

    // ConfirmMove by keyboard: when it opens the focus is inside it; Tab and Shift+Tab stay inside it and reach both of its
    // buttons; it has exactly two buttons (ConfirmMoveButton, CancelMove: nothing else confirms); Escape closes it and moves
    // nothing. Opened again, Tab to ConfirmMoveButton and Space moves the file (the primary button works by keyboard);
    // the MoveReport's controls then have names and are reached by Tab; Put back brings the file back.
    [TestMethod]
    [TestCategory("Console")]
    public void TheConfirmationIsCancelledByEscapeAndConfirmedOnlyByItsPrimaryButton()
    {
        using var fixture = new MoveFixture("a11y-dialog");
        var file = fixture.File("move-me.bin", 100 * KB, seed: 131);
        fixture.File("stay.bin", 60 * KB, seed: 132);
        var identity = MoveFixture.Identity(file);
        var hash = MoveFixture.Hash(file);

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var dialog = OpenConfirmation(app, "move-me.bin");
        var problems = new List<string>();

        Assert.IsTrue(AppSession.WaitUntil(() => Focused() is { } f && Ui.IsWithin(f, dialog), TimeSpan.FromSeconds(3)),
            "the keyboard focus is not inside the confirmation when it opens: " + (Focused() is { } focused ? Ui.Describe(focused) : "none"));
        var buttons = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
            .Where(b => !b.Current.IsOffscreen).ToList();
        CollectionAssert.AreEquivalent(DialogButtons, buttons.Select(b => b.Current.AutomationId).ToArray(),
            "the confirmation's buttons: " + string.Join(", ", buttons.Select(Ui.Describe)));
        CheckNames(app, "ConfirmMove", problems, atLeast: 2);
        TabProblems(app, "ConfirmMove", Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout), problems, scope: dialog, inside: e => Ui.IsWithin(e, dialog));

        Ui.Focus(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "Escape did not close the confirmation");
        AssertNothingMoved(app, file, identity);

        OpenConfirmation(app, "move-me.bin");
        var confirm = Ui.FindAnywhere(app, "ConfirmMoveButton", Ui.ElementTimeout);
        Ui.Focus(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        var reached = false;
        for (var press = 0; press < 6 && !reached; press++)
        {
            Keys.Press(Keys.Tab);
            reached = AppSession.WaitUntil(() => Ui.HasFocus(confirm), TimeSpan.FromSeconds(0.5));
        }

        Assert.IsTrue(reached, "Tab does not reach ConfirmMoveButton from CancelMove within 6 presses");
        Keys.Press(Keys.Space);
        var report = Ui.WaitForReport(app, Text.SpaceReturnsWhenEmptied);
        try
        {
            Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "the move skipped or lost the file: " + Ui.Dump(report));
            Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(file) is not null, TimeSpan.FromSeconds(10)), file + " is not in the Recycle Bin");
            CheckNames(app, "MoveReport", problems, atLeast: 2);
            var window = app.Window(Ui.WindowTimeout);
            TabProblems(app, "main with MoveReport", AppSession.Find(window, "Scan", Ui.ElementTimeout), problems);
        }
        finally
        {
            Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(file) && RecycleBin.Holding(file) is null, Ui.MoveTimeout), file + " was not put back");
        }

        Assert.AreEqual(identity, MoveFixture.Identity(file), "put back as a different file");
        Assert.AreEqual(hash, MoveFixture.Hash(file), "the bytes put back differ");
        var done = file + ": " + Text.PutBackDone;
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.FindAllAnywhere(app, "ReportItem").Any(r => string.Equals(r.Current.Name, done, StringComparison.OrdinalIgnoreCase)), Ui.ElementTimeout),
            "no ReportItem \"" + done + "\"");
        CheckNames(app, "MoveReport after Put back", problems, atLeast: 3);
        AssertNoProblems(problems, "the confirmation and the report");
    }

    // ---- the focus after Cancel ---------------------------------------------------------------------------------------------------

    // ASP-22: the confirmation is opened by keyboard (the Move button of Large and old, focused, Space); Escape closes it, and so does
    // Tab to Cancel and Enter. Each time the dialog is gone the keyboard focus is on the Move button that opened it (not lost on the
    // window), and nothing moved: the file is the same, the Recycle Bin does not hold it, no report appeared.
    [TestMethod]
    [TestCategory("Console")]
    public void AfterCancelByEscapeOrByTabAndEnterTheFocusIsOnTheMoveButtonThatOpenedTheConfirmation()
    {
        using var fixture = ViewsTree();
        var path = fixture.Sub(OldName);
        var identity = MoveFixture.Identity(path);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", OldName) is not null, Ui.ElementTimeout), "no Large and old row for " + OldName + ": " + Table.Dump(view, "LargeOldSelect"));
        Table.Tick(Table.ControlWith(view, "LargeOldSelect", OldName)!, () => Table.ControlWith(view, "LargeOldSelect", OldName), OldName);

        foreach (var way in new[] { "Escape", "Tab to Cancel and Enter" })
        {
            var move = AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), way + ": Move is disabled with a row ticked");
            Ui.Focus(move);
            Keys.Press(Keys.Space);
            var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
            System.Threading.Thread.Sleep(500); // the opening animation hands the dialog the keyboard
            Assert.IsTrue(AppSession.WaitUntil(() => Focused() is { } f && Ui.IsWithin(f, dialog), TimeSpan.FromSeconds(3)), way + ": the focus is not inside the confirmation when it opens");

            if (way == "Escape")
            {
                Keys.Press(Keys.Escape);
            }
            else
            {
                var confirm = AppSession.WaitFor(() => app.FindInAnyWindow("ConfirmMoveButton"), Ui.ElementTimeout);
                if (confirm is null)
                {
                    try { DumpMissingConfirmation(app, dialog, way); }
                    catch (Exception error) { Console.WriteLine("Confirmation diagnostics failed: " + error); }
                }
                Assert.IsNotNull(confirm, "no element with AutomationId ConfirmMoveButton within the original timeout; see confirmation diagnostics");
                var cancel = Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout);
                PressTabUntilFocused(confirm, way + ": Tab does not reach ConfirmMoveButton");
                PressTabUntilFocused(cancel, way + ": Tab does not come back to CancelMove"); // never Enter on the primary button
                Keys.Press(Keys.Enter);
            }

            Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), way + ": the confirmation did not close");
            Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout)), TimeSpan.FromSeconds(3)),
                way + ": after Cancel the focus is not on the Move button that opened the confirmation; it is on " + (Focused() is { } now ? Ui.Describe(now) : "nothing"));
            AssertNothingMoved(app, path, identity);
        }
    }

    // ASP-22 (docs/spec/02): when the opener is not the view's Move button. (1) The row's own Move button (MoveRow, shown while the row holds the
    // focus): Escape returns the focus to that button. (2) The row's menu item "Move to Recycle Bin..." (the menu is gone once the
    // confirmation opens): Escape returns the focus to the Move button of the view, MoveLargeOld, which the row rule has just enabled.
    // Nothing moves.
    [TestMethod]
    [TestCategory("Console")]
    public void AfterCancelTheFocusIsOnTheRowsMoveButtonOrWhenTheMenuIsGoneOnTheViewsMoveButton()
    {
        using var fixture = ViewsTree();
        var path = fixture.Sub(OldName);
        var identity = MoveFixture.Identity(path);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", OldName) is not null, Ui.ElementTimeout), "no Large and old row for " + OldName);
        AutomationElement? RowMove() => Table.InRow(Table.RowOf(Table.ControlWith(view, "LargeOldSelect", OldName)!)!, "MoveRow");

        // (1) the row's own button
        Ui.Focus(Table.ControlWith(view, "LargeOldSelect", OldName)!);
        Assert.IsTrue(AppSession.WaitUntil(() => Table.InRow(Table.RowOf(Table.ControlWith(view, "LargeOldSelect", OldName)!)!, "MoveRow") is { } b && !b.Current.IsOffscreen, Ui.ElementTimeout),
            "the row shows no MoveRow button while it holds the focus");
        Ui.Focus(RowMove() ?? throw new AssertFailedException("the focused row has no MoveRow button"));
        Keys.Press(Keys.Space);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Assert.IsNotNull(dialog);
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation did not close");
        Assert.IsTrue(AppSession.WaitUntil(() => RowMove() is { } move && Ui.HasFocus(move), TimeSpan.FromSeconds(3)),
            "after Cancel the focus is not on the row's MoveRow button that opened the confirmation; it is on " + (Focused() is { } first ? Ui.Describe(first) : "nothing"));
        AssertNothingMoved(app, path, identity);

        // (2) the row's menu
        var row = Table.RowOf(Table.ControlWith(view, "LargeOldSelect", OldName)!)!;
        var item = Ui.MenuItemOf(app, row, "MoveToRecycleBin");
        Assert.IsTrue(item.Current.IsEnabled, "Move to Recycle Bin... is disabled on the row");
        Ui.Click(item);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Keys.Press(Keys.Escape);
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation (from the menu) did not close");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.HasFocus(AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout)), TimeSpan.FromSeconds(3)),
            "after Cancel of a confirmation opened from the row menu the focus is not on the view's Move button; it is on " + (Focused() is { } second ? Ui.Describe(second) : "nothing"));
        AssertNothingMoved(app, path, identity);
    }

    // Presses Tab (at most 6 times) until the focus is on <paramref name="target"/>; fails with <paramref name="message"/>.
    private static void PressTabUntilFocused(AutomationElement target, string message)
    {
        for (var press = 0; press < 6 && !Ui.HasFocus(target); press++)
        {
            Keys.Press(Keys.Tab);
            AppSession.WaitUntil(() => Ui.HasFocus(target), TimeSpan.FromSeconds(0.5));
        }

        Assert.IsTrue(Ui.HasFocus(target), message);
    }

    private static void DumpMissingConfirmation(AppSession app, AutomationElement dialog, string phase)
    {
        var requested = Environment.GetEnvironmentVariable("LEAN_FOCUS_DIAGNOSTICS");
        var folder = string.IsNullOrWhiteSpace(requested) ? Path.Combine(Path.GetTempPath(), "LeanFocusProof") : requested;
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("LEAN_FOCUS_DIAGNOSTICS must be absolute");
        Directory.CreateDirectory(folder);
        var stem = "missing-confirm-" + app.Process.Id + "-" + Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(folder, stem + ".txt");
        var text = new System.Text.StringBuilder();
        text.AppendLine("UTC=" + DateTimeOffset.UtcNow.ToString("o"));
        text.AppendLine("Case=AfterCancelByEscapeOrByTabAndEnterTheFocusIsOnTheMoveButtonThatOpenedTheConfirmation");
        text.AppendLine("Phase=" + phase + "; processId=" + app.Process.Id + "; sinceStart=" + app.SinceStart.Elapsed);
        text.AppendLine("Existing ConfirmMove peer: " + DescribeDiagnostic(dialog));
        try
        {
            text.AppendLine("Focused before snapshot: " + (AutomationElement.FocusedElement is { } focus ? DescribeDiagnostic(focus) : "none"));
        }
        catch (Exception error) { text.AppendLine("Focused read failed: " + error); }

        try
        {
            var windows = Ui.TopWindows(app); // every top-level app window, not only the title-matched main window
            text.AppendLine("Top-level app windows=" + windows.Count);
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                text.AppendLine("WINDOW " + index + ": " + DescribeDiagnostic(window));
                try
                {
                    // TrueCondition retains non-button/template peers too; every button carries id/name/state/bounds below.
                    var descendants = window.FindAll(TreeScope.Subtree, Condition.TrueCondition);
                    text.AppendLine("All exposed descendants (including window)=" + descendants.Count);
                    foreach (AutomationElement element in descendants)
                        text.AppendLine(DescribeDiagnostic(element));
                }
                catch (Exception error) { text.AppendLine("Tree read failed: " + error); }
                try
                {
                    var handle = new AutomationElementHandle(new IntPtr(window.Current.NativeWindowHandle));
                    if (handle.Handle == IntPtr.Zero) { text.AppendLine("No native handle for window screenshot"); continue; }
                    var capture = ScreenCapture.Of(WindowOnScreen.Frame(handle));
                    // Save creates this test's unique intermediate image under %TEMP%\LeanUiTheme; copy it into the requested proof folder.
                    var intermediate = capture.Save(stem + "-window-" + index);
                    var screenshot = Path.Combine(folder, stem + "-window-" + index + ".png");
                    File.Copy(intermediate, screenshot, overwrite: false);
                    File.Delete(intermediate); // only the uniquely named diagnostic image made immediately above
                    text.AppendLine("Screenshot=" + screenshot);
                }
                catch (Exception error) { text.AppendLine("Screenshot failed: " + error); }
            }
        }
        finally
        {
            File.WriteAllText(logPath, text.ToString());
            Console.WriteLine("Confirmation diagnostic: " + logPath);
            Console.WriteLine(text.ToString()); // also retained in the failing test's normal TRX output
        }
    }

    private static string DescribeDiagnostic(AutomationElement element)
    {
        try
        {
            var c = element.Current;
            string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
            return "runtimeId=" + string.Join(".", element.GetRuntimeId())
                + " controlType=" + c.ControlType.ProgrammaticName
                + " id=" + Quote(c.AutomationId) + " name=" + Quote(c.Name)
                + " class=" + Quote(c.ClassName) + " framework=" + Quote(c.FrameworkId)
                + " enabled=" + c.IsEnabled + " offscreen=" + c.IsOffscreen
                + " keyboardFocusable=" + c.IsKeyboardFocusable + " hasKeyboardFocus=" + c.HasKeyboardFocus
                + " isControl=" + c.IsControlElement + " isContent=" + c.IsContentElement
                + " bounds=" + c.BoundingRectangle + " nativeHandle=" + c.NativeWindowHandle;
        }
        catch (Exception error) { return "<unavailable peer: " + error.GetType().Name + ": " + error.Message + ">"; }
    }

}
