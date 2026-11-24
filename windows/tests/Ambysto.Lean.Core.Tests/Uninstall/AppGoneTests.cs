using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>docs/spec/04 Windows note and plan "App is gone": an app counts as gone when its entry has been absent from
/// both hives and both views, and no Store package and no other entry names the same install location or display name,
/// for 10 s (checked every 2 s), and no process runs from its install location. Any sighting restarts the settle time.
/// The decoys of intent criterion 6 (inventory part): a re-registration under a new key, another entry with the same
/// name, a Store package with the same location or name, a process from the install location.</summary>
[TestClass]
public sealed class AppGoneTests
{
    private const string Location = @"C:\Users\me\AppData\Local\Programs\Contoso Notes";
    private static readonly UninstallEntry Notes = FakeUninstallSource.Entry("{NOTES}", "Contoso Notes", Location);

    private static (AppGoneWatcher Watcher, FakeUninstallSource Source, FakeTimeProvider Time) Watch(bool installed = true)
    {
        var source = new FakeUninstallSource();
        if (installed)
        {
            source.Entries.Add(Notes);
        }

        source.Entries.Add(FakeUninstallSource.Entry("{OTHER}", "Other App", @"C:\Users\me\AppData\Local\Programs\Other"));
        var time = new FakeTimeProvider();
        return (new AppGoneWatcher(Notes, source, time), source, time);
    }

    /// <summary>Checks every 2 s for <paramref name="seconds"/> seconds; returns the last answer.</summary>
    private static bool CheckFor(AppGoneWatcher watcher, FakeTimeProvider time, int seconds)
    {
        var gone = watcher.Check();
        for (var elapsed = 0; elapsed < seconds; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            gone = watcher.Check();
        }

        return gone;
    }

