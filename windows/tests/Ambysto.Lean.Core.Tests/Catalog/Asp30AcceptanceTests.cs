// Independent ASP-30 acceptance, drafted before implementation from the frozen contract and observed CapCut schema.
// Intended split: Catalog/DeveloperSpaceTests.cs and Catalog/InstallerVersionsTests.cs; no real registry/tools touched.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

[TestClass]
public sealed class DeveloperSpaceTests
{
    [TestMethod]
    public async Task KnownDisjointStoresAreActuallyMeasuredWithMeaningfulCategoriesAndNoMutation()
    {
        using var fx = new ProfileFixture("asp30-diagnosis");
        var paths = new[]
        {
            fx.InProfile(".rustup"), fx.InProfile(".cargo", "registry"), fx.InProfile(".cargo", "bin"),
            fx.InProfile(".cache"), fx.InProfile(".codex"), fx.InProfile(".nuget", "packages"),
            Path.Combine(fx.LocalAppData, "Programs"), Path.Combine(fx.LocalAppData, "Packages"),
            Path.Combine(fx.LocalAppData, "CapCut", "Apps"), Path.Combine(fx.LocalAppData, "CapCut", "User Data"),
            Path.Combine(fx.LocalAppData, "Lark"), Path.Combine(fx.LocalAppData, "LarkInternational"),
            Path.Combine(fx.RoamingAppData, "Notion"), Path.Combine(fx.RoamingAppData, "Orca"),
            Path.Combine(fx.LocalAppData, "hermes"),
            Path.Combine(fx.LocalAppData, "Google", "Chrome", "User Data", "OptGuideOnDeviceModel"),
            Path.Combine(fx.LocalAppData, "npm"), Path.Combine(fx.RoamingAppData, "npm"),
        };
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sentinels = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Length; i++)
        {
            var file = ProfileFixture.MakeFile(Path.Combine(paths[i], "sentinel.bin"), (i + 1) * 4096, i + 11);
            expected.Add(paths[i], Native.AllocationSizeOf(file)); // independent NTFS allocation oracle, not CacheFolder/Scanner
            sentinels.Add(file, File.ReadAllBytes(file));
        }
        var source = new Asp30Inventory();
        var rows = await DeveloperSpace.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(paths, rows.Select(r => r.Path).ToArray(), "each supported existing disjoint root appears once");
        Assert.AreEqual(paths.Length, rows.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var row in rows)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Name), row.Path + " has an owner/tool name");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Category), row.Path + " distinguishes the type of stored data");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), row.Path + " explains preservation/uncertainty");
            Assert.IsNotNull((object?)row.Bytes, "known measured bytes are not unknown");
            Assert.AreEqual(expected[row.Path], Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture), row.Path);
        }
        Assert.AreEqual(expected.Values.Sum(), rows.Sum(r => Convert.ToInt64(r.Bytes, CultureInfo.InvariantCulture)), "no ancestor/child double count");
        foreach (var entry in sentinels) CollectionAssert.AreEqual(entry.Value, File.ReadAllBytes(entry.Key), "diagnosis never changes data");
    }

    [TestMethod]
    public async Task NestedAndAliasedKnownRootsCountTheirAllocatedBytesOnce()
    {
        using var fx = new ProfileFixture("asp30-diagnosis-nested");
        var outer = fx.InProfile(".rustup");
        var aliasedAppData = Path.Combine(outer, "appdata");
        var programs = Path.Combine(aliasedAppData, "Programs");
        var npm = Path.Combine(aliasedAppData, "npm");
        var files = new[]
        {
            ProfileFixture.MakeFile(Path.Combine(outer, "toolchain.bin"), 4096, 21),
            ProfileFixture.MakeFile(Path.Combine(programs, "tool.bin"), 8192, 22),
            ProfileFixture.MakeFile(Path.Combine(npm, "global.bin"), 12288, 23),
        };
        var before = files.Select(File.ReadAllBytes).ToArray();
        // Both app-data locations alias the same physical path; Programs/npm also nest under .rustup.
        var context = fx.Context with { LocalAppData = aliasedAppData, RoamingAppData = aliasedAppData };
        var rows = await DeveloperSpace.ReadAsync(context, new Asp30Inventory(), CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { outer }, rows.Select(r => r.Path).ToArray(),
            "a declared descendant or two spellings of the same known store cannot add another total");
        Assert.AreEqual(files.Sum(Native.AllocationSizeOf), Convert.ToInt64(rows.Single().Bytes, CultureInfo.InvariantCulture));
        for (var i = 0; i < files.Length; i++) CollectionAssert.AreEqual(before[i], File.ReadAllBytes(files[i]));
    }

    [TestMethod]
    public async Task HardLinkedFilesAcrossKnownRootsHaveOneAllocatedOwnerAndPreserveBothUniqueShares()
    {
        using var fx = new ProfileFixture("asp30-diagnosis-hardlinks");
        var first = fx.InProfile(".rustup");
        var second = fx.InProfile(".cargo", "bin");
        var shared = ProfileFixture.MakeFile(Path.Combine(first, "shared.bin"), 16384, 31);
        var firstOnly = ProfileFixture.MakeFile(Path.Combine(first, "first.bin"), 4096, 32);
        var secondOnly = ProfileFixture.MakeFile(Path.Combine(second, "second.bin"), 8192, 33);
        var alias = Path.Combine(second, "shared-alias.bin");
        Native.CreateHardLink(alias, shared);
        var before = File.ReadAllBytes(shared);
        var rows = await DeveloperSpace.ReadAsync(fx.Context, new Asp30Inventory(), CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { first, second }, rows.Select(r => r.Path).ToArray());
        Assert.AreEqual(Native.AllocationSizeOf(shared) + Native.AllocationSizeOf(firstOnly),
            Convert.ToInt64(rows.Single(r => string.Equals(r.Path, first, StringComparison.OrdinalIgnoreCase)).Bytes, CultureInfo.InvariantCulture),
            "the first declared root owns the shared identity once");
        Assert.AreEqual(Native.AllocationSizeOf(secondOnly),
            Convert.ToInt64(rows.Single(r => string.Equals(r.Path, second, StringComparison.OrdinalIgnoreCase)).Bytes, CultureInfo.InvariantCulture),
            "later roots retain their unique bytes without counting another hard-link name");
        Assert.AreEqual(new[] { shared, firstOnly, secondOnly }.Sum(Native.AllocationSizeOf),
            rows.Sum(r => Convert.ToInt64(r.Bytes, CultureInfo.InvariantCulture)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(shared));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(alias));
    }

    [TestMethod]
    public async Task MissingAndEmptyStoresDoNotBecomeInventedZeroSizedDiagnosisRows()
    {
        using var fx = new ProfileFixture("asp30-empty-diagnosis");
        Directory.CreateDirectory(fx.InProfile(".rustup"));
        Directory.CreateDirectory(Path.Combine(fx.LocalAppData, "Packages"));
        var rows = await DeveloperSpace.ReadAsync(fx.Context, new Asp30Inventory(), CancellationToken.None);
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task DiagnosisDoesNotFollowAKnownStoreJunctionIntoAnotherUsersData()
    {
        using var fx = new ProfileFixture("asp30-diagnosis-junction");
        var target = Path.Combine(fx.OtherProfile, "private-models");
        var sentinel = ProfileFixture.MakeFile(Path.Combine(target, "secret.bin"), 8192, 91);
        var before = File.ReadAllBytes(sentinel);
        Native.CreateJunction(fx.InProfile(".cache"), target);
        var rows = await DeveloperSpace.ReadAsync(fx.Context, new Asp30Inventory(), CancellationToken.None);
        Assert.IsFalse(rows.Any(r => string.Equals(r.Path, target, StringComparison.OrdinalIgnoreCase)), "junction target is not attributed to this user");
        Assert.IsFalse(rows.Any(r => r.Path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        var unknown = rows.Single(r => string.Equals(r.Path, fx.InProfile(".cache"), StringComparison.OrdinalIgnoreCase));
        Assert.IsNull((object?)unknown.Bytes, "existing redirected store is visible as unknown, never omitted or fabricated zero");
        Assert.IsFalse(string.IsNullOrWhiteSpace(unknown.Note), "unknown size has an explanatory reason");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(sentinel));
    }

    [TestMethod]
    public async Task CancelledDiagnosisPropagatesCancellationInsteadOfPublishingPartialSuccess()
    {
        using var fx = new ProfileFixture("asp30-diagnosis-cancel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { _ = await DeveloperSpace.ReadAsync(fx.Context, new Asp30Inventory(), cancelled.Token); Assert.Fail("caller cancellation swallowed"); }
        catch (OperationCanceledException) { }
    }
}

[TestClass]
public sealed class InstallerVersionsTests
{
    private static readonly string[] Old = ["7.3.0", "7.5.0", "7.7.0", "8.1.1", "8.2.0", "8.3.0", "8.5.0"];
    private const string Current = "9.1.0.3879";

    private sealed class Rig : IDisposable
    {
        public ProfileFixture Fx { get; } = new("asp30-versions");
        public Asp30Inventory Source { get; } = new();
        public Dictionary<string, long> OriginalAllocations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string CapCut => Path.Combine(Fx.LocalAppData, "CapCut");
        public string Apps => Path.Combine(CapCut, "Apps");
        public string ProductInfo => Path.Combine(Apps, "ProductInfo.xml");
        public string Configure => Path.Combine(Apps, "Configure.ini");
        public Rig()
        {
            foreach (var version in Old.Append(Current))
            {
                var path = Leaf(version);
                OriginalAllocations.Add(path, Native.AllocationSizeOf(Path.Combine(path, "payload.bin")));
            }
            WriteCurrent(Current);
            File.WriteAllText(Configure, "[capcut]\r\nlast_version=\r\n");
            Source.Entries.Add(Entry(Current)); // real CapCut install has no InstallLocation
        }
        public string Leaf(string version)
        {
            var path = Path.Combine(Apps, version);
            ProfileFixture.MakeFile(Path.Combine(path, "payload.bin"), 8192, 73);
            return path;
        }
        public string MakeImage(string path)
        {
            var file = ProfileFixture.MakeFile(path, 4096, 82);
            var dir = Path.GetDirectoryName(file);
            if (dir is not null && OriginalAllocations.TryGetValue(dir, out var bytes))
                OriginalAllocations[dir] = bytes + Native.AllocationSizeOf(file);
            return file;
        }
        public void WriteCurrent(string version) => File.WriteAllText(ProductInfo,
            "<packet><full_appver value=\"" + version + "\"/></packet>");
        public static UninstallEntry Entry(string version, string? location = null, string key = "CapCut") =>
            new(key, UninstallScope.PerUser, RegistryViewKind.Bits64, "CapCut", "Bytedance Pte. Ltd.", version, location, false);
        public Task<IReadOnlyList<InstallerVersionRow>> Read(PathContext? context = null) =>
            InstallerVersions.ReadAsync(context ?? Fx.Context, Source, CancellationToken.None);
        public void Dispose() => Fx.Dispose();
    }

    private static InstallerVersionRow Row(IReadOnlyList<InstallerVersionRow> rows, string path) =>
        rows.Single(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    private static void Blocked(Rig rig, IReadOnlyList<InstallerVersionRow> rows)
    {
        foreach (var version in Old.Append(Current))
        {
            var path = Path.Combine(rig.Apps, version);
            var row = Row(rows, path); // each original path exactly once; arbitrary extra rows cannot stand in for it
            Assert.AreEqual(version, row.Version);
            Assert.AreEqual(rig.OriginalAllocations[path],
                Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture), "uncertain evidence preserves original measured bytes: " + path);
        }
        Assert.IsFalse(rows.Any(r => r.CanMove), "uncertain current/reference evidence grants no action");
        Assert.IsTrue(rows.All(r => !string.IsNullOrWhiteSpace(r.Reason)), "each retained/unknown row explains why");
    }

    [TestMethod]
    public async Task ObservedRegistryAndProductInfoSchemaEnableOnlySevenOldIndividualLeavesWithoutInstallLocation()
    {
        using var rig = new Rig();
        var rows = await rig.Read();
        CollectionAssert.AreEquivalent(Old.Append(Current).Select(v => Path.Combine(rig.Apps, v)).ToArray(), rows.Select(r => r.Path).ToArray());
        foreach (var version in Old.Append(Current))
        {
            var row = Row(rows, Path.Combine(rig.Apps, version));
            Assert.AreEqual("CapCut", row.AppName);
            Assert.AreEqual(version, row.Version);
            Assert.AreEqual(Native.AllocationSizeOf(Path.Combine(row.Path, "payload.bin")), Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
            Assert.AreEqual(version != Current, row.CanMove, version);
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Reason));
        }
        foreach (var version in Old)
            Assert.IsTrue(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, version), rig.Fx.Context, rig.Source));
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Current), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    public async Task RegisteredCurrentOlderThanHighestAndEveryAdditionalRegistryReferenceAreRetained()
    {
        using var rig = new Rig();
        rig.Source.Entries[0] = Rig.Entry("8.5.0");
        rig.WriteCurrent("8.5.0");
        rig.Source.Entries.Add(Rig.Entry("8.2.0", Path.Combine(rig.Apps, "8.2.0"), "CapCut-previous"));
        var rows = await rig.Read();
        foreach (var version in new[] { "8.5.0", "8.2.0", Current })
            Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, version)).CanMove, "retain registered/referenced/highest: " + version);
        Assert.IsTrue(Row(rows, Path.Combine(rig.Apps, "7.3.0")).CanMove, "known unreferenced old leaves still have usable action");
    }

    [TestMethod]
    public async Task SecondaryExactVendorRegistrationRetainsItsVersionWithoutAnInstallLocation()
    {
        using var rig = new Rig();
        var referenced = Path.Combine(rig.Apps, "8.2.0");
        Assert.IsTrue(Row(await rig.Read(), referenced).CanMove, "control: this old version was otherwise eligible");
        rig.Source.Entries.Add(Rig.Entry("8.2.0", string.Empty, "CapCut-previous"));
        var rows = await rig.Read();
        var retained = Row(rows, referenced);
        Assert.IsFalse(retained.CanMove, "exact app/vendor registration retains its version even without InstallLocation");
        Assert.IsFalse(string.IsNullOrWhiteSpace(retained.Reason));
        Assert.AreEqual(rig.OriginalAllocations[referenced], Convert.ToInt64(retained.Bytes, CultureInfo.InvariantCulture));
        Assert.IsFalse(InstallerVersions.CanMoveNow(referenced, rig.Fx.Context, rig.Source));
        var unreferenced = Path.Combine(rig.Apps, "7.3.0");
        Assert.IsTrue(Row(rows, unreferenced).CanMove, "a conclusive secondary reference does not disable every old leaf");
        Assert.IsTrue(InstallerVersions.CanMoveNow(unreferenced, rig.Fx.Context, rig.Source));
        Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, Current)).CanMove, "current/highest remains retained");
    }

    [TestMethod]
    public async Task SecondaryExactVendorRegistrationWithUnknownVersionBlocksEveryOldVersion()
    {
        using var rig = new Rig();
        var candidate = Path.Combine(rig.Apps, Old[0]);
        Assert.IsTrue(Row(await rig.Read(), candidate).CanMove, "control: complete evidence permits an old leaf");
        rig.Source.Entries.Add(Rig.Entry("not-a-version", string.Empty, "CapCut-previous"));
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source),
            "unknown version in a related registration may reference any candidate");
    }

    [TestMethod]
    public async Task ConfigureLastVersionIsRetainedEvenWhenItIsNotTheCurrentOrHighest()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.Configure, "[capcut]\r\nlast_version=8.2.0\r\n");
        var rows = await rig.Read();
        Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, "8.2.0")).CanMove);
        Assert.IsTrue(Row(rows, Path.Combine(rig.Apps, "7.3.0")).CanMove);
    }

    [TestMethod]
    public async Task NumericAliasesOfCurrentAndHighestRemainRetainedAndWindowsPathCaseDoesNotChangeEligibility()
    {
        using var rig = new Rig();
        var alias = rig.Leaf("09.1.0.3879");
        Assert.IsFalse(Row(await rig.Read(), alias).CanMove, "leading-zero spelling is numerically the retained current/highest version");
        Assert.IsFalse(InstallerVersions.CanMoveNow(alias, rig.Fx.Context, rig.Source));
        Assert.IsTrue(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]).ToUpperInvariant(), rig.Fx.Context, rig.Source),
            "Windows paths and registry install references compare without case sensitivity");
    }

    [TestMethod]
    public async Task ConflictingCapCutPrimaryRegistrationsAcrossViewsNeverChooseOneCurrentVersionByEnumerationOrder()
    {
        using var rig = new Rig();
        rig.Source.Entries.Add(Rig.Entry("8.5.0") with { View = RegistryViewKind.Bits32 });
        Blocked(rig, await rig.Read());
        rig.Source.Entries.Reverse();
        Blocked(rig, await rig.Read());
    }

    [TestMethod]
    public async Task NumericOrderIsNotLexicalAndOnlyStrictlyOlderLeavesAreEligible()
    {
        using var rig = new Rig();
        rig.Leaf("1.9.999"); rig.Leaf("1.10.1");
        rig.Source.Entries[0] = Rig.Entry("1.10.1"); rig.WriteCurrent("1.10.1");
        var rows = await rig.Read();
        Assert.IsTrue(Row(rows, Path.Combine(rig.Apps, "1.9.999")).CanMove);
        Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, "1.10.1")).CanMove);
        foreach (var version in Old.Append(Current))
        {
            Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, version)).CanMove,
                "every numerically newer-than-current leaf stays retained, not only the highest: " + version);
            Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, version), rig.Fx.Context, rig.Source));
        }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("mismatch")]
    [DataRow("duplicate")]
    [DataRow("dtd")]
    [DataRow("oversized")]
    [DataRow("invalid-utf8")]
    public async Task MissingOrUntrustedProductInfoLeavesAllVersionsVisibleWithoutAction(string mode)
    {
        using var rig = new Rig();
        switch (mode)
        {
            case "missing": File.Delete(rig.ProductInfo); break;
            case "malformed": File.WriteAllText(rig.ProductInfo, "<packet><full_appver"); break;
            case "mismatch": rig.WriteCurrent("8.5.0"); break;
            case "duplicate": File.WriteAllText(rig.ProductInfo, "<packet><full_appver value=\"9.1.0.3879\"/><full_appver value=\"9.1.0.3879\"/></packet>"); break;
            case "dtd": File.WriteAllText(rig.ProductInfo, "<!DOCTYPE packet [<!ENTITY version '9.1.0.3879'>]><packet><full_appver value=\"&version;\"/></packet>"); break;
            case "oversized": File.WriteAllText(rig.ProductInfo, "<packet><!--" + new string('x', 1024 * 1024) + "--><full_appver value=\"9.1.0.3879\"/></packet>"); break;
            case "invalid-utf8":
                // Replacement decoding yields valid XML and matching current metadata; strict decoding must reject it.
                File.WriteAllBytes(rig.ProductInfo, [.. Encoding.UTF8.GetBytes("<packet><!--"), 0xff,
                    .. Encoding.UTF8.GetBytes("--><full_appver value=\"9.1.0.3879\"/></packet>")]);
                break;
            default: Assert.Fail(mode); break;
        }
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    [DataRow("wrong-key")]
    [DataRow("wrong-name")]
    [DataRow("wrong-publisher")]
    [DataRow("machine")]
    [DataRow("system")]
    [DataRow("bad-version")]
    [DataRow("missing-registration")]
    public async Task ExactPerUserCapCutIdentityAndNumericRegisteredVersionAreRequired(string mode)
    {
        using var rig = new Rig();
        var entry = rig.Source.Entries[0];
        rig.Source.Entries[0] = mode switch
        {
            "wrong-key" => entry with { KeyName = "CapCut-decoy" },
            "wrong-name" => entry with { DisplayName = "CapCut Pro" },
            "wrong-publisher" => entry with { Publisher = "Unrelated Publisher" },
            "machine" => entry with { Scope = UninstallScope.PerMachine },
            "system" => entry with { SystemComponent = true },
            "bad-version" => entry with { DisplayVersion = "newest" },
            _ => entry,
        };
        if (mode == "missing-registration") rig.Source.Entries.Clear();
        Blocked(rig, await rig.Read());
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("duplicate")]
    [DataRow("duplicate-spaced")]
    [DataRow("duplicate-tabbed")]
    [DataRow("oversized")]
    public async Task MalformedOrAmbiguousConfigureEvidenceBlocksAction(string mode)
    {
        using var rig = new Rig();
        var value = mode switch
        {
            "malformed" => "[capcut]\r\nlast_version=not-a-version\r\n",
            "duplicate" => "[capcut]\r\nlast_version=8.2.0\r\nlast_version=8.5.0\r\n",
            "duplicate-spaced" => "[capcut]\r\nlast_version=\r\nlast_version =8.2.0\r\n",
            "duplicate-tabbed" => "[capcut]\r\nlast_version=\r\nlast_version\t=8.2.0\r\n",
            _ => "[capcut]\r\nlast_version=\r\n;" + new string('x', 1024 * 1024),
        };
        File.WriteAllText(rig.Configure, value);
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    [DataRow("old")]
    [DataRow("current")]
    [DataRow("launcher")]
    [DataRow("updater")]
    [DataRow("enumeration-failed")]
    public async Task AnyRelatedRunningProcessOrUnreadableProcessInventoryBlocksAllCapCutActions(string mode)
    {
        using var rig = new Rig();
        if (mode == "enumeration-failed") rig.Source.ProcessError = new IOException("process images unavailable");
        else rig.Source.Images.Add(rig.MakeImage(mode switch
        {
            "old" => Path.Combine(rig.Apps, "7.3.0", "CapCut.exe"),
            "current" => Path.Combine(rig.Apps, Current, "CapCut.exe"),
            "launcher" => Path.Combine(rig.Apps, "CapCut.exe"),
            _ => Path.Combine(rig.CapCut, "updater.exe"),
        }));
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    public async Task InventoryFailureBlocksActionsAndAnUnrelatedProcessDoesNot()
    {
        using var rig = new Rig();
        var unrelated = ProfileFixture.MakeFile(Path.Combine(rig.Fx.Profile, "CapCut-decoy", "CapCut.exe"), 4096, 82);
        rig.Source.Images.Add(unrelated);
        Assert.IsTrue(Row(await rig.Read(), Path.Combine(rig.Apps, Old[0])).CanMove, "not a process-name-only block");
        rig.Source.EntryError = new UnauthorizedAccessException("registry view unreadable");
        Blocked(rig, await rig.Read());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task AnyUnreadableUninstallKeyBlocksActionsEvenWhenTheKnownPrimaryKeyIsReadable(bool primary)
    {
        using var rig = new Rig();
        Assert.IsTrue(Row(await rig.Read(), Path.Combine(rig.Apps, Old[0])).CanMove, "control: complete evidence permits old leaf");
        if (primary) rig.Source.Entries[0] = rig.Source.Entries[0] with { ValuesReadable = false };
        else rig.Source.Entries.Add(new UninstallEntry("unreadable-unknown-app", UninstallScope.PerUser,
            RegistryViewKind.Bits32, null, null, null, null, false) with { ValuesReadable = false });
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source),
            "an unreadable key may hide a reference to an approved leaf");
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task RelativeInstallLocationsAndProcessImagesAreUnknownEvidenceRatherThanResolvedAgainstCurrentDirectory(bool process)
    {
        using var rig = new Rig();
        var relative = Path.GetRelativePath(rig.Fx.Profile, Path.Combine(rig.Apps, "8.2.0"));
        Assert.IsFalse(Path.IsPathFullyQualified(relative), "fixture really is relative");
        if (process) rig.Source.Images.Add(Path.Combine(relative, "CapCut.exe"));
        else rig.Source.Entries.Add(Rig.Entry("8.2.0", relative, "CapCut-previous"));
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task MissingInstallLocationOrProcessImageLeavesReferenceEvidenceUnknownAndBlocksAction(bool process)
    {
        using var rig = new Rig();
        var candidate = Path.Combine(rig.Apps, Old[0]);
        Assert.IsTrue(Row(await rig.Read(), candidate).CanMove, "control: complete evidence permits the old leaf");
        var missing = Path.Combine(rig.Fx.Documents, "missing-reference", process ? "app.exe" : "install");
        Assert.IsFalse(File.Exists(missing));
        Assert.IsFalse(Directory.Exists(missing));
        if (process) rig.Source.Images.Add(missing);
        else rig.Source.Entries.Add(Rig.Entry("8.2.0", missing, "CapCut-previous"));
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source),
            "missing reference facts cannot certify an old version is unreferenced");
    }

    [TestMethod]
    public async Task ExtendedLocalProcessImageStillBlocksAllCapCutActions()
    {
        using var rig = new Rig();
        rig.Source.Images.Add(@"\\?\" + rig.MakeImage(Path.Combine(rig.Apps, "8.2.0", "CapCut.exe")));
        Blocked(rig, await rig.Read());
        Assert.IsFalse(InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExtendedLocalOrJunctionAliasReferencesCannotLoseARetainedOldVersion(bool junction)
    {
        using var rig = new Rig();
        var candidate = Path.Combine(rig.Apps, "8.2.0");
        Assert.IsTrue(Row(await rig.Read(), candidate).CanMove, "control: this exact plain leaf is otherwise eligible");
        string reference;
        if (junction)
        {
            var alias = Path.Combine(rig.Fx.Documents, "CapCutAlias");
            Native.CreateJunction(alias, rig.Apps);
            reference = Path.Combine(alias, "8.2.0");
        }
        else reference = @"\\?\" + candidate;
        rig.Source.Entries.Add(Rig.Entry("8.2.0", reference, "CapCut-previous"));
        var rows = await rig.Read();
        var retained = Row(rows, candidate);
        Assert.IsFalse(retained.CanMove, "a differently spelled reference still retains the real version, or blocks uncertainty");
        Assert.AreEqual(rig.OriginalAllocations[candidate], Convert.ToInt64(retained.Bytes, CultureInfo.InvariantCulture));
        Assert.IsFalse(string.IsNullOrWhiteSpace(retained.Reason));
        Assert.IsFalse(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source));
        Assert.IsFalse(Row(rows, Path.Combine(rig.Apps, Current)).CanMove, "current/highest retention remains intact");
    }

    [TestMethod]
    public async Task CanMoveNowReadsChangedCurrentAndProcessEvidenceInsteadOfTrustingPreview()
    {
        using var rig = new Rig();
        var candidate = Path.Combine(rig.Apps, Old[0]);
        Assert.IsTrue(Row(await rig.Read(), candidate).CanMove);
        rig.Source.Entries[0] = Rig.Entry(Old[0]); rig.WriteCurrent(Old[0]);
        Assert.IsFalse(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source), "newly current after preview");
        rig.Source.Entries[0] = Rig.Entry(Current); rig.WriteCurrent(Current);
        Assert.IsTrue(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source));
        rig.Source.Images.Add(rig.MakeImage(Path.Combine(candidate, "CapCut.exe")));
        Assert.IsFalse(InstallerVersions.CanMoveNow(candidate, rig.Fx.Context, rig.Source), "new process after approval");
    }

    [TestMethod]
    public void ExactDirectNumericLeafIsRequiredNeverVendorParentOrUserDataOrNestedPayload()
    {
        using var rig = new Rig();
        var decoys = new[] { rig.CapCut, rig.Apps, Path.Combine(rig.CapCut, "User Data"),
            Path.Combine(rig.Apps, Old[0], "payload.bin"), Path.Combine(rig.Apps, "project.backup"), rig.Fx.LocalAppData };
        Directory.CreateDirectory(decoys[2]); Directory.CreateDirectory(decoys[4]);
        foreach (var path in decoys)
            Assert.IsFalse(InstallerVersions.CanMoveNow(path, rig.Fx.Context, rig.Source), "only exact approved version leaf: " + path);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task JunctionCandidateOrAncestorNeverGrantsAnOutsideRootException(bool ancestor)
    {
        using var rig = new Rig();
        var old = Path.Combine(rig.Apps, Old[0]);
        var outside = Path.Combine(rig.Fx.Documents, "junction-target");
        if (ancestor)
        {
            Directory.Move(rig.CapCut, outside);
            Native.CreateJunction(rig.CapCut, outside);
        }
        else
        {
            Directory.Move(old, outside);
            Native.CreateJunction(old, outside);
        }
        var sentinel = Path.Combine(outside, ancestor ? Path.Combine("Apps", Old[0], "payload.bin") : "payload.bin");
        var before = File.ReadAllBytes(sentinel);
        Assert.IsFalse(InstallerVersions.CanMoveNow(old, rig.Fx.Context, rig.Source));
        var rows = await rig.Read();
        Assert.IsFalse(rows.Any(r => r.CanMove && string.Equals(r.Path, old, StringComparison.OrdinalIgnoreCase)));
        if (!ancestor)
        {
            var unknown = Row(rows, old);
            Assert.IsNull((object?)unknown.Bytes, "a known numeric junction leaf remains visible with unknown size");
            Assert.IsFalse(string.IsNullOrWhiteSpace(unknown.Reason));
            Assert.IsFalse(unknown.CanMove);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(sentinel));
    }

    [TestMethod]
    public async Task UnknownCodexVersionReferencesAreMeasuredIndividuallyAndNeverMadeActionableByHighestOrdering()
    {
        using var fx = new ProfileFixture("asp30-codex-unknown");
        var versions = new[] { "0.160.0", "0.160.1", "0.161.0", "0.163.0" };
        var roots = versions.Select(v => fx.InProfile(".codex", "releases", v)).ToArray();
        var files = roots.Select((r, i) => ProfileFixture.MakeFile(Path.Combine(r, "payload.bin"), (i + 1) * 4096, i + 34)).ToArray();
        var auth = ProfileFixture.MakeFile(fx.InProfile(".codex", "auth.json"), 4096, 99);
        var authBefore = File.ReadAllBytes(auth);
        var source = new Asp30Inventory();
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(roots, rows.Select(r => r.Path).ToArray());
        for (var i = 0; i < roots.Length; i++)
        {
            var row = Row(rows, roots[i]);
            Assert.IsFalse(row.CanMove); Assert.IsFalse(string.IsNullOrWhiteSpace(row.Reason));
            Assert.AreEqual(Native.AllocationSizeOf(files[i]), Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
            Assert.IsFalse(InstallerVersions.CanMoveNow(roots[i], fx.Context, source));
        }
        CollectionAssert.AreEqual(authBefore, File.ReadAllBytes(auth));
    }

    [TestMethod]
    public async Task LarkAppAndNumericVersionFoldersRemainIndividualMeasuredGuideOnlyArtifacts()
    {
        using var fx = new ProfileFixture("asp30-lark-versions");
        var root = Path.Combine(fx.LocalAppData, "Lark");
        var paths = new[] { Path.Combine(root, "app"), Path.Combine(root, "versions", "7.68"), Path.Combine(root, "versions", "7.74.22") };
        var versions = new[] { Text.Unknown, "7.68", "7.74.22" };
        var files = paths.Select((path, i) => ProfileFixture.MakeFile(Path.Combine(path, "payload.bin"), (i + 2) * 4096, i + 61)).ToArray();
        var expectedBytes = files.Select(Native.AllocationSizeOf).ToArray();
        var before = files.Select(File.ReadAllBytes).ToArray();
        var account = ProfileFixture.MakeFile(Path.Combine(root, "User Data", "account.bin"), 4096, 65);
        var accountBefore = File.ReadAllBytes(account);
        var source = new Asp30Inventory();
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(paths, rows.Select(r => r.Path).ToArray(), "individual version artifacts, never the Lark or User Data parent");
        for (var i = 0; i < paths.Length; i++)
        {
            var row = Row(rows, paths[i]);
            Assert.AreEqual("Lark", row.AppName);
            Assert.AreEqual(versions[i], row.Version);
            Assert.AreEqual(expectedBytes[i], Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
            Assert.IsFalse(row.CanMove, "numeric ordering without current-reference evidence grants no Lark cleanup");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Reason));
            Assert.IsFalse(InstallerVersions.CanMoveNow(paths[i], fx.Context, source));
            CollectionAssert.AreEqual(before[i], File.ReadAllBytes(files[i]));
        }
        CollectionAssert.AreEqual(accountBefore, File.ReadAllBytes(account));
    }

    [TestMethod]
    public async Task LarkInternationalDownloadingArtifactIsAttributedMeasuredAndPendingGuideOnly()
    {
        using var fx = new ProfileFixture("asp30-lark-download");
        var path = Path.Combine(fx.LocalAppData, "LarkInternational", "update", "update_downloading");
        var file = ProfileFixture.MakeFile(Path.Combine(path, "download.bin"), 16384, 71);
        var expectedBytes = Native.AllocationSizeOf(file);
        var before = File.ReadAllBytes(file);
        var source = new Asp30Inventory();
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { path }, rows.Select(r => r.Path).ToArray());
        var row = Row(rows, path);
        Assert.AreEqual("LarkInternational", row.AppName);
        Assert.AreEqual(Text.Unknown, row.Version);
        Assert.AreEqual(expectedBytes, Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
        Assert.IsFalse(row.CanMove);
        Assert.IsTrue(row.Reason.Contains("pending", StringComparison.OrdinalIgnoreCase), "download state is disclosed, never called proven redundant");
        Assert.IsFalse(InstallerVersions.CanMoveNow(path, fx.Context, source));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
    }

    [TestMethod]
    public async Task ElectronUpdaterMetadataAttributesOnlyItsExactPendingFolderWithoutGrantingMoveAuthority()
    {
        using var fx = new ProfileFixture("asp30-updater-artifact");
        var install = Path.Combine(fx.LocalAppData, "Programs", "AcmeTool");
        var metadata = Path.Combine(install, "resources", "app-update.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(metadata, "provider: generic\nupdaterCacheDirName: acme-tool-updater\n");
        var metadataBefore = File.ReadAllBytes(metadata);
        var pending = Path.Combine(fx.LocalAppData, "acme-tool-updater", "pending");
        var file = ProfileFixture.MakeFile(Path.Combine(pending, "installer.exe"), 20480, 75);
        var expectedBytes = Native.AllocationSizeOf(file);
        var before = File.ReadAllBytes(file);
        var decoy = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "unattributed-updater", "pending", "installer.exe"), 8192, 76);
        var decoyBefore = File.ReadAllBytes(decoy);
        var source = new Asp30Inventory();
        source.Entries.Add(new UninstallEntry("AcmeTool", UninstallScope.PerUser, RegistryViewKind.Bits64,
            "Acme Developer Tool", "Acme", "3.0.0", install, false));
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { pending }, rows.Select(r => r.Path).ToArray(), "metadata identifies the exact artifact; folder-name guesses add nothing");
        var row = Row(rows, pending);
        Assert.AreEqual("Acme Developer Tool", row.AppName);
        Assert.AreEqual(Text.Unknown, row.Version, "installed app version does not identify its downloaded installer version");
        Assert.AreEqual(expectedBytes, Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
        Assert.IsFalse(row.CanMove);
        Assert.IsTrue(row.Reason.Contains("pending", StringComparison.OrdinalIgnoreCase), "possible pending update is retained");
        Assert.IsFalse(InstallerVersions.CanMoveNow(pending, fx.Context, source));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        CollectionAssert.AreEqual(metadataBefore, File.ReadAllBytes(metadata));
        CollectionAssert.AreEqual(decoyBefore, File.ReadAllBytes(decoy));
    }

    [TestMethod]
    public async Task SharedUpdaterFolderHasOneAmbiguousOwnerIndependentOfInventoryOrder()
    {
        using var fx = new ProfileFixture("asp30-shared-updater");
        var source = new Asp30Inventory();
        var metadata = new List<string>();
        foreach (var name in new[] { "Alpha Tool", "Beta Tool" })
        {
            var install = Path.Combine(fx.LocalAppData, "Programs", name);
            var yml = Path.Combine(install, "resources", "app-update.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(yml)!);
            File.WriteAllText(yml, "updaterCacheDirName: shared-tool-updater\n");
            metadata.Add(yml);
            source.Entries.Add(new UninstallEntry(name, UninstallScope.PerUser, RegistryViewKind.Bits64,
                name, "Fixture Publisher", "3.0.0", install, false));
        }
        var pending = Path.Combine(fx.LocalAppData, "shared-tool-updater", "pending");
        var file = ProfileFixture.MakeFile(Path.Combine(pending, "installer.exe"), 12288, 83);
        var expectedBytes = Native.AllocationSizeOf(file);
        var before = File.ReadAllBytes(file);
        var metadataBefore = metadata.Select(File.ReadAllBytes).ToArray();
        string? firstName = null;
        for (var pass = 0; pass < 2; pass++)
        {
            var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { pending }, rows.Select(r => r.Path).ToArray(), "a shared artifact occupies disk once");
            var row = Row(rows, pending);
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.AppName));
            Assert.AreNotEqual("Alpha Tool", row.AppName, "shared ownership is not assigned to one app");
            Assert.AreNotEqual("Beta Tool", row.AppName, "shared ownership is not assigned to one app");
            Assert.IsTrue(row.Reason.Contains("shared", StringComparison.OrdinalIgnoreCase)
                || row.Reason.Contains("ambig", StringComparison.OrdinalIgnoreCase), "ownership ambiguity must be disclosed");
            Assert.AreEqual(expectedBytes, Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
            Assert.IsFalse(row.CanMove);
            Assert.IsFalse(InstallerVersions.CanMoveNow(pending, fx.Context, source));
            if (firstName is null) firstName = row.AppName;
            else Assert.AreEqual(firstName, row.AppName, "registry enumeration order cannot choose the owner");
            source.Entries.Reverse();
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        for (var i = 0; i < metadata.Count; i++) CollectionAssert.AreEqual(metadataBefore[i], File.ReadAllBytes(metadata[i]));
    }

    [TestMethod]
    public async Task InvalidUtf8UpdaterMetadataDoesNotHideALaterValidAppsPendingArtifact()
    {
        using var fx = new ProfileFixture("asp30-updater-invalid-encoding");
        var source = new Asp30Inventory();
        var metadata = new List<string>();
        var payloads = new List<string>();
        foreach (var name in new[] { "broken", "valid" })
        {
            var install = Path.Combine(fx.LocalAppData, "Programs", name);
            var yml = Path.Combine(install, "resources", "app-update.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(yml)!);
            var yaml = Encoding.UTF8.GetBytes("updaterCacheDirName: " + name + "-updater\n# comment ");
            byte[] content = name == "broken" ? [.. yaml, 0xff, 0x0a] : [.. yaml, 0x0a];
            File.WriteAllBytes(yml, content);
            metadata.Add(yml);
            payloads.Add(ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, name + "-updater", "pending", "installer.exe"), 8192, 85));
            source.Entries.Add(new UninstallEntry(name, UninstallScope.PerUser, RegistryViewKind.Bits64,
                name + " Tool", "Fixture Publisher", "3.0.0", install, false));
        }
        var metadataBefore = metadata.Select(File.ReadAllBytes).ToArray();
        var payloadBefore = payloads.Select(File.ReadAllBytes).ToArray();
        var expectedBytes = Native.AllocationSizeOf(payloads[1]);
        var pending = Path.GetDirectoryName(payloads[1])!;
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { pending }, rows.Select(r => r.Path).ToArray(),
            "invalid first entry grants no attribution and does not abort independent later entries");
        var row = Row(rows, pending);
        Assert.AreEqual("valid Tool", row.AppName);
        Assert.AreEqual(expectedBytes, Convert.ToInt64(row.Bytes, CultureInfo.InvariantCulture));
        Assert.IsFalse(row.CanMove);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.Reason));
        for (var i = 0; i < metadata.Count; i++)
        {
            CollectionAssert.AreEqual(metadataBefore[i], File.ReadAllBytes(metadata[i]));
            CollectionAssert.AreEqual(payloadBefore[i], File.ReadAllBytes(payloads[i]));
        }
    }

    [TestMethod]
    [DataRow("\"quote-updater")]
    [DataRow("'quote-updater")]
    [DataRow("\"quote-updater'")]
    public async Task MalformedQuotedUpdaterScalarNeverAttributesARealPendingFolder(string scalar)
    {
        using var fx = new ProfileFixture("asp30-updater-bad-quote");
        var install = Path.Combine(fx.LocalAppData, "Programs", "QuotedTool");
        var yml = Path.Combine(install, "resources", "app-update.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(yml)!);
        File.WriteAllText(yml, "updaterCacheDirName: " + scalar + "\n");
        var metadataBefore = File.ReadAllBytes(yml);
        var pending = Path.Combine(fx.LocalAppData, "quote-updater", "pending");
        var file = ProfileFixture.MakeFile(Path.Combine(pending, "installer.exe"), 8192, 87);
        var before = File.ReadAllBytes(file);
        var source = new Asp30Inventory();
        source.Entries.Add(new UninstallEntry("QuotedTool", UninstallScope.PerUser, RegistryViewKind.Bits64,
            "Quoted Tool", "Fixture Publisher", "3.0.0", install, false));
        var rows = await InstallerVersions.ReadAsync(fx.Context, source, CancellationToken.None);
        Assert.AreEqual(0, rows.Count, "trimming unmatched or mismatched quotes must not manufacture trustworthy attribution");
        Assert.IsFalse(InstallerVersions.CanMoveNow(pending, fx.Context, source));
        CollectionAssert.AreEqual(metadataBefore, File.ReadAllBytes(yml));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
    }

    [TestMethod]
    public async Task VersionDiscoveryDoesNotModifyProjectAccountOrVersionPayloadFiles()
    {
        using var rig = new Rig();
        var files = new[] { ProfileFixture.MakeFile(Path.Combine(rig.CapCut, "User Data", "Projects", "project.bin"), 4096, 44),
            ProfileFixture.MakeFile(Path.Combine(rig.CapCut, "User Data", "Config", "account.bin"), 4096, 45),
            Path.Combine(rig.Apps, Old[0], "payload.bin") };
        var before = files.Select(File.ReadAllBytes).ToArray();
        _ = await rig.Read();
        _ = InstallerVersions.CanMoveNow(Path.Combine(rig.Apps, Old[0]), rig.Fx.Context, rig.Source);
        for (var i = 0; i < files.Length; i++) CollectionAssert.AreEqual(before[i], File.ReadAllBytes(files[i]));
    }

    [TestMethod]
    public async Task CancelledVersionDiscoveryPropagatesInsteadOfReturningAnEligiblePartialList()
    {
        using var rig = new Rig();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { _ = await InstallerVersions.ReadAsync(rig.Fx.Context, rig.Source, cancelled.Token); Assert.Fail("caller cancellation swallowed"); }
        catch (OperationCanceledException) { }
    }
}

internal sealed class Asp30Inventory : IUninstallSource
{
    public List<UninstallEntry> Entries { get; } = [];
    public List<string> Images { get; } = [];
    public Exception? EntryError { get; set; }
    public Exception? ProcessError { get; set; }
    public IReadOnlyList<UninstallEntry> ReadEntries() { if (EntryError is { } error) throw error; return Entries.ToArray(); }
    public IReadOnlyList<StorePackage> ReadStorePackages() => [];
    public IReadOnlyList<string> ReadProcessImagePaths() { if (ProcessError is { } error) throw error; return Images.ToArray(); }
}
