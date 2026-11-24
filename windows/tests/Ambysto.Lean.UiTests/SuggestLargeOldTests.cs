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

/// <summary>ASP-17 phase C (intent/2026-10-20-asp-17-suggest.md): Suggest in Large and old, Pro, in a Debug build started with
/// <c>--pro</c> (TestCategory ProDebug only). The fixtures are scratch folders inside the user's real Downloads known folder
/// (<see cref="DownloadsFixture"/>: the installer rule is about that folder and the app reads it; it cannot be pointed at a scratch
/// folder without changing the user's own setting), named <c>LeanUiSuggest-*</c>, removed in <c>Dispose</c>; the scan is of the
/// scratch folder, so the lists hold only the test's own files. The shared fixture holds an old installer, an old disk image, a
/// recent installer, a 1 GiB file unchanged for 400 days and a 150 MB one unchanged for 400 days: Suggest lists the first two and
/// the 1 GiB one, the filtered list (100 MB, 12 months) the two big ones.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SuggestLargeOldTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;

    private static DownloadsFixture? main;
    private static readonly Dictionary<string, DateTime> Written = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    private const string Zip = "suggest-setup.zip";
    private const string Iso = "suggest-disc.iso";
    private const string RecentZip = "suggest-recent.zip";
    private const string Movie = "suggest-movie.mkv";
    private const string Medium = "suggest-medium.bin";

    /// <summary>The shared scratch folder, made once (the 1 GiB file takes a few seconds to write).</summary>
    private static DownloadsFixture Shared()
    {
        lock (Gate)
        {
            if (main is not null)
            {
                return main;
            }

            var tree = new DownloadsFixture("main");
            foreach (var (name, length, days) in new[] { (Zip, 200 * KB, 200), (Iso, 300 * KB, 120), (RecentZip, 100 * KB, 30), (Movie, ByteUnits.GB, 400), (Medium, 150 * MB, 400) })
            {
                Written[name] = File.GetLastWriteTimeUtc(tree.File(name, length, days, seed: name.Length));
            }

            main = tree;
            return tree;
        }
    }

    [ClassCleanup]
    public static void RemoveTheTree()
    {
        main?.Dispose();
    }

    private static string[] Suggested => [Zip, Iso, Movie];

    private static long SuggestedBytes() => Suggested.Sum(n => MoveFixture.AllocatedBytes(Shared().Sub(n)));

    private static AutomationElement OpenView(AppSession app)
    {
        Ui.WaitForScan(app);
        return Ui.OpenView(app, "LargeOld", "LargeOldView");
    }

    private static AutomationElement? Box(AutomationElement view, string name) => Table.ControlWith(view, "LargeOldSelect", name);

    private static bool Listed(AutomationElement view, string name) => Box(view, name) is not null;

    private static void WaitForRow(AutomationElement view, string name) =>
        Assert.IsTrue(AppSession.WaitUntil(() => Listed(view, name), Ui.ElementTimeout), $"no row for {name}: {Table.Dump(view, "LargeOldSelect")}");

    private static void WaitForSuggestion(AutomationElement view) =>
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "ClearSuggestion") is not null && SuggestUi.HasWhyColumn(view), Ui.ElementTimeout),
            "Suggest did not show its own list (no ClearSuggestion and Why column): " + Table.Dump(view, "LargeOldSelect"));

    private static string WhyFor(string name) => name == Movie
        ? Text.WhyLargeUnused(new DateTimeOffset(Written[name], TimeSpan.Zero))
        : Text.WhyOldInstaller(new DateTimeOffset(Written[name], TimeSpan.Zero));

    private static readonly string[] FilterIds = ["MinimumSize", "MinimumAge", "ShowSystemItems"];

    private static bool FiltersEnabled(AutomationElement view) =>
        FilterIds.All(id => AppSession.TryFind(view, id) is { } f && f.Current.IsEnabled);

    private static bool FiltersDisabled(AutomationElement view) =>
        FilterIds.All(id => AppSession.TryFind(view, id) is { } f && !f.Current.IsEnabled);

    // Contract "Large and old": Suggest replaces the list with Suggest.LargeOld's rows from the whole scan, all ticked, with a Why
    // column: the old installer and the old disk image in Downloads (Text.WhyOldInstaller) and the 1 GiB file unchanged for a year
    // (Text.WhyLargeUnused), not the recent installer, not the 150 MB file. The size, age and Show system items filters are disabled
    // while it shows; ClearSuggestion is there; the status line says Text.SuggestedFiles(count, size); Move is enabled.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestReplacesTheListWithTheSuggestedFilesTickedAndWithTheirWhy()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Movie);
        WaitForRow(view, Medium);
        Assert.IsFalse(Listed(view, Zip) || Listed(view, Iso), "precondition: the installers are not in the filtered list (100 MB, 12 months)");
        var button = SuggestUi.AssertPro(view, "Large and old");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), "ClearSuggestion is shown before Suggest");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column before Suggest");
        Assert.IsTrue(FiltersEnabled(view), "the filters are not enabled before Suggest");

        Ui.Click(button);
        WaitForSuggestion(view);

        Assert.AreEqual(Text.ClearSuggestion, AppSession.Find(view, "ClearSuggestion", Ui.ElementTimeout).Current.Name, "the label of ClearSuggestion");
        Assert.IsTrue(AppSession.Find(view, "ClearSuggestion", Ui.ElementTimeout).Current.IsEnabled, "ClearSuggestion is disabled");
        foreach (var name in Suggested)
        {
            var box = Box(view, name);
            Assert.IsNotNull(box, $"{name} is not in the suggestion: {Table.Dump(view, "LargeOldSelect")}");
            Assert.AreEqual(ToggleState.On, Table.State(box), name + " is not ticked");
            Assert.IsTrue(Table.RowName(box).Contains(WhyFor(name), StringComparison.Ordinal), $"the row of {name} does not show \"{WhyFor(name)}\": \"{Table.RowName(box)}\"");
        }

        Assert.IsFalse(Listed(view, RecentZip), "the recent installer is suggested");
        Assert.IsFalse(Listed(view, Medium), "the 150 MB file is suggested (it is neither an installer in Downloads nor 1 GiB)");
        Assert.AreEqual(3, Table.Controls(view, "LargeOldSelect").Count, "the suggestion lists other rows: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.On), "a row of the suggestion is not ticked");
        Assert.IsTrue(FiltersDisabled(view), "the size, age and Show system items filters are not all disabled while the suggestion shows");
        var expected = Text.SuggestedFiles(3, ByteUnits.Format(SuggestedBytes()));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, Ui.ElementTimeout), $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\"");
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout).Current.IsEnabled, Ui.ElementTimeout), "Move is disabled with rows ticked");
    }

    // Contract: Clear suggestion returns to the filtered list with nothing ticked: the two big files, no Why column, no
    // ClearSuggestion, the filters enabled again, Move disabled.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClearSuggestionBringsBackTheFilteredListWithNothingTicked()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Movie);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);

        Ui.Click(AppSession.Find(view, "ClearSuggestion", Ui.ElementTimeout));

        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "ClearSuggestion") is null && !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), "ClearSuggestion or the Why column stays");
        WaitForRow(view, Movie);
        WaitForRow(view, Medium);
        Assert.IsFalse(Listed(view, Zip) || Listed(view, Iso), "the installers stay listed after Clear suggestion");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), "a row is ticked after Clear suggestion: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsTrue(AppSession.WaitUntil(() => FiltersEnabled(view), Ui.ElementTimeout), "the filters stay disabled after Clear suggestion");
        Assert.IsFalse(AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout).Current.IsEnabled, "Move is enabled with nothing ticked");
        Assert.IsTrue(SuggestUi.EnabledNow(SuggestUi.TryButton(view)), "Suggest is disabled after Clear suggestion");
    }

    // ASP-22: Clear suggestion takes Suggest's status line away. Large and old shows the status line the scan left ("Scanned ..."); Suggest
    // writes "Suggested 3 files, ..." over it, and Clear suggestion puts the scan's line back, never leaving "Suggested ..." under a list
    // that suggests nothing.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClearSuggestionTakesSuggestsStatusLineAway()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Movie);
        var before = SuggestUi.Status(app);
        Assert.IsFalse(string.IsNullOrEmpty(before), "precondition: the scan left no status line");
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);
        var suggested = Text.SuggestedFiles(3, ByteUnits.Format(SuggestedBytes()));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == suggested, Ui.ElementTimeout), $"the status line is \"{SuggestUi.Status(app)}\", expected \"{suggested}\"");

        Ui.Click(AppSession.Find(view, "ClearSuggestion", Ui.ElementTimeout));

        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "ClearSuggestion") is null, Ui.ElementTimeout), "ClearSuggestion stays");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == before, Ui.ElementTimeout),
            $"after Clear suggestion the status line is \"{SuggestUi.Status(app)}\", expected the line from before Suggest, \"{before}\"");
    }

    // ASP-22: Suggest, Clear, Suggest, Clear: each Clear puts back the line from before Suggest, never the line of the Suggest before.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestAndClearTwiceLeavesTheScanStatusLine()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Movie);
        var before = SuggestUi.Status(app);
        var suggested = Text.SuggestedFiles(3, ByteUnits.Format(SuggestedBytes()));
        for (var round = 1; round <= 2; round++)
        {
            Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
            WaitForSuggestion(view);
            Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == suggested, Ui.ElementTimeout), $"round {round}: the status line is \"{SuggestUi.Status(app)}\", expected \"{suggested}\"");
            Ui.Click(AppSession.Find(view, "ClearSuggestion", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => AppSession.TryFind(view, "ClearSuggestion") is null && !SuggestUi.HasWhyColumn(view), Ui.ElementTimeout), $"round {round}: Clear suggestion did not end the suggestion");
            Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == before, Ui.ElementTimeout), $"round {round}: after Clear suggestion the status line is \"{SuggestUi.Status(app)}\", expected \"{before}\"");
            WaitForRow(view, Movie);
        }
    }

    // ASP-22 (docs/spec/03, 07): with the Why column shown, at a window 1280 DIP wide (1280 x the DPI scale in pixels) every other column is shown in full: the texts of a
    // row (name, folder, size, date) end inside the list, the row is one line, and Why takes what is left after them (it is trimmed, its
    // tooltip the full text). Checked at the default width too. The window is put back at the end; Inconclusive (never a pass) when it
    // cannot be made that wide (the screen is narrower).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void At1280DipEveryColumnButWhyIsShownInFull()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var window = app.Window(Ui.WindowTimeout);
        if (!window.TryGetCurrentPattern(TransformPattern.Pattern, out var pattern) || !((TransformPattern)pattern).Current.CanResize)
        {
            Assert.Inconclusive("the window cannot be resized through UI Automation: the 1280 DIP layout was not checked");
        }

        var transform = (TransformPattern)pattern;
        var original = window.Current.BoundingRectangle;
        var view = OpenView(app);
        WaitForRow(view, Movie);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);
        AssertAllButWhyAreWhole(app, view, "default width");
        try
        {
            var target = 1280 * WindowOnScreen.Scale(new AutomationElementHandle(new nint(window.Current.NativeWindowHandle)));
            transform.Resize(target, original.Height);
            System.Threading.Thread.Sleep(1000);
            var actual = app.Window(Ui.WindowTimeout).Current.BoundingRectangle.Width;
            if (Math.Abs(actual - target) > 8)
            {
                Assert.Inconclusive($"the window could not be made 1280 DIP ({target:F0} px) wide (it is {actual:F0} px): the 1280 DIP layout was not checked");
            }

            AssertAllButWhyAreWhole(app, view, "1280 DIP");
        }
        finally
        {
            transform.Resize(original.Width, original.Height);
            System.Threading.Thread.Sleep(800);
        }
    }

    private static FontFile? mono;

    /// <summary>The width in pixels of <paramref name="text"/> in JetBrains Mono Regular at 14 DIP.</summary>
    private static double TextWidth(string text, double scale)
    {
        mono ??= FontFile.Read(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppSession.AppPath())!, "Assets", "Fonts", "JetBrainsMono-Regular.ttf"));
        return text.Sum(c => mono.Advance(c) ?? mono.UnitsPerEm / 2) * 14.0 * scale / mono.UnitsPerEm;
    }

    private static void AssertAllButWhyAreWhole(AppSession app, AutomationElement view, string where)
    {
        SuggestUi.AssertOneLineRows(app, view, "LargeOldSelect", where, atLeast: 3);
        var list = AppSession.Find(view, "LargeOldList", Ui.ElementTimeout).Current.BoundingRectangle;
        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle)));
        foreach (var name in Suggested)
        {
            var row = Table.RowOf(Box(view, name) ?? throw new AssertFailedException($"{where}: no row for {name}"))
                ?? throw new AssertFailedException($"{where}: the box of {name} is in no row");
            var texts = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                .Where(t => !t.Current.BoundingRectangle.IsEmpty).ToList();
            foreach (var text in texts)
            {
                var rect = text.Current.BoundingRectangle;
                Assert.IsTrue(rect.Left >= list.Left - 1 && rect.Right <= list.Right + 1,
                    $"{where}: the text \"{text.Current.Name}\" of {name} lies {rect} outside the list {list}");
                if (text.Current.Name == WhyFor(name))
                {
                    continue; // Why alone may be trimmed
                }

                // Not trimmed: a TextBlock cut with an ellipsis keeps its cell's width, so the cell must be as wide as the text drawn in it
                // (JetBrains Mono, 14 DIP, advances read from the bundled font file) needs. The old layout (Folder 200 px for a path shortened to
                // 40 characters = 336 px) fails here.
                var needed = TextWidth(text.Current.Name, scale);
                Assert.IsTrue(rect.Width + 1 >= needed, $"{where}: the cell {rect} of \"{text.Current.Name}\" in the row of {name} is {rect.Width:F0} px wide, the text needs {needed:F0} px: it is trimmed");
            }

            var date = Table.ShortDate(Written[name]);
            var dateText = texts.FirstOrDefault(t => t.Current.Name == date);
            Assert.IsNotNull(dateText, $"{where}: no date cell \"{date}\" in the row of {name}: " + string.Join(" | ", texts.Select(t => t.Current.Name)));
            var whyText = texts.FirstOrDefault(t => t.Current.Name == WhyFor(name));
            Assert.IsNotNull(whyText, $"{where}: no Why cell \"{WhyFor(name)}\" in the row of {name}: " + string.Join(" | ", texts.Select(t => t.Current.Name)));
            var dateRect = dateText.Current.BoundingRectangle;
            var whyRect = whyText.Current.BoundingRectangle;
            Assert.IsTrue(whyRect.Left >= dateRect.Right - 1, $"{where}: the Why cell {whyRect} starts inside the date cell {dateRect} of {name}");
            Assert.IsTrue(whyRect.Width > 0, $"{where}: the Why cell of {name} has no width");
        }

        var header = SuggestUi.WhyHeaders(view).FirstOrDefault();
        Assert.IsNotNull(header, where + ": no Why header");
        Assert.IsTrue(header.Current.BoundingRectangle.Right <= list.Right + 1, $"{where}: the Why header {header.Current.BoundingRectangle} ends outside the list {list}");
    }

    // Contract: an empty suggestion says Text.NothingToSuggest in the status line and leaves the list as it was: a scan with only a 150 MB file
    // unchanged for 400 days and a recent installer has nothing to suggest.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void NothingToSuggestIsSaidAndTheListStaysAsItWas()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("empty");
        fixture.File("empty-medium.bin", 150 * MB, 400);
        fixture.File("empty-recent.zip", 100 * KB, 30);
        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        WaitForRow(view, "empty-medium.bin");
        var button = SuggestUi.AssertPro(view, "Large and old");
        var rows = Table.Dump(view, "LargeOldSelect");

        Ui.Click(button);

        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == Text.NothingToSuggest, Ui.ElementTimeout),
            $"the status line is \"{SuggestUi.Status(app)}\", expected \"{Text.NothingToSuggest}\"");
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(rows, Table.Dump(view, "LargeOldSelect"), "the list changed with nothing to suggest");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), "a row is ticked with nothing to suggest");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column with nothing to suggest");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), "ClearSuggestion is shown with nothing to suggest");
        Assert.IsTrue(FiltersEnabled(view), "the filters are disabled with nothing to suggest");
    }

    // Contract "The confirmation": above 100 files Suggest asks first with Text.SuggestConfirm(101, 0) (no program file: .zip),
    // Tick and Cancel. Cancel leaves the list as it was: no Why, no ClearSuggestion, the filters on, the status line unchanged.
    // Again, Tick: the suggestion shows with Text.SuggestedFiles(101, size). 100 files need no confirmation (next test).
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestAsksFirstAboveOneHundredFilesCancelChangesNothingAndTickShowsThem()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("bulk");
        for (var i = 0; i < 101; i++)
        {
            fixture.File($"bulk-{i:D3}.zip", 8 * KB, 100, seed: i);
        }

        fixture.File("bulk-medium.bin", 150 * MB, 400);
        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        WaitForRow(view, "bulk-medium.bin");
        var button = SuggestUi.AssertPro(view, "Large and old");
        var status = SuggestUi.Status(app);
        var rows = Table.Dump(view, "LargeOldSelect");

        Ui.Click(button);
        var (scope, text) = SuggestDialog.Wait(app);
        Assert.AreEqual(Text.SuggestConfirm(101, 0), text, "the confirmation");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a Why column while the confirmation is open");
        Assert.AreEqual(status, SuggestUi.Status(app), "the status line changed while the confirmation is open");

        SuggestDialog.Press(app, scope, Text.Cancel);
        System.Threading.Thread.Sleep(500);
        Assert.AreEqual(rows, Table.Dump(view, "LargeOldSelect"), "the list changed after Cancel");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "Cancel showed a Why column");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), "Cancel showed ClearSuggestion");
        Assert.IsTrue(FiltersEnabled(view), "Cancel disabled the filters");
        Assert.AreEqual(status, SuggestUi.Status(app), "the status line changed after Cancel");

        Ui.Click(SuggestUi.Button(view));
        SuggestDialog.Press(app, SuggestDialog.Wait(app).Scope, Text.RuleConfirmTick);
        WaitForSuggestion(view);
        var expected = Text.SuggestedFiles(101, ByteUnits.Format(101 * 8 * KB));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, Ui.ElementTimeout), $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\"");
        Assert.IsFalse(Listed(view, "bulk-medium.bin"), "the 150 MB file is in the suggestion");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.On), "a row of the suggestion is not ticked");
    }

    // Contract: "more than 100" asks; exactly 100 files are ticked at once, with no confirmation.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ExactlyOneHundredFilesAreSuggestedAtOnceWithNoConfirmation()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("hundred");
        for (var i = 0; i < 100; i++)
        {
            fixture.File($"hundred-{i:D3}.zip", 8 * KB, 100, seed: i);
        }

        fixture.File("hundred-medium.bin", 150 * MB, 400);
        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        WaitForRow(view, "hundred-medium.bin"); // the list is read before Suggest is pressed (a refresh under way must not be mistaken for the answer)
        var button = SuggestUi.AssertPro(view, "Large and old");

        Ui.Click(button);

        Assert.IsFalse(AppSession.WaitUntil(() => SuggestDialog.Shown(app), Ui.NeverWatch), "100 files asked for a confirmation");
        WaitForSuggestion(view);
        var expected = Text.SuggestedFiles(100, ByteUnits.Format(100 * 8 * KB));
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.Status(app) == expected, Ui.ElementTimeout), $"the status line is \"{SuggestUi.Status(app)}\", expected \"{expected}\"");
    }

    // Contract "Display-only rows are never ticked": a file in a folder the user protects (docs/spec/02) is never suggested, however
    // old; the unprotected one beside it is. (The settings file is replaced for the test and put back.)
    [TestMethod]
    [TestCategory("ProDebug")]
    [TestCategory("SharedState")]
    public void AFileInAProtectedFolderIsNeverSuggested()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("protected");
        fixture.File(@"keep\protected-setup.zip", 100 * KB, 200);
        fixture.File("open-setup.zip", 100 * KB, 200);
        using var settings = new SettingsBackup();
        SettingsBackup.WriteProtectedFolders(fixture.Sub("keep"));
        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));

        WaitForSuggestion(view);
        Assert.IsTrue(Listed(view, "open-setup.zip"), "the unprotected installer is not suggested: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsFalse(Listed(view, "protected-setup.zip"), "a file in a protected folder is suggested: " + Table.Dump(view, "LargeOldSelect"));
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => b.Current.IsEnabled && Table.State(b) == ToggleState.On), "a row is disabled or unticked in the suggestion");
    }

    // Contract "Busy states": Suggest is off while a move is being confirmed. A row of the filtered list is ticked by hand, Move opens
    // the confirmation; Cancel brings Suggest back.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestIsOffWhileAMoveIsBeingConfirmed()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Medium);
        SuggestUi.AssertPro(view, "Large and old");
        Table.Tick(Box(view, Medium)!, () => Box(view, Medium), Medium);
        var move = AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move stays disabled with a row ticked");

        Ui.Click(move);
        Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        System.Threading.Thread.Sleep(500);
        Assert.IsFalse(SuggestUi.EnabledNow(SuggestUi.TryButton(view)), "Suggest is enabled while the move is being confirmed");

        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout), "the confirmation stays open after Cancel");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(view)), Ui.ElementTimeout), "Suggest stays disabled after the confirmation was cancelled");
    }

    // Contract "Busy states" and "When the Why column goes": Suggest is off while a scan runs, in Temporary files and in Large and old
    // (a walk slowed by --scan-delay over 400 folders (about 12 s); Inconclusive when the button is not there to be sampled); it is on again after
    // the scan. And a new scan (Scan pressed again) takes the suggestion away: no Why column, no ClearSuggestion.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void SuggestIsOffWhileAScanRunsAndANewScanClearsTheSuggestion()
    {
        ProApp.RequireDebugApp();
        using var tree = new MoveFixture("suggest-scan");
        for (var i = 0; i < 400; i++)
        {
            tree.File($@"d{i:D3}\f.bin", 4 * KB, i);
        }

        using var app = ProApp.StartPro("--scan", tree.Path, "--scan-delay", "150");
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.ScanRunning(app), Ui.ElementTimeout), "the scan did not start");
        var samples = 0;
        var enabled = 0;
        foreach (var (item, viewId) in new[] { ("TemporaryFiles", "TempView"), ("LargeOld", "LargeOldView") })
        {
            AutomationElement view;
            try
            {
                view = Ui.OpenView(app, item, viewId);
            }
            catch (Exception e) when (e is InvalidOperationException or ElementNotEnabledException or AssertFailedException)
            {
                Assert.Inconclusive($"the {item} item of the sidebar cannot be used while a scan runs: " + e.Message);
                return;
            }

            while (SuggestUi.ScanRunningAnywhere(app))
            {
                // A sample counts only when the scan still runs after it was read: the scan can end between the loop's check and the
                // read, and a button enabled after the scan is right (gates 2026-10-21, 1 of 47 samples on cacea39).
                if (SuggestUi.TryButton(view) is { } button)
                {
                    var on = SuggestUi.EnabledNow(button);
                    if (SuggestUi.ScanRunningAnywhere(app))
                    {
                        samples++;
                        enabled += on ? 1 : 0;
                    }
                }

                System.Threading.Thread.Sleep(100);
                if (viewId == "TempView" && samples > 15)
                {
                    break;
                }
            }
        }

        Assert.AreEqual(0, enabled, $"Suggest was enabled in {enabled} of {samples} samples while the scan ran");
        Ui.Click(Ui.SidebarItem(app, "Overview"));
        Ui.WaitForScan(app);
        var large = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => SuggestUi.EnabledNow(SuggestUi.TryButton(large)), Ui.ElementTimeout), "Suggest is not enabled once the scan is over");
        if (samples == 0)
        {
            Assert.Inconclusive("Suggest is not in the views' UI Automation tree while a scan runs: there is nothing to be disabled; only the state after the scan was tested");
        }
    }

    // Contract "When the Why column goes": a new scan. The suggestion is shown (Why column, ClearSuggestion); Scan is pressed again;
    // the scan ends and Large and old, opened again, holds the filtered list with nothing ticked.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ANewScanTakesTheSuggestionAway()
    {
        ProApp.RequireDebugApp();
        using var app = ProApp.StartPro("--scan", Shared().Path);
        var view = OpenView(app);
        WaitForRow(view, Movie);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);

        Ui.Click(Ui.SidebarItem(app, "Overview")); // Scan is shown on Overview only
        var scan = AppSession.Find(app.Window(Ui.WindowTimeout), "Scan", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => scan.Current.IsEnabled, Ui.ElementTimeout), "Scan is disabled");
        Ui.Click(scan);

        // The scan of the scratch folder is short; when it has ended, Large and old is opened afresh: the filtered list, no Why column,
        // no ClearSuggestion, the filters on, nothing ticked.
        System.Threading.Thread.Sleep(1500);
        Ui.WaitForScan(app);
        view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        WaitForRow(view, Movie);
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "the Why column stays after a new scan");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), "ClearSuggestion stays after a new scan");
        Assert.IsTrue(FiltersEnabled(view), "the filters stay disabled after a new scan");
        Assert.IsFalse(Listed(view, Zip), "the suggestion's rows stay after a new scan");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), "a row is ticked after a new scan");
    }

    // Contract "One line per row" (ASP-14 lesson), at the default window size: while the suggestion shows, every row is one line high by
    // rectangle after the layout settles, the long Name (about 50 characters) and the long Folder and the Why trimmed with an ellipsis, each
    // with its full text as tooltip. Columns: Move, Name, Folder 200, Size 90, Last modified 110, Why 260.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void EveryRowOfTheSuggestionIsOneLineHighWithLongNames()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("lines");
        const string Folder = "a-folder-with-a-quite-long-name-for-the-test";
        var names = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var name = $"suggest-a-long-installer-name-for-the-row-height-{i}.zip";
            fixture.File(Folder + "\\" + name, 120 * KB, 150 + i, seed: i);
            names.Add(name);
        }

        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);

        SuggestUi.AssertOneLineRows(app, view, "LargeOldSelect", "Large and old suggestion", atLeast: 3);
        var box = Box(view, names[0])!;
        SuggestUi.AssertTooltipHolds(box, names[0], "the Name cell");
        var why = Text.WhyOldInstaller(new DateTimeOffset(File.GetLastWriteTimeUtc(fixture.Sub(Folder + "\\" + names[0])), TimeSpan.Zero));
        SuggestUi.AssertTooltipHolds(box, why, "the Why cell");
    }

    // Contract "Then a real Move of a suggested test file and its Put back": one old installer in Downloads is suggested, ticked, moved
    // through ConfirmMove to the Recycle Bin (gone from its place) and put back byte for byte (same file id and bytes). Nothing else
    // is ticked or moved.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void ASuggestedFileIsMovedAndPutBack()
    {
        ProApp.RequireDebugApp();
        using var fixture = new DownloadsFixture("move");
        var path = fixture.File("move-setup.zip", 200 * KB, 150, seed: 77);
        var identity = MoveFixture.Identity(path);
        var hash = MoveFixture.Hash(path);
        using var app = ProApp.StartPro("--scan", fixture.Path);
        var view = OpenView(app);
        Ui.Click(SuggestUi.AssertPro(view, "Large and old"));
        WaitForSuggestion(view);
        var box = Box(view, "move-setup.zip");
        Assert.IsNotNull(box, "the installer is not suggested: " + Table.Dump(view, "LargeOldSelect"));
        Assert.AreEqual(ToggleState.On, Table.State(box), "the suggested installer is not ticked");
        var move = AppSession.Find(view, "MoveLargeOld", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout), "Move is disabled with the suggestion ticked");

        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        Assert.IsTrue(Ui.AnyNameContains(dialog, "move-setup.zip"), "the confirmation does not list the file: " + Ui.Dump(dialog));
        var report = Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
        Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "an item was skipped or lost: " + Ui.Dump(report));
        Assert.IsTrue(AppSession.WaitUntil(() => !File.Exists(path), Ui.MoveTimeout), "the file is still in place after the move");

        Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(path), Ui.MoveTimeout), "the file was not put back");
        Assert.AreEqual(identity, MoveFixture.Identity(path), "the file was put back as a different file");
        Assert.AreEqual(hash, MoveFixture.Hash(path), "the put back file has other bytes");
    }
}
