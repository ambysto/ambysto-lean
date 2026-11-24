using System;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>docs/spec/04 "Inventory", Windows note: the registered uninstallers of both hives and both registry views;
/// system components hidden; per-machine entries "needs admin"; Store (MSIX) apps display-only; Lean never runs an
/// uninstaller: "Uninstall…" opens Settings &gt; Apps and names the entry to remove.</summary>
[TestClass]
// Writes the real HKCU (scratch keys, or the Uninstall key's ACL): kept out of parallel mutation runs, which can stop a
// test between its change and its clean-up (2026-10-18: a stopped run left HKCU\...\Uninstall unreadable).
[TestCategory("SharedState")]
public sealed class InventoryTests
{
    // docs/spec/04 Windows note: entries marked as system components are hidden.
    [TestMethod]
    public void SystemComponentsAreHidden()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{A}", "Contoso Notes", @"C:\Users\me\AppData\Local\Programs\Notes"));
        source.Entries.Add(FakeUninstallSource.Entry("{B}", "Microsoft Visual C++ Runtime", @"C:\Program Files\Common", UninstallScope.PerMachine, systemComponent: true));
        source.Entries.Add(FakeUninstallSource.Entry("{C}", "Hidden Helper", null, systemComponent: true));
        var apps = UninstallInventory.Read(source);
        Check.Sequence(apps.Select(a => a.DisplayName).ToArray(), "Contoso Notes");
    }

    // docs/spec/04 Windows note: an entry without a display name (missing or blank) is not listed.
    [TestMethod]
    public void EntriesWithoutADisplayNameAreHidden()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{A}", "Contoso Notes", @"C:\Users\me\AppData\Local\Programs\Notes"));
        source.Entries.Add(FakeUninstallSource.Entry("{N}", null, @"C:\Users\me\AppData\Local\Programs\Nameless"));
        source.Entries.Add(FakeUninstallSource.Entry("{W}", "  ", @"C:\Users\me\AppData\Local\Programs\Blank"));
        var apps = UninstallInventory.Read(source);
        Check.Sequence(apps.Select(a => a.DisplayName).ToArray(), "Contoso Notes");
    }

    // docs/spec/04 Windows note: per-machine entries are "needs admin"; per-user entries hand off to Settings.
    [TestMethod]
    public void PerMachineIsNeedsAdminAndPerUserIsPerUser()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{U}", "User App", @"C:\Users\me\AppData\Local\Programs\UserApp"));
        source.Entries.Add(FakeUninstallSource.Entry("{M}", "Machine App", @"C:\Program Files\MachineApp", UninstallScope.PerMachine));
        var apps = UninstallInventory.Read(source).ToDictionary(a => a.DisplayName, a => a, StringComparer.Ordinal);
        Assert.AreEqual(AppClass.PerUser, apps["User App"].Class);
        Assert.AreEqual(AppClass.NeedsAdmin, apps["Machine App"].Class);
        Assert.IsNotNull(apps["Machine App"].Entry);
        Assert.IsNull(apps["Machine App"].Package);
    }

    // docs/spec/04 Windows note: both registry views are read (a 32-bit entry is listed too).
    [TestMethod]
    public void EntriesOfBothRegistryViewsAreListed()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{64}", "Sixty-four", @"C:\Program Files\S64", UninstallScope.PerMachine, RegistryViewKind.Bits64));
        source.Entries.Add(FakeUninstallSource.Entry("{32}", "Thirty-two", @"C:\Program Files (x86)\S32", UninstallScope.PerMachine, RegistryViewKind.Bits32));
        source.Entries.Add(FakeUninstallSource.Entry("{U32}", "User thirty-two", @"C:\Users\me\AppData\Local\U32", UninstallScope.PerUser, RegistryViewKind.Bits32));
        var apps = UninstallInventory.Read(source);
        Check.Set(apps.Select(a => a.DisplayName).ToArray(), "Sixty-four", "Thirty-two", "User thirty-two");
    }

    // docs/spec/04 Windows note: Store (MSIX) apps are listed display-only.
    [TestMethod]
    public void StoreAppsAreListedDisplayOnly()
    {
        var source = new FakeUninstallSource();
        source.StorePackages.Add(new StorePackage("Contoso.Paint_1.2.3.0_x64__8wekyb3d8bbwe", "Contoso Paint", "Contoso", @"C:\Program Files\WindowsApps\Contoso.Paint_1.2.3.0_x64__8wekyb3d8bbwe"));
        var apps = UninstallInventory.Read(source);
        Assert.AreEqual(1, apps.Count);
        Assert.AreEqual(AppClass.StoreApp, apps[0].Class);
        Assert.AreEqual("Contoso Paint", apps[0].DisplayName);
        Assert.IsNotNull(apps[0].Package);
        Assert.IsNull(apps[0].Entry);
    }

    // docs/spec/04 Inventory: for each app: name, publisher, version, install location (what the Windows inventory has).
    [TestMethod]
    public void AppsCarryNamePublisherVersionAndInstallLocation()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(new UninstallEntry("{K}", UninstallScope.PerUser, RegistryViewKind.Bits64, "Contoso Notes", "Contoso Ltd", "4.2.1", @"C:\Users\me\AppData\Local\Programs\Notes", false));
        var app = UninstallInventory.Read(source).Single();
        Assert.AreEqual("Contoso Notes", app.DisplayName);
        Assert.AreEqual("Contoso Ltd", app.Publisher);
        Assert.AreEqual("4.2.1", app.Version);
        Assert.AreEqual(@"C:\Users\me\AppData\Local\Programs\Notes", app.InstallLocation);
        Assert.AreEqual(source.Entries[0], app.Entry);
    }

    // Plan "Public surface": the inventory is sorted by display name.
    [TestMethod]
    public void InventoryIsSortedByDisplayName()
    {
        var source = new FakeUninstallSource();
        source.Entries.Add(FakeUninstallSource.Entry("{1}", "zeta", null));
        source.Entries.Add(FakeUninstallSource.Entry("{2}", "Alpha", null));
        source.Entries.Add(FakeUninstallSource.Entry("{3}", "beta", null, UninstallScope.PerMachine));
        source.StorePackages.Add(new StorePackage("Contoso.Gamma_1.0_x64__abc", "Gamma", null, @"C:\Program Files\WindowsApps\Contoso.Gamma_1.0_x64__abc"));
        var names = UninstallInventory.Read(source).Select(a => a.DisplayName).ToArray();
        CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), names);
    }

    // docs/spec/04 Windows note: "Uninstall…" opens Settings > Apps (the list page) and names the entry to remove.
    [TestMethod]
    public void HandOffOpensTheAppsListPageAndNamesTheEntry()
    {
        Assert.AreEqual(new Uri("ms-settings:appsfeatures"), SettingsHandOff.AppsPage);
        var app = new InstalledApp("Contoso Notes", "Contoso", "1.0", @"C:\Users\me\AppData\Local\Programs\Notes", AppClass.PerUser, null, null);
        var instruction = SettingsHandOff.Instruction(app);
        Assert.AreEqual(Text.UninstallInSettings("Contoso Notes"), instruction);
        Check.DoesNotContain(instruction, "Finder");
    }

    // docs/spec/04 Windows note: a "needs admin" entry is uninstalled the same way (hand-off still names it; no password asked).
    [TestMethod]
    public void HandOffNamesANeedsAdminEntryToo()
    {
        var app = new InstalledApp("Machine App", "Contoso", "1.0", @"C:\Program Files\MachineApp", AppClass.NeedsAdmin, null, null);
        Check.Contains(SettingsHandOff.Instruction(app), "Machine App");
    }

    // docs/spec/04 Windows note: both registry views are read, and HKCU\...\Uninstall is one key shared by both views: a
    // per-user entry is listed once, not once per view. Real inventory, a scratch per-user entry that the test removes.
    // Not parallel: the entry is visible to every real inventory read on this machine while it exists.
    [TestMethod]
    [DoNotParallelize]
    public void PerUserEntryIsListedOnceByTheRealInventory()
    {
        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var name = "AmbystoLeanTest-" + Guid.NewGuid().ToString("D");
        var displayName = "Ambysto Lean Test App " + Guid.NewGuid().ToString("N");
        var keyPath = UninstallPath + @"\" + name;
        using (var before = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            Assert.IsNull(before, "the scratch key existed before the test");
        }

        bool ParentExists()
        {
            using var parent = Registry.CurrentUser.OpenSubKey(UninstallPath);
            return parent is not null;
        }

        // CreateSubKey also creates a missing Uninstall key; the test removes it again if it made it and it is empty.
        var parentExisted = ParentExists();
        var bodyCompleted = false;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayName", displayName);
                key.SetValue("InstallLocation", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\" + name);
            }

            // The fact the bug came from: the 32-bit view of HKCU is the same key. If this is ever false, the test below
            // proves nothing, so it fails here instead of passing vacuously.
            using (var base32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
            using (var seen = base32.OpenSubKey(keyPath))
            {
                Assert.IsNotNull(seen, "the 32-bit view of HKCU does not show the entry: the test cannot detect a double listing");
            }

            var apps = UninstallInventory.Read();
            var mine = apps.Where(a => a.Entry is not null && string.Equals(a.Entry.KeyName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, mine.Length, $"the per-user entry {name} was listed {mine.Length} times");
            Assert.AreEqual(UninstallScope.PerUser, mine[0].Entry!.Scope);
            Assert.AreEqual(AppClass.PerUser, mine[0].Class);
            Assert.AreEqual(displayName, mine[0].DisplayName);

            var repeated = apps.Where(a => a.Entry is { Scope: UninstallScope.PerUser })
                .GroupBy(a => a.Entry!.KeyName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key + " x" + g.Count())
                .ToArray();
            Check.True(repeated.Length == 0, "per-user entries listed more than once: " + string.Join(", ", repeated));
            bodyCompleted = true;
        }
        finally
        {
            if (bodyCompleted)
            {
                Cleanup();
                using (var after = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    Assert.IsNull(after, "the scratch key was not removed");
                }

                Assert.AreEqual(parentExisted, ParentExists(), "the Uninstall key is not as the test found it");
            }
            else
            {
                try
                {
                    Cleanup(); // a failure of the body is the one to report, so a failing cleanup must not replace it
                }
                catch (Exception)
                {
                }
            }
        }

        void Cleanup()
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            if (parentExisted)
            {
                return;
            }

            using (var parent = Registry.CurrentUser.OpenSubKey(UninstallPath))
            {
                if (parent is null || parent.SubKeyCount != 0 || parent.ValueCount != 0)
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKey(UninstallPath, throwOnMissingSubKey: false);
        }
    }

    // docs/spec/04 Inventory on the real machine: reads only, and never throws for the standard user.
    [TestMethod]
    public void RealInventoryReadsWithoutFailing()
    {
        var apps = UninstallInventory.Read();
        Assert.IsNotNull(apps);
        foreach (var app in apps)
        {
            Check.True(!string.IsNullOrWhiteSpace(app.DisplayName), "an app without a display name");
            if (app.Entry is not null)
            {
                Assert.IsFalse(app.Entry.SystemComponent, app.DisplayName + " is a system component");
            }
        }
    }
}
