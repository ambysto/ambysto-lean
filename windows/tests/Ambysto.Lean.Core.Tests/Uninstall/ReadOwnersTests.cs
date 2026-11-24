using System;
using System.Linq;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Plan group 11 UI contract, "Leftover inventory": the matcher is given every entry the source lists, hidden
/// ones (system components, entries without a display name) included, plus the Store packages.
/// <see cref="UninstallInventory.ReadOwners"/> maps every entry as <see cref="UninstallInventory.Read(IUninstallSource)"/>
/// does but hides none.</summary>
[TestClass]
public sealed class ReadOwnersTests
{
    private static FakeUninstallSource SourceWithHiddenEntries()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{U}", "Contoso Notes", @"C:\Users\me\AppData\Local\Programs\Notes"));
        source.Entries.Add(FakeUninstallSource.Entry("{M}", "Machine App", @"C:\Program Files\MachineApp", UninstallScope.PerMachine));
        source.Entries.Add(FakeUninstallSource.Entry("{S}", "Contoso Runtime", @"C:\Users\me\AppData\Local\Contoso\Runtime", systemComponent: true));
        source.Entries.Add(FakeUninstallSource.Entry("{N}", null, @"C:\Users\me\AppData\Local\Programs\Nameless"));
        source.Entries.Add(FakeUninstallSource.Entry("{W}", "  ", @"C:\Users\me\AppData\Local\Programs\Blank", UninstallScope.PerUser, RegistryViewKind.Bits32));
        source.StorePackages.Add(new StorePackage("Contoso.Paint_1.2.3.0_x64__8wekyb3d8bbwe", "Contoso Paint", "Contoso", @"C:\Program Files\WindowsApps\Contoso.Paint_1.2.3.0_x64__8wekyb3d8bbwe"));
        return source;
    }

    // Contract: hidden entries are included: system components and entries without a display name (missing or blank).
    [TestMethod]
    public void EveryEntryIsIncludedHiddenOnesToo()
    {
        var source = SourceWithHiddenEntries();
        var owners = UninstallInventory.ReadOwners(source);
        var keys = owners.Where(a => a.Entry is not null).Select(a => a.Entry!.KeyName).ToArray();
        Check.Set(keys, "{U}", "{M}", "{S}", "{N}", "{W}");
        // Each entry is carried as read, so the matcher sees its install location and publisher.
        foreach (var entry in source.Entries)
        {
            var app = owners.Single(a => a.Entry is not null && a.Entry.KeyName == entry.KeyName);
            Assert.AreSame(entry, app.Entry, entry.KeyName + ": the entry record is carried as read");
            Assert.AreEqual(entry.InstallLocation, app.InstallLocation, entry.KeyName + ": install location");
            Assert.AreEqual(entry.Publisher, app.Publisher, entry.KeyName + ": publisher");
        }
    }

    // Contract: plus the Store packages.
    [TestMethod]
    public void StorePackagesAreIncluded()
    {
        var source = SourceWithHiddenEntries();
        var owners = UninstallInventory.ReadOwners(source);
        var store = owners.Where(a => a.Package is not null).ToArray();
        Assert.AreEqual(1, store.Length, "Store packages");
        Assert.AreEqual(AppClass.StoreApp, store[0].Class);
        Assert.AreEqual(@"C:\Program Files\WindowsApps\Contoso.Paint_1.2.3.0_x64__8wekyb3d8bbwe", store[0].InstallLocation);
        Assert.AreEqual(source.Entries.Count + source.StorePackages.Count, owners.Count, "every entry and every package, none hidden, none twice");
    }

    // Contract: "maps every entry as Read does": the entries Read lists come out of ReadOwners the same.
    [TestMethod]
    public void TheEntriesReadListsAreMappedTheSame()
    {
        var source = SourceWithHiddenEntries();
        var shown = UninstallInventory.Read(source);
        var owners = UninstallInventory.ReadOwners(source);
        foreach (var app in shown)
        {
            var match = owners.SingleOrDefault(o => (app.Entry is not null && o.Entry is not null && o.Entry.KeyName == app.Entry.KeyName)
                                                    || (app.Package is not null && o.Package is not null && o.Package.FullName == app.Package.FullName));
            Assert.IsNotNull(match, app.DisplayName + " is missing from ReadOwners");
            Assert.AreEqual(app, match, app.DisplayName + ": mapped differently from Read");
        }

        var machine = owners.Single(o => o.Entry?.KeyName == "{M}");
        Assert.AreEqual(AppClass.NeedsAdmin, machine.Class, "a per-machine entry is needs-admin, as in Read");
        var system = owners.Single(o => o.Entry?.KeyName == "{S}");
        Assert.AreEqual(AppClass.PerUser, system.Class, "a hidden per-user entry is classed per-user, as Read classes by scope");
    }

    // ReadOwners only reads: it lists the source once per call and changes nothing in it.
    [TestMethod]
    public void ReadOwnersReadsTheSourceAndChangesNothing()
    {
        var source = SourceWithHiddenEntries();
        var before = source.Entries.ToArray();
        _ = UninstallInventory.ReadOwners(source);
        CollectionAssert.AreEqual(before, source.Entries.ToArray());
        Assert.IsTrue(source.Reads >= 1, "the entries were not read");
        Assert.ThrowsExactly<ArgumentNullException>(() => UninstallInventory.ReadOwners(null!));
    }
}
