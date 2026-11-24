using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A2 (plan "Phase A2: the Guide rows", Tests paragraph): <see cref="GuideRows.ReadAsync"/> with temp
/// folders under a fake <c>PathContext.LocalAppData</c> and a fake <see cref="IUninstallSource"/>. Expected sizes are measured
/// here with Lean's own walker (<c>new Scanner().ScanAsync(folder, ...).Root.AllocatedBytes</c>) on the folders the rule names,
/// never hard-coded cluster sizes. Every link in these tests is a directory junction made with <c>Native.CreateJunction</c>
/// (a mount-point reparse point; a standard user may make one, no privilege needed). Session 0; written by the test writer
/// before the code, against a stub.</summary>
[TestClass]
public sealed partial class GuideRowsTests
{
    private const string UpdaterKey = "updaterCacheDirName";

    private static readonly string[] BothOrder = ["chrome", "edge"];
    private static readonly string[] NotVersions = ["1000000000.0", "99.0.0.0.0", "99", "99.x", "v99.0", "99.0-beta", "-99.0", "Crash Reports", "temp", "99.0.0.0.1"];
    private static readonly string[] FormFolders = ["apple-updater", "banana-updater", "cherry-updater", "date-updater"];
    private static readonly string[] FormTitles = ["Apple App", "Banana App", "Cherry App", "Date App"];
    private static readonly string[] ScrambledTitles = ["Charlie", "Banana", "apple", "alpha2", "Alpha"];
    private static readonly string[] SortedTitles = ["Alpha", "alpha2", "apple", "Banana", "Charlie"];
    private static readonly string[] AllIds = ["chrome", "edge", "capcut", "electron-updater", "electron-updater"];
    private static readonly string[] AllTitles = ["Google Chrome", "Microsoft Edge", "CapCut", "Echo App", "Zulu App"];

    private static async Task<long> Measured(params string[] folders)
    {
        long sum = 0;
        foreach (var folder in folders)
        {
            Assert.IsTrue(Directory.Exists(folder), "expected folder exists: " + folder);
            var bytes = (await new Scanner().ScanAsync(folder, null, CancellationToken.None)).Root.AllocatedBytes;
            Assert.IsGreaterThan(0, bytes, "the fixture folder holds something: " + folder);
            sum += bytes;
        }

        return sum;
    }

    private static GuideRow RowOf(IReadOnlyList<GuideRow> rows, string id) => rows.Single(r => r.Entry.Id == id);

    private static void NoRow(IReadOnlyList<GuideRow> rows, string id) =>
        Assert.AreEqual(0, rows.Count(r => r.Entry.Id == id), "no " + id + " row; got " + string.Join(", ", rows.Select(r => r.Entry.Id + "/" + r.Title)));

    /// <summary>A fake LocalAppData in a scratch folder, a fake uninstall source and the helpers every test uses.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(string name)
        {
            Temp = new TempFolder(name);
            Local = Temp.Folder("Vol", "Users", "me", "AppData", "Local");
            var roaming = Temp.Folder("Vol", "Users", "me", "AppData", "Roaming");
            Context = new PathContext(Temp.Folder("Vol", "Users", "me"), roaming, Local, Temp.Folder("Vol"), Temp.Folder("Vol", "Users"),
                [Temp.Folder("Vol", "Windows")], []);
        }

        public TempFolder Temp { get; }

        public string Local { get; }

        public PathContext Context { get; }

        public FakeUninstallSource Source { get; } = new();

        public Task<IReadOnlyList<GuideRow>> Read() => GuideRows.ReadAsync(Context, Source, CancellationToken.None);

        public string UserData(string vendorPath) => Path.Combine(Local, vendorPath, "User Data");

        /// <summary>A file of <paramref name="length"/> bytes at <paramref name="path"/> (folders made).</summary>
        public static string Put(string path, long length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TempFolder.WriteBytes(path, length, 3);
            return path;
        }

