using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Gap briefs H22-H25 (Stryker run 1): the real uninstall source (<see cref="RegistryUninstallSource"/>), read
/// against independent reads by the test. docs/spec/04 Windows note: the inventory is "the list of registered
/// uninstallers (Uninstall keys in both hives and both registry views)"; Store (MSIX) apps are listed display-only; an app
/// is gone only when no entry (hidden ones included), no Store package and no process names it; what the source cannot
/// read is an error, never an empty list. Scratch keys go under HKCU's Uninstall key only, are named
/// <c>AmbystoLeanTest-&lt;guid&gt;</c>, and are removed (with any ACL the test set) afterwards; no real entry is touched.
/// Not in parallel: the scratch entries are visible to every real inventory read while they exist.</summary>
[TestClass]
// Writes the real HKCU (scratch keys, or the Uninstall key's ACL): kept out of parallel mutation runs, which can stop a
// test between its change and its clean-up (2026-10-18: a stopped run left HKCU\...\Uninstall unreadable).
[TestCategory("SharedState")]
[DoNotParallelize]
public sealed partial class RealSourceTests
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Scratch per-user uninstall keys, removed on dispose (the Uninstall key too, when the test made it and it
    /// is empty again).</summary>
    private sealed class ScratchEntries : IDisposable
    {
        private readonly bool parentExisted;
        private readonly List<string> made = [];

        public ScratchEntries()
        {
            using var parent = Registry.CurrentUser.OpenSubKey(UninstallPath);
            parentExisted = parent is not null;
        }

        public string Make(Action<RegistryKey>? values = null)
        {
            var name = "AmbystoLeanTest-" + Guid.NewGuid().ToString("D");
            using (var existing = Registry.CurrentUser.OpenSubKey(UninstallPath + @"\" + name))
            {
                Assert.IsNull(existing, "the scratch key existed before the test");
            }

            made.Add(name);
            using var key = Registry.CurrentUser.CreateSubKey(UninstallPath + @"\" + name);
            values?.Invoke(key);
            return name;
        }

        /// <summary>Denies this user reading the values and subkeys of scratch key <paramref name="name"/>.</summary>
        public static void DenyRead(string name) => ChangeDeny(name, add: true);

        private static void ChangeDeny(string name, bool add)
        {
            var me = WindowsIdentity.GetCurrent().User!;
            using var key = Registry.CurrentUser.OpenSubKey(UninstallPath + @"\" + name, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
            if (key is null)
            {
                return;
            }

            var security = key.GetAccessControl(AccessControlSections.Access);
            var rule = new RegistryAccessRule(me, RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
            if (add)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRuleAll(new RegistryAccessRule(me, RegistryRights.QueryValues, AccessControlType.Deny));
            }

            key.SetAccessControl(security);
        }

        public void Dispose()
        {
            foreach (var name in made)
            {
                ChangeDeny(name, add: false);
                Registry.CurrentUser.DeleteSubKeyTree(UninstallPath + @"\" + name, throwOnMissingSubKey: false);
            }

            if (!parentExisted)
            {
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
    }

    /// <summary>The real entries, with no Store packages and no processes: isolates the entry part for a watcher.</summary>
    private sealed class EntriesOnlySource(IUninstallSource real) : IUninstallSource
    {
        public IReadOnlyList<UninstallEntry> ReadEntries() => real.ReadEntries();

        public IReadOnlyList<StorePackage> ReadStorePackages() => [];

        public IReadOnlyList<string> ReadProcessImagePaths() => [];
    }

    private static bool CheckFor(AppGoneWatcher watcher, FakeTimeProvider time, int seconds)
    {
        var gone = watcher.Check();
        for (var elapsed = 0; elapsed < seconds; elapsed += 2)
        {
            time.Advance(AppGoneWatcher.CheckInterval);
            gone = watcher.Check();
        }

        return gone;
    }

    private static HashSet<string> SubKeyNames(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath);
        return uninstall is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(uninstall.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
    }

    private static void SameNames(HashSet<string> expected, IEnumerable<string> actual, string what)
    {
        var got = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
        var missing = expected.Except(got, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = got.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
        Check.True(missing.Count == 0 && extra.Count == 0, $"{what}: missing [{string.Join(", ", missing)}], not in the registry [{string.Join(", ", extra)}]");
    }

    // Gap brief H22. docs/spec/04 Windows note: "the list of registered uninstallers (Uninstall keys in both hives and
    // both registry views)"; hidden entries (system components, no display name) still count for "app is gone".
    // (values) a per-user key is read once, per-user, with every value; (systemComponent) which values mark a system
    // component; (unreadable key) a key this user may not read is still reported by its name, hidden and without values,
    // and an app watched by that key name never becomes gone; (views) every key of the machine hive in each view, and of
    // the user's hive, is listed.
    [TestMethod]
    [DataRow("values")]
    [DataRow("systemComponent")]
    [DataRow("unreadable key")]
    [DataRow("views")]
    public void RealSourceListsEveryUninstallKeyWithItsValues(string part)
    {
        var source = new RegistryUninstallSource();
        using var scratch = new ScratchEntries();
        switch (part)
        {
            case "values":
            {
                var name = scratch.Make(key =>
                {
                    key.SetValue("DisplayName", "Ambysto Lean Test App");
                    key.SetValue("Publisher", "Ambysto Test Publisher");
                    key.SetValue("DisplayVersion", "9.8.7");
                    key.SetValue("InstallLocation", @"C:\Users\me\AppData\Local\Programs\AmbystoLeanTest");
                });
                var mine = source.ReadEntries().Where(e => string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.AreEqual(1, mine.Count, "the per-user key is listed once");
                Assert.AreEqual(UninstallScope.PerUser, mine[0].Scope);
                Assert.AreEqual("Ambysto Lean Test App", mine[0].DisplayName);
                Assert.AreEqual("Ambysto Test Publisher", mine[0].Publisher);
                Assert.AreEqual("9.8.7", mine[0].DisplayVersion);
                Assert.AreEqual(@"C:\Users\me\AppData\Local\Programs\AmbystoLeanTest", mine[0].InstallLocation);
                Assert.IsFalse(mine[0].SystemComponent);
                break;
            }

            case "systemComponent":
            {
                var rows = new (string Row, Action<RegistryKey>? Value, bool Expected)[]
                {
                    ("DWORD 1", k => k.SetValue("SystemComponent", 1, RegistryValueKind.DWord), true),
                    ("DWORD 0", k => k.SetValue("SystemComponent", 0, RegistryValueKind.DWord), false),
                    ("string 0", k => k.SetValue("SystemComponent", "0", RegistryValueKind.String), false),
                    ("string x", k => k.SetValue("SystemComponent", "x", RegistryValueKind.String), false),
                    ("no value", null, false),
                    ("QWORD 1", k => k.SetValue("SystemComponent", 1L, RegistryValueKind.QWord), true),
                    ("string 1", k => k.SetValue("SystemComponent", "1", RegistryValueKind.String), true),
                };
                var names = rows.Select(r => (r.Row, Name: scratch.Make(k =>
                {
                    k.SetValue("DisplayName", "Ambysto Lean Test " + r.Row);
                    r.Value?.Invoke(k);
                }), r.Expected)).ToList();
                var entries = source.ReadEntries();
                foreach (var (row, name, expected) in names)
                {
                    var entry = entries.SingleOrDefault(e => string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));
                    Assert.IsNotNull(entry, row + ": the key is listed");
                    Assert.AreEqual(expected, entry.SystemComponent, "SystemComponent " + row);
                }

                break;
            }

            case "unreadable key":
            {
                var name = scratch.Make(key =>
                {
                    key.SetValue("DisplayName", "Ambysto Lean Unreadable " + Guid.NewGuid().ToString("N"));
                    key.SetValue("InstallLocation", @"C:\Users\me\AppData\Local\Programs\AmbystoLeanUnreadable");
                });
                ScratchEntries.DenyRead(name);
                using (var denied = Registry.CurrentUser.OpenSubKey(UninstallPath))
                {
                    Assert.Throws<System.Security.SecurityException>(() => denied!.OpenSubKey(name), "fixture: this user may not read the key");
                }

                var mine = source.ReadEntries().Where(e => string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.AreEqual(1, mine.Count, "a key that cannot be read is still reported by its name");
                Assert.AreEqual(UninstallScope.PerUser, mine[0].Scope);
                Assert.IsNull(mine[0].DisplayName, "no values read");
                Assert.IsNull(mine[0].Publisher);
                Assert.IsNull(mine[0].DisplayVersion);
                Assert.IsNull(mine[0].InstallLocation);

                var watched = FakeUninstallSource.Entry(name, "Ambysto Lean Watched " + Guid.NewGuid().ToString("N"), null);
                var time = new FakeTimeProvider();
                var watcher = new AppGoneWatcher(watched, new EntriesOnlySource(source), time);
                Assert.IsFalse(CheckFor(watcher, time, 30), "the key is still there: the app is not gone");
                break;
            }

            default:
            {
                var entries = source.ReadEntries();
                SameNames(SubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry64),
                    entries.Where(e => e.Scope == UninstallScope.PerMachine && e.View == RegistryViewKind.Bits64).Select(e => e.KeyName), "HKLM, 64-bit view");
                SameNames(SubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry32),
                    entries.Where(e => e.Scope == UninstallScope.PerMachine && e.View == RegistryViewKind.Bits32).Select(e => e.KeyName), "HKLM, 32-bit view");
                SameNames(SubKeyNames(RegistryHive.CurrentUser, RegistryView.Default),
                    entries.Where(e => e.Scope == UninstallScope.PerUser).Select(e => e.KeyName), "HKCU");
                Check.True(entries.Any(e => e.Scope == UninstallScope.PerMachine), "the machine hive lists some uninstaller on this machine");
                break;
            }
        }
    }

    // Gap brief H23. docs/spec/04 "Store (MSIX) apps are listed display-only"; "no Store package … names the same install
    // location or display name". The Store list equals an independent query by the test, filtered to packages that are
    // not frameworks, not resource packages and not system-signed: the same full names, display names and installed
    // paths; no framework or system-signed package appears.
    [TestMethod]
    public void RealSourceListsThisUsersStoreAppsOnly()
    {
        List<(string FullName, string DisplayName, string Path, bool Component)> oracle;
        try
        {
            oracle = new PackageManager().FindPackagesForUser(string.Empty)
                .Select(p => (p.Id.FullName, p.DisplayName, p.InstalledPath,
                    p.IsFramework || p.IsResourcePackage || p.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.System))
                .ToList();
        }
        catch (COMException exception)
        {
            Assert.Inconclusive("the test's own package query failed: " + exception.Message);
            return;
        }

        var apps = oracle.Where(p => !p.Component).ToList();
        if (apps.Count == 0)
        {
            Assert.Inconclusive("this user has no Store app that is not a framework, a resource package or system-signed");
        }

        var listed = new RegistryUninstallSource().ReadStorePackages();
        var expected = apps.Select(p => p.FullName + " | " + p.DisplayName + " | " + p.Path).Order(StringComparer.Ordinal).ToArray();
        var actual = listed.Select(p => p.FullName + " | " + p.DisplayName + " | " + p.InstallLocation).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual, "Store list:\nexpected\n" + string.Join("\n", expected) + "\ngot\n" + string.Join("\n", actual));
        var components = oracle.Where(p => p.Component).Select(p => p.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.False(listed.Any(p => components.Contains(p.FullName)), "a framework, resource or system-signed package is listed");
    }

    /// <summary>A copy of <c>cmd.exe</c> started with its input redirected (it waits for commands until told to exit).</summary>
    private sealed class Child : IDisposable
    {
        private readonly Process process;

        public Child(string image)
        {
            var info = new ProcessStartInfo(image) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            process = Process.Start(info)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Assert.IsFalse(process.HasExited, "fixture: the child " + image + " runs");
        }

        public void Dispose()
        {
            try
            {
                process.StandardInput.WriteLine("exit");
                process.StandardInput.Close();
                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string CopyOfCmd(string folder)
    {
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), copy);
        return copy;
    }

    private static void Listed(string expected, string row)
    {
        var paths = new RegistryUninstallSource().ReadProcessImagePaths();
        Check.True(paths.Any(p => string.Equals(p, expected, StringComparison.OrdinalIgnoreCase)),
            $"{row}: {expected} is not listed; listed: [{string.Join("; ", paths.Where(p => p.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)))}] (cmd.exe images)");
        Check.False(paths.Any(p => p.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)), row + ": a \\Device\\ path is listed");
    }

    private static void RemoveFolder(string folder)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(300);
            }
        }
    }

    // Gap brief H24. docs/spec/04 "…and no process runs from its install location"; intent Outcome 4. The real process
    // list (a) returns and holds this test's own image; (b) holds a child started from a scratch folder, by its exact
    // path; (e) names, with a path on the system drive, the processes this user cannot open (found by the test: a
    // limited-query open fails); (f) admin fixture: holds a child started from a volume mounted in a folder with no drive
    // letter, under that folder (inconclusive when no such volume exists). Rows (c) and (d), network images, need the
    // console session's credentials: RealSourceNamesTheImageOfANetworkProcess.
    [TestMethod]
    [DataRow("a: own image")]
    [DataRow("b: scratch child")]
    [DataRow("e: unopenable process")]
    [DataRow("f: folder-mounted volume")]
    public void RealSourceNamesTheImageOfEveryRunningProcess(string row)
    {
        switch (row[0])
        {
            case 'a':
            {
                var own = Environment.ProcessPath!;
                Listed(own, row);
                break;
            }

            case 'b':
            {
                using var temp = new TempFolder("realsource-child");
                var image = CopyOfCmd(temp.Sub("bin"));
                using (new Child(image))
                {
                    Listed(image, row);
                }

                break;
            }

            case 'e':
            {
                // The source's own pseudo list (RegistryUninstallSource.IsPseudoProcess), vmmem and vmmemWSL included (probe 2026-10-22).
                string[] pseudo = ["Idle", "System", "Registry", "Secure System", "Memory Compression", "MemCompression", "vmmem", "vmmemWSL"];
                var byName = new Dictionary<string, (int Total, int Unopenable)>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (process.Id is 0 or 4 || pseudo.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var handle = OpenProcess(0x1000, 0, (uint)process.Id);
                        var unopenable = handle == 0;
                        if (!unopenable)
                        {
                            _ = CloseHandle(handle);
                        }

                        byName.TryGetValue(process.ProcessName, out var counts);
                        byName[process.ProcessName] = (counts.Total + 1, counts.Unopenable + (unopenable ? 1 : 0));
                    }
                }

                // Names whose every process this user cannot open: only the system query can name them.
                var names = byName.Where(kv => kv.Value.Unopenable == kv.Value.Total).Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 0)
                {
                    Assert.Inconclusive("this user can open every running process");
                }

                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
                var paths = new RegistryUninstallSource().ReadProcessImagePaths();
                var missing = names.Where(name => !paths.Any(p => p.StartsWith(systemDrive, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(p), name + ".exe", StringComparison.OrdinalIgnoreCase))).ToList();
                Check.True(missing.Count == 0, $"processes this user cannot open are not listed with a {systemDrive} path: [{string.Join(", ", missing)}] (of [{string.Join(", ", names)}])");
                break;
            }

            default:
            {
                var mount = FolderMountedVolume();
                if (mount is null)
                {
                    Assert.Inconclusive("admin fixture: no volume is mounted in a folder without a drive letter");
                    return;
                }

                var folder = Path.Combine(mount, "LeanRealSourceTests-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    string image;
                    try
                    {
                        image = CopyOfCmd(folder);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Assert.Inconclusive("admin fixture: this user may not write to the folder-mounted volume " + mount);
                        return;
                    }

                    using (new Child(image))
                    {
                        Listed(image, row);
                    }
                }
                finally
                {
                    RemoveFolder(folder);
                }

                break;
            }
        }
    }

    // Gap brief H24, rows (c) and (d), in the console session (the share needs the logon's credentials, which an SSH
    // session lacks): (c) a child started from \\localhost\ambynet\… is listed as \\localhost\ambynet\…, never as a
    // \Device\ path; (d) a child started through a drive letter the test maps to that share is listed too. Expected form
    // for (d), decided before the run: the UNC path, the share's own name, which every session sees (a drive mapping
    // belongs to one logon session); flagged for the main session.
    [TestMethod]
    [TestCategory("Console")]
    [DataRow("c: UNC child")]
    [DataRow("d: mapped-drive child")]
    public void RealSourceNamesTheImageOfANetworkProcess(string row)
    {
        const string share = @"\\localhost\ambynet";
        const string shareFolder = @"C:\AmbyNet";
        if (!Directory.Exists(shareFolder) || !Directory.Exists(share))
        {
            Assert.Inconclusive("the loopback share " + share + " (" + shareFolder + ") is not reachable here");
            return;
        }

        var scratch = "LeanRealSourceTests-" + Guid.NewGuid().ToString("N")[..8];
        var local = Path.Combine(shareFolder, scratch);
        try
        {
            CopyOfCmd(local);
            var unc = share + @"\" + scratch + @"\cmd.exe";
            if (row[0] == 'c')
            {
                using (new Child(unc))
                {
                    Listed(unc, row);
                }

                return;
            }

            var letter = Enumerable.Range('P', 'Y' - 'P' + 1).Select(c => (char)c).Reverse()
                .FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
            Assert.AreNotEqual('\0', letter, "no free drive letter");
            var drive = letter + ":";
            Net("use", drive, share);
            try
            {
                using (new Child(drive + @"\" + scratch + @"\cmd.exe"))
                {
                    Listed(unc, row);
                }
            }
            finally
            {
                Net("use", drive, "/delete", "/y");
            }
        }
        finally
        {
            RemoveFolder(local);
        }
    }

    private static void Net(params string[] args)
    {
        var info = new ProcessStartInfo("net.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var net = Process.Start(info)!;
        var output = net.StandardOutput.ReadToEnd() + net.StandardError.ReadToEnd();
        Assert.IsTrue(net.WaitForExit(30_000), "net " + string.Join(' ', args) + " did not finish");
        Assert.AreEqual(0, net.ExitCode, "net " + string.Join(' ', args) + ": " + output);
    }

    // Gap brief H25. docs/spec/04 rule for "gone"; the source's contract: what it cannot read is "an error, never an
    // empty list". The constructible row: the user's own Uninstall key itself cannot be read (an ACL the test sets and
    // removes; only when the key holds no entry and no value, so no real entry is touched). The read throws IOException,
    // and a watcher reading it never concludes "gone". The other rows (the Store catalog or one package failing, a process
    // neither query can name) need a fault seam in the source: not written here.
    [TestMethod]
    public void AnUnreadableSourcePartIsAnErrorNeverAnEmptyList()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var created = false;
        using (var existing = Registry.CurrentUser.OpenSubKey(UninstallPath))
        {
            if (existing is null)
            {
                created = true;
                Registry.CurrentUser.CreateSubKey(UninstallPath).Dispose();
            }
            else if (existing.SubKeyCount != 0 || existing.ValueCount != 0)
            {
                Assert.Inconclusive("HKCU\\" + UninstallPath + " holds real entries; its ACL is not changed");
                return;
            }
        }

        // The DACL is saved and restored as bytes through RegGetKeySecurity/RegSetKeySecurity: .NET's SetAccessControl would
        // also set the auto-inherited flag (measured 2026-10-18: "D:(…)" came back as "D:AI(…)"), so it is not used here.
        string before;
        byte[] original;
        var deny = new RegistryAccessRule(me, RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
        {
            original = DaclBytes(key);
            var security = key.GetAccessControl(AccessControlSections.Access);
            before = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            security.AddAccessRule(deny);
            SetDacl(key, security.GetSecurityDescriptorBinaryForm());
        }

        try
        {
            var source = new RegistryUninstallSource();
            Assert.Throws<IOException>(() => source.ReadEntries(), "an unreadable Uninstall key is an error, never an empty list");

            var watched = FakeUninstallSource.Entry("AmbystoLeanTest-" + Guid.NewGuid().ToString("D"), "Ambysto Lean Watched " + Guid.NewGuid().ToString("N"), null);
            var time = new FakeTimeProvider();
            Assert.IsFalse(CheckFor(new AppGoneWatcher(watched, new EntriesOnlySource(source), time), time, 30), "a failed read is a sighting");
        }
        finally
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
            {
                SetDacl(key, original);

                // The same entries, inherited or not, and the same protection; the auto-inherited control flag is not
                // compared (RegSetKeySecurity does not carry it back, measured 2026-10-18; it changes no access).
                Assert.AreEqual(Normalized(before), Normalized(key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)), "the Uninstall key's ACL is as the test found it");
            }

            if (created)
            {
                Registry.CurrentUser.DeleteSubKey(UninstallPath, throwOnMissingSubKey: false);
            }
        }

        // Control: readable again, the same read returns a list.
        Assert.IsNotNull(new RegistryUninstallSource().ReadEntries());
    }

    private const uint DaclSecurityInformation = 4;

    // "D:AI(…)" and "D:(…)" are one DACL; "D:P…" (protected) is kept apart.
    private static string Normalized(string sddl)
    {
        var aces = sddl.IndexOf('(', StringComparison.Ordinal);
        return sddl.StartsWith("D:", StringComparison.Ordinal) && aces > 2
            ? "D:" + sddl[2..aces].Replace("AI", "", StringComparison.Ordinal) + sddl[aces..]
            : sddl;
    }

    private static byte[] DaclBytes(RegistryKey key)
    {
        uint size = 0;
        _ = RegGetKeySecurity(key.Handle, DaclSecurityInformation, null, ref size);
        var bytes = new byte[size];
        var result = RegGetKeySecurity(key.Handle, DaclSecurityInformation, bytes, ref size);
        Assert.AreEqual(0, result, "RegGetKeySecurity");
        return bytes;
    }

    private static void SetDacl(RegistryKey key, byte[] descriptor) =>
        Assert.AreEqual(0, RegSetKeySecurity(key.Handle, DaclSecurityInformation, descriptor), "RegSetKeySecurity");

    [LibraryImport("advapi32.dll")]
    private static partial int RegGetKeySecurity(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint securityInformation, byte[]? descriptor, ref uint size);

    [LibraryImport("advapi32.dll")]
    private static partial int RegSetKeySecurity(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint securityInformation, byte[] descriptor);

    /// <summary>A folder in which a volume without a drive letter is mounted, or null.</summary>
    private static unsafe string? FolderMountedVolume()
    {
        var volume = new char[64];
        var names = new char[4096];
        fixed (char* volumePointer = volume)
        {
            var find = FindFirstVolume(volumePointer, (uint)volume.Length);
            if (find == -1)
            {
                return null;
            }

            try
            {
                do
                {
                    var guidPath = new string(volume, 0, Array.IndexOf(volume, '\0'));
                    Array.Clear(names);
                    fixed (char* namesPointer = names)
                    {
                        if (GetVolumePathNamesForVolumeName(guidPath, namesPointer, (uint)names.Length, out _) == 0)
                        {
                            continue;
                        }
                    }

                    var mounts = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (mounts.Length > 0 && !mounts.Any(m => m.Length <= 3))
                    {
                        return mounts[0].TrimEnd('\\');
                    }
                }
                while (FindNextVolume(find, volumePointer, (uint)volume.Length) != 0);
            }
            finally
            {
                _ = FindVolumeClose(find);
            }
        }

        return null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    private static unsafe partial nint FindFirstVolume(char* volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    private static unsafe partial int FindNextVolume(nint find, char* volumeName, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FindVolumeClose(nint find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int GetVolumePathNamesForVolumeName(string volumeName, char* pathNames, uint length, out uint returnLength);
}
