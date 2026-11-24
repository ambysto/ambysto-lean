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

/// <summary>ASP-20 phase C1 (plan "Phase C", "C1 Core" and "C1 tests"): <see cref="SpaceByApp.ReadAsync"/> with fake
/// <see cref="InstalledApp"/> values over temp folders and hand-made <see cref="GuideRow"/> values. Expected sizes are measured
/// here with Lean's own walker (<c>new Scanner().ScanAsync(folder, ...).Root.AllocatedBytes</c>), never hard-coded cluster
/// sizes. The key fields the matching rests on (<see cref="CatalogEntry.UninstallKeyName"/> for chrome, edge and capcut,
/// <see cref="GuideRow.UninstallKeyName"/> for electron-updater rows) are checked here too, the second through the real
/// <see cref="GuideRows"/>. Session 0; written by the test writer before the code, against a stub.</summary>
[TestClass]
public sealed class SpaceByAppTests
{
    private static readonly string[] OrderedNames = ["Top", "Mid", "Small", "Alpha", "Bravo", "Zulu"];

    private static async Task<long> Measured(string folder)
    {
        Assert.IsTrue(Directory.Exists(folder), "expected folder exists: " + folder);
        var bytes = (await new Scanner().ScanAsync(folder, null, CancellationToken.None)).Root.AllocatedBytes;
        Assert.IsGreaterThan(0, bytes, "the fixture folder holds something: " + folder);
        return bytes;
    }

    private static InstalledApp App(string name, string key, string? location, long? estimate = null, string? publisher = null) =>
        new(name, publisher, null, location, AppClass.PerUser,
            new UninstallEntry(key, UninstallScope.PerUser, RegistryViewKind.Bits64, name, publisher, null, location, false) { EstimatedSizeBytes = estimate }, null);

    /// <summary>A row of the catalog entry <paramref name="id"/>; <paramref name="key"/> is the row's own uninstall key
    /// (electron-updater rows only; the others carry it on their catalog entry).</summary>
    private static GuideRow Row(string id, long bytes, string? key = null, string? title = null)
    {
        var entry = AppCatalog.Entries.Single(e => e.Id == id);
        return new GuideRow(entry, title ?? entry.AppName, @"C:\fake\folder", bytes, GuideAction.OpenFolder) { UninstallKeyName = key };
    }

    private static string Describe(IReadOnlyList<GuideRow> rows) =>
        string.Join(",", rows.OrderBy(r => r.Entry.Id, StringComparer.Ordinal).ThenBy(r => r.Bytes).Select(r => r.Entry.Id + ":" + r.Bytes));

    private static AppSpaceRow Of(IReadOnlyList<AppSpaceRow> rows, string name) =>
        rows.Single(r => r.Name == name);

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

        /// <summary>An install folder holding two files of different sizes, one in a subfolder.</summary>
        public string Install(string name, long length = 70000)
        {
            var folder = Temp.Folder("apps", name);
            TempFolder.WriteBytes(Path.Combine(folder, "app.exe"), length, 3);
            Directory.CreateDirectory(Path.Combine(folder, "data"));
            TempFolder.WriteBytes(Path.Combine(folder, "data", "pack.bin"), length / 2 + 1, 4);
            return folder;
        }

