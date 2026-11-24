using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A2, review round 1, item 3 (docs/spec/01 rule 6): a <c>Local State</c> or <c>app-update.yml</c> that
/// is an online-only cloud placeholder is not opened: no row, and the provider's fetch log shows no read. Real placeholders
/// from the test machine's fake provider (AmbyCloud, the test-machine notes). The fetch log is the oracle: AmbyCloud's placeholder
/// content is not JSON or YAML, so "no row" alone would also hold after a fetch. Shown able to fail by a positive control
/// (this process reads one byte of a control placeholder; the log must record it with this pid). Console session only (a
/// read in session 0 is not reliably attributed), so these tests are in the Console suite and Inconclusive where AmbyCloud
/// is not installed. The sync roots are unregistered on dispose; stale ones are swept first (fixtures named <c>cloud-*</c>
/// with a folder <c>cloud</c>; the root of the Local State test is the <c>User Data</c> folder itself, so that fixture also
/// sits in a <c>cloud-guide-*</c> folder and a crashed run leaves its root for the sweep of the next).</summary>
[TestClass]
[DoNotParallelize]
public sealed class GuideRowsCloudTests
{
    private const string Control = "control.bin";
    private const string UpdaterKey = "updaterCacheDirName";

    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRoots();
        }
    }

    private static PathContext ContextOf(TempFolder temp, out string local)
    {
        local = temp.Folder("Vol", "Users", "me", "AppData", "Local");
        var roaming = temp.Folder("Vol", "Users", "me", "AppData", "Roaming");
        return new PathContext(temp.Folder("Vol", "Users", "me"), roaming, local, temp.Folder("Vol"), temp.Folder("Vol", "Users"),
            [temp.Folder("Vol", "Windows")], []);
    }

    private static void Put(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TempFolder.WriteBytes(path, length, 3);
    }

    /// <summary>The positive control: this process reads one byte of <see cref="Control"/> and the log must show it with this pid.</summary>
    private static void ProveTheOracleCanFail(CloudRoot cloud)
    {
        cloud.RequireOnlineOnly(Control);
        using (var stream = new FileStream(cloud.Combine(Control), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.IsGreaterThanOrEqualTo(0, stream.ReadByte(), "the control read returned nothing");
        }

        var pid = Environment.ProcessId;
        var mine = cloud.FetchesOf(Control).Where(line => line.Contains("\"pid\":" + pid + ",", StringComparison.Ordinal) || line.Contains("\"pid\":" + pid + "}", StringComparison.Ordinal)).ToList();
        Assert.IsGreaterThan(0, mine.Count, $"the fetch log did not record this process's (pid {pid}) read of {Control}; all fetches: [{string.Join(" | ", cloud.Fetches())}]");
        cloud.MarkFetchBaseline();
    }

    [TestMethod]
    [TestCategory("Console")]
    public async Task ALocalStateThatIsACloudPlaceholderIsNotOpenedAndGivesNoRow()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-guide-localstate");
        var context = ContextOf(temp, out var local);
        var userData = Path.Combine(local, "Google", "Chrome", "User Data");
        Directory.CreateDirectory(Path.GetDirectoryName(userData)!);
        using var cloud = CloudRoot.Register(userData, temp.Sub("fetch.log"));
        cloud.Populate(("Local State", 2048), (Control, 4096));
        cloud.Serve();
        cloud.RequireOnlineOnly("Local State");
        Put(Path.Combine(userData, "Default", "Cache", "Cache_Data", "data_0"), 52000);
        ProveTheOracleCanFail(cloud);

        var rows = await GuideRows.ReadAsync(context, new FakeUninstallSource(), CancellationToken.None);

        Assert.AreEqual(0, rows.Count(r => r.Entry.Id == "chrome"), "an online-only Local State is not opened: no row");
        cloud.AssertNoFetch("the guide read");
        Assert.AreEqual(0L, cloud.Status("Local State").OnDisk, "the read fetched Local State");
    }

    [TestMethod]
    [TestCategory("Console")]
    public async Task AnAppUpdateYmlThatIsACloudPlaceholderIsNotOpenedAndGivesNoRowForThatAppOnly()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-guide-yml");
        var context = ContextOf(temp, out var local);
        var install = temp.Sub("apps", "cloud");
        using var cloud = CloudRoot.Register(install, temp.Sub("fetch.log"));
        cloud.Populate((@"resources\", 0), (@"resources\app-update.yml", 120), (Control, 4096));
        cloud.Serve();
        cloud.RequireOnlineOnly(@"resources\app-update.yml");
        ProveTheOracleCanFail(cloud);
        Put(Path.Combine(local, "cloud-updater", "pending", "update.exe"), 28000);

        var plainInstall = temp.Folder("apps", "plain");
        var yml = Path.Combine(plainInstall, "resources", "app-update.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(yml)!);
        File.WriteAllText(yml, UpdaterKey + ": plain-updater\n");
        Put(Path.Combine(local, "plain-updater", "pending", "update.exe"), 26000);

        var source = new FakeUninstallSource();
        source.Entries.Add(new UninstallEntry("key-cloud", UninstallScope.PerUser, RegistryViewKind.Bits64, "Cloud App", null, null, install, false));
        source.Entries.Add(new UninstallEntry("key-plain", UninstallScope.PerUser, RegistryViewKind.Bits64, "Plain App", null, null, plainInstall, false));

        var rows = await GuideRows.ReadAsync(context, source, CancellationToken.None);

        Assert.AreEqual("Plain App", rows.Single().Title, "the app whose app-update.yml is a placeholder has no row; the other is kept");
        cloud.AssertNoFetch("the guide read");
        Assert.AreEqual(0L, cloud.Status(@"resources\app-update.yml").OnDisk, "the read fetched app-update.yml");
    }
}
