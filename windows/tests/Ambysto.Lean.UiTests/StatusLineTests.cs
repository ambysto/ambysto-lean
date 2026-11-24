using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan "UI contract for group 4", the main window's status line (intent outcomes 2 and 3; docs/spec/01 and 07
/// Windows notes of 2026-10-18): after a scan it says <c>Text.ScanSummaryLine(files, folders, unreadable, elapsed)</c>, and
/// during a move <c>Text.MovingProgress(index, count, name)</c>. The counts follow Core's reading (the scanned root and an
/// unlistable folder are folders; the unlistable one is the unreadable count); the time is matched loosely: any whole
/// number of seconds up to the test's own clock. The move is of the test's own duplicate copies under
/// <c>%USERPROFILE%\LeanUiMove-*</c>, put back afterwards.</summary>
[TestClass]
[DoNotParallelize]
public sealed class StatusLineTests
{
    private const long KB = ByteUnits.KB;
    private const long MB = ByteUnits.MB;

    /// <summary>a.bin, sub\b.bin, sub\deep\c.bin, sub\deep\d.bin and an empty folder: 4 files, 4 folders with the root.</summary>
    private static Fixture ReadableTree(string name)
    {
        var fixture = new Fixture(name);
        fixture.File("a.bin", 64 * KB);
        fixture.File(@"sub\b.bin", 64 * KB);
        fixture.File(@"sub\deep\c.bin", 64 * KB);
        fixture.File(@"sub\deep\d.bin", 10);
        Directory.CreateDirectory(fixture.Sub("empty"));
        return fixture;
    }

    /// <summary>The whole-second scan times whose summary line could be on screen: 1 s up to the test's clock, plus one.</summary>
    private static IEnumerable<TimeSpan> PossibleTimes(Stopwatch clock) =>
        Enumerable.Range(1, (int)Math.Ceiling(clock.Elapsed.TotalSeconds) + 1).Select(s => TimeSpan.FromSeconds(s));

    private static bool IsSummary(string status, long files, long folders, long unreadable, Stopwatch clock) =>
        PossibleTimes(clock).Any(t => status == Text.ScanSummaryLine(files, folders, unreadable, t));