        public void Dispose() => Temp.Dispose();
    }

    private static Task<IReadOnlyList<AppSpaceRow>> Read(IReadOnlyList<InstalledApp> apps, params GuideRow[] rows) =>
        SpaceByApp.ReadAsync(apps, rows, CancellationToken.None);

    // ---------------------------------------------------------------------------------------------------------------
    // The keys the matching rests on
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("chrome", "Google Chrome")]
    [DataRow("edge", "Microsoft Edge")]
    [DataRow("capcut", "CapCut")]
    [DataRow("npm", null)]
    [DataRow("pip", null)]
    [DataRow("uv", null)]
    [DataRow("nuget-http", null)]
    [DataRow("nuget-plugins", null)]
    [DataRow("electron-updater", null)]
    public void TheCatalogNamesTheUninstallKeyOfChromeEdgeAndCapCutOnly(string id, string? expected)
    {
        Assert.AreEqual(expected, AppCatalog.Entries.Single(e => e.Id == id).UninstallKeyName);
    }

    [TestMethod]
    public async Task ElectronUpdaterRowsCarryTheKeyOfTheEntryThatNamedThemAndTheOtherRowsNone()
    {
        using var rig = new Rig("sba-keys");
        var source = new FakeUninstallSource();
        foreach (var (title, folder, key) in new[] { ("Echo App", "echo-updater", "{3F2A-ECHO}"), ("Zulu App", "zulu-updater", "Zulu_is1") })
        {
            var install = rig.Temp.Folder("apps", folder);
            Directory.CreateDirectory(Path.Combine(install, "resources"));
            File.WriteAllText(Path.Combine(install, "resources", "app-update.yml"), "updaterCacheDirName: " + folder + "\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(rig.Local, folder, "pending"));
            TempFolder.WriteBytes(Path.Combine(rig.Local, folder, "pending", "update.exe"), 30000, 3);
            source.Entries.Add(new UninstallEntry(key, UninstallScope.PerUser, RegistryViewKind.Bits64, title, null, null, install, false));
        }

        var userData = Path.Combine(rig.Local, "Google", "Chrome", "User Data");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object> { ["Default"] = new { name = "p" } } } }));
        Directory.CreateDirectory(Path.Combine(userData, "Default", "GPUCache"));
        TempFolder.WriteBytes(Path.Combine(userData, "Default", "GPUCache", "data_0"), 50000, 3);
        var capCut = Path.Combine(rig.Local, "CapCut", "Apps");
        foreach (var version in new[] { "1.0.0.0", "2.0.0.0" })
        {
            Directory.CreateDirectory(Path.Combine(capCut, version));
            TempFolder.WriteBytes(Path.Combine(capCut, version, "capcut.exe"), 40000, 3);
        }

        var rows = await GuideRows.ReadAsync(rig.Context, source, CancellationToken.None);

        Assert.AreEqual("{3F2A-ECHO}", rows.Single(r => r.Title == "Echo App").UninstallKeyName, "the entry's own key, not a name or a folder");
        Assert.AreEqual("Zulu_is1", rows.Single(r => r.Title == "Zulu App").UninstallKeyName);
        Assert.AreEqual(2, rows.Count(r => r.Entry.Id == "electron-updater"));
        Assert.IsNull(rows.Single(r => r.Entry.Id == "chrome").UninstallKeyName, "a browser row is matched by its catalog entry, not by its own key");
        Assert.IsNull(rows.Single(r => r.Entry.Id == "capcut").UninstallKeyName);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The install folder: measured, estimated, unknown
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnInstallFolderThatIsAPlainFullLocalFolderIsMeasuredWithTheWalkerAndNotEstimated()
    {
        using var rig = new Rig("sba-measured");
        var folder = rig.Install("alpha");
        var expected = await Measured(folder);

        var rows = await Read([App("Alpha App", "key-alpha", folder, publisher: "Alpha Inc")]);

        var row = rows.Single();
        Assert.AreEqual("Alpha App", row.Name);
        Assert.AreEqual("Alpha Inc", row.Publisher);
        Assert.AreEqual(folder, row.InstallFolder);
        Assert.AreEqual(expected, row.InstallBytes);
        Assert.IsFalse(row.InstallEstimated);
        Assert.AreEqual(expected, row.TotalBytes);
        Assert.AreEqual(0, row.CatalogRows.Count);
    }

    [TestMethod]
    public async Task AMeasuredFolderBeatsTheEntrysEstimate()
    {
        using var rig = new Rig("sba-measured-wins");
        var folder = rig.Install("alpha");
        var expected = await Measured(folder);

        var row = (await Read([App("Alpha App", "key-alpha", folder, estimate: 987654321)])).Single();

        Assert.AreEqual(expected, row.InstallBytes);
        Assert.IsFalse(row.InstallEstimated);
    }

    [TestMethod]
    public async Task AnAppWhoseFolderCannotBeUsedFallsBackToTheEstimateMarkedEstimated()
    {
        using var rig = new Rig("sba-estimated");
        var real = rig.Install("real");
        var file = rig.Temp.File(@"apps\a-file.bin", 5000);
        var junction = Path.Combine(rig.Temp.Folder("apps"), "linked");
        var target = rig.Install("junction-target");
        Native.CreateJunction(junction, target);
        var cases = new (string Name, string? Location)[]
        {
            ("No Location", null),
            ("Empty Location", ""),
            ("Relative Location", @"apps\real"),
            ("Drive Relative", "C:apps"),
            ("Extended Prefix", @"\\?\" + real),
            ("Missing Folder", Path.Combine(rig.Temp.Path, "apps", "not-there")),
            ("A File", file),
            ("A Junction", junction),
        };
        var apps = cases.Select((c, i) => App(c.Name, "key-" + i, c.Location, estimate: 1_000_000 + i)).ToList();

        var rows = await Read(apps);

        Assert.AreEqual(cases.Length, rows.Count);
        for (var i = 0; i < cases.Length; i++)
        {
            var row = Of(rows, cases[i].Name);
            Assert.AreEqual(1_000_000L + i, row.InstallBytes, cases[i].Name + ": the estimate");
            Assert.IsTrue(row.InstallEstimated, cases[i].Name + ": marked estimated");
            Assert.AreEqual(1_000_000L + i, row.TotalBytes, cases[i].Name);
        }
    }

    [TestMethod]
    public async Task AnAppWithNoFolderAndNoEstimateHasNullInstallBytesAndIsKeptForItsCatalogRows()
    {
        var row = (await Read([App("Echo App", "key-echo", null)], Row("electron-updater", 30000, key: "key-echo", title: "Echo App"))).Single();

        Assert.IsNull(row.InstallBytes);
        Assert.IsFalse(row.InstallEstimated);
        Assert.AreEqual(30000L, row.TotalBytes, "the total counts a null install as 0 and adds the catalog rows");
        Assert.AreEqual(1, row.CatalogRows.Count);
    }

    [TestMethod]
    public async Task AStoreAppIsNeverMeasured()
    {
        using var rig = new Rig("sba-store");
        var folder = rig.Install("store-package");
        var package = new StorePackage("Contoso.App_1.0_x64__abc", "Contoso App", "Contoso", folder);
        var store = new InstalledApp("Contoso App", "Contoso", null, folder, AppClass.StoreApp, null, package);

        var rows = await Read([store]);

        // Whether a Store app with nothing to add is listed is the builder's reading of "zero rows dropped"; it is never measured.
        foreach (var row in rows)
        {
            Assert.IsNull(row.InstallBytes, "Lean does not measure package folders");
            Assert.AreEqual(0L, row.TotalBytes);
        }
    }

    [TestMethod]
    public async Task AnAppMeasuredAsZeroWithNoEstimateAndNoCatalogRowsIsNotListed()
    {
        using var rig = new Rig("sba-zero");
        var empty = rig.Temp.Folder("apps", "empty");
        var onlyFolders = rig.Temp.Folder("apps", "only-folders", "a", "b");
        var full = rig.Install("full");

        var rows = await Read([App("Empty App", "key-empty", empty), App("Folders App", "key-folders", onlyFolders), App("Full App", "key-full", full)]);

        Assert.AreEqual("Full App", rows.Single().Name);
    }

    [TestMethod]
    public async Task AnAppMeasuredAsZeroIsKeptWhenCatalogRowsBelongToIt()
    {
        using var rig = new Rig("sba-zero-rows");
        var empty = rig.Temp.Folder("apps", "empty");

        var row = (await Read([App("Google Chrome", "Google Chrome", empty)], Row("chrome", 12345))).Single();

        Assert.AreEqual(12345L, row.TotalBytes);
        Assert.AreEqual(1, row.CatalogRows.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Catalog rows: exact matching, never by name
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task CatalogRowsAttachOnlyToTheAppWhoseUninstallKeyIsExactlyTheirs()
    {
        var apps = new List<InstalledApp>
        {
            App("Google Chrome", "Google Chrome", null, estimate: 1000),
            App("Google Chrome Beta", "Google Chrome Beta", null, estimate: 1000),
            App("Microsoft Edge", "Microsoft Edge", null, estimate: 1000),
            App("CapCut", "CapCut", null, estimate: 1000),
            App("Echo App", "key-echo", null, estimate: 1000),
            App("Echo App Twin", "key-twin", null, estimate: 1000),
            App("Look Alike", "{GUID-1}", null, estimate: 1000),
        };
        var rows = new[]
        {
            Row("chrome", 11_000),
            Row("edge", 22_000),
            Row("capcut", 33_000),
            Row("electron-updater", 44_000, key: "key-echo", title: "Echo App"),
            Row("electron-updater", 55_000, key: "key-twin", title: "Echo App"),
            Row("electron-updater", 66_000, key: "Google Chrome Beta", title: "Google Chrome"),
            Row("electron-updater", 77_000, key: null, title: "Look Alike"),
            Row("electron-updater", 88_000, key: "key-not-installed", title: "Google Chrome"),
        };

        var result = await Read(apps, rows);

        Assert.AreEqual("chrome:11000", Describe(Of(result, "Google Chrome").CatalogRows), "the chrome row, not the Beta's updater row");
        Assert.AreEqual("electron-updater:66000", Describe(Of(result, "Google Chrome Beta").CatalogRows), "an updater row goes to its own key even when the title is another app's");
        Assert.AreEqual("edge:22000", Describe(Of(result, "Microsoft Edge").CatalogRows));
        Assert.AreEqual("capcut:33000", Describe(Of(result, "CapCut").CatalogRows));
        Assert.AreEqual("electron-updater:44000", Describe(Of(result, "Echo App").CatalogRows));
        Assert.AreEqual("electron-updater:55000", Describe(Of(result, "Echo App Twin").CatalogRows), "two rows with the same title go to their own apps");
        Assert.AreEqual(string.Empty, Describe(Of(result, "Look Alike").CatalogRows), "a row without a key and a row whose key is not installed attach to nobody");
        Assert.AreEqual(1000L + 11_000, Of(result, "Google Chrome").TotalBytes);
        Assert.AreEqual(1000L + 66_000, Of(result, "Google Chrome Beta").TotalBytes);
    }

    [TestMethod]
    public async Task ANameThatLooksLikeAnAppDoesNotAttachARowOnlyItsKeyDoes()
    {
        // The app is called "Google Chrome" but its key is not; the app whose key is exactly "Google Chrome" has another name.
        var apps = new List<InstalledApp>
        {
            App("Google Chrome", "{ABC-1}", null, estimate: 1000),
            App("Browser Of Mine", "Google Chrome", null, estimate: 2000),
        };

        var result = await Read(apps, Row("chrome", 9000));

        Assert.AreEqual(string.Empty, Describe(Of(result, "Google Chrome").CatalogRows));
        Assert.AreEqual("chrome:9000", Describe(Of(result, "Browser Of Mine").CatalogRows));
        Assert.AreEqual(11_000L, Of(result, "Browser Of Mine").TotalBytes);
    }

    [TestMethod]
    public async Task APrefixOrSuperstringOfTheKeyIsNotTheKey()
    {
        var apps = new List<InstalledApp> { App("Google Chrome Canary", "Google Chrome Canary", null, estimate: 1000), App("Chrome", "Google", null, estimate: 1000) };

        var result = await Read(apps, Row("chrome", 9000), Row("electron-updater", 4000, key: "Google Chrome"));

        Assert.IsTrue(result.All(r => r.CatalogRows.Count == 0), "neither a longer nor a shorter key attaches: " + string.Join(";", result.Select(r => r.Name + "=" + Describe(r.CatalogRows))));
    }

    [TestMethod]
    public async Task ARowWhoseAppIsNotInstalledIsLeftOutWithoutAnError()
    {
        var result = await Read([App("Other", "key-other", null, estimate: 500)], Row("chrome", 9000), Row("capcut", 8000));

        Assert.AreEqual(500L, result.Single().TotalBytes);
        Assert.AreEqual(0, result.Single().CatalogRows.Count);
    }

    [TestMethod]
    public async Task ARowIsCountedOnceWhenTwoEntriesShareItsKey()
    {
        // The same product can be registered in both registry views, with the same key.
        var apps = new List<InstalledApp> { App("Google Chrome", "Google Chrome", null, estimate: 1000), App("Google Chrome", "Google Chrome", null, estimate: 1000) };

        var result = await Read(apps, Row("chrome", 9000));

        Assert.AreEqual(9000L, result.Sum(r => r.CatalogRows.Sum(g => g.Bytes)), "the 9000 bytes of caches are one set of bytes");
    }

    [TestMethod]
    public async Task AStoreAppNeverGetsCatalogRows()
    {
        var package = new StorePackage("Google.Chrome_1.0_x64__abc", "Google Chrome", "Google", @"C:\Program Files\WindowsApps\x");
        var store = new InstalledApp("Google Chrome", "Google", null, null, AppClass.StoreApp, null, package);

        var result = await Read([store], Row("chrome", 9000));

        Assert.IsTrue(result.All(r => r.CatalogRows.Count == 0));
    }

    [TestMethod]
    public async Task TheRealGuideRowsOfBrowsersAndUpdatersFindTheirAppsByKey()
    {
        using var rig = new Rig("sba-real");
        var userData = Path.Combine(rig.Local, "Google", "Chrome", "User Data");
        Directory.CreateDirectory(Path.Combine(userData, "Default", "GPUCache"));
        File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object> { ["Default"] = new { name = "p" } } } }));
        TempFolder.WriteBytes(Path.Combine(userData, "Default", "GPUCache", "data_0"), 50000, 3);
        var chromeInstall = rig.Install("chrome", 90000);
        var echoInstall = rig.Install("echo", 60000);
        Directory.CreateDirectory(Path.Combine(echoInstall, "resources"));
        File.WriteAllText(Path.Combine(echoInstall, "resources", "app-update.yml"), "updaterCacheDirName: echo-updater\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(rig.Local, "echo-updater", "pending"));
        TempFolder.WriteBytes(Path.Combine(rig.Local, "echo-updater", "pending", "update.exe"), 30000, 3);
        var source = new FakeUninstallSource();
        source.Entries.Add(new UninstallEntry("Google Chrome", UninstallScope.PerMachine, RegistryViewKind.Bits64, "Google Chrome", "Google LLC", null, chromeInstall, false));
        source.Entries.Add(new UninstallEntry("{ECHO-KEY}", UninstallScope.PerUser, RegistryViewKind.Bits64, "Echo App", null, null, echoInstall, false));
        var guide = await GuideRows.ReadAsync(rig.Context, source, CancellationToken.None);
        Assert.AreEqual(2, guide.Count, "the fixture gives a chrome row and an updater row");
        var chromeBytes = guide.Single(g => g.Entry.Id == "chrome").Bytes;
        var echoBytes = guide.Single(g => g.Entry.Id == "electron-updater").Bytes;
        var chromeFolder = await Measured(chromeInstall);
        var echoFolder = await Measured(echoInstall);

        var result = await SpaceByApp.ReadAsync(UninstallInventory.Read(source), guide, CancellationToken.None);

        Assert.AreEqual(chromeFolder + chromeBytes, Of(result, "Google Chrome").TotalBytes);
        Assert.AreEqual(echoFolder + echoBytes, Of(result, "Echo App").TotalBytes);
        Assert.AreEqual("electron-updater", Of(result, "Echo App").CatalogRows.Single().Entry.Id);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Totals and order
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheTotalIsTheInstallBytesPlusItsCatalogRowsAndNullCountsAsZero()
    {
        using var rig = new Rig("sba-totals");
        var folder = rig.Install("alpha");
        var measured = await Measured(folder);

        var result = await Read(
            [App("Google Chrome", "Google Chrome", folder), App("Microsoft Edge", "Microsoft Edge", null, estimate: 4000), App("CapCut", "CapCut", null)],
            Row("chrome", 7000), Row("edge", 2000), Row("capcut", 600));

        Assert.AreEqual(measured + 7000, Of(result, "Google Chrome").TotalBytes);
        Assert.AreEqual(4000L + 2000, Of(result, "Microsoft Edge").TotalBytes);
        Assert.AreEqual(600L, Of(result, "CapCut").TotalBytes);
        Assert.IsNull(Of(result, "CapCut").InstallBytes);
    }

    [TestMethod]
    public async Task RowsAreSortedByTotalLargestFirstThenByName()
    {
        using var rig = new Rig("sba-order");
        var big = rig.Install("big", 4_500_000);
        var bigBytes = await Measured(big);
        Assert.IsGreaterThan(4_000_000L, bigBytes, "the fixture is the largest total below");
        var apps = new List<InstalledApp>
        {
            App("Zulu", "key-zulu", null, estimate: 500),
            App("Small", "key-small", null, estimate: 2_000_000),
            App("Bravo", "key-bravo", null, estimate: 500),
            App("Mid", "Google Chrome", null, estimate: 1_000_000),
            App("Top", "key-top", big),
            App("Alpha", "key-alpha", null, estimate: 500),
        };

        var result = await Read(apps, Row("chrome", 3_000_000));

        CollectionAssert.AreEqual(OrderedNames, result.Select(r => r.Name).ToArray());
        Assert.AreEqual(4_000_000L, Of(result, "Mid").TotalBytes);
        for (var i = 1; i < result.Count; i++)
        {
            Assert.IsLessThanOrEqualTo(result[i - 1].TotalBytes, result[i].TotalBytes, "totals fall down the list");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The same folder is never counted twice
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, "same text")]
    [DataRow(true, "same text")]
    [DataRow(false, "case and trailing separator")]
    [DataRow(true, "case and trailing separator")]
    public async Task TwoEntriesWithTheSameFolderCountItOnceForTheFirstInListOrder(bool reversed, string variant)
    {
        using var rig = new Rig("sba-shared");
        var folder = rig.Install("shared");
        var expected = await Measured(folder);
        var other = variant == "same text" ? folder : folder.ToUpperInvariant() + Path.DirectorySeparatorChar;
        var apps = new List<InstalledApp> { App("First", "key-1", folder), App("Second", "key-2", other) };
        if (reversed)
        {
            apps.Reverse();
        }

        var result = await Read(apps);

        Assert.AreEqual(expected, result.Sum(r => r.TotalBytes), "the folder counts once in all");
        var first = apps[0].DisplayName;
        Assert.AreEqual(expected, Of(result, first).InstallBytes, "the first in list order owns it");
        Assert.IsFalse(Of(result, first).InstallEstimated);
    }

    [TestMethod]
    public async Task AFolderInsideAnotherAppsFolderIsNotCountedTwiceWhenTheOuterComesFirst()
    {
        using var rig = new Rig("sba-nested-outer");
        var outer = rig.Install("outer");
        var inner = Path.Combine(outer, "plugins", "inner");
        Directory.CreateDirectory(inner);
        TempFolder.WriteBytes(Path.Combine(inner, "inner.dll"), 60000, 5);
        var outerBytes = await Measured(outer);

        var result = await Read([App("Outer", "key-outer", outer), App("Inner", "key-inner", inner)]);

        Assert.AreEqual(outerBytes, Of(result, "Outer").InstallBytes, "the outer folder is measured whole for the first");
        Assert.AreEqual(outerBytes, result.Sum(r => r.TotalBytes), "nothing is counted twice: the inner bytes are already in the outer's");
    }

    [TestMethod]
    public async Task AFolderInsideAnotherAppsFolderIsNotCountedTwiceWhenTheInnerComesFirst()
    {
        using var rig = new Rig("sba-nested-inner");
        var outer = rig.Install("outer");
        var inner = Path.Combine(outer, "plugins", "inner");
        Directory.CreateDirectory(inner);
        TempFolder.WriteBytes(Path.Combine(inner, "inner.dll"), 60000, 5);
        var outerBytes = await Measured(outer);
        var innerBytes = await Measured(inner);

        var result = await Read([App("Inner", "key-inner", inner), App("Outer", "key-outer", outer)]);

        Assert.AreEqual(innerBytes, Of(result, "Inner").InstallBytes, "the first in list order keeps its folder");
        Assert.IsLessThanOrEqualTo(outerBytes, result.Sum(r => r.TotalBytes), "no byte is counted twice");
    }

    [TestMethod]
    public async Task SiblingFoldersWithACommonNamePrefixAreNotNested()
    {
        using var rig = new Rig("sba-prefix");
        var foo = rig.Install("foo", 50000);
        var fooBar = rig.Install("foo-bar", 80000);
        var fooBytes = await Measured(foo);
        var fooBarBytes = await Measured(fooBar);

        var result = await Read([App("Foo", "key-foo", foo), App("Foo Bar", "key-foobar", fooBar)]);

        Assert.AreEqual(fooBytes, Of(result, "Foo").InstallBytes);
        Assert.AreEqual(fooBarBytes, Of(result, "Foo Bar").InstallBytes, "foo-bar is not inside foo");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Cancel
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ACancelledTokenIsAThrownCancel()
    {
        using var rig = new Rig("sba-cancel");
        var folder = rig.Install("alpha");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        try
        {
            await SpaceByApp.ReadAsync([App("Alpha", "key-alpha", folder)], [], source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("expected OperationCanceledException, got a result instead");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Phase C1 review round 1 (plan items 3, 4 and 6): a shared or shallow InstallLocation is never measured; a quoted one is
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The shared folders of plan item 3, built from the environment (never typed as drive letters). A folder that does
    /// not exist on this machine is left out.</summary>
    private static string? SharedFolder(string key)
    {
        string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var path = key switch
        {
            "ProgramFiles" => Special(Environment.SpecialFolder.ProgramFiles),
            "ProgramFilesX86" => Special(Environment.SpecialFolder.ProgramFilesX86),
            "ProgramData" => Special(Environment.SpecialFolder.CommonApplicationData),
            "Windows" => Special(Environment.SpecialFolder.Windows),
            "Profile" => Special(Environment.SpecialFolder.UserProfile),
            "Users" => Path.GetDirectoryName(Special(Environment.SpecialFolder.UserProfile)),
            "AppData" => Path.Combine(Special(Environment.SpecialFolder.UserProfile), "AppData"),
            "Roaming" => Special(Environment.SpecialFolder.ApplicationData),
            "LocalAppData" => Special(Environment.SpecialFolder.LocalApplicationData),
            "LocalPrograms" => Path.Combine(Special(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            _ => throw new ArgumentException(key),
        };
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>Reads one app at <paramref name="location"/> with the estimate <paramref name="estimate"/> and asserts the
    /// estimate is the answer: the folder was not measured (a measure of a real system folder would take far longer than the
    /// 30 s bound and give another number).</summary>
    private static async Task AssertFallsBackToTheEstimate(string location, long estimate, string what)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IReadOnlyList<AppSpaceRow> rows;
        try
        {
            rows = await SpaceByApp.ReadAsync([App("Shared App", "key-shared", location, estimate: estimate)], [], limit.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(what + ": the folder " + location + " was being measured (no answer in 30 s); expected the entry's estimate");
            return;
        }

        var row = rows.Single();
        Assert.AreEqual(estimate, row.InstallBytes, what + ": " + location + " is not measured; the estimate is used");
        Assert.IsTrue(row.InstallEstimated, what + ": " + location + " is marked estimated");
        Assert.AreEqual(estimate, row.TotalBytes, what);
    }

    [TestMethod]
    [DataRow("ProgramFiles")]
    [DataRow("ProgramFilesX86")]
    [DataRow("ProgramData")]
    [DataRow("Windows")]
    [DataRow("Users")]
    [DataRow("Profile")]
    [DataRow("AppData")]
    [DataRow("Roaming")]
    [DataRow("LocalAppData")]
    [DataRow("LocalPrograms")]
    public async Task AnInstallLocationThatIsASharedFolderIsNotMeasuredAndTheEstimateIsUsed(string key)
    {
        if (SharedFolder(key) is not { } folder || !Directory.Exists(folder))
        {
            Assert.Inconclusive(key + " does not exist on this machine");
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await AssertFallsBackToTheEstimate(folder, 4_200_001, key);
        await AssertFallsBackToTheEstimate(folder + @"\", 4_200_002, key + " with a trailing separator");
        await AssertFallsBackToTheEstimate(folder.ToLowerInvariant(), 4_200_003, key + " in lower case");
        await AssertFallsBackToTheEstimate("\"" + folder + "\"", 4_200_004, key + " in quotes");
        Assert.IsLessThan(25_000, watch.ElapsedMilliseconds, key + ": the answer came without a walk of the folder");
    }

    [TestMethod]
    public async Task AnInstallLocationThatIsAVolumeRootIsNotMeasuredAndTheEstimateIsUsed()
    {
        var roots = new[] { @"C:\", Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\" }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var root in roots)
        {
            await AssertFallsBackToTheEstimate(root, 5_300_001, "volume root " + root);
            await AssertFallsBackToTheEstimate(root.TrimEnd('\\'), 5_300_002, "volume root without its separator " + root);
        }

        Assert.IsLessThan(25_000, watch.ElapsedMilliseconds, "the answer came without a walk of a volume");
    }

    [TestMethod]
    public async Task AnInstallLocationOneComponentBelowARootIsNotMeasuredButTwoBelowIs()
    {
        // A stand-in folder this test makes itself and removes: one component below the temp volume's root. Under it, a folder
        // two components below the root holds the same kind of content and is measured.
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var shallow = Path.Combine(root, "asp20-shallow-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(shallow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("this user cannot make a folder in " + root + ": " + exception.Message);
            return;
        }

        try
        {
            TempFolder.WriteBytes(Path.Combine(shallow, "big.bin"), 900_000, 3);
            var deep = Path.Combine(shallow, "app");
            Directory.CreateDirectory(deep);
            TempFolder.WriteBytes(Path.Combine(deep, "app.exe"), 70_000, 3);
            var expected = await Measured(deep);

            var rows = await Read([App("One Below", "key-one", shallow, estimate: 1_111_111), App("Two Below", "key-two", deep, estimate: 2_222_222)]);

            var one = Of(rows, "One Below");
            Assert.AreEqual(1_111_111L, one.InstallBytes, "one component below a root: the estimate");
            Assert.IsTrue(one.InstallEstimated);
            var two = Of(rows, "Two Below");
            Assert.AreEqual(expected, two.InstallBytes, "two components below a root: measured");
            Assert.IsFalse(two.InstallEstimated);
        }
        finally
        {
            try
            {
                Directory.Delete(shallow, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // best effort: a folder with a guid name made by this test
            }
        }
    }

    [TestMethod]
    public async Task AQuotedInstallLocationIsMeasuredLikeTheUnquotedOne()
    {
        using var rig = new Rig("sba-quoted");
        var folder = rig.Install("quoted app");
        var expected = await Measured(folder);

        var rows = await Read([App("Quoted App", "key-quoted", "\"" + folder + "\"", estimate: 987_654_321)]);

        var row = rows.Single();
        Assert.AreEqual(expected, row.InstallBytes, "the quotes are trimmed, then the folder is measured");
        Assert.IsFalse(row.InstallEstimated);
        Assert.AreEqual(expected, row.TotalBytes);
        Assert.AreEqual(folder, row.InstallFolder, "the folder shown has no quotes");
    }

    [TestMethod]
    public async Task AQuotedInstallLocationIsTheSameFolderAsTheUnquotedSpelling()
    {
        using var rig = new Rig("sba-quoted-once");
        var folder = rig.Install("one folder");
        var expected = await Measured(folder);

        var rows = await Read([App("First", "key-first", folder), App("Second", "key-second", "\"" + folder + "\"", estimate: 50)]);

        Assert.AreEqual(expected, Of(rows, "First").InstallBytes);
        Assert.IsFalse(rows.Any(r => r.Name == "Second"), "the quoted spelling is the same folder: counted for the first app only, so the second has nothing");
    }
}
