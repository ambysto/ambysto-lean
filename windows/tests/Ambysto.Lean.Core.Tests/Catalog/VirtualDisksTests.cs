using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase C1 (plan "Phase C", "C1 Core" and "C1 tests"): <see cref="VirtualDisks.ReadAsync"/> with a fake
/// <see cref="IVirtualDiskSource"/>, a fake <see cref="PathContext"/> (the profile holds <c>.VirtualBox</c>, LocalAppData holds
/// <c>Docker</c>, so no test touches the real profile) and temp files. Every disk is the only file in its own folder, so its
/// expected size is that folder measured with Lean's own walker (allocated bytes), never a hard-coded cluster size. The advice
/// wording is the builder's (from the vendors' documentation); here it must be non-empty, and a WSL advice must never unregister.
/// Links are directory junctions made with <c>Native.CreateJunction</c>. Session 0; written by the test writer before the
/// code, against a stub.</summary>
[TestClass]
public sealed class VirtualDisksTests
{
    private const int FourMiB = 4 * 1024 * 1024;

    private sealed class FakeDiskSource : IVirtualDiskSource
    {
        public List<(string Name, string BasePath)> Distributions { get; } = [];

        public int Reads { get; private set; }

        public IReadOnlyList<(string Name, string BasePath)> WslDistributions()
        {
            Reads++;
            return Distributions;
        }
    }