    // Plan "App is gone": a settle time of 10 s, checked every 2 s (the plan's value; a test pins it).
    [TestMethod]
    public void SettleTimeIsTenSecondsCheckedEveryTwo()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), AppGoneWatcher.SettleTime);
        Assert.AreEqual(TimeSpan.FromSeconds(2), AppGoneWatcher.CheckInterval);
    }

    // docs/spec/04 Windows note: an app whose entry is present is not gone.
    [TestMethod]
    public void InstalledAppIsNotGone()
    {
        var (watcher, _, time) = Watch();
        Assert.IsFalse(CheckFor(watcher, time, 30));
        Assert.IsFalse(watcher.IsGone);
    }

    // docs/spec/04 Windows note: gone only after 10 s of absence.
    [TestMethod]
    public void AbsentEntryIsGoneOnlyAfterTenSeconds()
    {
        var (watcher, source, time) = Watch(installed: false);
        Assert.IsFalse(watcher.Check(), "first absence seen: the settle time starts now");
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.IsFalse(watcher.Check(), "8 s absent is not 10 s");
        Assert.IsFalse(watcher.IsGone);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.IsTrue(watcher.Check(), "10 s absent");
        Assert.IsTrue(watcher.IsGone);
        Assert.IsTrue(source.Reads >= 3, "the source was read at each check");
    }

    // docs/spec/04 Windows note: the entry is removed while watching; the settle time counts from the first absence.
    [TestMethod]
    public void SettleTimeCountsFromTheFirstAbsence()
    {
        var (watcher, source, time) = Watch();
        Assert.IsFalse(CheckFor(watcher, time, 4));
        source.Entries.Remove(Notes);
        Assert.IsFalse(watcher.Check(), "removed now (t0)");
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.IsFalse(watcher.Check(), "t0 + 9 s");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(watcher.Check(), "t0 + 10 s");
    }

    // Plan "App is gone" / doc comment: any sighting restarts the settle time.
    [TestMethod]
    public void ASightingRestartsTheSettleTime()
    {
        var (watcher, source, time) = Watch(installed: false);
        Assert.IsFalse(watcher.Check());
        time.Advance(TimeSpan.FromSeconds(6));
        source.Entries.Add(Notes);
        Assert.IsFalse(watcher.Check(), "seen again at +6 s");
        source.Entries.Remove(Notes);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.IsFalse(watcher.Check(), "absent again at +8 s: the clock restarts");
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.IsFalse(watcher.Check(), "+14 s: only 6 s since the sighting");
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.IsTrue(watcher.Check(), "+18 s: 10 s since the sighting");
    }

    // Intent Outcome 4: an update that re-registers under a new key keeps the app installed (same InstallLocation).
    [TestMethod]
    public void ReRegistrationUnderANewKeyWithTheSameLocationKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{NEW-KEY}", "Contoso Notes 2", Location));
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: no other entry names the same display name (a decoy of criterion 6: two entries with one name).
    [TestMethod]
    public void AnotherEntryWithTheSameDisplayNameKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{TWIN}", "Contoso Notes", @"C:\Program Files\Contoso Notes", UninstallScope.PerMachine, RegistryViewKind.Bits32));
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: both hives and both views: the same entry still present in the other hive and view.
    [TestMethod]
    public void TheSameEntryInTheOtherHiveOrViewKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(Notes with { Scope = UninstallScope.PerMachine, View = RegistryViewKind.Bits32 });
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: no Store package names the same install location or display name.
    [TestMethod]
    public void AStorePackageWithTheSameLocationOrNameKeepsItInstalled()
    {
        var (byLocation, sourceA, timeA) = Watch(installed: false);
        sourceA.StorePackages.Add(new StorePackage("Contoso.Notes_1.0_x64__abc", "Notes (Store)", "Contoso", Location));
        Assert.IsFalse(CheckFor(byLocation, timeA, 30), "same install location");

        var (byName, sourceB, timeB) = Watch(installed: false);
        sourceB.StorePackages.Add(new StorePackage("Contoso.Notes_1.0_x64__abc", "Contoso Notes", "Contoso", @"C:\Program Files\WindowsApps\Contoso.Notes_1.0_x64__abc"));
        Assert.IsFalse(CheckFor(byName, timeB, 30), "same display name");
    }

    // docs/spec/04 Windows note: no process runs from its install location.
    [TestMethod]
    public void AProcessRunningFromTheInstallLocationKeepsItNotGone()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.ProcessImagePaths.Add(Location + @"\Notes.exe");
        Assert.IsFalse(CheckFor(watcher, time, 30), "the uninstaller or the app still runs from the folder");
        source.ProcessImagePaths.Clear();
        Assert.IsTrue(CheckFor(watcher, time, 12), "once no process runs there, the settle time completes");
    }

    // docs/spec/04 Windows note: a process in a sibling folder whose name merely starts the same is not "from the install location".
    [TestMethod]
    public void AProcessFromASiblingFolderWithAPrefixNameDoesNotCount()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.ProcessImagePaths.Add(Location + @" Helper\helper.exe");
        source.ProcessImagePaths.Add(@"C:\Users\me\AppData\Local\Programs\Contoso\notes.exe");
        Assert.IsTrue(CheckFor(watcher, time, 12));
    }

    // docs/spec/04 Match rules (exact, never substring), applied to the inventory decoys: a name prefix is another app.
    [TestMethod]
    public void AnEntryWhoseNameMerelyStartsTheSameDoesNotCount()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{PREFIX}", "Contoso Notes Viewer", @"C:\Users\me\AppData\Local\Programs\Contoso Notes Viewer"));
        source.Entries.Add(FakeUninstallSource.Entry("{PARENT}", "Contoso", @"C:\Users\me\AppData\Local\Programs"));
        Assert.IsTrue(CheckFor(watcher, time, 12));
    }

    // docs/spec/04 Windows note: install locations compare as Windows paths (case and a trailing separator do not matter).
    [TestMethod]
    public void InstallLocationComparisonIgnoresCaseAndTrailingSeparator()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{CASE}", "Something Else", Location.ToUpperInvariant() + @"\"));
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: an entry without an install location is matched by display name only.
    [TestMethod]
    public void EntryWithoutInstallLocationIsGoneByNameAlone()
    {
        var entry = FakeUninstallSource.Entry("{NOLOC}", "Locationless", null);
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{OTHER}", "Other", null));
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(entry, source, time);
        Assert.IsTrue(CheckFor(watcher, time, 12));

        source.Entries.Add(FakeUninstallSource.Entry("{SAME-NAME}", "Locationless", null));
        var watcher2 = new AppGoneWatcher(entry, source, time);
        Assert.IsFalse(CheckFor(watcher2, time, 30));
    }

    // docs/spec/04 Windows note: an empty install location matches nothing: an entry with "" is gone by name alone,
    // and another entry with "" does not keep it installed.
    [TestMethod]
    public void EmptyInstallLocationMatchesNothing()
    {
        var entry = FakeUninstallSource.Entry("{EMPTY}", "Empty Location App", "");
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{OTHER-EMPTY}", "Another App", ""));
        source.Entries.Add(FakeUninstallSource.Entry("{HIDDEN-EMPTY}", null, "", systemComponent: true));
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(entry, source, time);
        Assert.IsTrue(CheckFor(watcher, time, 12), "entries that also have an empty install location do not keep it installed");
    }

    // docs/spec/04 Windows note: "no other entry (hidden ones included: system components ...)": a system component
    // with the same install location keeps the app installed.
    [TestMethod]
    public void AHiddenSystemComponentWithTheSameLocationKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{SYS}", "Contoso Notes Runtime", Location, systemComponent: true));
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: "... and entries without a display name": a nameless entry with the same install
    // location keeps the app installed.
    [TestMethod]
    public void ANamelessEntryWithTheSameLocationKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.Entries.Add(FakeUninstallSource.Entry("{NAMELESS}", null, Location));
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: "a process anywhere below the location counts": a process in a subfolder keeps it installed.
    [TestMethod]
    public void AProcessInASubfolderOfTheInstallLocationKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.ProcessImagePaths.Add(Location + @"\bin\x.exe");
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: "locations compare as paths (case-insensitive ...)": a process whose path differs only
    // in case keeps it installed.
    [TestMethod]
    public void AProcessWithACaseDifferentPathKeepsItInstalled()
    {
        var (watcher, source, time) = Watch(installed: false);
        source.ProcessImagePaths.Add(Location.ToUpperInvariant() + @"\NOTES.EXE");
        Assert.IsFalse(CheckFor(watcher, time, 30));
    }

    // docs/spec/04 Windows note: a per-machine ("needs admin") entry is watched the same way.
    [TestMethod]
    public void PerMachineEntryIsWatchedTheSameWay()
    {
        var entry = FakeUninstallSource.Entry("{MACHINE}", "Machine App", @"C:\Program Files\MachineApp", UninstallScope.PerMachine);
        var source = new FakeUninstallSource();
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(entry, source, time);
        Assert.IsFalse(watcher.Check());
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.IsTrue(watcher.Check());
    }

    // docs/spec/04 Windows note: checked every 2 s: WaitAsync reads the source about once per interval.
    // TODO(later groups): this test relies on the implementation waiting with Task.Delay(…, time) (or a timer made by the
    // TimeProvider) so that FakeTimeProvider.Advance fires the continuation inline; a wait that uses the real clock
    // would never complete under the fake clock, and the test would time out rather than fail with a reason.
    [TestMethod]
    [Timeout(30_000)]
    public async Task WaitAsyncChecksEveryTwoSecondsAndCompletesWhenGone()
    {
        var (watcher, source, time) = Watch(installed: false);
        using var cts = new CancellationTokenSource();
        var wait = watcher.WaitAsync(cts.Token);
        for (var i = 0; i < 6 && !wait.IsCompleted; i++)
        {
            var reads = source.Reads;
            time.Advance(AppGoneWatcher.CheckInterval);
            var clock = Stopwatch.StartNew();
            while (source.Reads == reads && !wait.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(10, CancellationToken.None);
            }
        }

        Assert.IsTrue(await wait, "gone after 10 s of absence");
        Check.True(source.Reads >= 5 && source.Reads <= 8, $"reads: {source.Reads} (one per 2 s over 10 s, plus at most the first and last)");
    }

    // docs/spec/04 Windows note: cancelling the wait gives false, never "gone".
    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelledWaitIsNotGone()
    {
        var (watcher, _, _) = Watch();
        using var cts = new CancellationTokenSource();
        var wait = watcher.WaitAsync(cts.Token);
        await cts.CancelAsync();
        Assert.IsFalse(await wait);
        Assert.IsFalse(watcher.IsGone);
    }

    /// <summary>A source in which the watched app is absent and nothing names it, with one part failing per row.</summary>
    private static FakeUninstallSource FailingSource(string part)
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{OTHER}", "Other App", @"C:\Users\me\AppData\Local\Programs\Other"));
        var failure = new System.IO.IOException("the " + part + " could not be read (fake)");
        switch (part)
        {
            case "entries":
                source.EntriesFailure = failure;
                break;
            case "store":
                source.StoreFailure = failure;
                break;
            default:
                source.ProcessFailure = failure;
                break;
        }

        return source;
    }

    // Gap brief H19. docs/spec/04 Windows note ("An app counts as gone when … for 10 s"); intent Outcome 4; the watcher's
    // rule: "app is gone" is never concluded from a failed read. A source whose entry list, Store list or process list
    // cannot be read never makes the app gone, checked every 2 s for 30 s, and a wait cancelled at 30 s gives false.
    // Control: the same source, with nothing failing, makes the app gone after 10 s.
    [TestMethod]
    [Timeout(30_000)]
    [DataRow("entries")]
    [DataRow("store")]
    [DataRow("processes")]
    public async Task ASourceThatFailsNeverMakesTheAppGone(string part)
    {
        var source = FailingSource(part);
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(Notes, source, time);
        Assert.IsFalse(watcher.Check(), "t0");
        for (var elapsed = 2; elapsed <= 30; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            Assert.IsFalse(watcher.Check(), $"the {part} cannot be read: still installed at +{elapsed} s");
        }

        Assert.IsFalse(watcher.IsGone);

        // The same through WaitAsync on a second watcher: 30 s of checks, then cancelled.
        var waiter = new AppGoneWatcher(Notes, source, time);
        using var cts = new CancellationTokenSource();
        var wait = waiter.WaitAsync(cts.Token);
        for (var i = 0; i < 15 && !wait.IsCompleted; i++)
        {
            var reads = source.Reads;
            time.Advance(AppGoneWatcher.CheckInterval);
            var clock = Stopwatch.StartNew();
            while (source.Reads == reads && !wait.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(10, CancellationToken.None);
            }
        }

        Check.False(wait.IsCompleted && await wait, "the wait concluded \"gone\" from a source that cannot be read");
        await cts.CancelAsync();
        Assert.IsFalse(await wait, "a wait cancelled at 30 s is not gone");
        Assert.IsFalse(waiter.IsGone);

        // Control: nothing fails any more; the app is absent, so it is gone after 10 s.
        source.EntriesFailure = null;
        source.StoreFailure = null;
        source.ProcessFailure = null;
        var control = new AppGoneWatcher(Notes, source, time);
        Assert.IsTrue(CheckFor(control, time, 10), "control: the readable source makes the app gone after 10 s");
    }

    // Gap brief H20. docs/spec/04 "locations compare as paths (case-insensitive, a process anywhere below the location
    // counts)": the \\?\ spelling, forward slashes with a trailing separator, a \\?\ process image, and the watched
    // entry itself spelled \\?\…\ with a plain-spelled process below it all name the same folder.
    [TestMethod]
    [DataRow("extended entry")]
    [DataRow("slashes entry")]
    [DataRow("extended process")]
    [DataRow("extended watched")]
    public void InstallLocationMatchesInEverySpelling(string row)
    {
        const string foo = @"C:\Users\me\AppData\Local\Programs\Foo";
        var watched = FakeUninstallSource.Entry("{FOO}", "Foo", row == "extended watched" ? @"\\?\" + foo + @"\" : foo);
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{UNRELATED}", "Unrelated", @"C:\Users\me\AppData\Local\Programs\Unrelated"));
        switch (row)
        {
            case "extended entry":
                source.Entries.Add(FakeUninstallSource.Entry("{FOO-2}", "Foo Two", @"\\?\" + foo));
                break;
            case "slashes entry":
                source.Entries.Add(FakeUninstallSource.Entry("{FOO-2}", "Foo Two", foo.Replace('\\', '/') + "/"));
                break;
            case "extended process":
                source.ProcessImagePaths.Add(@"\\?\" + foo + @"\bin\foo.exe");
                break;
            default:
                source.ProcessImagePaths.Add(foo + @"\foo.exe");
                break;
        }

        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(watched, source, time);
        Assert.IsFalse(CheckFor(watcher, time, 30), row + ": the same install location keeps the app installed");

        // Control: without the sighting, the app is gone after 10 s.
        source.Entries.RemoveAll(e => e.KeyName == "{FOO-2}");
        source.ProcessImagePaths.Clear();
        Assert.IsTrue(CheckFor(new AppGoneWatcher(watched, source, time), time, 10), row + ": control");
    }

    // Gap brief H21. docs/spec/04 "a process anywhere below the location counts" and "An empty install location matches
    // nothing": a process in another folder is no sighting even when a separator stands where the location ends; a
    // location that is only separators is empty, so another separator-only location or a network process matches it not.
    [TestMethod]
    [DataRow("separator at the end of the location")]
    [DataRow(@"\")]
    [DataRow("//")]
    public void AnUnrelatedProcessOrASeparatorOnlyLocationIsNoSighting(string row)
    {
        var source = new FakeUninstallSource();
        UninstallEntry watched;
        if (row.StartsWith("separator", StringComparison.Ordinal))
        {
            const string location = @"C:\Apps\Fo";
            Assert.AreEqual('\\', @"C:\Windows\System32\cmd.exe"[location.Length], "the fixture: a separator at the position where the location ends");
            watched = FakeUninstallSource.Entry("{FO}", "Fo", location);
            source.ProcessImagePaths.Add(@"C:\Windows\System32\cmd.exe");
        }
        else
        {
            watched = FakeUninstallSource.Entry("{SEP}", "Separator App", row);
            source.Entries.Add(FakeUninstallSource.Entry("{SLASH}", "Slash App", "/"));
            source.ProcessImagePaths.Add(@"\\server\share\x.exe");
            source.ProcessImagePaths.Add(@"C:\x.exe");
        }

        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(watched, source, time);
        Assert.IsTrue(CheckFor(watcher, time, 10), row + ": nothing names the app, so it is gone after 10 s");
    }

    // Hard review of AppGoneWatcher, item 1. Plan owner answer 3 (2026-10-18, #2173): "A process whose image path is
    // the install location itself counts as running from it, so the app is not gone." Some entries name the program
    // file as their InstallLocation; while that program runs, the app is never gone. Pinned beside it: an image below
    // the location is a sighting, an image in a sibling folder whose name starts the same is not.
    [TestMethod]
    [DataRow("plain")]
    [DataRow("case")]
    [DataRow("extended")]
    public void AProcessWhoseImageIsTheInstallLocationItselfKeepsItNotGone(string row)
    {
        const string program = @"C:\Users\me\AppData\Local\Programs\Foo\Foo.exe";
        var watched = FakeUninstallSource.Entry("{FOO-EXE}", "Foo", program);
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{UNRELATED}", "Unrelated", @"C:\Users\me\AppData\Local\Programs\Unrelated"));
        source.ProcessImagePaths.Add(row switch
        {
            "case" => program.ToUpperInvariant(),
            "extended" => @"\\?\" + program,
            _ => program,
        });
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(watched, source, time);
        Assert.IsFalse(CheckFor(watcher, time, 30), row + ": Foo.exe runs from exactly its install location, so the app is not gone");
        Assert.IsFalse(watcher.IsGone);

        // Control: once Foo.exe no longer runs, the settle time completes.
        source.ProcessImagePaths.Clear();
        Assert.IsTrue(CheckFor(watcher, time, 12), row + ": control, no process runs from the location");
    }

    // Hard review item 1, pinned: an image below the location is a sighting.
    [TestMethod]
    public void AProcessImageBelowTheInstallLocationIsASighting()
    {
        const string foo = @"C:\Users\me\AppData\Local\Programs\Foo";
        var watched = FakeUninstallSource.Entry("{FOO}", "Foo", foo);
        var source = new FakeUninstallSource();
        source.ProcessImagePaths.Add(foo + @"\Foo.exe");
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(watched, source, time);
        Assert.IsFalse(CheckFor(watcher, time, 30));
        source.ProcessImagePaths.Clear();
        Assert.IsTrue(CheckFor(watcher, time, 12), "control");
    }

    // Hard review item 1, pinned: an image in a sibling folder with the same prefix (...\Foo2\x.exe for ...\Foo) is not
    // a sighting, nor is the sibling folder's own path.
    [TestMethod]
    public void AProcessImageInASiblingFolderWithTheSamePrefixIsNoSighting()
    {
        const string foo = @"C:\Users\me\AppData\Local\Programs\Foo";
        var watched = FakeUninstallSource.Entry("{FOO}", "Foo", foo);
        var source = new FakeUninstallSource();
        source.ProcessImagePaths.Add(foo + @"2\x.exe");
        source.ProcessImagePaths.Add(foo + "2");
        source.ProcessImagePaths.Add(foo + ".exe");
        var time = new FakeTimeProvider();
        Assert.IsTrue(CheckFor(new AppGoneWatcher(watched, source, time), time, 10));
    }

    // Hard review of AppGoneWatcher, item 2. Some NSIS and Inno Setup installers write InstallLocation in quotes, as
    // "C:\Program Files\X", sometimes with spaces around them. docs/spec/04: "locations compare as paths": the quoted
    // spelling names the same folder, so a process below it is a sighting, and so is another entry naming the folder.
    [TestMethod]
    [DataRow("quoted", "\"C:\\Program Files\\X\"")]
    [DataRow("quoted with spaces", "  \"C:\\Program Files\\X\"  ")]
    [DataRow("quoted with a trailing separator", "\"C:\\Program Files\\X\\\"")]
    public void AQuotedInstallLocationMatchesAProcessBelowIt(string row, string location)
    {
        var watched = FakeUninstallSource.Entry("{X}", "X App", location, UninstallScope.PerMachine);
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{UNRELATED}", "Unrelated", @"C:\Program Files\Unrelated", UninstallScope.PerMachine));
        source.ProcessImagePaths.Add(@"C:\Program Files\X\x.exe");
        var time = new FakeTimeProvider();
        var watcher = new AppGoneWatcher(watched, source, time);
        Assert.IsFalse(CheckFor(watcher, time, 30), row + ": x.exe runs from the quoted location, so the app is not gone");

        // Control: a sibling folder with the same prefix is still no sighting, and without the process the app is gone.
        source.ProcessImagePaths.Clear();
        source.ProcessImagePaths.Add(@"C:\Program Files\X2\x.exe");
        Assert.IsTrue(CheckFor(watcher, time, 12), row + ": control");
    }

    // Hard review item 2, the other side: the watched entry is plain and another entry names the same folder in quotes
    // (a re-registration by an installer that quotes); and the reverse.
    [TestMethod]
    [DataRow("other quoted", @"C:\Program Files\X", "\"C:\\Program Files\\X\"")]
    [DataRow("watched quoted", " \"C:\\Program Files\\X\" ", @"C:\Program Files\X\")]
    public void AQuotedInstallLocationNamesTheSameFolderAsThePlainOne(string row, string watchedLocation, string otherLocation)
    {
        var watched = FakeUninstallSource.Entry("{X}", "X App", watchedLocation, UninstallScope.PerMachine);
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{X-NEW}", "X App 2", otherLocation, UninstallScope.PerMachine));
        var time = new FakeTimeProvider();
        Assert.IsFalse(CheckFor(new AppGoneWatcher(watched, source, time), time, 30), row + ": the same folder keeps the app installed");
    }

    // Hard review of AppGoneWatcher, item 3: the 10 s settle time runs on monotonic time, not the wall clock. The wall
    // clock jumps forward (a time sync, the user setting the time, a 7 h correction after boot) while only 2 s
    // pass: the app has been absent 2 s, not 10, so it is not gone.
    [TestMethod]
    [DataRow(10)]
    [DataRow(3600)]
    public void AWallClockJumpForwardDoesNotShortenTheSettleTime(int jumpSeconds)
    {
        var (watcher, _, time) = Watch(installed: false);
        Assert.IsFalse(watcher.Check(), "check 1: first absence");
        time.SetWallClock(time.GetUtcNow().AddSeconds(jumpSeconds));
        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.IsFalse(watcher.Check(), $"check 2: the wall clock moved {jumpSeconds + 2} s, monotonic time only 2 s");
        Assert.IsFalse(watcher.IsGone);
        for (var elapsed = 4; elapsed < 10; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            Assert.IsFalse(watcher.Check(), $"+{elapsed} s monotonic");
        }

        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.IsTrue(watcher.Check(), "+10 s monotonic: gone");
    }

    // Hard review item 3: 10 s of monotonic time with the app absent at every check makes it gone (control of the
    // monotonic settle time, checked every 2 s, not gone at 8 s).
    [TestMethod]
    public void TenSecondsOfMonotonicTimeWithTheAppAbsentAtEveryCheckMakeItGone()
    {
        var (watcher, source, time) = Watch(installed: false);
        var start = time.GetTimestamp();
        Assert.IsFalse(watcher.Check(), "t0");
        for (var elapsed = 2; elapsed <= 8; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            Assert.IsFalse(watcher.Check(), $"+{elapsed} s");
        }

        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(10), time.GetElapsedTime(start, time.GetTimestamp()), "the fixture: 10 s of monotonic time");
        Assert.IsTrue(watcher.Check(), "+10 s");
        Assert.IsTrue(watcher.IsGone);
        Assert.AreEqual(6, source.Reads, "the source was read at each of the 6 checks");
    }

    // Hard review item 3: a wall clock that jumps backwards (by a minute or by 7 h) must not make the app
    // never gone: after 10 s of monotonic time with the app absent at every check, it is gone.
    [TestMethod]
    [DataRow(60)]
    [DataRow(7 * 3600)]
    public void AWallClockJumpBackwardDoesNotKeepTheAppFromBeingGone(int jumpSeconds)
    {
        var (watcher, _, time) = Watch(installed: false);
        Assert.IsFalse(watcher.Check(), "t0: first absence");
        time.SetWallClock(time.GetUtcNow().AddSeconds(-jumpSeconds));
        for (var elapsed = 2; elapsed <= 8; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            Assert.IsFalse(watcher.Check(), $"+{elapsed} s monotonic");
        }

        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.IsTrue(watcher.Check(), $"+10 s monotonic, although the wall clock went back {jumpSeconds} s");
    }

    // Hard review item 3: a sighting still restarts the settle time when the wall clock jumps (forward between the
    // sighting and the next absence): the 10 s count from the absence after the sighting, in monotonic time.
    [TestMethod]
    public void ASightingRestartsTheSettleTimeAcrossAWallClockJump()
    {
        var (watcher, source, time) = Watch(installed: false);
        Assert.IsFalse(watcher.Check(), "t0");
        time.Advance(TimeSpan.FromSeconds(6));
        source.Entries.Add(Notes);
        Assert.IsFalse(watcher.Check(), "seen again at +6 s");
        source.Entries.Remove(Notes);
        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.IsFalse(watcher.Check(), "absent again at +8 s: the settle time restarts");
        time.SetWallClock(time.GetUtcNow().AddHours(1));
        time.Advance(AppGoneWatcher.CheckInterval);
        Assert.IsFalse(watcher.Check(), "+10 s monotonic, only 2 s since the restart (the wall clock jumped 1 h)");
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.IsTrue(watcher.Check(), "+18 s: 10 s since the restart");
    }
}
