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

/// <summary>Plan "UI contract for group 4" (intent 2026-10-18-lean-windows-v1-additions, outcome 1; docs/spec/02 and 07
/// Windows notes of 2026-10-18): the Settings view's protected folders (<c>ProtectedFoldersList</c> with one
/// <c>ProtectedFolder</c> row per folder named by its path, <c>AddProtectedFolder</c> through the system folder picker,
/// <c>RemoveProtectedFolder</c> enabled with a row selected, the result in <c>ProtectedFoldersMessage</c>, the list saved at
/// once to <c>%LOCALAPPDATA%\Ambysto\Space\settings.jsonl</c>), their effect (an item inside a protected folder is
/// display-only with <c>Text.HintProtectedByYou</c> on the map and in Large and old, and the gate refuses it), and a
/// settings file that cannot be read (<c>Text.SettingsUnreadable</c>, the file left as it is). Every test replaces the
/// settings file and restores its bytes exactly afterwards (<see cref="SettingsBackup"/>), so they are SharedState. The
/// fixtures are the test's own folders under <c>%USERPROFILE%\LeanUiMove-*</c>; the only move ever started is one of a
/// fixture item the gate must refuse.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ProtectedFoldersTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;
    private const string Guarded = "guarded";

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static AutomationElement OpenSettings(AppSession app) => Ui.OpenView(app, "Settings", "SettingsView");

    private static List<AutomationElement> FolderRows(AutomationElement view) =>
        AppSession.FindAll(AppSession.Find(view, "ProtectedFoldersList", Ui.ElementTimeout), "ProtectedFolder");

    private static string RowNames(AutomationElement view) => "[" + string.Join(" | ", FolderRows(view).Select(r => r.Current.Name)) + "]";

    private static AutomationElement? RowOf(AutomationElement view, string folder) =>
        FolderRows(view).FirstOrDefault(r => string.Equals(Path.TrimEndingDirectorySeparator(r.Current.Name), folder, StringComparison.OrdinalIgnoreCase));

    private static bool Saved(string folder) =>
        SettingsBackup.LastProtectedFolders() is { } saved && saved.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The HelpText of a tile or row: the full path, then (display-only) the hint on a new line.</summary>
    private static string? HintIn(string help, string path)
    {
        if (!help.StartsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = help[path.Length..];
        return rest.StartsWith('\n') || rest.StartsWith("\r\n", StringComparison.Ordinal) ? rest.Trim() : null;
    }

    /// <summary>The tile <paramref name="name"/> on the map (ASP-14: the map shows every item with its hint; ShowSystemItems
    /// is in Large and old, the only list it changes).</summary>
    private static AutomationElement TileShown(AppSession app, string name) => Ui.TileOf(Ui.Map(app), name);

    private static void AssertProtectedTile(AppSession app, string name, string path)
    {
        var tile = TileShown(app, name);
        var help = tile.Current.HelpText ?? string.Empty;
        var hint = HintIn(help, path);
        Assert.IsTrue(hint is not null && hint.Contains(Text.HintProtectedByYou, StringComparison.Ordinal),
            $"the tile {name} of a protected folder does not show \"{Text.HintProtectedByYou}\" after its path: HelpText \"{help}\"");
        Assert.IsFalse(Ui.TileMoveEnabled(app, Ui.Map(app), name), $"Move to Recycle Bin… is enabled on the protected tile {name}");
    }

    /// <summary>Closes the app as a user would and waits for the process to end.</summary>
    private static void Close(AppSession app)
    {
        if (!app.Process.HasExited)
        {
            app.Process.CloseMainWindow();
        }

        Assert.IsTrue(app.Process.WaitForExit(15_000), "the app did not close within 15 s");
    }

    // ---- the effect: map and Large and old -----------------------------------------------------------------------------

    // Contract and docs/spec/02 Windows note: a folder in the settings file's protected list (written before launch) is
    // display-only on the map: its tile's HelpText is its path, then Text.HintProtectedByYou on a new line, and its Move
    // to Recycle Bin… is disabled; the unprotected folder beside it stays movable. In Large and old, a 101 MB file of three
    // years ago inside the protected folder is display-only (shown, with Show system items if need be, with the hint and
    // Move disabled), while the same kind of file outside it is a movable row. Nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AProtectedFolderIsShownProtectedByYouOnTheMapAndInLargeAndOld()
    {
        using var settings = new SettingsBackup();
        using var fixture = new MoveFixture("protect");
        var threeYearsAgo = DateTime.UtcNow.AddYears(-3);
        var guardedFile = fixture.File(@"guarded\guarded-old.bin", 101 * MB, seed: 41);
        var freeFile = fixture.File(@"free\free-old.bin", 101 * MB, seed: 42);
        fixture.SetModified(@"guarded\guarded-old.bin", threeYearsAgo);
        fixture.SetModified(@"free\free-old.bin", threeYearsAgo);
        var guarded = fixture.Sub(Guarded);
        SettingsBackup.WriteProtectedFolders(guarded);

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        AssertProtectedTile(app, Guarded, guarded);
        Assert.IsTrue(Ui.TileMoveEnabled(app, Ui.Map(app), "free"), "Move to Recycle Bin… is disabled on the unprotected tile free");

        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        AppSession.Find(view, "LargeOldList", Ui.ElementTimeout);
        // ASP-10: a Large and old row is the row of a LargeOldSelect box; its automation name joins its cells.
        AutomationElement? Box(AutomationElement scope, string name) => Table.ControlWith(scope, "LargeOldSelect", name);
        AutomationElement? Row(AutomationElement scope, string name) => Box(scope, name) is { } b ? Table.RowOf(b) : null;
        Assert.IsTrue(AppSession.WaitUntil(() => Row(view, "free-old.bin") is not null, Ui.ElementTimeout), "no Large and old row for the unprotected free-old.bin: " + Ui.Dump(view));
        if (!AppSession.WaitUntil(() => Row(view, "guarded-old.bin") is not null, Ui.NeverWatch))
        {
            // Display-only rows wait for Show system items, inside Large and old (ASP-14).
            var toggle = (TogglePattern)AppSession.Find(view, "ShowSystemItems", Ui.ElementTimeout).GetCurrentPattern(TogglePattern.Pattern);
            if (toggle.Current.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
            }
        }

        AutomationElement? row = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (row = Row(view, "guarded-old.bin")) is not null, Ui.ElementTimeout),
            "no Large and old row for the protected guarded-old.bin, even with Show system items: " + Ui.Dump(view));
        var rowName = row!.Current.Name;
        Assert.IsTrue(Table.Holds(rowName, guardedFile) && rowName.Contains(Text.HintProtectedByYou, StringComparison.Ordinal),
            $"the Large and old row of a file in a protected folder does not hold its path and \"{Text.HintProtectedByYou}\": \"{rowName}\"");
        var guardedBox = Box(view, "guarded-old.bin")!;
        Assert.IsFalse(guardedBox.Current.IsEnabled, "the LargeOldSelect box of a row inside a protected folder is enabled: " + rowName);
        Assert.AreEqual(ToggleState.Off, Table.State(guardedBox), "a row inside a protected folder is ticked: " + rowName);
        var move = Ui.MenuItemOf(app, row, "MoveToRecycleBin");
        try
        {
            Assert.IsFalse(move.Current.IsEnabled, "Move to Recycle Bin… is enabled on a Large and old row inside a protected folder");
        }
        finally
        {
            Keys.Press(Keys.Escape);
        }

        var freeName = Row(view, "free-old.bin")!.Current.Name;
        Assert.IsFalse(freeName.Contains(Text.HintProtectedByYou, StringComparison.Ordinal), $"the unprotected row free-old.bin says {Text.HintProtectedByYou}: \"{freeName}\"");
        Assert.IsTrue(Box(view, "free-old.bin")!.Current.IsEnabled, "the unprotected row's LargeOldSelect box is disabled: " + freeName);
        Assert.IsTrue(File.Exists(guardedFile) && File.Exists(freeFile), "a fixture file is gone");
    }

    // ---- the effect: Duplicates ----------------------------------------------------------------------------------------

    // docs/spec/03 Duplicates, Windows note (2026-10-18), with docs/spec/02's protected folders: a byte-identical 2 MB
    // pair, guarded\copy-g.dat (inside the protected folder, written before launch; the newest, so the suggested keep) and
    // free\copy-f.dat. After FindDuplicates both copies are in the group; the protected copy's DuplicateCopy check box is
    // disabled and its row (ASP-10: the row's automation name, or a tooltip in it) holds Text.HintProtectedByYou; the other
    // copy's check box is enabled, without the hint, and can be ticked (MoveToRecycleBin then enabled). The protected copy
    // is the newest, so the suggested keep; its box is disabled for both reasons, and the free copy is the one to move.
    // Nothing is moved.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ACopyInAProtectedFolderIsDisplayOnlyInDuplicates()
    {
        using var settings = new SettingsBackup();
        using var fixture = new MoveFixture("protect-dups");
        var content = new byte[(int)(2 * MB)];
        new Random(18_10_2026).NextBytes(content);
        var guardedCopy = fixture.FileWithBytes(@"guarded\copy-g.dat", content);
        var freeCopy = fixture.FileWithBytes(@"free\copy-f.dat", content);
        var now = DateTime.UtcNow;
        fixture.SetModified(@"guarded\copy-g.dat", now.AddDays(-1));
        fixture.SetModified(@"free\copy-f.dat", now.AddDays(-2));
        var guarded = fixture.Sub(Guarded);
        SettingsBackup.WriteProtectedFolders(guarded);

        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        AutomationElement? CopyOf(string path) => Table.ControlFor(view, "DuplicateCopy", path);
        string Copies() => Table.Dump(view, "DuplicateCopy");
        bool ShowsHint(AutomationElement box) =>
            Table.RowOf(box) is { } r && Table.Texts(r).Any(t => (t.Name + "\n" + t.Help).Contains(Text.HintProtectedByYou, StringComparison.Ordinal));
        Assert.IsTrue(AppSession.WaitUntil(() => CopyOf(freeCopy) is not null && CopyOf(guardedCopy) is not null, Ui.DuplicatesTimeout),
            "the pair is not shown as one group with both copies (the protected copy stays in its group): " + Copies() + " " + Ui.Dump(view));

        var protectedBox = CopyOf(guardedCopy)!;
        Assert.IsTrue(Table.RowName(protectedBox).Contains(Text.HintProtectedByYou, StringComparison.Ordinal) || ShowsHint(protectedBox),
            $"the protected copy's row lacks \"{Text.HintProtectedByYou}\": \"{Table.RowName(protectedBox)}\"");
        Assert.IsFalse(protectedBox.Current.IsEnabled, "the protected copy's check box is enabled: " + Copies());
        Assert.AreEqual(ToggleState.Off, ((TogglePattern)protectedBox.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState,
            "the protected copy is ticked");

        var freeBox = CopyOf(freeCopy)!;
        Assert.IsFalse(ShowsHint(freeBox),
            $"the unprotected copy says \"{Text.HintProtectedByYou}\": \"{Table.RowName(freeBox)}\"");
        Assert.IsTrue(freeBox.Current.IsEnabled, "the unprotected copy's check box is disabled: " + Copies());
        var move = AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout);
        Assert.IsFalse(move.Current.IsEnabled, "Move is enabled with nothing ticked");
        ((TogglePattern)freeBox.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Assert.IsTrue(AppSession.WaitUntil(() => CopyOf(freeCopy) is { } c && ((TogglePattern)c.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On, TimeSpan.FromSeconds(3)),
            "the unprotected copy did not tick: " + Copies());
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, TimeSpan.FromSeconds(3)), "Move stays disabled with the unprotected copy ticked");
        Assert.IsFalse(CopyOf(guardedCopy)!.Current.IsEnabled, "the protected copy's check box became enabled after the other copy was ticked");

        Assert.IsTrue(File.Exists(guardedCopy) && File.Exists(freeCopy), "a fixture file is gone");
    }

    // ---- the Settings list: Remove ---------------------------------------------------------------------------------------

    // Contract: with one protected folder in the settings file, ProtectedFoldersList shows one ProtectedFolder row named by
    // its path. RemoveProtectedFolder is disabled while no row is selected and enabled with the row selected; pressing it
    // takes the row away, ProtectedFoldersMessage shows the result (not Text.SettingsNotSaved), and the list is saved at
    // once (the file's last line that parses no longer holds the folder). Back on the map the folder's tile is movable
    // again: at once, or after a new scan (the app started again with --scan, which also proves the removal was saved).
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void RemovingAProtectedFolderSavesTheListAndMakesItMovableAgain()
    {
        using var settings = new SettingsBackup();
        using var fixture = new MoveFixture("unprotect");
        fixture.File(@"guarded\a.bin", 100 * KB, seed: 43);
        fixture.File("stay.bin", 60 * KB, seed: 44);
        var guarded = fixture.Sub(Guarded);
        SettingsBackup.WriteProtectedFolders(guarded);

        using (var app = AppSession.Start("--scan", fixture.Path))
        {
            Ui.WaitForScan(app);
            AssertProtectedTile(app, Guarded, guarded);

            var view = OpenSettings(app);
            Assert.IsTrue(AppSession.WaitUntil(() => FolderRows(view).Count == 1 && RowOf(view, guarded) is not null, Ui.ElementTimeout),
                $"ProtectedFoldersList does not show exactly the one folder {guarded}: {RowNames(view)}");
            var remove = AppSession.Find(view, "RemoveProtectedFolder", Ui.ElementTimeout);
            Assert.IsFalse(remove.Current.IsEnabled, "RemoveProtectedFolder is enabled with no row selected");
            Ui.SelectionItemOf(RowOf(view, guarded)!).Select();
            Assert.IsTrue(AppSession.WaitUntil(() => remove.Current.IsEnabled, TimeSpan.FromSeconds(3)), "RemoveProtectedFolder stays disabled with the row selected");
            Ui.Click(remove);

            Assert.IsTrue(AppSession.WaitUntil(() => RowOf(view, guarded) is null, Ui.ElementTimeout), "the removed folder is still listed: " + RowNames(view));
            var message = AppSession.Find(view, "ProtectedFoldersMessage", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => !string.IsNullOrWhiteSpace(message.Current.Name), Ui.ElementTimeout), "ProtectedFoldersMessage shows no result after Remove");
            Assert.AreNotEqual(Text.SettingsNotSaved, message.Current.Name, "the removal was not saved");
            Assert.IsTrue(AppSession.WaitUntil(() => SettingsBackup.LastProtectedFolders() is not null && !Saved(guarded), Ui.ElementTimeout),
                "the settings file still protects the folder after Remove (not saved at once): " + string.Join(", ", SettingsBackup.LastProtectedFolders() ?? []));

            Ui.BackToOverview(app, "SettingsView");
            if (AppSession.WaitUntil(() => !(Ui.TileOf(Ui.Map(app), Guarded).Current.HelpText ?? string.Empty).Contains(Text.HintProtectedByYou, StringComparison.Ordinal), TimeSpan.FromSeconds(5))
                && Ui.TileMoveEnabled(app, Ui.Map(app), Guarded))
            {
                Console.WriteLine("the tile became movable at once, without a new scan");
                return;
            }

            Console.WriteLine("the tile stays display-only until a new scan; starting the app again with --scan");
            Close(app);
        }

        using var again = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(again);
        var tile = Ui.TileOf(Ui.Map(again), Guarded);
        Assert.IsFalse((tile.Current.HelpText ?? string.Empty).Contains(Text.HintProtectedByYou, StringComparison.Ordinal),
            "after Remove and a new scan the tile still says " + Text.HintProtectedByYou + ": " + tile.Current.HelpText);
        Assert.IsTrue(Ui.TileMoveEnabled(again, Ui.Map(again), Guarded), "after Remove and a new scan, Move to Recycle Bin… is still disabled on the folder");
    }

    // ---- the Settings list: Add through the folder picker -------------------------------------------------------------

    // Contract: AddProtectedFolder opens the system folder picker; the test types the fixture folder's full path into the
    // picker's folder box and presses Select Folder. A ProtectedFolder row named by that path appears,
    // ProtectedFoldersMessage says Text.ProtectedFolderAdded, and the settings file's last line holds it (saved at once).
    // Adding a folder inside it says Text.ProtectedFolderAlreadyCovered and adds no row. Back on the map (no new scan),
    // the next gate session uses the list: either the tile is display-only with Text.HintProtectedByYou, or, if its Move
    // is still offered, the confirmed move is refused (a ReportItem names it) and the folder stays in place, not in the
    // Recycle Bin. Only the test's own folder could move.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AddProtectedFolderUsesTheFolderPickerAndTheGateUsesTheList()
    {
        using var settings = new SettingsBackup();
        using var fixture = new MoveFixture("addprotect");
        fixture.File(@"guarded\a.bin", 100 * KB, seed: 45);
        fixture.File(@"guarded\inner\b.bin", 60 * KB, seed: 46);
        fixture.File("stay.bin", 60 * KB, seed: 47);
        var guarded = fixture.Sub(Guarded);
        var inner = fixture.Sub(@"guarded\inner");
        var identity = MoveFixture.Identity(guarded);
        SettingsBackup.WriteProtectedFolders();

        using var app = AppSession.Start("--scan", fixture.Path);
        try
        {
            Ui.WaitForScan(app);
            Assert.IsTrue(Ui.TileMoveEnabled(app, Ui.Map(app), Guarded), "precondition: the folder is movable before it is protected");

            var view = OpenSettings(app);
            Assert.IsNull(RowOf(view, guarded), "precondition: the folder is listed before Add: " + RowNames(view));
            var add = AppSession.Find(view, "AddProtectedFolder", Ui.ElementTimeout);
            Assert.IsTrue(add.Current.IsEnabled, "AddProtectedFolder is disabled");
            Ui.Click(add);
            FolderPicker.Choose(app, guarded, Ui.ElementTimeout);

            Assert.IsTrue(AppSession.WaitUntil(() => RowOf(view, guarded) is not null, Ui.ElementTimeout), $"no ProtectedFolder row named {guarded} after Add: {RowNames(view)}");
            var message = AppSession.Find(view, "ProtectedFoldersMessage", Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => message.Current.Name == Text.ProtectedFolderAdded, Ui.ElementTimeout),
                $"ProtectedFoldersMessage \"{message.Current.Name}\", expected \"{Text.ProtectedFolderAdded}\"");
            Assert.IsTrue(AppSession.WaitUntil(() => Saved(guarded), Ui.ElementTimeout),
                "the settings file does not hold the added folder (not saved at once): " + string.Join(", ", SettingsBackup.LastProtectedFolders() ?? []));

            Ui.Click(AppSession.Find(view, "AddProtectedFolder", Ui.ElementTimeout));
            FolderPicker.Choose(app, inner, Ui.ElementTimeout);
            Assert.IsTrue(AppSession.WaitUntil(() => message.Current.Name == Text.ProtectedFolderAlreadyCovered, Ui.ElementTimeout),
                $"adding a folder inside a protected one: ProtectedFoldersMessage \"{message.Current.Name}\", expected \"{Text.ProtectedFolderAlreadyCovered}\"");
            Assert.IsNull(RowOf(view, inner), "a folder inside a protected one was added as a row: " + RowNames(view));
            Assert.AreEqual(1, FolderRows(view).Count, "rows after adding a covered folder: " + RowNames(view));

            Ui.BackToOverview(app, "SettingsView");
            var tile = TileShown(app, Guarded);
            if (AppSession.WaitUntil(() => (Ui.TileOf(Ui.Map(app), Guarded).Current.HelpText ?? string.Empty).Contains(Text.HintProtectedByYou, StringComparison.Ordinal), TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("the map shows the folder display-only at once");
                AssertProtectedTile(app, Guarded, guarded);
                return;
            }

            Console.WriteLine("the map still offers Move on the folder; the gate must refuse it");
            var move = Ui.MenuItemOf(app, Ui.TileOf(Ui.Map(app), Guarded), "MoveToRecycleBin");
            if (!move.Current.IsEnabled)
            {
                Keys.Press(Keys.Escape);
                Assert.Fail("the tile's Move is disabled but its HelpText lacks " + Text.HintProtectedByYou + ": " + tile.Current.HelpText);
            }

            Ui.Click(move);
            Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
            var report = Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
            Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(report, "ReportItem").Any(r => r.Current.Name.StartsWith(guarded, StringComparison.OrdinalIgnoreCase)), Ui.ElementTimeout),
                "the move of a protected folder was not refused in the report: " + Ui.Dump(report));
            Assert.IsTrue(Directory.Exists(guarded), "the protected folder was moved");
            Assert.AreEqual(identity, MoveFixture.Identity(guarded), "the protected folder is not the same item");
            Assert.IsNull(RecycleBin.Holding(guarded), "the protected folder is in the Recycle Bin");
        }
        finally
        {
            FolderPicker.CancelIfOpen(app);
        }
    }

    // ---- a settings file that cannot be read --------------------------------------------------------------------------

    // Plan Design row Settings.LeanSettings: a file with content but no line that parses gives the defaults, the app shows
    // Text.SettingsUnreadable, and the file is left as it is. The text is looked for in every window of the app at launch
    // and with Settings open; the list then has no row; after the app has closed the file's bytes are unchanged.
    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void AnUnreadableSettingsFileIsReportedAndLeftAsItIs()
    {
        using var settings = new SettingsBackup();
        SettingsBackup.WriteRaw("this is not a settings line\n{\"version\":1,\"protectedFolders\":[\"C:\\\\Users\n");
        var before = SettingsBackup.Bytes()!;

        using (var app = AppSession.Start())
        {
            app.Window(Ui.WindowTimeout);
            bool Shown() => Ui.TopWindows(app).Any(w => Ui.AnyNameContains(w, Text.SettingsUnreadable));
            var atLaunch = AppSession.WaitUntil(Shown, TimeSpan.FromSeconds(5));
            var view = OpenSettings(app);
            Assert.IsTrue(atLaunch || AppSession.WaitUntil(Shown, Ui.ElementTimeout),
                $"\"{Text.SettingsUnreadable}\" is shown neither at launch nor in Settings: {Ui.Dump(view)}");
            AppSession.Find(view, "ProtectedFoldersList", Ui.ElementTimeout);
            Assert.AreEqual(0, FolderRows(view).Count, "an unreadable settings file gave protected folders: " + RowNames(view));
            Close(app);
        }

        CollectionAssert.AreEqual(before, SettingsBackup.Bytes(), "the unreadable settings file was changed");
    }
}
