// Integrate as windows/tests/Ambysto.Lean.UiTests/MoveAndPutBackTests.UpdateClose.cs.
// Independent acceptance from frozen ASP-11 close/update contract + ASP-24 approved-session completion contract.
// Reads only specs, existing tests and fixtures. No product implementation used. Drafted before the close-hook fix.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

public sealed partial class MoveAndPutBackTests
{
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClosingDuringApprovedMoveCancelsUpdatesImmediatelyAndStillFinishesEveryApprovedItem()
    {
        ProApp.RequireDebugApp();
        using var journalBackup = new JournalBackup();
        JournalBackup.Write([]); // isolate history; original journal restored only after both app processes exit
        using var fixture = new MoveFixture("update-closehold");
        var (a, b, c) = Duplicates(fixture);
        var approved = new[] { a, c };
        var facts = new[] { a, b, c }.ToDictionary(p => p,
            p => (Identity: MoveFixture.Identity(p), Hash: MoveFixture.Hash(p)), StringComparer.OrdinalIgnoreCase);
        var updateFixture = Path.Combine(Path.GetTempPath(), "LeanUpdateClose-" + Guid.NewGuid().ToString("N") + ".json");
        const string feed = "https://lean.ambysto.com/windows/latest.json";
        File.WriteAllText(updateFixture, JsonSerializer.Serialize(new
        {
            body = JsonSerializer.Serialize(new { version = "65535.65535.65535", downloadUrl = "https://lean.ambysto.com/windows/" }),
            delayMilliseconds = 750, launchSucceeds = true
        }));

        string[] UpdateEvents()
        {
            try { return File.Exists(updateFixture + ".events") ? File.ReadAllLines(updateFixture + ".events").Where(s => s.Length > 0).ToArray() : []; }
            catch (IOException) { return []; } // retry an in-progress append during the initial wait
        }

        void InvokeOrObserveDisabled(AppSession app, string id)
        {
            var control = FindAnywhere(app, id, ElementTimeout);
            if (!control.Current.IsEnabled) return;
            try { Click(control); }
            catch (ElementNotEnabledException) { } // a close transition may disable it between observation and Invoke
        }

        void RestoreOnlyThisTestsItems()
        {
            var missing = approved.Where(p => !File.Exists(p) && RecycleBin.Holding(p) is not null).ToArray();
            if (missing.Length == 0) return;
            using var cleanup = AppSession.Start("--scan", fixture.Path);
            WaitForScan(cleanup);
            Click(Ui.SidebarItem(cleanup, "History"));
            foreach (var path in missing)
            {
                AutomationElement? entry = null;
                Assert.IsTrue(AppSession.WaitUntil(() =>
                {
                    var history = FindAnywhere(cleanup, "HistoryView", ElementTimeout);
                    entry = EntryOf(history, path, Text.ResultTrashed);
                    return entry is not null;
                }, ElementTimeout), "cleanup history must contain only this test's moved item: " + path);
                var putBack = Table.InRow(entry!, "PutBackEntry");
                Assert.IsNotNull(putBack, "cleanup PutBackEntry for " + path);
                Click(putBack);
                AssertPutBack(path, facts[path].Identity, facts[path].Hash, MoveTimeout);
                Assert.IsTrue(AppSession.WaitUntil(() => JournalLines(path, "restored") == 1, ElementTimeout),
                    "cleanup Put back is journaled before restoring the pre-test journal");
            }
        }

        try
        {
            using (var app = AppSession.Start("--scan", fixture.Path, "--move-delay", "3000", "--update-qa", updateFixture))
            {
                WaitForScan(app);
                var duplicates = OpenDuplicatesAndFind(app);
                Tick(duplicates, a);
                Tick(duplicates, c);
                Click(AppSession.Find(duplicates, "MoveToRecycleBin", ElementTimeout));
                AssertConfirmation(app, FindAnywhere(app, "ConfirmMove", ElementTimeout), 2);
                Click(FindAnywhere(app, "ConfirmMoveButton", ElementTimeout));
                Assert.IsTrue(AppSession.WaitUntil(() => StatusOf(app).StartsWith(Text.MovingProgress(1, 2, string.Empty), StringComparison.Ordinal), ElementTimeout),
                    "precondition: the first approved move must be running");
                Assert.AreEqual(0, approved.Sum(p => JournalLines(p, "trashed")), "delay must hold the approved move before its journal entry");

                Ui.OpenView(app, "About", "AboutView");
                Click(FindAnywhere(app, "CheckForUpdates", ElementTimeout));
                Assert.IsTrue(AppSession.WaitUntil(() => UpdateEvents().SequenceEqual(new[] { "GET " + feed }), TimeSpan.FromSeconds(3)),
                    "precondition: one delayed update GET, no page opened before close");
                Assert.IsFalse(FindAnywhere(app, "CheckForUpdates", ElementTimeout).Current.IsEnabled, "update check is in flight");
                Assert.AreEqual(0, approved.Sum(p => JournalLines(p, "trashed")), "close must be requested while the approved move is still held");

                CloseWindow(app); // initial X, while ASP-24 holds actual window destruction until the session ends
                Assert.IsFalse(app.Process.HasExited, "closing intent must preserve the approved move session");
                Assert.IsTrue(AppSession.WaitUntil(() => StatusOf(app) == Text.LeanClosesWhenMovingEnds, TimeSpan.FromSeconds(2)),
                    "the close was accepted and is being held for the approved move");

                // Cancellation alone must not re-enable actions that can issue a new check or browser launch while close is held.
                InvokeOrObserveDisabled(app, "CheckForUpdates");
                InvokeOrObserveDisabled(app, "Website");
                Thread.Sleep(1100); // longer than the 750 ms fixture response, shorter than the outstanding approved work
                Assert.IsFalse(app.Process.HasExited, "the delayed approved session should still hold this close");
                CollectionAssert.AreEqual(new[] { "GET " + feed }, UpdateEvents(), "initial X cancels the pending check; no browser or replacement GET");
                InvokeOrObserveDisabled(app, "CheckForUpdates");
                InvokeOrObserveDisabled(app, "Website");

                Assert.AreEqual(0, app.ExitCode(MoveTimeout), "the held close finishes after every approved item is processed");
                CollectionAssert.AreEqual(new[] { "GET " + feed }, UpdateEvents(), "no update or browser action after closing intent, through actual exit");
            }

            foreach (var path in approved)
            {
                AssertInRecycleBin(path, facts[path].Identity);
                Assert.AreEqual(1, JournalLines(path, "trashed"), "every approved item must have exactly one successful move entry: " + path);
            }
            AssertInPlaceAndNotInRecycleBin(b, facts[b].Identity, facts[b].Hash);
        }
        finally
        {
            try { RestoreOnlyThisTestsItems(); }
            finally
            {
                File.Delete(updateFixture);
                File.Delete(updateFixture + ".events");
            }
        }
        foreach (var path in new[] { a, b, c })
            AssertInPlaceAndNotInRecycleBin(path, facts[path].Identity, facts[path].Hash);
    }
}
