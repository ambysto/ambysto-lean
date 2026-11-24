using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Group 13 finding (2026-10-17): Windows Installer lists a product installed for one user under the machine
/// hive's Uninstall key. Such an entry is labelled per-user, not "needs admin"; its scope (the entry's identity for "app
/// is gone") stays the hive's. Main session, with the fix.</summary>
[TestClass]
public sealed class PerUserInstallerTests
{
    [TestMethod]
    public void TheProductCodeIsPackedAsWindowsInstallerStoresIt()
    {
        // Measured in the test machine's registry with Lean's own installer.
        Assert.AreEqual("996EDEBDBF8951F47B6AF6817EE030D7", RegistryUninstallSource.PackedProductCode("{DBEDE699-98FB-4F15-B7A6-6F18E70E037D}"));
        Assert.AreEqual("996EDEBDBF8951F47B6AF6817EE030D7", RegistryUninstallSource.PackedProductCode("{dbede699-98fb-4f15-b7a6-6f18e70e037d}"));
        Assert.IsNull(RegistryUninstallSource.PackedProductCode("Mozilla Firefox 131.0 (x64 en-US)"));
        Assert.IsNull(RegistryUninstallSource.PackedProductCode("DBEDE699-98FB-4F15-B7A6-6F18E70E037D"));
    }

    [TestMethod]
    public void AMachineHiveEntryInstalledForTheUserIsNotNeedsAdmin()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{A}", "Per-user MSI", @"C:\Users\me\AppData\Local\Programs\A", UninstallScope.PerMachine) with { InstalledForCurrentUser = true });
        source.Entries.Add(FakeUninstallSource.Entry("{B}", "Machine MSI", @"C:\Program Files\B", UninstallScope.PerMachine));

        var apps = UninstallInventory.Read(source);

        Assert.AreEqual(AppClass.PerUser, Single(apps, "Per-user MSI").Class);
        Assert.AreEqual(AppClass.NeedsAdmin, Single(apps, "Machine MSI").Class);
    }

    private static InstalledApp Single(System.Collections.Generic.IReadOnlyList<InstalledApp> apps, string name) =>
        System.Linq.Enumerable.Single(apps, a => a.DisplayName == name);
}