    private static async Task<long> Measured(string folder)
    {
        Assert.IsTrue(Directory.Exists(folder), "expected folder exists: " + folder);
        var bytes = (await new Scanner().ScanAsync(folder, null, CancellationToken.None)).Root.AllocatedBytes;
        Assert.IsGreaterThan(0, bytes, "the fixture folder holds something: " + folder);
        return bytes;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(string name)
        {
            Temp = new TempFolder(name);
            Profile = Temp.Folder("Vol", "Users", "me");
            Local = Temp.Folder("Vol", "Users", "me", "AppData", "Local");
            var roaming = Temp.Folder("Vol", "Users", "me", "AppData", "Roaming");
            Context = new PathContext(Profile, roaming, Local, Temp.Folder("Vol"), Temp.Folder("Vol", "Users"), [Temp.Folder("Vol", "Windows")], []);
        }

        public TempFolder Temp { get; }

        public string Profile { get; }

        public string Local { get; }

        public PathContext Context { get; }

        public FakeDiskSource Source { get; } = new();

        public Task<IReadOnlyList<VirtualDiskRow>> Read() => VirtualDisks.ReadAsync(Context, Source, CancellationToken.None);

        /// <summary>A file of <paramref name="length"/> bytes at <paramref name="path"/> (folders made); returns the path.</summary>
        public static string Put(string path, long length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TempFolder.WriteBytes(path, length, 3);
            return path;
        }

        /// <summary>A WSL distribution registered with the source; <paramref name="length"/> null leaves its ext4.vhdx out.</summary>
        public string Wsl(string name, long? length)
        {
            var folder = Temp.Folder("wsl", name);
            if (length is { } bytes)
            {
                Put(Path.Combine(folder, "ext4.vhdx"), bytes);
            }

            Source.Distributions.Add((name, folder));
            return folder;
        }

        public string DockerDisk(long length) => Put(Path.Combine(Local, "Docker", "wsl", "disk", "docker_data.vhdx"), length);

        public string DockerData(long length) => Put(Path.Combine(Local, "Docker", "wsl", "data", "ext4.vhdx"), length);

        public string VirtualBoxXml => Path.Combine(Profile, ".VirtualBox", "VirtualBox.xml");

        /// <summary>Writes %USERPROFILE%\.VirtualBox\VirtualBox.xml listing the given machine files.</summary>
        public void Registry(params string[] vboxFiles) => WriteRegistry(MachineEntries(vboxFiles));

        public void WriteRegistry(string machineEntries, string prolog = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VirtualBoxXml)!);
            File.WriteAllText(VirtualBoxXml,
                "<?xml version=\"1.0\"?>\n" + prolog + "<VirtualBox xmlns=\"http://www.virtualbox.org/\" version=\"1.12-windows\">\n  <Global>\n    <MachineRegistry>\n"
                + machineEntries + "    </MachineRegistry>\n  </Global>\n</VirtualBox>\n", new UTF8Encoding(false));
        }

        public static string MachineEntries(params string[] vboxFiles) =>
            string.Concat(vboxFiles.Select(f => "      <MachineEntry uuid=\"{" + Guid.NewGuid() + "}\" src=\"" + SecurityElement.Escape(f) + "\"/>\n"));

        /// <summary>Writes <paramref name="folder"/>\<paramref name="name"/>.vbox with the given disk locations; returns its path.</summary>
        public static string Vbox(string folder, string name, params string[] diskLocations)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + ".vbox");
            File.WriteAllText(path, VboxText(name, diskLocations), new UTF8Encoding(false));
            return path;
        }

        public static string VboxText(string name, params string[] diskLocations) =>
            "<?xml version=\"1.0\"?>\n<VirtualBox xmlns=\"http://www.virtualbox.org/\" version=\"1.19-windows\">\n  <Machine uuid=\"{" + Guid.NewGuid() + "}\" name=\"" + SecurityElement.Escape(name)
            + "\" OSType=\"Linux_64\">\n    <MediaRegistry>\n      <HardDisks>\n"
            + string.Concat(diskLocations.Select(d => "        <HardDisk uuid=\"{" + Guid.NewGuid() + "}\" location=\"" + SecurityElement.Escape(d) + "\" format=\"VDI\" type=\"Normal\"/>\n"))
            + "      </HardDisks>\n    </MediaRegistry>\n  </Machine>\n</VirtualBox>\n";

        public void Dispose() => Temp.Dispose();
    }

    private static void AssertAdvice(VirtualDiskRow row)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.Advice), row.Kind + " " + row.Name + ": there is advice");
        if (row.Kind == VirtualDiskKind.Wsl)
        {
            Assert.IsFalse(row.Advice.Contains("unregister", StringComparison.OrdinalIgnoreCase), "never an advice that unregisters (it deletes the distribution)");
        }
    }

    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // WSL
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AWslDistributionWithAnExt4VhdxIsARowWithItsAllocatedSizeAndAnAdviceThatDoesNotUnregister()
    {
        using var rig = new Rig("vd-wsl");
        var folder = rig.Wsl("Ubuntu", 3_000_000);
        rig.Wsl("Debian", null);
        var expected = await Measured(folder);

        var rows = await rig.Read();

        var row = rows.Single();
        Assert.AreEqual(VirtualDiskKind.Wsl, row.Kind);
        Assert.AreEqual("Ubuntu", row.Name);
        Assert.AreEqual(Path.Combine(folder, "ext4.vhdx"), row.Path, ignoreCase: true);
        Assert.AreEqual(expected, row.Bytes);
        AssertAdvice(row);
        Assert.AreEqual(1, rig.Source.Reads, "the source is asked once");
    }

    [TestMethod]
    public async Task WslDistributionsWithoutAUsableFileGiveNoRow()
    {
        using var rig = new Rig("vd-wsl-none");
        rig.Source.Distributions.Add(("Missing Folder", Path.Combine(rig.Temp.Path, "wsl", "nowhere")));
        rig.Wsl("No Disk", null);
        var asDirectory = rig.Temp.Folder("wsl", "As Directory");
        Rig.Put(Path.Combine(asDirectory, "ext4.vhdx", "inside.bin"), 400000);
        rig.Source.Distributions.Add(("As Directory", asDirectory));
        var viaJunction = rig.Temp.Folder("wsl", "Via Junction");
        var target = rig.Temp.Folder("link-targets", "wsl");
        Rig.Put(Path.Combine(target, "inside.bin"), 400000);
        Native.CreateJunction(Path.Combine(viaJunction, "ext4.vhdx"), target);
        rig.Source.Distributions.Add(("Via Junction", viaJunction));
        var viaSymlink = rig.Temp.Folder("wsl", "Via Symlink");
        var real = Rig.Put(rig.Temp.Sub("link-targets", "real.vhdx"), 400000);
        try
        {
            File.CreateSymbolicLink(Path.Combine(viaSymlink, "ext4.vhdx"), real);
            rig.Source.Distributions.Add(("Via Symlink", viaSymlink));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A standard user may not make a file symlink here; the junction and the folder above stand for "not a plain file".
        }

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, string.Join(",", rows.Select(r => r.Name)));
    }

    [TestMethod]
    public async Task NoDistributionsAndNoFilesGiveNoRows()
    {
        using var rig = new Rig("vd-empty");

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Docker Desktop
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task DockerDesktopsTwoDiskPathsAreEachARowWhenAPlainFile()
    {
        using var rig = new Rig("vd-docker");
        var disk = rig.DockerDisk(2_000_000);
        var data = rig.DockerData(5_000_000);
        var diskBytes = await Measured(Path.GetDirectoryName(disk)!);
        var dataBytes = await Measured(Path.GetDirectoryName(data)!);

        var rows = await rig.Read();

        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.All(r => r.Kind == VirtualDiskKind.DockerDesktop));
        Assert.AreEqual(dataBytes, rows[0].Bytes, "largest first");
        Assert.AreEqual(Path.Combine(rig.Local, "Docker", "wsl", "data", "ext4.vhdx"), rows[0].Path, ignoreCase: true);
        Assert.AreEqual(diskBytes, rows[1].Bytes);
        Assert.AreEqual(Path.Combine(rig.Local, "Docker", "wsl", "disk", "docker_data.vhdx"), rows[1].Path, ignoreCase: true);
        Assert.IsTrue(rows.All(r => !string.IsNullOrWhiteSpace(r.Name)));
        foreach (var row in rows)
        {
            AssertAdvice(row);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DockerDesktopsOnlyPresentPathIsTheOnlyRow(bool diskPath)
    {
        using var rig = new Rig("vd-docker-one");
        var file = diskPath ? rig.DockerDisk(2_500_000) : rig.DockerData(2_500_000);
        var expected = await Measured(Path.GetDirectoryName(file)!);

        var row = (await rig.Read()).Single();

        Assert.AreEqual(VirtualDiskKind.DockerDesktop, row.Kind);
        Assert.AreEqual(expected, row.Bytes);
    }

    [TestMethod]
    public async Task DockerDesktopsPathThatIsAFolderOrAJunctionIsNotAPlainFile()
    {
        using var rig = new Rig("vd-docker-links");
        Rig.Put(Path.Combine(rig.Local, "Docker", "wsl", "disk", "docker_data.vhdx", "inside.bin"), 400000);
        var target = rig.Temp.Folder("link-targets", "docker");
        Rig.Put(Path.Combine(target, "inside.bin"), 400000);
        Directory.CreateDirectory(Path.Combine(rig.Local, "Docker", "wsl", "data"));
        Native.CreateJunction(Path.Combine(rig.Local, "Docker", "wsl", "data", "ext4.vhdx"), target);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, string.Join(",", rows.Select(r => r.Path)));
    }

    [TestMethod]
    public async Task ADockerDiskThatIsAJunctionDoesNotHideTheOtherPath()
    {
        using var rig = new Rig("vd-docker-link-other");
        var target = rig.Temp.Folder("link-targets", "docker");
        Rig.Put(Path.Combine(target, "inside.bin"), 400000);
        Directory.CreateDirectory(Path.Combine(rig.Local, "Docker", "wsl", "disk"));
        Native.CreateJunction(Path.Combine(rig.Local, "Docker", "wsl", "disk", "docker_data.vhdx"), target);
        var data = rig.DockerData(1_500_000);
        var expected = await Measured(Path.GetDirectoryName(data)!);

        var row = (await rig.Read()).Single();

        Assert.AreEqual(expected, row.Bytes);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // VirtualBox
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AVirtualBoxMachineIsOneRowWithTheSumOfItsDisksRelativeAndAbsolute()
    {
        using var rig = new Rig("vd-vbox");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var relative = Rig.Put(Path.Combine(machine, "Disks", "a.vdi"), 2_000_000);
        var up = Rig.Put(rig.Temp.Sub("VirtualBox VMs", "shared", "b.vdi"), 1_200_000);
        var absolute = Rig.Put(rig.Temp.Sub("elsewhere", "c.vdi"), 3_000_000);
        var vbox = Rig.Vbox(machine, "vm1", @"Disks\a.vdi", @"..\shared\b.vdi", absolute);
        var expected = await Measured(Path.GetDirectoryName(relative)!) + await Measured(Path.GetDirectoryName(up)!) + await Measured(Path.GetDirectoryName(absolute)!);
        rig.Registry(vbox);

        var rows = await rig.Read();

        var row = rows.Single();
        Assert.AreEqual(VirtualDiskKind.VirtualBox, row.Kind);
        Assert.AreEqual("vm1", row.Name);
        Assert.AreEqual(expected, row.Bytes, "the sum of the three disks: a relative one, one up a folder, an absolute one");
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.Path));
        AssertAdvice(row);
    }

    [TestMethod]
    public async Task VirtualBoxReadsNothingButLeavesEveryFileAsItWas()
    {
        using var rig = new Rig("vd-vbox-readonly");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        Rig.Put(Path.Combine(machine, "Disks", "a.vdi"), 1_000_000);
        rig.Registry(Rig.Vbox(machine, "vm1", @"Disks\a.vdi"));
        rig.Wsl("Ubuntu", 1_000_000);
        rig.DockerDisk(1_000_000);
        string Snapshot() => string.Join("|", Directory.EnumerateFiles(rig.Temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => f + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks));
        var before = Snapshot();
        var folders = Directory.EnumerateDirectories(rig.Temp.Path, "*", SearchOption.AllDirectories).Count();

        var rows = await rig.Read();

        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual(before, Snapshot(), "no file written, resized or touched");
        Assert.AreEqual(folders, Directory.EnumerateDirectories(rig.Temp.Path, "*", SearchOption.AllDirectories).Count(), "no folder made");
    }

    [TestMethod]
    public async Task AMissingDiskCountsNothingAndAMachineWithNoDiskOnDiskGivesNoRow()
    {
        using var rig = new Rig("vd-vbox-missing");
        var one = rig.Temp.Folder("VirtualBox VMs", "one");
        var present = Rig.Put(Path.Combine(one, "disk", "present.vdi"), 1_500_000);
        var vboxOne = Rig.Vbox(one, "one", @"disk\present.vdi", @"disk\gone.vdi");
        var two = rig.Temp.Folder("VirtualBox VMs", "two");
        var vboxTwo = Rig.Vbox(two, "two", @"disk\gone.vdi", @"C:\no\such\place\gone2.vdi");
        var three = rig.Temp.Folder("VirtualBox VMs", "three");
        var vboxThree = Rig.Vbox(three, "three");
        var expected = await Measured(Path.GetDirectoryName(present)!);
        rig.Registry(vboxOne, vboxTwo, vboxThree, Path.Combine(rig.Temp.Path, "VirtualBox VMs", "no-machine", "no-machine.vbox"));

        var rows = await rig.Read();

        var row = rows.Single();
        Assert.AreEqual("one", row.Name);
        Assert.AreEqual(expected, row.Bytes, "only the disk that is there");
    }

    [TestMethod]
    public async Task ADamagedVboxGivesNoRowForThatMachineOnly()
    {
        using var rig = new Rig("vd-vbox-damaged");
        var good = rig.Temp.Folder("VirtualBox VMs", "good");
        var goodDisk = Rig.Put(Path.Combine(good, "disk", "good.vdi"), 1_000_000);
        var vboxGood = Rig.Vbox(good, "good", @"disk\good.vdi");
        var bad = rig.Temp.Folder("VirtualBox VMs", "bad");
        Rig.Put(Path.Combine(bad, "disk", "bad.vdi"), 1_000_000);
        var vboxBad = Path.Combine(bad, "bad.vbox");
        File.WriteAllText(vboxBad, "<VirtualBox xmlns=\"http://www.virtualbox.org/\"><Machine name=\"bad\"><MediaRegistry><HardDisks><HardDisk location=\"disk\\bad.vdi\"");
        var binary = rig.Temp.Folder("VirtualBox VMs", "binary");
        Rig.Put(Path.Combine(binary, "disk", "bin.vdi"), 1_000_000);
        var vboxBinary = Path.Combine(binary, "binary.vbox");
        File.WriteAllBytes(vboxBinary, [0xFF, 0xFE, 0x00, 0x01, 0xC3, 0x28, 0x00]);
        rig.Registry(vboxBad, vboxBinary, vboxGood);

        var rows = await rig.Read();

        var row = rows.Single();
        Assert.AreEqual("good", row.Name);
        Assert.AreEqual(await Measured(Path.GetDirectoryName(goodDisk)!), row.Bytes);
    }

    [TestMethod]
    [DataRow("not xml at all")]
    [DataRow("<VirtualBox xmlns=\"http://www.virtualbox.org/\"><Global><MachineRegistry><MachineEntry src=\"x")]
    [DataRow("")]
    public async Task ADamagedOrEmptyVirtualBoxXmlGivesNoRowsAndNoException(string content)
    {
        using var rig = new Rig("vd-vbox-xml-damaged");
        Directory.CreateDirectory(Path.GetDirectoryName(rig.VirtualBoxXml)!);
        File.WriteAllText(rig.VirtualBoxXml, content);
        rig.DockerDisk(1_000_000);

        var rows = await rig.Read();

        Assert.AreEqual(VirtualDiskKind.DockerDesktop, rows.Single().Kind, "the other sources still give their rows");
    }

    [TestMethod]
    public async Task AVirtualBoxXmlThatIsAFolderOrMissingGivesNoRowsAndNoException()
    {
        using var rig = new Rig("vd-vbox-xml-folder");
        Directory.CreateDirectory(rig.VirtualBoxXml);
        Assert.AreEqual(0, (await rig.Read()).Count);

        Directory.Delete(rig.VirtualBoxXml);
        Directory.Delete(Path.GetDirectoryName(rig.VirtualBoxXml)!);
        Assert.AreEqual(0, (await rig.Read()).Count);
    }

    [TestMethod]
    public async Task ADocumentTypeThatNamesALocalFileIsNeverResolvedAndNothingOfItAppears()
    {
        using var rig = new Rig("vd-vbox-xxe");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_000_000);
        var vbox = Rig.Vbox(machine, "vm1", @"disk\a.vdi");
        // If the entity were resolved, src would be the path of a real machine file and a row would appear.
        var secret = rig.Temp.Sub("secret.txt");
        File.WriteAllText(secret, vbox, new UTF8Encoding(false));
        var uri = new Uri(secret).AbsoluteUri;
        var prolog = "<!DOCTYPE VirtualBox [\n  <!ENTITY leak SYSTEM \"" + uri + "\">\n]>\n";
        rig.WriteRegistry("      <MachineEntry uuid=\"{00000000-0000-0000-0000-000000000001}\" src=\"&leak;\"/>\n", prolog);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, "the entity was not resolved: " + string.Join(",", rows.Select(r => r.Name + "@" + r.Path)));
    }

    [TestMethod]
    public async Task AnExternalDtdIsNeverFetchedAndAnInternalEntityIsNeverExpanded()
    {
        using var rig = new Rig("vd-vbox-dtd");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_000_000);
        var vbox = Rig.Vbox(machine, "vm1", @"disk\a.vdi");
        var secret = rig.Temp.Sub("secret.txt");
        File.WriteAllText(secret, vbox, new UTF8Encoding(false));
        var dtd = rig.Temp.Sub("evil.dtd");
        File.WriteAllText(dtd, "<!ENTITY ext SYSTEM \"" + new Uri(secret).AbsoluteUri + "\">\n");
        var externalProlog = "<!DOCTYPE VirtualBox SYSTEM \"" + new Uri(dtd).AbsoluteUri + "\">\n";
        rig.WriteRegistry("      <MachineEntry uuid=\"{00000000-0000-0000-0000-000000000001}\" src=\"&ext;\"/>\n", externalProlog);
        Assert.AreEqual(0, (await rig.Read()).Count, "an external DTD is not fetched");

        var internalProlog = "<!DOCTYPE VirtualBox [\n  <!ENTITY machine \"" + SecurityElement.Escape(vbox) + "\">\n]>\n";
        rig.WriteRegistry("      <MachineEntry uuid=\"{00000000-0000-0000-0000-000000000002}\" src=\"&machine;\"/>\n", internalProlog);
        Assert.AreEqual(0, (await rig.Read()).Count, "DTD processing is off: not even an internal entity is expanded");
    }

    [TestMethod]
    public async Task ADocumentTypeInAVboxIsNotResolvedEither()
    {
        using var rig = new Rig("vd-vbox-vbox-dtd");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_000_000);
        var diskPath = Path.Combine(machine, "disk", "a.vdi");
        var secret = rig.Temp.Sub("secret.txt");
        File.WriteAllText(secret, diskPath, new UTF8Encoding(false));
        var vbox = Path.Combine(machine, "vm1.vbox");
        File.WriteAllText(vbox,
            "<?xml version=\"1.0\"?>\n<!DOCTYPE VirtualBox [<!ENTITY leak SYSTEM \"" + new Uri(secret).AbsoluteUri + "\">]>\n"
            + "<VirtualBox xmlns=\"http://www.virtualbox.org/\"><Machine name=\"vm1\"><MediaRegistry><HardDisks><HardDisk location=\"&leak;\"/></HardDisks></MediaRegistry></Machine></VirtualBox>\n",
            new UTF8Encoding(false));
        rig.Registry(vbox);

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, string.Join(",", rows.Select(r => r.Name + "@" + r.Path)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AVirtualBoxXmlBiggerThanFourMiBIsNotReadAndOneOfExactlyFourMiBIs(bool oneOver)
    {
        using var rig = new Rig("vd-vbox-limit-xml");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var disk = Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_000_000);
        var vbox = Rig.Vbox(machine, "vm1", @"disk\a.vdi");
        rig.Registry(vbox);
        PadTo(rig.VirtualBoxXml, FourMiB + (oneOver ? 1 : 0));
        var expected = await Measured(Path.GetDirectoryName(disk)!);

        var rows = await rig.Read();

        if (oneOver)
        {
            Assert.AreEqual(0, rows.Count, "over 4 MiB is not read");
        }
        else
        {
            Assert.AreEqual(expected, rows.Single().Bytes, "exactly 4 MiB is read");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AVboxBiggerThanFourMiBIsNotReadAndOneOfExactlyFourMiBIs(bool oneOver)
    {
        using var rig = new Rig("vd-vbox-limit-vbox");
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var disk = Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_000_000);
        var vbox = Rig.Vbox(machine, "vm1", @"disk\a.vdi");
        PadTo(vbox, FourMiB + (oneOver ? 1 : 0));
        rig.Registry(vbox);
        var expected = await Measured(Path.GetDirectoryName(disk)!);

        var rows = await rig.Read();

        if (oneOver)
        {
            Assert.AreEqual(0, rows.Count, "over 4 MiB is not read");
        }
        else
        {
            Assert.AreEqual(expected, rows.Single().Bytes, "exactly 4 MiB is read");
        }
    }

    [TestMethod]
    public async Task AVboxOrADiskThatIsALinkIsNotFollowed()
    {
        using var rig = new Rig("vd-vbox-links");
        // A machine file that is a junction to a folder, and a disk location that is a junction to a folder.
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var target = rig.Temp.Folder("link-targets", "disk");
        Rig.Put(Path.Combine(target, "inside.bin"), 800000);
        Native.CreateJunction(Path.Combine(machine, "linked.vdi"), target);
        var vbox = Rig.Vbox(machine, "vm1", "linked.vdi");
        var other = rig.Temp.Folder("VirtualBox VMs", "vm2");
        Native.CreateJunction(Path.Combine(other, "vm2.vbox"), rig.Temp.Folder("link-targets", "vm2-folder"));
        rig.Registry(vbox, Path.Combine(other, "vm2.vbox"));

        var rows = await rig.Read();

        Assert.AreEqual(0, rows.Count, string.Join(",", rows.Select(r => r.Name + "@" + r.Path)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // All kinds together, cancel
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RowsOfAllKindsAreSortedLargestFirst()
    {
        using var rig = new Rig("vd-all");
        var wsl = rig.Wsl("Ubuntu", 2_000_000);
        var docker = rig.DockerDisk(6_000_000);
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var vdi = Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 4_000_000);
        rig.Registry(Rig.Vbox(machine, "vm1", @"disk\a.vdi"));
        var wslBytes = await Measured(wsl);
        var dockerBytes = await Measured(Path.GetDirectoryName(docker)!);
        var vboxBytes = await Measured(Path.GetDirectoryName(vdi)!);

        var rows = await rig.Read();

        CollectionAssert.AreEqual(new[] { VirtualDiskKind.DockerDesktop, VirtualDiskKind.VirtualBox, VirtualDiskKind.Wsl }, rows.Select(r => r.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { dockerBytes, vboxBytes, wslBytes }, rows.Select(r => r.Bytes).ToArray());
        foreach (var row in rows)
        {
            AssertAdvice(row);
        }
    }

    [TestMethod]
    public async Task ACancelledTokenIsAThrownCancel()
    {
        using var rig = new Rig("vd-cancel");
        rig.Wsl("Ubuntu", 1_000_000);

        try
        {
            await VirtualDisks.ReadAsync(rig.Context, rig.Source, Cancelled());
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("expected OperationCanceledException, got a result instead");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Phase C1 review round 1 (plan items 1, 2, 4 and 5; owner decision 16)
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly string[] ExpectedWslNames = ["Ubuntu", "docker-desktop-mine"];

    private sealed class ThrowingSource(Func<Exception> make) : IVirtualDiskSource
    {
        public IReadOnlyList<(string Name, string BasePath)> WslDistributions() => throw make();
    }

    [TestMethod]
    public async Task DockerDesktopsOwnTwoWslDistributionsAreNotWslRowsButSimilarNamesAre()
    {
        using var rig = new Rig("vd-r1-docker-distros");
        rig.Wsl("docker-desktop", 1_000_000);
        rig.Wsl("docker-desktop-data", 1_000_000);
        rig.Wsl("docker-desktop-mine", 1_000_000);
        rig.Wsl("Ubuntu", 1_000_000);

        var rows = await rig.Read();

        CollectionAssert.AreEquivalent(ExpectedWslNames, rows.Select(r => r.Name).ToArray(),
            string.Join(",", rows.Select(r => r.Name)));
        Assert.IsTrue(rows.All(r => r.Kind == VirtualDiskKind.Wsl));
    }

    [TestMethod]
    public async Task ADiskPathListedByTwoSourcesIsOneRowAndTheDockerRowWins()
    {
        using var rig = new Rig("vd-r1-dup-docker");
        var disk = rig.DockerData(3_000_000);
        // A WSL distribution whose base folder is Docker's data folder: the same ext4.vhdx as Docker's second path.
        rig.Source.Distributions.Add(("Mine", Path.GetDirectoryName(disk)!));

        var rows = await rig.Read();

        var row = rows.Single();
        Assert.AreEqual(VirtualDiskKind.DockerDesktop, row.Kind, "the first source in the order Docker, VirtualBox, WSL wins");
        Assert.AreEqual(disk, row.Path, ignoreCase: true);
    }

    [TestMethod]
    public async Task TheSameDiskPathInAnotherCaseOrSpellingIsStillOneRow()
    {
        using var rig = new Rig("vd-r1-dup-case");
        var disk = rig.DockerData(3_000_000);
        var folder = Path.GetDirectoryName(disk)!;
        rig.Source.Distributions.Add(("Upper", folder.ToUpperInvariant()));
        rig.Source.Distributions.Add(("Prefixed", @"\\?\" + folder + @"\"));
        var own = rig.Wsl("Own", 1_000_000);
        rig.Source.Distributions.Add(("Own again", own));
        rig.Source.Distributions.Add(("Own upper", own.ToUpperInvariant()));

        var rows = await rig.Read();

        Assert.AreEqual(2, rows.Count, string.Join(",", rows.Select(r => r.Name + "@" + r.Path)));
        Assert.AreEqual(1, rows.Count(r => r.Kind == VirtualDiskKind.DockerDesktop));
        Assert.AreEqual(1, rows.Count(r => r.Kind == VirtualDiskKind.Wsl));
        Assert.AreEqual(2, rows.Select(r => r.Path.ToUpperInvariant()).Distinct().Count(), "no path twice");
    }

    [TestMethod]
    public async Task AWslBasePathWithTheExtendedPrefixAndALocalDriveIsReadAsLocal()
    {
        using var rig = new Rig("vd-r1-extended-local");
        var folder = rig.Temp.Folder("wsl", "Prefixed");
        Rig.Put(Path.Combine(folder, "ext4.vhdx"), 2_000_000);
        rig.Source.Distributions.Add(("Prefixed", @"\\?\" + folder));
        var expected = await Measured(folder);

        var row = (await rig.Read()).Single();

        Assert.AreEqual(VirtualDiskKind.Wsl, row.Kind);
        Assert.AreEqual("Prefixed", row.Name);
        Assert.AreEqual(Path.Combine(folder, "ext4.vhdx"), row.Path, ignoreCase: true, "the path shown has no \\\\?\\ prefix");
        Assert.AreEqual(expected, row.Bytes);
    }

    [TestMethod]
    public async Task AWslBasePathThatIsUncExtendedUncDeviceOrAShareGivesNoRowAndALocalOneStays()
    {
        using var rig = new Rig("vd-r1-nonlocal");
        var folder = rig.Temp.Folder("wsl", "Local One");
        Rig.Put(Path.Combine(folder, "ext4.vhdx"), 2_000_000);
        var share = @"localhost\" + char.ToUpperInvariant(folder[0]) + "$" + folder[2..]; // the admin share of the same folder
        rig.Source.Distributions.Add(("Extended UNC", @"\\?\UNC\" + share));
        rig.Source.Distributions.Add(("Device Path", @"\\.\" + folder));
        rig.Source.Distributions.Add(("Share", @"\\" + share));
        rig.Source.Distributions.Add(("Extended Volume", @"\\?\Volume{00000000-0000-0000-0000-000000000000}\wsl"));
        rig.Source.Distributions.Add(("Local One", folder));

        var rows = await rig.Read();

        Assert.AreEqual("Local One", rows.Single().Name, string.Join(",", rows.Select(r => r.Name)));
    }

    [TestMethod]
    [DataRow("InvalidOperation")]
    [DataRow("IO")]
    [DataRow("UnauthorizedAccess")]
    [DataRow("Security")]
    [DataRow("Argument")]
    public async Task ASourceThatThrowsGivesNoWslRowsAndTheOtherKindsStay(string kind)
    {
        using var rig = new Rig("vd-r1-throw");
        var docker = rig.DockerDisk(2_000_000);
        var machine = rig.Temp.Folder("VirtualBox VMs", "vm1");
        var vdi = Rig.Put(Path.Combine(machine, "disk", "a.vdi"), 1_500_000);
        rig.Registry(Rig.Vbox(machine, "vm1", @"disk\a.vdi"));
        Func<Exception> make = kind switch
        {
            "IO" => () => new IOException("registry gone"),
            "UnauthorizedAccess" => () => new UnauthorizedAccessException("no"),
            "Security" => () => new SecurityException("no"),
            "Argument" => () => new ArgumentException("bad"),
            _ => () => new InvalidOperationException("boom"),
        };

        var rows = await VirtualDisks.ReadAsync(rig.Context, new ThrowingSource(make), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { VirtualDiskKind.DockerDesktop, VirtualDiskKind.VirtualBox }, rows.OrderByDescending(r => r.Bytes).Select(r => r.Kind).ToArray(),
            kind + ": " + string.Join(",", rows.Select(r => r.Kind)));
        Assert.AreEqual(await Measured(Path.GetDirectoryName(docker)!), rows.Single(r => r.Kind == VirtualDiskKind.DockerDesktop).Bytes);
        Assert.IsNotNull(vdi);
    }

    [TestMethod]
    public async Task TheDockerAdviceIsTheCopyTextAdviceAndSaysToUpdateFirst()
    {
        using var rig = new Rig("vd-r1-docker-advice");
        rig.DockerDisk(2_000_000);
        rig.DockerData(3_000_000);

        var rows = await rig.Read();

        Assert.AreEqual(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.AreEqual(Copy.Text.AdviceDockerDesktop, row.Advice, row.Name);
        }

        Assert.Contains("4.92", Copy.Text.AdviceDockerDesktop, "owner decision 16: update to 4.92 or later");
        Assert.Contains("first", Copy.Text.AdviceDockerDesktop, "the update comes first");
        Assert.Contains("Disk image location", Copy.Text.AdviceDockerDesktop);
        Assert.IsLessThan(
            Copy.Text.AdviceDockerDesktop.IndexOf("Settings", StringComparison.Ordinal),
            Copy.Text.AdviceDockerDesktop.IndexOf("4.92", StringComparison.Ordinal),
            "the update is named before the setting");
    }

    [TestMethod]
    public async Task TheDockerRowNamesComeFromACopyTextConstantNamedForDocker()
    {
        // The builder adds the names to Copy.Text; their constant names are not known here, so each Docker row's Name must equal
        // the value of some public const string of Copy.Text whose field name contains "Docker" (and not be a literal in VirtualDisks).
        using var rig = new Rig("vd-r1-docker-names");
        rig.DockerDisk(2_000_000);
        rig.DockerData(3_000_000);
        var dockerTexts = typeof(Copy.Text).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.Contains("Docker", StringComparison.OrdinalIgnoreCase))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();

        var rows = await rig.Read();

        Assert.AreEqual(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Name));
            CollectionAssert.Contains(dockerTexts, row.Name, "the row name '" + row.Name + "' is a Copy.Text constant with Docker in its name");
        }

        Assert.AreEqual(2, rows.Select(r => r.Name).Distinct().Count(), "the two Docker rows have different names");
    }

    /// <summary>Pads an XML file with a trailing comment so that it is exactly <paramref name="length"/> bytes long.</summary>
    private static void PadTo(string path, int length)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimEnd() + "\n<!--";
        var closing = "-->";
        var fill = length - Encoding.UTF8.GetByteCount(text) - closing.Length;
        Assert.IsGreaterThan(0, fill, "the file is small enough to pad");
        File.WriteAllText(path, text + new string('x', fill) + closing, new UTF8Encoding(false));
        Assert.AreEqual((long)length, new FileInfo(path).Length);
    }
}
