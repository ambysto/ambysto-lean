using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-24: closing the window never ends Lean between an item's move and its journal entry (docs/spec/02 "Closing the window",
/// 05 Put back, 07). A close during a session waits until every approved item is moved and journaled; a close during Put back waits for it;
/// a close while only the confirmation is open ends it as Cancel. The slow move and Put back come from the Debug-only <c>--move-delay</c>,
/// so those tests run in the pro-debug suite. Only the test's own fixture items under <c>%USERPROFILE%\LeanUiMove-*</c> are moved.</summary>
public sealed partial class MoveAndPutBackTests
{
    private const string MoveDelay = "6000";

    /// <summary>Closes the window as its close button does (WM_CLOSE through UI Automation's WindowPattern).</summary>
    private static void CloseWindow(AppSession app)
    {
        var window = app.Window(WindowTimeout);
        Assert.IsTrue(window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern), "the main window has no WindowPattern");
        ((WindowPattern)pattern).Close();
    }

    private static string StatusOf(AppSession app) => AppSession.Find(app.Window(WindowTimeout), "Status", ElementTimeout).Current.Name;

    /// <summary>The journal's lines for <paramref name="path"/> with <paramref name="result"/> (docs/spec/05 Entry), read without the app.</summary>
    private static int JournalLines(string path, string result)
    {
        if (!File.Exists(JournalBackup.Path))
        {
            return 0;
        }

        var count = 0;
        using var stream = new FileStream(JournalBackup.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("originalPath", out var original) && string.Equals(original.GetString(), path, StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("result", out var value) && value.GetString() == result)
                {
                    count++;
                }
            }
            catch (JsonException)
            {
                // a torn last line is ignored, as the app does
            }
        }

        return count;
    }

    // While the session moves its first item (held by --move-delay), the window is closed: it stays open with "Lean closes when moving ends",
    // then closes by itself once the session has ended (owner decision 2026-10-23); both approved items are in the Recycle Bin, each with one
    // "trashed" journal line, the kept copy is in place, and History (a new run of the app) lists both. Then a Put back from History is held the same way: the window stays
    // with "Lean closes when putting back ends", closes by itself, and the item is back byte for byte with a "restored" line.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClosingDuringAMoveFinishesTheSessionAndJournalsEveryItem()
    {
        ProApp.RequireDebugApp();
        using var fixture = new MoveFixture("closehold");
        var (a, b, c) = Duplicates(fixture);
        var facts = new[] { a, b, c }.ToDictionary(p => p, p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);

        string moved;
        using (var app = AppSession.Start("--scan", fixture.Path, "--move-delay", MoveDelay))
        {
            WaitForScan(app);
            var view = OpenDuplicatesAndFind(app);
            Tick(view, a);
            Tick(view, c);
            Click(AppSession.Find(view, "MoveToRecycleBin", ElementTimeout));
            AssertConfirmation(app, FindAnywhere(app, "ConfirmMove", ElementTimeout), 2);
            Click(FindAnywhere(app, "ConfirmMoveButton", ElementTimeout));

            var first = string.Empty;
            Assert.IsTrue(AppSession.WaitUntil(() => (first = StatusOf(app)).StartsWith(Text.MovingProgress(1, 2, string.Empty), StringComparison.Ordinal), ElementTimeout),
                $"the status line never read \"Moving 1 of 2: <name>\"; it reads \"{first}\"");
            moved = first.EndsWith(Path.GetFileName(a), StringComparison.OrdinalIgnoreCase) ? a : c;
            Assert.IsTrue(first.EndsWith(Path.GetFileName(moved), StringComparison.OrdinalIgnoreCase), $"the status line \"{first}\" names neither ticked copy");
            Assert.AreEqual(0, JournalLines(moved, "trashed"), "precondition: the item in progress has no journal line yet (the move delay is too short)");

            CloseWindow(app);
            Assert.IsTrue(AppSession.WaitUntil(() => StatusOf(app) == Text.LeanClosesWhenMovingEnds, ElementTimeout),
                $"the status line after the close is \"{StatusOf(app)}\", expected \"{Text.LeanClosesWhenMovingEnds}\"");
            Assert.IsFalse(app.Process.HasExited, "the app ended at the close although an item was being moved");
            Assert.AreEqual(0, app.ExitCode(MoveTimeout), "the app did not close by itself after the move ended");
        }

        foreach (var path in new[] { a, c })
        {
            AssertInRecycleBin(path, facts[path].Identity);
            Assert.AreEqual(1, JournalLines(path, "trashed"), path + " was approved but has no single \"trashed\" journal line after the held close");
        }

        AssertInPlaceAndNotInRecycleBin(b, facts[b].Identity, facts[b].Hash);

        using (var app = AppSession.Start("--scan", fixture.Path, "--move-delay", MoveDelay))
        {
            WaitForScan(app);
            Click(Ui.SidebarItem(app, "History"));
            var history = FindAnywhere(app, "HistoryView", ElementTimeout);
            var other = moved == a ? c : a;
            Assert.IsTrue(AppSession.WaitUntil(() => EntryOf(history, other, Text.ResultTrashed) is not null, ElementTimeout),
                $"History has no entry row of {other} with \"{Text.ResultTrashed}\": {HistoryDump(history)}");
            AutomationElement? entry = null;
            Assert.IsTrue(AppSession.WaitUntil(() => (entry = EntryOf(history, moved, Text.ResultTrashed)) is not null, ElementTimeout),
                $"History has no entry row of {moved} with \"{Text.ResultTrashed}\": {HistoryDump(history)}");
            var putBack = Table.InRow(entry!, "PutBackEntry");
            Assert.IsNotNull(putBack, "no PutBackEntry in the entry row of " + moved);
            Click(putBack);

            CloseWindow(app);
            Assert.IsTrue(AppSession.WaitUntil(() => StatusOf(app) == Text.LeanClosesWhenPuttingBackEnds, ElementTimeout),
                $"the status line after the close is \"{StatusOf(app)}\", expected \"{Text.LeanClosesWhenPuttingBackEnds}\"");
            Assert.AreEqual(0, JournalLines(moved, "restored"), "precondition: the Put back had ended before the close (the move delay is too short)");
            Assert.IsFalse(app.Process.HasExited, "the app ended at the close although Put back was running");
            Assert.AreEqual(0, app.ExitCode(MoveTimeout), "the app did not close by itself after Put back ended");
        }

        AssertPutBack(moved, facts[moved].Identity, facts[moved].Hash, ElementTimeout);
        Assert.AreEqual(1, JournalLines(moved, "restored"), moved + " was put back but has no single \"restored\" journal line");
    }

    // A close while only the confirmation is open ends it as Cancel and closes the window: nothing is moved and nothing is journaled.
    [TestMethod]
    [TestCategory("Console")]
    public void ClosingWhileTheConfirmationIsOpenMovesNothingAndCloses()
    {
        using var fixture = new MoveFixture("closeconfirm");
        var file = fixture.File("stay-put.bin", 100 * KB, seed: 40);
        fixture.File("other.bin", 60 * KB, seed: 41);
        var identity = MoveFixture.Identity(file);
        var hash = MoveFixture.Hash(file);

        using (var app = AppSession.Start("--scan", fixture.Path))
        {
            var map = WaitForScan(app);
            AssertConfirmation(app, StartMoveFromTile(app, map, "stay-put.bin"), 1);
            CloseWindow(app);
            Assert.AreEqual(0, app.ExitCode(ElementTimeout), "the app did not close after the close ended the confirmation");
        }

        AssertInPlaceAndNotInRecycleBin(file, identity, hash);
        Assert.AreEqual(0, JournalLines(file, "trashed") + JournalLines(file, "skipped"), file + " has a journal line although nothing was confirmed");
    }
}