    private static void AssertSummary(AppSession app, long files, long folders, long unreadable, Stopwatch clock)
    {
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => IsSummary(status.Current.Name, files, folders, unreadable, clock), Ui.ElementTimeout),
            $"the status after the scan is \"{status.Current.Name}\", expected \"{Text.ScanSummaryLine(files, folders, unreadable, TimeSpan.FromSeconds(1))}\" (with any time up to {clock.Elapsed.TotalSeconds:F0} s)");
    }

    private static string CommonPrefix(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }

        return a[..length];
    }

    // Contract: after a scan of a known tree (4 files; 4 folders counting the scanned root; nothing unreadable) the status
    // line is Text.ScanSummaryLine(4, 4, 0, elapsed): the counts exact, no "could not be read" part, the time any whole
    // number of seconds the scan could have taken.
    [TestMethod]
    [TestCategory("Console")]
    public void AfterAScanTheStatusLineSaysWhatItRead()
    {
        using var fixture = ReadableTree("ui-status");
        var clock = Stopwatch.StartNew();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        AssertSummary(app, files: 4, folders: 4, unreadable: 0, clock);
    }

    // Contract and docs/spec/01 Windows note: a folder the user cannot list (a deny ACE on a scratch folder, removed after
    // the app has closed) is counted as one that could not be read, and the line ends with "; 1 could not be read": the
    // file inside it is not counted, the folder is (Core's reading): Text.ScanSummaryLine(4, 5, 1, elapsed).
    [TestMethod]
    [TestCategory("Console")]
    public void AnUnreadableFolderIsCountedInTheStatusLine()
    {
        using var fixture = ReadableTree("ui-status-locked");
        var locked = fixture.Sub("locked");
        fixture.File(@"locked\inside.bin", 64 * KB);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var clock = Stopwatch.StartNew();
            using var app = AppSession.Start("--scan", fixture.Path);
            Ui.WaitForScan(app);
            AssertSummary(app, files: 4, folders: 5, unreadable: 1, clock);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    // Contract: during a move the status line shows Text.MovingProgress(index, count, name). Made visible by moving nine
    // items in one session (nine of ten byte-identical 2 MB copies, ticked in Duplicates; the newest, copy-10, is kept)
    // while a StatusRecorder takes every text the status shows, from its Name-changed events and a 20 ms poll. At least one
    // The lines of Text.MovingProgress's shape are exactly nine: Text.MovingProgress(i, 9, name) for i = 1..9 in order, the
    // name (file name or full path) the i-th item of the session (the confirmation's order). The only other "Moving …"
    // line allowed is group 11's Text.MovingItems, before the first of them. The nine copies are in the Recycle Bin after the move, and PutBackSession then
    // brings them back.
    [TestMethod]
    [TestCategory("Console")]
    public void DuringAMoveTheStatusLineShowsMovingProgress()
    {
        const int copies = 10;
        const int moved = copies - 1;
        using var fixture = new MoveFixture("progress");
        var content = new byte[(int)(2 * MB)];
        new Random(4_2_7).NextBytes(content);
        var now = DateTime.UtcNow;
        var paths = new string[copies];
        for (var i = 0; i < copies; i++)
        {
            var name = $"copy-{i + 1:D2}.dat";
            paths[i] = fixture.FileWithBytes(Path.Combine($"c{i + 1:D2}", name), content);
            fixture.SetModified(Path.Combine($"c{i + 1:D2}", name), now.AddDays(-(copies - i)));
        }

        var identities = paths.ToDictionary(p => p, MoveFixture.Identity, StringComparer.OrdinalIgnoreCase);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        var view = Ui.OpenView(app, "Duplicates", "DuplicatesView");
        Ui.Click(AppSession.Find(view, "FindDuplicates", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(view, "DuplicateCopy").Count >= copies, Ui.DuplicatesTimeout),
            "precondition: the duplicates search did not list the ten copies: " + Ui.Dump(view));
        foreach (var path in paths[..moved])
        {
            // ASP-10: the copy's Move box is in the row that holds its name and full folder; copy-10, the newest, is kept.
            var copy = Table.ControlFor(view, "DuplicateCopy", path);
            Assert.IsNotNull(copy, "precondition: no DuplicateCopy for " + path + ": " + Table.Dump(view, "DuplicateCopy"));
            Table.Tick(copy, () => Table.ControlFor(view, "DuplicateCopy", path), "precondition: " + path);
        }

        Ui.Click(AppSession.Find(view, "MoveToRecycleBin", Ui.ElementTimeout));
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => AppSession.FindAll(dialog, "ConfirmItem").Count == moved, Ui.ElementTimeout),
            $"precondition: the confirmation does not list {moved} items: {Ui.Dump(dialog)}");

        // The session's order: the order of the confirmation's items ("<path>, about <size>").
        var confirmItems = AppSession.FindAll(dialog, "ConfirmItem").Select(c => c.Current.Name).ToList();
        var order = confirmItems.Select(n => paths[..moved].FirstOrDefault(p => n.StartsWith(p + ", ", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.IsTrue(order.All(p => p is not null) && order.Distinct(StringComparer.OrdinalIgnoreCase).Count() == moved,
            "precondition: the confirmation's items are not the nine ticked copies: [" + string.Join(" | ", confirmItems) + "]");

        List<string> seen;
        using (var recorder = new StatusRecorder(status))
        {
            var report = Ui.ConfirmAndWaitForReport(app, Text.SpaceReturnsWhenEmptied);
            seen = recorder.Seen;
            Assert.AreEqual(0, AppSession.FindAll(report, "ReportItem").Count, "an item was skipped or lost: " + Ui.Dump(report));
        }

        var dump = "status lines seen: [" + string.Join(" | ", seen) + "]";
        Console.WriteLine(dump);
        // Lines of Text.MovingProgress's shape: the text with its index, count and name made into groups.
        var shape = new Regex("^" + Regex.Escape(Text.MovingProgress(111_111, 222_222, "\u0001"))
            .Replace("111111", @"(?<index>\d+)", StringComparison.Ordinal)
            .Replace("222222", @"(?<count>\d+)", StringComparison.Ordinal)
            .Replace("\u0001", "(?<name>.+)", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var progress = seen.Where(l => shape.IsMatch(l)).ToList();
        Assert.AreEqual(moved, progress.Count, $"progress lines seen: {progress.Count}, expected one per item ({moved}); {dump}");
        for (var i = 0; i < moved; i++)
        {
            var path = order[i]!;
            var expected = new[] { Text.MovingProgress(i + 1, moved, Path.GetFileName(path)), Text.MovingProgress(i + 1, moved, path) };
            Assert.IsTrue(expected.Contains(progress[i], StringComparer.Ordinal),
                $"progress line {i + 1} is \"{progress[i]}\", expected \"{expected[0]}\" (or with the full path); {dump}");
        }

        // Any other "Moving …" line is group 11's Text.MovingItems, shown before the first progress line.
        var movingPrefix = CommonPrefix(Text.MovingProgress(1, moved, "x"), Text.MovingProgress(2, moved, "x"));
        var firstProgress = seen.IndexOf(progress[0]);
        for (var at = 0; at < seen.Count; at++)
        {
            var line = seen[at];
            if (!line.StartsWith(movingPrefix, StringComparison.Ordinal) || shape.IsMatch(line))
            {
                continue;
            }

            Assert.AreEqual(Text.MovingItems, line, $"a \"{movingPrefix}…\" status line is neither Text.MovingProgress nor Text.MovingItems; {dump}");
            Assert.IsTrue(at < firstProgress, $"\"{Text.MovingItems}\" was shown after the first progress line; {dump}");
        }

        foreach (var path in paths[..moved])
        {
            Assert.IsTrue(AppSession.WaitUntil(() => RecycleBin.Holding(path) is not null, TimeSpan.FromSeconds(10)), path + " is not in the Recycle Bin");
        }

        Ui.Click(Ui.FindAnywhere(app, "PutBackSession", Ui.ElementTimeout));
        foreach (var path in paths[..moved])
        {
            Assert.IsTrue(AppSession.WaitUntil(() => File.Exists(path), Ui.MoveTimeout), path + " was not put back");
            Assert.AreEqual(identities[path], MoveFixture.Identity(path), path + " was put back as a different file");
        }
    }
}
