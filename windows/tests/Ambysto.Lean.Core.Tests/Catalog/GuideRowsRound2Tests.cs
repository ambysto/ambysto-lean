using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Acceptance.Find;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A2, review round 2 (plan "Phase A2 review round 2", items 1 to 3), written from the plan before the
/// fix. Item 1: an <c>InstallLocation</c> is made full (<c>Path.GetFullPath</c>) before any <c>\\?\</c> prefix, so <c>/</c>,
/// <c>.</c> and <c>..</c> still give the row, and one that is not an absolute local path gives none. Item 2: a
/// <c>Local State</c> or <c>app-update.yml</c> that is a symbolic link is never followed (no row). Item 3: a cancel during the
/// last measure throws. The AmbyCloud tests of item 2's recall guard are <see cref="GuideRowsCloudTests"/> (round 1); a file
/// that is hydrated at the attribute check and dehydrated before the open cannot be arranged deterministically (no hook
/// between the two calls in the code under test, and the provider dehydrates only when its serve process is stopped).</summary>
public sealed partial class GuideRowsTests
{
    // Item 1: the InstallLocation is made full first ----------------------------------------------------------------

    private static async Task<(IReadOnlyList<GuideRow> Rows, long Expected)> ReadOneUpdaterAsync(string kind, Func<string, string> locate)
    {
        using var rig = new Rig("guide-r2-location-" + kind);
        var install = rig.AddApp("Located App", UpdaterKey + ": located-updater\n");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(install)!, "sub"));
        Rig.Put(Path.Combine(rig.Pending("located-updater"), "u.exe"), 37000);
        var expected = await Measured(rig.Pending("located-updater"));
        rig.Source.Entries[^1] = Rig.Entry("Located App", locate(install));
        Assert.IsTrue(File.Exists(Path.Combine(install, "resources", "app-update.yml")), "precondition: the real folder holds the yml");

        var rows = await rig.Read();

        return (rows, expected);
    }

    [TestMethod]
    [DataRow("forward-slashes")]
    [DataRow("dot-component")]
    [DataRow("dotdot-component")]
    [DataRow("forward-slashes-with-dotdot")]
    [DataRow("trailing-separator")]
    public async Task AnInstallLocationThatNeedsNormalisingStillResolvesToTheRealFolderAndGivesTheRow(string kind)
    {
        string Locate(string install)
        {
            var parent = Path.GetDirectoryName(install)!;
            var name = Path.GetFileName(install);
            return kind switch
            {
                "forward-slashes" => install.Replace('\\', '/'),
                "dot-component" => parent + @"\.\" + name,
                "dotdot-component" => parent + @"\sub\..\" + name,
                "forward-slashes-with-dotdot" => (parent + @"\sub\..\" + name).Replace('\\', '/'),
                "trailing-separator" => install + @"\",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        var (rows, expected) = await ReadOneUpdaterAsync(kind, Locate);

        Assert.AreEqual(1, rows.Count, "a row for the install location " + kind);
        var row = rows[0];
        Assert.AreEqual("Located App", row.Title);
        Assert.AreEqual(expected, row.Bytes, "the install location " + kind + " resolves to the real folder, whose yml names the updater folder");
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("rooted-without-drive")]
    [DataRow("drive-relative")]
    [DataRow("unc-admin-share")]
    public async Task AnInstallLocationThatIsNotAnAbsoluteLocalPathGivesNoRow(string kind)
    {
        using var rig = new Rig("guide-r2-not-local-" + kind);
        var install = rig.AddApp("Remote App", UpdaterKey + ": remote-updater\n");
        Rig.Put(Path.Combine(rig.Pending("remote-updater"), "u.exe"), 37000);
        var cwd = Environment.CurrentDirectory;
        var sameDrive = string.Equals(Path.GetPathRoot(cwd), Path.GetPathRoot(install), StringComparison.OrdinalIgnoreCase);
        string location;
        switch (kind)
        {
            case "relative":
                location = Path.GetRelativePath(cwd, install);
                break;
            case "rooted-without-drive":
                location = install[2..];
                break;
            case "drive-relative":
                location = install[..2] + Path.GetRelativePath(cwd, install);
                break;
            default:
                location = @"\\localhost\" + install[0] + "$" + install[2..];
                break;
        }

        if (kind != "unc-admin-share" && !sameDrive)
        {
            Assert.Inconclusive("the working folder " + cwd + " is on another drive than the fixture " + install + ": the relative form would not reach it");
        }

        if (kind != "unc-admin-share")
        {
            // Whatever it is, the form must reach the real folder from here, or the test proves nothing.
            var reached = kind == "rooted-without-drive" ? Path.Combine(Path.GetPathRoot(cwd)!, location.TrimStart('\\')) : Path.GetFullPath(location, cwd);
            Assert.IsTrue(File.Exists(Path.Combine(reached, "resources", "app-update.yml")), "precondition: '" + location + "' reaches the real folder from the working folder");
        }

        rig.Source.Entries[^1] = Rig.Entry("Remote App", location);
        rig.AddApp("Good App", UpdaterKey + ": good-updater\n");
        Rig.Put(Path.Combine(rig.Pending("good-updater"), "y.exe"), 21000);

        var rows = await rig.Read();

        Assert.AreEqual("Good App", rows.Single().Title, "'" + location + "' is not an absolute local path: no row for it; the other is kept");
    }

    // Item 2: a link is never followed (a file symbolic link needs the privilege: category Admin, Inconclusive without it) ---

    [TestMethod]
    [TestCategory("Admin")]
    public async Task ALocalStateThatIsASymbolicLinkToAReadableFileGivesNoRow()
    {
        using var rig = new Rig("guide-r2-link-localstate");
        var real = rig.Temp.Folder("real-local-state");
        Rig.LocalState(real, "Default");
        var chromeData = rig.UserData(@"Google\Chrome");
        Directory.CreateDirectory(chromeData);
        PicturePreviewTests.MakeFileLinkOrInconclusive(Path.Combine(chromeData, "Local State"), Path.Combine(real, "Local State"));
        Rig.Put(Path.Combine(chromeData, "Default", "Cache", "Cache_Data", "data_0"), 52000);

        // Control: the same file, plain, in Edge's User Data gives a row; so the link is the only difference.
        var edgeData = rig.UserData(@"Microsoft\Edge");
        Directory.CreateDirectory(edgeData);
        File.Copy(Path.Combine(real, "Local State"), Path.Combine(edgeData, "Local State"));
        Rig.Put(Path.Combine(edgeData, "Default", "Cache", "Cache_Data", "data_0"), 41000);
        var edgeBytes = await Measured(Path.Combine(edgeData, "Default", "Cache", "Cache_Data"));

        var rows = await rig.Read();

        NoRow(rows, "chrome");
        Assert.AreEqual(edgeBytes, RowOf(rows, "edge").Bytes, "control: the plain copy is read");
    }

    [TestMethod]
    [TestCategory("Admin")]
    public async Task AnAppUpdateYmlThatIsASymbolicLinkToAReadableFileGivesNoRowForThatAppOnly()
    {
        using var rig = new Rig("guide-r2-link-yml");
        var realYml = Path.Combine(rig.Temp.Folder("real-yml"), "app-update.yml");
        File.WriteAllText(realYml, UpdaterKey + ": linked-updater\n");
        var install = rig.Temp.Folder("apps", "linked");
        Directory.CreateDirectory(Path.Combine(install, "resources"));
        PicturePreviewTests.MakeFileLinkOrInconclusive(Path.Combine(install, "resources", "app-update.yml"), realYml);
        rig.Source.Entries.Add(Rig.Entry("Linked App", install));
        Rig.Put(Path.Combine(rig.Pending("linked-updater"), "l.exe"), 29000);

        rig.AddApp("Good App", UpdaterKey + ": good-updater\n");
        Rig.Put(Path.Combine(rig.Pending("good-updater"), "y.exe"), 21000);
        var good = await Measured(rig.Pending("good-updater"));

        var rows = await rig.Read();

        Assert.AreEqual(1, rows.Count, "only the other app has a row; got " + string.Join(", ", rows.Select(r => r.Title)));
        Assert.AreEqual("Good App", rows[0].Title, "the app whose app-update.yml is a link has no row; the other is kept");
        Assert.AreEqual(good, rows[0].Bytes);
    }

    // Item 3: a cancel during the last measure ------------------------------------------------------------------------

    /// <summary>No browser, no CapCut, one updater whose <c>pending</c> is the only measure, so the last measure is the
    /// first and nothing is measured after it. Not deterministic (the read has no hook to cancel from; the one
    /// <c>IUninstallSource.ReadEntries</c> call is before the measure): the cancel is timed from an uncancelled run and
    /// raised by a spinning thread (a timer is too coarse). The walker stops soon after a cancel and returns the size so far,
    /// so what tells a partial row from a finished one is when the cancel came: at 40, 30 and 20 percent of the shortest of
    /// three warm uncancelled reads, the walk is still running, and the read must throw. Flake risk: a run more than twice
    /// as fast as the fastest of three warm ones; Inconclusive when the walk takes under 10 ms here.</summary>
    [TestMethod]
    public async Task ACancelDuringTheLastMeasureThrowsAndNeverReturnsAList()
    {
        using var rig = new Rig("guide-r2-cancel-last");
        rig.AddApp("Big App", UpdaterKey + ": big-updater\n");
        var pending = rig.Pending("big-updater");
        Parallel.For(0, 800, new ParallelOptions { MaxDegreeOfParallelism = 8 }, d =>
        {
            var folder = Path.Combine(pending, "d" + d);
            Directory.CreateDirectory(folder);
            for (var f = 0; f < 100; f++)
            {
                File.WriteAllBytes(Path.Combine(folder, "f" + f), [1]);
            }
        });

        var whole = double.MaxValue;
        for (var run = 0; run < 4; run++)
        {
            var watch = Stopwatch.StartNew();
            var rows = await rig.Read();
            watch.Stop();
            Assert.AreEqual("Big App", rows.Single().Title);
            if (run > 0)
            {
                whole = Math.Min(whole, watch.Elapsed.TotalMilliseconds);
            }
        }

        if (whole < 10)
        {
            Assert.Inconclusive("the measure of 80000 files took only " + whole.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms here: too fast to cancel inside it");
        }

        foreach (var fraction in new[] { 0.4, 0.3, 0.2 })
        {
            using var cts = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            var cancelledAt = -1.0;
            var canceller = Task.Factory.StartNew(() =>
            {
                var until = whole * fraction;
                while (clock.Elapsed.TotalMilliseconds < until)
                {
                    Thread.SpinWait(50);
                }

                cancelledAt = clock.Elapsed.TotalMilliseconds;
                cts.Cancel();
            }, TaskCreationOptions.LongRunning);
            try
            {
                var result = await GuideRows.ReadAsync(rig.Context, rig.Source, cts.Token);
                var returnedAt = clock.Elapsed.TotalMilliseconds;
                await canceller;
                Assert.Fail("the read returned " + result.Count + " row(s) (" + string.Join(", ", result.Select(r => r.Title + " " + r.Bytes))
                    + ") although the token was cancelled at " + cancelledAt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms, inside a measure that takes " + whole.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    + " ms (returned at " + returnedAt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms)");
            }
            catch (OperationCanceledException)
            {
                // Right: a cancel is a thrown cancel.
            }

            await canceller;
        }
    }
}