        /// <summary>User Data\Local State with the given profile keys in profile.info_cache.</summary>
        public static void LocalState(string userData, params string[] profiles)
        {
            Directory.CreateDirectory(userData);
            var cache = profiles.ToDictionary(p => p, _ => new { name = "person" });
            File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = cache } }));
        }

        /// <summary>A junction at <paramref name="link"/> to a real folder holding a file of <paramref name="length"/> bytes
        /// elsewhere in the scratch folder (its parent is made).</summary>
        public string Junction(string link, long length, string targetName)
        {
            var target = Temp.Folder("link-targets", targetName);
            Put(Path.Combine(target, "inside.bin"), length);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Native.CreateJunction(link, target);
            Assert.IsTrue(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "the fixture link is a reparse point");
            return target;
        }

        /// <summary>An installed app whose InstallLocation holds resources\app-update.yml with the given text.</summary>
        public string AddApp(string name, string? yml, string? key = null)
        {
            var install = Temp.Folder("apps", Guid.NewGuid().ToString("N")[..6] + "-" + name.Replace(' ', '_'));
            if (yml is not null)
            {
                var file = Path.Combine(install, "resources", "app-update.yml");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, yml, new UTF8Encoding(false));
            }

            Source.Entries.Add(Entry(name, install, key));
            return install;
        }

        public static UninstallEntry Entry(string name, string? installLocation, string? key = null) =>
            new(key ?? "key-" + name, UninstallScope.PerUser, RegistryViewKind.Bits64, name, null, null, installLocation, false);

        public string Pending(string folderName) => Path.Combine(Local, folderName, "pending");

        public void Dispose() => Temp.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Browsers
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("chrome", @"Google\Chrome", "Google Chrome")]
    [DataRow("edge", @"Microsoft\Edge", "Microsoft Edge")]
    public async Task OneRowPerBrowserSumsExactlyTheThreeCacheFoldersOfEveryListedProfile(string id, string vendorPath, string appName)
    {
        using var rig = new Rig("guide-" + id);
        var userData = rig.UserData(vendorPath);
        Rig.LocalState(userData, "Default", "Profile 1", "Profile 2");

        // Default: the three cache folders, plus things that are not counted.
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 4000);
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "sub", "f_000001"), 70001);
        Rig.Put(Path.Combine(userData, "Default", "Code Cache", "js", "index"), 30000);
        Rig.Put(Path.Combine(userData, "Default", "GPUCache", "data_1"), 7000);
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Other", "not-counted.bin"), 50000);
        Rig.Put(Path.Combine(userData, "Default", "Service Worker", "CacheStorage", "not-counted.bin"), 40000);
        Rig.Put(Path.Combine(userData, "Default", "Cookies"), 11000);
        Rig.Put(Path.Combine(userData, "Default", "History"), 12000);

        // Profile 1: two of the three. Profile 2 is in the list but has no folder at all (counts 0).
        Rig.Put(Path.Combine(userData, "Profile 1", "Cache", "Cache_Data", "data_0"), 90000);
        Rig.Put(Path.Combine(userData, "Profile 1", "GPUCache", "data_1"), 12345);

        // Not in the list: not counted, however much it holds.
        Rig.Put(Path.Combine(userData, "Profile 7", "Cache", "Cache_Data", "data_0"), 60000);
        Rig.Put(Path.Combine(userData, "Guest Profile", "Cache", "Cache_Data", "data_0"), 61000);
        Rig.Put(Path.Combine(userData, "System Profile", "GPUCache", "data_1"), 62000);

        var expected = await Measured(
            Path.Combine(userData, "Default", "Cache", "Cache_Data"), Path.Combine(userData, "Default", "Code Cache"), Path.Combine(userData, "Default", "GPUCache"),
            Path.Combine(userData, "Profile 1", "Cache", "Cache_Data"), Path.Combine(userData, "Profile 1", "GPUCache"));

        var rows = await rig.Read();

        var row = RowOf(rows, id);
        Assert.AreEqual(1, rows.Count(r => r.Entry.Id == id), "one row per browser");
        Assert.AreEqual(appName, row.Title, "Title = the app name");
        Assert.AreEqual(AppCatalog.Entries.Single(e => e.Id == id), row.Entry);
        Assert.AreEqual(userData, row.Folder, "Folder = User Data");
        Assert.AreEqual(GuideAction.OpenClearBrowsingData, row.Action);
        Assert.AreEqual(expected, row.Bytes);
    }

    [TestMethod]
    [DataRow(@"Google\Chrome")]
    [DataRow(@"Microsoft\Edge")]
    public async Task TheSharedShaderCachesAreCountedOnceNotOncePerProfile(string vendorPath)
    {
        using var rig = new Rig("guide-shader");
        var userData = rig.UserData(vendorPath);
        Rig.LocalState(userData, "Default", "Profile 1", "Profile 3");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 21000);
        Rig.Put(Path.Combine(userData, "Profile 1", "Cache", "Cache_Data", "data_0"), 33000);
        Rig.Put(Path.Combine(userData, "ShaderCache", "GPUCache", "data_1"), 20000);
        Rig.Put(Path.Combine(userData, "GrShaderCache", "GPUCache", "data_2"), 25000);
        var expected = await Measured(
            Path.Combine(userData, "Default", "Cache", "Cache_Data"), Path.Combine(userData, "Profile 1", "Cache", "Cache_Data"),
            Path.Combine(userData, "ShaderCache"), Path.Combine(userData, "GrShaderCache"));

        var rows = await rig.Read();

        Assert.AreEqual(expected, rows.Single().Bytes, "each shader cache once, with three profiles listed");
    }

    [TestMethod]
    public async Task ShaderCachesAloneGiveARowWhenTheProfileListIsReadable()
    {
        using var rig = new Rig("guide-shader-only");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.LocalState(userData, "Default");
        Rig.Put(Path.Combine(userData, "ShaderCache", "data_0"), 18000);
        Rig.Put(Path.Combine(userData, "GrShaderCache", "data_0"), 19000);
        var expected = await Measured(Path.Combine(userData, "ShaderCache"), Path.Combine(userData, "GrShaderCache"));

        var rows = await rig.Read();

        Assert.AreEqual(expected, RowOf(rows, "chrome").Bytes);
    }

    [TestMethod]
    public async Task BothBrowsersGiveTheirOwnRowsChromeThenEdge()
    {
        using var rig = new Rig("guide-both");
        var chrome = rig.UserData(@"Google\Chrome");
        var edge = rig.UserData(@"Microsoft\Edge");
        Rig.LocalState(chrome, "Default");
        Rig.LocalState(edge, "Default");
        Rig.Put(Path.Combine(chrome, "Default", "Cache", "Cache_Data", "d"), 40000);
        Rig.Put(Path.Combine(edge, "Default", "Cache", "Cache_Data", "d"), 80000);
        var chromeBytes = await Measured(Path.Combine(chrome, "Default", "Cache", "Cache_Data"));
        var edgeBytes = await Measured(Path.Combine(edge, "Default", "Cache", "Cache_Data"));

        var rows = await rig.Read();

        CollectionAssert.AreEqual(BothOrder, rows.Select(r => r.Entry.Id).ToArray());
        Assert.AreEqual(chromeBytes, rows[0].Bytes, "Edge's folders are not in Chrome's row");
        Assert.AreEqual(edgeBytes, rows[1].Bytes);
        Assert.AreEqual(chrome, rows[0].Folder);
        Assert.AreEqual(edge, rows[1].Folder);
    }

    [TestMethod]
    public async Task ProfileKeysThatAreNotASinglePlainFolderNameAreSkipped()
    {
        using var rig = new Rig("guide-unsafe-keys");
        var chromeRoot = Path.Combine(rig.Local, "Google", "Chrome");
        var userData = Path.Combine(chromeRoot, "User Data");
        // "C:x" has no decoy folder (a drive-relative name has no place under the scratch folder); the others do.
        Rig.LocalState(userData, "Default", "", ".", "..", @"a\b", "a/b", "C:x", @"..\Escape");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 15000);

        // Where each unsafe key would lead if it were joined to User Data.
        Rig.Put(Path.Combine(userData, "Cache", "Cache_Data", "decoy-empty-and-dot"), 91000);        // "" and "."
        Rig.Put(Path.Combine(userData, "Code Cache", "decoy-empty-and-dot"), 92000);
        Rig.Put(Path.Combine(chromeRoot, "Cache", "Cache_Data", "decoy-dotdot"), 93000);              // ".."
        Rig.Put(Path.Combine(chromeRoot, "GPUCache", "decoy-dotdot"), 94000);
        Rig.Put(Path.Combine(userData, "a", "b", "Cache", "Cache_Data", "decoy-slash"), 95000);       // "a\b" and "a/b"
        Rig.Put(Path.Combine(userData, "a", "b", "GPUCache", "decoy-slash"), 96000);
        Rig.Put(Path.Combine(chromeRoot, "Escape", "Cache", "Cache_Data", "decoy-escape"), 97000);    // "..\Escape"
        var expected = await Measured(Path.Combine(userData, "Default", "Cache", "Cache_Data"));

        var rows = await rig.Read();

        Assert.AreEqual(expected, RowOf(rows, "chrome").Bytes, "only Default counts");
    }

    [TestMethod]
    public async Task AFolderThatIsALinkCountsZeroAndTheRealOnesNextToItStillCount()
    {
        using var rig = new Rig("guide-links");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.LocalState(userData, "Default");
        // Links (directory junctions) in place of Cache_Data, GPUCache and ShaderCache, each to a real folder with files.
        rig.Junction(Path.Combine(userData, "Default", "Cache", "Cache_Data"), 70000, "cache-data");
        rig.Junction(Path.Combine(userData, "Default", "GPUCache"), 71000, "gpu");
        rig.Junction(Path.Combine(userData, "ShaderCache"), 72000, "shader");
        // Real: Code Cache and GrShaderCache.
        Rig.Put(Path.Combine(userData, "Default", "Code Cache", "js", "index"), 25000);
        Rig.Put(Path.Combine(userData, "GrShaderCache", "data_0"), 26000);
        var expected = await Measured(Path.Combine(userData, "Default", "Code Cache"), Path.Combine(userData, "GrShaderCache"));

        var rows = await rig.Read();

        Assert.AreEqual(expected, RowOf(rows, "chrome").Bytes, "the three linked folders are not counted, not followed");
    }

    [TestMethod]
    public async Task WhenEveryCacheFolderIsALinkTheBrowserHasNoRow()
    {
        using var rig = new Rig("guide-links-only");
        var userData = rig.UserData(@"Microsoft\Edge");
        Rig.LocalState(userData, "Default");
        rig.Junction(Path.Combine(userData, "Default", "Cache", "Cache_Data"), 70000, "a");
        rig.Junction(Path.Combine(userData, "Default", "Code Cache"), 71000, "b");
        rig.Junction(Path.Combine(userData, "Default", "GPUCache"), 72000, "c");
        rig.Junction(Path.Combine(userData, "ShaderCache"), 73000, "d");
        rig.Junction(Path.Combine(userData, "GrShaderCache"), 74000, "e");

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, "a link gives no row");
    }

    [TestMethod]
    public async Task NoLocalStateGivesNoRowHoweverMuchUserDataHolds()
    {
        using var rig = new Rig("guide-no-local-state");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 80000);
        Rig.Put(Path.Combine(userData, "ShaderCache", "data_0"), 81000);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("")]
    [DataRow("{\"profile\":{\"info_cache\":")]
    [DataRow("{}")]
    [DataRow("{\"profile\":{}}")]
    [DataRow("{\"profile\":{\"info_cache\":{}}}")]
    [DataRow("{\"profile\":{\"info_cache\":[\"Default\"]}}")]
    [DataRow("{\"profile\":{\"info_cache\":\"Default\"}}")]
    [DataRow("{\"profile\":[]}")]
    [DataRow("[\"Default\"]")]
    public async Task ALocalStateWithoutAReadableProfileListGivesNoRow(string localState)
    {
        using var rig = new Rig("guide-bad-local-state");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 80000);
        Rig.Put(Path.Combine(userData, "ShaderCache", "data_0"), 81000);
        File.WriteAllText(Path.Combine(userData, "Local State"), localState);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, "no readable profile list: no row (nothing is guessed)");
    }

    [TestMethod]
    public async Task ALocalStateOverFourMebibytesWhoseProfileListLiesBeyondTheLimitGivesNoRow()
    {
        using var rig = new Rig("guide-big-local-state");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 80000);
        // 5 MiB of padding first, the profile list after it: beyond the 4 MiB the code may read.
        var json = "{\"pad\":\"" + new string('x', 5 * 1024 * 1024) + "\",\"profile\":{\"info_cache\":{\"Default\":{}}}}";
        File.WriteAllText(Path.Combine(userData, "Local State"), json);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task ALargeLocalStateUnderTheLimitIsStillRead()
    {
        using var rig = new Rig("guide-large-local-state");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 80000);
        var json = "{\"pad\":\"" + new string('x', 3 * 1024 * 1024) + "\",\"profile\":{\"info_cache\":{\"Default\":{}}}}";
        File.WriteAllText(Path.Combine(userData, "Local State"), json);
        var expected = await Measured(Path.Combine(userData, "Default", "Cache", "Cache_Data"));

        var rows = await rig.Read();

        Assert.AreEqual(expected, RowOf(rows, "chrome").Bytes);
    }

    [TestMethod]
    public async Task AProfileListWithNothingToCountGivesNoRow()
    {
        using var rig = new Rig("guide-zero-browser");
        var userData = rig.UserData(@"Google\Chrome");
        Rig.LocalState(userData, "Default", "Profile 1");
        // The folders exist but hold nothing (empty folders, one empty file): measured size 0.
        Directory.CreateDirectory(Path.Combine(userData, "Default", "Cache", "Cache_Data", "empty-sub"));
        Directory.CreateDirectory(Path.Combine(userData, "Default", "Code Cache"));
        Directory.CreateDirectory(Path.Combine(userData, "Default", "GPUCache"));
        Directory.CreateDirectory(Path.Combine(userData, "ShaderCache"));
        Directory.CreateDirectory(Path.Combine(userData, "GrShaderCache"));
        Rig.Put(Path.Combine(userData, "Profile 1", "GPUCache", "empty.bin"), 0);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, "size 0: not listed");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // CapCut
    // ---------------------------------------------------------------------------------------------------------------

    private static string Apps(Rig rig) => Path.Combine(rig.Local, "CapCut", "Apps");

    private static string Version(Rig rig, string name, long length)
    {
        var folder = Path.Combine(Apps(rig), name);
        Rig.Put(Path.Combine(folder, "bin", "nested", "app.dll"), length);
        Rig.Put(Path.Combine(folder, "readme.txt"), length / 3 + 1);
        return folder;
    }

    [TestMethod]
    public async Task CapCutKeepsTheNewestVersionAndCountsTheOthers()
    {
        using var rig = new Rig("guide-capcut");
        var v3 = Version(rig, "3.0.0", 300000);
        var v4 = Version(rig, "4.0.0", 400000);
        var v5 = Version(rig, "5.0.0", 500000);
        var older = await Measured(v3, v4);
        Assert.IsGreaterThan(0, await Measured(v5));

        var rows = await rig.Read();

        var row = RowOf(rows, "capcut");
        Assert.AreEqual(older, row.Bytes, "the newest (5.0.0) is never counted");
        Assert.AreEqual("CapCut", row.Title);
        Assert.AreEqual(Apps(rig), row.Folder, "Folder = Apps");
        Assert.AreEqual(GuideAction.OpenFolder, row.Action);
        Assert.AreEqual(AppCatalog.Entries.Single(e => e.Id == "capcut"), row.Entry);
    }

    [TestMethod]
    public async Task CapCutComparesVersionsAsNumbersNotAsText()
    {
        using var rig = new Rig("guide-capcut-compare");
        var v99 = Version(rig, "9.9", 250000);
        var v98 = Version(rig, "9.8", 350000);
        Version(rig, "10.0", 900000);
        var older = await Measured(v99, v98);

        var rows = await rig.Read();

        Assert.AreEqual(older, RowOf(rows, "capcut").Bytes, "10.0 is newer than 9.9: it is kept, 9.9 and 9.8 are counted");
    }

    [TestMethod]
    public async Task CapCutAcceptsTwoToFourPartsAndPartsOfNineDigits()
    {
        using var rig = new Rig("guide-capcut-parts");
        var a = Version(rig, "1.2", 120000);
        var b = Version(rig, "1.2.3", 130000);
        var c = Version(rig, "1.2.3.4", 140000); // newest of the three
        var older = await Measured(a, b);
        Assert.IsGreaterThan(0, await Measured(c));

        var rows = await rig.Read();

        Assert.AreEqual(older, RowOf(rows, "capcut").Bytes);

        // Nine digits in a part is the most that counts as a version.
        Directory.Delete(Path.Combine(rig.Local, "CapCut"), recursive: true);
        var x = Version(rig, "2.0", 210000);
        var y = Version(rig, "1.0", 220000);
        Version(rig, "999999999.0", 230000);
        var olderNine = await Measured(x, y);

        var rowsNine = await rig.Read();

        Assert.AreEqual(olderNine, RowOf(rowsNine, "capcut").Bytes, "999999999.0 parses and is the newest");
    }

    [TestMethod]
    public async Task CapCutIgnoresNamesThatAreNotVersionsAndFilesInApps()
    {
        using var rig = new Rig("guide-capcut-decoys");
        var v2 = Version(rig, "2.0", 200000);
        Version(rig, "3.0", 330000); // the real newest
        // Names that must not parse: none of these is the newest or is counted, although several look newer than 3.0.
        foreach (var name in NotVersions)
        {
            Rig.Put(Path.Combine(Apps(rig), name, "decoy.bin"), 777000);
        }

        // A file named like a version is not a version.
        Rig.Put(Path.Combine(Apps(rig), "99.0"), 888000);
        var older = await Measured(v2);

        var rows = await rig.Read();

        Assert.AreEqual(older, RowOf(rows, "capcut").Bytes);
    }

    [TestMethod]
    public async Task CapCutWithOnlyOneParsableVersionHasNoRow()
    {
        using var rig = new Rig("guide-capcut-single");
        Version(rig, "4.1.0", 400000);
        Rig.Put(Path.Combine(Apps(rig), "not-a-version", "big.bin"), 900000);
        Rig.Put(Path.Combine(Apps(rig), "1000000000.0", "big.bin"), 900000);

        var rows = await rig.Read();

        NoRow(rows, "capcut");
    }

    [TestMethod]
    public async Task CapCutWithNoAppsFolderOrNoVersionsHasNoRow()
    {
        using var rig = new Rig("guide-capcut-none");
        Assert.AreEqual(0, (await rig.Read()).Count, "no CapCut folder at all");

        Directory.CreateDirectory(Apps(rig));
        Assert.AreEqual(0, (await rig.Read()).Count, "an empty Apps folder");
    }

    [TestMethod]
    public async Task CapCutOlderVersionsThatAreLinksCountZero()
    {
        using var rig = new Rig("guide-capcut-links");
        var v1 = Version(rig, "1.0", 100000);
        rig.Junction(Path.Combine(Apps(rig), "2.0"), 600000, "capcut-2");
        var v3 = Version(rig, "3.0", 300000);
        Version(rig, "4.0", 400000);
        var older = await Measured(v1, v3);

        var rows = await rig.Read();

        Assert.AreEqual(older, RowOf(rows, "capcut").Bytes, "the linked 2.0 is not counted, not followed");
    }

    [TestMethod]
    public async Task CapCutWhoseOlderVersionsAreOnlyLinksHasNoRow()
    {
        using var rig = new Rig("guide-capcut-links-only");
        rig.Junction(Path.Combine(Apps(rig), "1.0"), 600000, "capcut-1");
        Version(rig, "2.0", 400000);

        var rows = await rig.Read();

        NoRow(rows, "capcut");
    }

    [TestMethod]
    public async Task CapCutOlderVersionsThatAreEmptyHaveNoRow()
    {
        using var rig = new Rig("guide-capcut-zero");
        Directory.CreateDirectory(Path.Combine(Apps(rig), "1.0", "empty"));
        Directory.CreateDirectory(Path.Combine(Apps(rig), "2.0", "empty"));
        Version(rig, "3.0", 400000);

        var rows = await rig.Read();

        NoRow(rows, "capcut");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // electron-updater
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheUpdaterFolderNameIsReadWithAndWithoutQuotesAndTrimmed()
    {
        using var rig = new Rig("guide-yml-forms");
        rig.AddApp("Apple App", "updaterCacheDirName: apple-updater\n");
        rig.AddApp("Banana App", "updaterCacheDirName: \"banana-updater\"\r\n");
        rig.AddApp("Cherry App", "provider: generic\r\nurl: https://example.invalid/\r\nupdaterCacheDirName: 'cherry-updater'\r\n");
        rig.AddApp("Date App", "updaterCacheDirName:    date-updater   \n");
        foreach (var name in FormFolders)
        {
            Rig.Put(Path.Combine(rig.Pending(name), "update.exe"), 30000 + name.Length);
            Rig.Put(Path.Combine(rig.Local, name, "sibling-not-counted.bin"), 44444);
        }

        var rows = await rig.Read();

        var updaters = rows.Where(r => r.Entry.Id == "electron-updater").ToArray();
        CollectionAssert.AreEqual(FormTitles, updaters.Select(r => r.Title).ToArray());
        var names = FormFolders;
        for (var i = 0; i < updaters.Length; i++)
        {
            Assert.AreEqual(rig.Pending(names[i]), updaters[i].Folder, updaters[i].Title);
            Assert.AreEqual(await Measured(rig.Pending(names[i])), updaters[i].Bytes, updaters[i].Title + ": only pending is counted");
            Assert.AreEqual(GuideAction.OpenFolder, updaters[i].Action);
            Assert.AreEqual(AppCatalog.Entries.Single(e => e.Id == "electron-updater"), updaters[i].Entry);
        }
    }

    [TestMethod]
    public async Task TheFirstUpdaterLineWinsAndATrailingBackslashOnInstallLocationIsFine()
    {
        using var rig = new Rig("guide-yml-first");
        var install = rig.AddApp("First App", "updaterCacheDirName: first-updater\nupdaterCacheDirName: second-updater\n");
        rig.Source.Entries[0] = Rig.Entry("First App", install + @"\");
        Rig.Put(Path.Combine(rig.Pending("first-updater"), "a.exe"), 31000);
        Rig.Put(Path.Combine(rig.Pending("second-updater"), "b.exe"), 99000);
        var expected = await Measured(rig.Pending("first-updater"));

        var rows = await rig.Read();

        var row = RowOf(rows, "electron-updater");
        Assert.AreEqual(rig.Pending("first-updater"), row.Folder);
        Assert.AreEqual(expected, row.Bytes);
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("")]
    [DataRow(@"a\b")]
    [DataRow("a/b")]
    [DataRow("C:x")]
    [DataRow(@"..\Escape")]
    [DataRow("\"..\"")]
    [DataRow("'a\\b'")]
    public async Task AnUnsafeUpdaterFolderNameGivesNoRowAndTheFolderItPointsAtIsNeverCounted(string value)
    {
        using var rig = new Rig("guide-yml-unsafe");
        rig.AddApp("Unsafe App", UpdaterKey + ": " + value + "\n");
        var plain = value.Trim('"', '\'');
        if (!plain.Contains(':', StringComparison.Ordinal))
        {
            // Where the value would lead if it were joined to LocalAppData.
            Rig.Put(Path.Combine(rig.Local, plain.Replace('/', '\\'), "pending", "decoy.exe"), 55000);
        }

        // A normal app next to it still gets its row, so the missing row is about the unsafe value only.
        rig.AddApp("Fine App", UpdaterKey + ": fine-updater\n");
        Rig.Put(Path.Combine(rig.Pending("fine-updater"), "ok.exe"), 12000);

        var rows = await rig.Read();

        Assert.AreEqual("Fine App", rows.Single(r => r.Entry.Id == "electron-updater").Title);
    }

    [TestMethod]
    public async Task AMissingPendingFolderAPendingFileOrALinkedPendingGivesNoRow()
    {
        using var rig = new Rig("guide-pending-bad");
        rig.AddApp("Missing Pending", UpdaterKey + ": missing-updater\n");
        Rig.Put(Path.Combine(rig.Local, "missing-updater", "other.bin"), 50000); // the folder exists, pending does not

        rig.AddApp("File Pending", UpdaterKey + ": file-updater\n");
        Rig.Put(rig.Pending("file-updater"), 50000); // pending is a file

        rig.AddApp("Link Pending", UpdaterKey + ": link-updater\n");
        rig.Junction(rig.Pending("link-updater"), 50000, "pending-target"); // pending is a link

        rig.AddApp("No Folder At All", UpdaterKey + ": gone-updater\n");

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task AnEmptyPendingFolderGivesNoRow()
    {
        using var rig = new Rig("guide-pending-zero");
        rig.AddApp("Empty Pending", UpdaterKey + ": empty-updater\n");
        Directory.CreateDirectory(Path.Combine(rig.Pending("empty-updater"), "sub"));

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, "size 0: not listed");
    }

    [TestMethod]
    public async Task EntriesWithoutAUsableYmlGiveNoRowAndDoNotStopTheOthers()
    {
        using var rig = new Rig("guide-yml-missing");
        Rig.Put(Path.Combine(rig.Pending("anything-updater"), "x.exe"), 20000);
        rig.AddApp("No Yml At All", null);
        rig.AddApp("Yml Without The Line", "provider: generic\nchannel: latest\n");
        rig.AddApp("Line Without A Value", UpdaterKey + ":\n");
        rig.AddApp("Different Key", "updaterCacheDirNameX: anything-updater\nupdaterCacheDir: anything-updater\n");
        rig.Source.Entries.Add(Rig.Entry("No Install Location", null));
        rig.Source.Entries.Add(Rig.Entry("Empty Install Location", string.Empty));
        rig.Source.Entries.Add(Rig.Entry("Relative Install Location", @"relative\app"));
        rig.Source.Entries.Add(Rig.Entry("Missing Install Location", Path.Combine(rig.Temp.Path, "no-such-install")));
        rig.AddApp("Good App", UpdaterKey + ": good-updater\n");
        Rig.Put(Path.Combine(rig.Pending("good-updater"), "y.exe"), 21000);

        var rows = await rig.Read();

        Assert.AreEqual("Good App", rows.Single().Title);
    }

    [TestMethod]
    public async Task TheYmlIsReadOnlyUpToSixtyFourKibibytes()
    {
        using var rig = new Rig("guide-yml-size");
        var padUnder = string.Concat(Enumerable.Repeat("# padding padding padding padding padding padding\n", 1000)); // about 49 KB
        var padOver = string.Concat(Enumerable.Repeat("# padding padding padding padding padding padding\n", 1600));  // about 78 KB
        Assert.IsLessThan(64 * 1024, padUnder.Length);
        Assert.IsGreaterThan(64 * 1024, padOver.Length);
        rig.AddApp("Under App", padUnder + UpdaterKey + ": under-updater\n");
        rig.AddApp("Over App", padOver + UpdaterKey + ": over-updater\n");
        Rig.Put(Path.Combine(rig.Pending("under-updater"), "u.exe"), 20000);
        Rig.Put(Path.Combine(rig.Pending("over-updater"), "o.exe"), 20000);

        var rows = await rig.Read();

        Assert.AreEqual("Under App", rows.Single().Title, "the line beyond 64 KiB is not read");
    }

    [TestMethod]
    public async Task TwoEntriesNamingTheSameFolderGiveOneRowNamedByTheFirstEntry()
    {
        using var rig = new Rig("guide-yml-dup");
        rig.AddApp("Zeta Edition", UpdaterKey + ": shared-updater\n");
        rig.AddApp("Alpha Edition", UpdaterKey + ": Shared-Updater\n"); // same folder, other case
        rig.AddApp("Mid Edition", "updaterCacheDirName: \"SHARED-UPDATER\"\n");
        rig.AddApp("Other App", UpdaterKey + ": other-updater\n");
        Rig.Put(Path.Combine(rig.Pending("shared-updater"), "s.exe"), 33000);
        Rig.Put(Path.Combine(rig.Pending("other-updater"), "o.exe"), 34000);

        var rows = await rig.Read();

        var updaters = rows.Where(r => r.Entry.Id == "electron-updater").ToArray();
        Assert.AreEqual(2, updaters.Length, "one row per distinct folder");
        Assert.AreEqual("Other App", updaters[0].Title);
        Assert.AreEqual("Zeta Edition", updaters[1].Title, "the first entry in ReadEntries order names the row, not the alphabetically first");
        Assert.AreEqual(await Measured(rig.Pending("shared-updater")), updaters[1].Bytes, "counted once");
    }

    [TestMethod]
    public async Task UpdaterRowsAreOrderedByTitleOrdinalIgnoreCase()
    {
        using var rig = new Rig("guide-yml-order");
        // Listed in a scrambled order; with a case-sensitive ordinal order "Banana" would come before "apple".
        foreach (var name in ScrambledTitles)
        {
            var folder = name.ToLowerInvariant() + "-updater";
            rig.AddApp(name, UpdaterKey + ": " + folder + "\n");
            Rig.Put(Path.Combine(rig.Pending(folder), "f.exe"), 15000);
        }

        var rows = await rig.Read();

        CollectionAssert.AreEqual(SortedTitles, rows.Select(r => r.Title).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // All together
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RowsComeInCatalogOrderChromeEdgeCapCutThenUpdatersByTitleAndReadingChangesNothing()
    {
        using var rig = new Rig("guide-order");
        // Created in the opposite order to the expected one.
        rig.AddApp("Zulu App", UpdaterKey + ": zulu-updater\n");
        rig.AddApp("Echo App", UpdaterKey + ": echo-updater\n");
        Rig.Put(Path.Combine(rig.Pending("zulu-updater"), "z.exe"), 17000);
        Rig.Put(Path.Combine(rig.Pending("echo-updater"), "e.exe"), 18000);
        Version(rig, "1.0", 100000);
        Version(rig, "2.0", 200000);
        var edge = rig.UserData(@"Microsoft\Edge");
        Rig.LocalState(edge, "Default");
        Rig.Put(Path.Combine(edge, "Default", "GPUCache", "g"), 19000);
        var chrome = rig.UserData(@"Google\Chrome");
        Rig.LocalState(chrome, "Default");
        Rig.Put(Path.Combine(chrome, "Default", "GPUCache", "g"), 16000);
        var before = Snapshot(rig.Temp.Path);

        var rows = await rig.Read();

        CollectionAssert.AreEqual(AllIds, rows.Select(r => r.Entry.Id).ToArray());
        CollectionAssert.AreEqual(AllTitles, rows.Select(r => r.Title).ToArray());
        foreach (var row in rows)
        {
            Assert.IsGreaterThan(0, row.Bytes, row.Title);
        }

        CollectionAssert.AreEqual(before, Snapshot(rig.Temp.Path), "reading rows creates, changes and deletes nothing");
    }

    [TestMethod]
    public async Task NothingInstalledGivesNoRows()
    {
        using var rig = new Rig("guide-nothing");

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count);
    }

    private static string[] Snapshot(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p =>
        {
            var isFile = File.Exists(p);
            return p + "|" + (isFile ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir");
        })];
}
