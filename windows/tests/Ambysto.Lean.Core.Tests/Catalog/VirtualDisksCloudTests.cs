using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase C1 (plan "C1 Core": a cloud placeholder is never opened, A2's read rule through the same helper): a
/// <c>VirtualBox.xml</c> or a <c>.vbox</c> that is an online-only placeholder is not opened (no row for it, and the provider's
/// fetch log shows no read). Same oracle and rules as <see cref="GuideRowsCloudTests"/>: real placeholders from AmbyCloud,
/// a positive control proves the log can fail, Console session only, Inconclusive where AmbyCloud is not installed.</summary>
[TestClass]
[DoNotParallelize]
public sealed class VirtualDisksCloudTests
{
    private const string Control = "control.bin";

    private sealed class NoDistributions : IVirtualDiskSource
    {
        public System.Collections.Generic.IReadOnlyList<(string Name, string BasePath)> WslDistributions() => [];
    }

    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRoots();
        }
    }

    private static PathContext ContextOf(TempFolder temp, out string profile)
    {
        profile = temp.Folder("Vol", "Users", "me");
        var local = temp.Folder("Vol", "Users", "me", "AppData", "Local");
        var roaming = temp.Folder("Vol", "Users", "me", "AppData", "Roaming");
        return new PathContext(profile, roaming, local, temp.Folder("Vol"), temp.Folder("Vol", "Users"), [temp.Folder("Vol", "Windows")], []);
    }

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

    private static string Machine(TempFolder temp, string name, long diskLength)
    {
        var folder = temp.Folder("VirtualBox VMs", name);
        Directory.CreateDirectory(Path.Combine(folder, "disk"));
        TempFolder.WriteBytes(Path.Combine(folder, "disk", name + ".vdi"), diskLength, 3);
        var vbox = Path.Combine(folder, name + ".vbox");
        File.WriteAllText(vbox, "<VirtualBox xmlns=\"http://www.virtualbox.org/\"><Machine name=\"" + name + "\"><MediaRegistry><HardDisks><HardDisk location=\"disk\\" + name + ".vdi\"/></HardDisks></MediaRegistry></Machine></VirtualBox>");
        return vbox;
    }

    [TestMethod]
    [TestCategory("Console")]
    public async Task AVirtualBoxXmlThatIsACloudPlaceholderIsNotOpenedAndGivesNoRow()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-vbox-xml");
        var context = ContextOf(temp, out var profile);
        var vbox = Machine(temp, "vm1", 900000);
        var folder = Path.Combine(profile, ".VirtualBox");
        using var cloud = CloudRoot.Register(folder, temp.Sub("fetch.log"));
        cloud.Populate(("VirtualBox.xml", 2048), (Control, 4096));
        cloud.Serve();
        cloud.RequireOnlineOnly("VirtualBox.xml");
        ProveTheOracleCanFail(cloud);
        Assert.IsTrue(File.Exists(vbox));

        var rows = await VirtualDisks.ReadAsync(context, new NoDistributions(), CancellationToken.None);

        Assert.AreEqual(0, rows.Count, "an online-only VirtualBox.xml is not opened: no row");
        cloud.AssertNoFetch("the virtual disks read");
        Assert.AreEqual(0L, cloud.Status("VirtualBox.xml").OnDisk, "the read fetched VirtualBox.xml");
    }

    [TestMethod]
    [TestCategory("Console")]
    public async Task AVboxThatIsACloudPlaceholderIsNotOpenedAndGivesNoRowForThatMachineOnly()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-vbox-vbox");
        var context = ContextOf(temp, out var profile);
        var plain = Machine(temp, "plain", 700000);
        var cloudFolder = temp.Sub("VirtualBox VMs", "cloud");
        using var cloud = CloudRoot.Register(cloudFolder, temp.Sub("fetch.log"));
        cloud.Populate(("cloud.vbox", 1200), (Control, 4096));
        cloud.Serve();
        cloud.RequireOnlineOnly("cloud.vbox");
        ProveTheOracleCanFail(cloud);
        var xml = Path.Combine(profile, ".VirtualBox", "VirtualBox.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xml)!);
        File.WriteAllText(xml,
            "<VirtualBox xmlns=\"http://www.virtualbox.org/\"><Global><MachineRegistry>"
            + "<MachineEntry uuid=\"{00000000-0000-0000-0000-000000000001}\" src=\"" + cloud.Combine("cloud.vbox") + "\"/>"
            + "<MachineEntry uuid=\"{00000000-0000-0000-0000-000000000002}\" src=\"" + plain + "\"/>"
            + "</MachineRegistry></Global></VirtualBox>");

        var rows = await VirtualDisks.ReadAsync(context, new NoDistributions(), CancellationToken.None);

        Assert.AreEqual("plain", rows.Single().Name, "the machine whose .vbox is a placeholder has no row; the other is kept");
        cloud.AssertNoFetch("the virtual disks read");
        Assert.AreEqual(0L, cloud.Status("cloud.vbox").OnDisk, "the read fetched cloud.vbox");
    }
}
