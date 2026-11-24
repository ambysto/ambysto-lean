using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03 "Temporary files" (Windows, owner decision 2026-10-18), plan Rules "Temporary files" (HARD): the
/// direct children of <c>%LOCALAPPDATA%\Temp</c> and <c>%LOCALAPPDATA%\CrashDumps</c> whose newest last-write time over the
/// child's whole tree is older than the age (1 week = 7 days, default; 1 month and 1 quarter = 3 months by the calendar);
/// a child holding a link or a cloud file is display-only with a hint; <c>Windows\Temp</c> is one display-only row with
/// <see cref="Text.HintSystemWide"/>; nothing is selected. The folders are a <see cref="ProfileFixture"/>'s
/// (<c>LocalAppData</c> in a scratch folder), every time is set by the test, and "now" is a <see cref="FakeTimeProvider"/>'s.</summary>
[TestClass]
public sealed class TempFinderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRootsUnder(TempFolder.BaseFolder, "cloud-temp-*");
        }
    }

    private static string TempOf(ProfileFixture fx) => Path.Combine(fx.LocalAppData, "Temp");

    private static string DumpsOf(ProfileFixture fx) => Path.Combine(fx.LocalAppData, "CrashDumps");

    private static Task<IReadOnlyList<TempItem>> FindAsync(ProfileFixture fx, TempAge age, DateTimeOffset now, PathContext? context = null) =>
        TempFinder.FindAsync(context ?? fx.Context, age, new FakeTimeProvider(now), CancellationToken.None);

    private static bool IsWindowsTemp(TempItem row) => row.Node.Path.EndsWith(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rows for children of the user's temporary folders (the Windows\Temp row left out).</summary>
    private static List<TempItem> Children(IReadOnlyList<TempItem> rows) => rows.Where(r => !IsWindowsTemp(r)).ToList();

    private static string[] Names(IEnumerable<TempItem> rows) => rows.Select(r => r.Node.Name).ToArray();

    private static TempItem Row(IReadOnlyList<TempItem> rows, string path) =>
        rows.SingleOrDefault(r => string.Equals(r.Node.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertFailedException("no row for " + path + "; rows: " + string.Join(", ", rows.Select(r => r.Node.Path)));

    /// <summary>A file at <paramref name="path"/> last written at <paramref name="utc"/>.</summary>
    private static string FileAt(string path, DateTimeOffset utc, long length = 64 * 1024)
    {
        ProfileFixture.MakeFile(path, length);
        ItemTimes.Set(path, utc.UtcDateTime);
        return path;
    }

    /// <summary>A folder holding a file and a sub-folder with a file, every entry (folders included) at <paramref name="utc"/>.</summary>
    private static string FolderAt(string path, DateTimeOffset utc)
    {
        ProfileFixture.MakeFile(Path.Combine(path, "a.tmp"), 4096);
        ProfileFixture.MakeFile(Path.Combine(path, "sub", "b.tmp"), 4096);
        ItemTimes.SetTree(path, utc.UtcDateTime);
        return path;
    }

    /// <summary>The UTC ticks of a time the finder reports (a local time converted; an unspecified kind read as UTC).</summary>
    private static long UtcTicks(DateTime time) => time.Kind == DateTimeKind.Local ? time.ToUniversalTime().Ticks : time.Ticks;

    private static void ListedExactly(IReadOnlyList<TempItem> rows, params string[] names) =>
        Check.Set(Names(Children(rows)), names);

    // docs/spec/03: 1 week (the default) = 7 days. Just older is listed, just younger is not; files and folders alike.
    [TestMethod]
    public async Task AWeekIsSevenDays()
    {
        using var fx = new ProfileFixture("temp-week");
        var temp = TempOf(fx);
        var week = Now.AddDays(-7);
        FileAt(Path.Combine(temp, "old.tmp"), week.AddHours(-1));
        FileAt(Path.Combine(temp, "young.tmp"), week.AddHours(1));
        FolderAt(Path.Combine(temp, "oldfolder"), week.AddHours(-1));
        FolderAt(Path.Combine(temp, "youngfolder"), week.AddHours(1));

        ListedExactly(await FindAsync(fx, TempAge.Week, Now), "old.tmp", "oldfolder");
    }

    // docs/spec/03 and the intent: the default age is 1 week (the enum's default value).
    [TestMethod]
    public void TheDefaultAgeIsAWeek()
    {
        var values = Enum.GetValues<TempAge>();
        TempAge[] ages = [TempAge.Week, TempAge.Month, TempAge.Quarter];
        CollectionAssert.AreEqual(ages, values);
        Assert.AreEqual(0, (int)values[0], "the default value of the enum is 1 week");
    }

    // 1 month is a calendar month: on 31 May the boundary is 30 April (31 days), so a file 30 days and 23 hours old is not
    // listed (a 30-day month would list it), and one just over the calendar month is.
    [TestMethod]
    public async Task AMonthIsACalendarMonth()
    {
        using var fx = new ProfileFixture("temp-month");
        var now = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var month = now.AddMonths(-1);
        Assert.AreEqual(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero), month, "setup");
        var temp = TempOf(fx);
        FileAt(Path.Combine(temp, "old.tmp"), month.AddHours(-1));
        FileAt(Path.Combine(temp, "young.tmp"), month.AddHours(1));
        FolderAt(Path.Combine(temp, "oldfolder"), month.AddHours(-1));
        FolderAt(Path.Combine(temp, "youngfolder"), month.AddHours(1));

        ListedExactly(await FindAsync(fx, TempAge.Month, now), "old.tmp", "oldfolder");
        ListedExactly(await FindAsync(fx, TempAge.Week, now), "old.tmp", "oldfolder", "young.tmp", "youngfolder");
    }

    // 1 quarter is 3 calendar months: on 31 May the boundary is 28 February (92 days), so a file 91 days and 23 hours old is
    // not listed (a 90-day quarter would list it).
    [TestMethod]
    public async Task AQuarterIsThreeCalendarMonths()
    {
        using var fx = new ProfileFixture("temp-quarter");
        var now = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var quarter = now.AddMonths(-3);
        Assert.AreEqual(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero), quarter, "setup");
        var temp = TempOf(fx);
        FileAt(Path.Combine(temp, "old.tmp"), quarter.AddHours(-1));
        FileAt(Path.Combine(temp, "young.tmp"), quarter.AddHours(1));
        FolderAt(Path.Combine(temp, "oldfolder"), quarter.AddHours(-1));
        FolderAt(Path.Combine(temp, "youngfolder"), quarter.AddHours(1));

        ListedExactly(await FindAsync(fx, TempAge.Quarter, now), "old.tmp", "oldfolder");
        ListedExactly(await FindAsync(fx, TempAge.Month, now), "old.tmp", "oldfolder", "young.tmp", "youngfolder");
    }

    // "Now" is the time provider's, not the machine's clock: the same files, judged a year later, are all old.
    [TestMethod]
    public async Task NowIsTheTimeProvidersNow()
    {
        using var fx = new ProfileFixture("temp-now");
        FileAt(Path.Combine(TempOf(fx), "young.tmp"), Now.AddDays(-1));

        ListedExactly(await FindAsync(fx, TempAge.Week, Now));
        ListedExactly(await FindAsync(fx, TempAge.Week, Now.AddYears(1)), "young.tmp");
    }

    // A folder's age is that of the newest file anywhere in its tree: one fresh file deep down keeps the whole child off
    // the list, although the child and every other entry are old; a listed child reports its newest file's time, not its
    // own.
    [TestMethod]
    public async Task AFoldersAgeIsItsNewestFileAnywhereInItsTree()
    {
        using var fx = new ProfileFixture("temp-newest");
        var temp = TempOf(fx);
        var old = Now.AddDays(-60);

        var fresh = FolderAt(Path.Combine(temp, "fresh-inside"), old);
        var deep = FileAt(Path.Combine(fresh, "x", "y", "z", "new.log"), Now.AddDays(-1));
        ItemTimes.SetTree(fresh, old.UtcDateTime);
        ItemTimes.Set(deep, Now.AddDays(-1).UtcDateTime);

        var stale = FolderAt(Path.Combine(temp, "stale"), old);
        var newestOld = FileAt(Path.Combine(stale, "p", "q", "newest.log"), Now.AddDays(-10));
        ItemTimes.SetTree(stale, old.UtcDateTime);
        ItemTimes.Set(newestOld, Now.AddDays(-10).UtcDateTime);

        var rows = await FindAsync(fx, TempAge.Week, Now);

        ListedExactly(rows, "stale");
        var row = Row(rows, stale);
        Check.Near(Now.AddDays(-10).UtcDateTime.Ticks, UtcTicks(row.NewestWriteUtc), TimeSpan.FromMinutes(1).Ticks, "the newest file's time, not the folder's own: " + row.NewestWriteUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        Check.True(row.Node.IsFolder, "the row is the child folder");
        Assert.IsFalse(row.DisplayOnly);
        Assert.IsNull(row.Hint);
    }

    // A file child's time is its own; its size comes from the walk (allocated, "about <size>").
    [TestMethod]
    public async Task AFileChildReportsItsOwnTimeAndItsSize()
    {
        using var fx = new ProfileFixture("temp-file");
        var file = FileAt(Path.Combine(TempOf(fx), "setup.log"), Now.AddDays(-20), 256 * 1024);

        var row = Row(await FindAsync(fx, TempAge.Week, Now), file);

        Check.Near(Now.AddDays(-20).UtcDateTime.Ticks, UtcTicks(row.NewestWriteUtc), TimeSpan.FromMinutes(1).Ticks, "its own time");
        Assert.IsFalse(row.Node.IsFolder);
        Check.AtLeast(256 * 1024, row.Node.AllocatedBytes, "its allocated size, from the walk");
        Assert.IsFalse(row.DisplayOnly);
        Assert.IsNull(row.Hint);
    }

    // Both folders count: %LOCALAPPDATA%\Temp and %LOCALAPPDATA%\CrashDumps.
    [TestMethod]
    public async Task CrashDumpsIsListedToo()
    {
        using var fx = new ProfileFixture("temp-dumps");
        var old = FileAt(Path.Combine(DumpsOf(fx), "app.exe.1234.dmp"), Now.AddDays(-30));
        FileAt(Path.Combine(DumpsOf(fx), "fresh.dmp"), Now.AddDays(-2));
        var tempOld = FileAt(Path.Combine(TempOf(fx), "old.tmp"), Now.AddDays(-30));

        var rows = Children(await FindAsync(fx, TempAge.Week, Now));

        Check.Set(rows.Select(r => r.Node.Path), old, tempOld);
    }

    // Only the direct children of the two folders: never an item deeper inside, never another folder of AppData or of the
    // profile (a folder named Temp elsewhere included), never the two folders themselves.
    [TestMethod]
    public async Task OnlyDirectChildrenOfTheTwoFoldersAreRows()
    {
        using var fx = new ProfileFixture("temp-direct");
        var old = Now.AddDays(-90);
        var child = FolderAt(Path.Combine(TempOf(fx), "child"), old);
        FileAt(Path.Combine(fx.LocalAppData, "Contoso", "old.tmp"), old);
        FolderAt(Path.Combine(fx.LocalAppData, "Temp2"), old);
        FileAt(fx.InProfile("Documents", "Temp", "old.tmp"), old);
        FileAt(Path.Combine(fx.RoamingAppData, "Temp", "old.tmp"), old);
        FileAt(fx.InProfile("AppData", "LocalLow", "Temp", "old.tmp"), old);
        ItemTimes.Set(TempOf(fx), old.UtcDateTime);
        ItemTimes.Set(DumpsOf(fx), old.UtcDateTime);

        var rows = Children(await FindAsync(fx, TempAge.Week, Now));

        Check.Set(rows.Select(r => r.Node.Path), child);
        foreach (var row in rows)
        {
            var parent = Path.GetDirectoryName(row.Node.Path)!;
            Check.True(string.Equals(parent, TempOf(fx), StringComparison.OrdinalIgnoreCase) || string.Equals(parent, DumpsOf(fx), StringComparison.OrdinalIgnoreCase), "not a direct child: " + row.Node.Path);
        }
    }

    // A missing CrashDumps folder (common) is no error: the Temp rows still come.
    [TestMethod]
    public async Task AMissingCrashDumpsFolderIsNoError()
    {
        using var fx = new ProfileFixture("temp-nodumps");
        Directory.Delete(DumpsOf(fx));
        var old = FileAt(Path.Combine(TempOf(fx), "old.tmp"), Now.AddDays(-30));

        Check.Set(Children(await FindAsync(fx, TempAge.Week, Now)).Select(r => r.Node.Path), old);
    }

    // A child holding a junction is display-only, with a hint; the junction is never entered (its target's fresh file does
    // not make the child young, and the target is untouched).
    [TestMethod]
    public async Task AChildHoldingAJunctionIsDisplayOnlyWithAHint()
    {
        using var fx = new ProfileFixture("temp-junction");
        var old = Now.AddDays(-30);
        var target = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Target"));
        var targetFile = FileAt(Path.Combine(target, "fresh.bin"), Now.AddHours(-1));
        var holder = FolderAt(Path.Combine(TempOf(fx), "holder"), old);
        Native.CreateJunction(Path.Combine(holder, "link"), target);
        ItemTimes.SetTree(holder, old.UtcDateTime);
        var plain = FolderAt(Path.Combine(TempOf(fx), "plain"), old);

        var rows = await FindAsync(fx, TempAge.Week, Now);

        var linked = Row(rows, holder);
        Assert.IsTrue(linked.DisplayOnly, "a child holding a link may not be moved from this view");
        Check.False(string.IsNullOrWhiteSpace(linked.Hint), "a display-only row says why");
        var plainRow = Row(rows, plain);
        Assert.IsFalse(plainRow.DisplayOnly, "control: a plain old child");
        Assert.IsNull(plainRow.Hint);
        Check.True(File.Exists(targetFile), "the junction's target is untouched");
    }

    // A child that is (or holds) a user-protected folder is display-only, "Protected by you" (docs/spec/02 Windows note).
    [TestMethod]
    public async Task AChildInsideOrHoldingAUserProtectedFolderIsProtectedByYou()
    {
        using var fx = new ProfileFixture("temp-protected");
        var old = Now.AddDays(-30);
        var keep = FolderAt(Path.Combine(TempOf(fx), "keep"), old);
        var holder = FolderAt(Path.Combine(TempOf(fx), "holder"), old);
        var inner = ProfileFixture.MakeFolder(Path.Combine(holder, "inner"));
        ItemTimes.SetTree(holder, old.UtcDateTime);
        var plain = FolderAt(Path.Combine(TempOf(fx), "plain"), old);
        var context = fx.Context with { UserProtectedFolders = [keep, inner] };

        var rows = await FindAsync(fx, TempAge.Week, Now, context);

        foreach (var path in new[] { keep, holder })
        {
            var row = Row(rows, path);
            Assert.IsTrue(row.DisplayOnly, path);
            Assert.AreEqual(Text.HintProtectedByYou, row.Hint, path);
        }

        Assert.IsFalse(Row(rows, plain).DisplayOnly, "control: a plain old child");
    }

    // docs/spec/03: Windows\Temp is one display-only row, "System-wide: needs an administrator; Lean does not change it",
    // and none of its children is a row.
    [TestMethod]
    public async Task WindowsTempIsOneDisplayOnlySystemWideRow()
    {
        using var fx = new ProfileFixture("temp-windows");
        var windowsTemp = Path.Combine(fx.WindowsFolder, "Temp");
        FileAt(Path.Combine(windowsTemp, "old.tmp"), Now.AddDays(-90));
        FolderAt(Path.Combine(windowsTemp, "oldfolder"), Now.AddDays(-90));
        ItemTimes.Set(windowsTemp, Now.AddDays(-90).UtcDateTime);
        FileAt(Path.Combine(TempOf(fx), "old.tmp"), Now.AddDays(-30));

        var rows = await FindAsync(fx, TempAge.Week, Now);

        var system = rows.Where(IsWindowsTemp).ToList();
        Assert.AreEqual(1, system.Count, "one Windows\\Temp row: " + string.Join(", ", rows.Select(r => r.Node.Path)));
        Assert.IsTrue(system[0].DisplayOnly);
        Assert.AreEqual(Text.HintSystemWide, system[0].Hint);
        Check.False(rows.Any(r => r.Node.Path.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase)), "a child of Windows\\Temp is a row");
        Check.Set(Names(Children(rows)), "old.tmp");
    }

    // Nothing is selected: a row has no selection state at all (the API offers none: its only members are the node, the
    // newest time, display-only and the hint), so the view's "none ticked" is the only start.
    [TestMethod]
    public void ARowHasNoSelectionState()
    {
        var properties = typeof(TempItem).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();
        Check.Set(properties, nameof(TempItem.Node), nameof(TempItem.NewestWriteUtc), nameof(TempItem.DisplayOnly), nameof(TempItem.Hint));
        string[] selectionWords = ["select", "check", "tick", "mark", "chosen"];
        var members = typeof(TempItem).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name)
            .Concat(typeof(TempFinder).GetMembers(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name));
        foreach (var member in members)
        {
            Check.False(selectionWords.Any(w => member.Contains(w, StringComparison.OrdinalIgnoreCase)), "a selection member: " + member);
        }

        var finderMethods = typeof(TempFinder).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name).Distinct().ToArray();
        Check.Set(finderMethods, nameof(TempFinder.FindAsync));
    }

    // A cancelled search never hands back a row that may be moved: a partial walk could miss a child's newest file and make
    // it look old. It either throws OperationCanceledException or returns only display-only rows.
    [TestMethod]
    public async Task ACancelledSearchReturnsNoMovableRow()
    {
        using var fx = new ProfileFixture("temp-cancel");
        for (var i = 0; i < 20; i++)
        {
            FolderAt(Path.Combine(TempOf(fx), "child" + i), Now.AddDays(-30));
        }

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        IReadOnlyList<TempItem> rows;
        try
        {
            rows = await TempFinder.FindAsync(fx.Context, TempAge.Week, new FakeTimeProvider(Now), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Check.False(rows.Any(r => !r.DisplayOnly), "a cancelled search returned movable rows: " + string.Join(", ", Names(rows.Where(r => !r.DisplayOnly))));
    }

    // A child holding an online-only cloud file is display-only with a hint, and the search downloads nothing (the fake
    // provider's fetch log, shown able to fail by a positive control). Console session, as the other cloud tests.
    [TestMethod]
    [TestCategory("Console")]
    [DoNotParallelize]
    public async Task AChildHoldingACloudFileIsDisplayOnlyAndNothingIsFetched()
    {
        CloudRoot.RequireInstalled();
        using var fx = new ProfileFixture("cloud-temp");
        var holder = ProfileFixture.MakeFolder(Path.Combine(TempOf(fx), "holder"));
        using var cloud = CloudRoot.Register(Path.Combine(holder, "cloud"), fx.OnVolume("fetch.log"));
        const string control = "control.bin";
        cloud.Populate(("online.bin", 1024 * 1024), (control, 4096)); // placeholders dated 2024-01-01 by the provider
        cloud.Serve();
        cloud.RequireOnlineOnly("online.bin");
        ItemTimes.Set(cloud.Root, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        ItemTimes.Set(holder, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var plain = FolderAt(Path.Combine(TempOf(fx), "plain"), Now.AddDays(-30));

        // Positive control: this process's read of the control placeholder is in the fetch log.
        using (var stream = new FileStream(cloud.Combine(control), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.True(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        Check.True(cloud.FetchesOf(control).Count > 0, "the fetch log did not record the control read; all fetches: [" + string.Join(" | ", cloud.Fetches()) + "]");
        cloud.MarkFetchBaseline();

        var rows = await FindAsync(fx, TempAge.Week, Now);

        cloud.AssertNoFetch("the Temporary files search");
        Assert.AreEqual(0L, cloud.Status("online.bin").OnDisk, "the search downloaded online.bin");
        var row = Row(rows, holder);
        Assert.IsTrue(row.DisplayOnly, "a child holding an online-only file may not be moved from this view");
        Check.False(string.IsNullOrWhiteSpace(row.Hint), "a display-only row says why");
        Assert.IsFalse(Row(rows, plain).DisplayOnly, "control: a plain old child");
    }
}
