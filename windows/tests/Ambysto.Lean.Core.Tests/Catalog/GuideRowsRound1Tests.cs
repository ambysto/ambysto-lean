using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A2, review round 1 (plan "Phase A2 review round 1", items 1 to 5), written from the plan before the
/// fix: bytes that are not UTF-8 never fault the read, a cancel is a thrown <see cref="OperationCanceledException"/> and
/// never a partial list, and the edge cases of item 4 and 5. The cloud placeholder tests (item 3) are in
/// <see cref="GuideRowsCloudTests"/>.</summary>
public sealed partial class GuideRowsTests
{
    private static readonly byte[] BadUtf8 = [0xC3, 0x28];

    private static byte[] Bytes(params object[] parts)
    {
        var all = new List<byte>();
        foreach (var part in parts)
        {
            all.AddRange(part is byte[] raw ? raw : new UTF8Encoding(false).GetBytes((string)part));
        }

        return [.. all];
    }

    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    private static async Task AssertCancels(Func<Task> read, string because)
    {
        try
        {
            await read();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("expected OperationCanceledException, got a result instead: " + because);
    }

    // Item 1: invalid UTF-8 -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ALocalStateWhoseProfileKeyIsNotUtf8GivesNoRowForThatBrowserAndDoesNotStopTheOthers()
    {
        using var rig = new Rig("guide-r1-bad-utf8-key");
        var chromeData = rig.UserData(@"Google\Chrome");
        Directory.CreateDirectory(chromeData);
        // "Default" is a valid-looking profile with real cache data; the broken key sits next to it.
        File.WriteAllBytes(Path.Combine(chromeData, "Local State"),
            Bytes("{\"profile\":{\"info_cache\":{\"Default\":{},\"", BadUtf8, "\":{}}}}"));
        Rig.Put(Path.Combine(chromeData, "Default", "Cache", "Cache_Data", "data_0"), 52000);
        Rig.Put(Path.Combine(chromeData, "ShaderCache", "data_0"), 18000);

        var edgeData = rig.UserData(@"Microsoft\Edge");
        Rig.LocalState(edgeData, "Default");
        Rig.Put(Path.Combine(edgeData, "Default", "Cache", "Cache_Data", "data_0"), 41000);
        var edgeBytes = await Measured(Path.Combine(edgeData, "Default", "Cache", "Cache_Data"));

        var v1 = Version(rig, "1.0", 100000);
        var v2 = Version(rig, "2.0", 200000);
        var capCutBytes = await Measured(v1);
        Assert.IsGreaterThan(0, await Measured(v2));

        var rows = await rig.Read();

        NoRow(rows, "chrome");
        Assert.AreEqual(edgeBytes, RowOf(rows, "edge").Bytes, "Edge is read as usual");
        Assert.AreEqual(capCutBytes, RowOf(rows, "capcut").Bytes, "CapCut is read as usual");
    }

    [TestMethod]
    public async Task AnAppUpdateYmlWhoseValueIsNotUtf8GivesNoRowForThatAppAndKeepsTheOthers()
    {
        using var rig = new Rig("guide-r1-bad-utf8-yml");
        // A lenient decoder would read "bad�(-updater", a folder that exists: that must still give no row.
        var lenientName = "bad�(-updater";
        Rig.Put(Path.Combine(rig.Pending(lenientName), "x.exe"), 23000);
        var install = rig.Temp.Folder("apps", "bad-utf8");
        var yml = Path.Combine(install, "resources", "app-update.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(yml)!);
        File.WriteAllBytes(yml, Bytes("provider: generic\n" + UpdaterKey + ": bad", BadUtf8, "-updater\n"));
        rig.Source.Entries.Add(Rig.Entry("Bad Bytes App", install));
        rig.AddApp("Good App", UpdaterKey + ": good-updater\n");
        Rig.Put(Path.Combine(rig.Pending("good-updater"), "y.exe"), 21000);
        var good = await Measured(rig.Pending("good-updater"));

        var rows = await rig.Read();

        Assert.AreEqual("Good App", rows.Single().Title, "the app with the broken yml has no row; the other is kept");
        Assert.AreEqual(good, rows.Single().Bytes);
    }

    // Item 2: a cancel is thrown, never a partial list --------------------------------------------------------------

    /// <summary>One of every kind of row, so the read has work to do after any single step.</summary>
    private static async Task FillEveryKind(Rig rig)
    {
        var userData = rig.UserData(@"Google\Chrome");
        Rig.LocalState(userData, "Default");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 52000);
        Version(rig, "1.0", 100000);
        Version(rig, "2.0", 200000);
        rig.AddApp("Echo App", UpdaterKey + ": echo-updater\n");
        Rig.Put(Path.Combine(rig.Pending("echo-updater"), "e.exe"), 33000);
        Assert.IsGreaterThan(0, await Measured(Path.Combine(userData, "Default", "Cache", "Cache_Data")));
    }

    [TestMethod]
    public async Task ACancelledTokenBeforeTheCallThrowsOperationCanceledAndReturnsNoList()
    {
        using var rig = new Rig("guide-r1-cancel-before");
        await FillEveryKind(rig);

        await AssertCancels(() => GuideRows.ReadAsync(rig.Context, rig.Source, Cancelled()), "the token was cancelled before the call");
    }

    [TestMethod]
    public async Task ACancelRaisedWhileTheReadIsRunningThrowsOperationCanceledAndReturnsNoPartialList()
    {
        using var rig = new Rig("guide-r1-cancel-inside");
        await FillEveryKind(rig);
        using var cts = new CancellationTokenSource();
        var source = new CancellingSource(cts);
        source.Entries.AddRange(rig.Source.Entries);

        await AssertCancels(() => GuideRows.ReadAsync(rig.Context, source, cts.Token),
            "the token was cancelled by the uninstall source, with a browser, CapCut and an updater row still to measure");
        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.AreEqual(1, source.Calls, "the source was asked once");
    }

    // A cancel inside a measure is covered by GuideRowsRound2Tests.ACancelDuringTheLastMeasureThrowsAndNeverReturnsAList: the
    // timing-based test that stood here was inconclusive or flaky (A2 review round 2) and was removed.

    /// <summary>An uninstall source whose first read cancels the caller's token (the cancel arrives in the middle of the read).</summary>
    private sealed class CancellingSource(CancellationTokenSource cts) : IUninstallSource
    {
        public List<UninstallEntry> Entries { get; } = [];

        public int Calls { get; private set; }

        public IReadOnlyList<UninstallEntry> ReadEntries()
        {
            Calls++;
            cts.Cancel();
            return Entries.ToArray();
        }

        public IReadOnlyList<StorePackage> ReadStorePackages() => [];

        public IReadOnlyList<string> ReadProcessImagePaths() => [];
    }

    // Item 4: edge cases ----------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task AnEntryWithoutADisplayNameGivesNoRowAndTheOthersStay(string? displayName)
    {
        using var rig = new Rig("guide-r1-no-name");
        var install = rig.AddApp("placeholder", UpdaterKey + ": nameless-updater\n");
        rig.Source.Entries[^1] = new UninstallEntry("key-nameless", UninstallScope.PerUser, RegistryViewKind.Bits64, displayName, null, null, install, false);
        Rig.Put(Path.Combine(rig.Pending("nameless-updater"), "n.exe"), 24000);
        rig.AddApp("Good App", UpdaterKey + ": good-updater\n");
        Rig.Put(Path.Combine(rig.Pending("good-updater"), "y.exe"), 21000);

        var rows = await rig.Read();

        Assert.AreEqual("Good App", rows.Single().Title, "an entry without a display name has no row");
    }

    [TestMethod]
    public async Task CapCutWhoseNewestVersionIsALinkHasNoRow()
    {
        using var rig = new Rig("guide-r1-capcut-newest-link");
        Version(rig, "1.0", 100000);
        rig.Junction(Path.Combine(Apps(rig), "2.0"), 600000, "capcut-newest");

        var rows = await rig.Read();

        NoRow(rows, "capcut");
    }

    [TestMethod]
    public async Task ALocalStateWithAUtf8ByteOrderMarkIsReadLikeOneWithout()
    {
        using var plain = new Rig("guide-r1-bom-plain");
        using var withBom = new Rig("guide-r1-bom");
        long[] sizes = [52000, 17000];
        foreach (var (rig, bom) in new[] { (plain, false), (withBom, true) })
        {
            var userData = rig.UserData(@"Google\Chrome");
            Rig.LocalState(userData, "Default", "Profile 1");
            Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), sizes[0]);
            Rig.Put(Path.Combine(userData, "Profile 1", "GPUCache", "data_1"), sizes[1]);
            if (bom)
            {
                var path = Path.Combine(userData, "Local State");
                File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(path)]);
                Assert.AreEqual(0xEF, File.ReadAllBytes(path)[0], "the fixture starts with a byte order mark");
            }
        }

        var expected = await Measured(Path.Combine(plain.UserData(@"Google\Chrome"), "Default", "Cache", "Cache_Data"), Path.Combine(plain.UserData(@"Google\Chrome"), "Profile 1", "GPUCache"));

        var plainRows = await plain.Read();
        var bomRows = await withBom.Read();

        Assert.AreEqual(expected, RowOf(plainRows, "chrome").Bytes, "the control without a mark");
        Assert.AreEqual(expected, RowOf(bomRows, "chrome").Bytes, "with a mark: the same row");
    }

    [TestMethod]
    public async Task ProfileKeysThatDifferOnlyInCaseAreOneProfileCountedOnce()
    {
        using var rig = new Rig("guide-r1-case-keys");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.LocalState(userData, "Default", "default");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 61000);
        var once = await Measured(Path.Combine(userData, "Default", "Cache", "Cache_Data"));

        var rows = await rig.Read();

        Assert.AreEqual(once, RowOf(rows, "chrome").Bytes, "Default and default are the same folder on Windows: counted once");
    }

    [TestMethod]
    public async Task AProfileListOfOnlyUnsafeKeysGivesNoRow()
    {
        using var rig = new Rig("guide-r1-all-unsafe");
        var chromeRoot = Path.Combine(rig.Local, "Google", "Chrome");
        var userData = Path.Combine(chromeRoot, "User Data");
        Rig.LocalState(userData, "", ".", "..", @"a\b", "a/b", "C:x", @"..\Escape");
        // No shared shader cache here, so nothing legitimate is left to count; where each key would lead has decoys.
        Rig.Put(Path.Combine(userData, "Cache", "Cache_Data", "decoy"), 91000);
        Rig.Put(Path.Combine(userData, "GPUCache", "decoy"), 92000);
        Rig.Put(Path.Combine(chromeRoot, "Cache", "Cache_Data", "decoy"), 93000);
        Rig.Put(Path.Combine(userData, "a", "b", "Cache", "Cache_Data", "decoy"), 95000);
        Rig.Put(Path.Combine(chromeRoot, "Escape", "Cache", "Cache_Data", "decoy"), 97000);

        var rows = await rig.Read();

        NoRow(rows, "chrome");
    }

    // Item 5: versions that are equal as versions --------------------------------------------------------------------

    [TestMethod]
    public async Task CapCutKeepsTheGreatestNameAmongEqualVersionsAndCountsTheOthers()
    {
        using var rig = new Rig("guide-r1-capcut-equal");
        var padded = Version(rig, "01.2", 150000);
        var plainOld = Version(rig, "1.0", 90000);
        var kept = Version(rig, "1.2", 330000);
        var counted = await Measured(padded, plainOld);
        Assert.IsGreaterThan(0, await Measured(kept));

        var rows = await rig.Read();

        Assert.AreEqual(counted, RowOf(rows, "capcut").Bytes, "1.2 and 01.2 are equal versions: the greatest name (ordinal) 1.2 is kept; 01.2 and 1.0 are counted");
    }
}
