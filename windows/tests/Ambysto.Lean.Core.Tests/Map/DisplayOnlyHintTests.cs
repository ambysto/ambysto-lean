using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Map;

/// <summary>docs/spec/01 "Map" hint table with its Windows notes, docs/spec/02 Windows note on allowed roots (intent
/// decision 7, roots (a) and (b)), plan "Display-only hints in phase A": <c>LeanRules.Preview</c> decides by path and
/// owner alone what is shown as display-only; <c>DisplayOnly.HintFor</c> says why and where to manage it
/// (docs/spec/07: "Display-only items always say why and where to manage them").</summary>
[TestClass]
public sealed class DisplayOnlyHintTests
{
    // docs/spec/02 Windows note (a): inside %USERPROFILE%, at least 2 path components below it.
    [TestMethod]
    public void HomeItemTwoComponentsBelowTheProfileIsAllowed()
    {
        using var fx = new ProfileFixture("hint-home");
        var file = ProfileFixture.MakeFile(fx.InProfile("Documents", "report.pdf"));
        Assert.IsTrue(LeanRules.Preview(file, fx.Context));
        Assert.IsNull(DisplayOnly.HintFor(file, fx.Context));
        var folder = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Old"));
        Assert.IsTrue(LeanRules.Preview(folder, fx.Context));
        // Inside root (a) and in no never-touch location with a hint of its own (DisplayOnly contract): null.
        var video = ProfileFixture.MakeFile(fx.InProfile("Videos", "Holiday", "clip.mp4"));
        Assert.IsTrue(LeanRules.Preview(video, fx.Context));
        Assert.IsNull(DisplayOnly.HintFor(video, fx.Context));
    }

    // docs/spec/02 Windows note (a): one component below the profile is not enough (the same depth rule as macOS).
    [TestMethod]
    public void ItemDirectlyInTheProfileIsDisplayOnly()
    {
        using var fx = new ProfileFixture("hint-profile-direct");
        var file = ProfileFixture.MakeFile(fx.InProfile("loose.iso"));
        Assert.IsFalse(LeanRules.Preview(file, fx.Context));
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(file, fx.Context));
        Assert.IsFalse(LeanRules.Preview(fx.Documents, fx.Context), "a top-level profile folder itself");
        Assert.IsFalse(LeanRules.Preview(fx.Profile, fx.Context), "the profile itself");
    }

    // docs/spec/02 Windows note (a): not inside AppData.
    [TestMethod]
    public void AppDataIsDisplayOnly()
    {
        using var fx = new ProfileFixture("hint-appdata");
        var roaming = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Contoso", "settings.dat"));
        var local = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Contoso", "cache.bin"));
        var programs = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Programs", "Tool", "tool.exe"));
        var appData = ProfileFixture.MakeFile(fx.InProfile("AppData", "LocalLow", "x", "y.bin"));
        string[] appDataPaths = [roaming, local, programs, appData];
        foreach (var path in appDataPaths)
        {
            Assert.IsFalse(LeanRules.Preview(path, fx.Context), path);
            Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(path, fx.Context), path);
        }
    }

    // docs/spec/01 hint table, "Developer caches": Text.HintDeveloperCaches has no Windows rule yet (intent, Out of scope:
    // "Developer-cache hints beyond what 01 lists for Windows"); a developer cache in AppData is simply outside.
    [TestMethod]
    public void DeveloperCacheInAppDataIsOutsideNotADeveloperCachesHint()
    {
        using var fx = new ProfileFixture("hint-devcache");
        var npm = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "npm-cache", "_cacache", "index.bin"));
        var nuget = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "NuGet", "v3-cache", "x.dat"));
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(npm, fx.Context));
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(nuget, fx.Context));
    }

    // docs/spec/02 Windows note (a): except %LOCALAPPDATA%\Temp\* and %LOCALAPPDATA%\CrashDumps\* (by these literal paths).
    [TestMethod]
    public void LocalTempAndCrashDumpsContentsAreAllowedButNotTheFoldersThemselves()
    {
        using var fx = new ProfileFixture("hint-temp");
        var temp = Path.Combine(fx.LocalAppData, "Temp");
        var crash = Path.Combine(fx.LocalAppData, "CrashDumps");
        var inTemp = ProfileFixture.MakeFile(Path.Combine(temp, "installer.tmp"));
        var deepInTemp = ProfileFixture.MakeFile(Path.Combine(temp, "sub", "deep.tmp"));
        var inCrash = ProfileFixture.MakeFile(Path.Combine(crash, "app.exe.1234.dmp"));
        Assert.IsTrue(LeanRules.Preview(inTemp, fx.Context));
        Assert.IsTrue(LeanRules.Preview(deepInTemp, fx.Context));
        Assert.IsTrue(LeanRules.Preview(inCrash, fx.Context));
        Assert.IsNull(DisplayOnly.HintFor(inTemp, fx.Context));
        Assert.IsFalse(LeanRules.Preview(temp, fx.Context), "Temp itself");
        Assert.IsFalse(LeanRules.Preview(crash, fx.Context), "CrashDumps itself");
    }

    // docs/spec/02 Windows note (a): "by these literal paths, whatever %TEMP% says": a Temp elsewhere in AppData is not the exception.
    [TestMethod]
    public void ATempFolderElsewhereInAppDataIsNotTheException()
    {
        using var fx = new ProfileFixture("hint-temp-other");
        var roamingTemp = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Temp", "x.tmp"));
        var nestedTemp = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Contoso", "Temp", "x.tmp"));
        Assert.IsFalse(LeanRules.Preview(roamingTemp, fx.Context));
        Assert.IsFalse(LeanRules.Preview(nestedTemp, fx.Context));
    }

    // docs/spec/02 Windows note (b): a folder at the root of the profile's volume that the current user owns, every
    // folder down to the item too, at least one component inside the top folder.
    [TestMethod]
    public void UserOwnedFolderAtTheVolumeRootIsAllowedOneLevelInside()
    {
        using var fx = new ProfileFixture("hint-root-b");
        var data = ProfileFixture.MakeFolder(fx.OnVolume("Data"));
        var file = ProfileFixture.MakeFile(fx.OnVolume("Data", "old.iso"));
        var deep = ProfileFixture.MakeFile(fx.OnVolume("Data", "Archive", "2019", "old.zip"));
        Assert.IsTrue(LeanRules.Preview(file, fx.Context), "C:\\Data\\old.iso may move");
        Assert.IsTrue(LeanRules.Preview(deep, fx.Context));
        Assert.IsNull(DisplayOnly.HintFor(file, fx.Context));
        Assert.IsFalse(LeanRules.Preview(data, fx.Context), "C:\\Data itself may not");
        Assert.IsFalse(LeanRules.Preview(fx.VolumeRoot, fx.Context), "the volume root never");
    }

    // docs/spec/02 Windows note (b): never a system location or Users.
    [TestMethod]
    public void SystemFoldersAndUsersAreDisplayOnlyWithTheSystemWideHint()
    {
        using var fx = new ProfileFixture("hint-system");
        var paths = new[]
        {
            ProfileFixture.MakeFile(Path.Combine(fx.WindowsFolder, "System32", "x.dll")),
            ProfileFixture.MakeFile(Path.Combine(fx.ProgramFiles, "Contoso", "app.exe")),
            ProfileFixture.MakeFile(Path.Combine(fx.ProgramFilesX86, "Contoso", "app.exe")),
            ProfileFixture.MakeFile(Path.Combine(fx.ProgramData, "Contoso", "data.bin")),
            ProfileFixture.MakeFile(Path.Combine(fx.OtherProfile, "Documents", "theirs.pdf")),
        };
        foreach (var path in paths)
        {
            Assert.IsFalse(LeanRules.Preview(path, fx.Context), path);
            Assert.AreEqual(Text.HintSystemWide, DisplayOnly.HintFor(path, fx.Context), path);
        }

        Assert.IsFalse(LeanRules.Preview(Path.Combine(fx.UsersFolder, "Public", "x.bin"), fx.Context), "Users is never root (b)");
    }

    // docs/spec/01 Windows notes on hints: other users' folders are "System-wide: needs an administrator; Lean does not change it".
    [TestMethod]
    public void AnotherUsersFolderGetsTheSystemWideHintEvenDeepInside()
    {
        using var fx = new ProfileFixture("hint-other-user");
        var theirs = ProfileFixture.MakeFile(Path.Combine(fx.OtherProfile, "Downloads", "Big", "film.mkv"));
        Assert.AreEqual(Text.HintSystemWide, DisplayOnly.HintFor(theirs, fx.Context));
    }

    // docs/spec/01 Windows notes on hints: Docker and WSL disks (*.vhdx inside AppData) "Manage in Docker Desktop or with wsl.exe".
    [TestMethod]
    public void VhdxInsideAppDataGetsTheDockerWslHint()
    {
        using var fx = new ProfileFixture("hint-vhdx");
        var docker = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Docker", "wsl", "disk", "docker_data.vhdx"));
        var wsl = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Packages", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc", "LocalState", "ext4.vhdx"));
        var wslLocal = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "wsl", "{guid}", "ext4.vhdx"));
        Assert.AreEqual(Text.HintDockerWsl, DisplayOnly.HintFor(docker, fx.Context));
        Assert.AreEqual(Text.HintDockerWsl, DisplayOnly.HintFor(wsl, fx.Context));
        Assert.AreEqual(Text.HintDockerWsl, DisplayOnly.HintFor(wslLocal, fx.Context));
        Assert.IsFalse(LeanRules.Preview(docker, fx.Context));
    }

    // docs/spec/01 Windows notes on hints: a .vhdx in an allowed root is not a Docker/WSL disk (intent decision 8: it may move).
    [TestMethod]
    public void VhdxInAnAllowedRootIsNotDisplayOnly()
    {
        using var fx = new ProfileFixture("hint-vhdx-allowed");
        var mine = ProfileFixture.MakeFile(fx.InProfile("VMs", "test.vhdx"));
        Assert.IsTrue(LeanRules.Preview(mine, fx.Context));
        Assert.IsNull(DisplayOnly.HintFor(mine, fx.Context));
    }

    // docs/spec/01 Windows notes on hints: a cloud-synced folder "Synced by <provider>: manage it there".
    [TestMethod]
    public void CloudSyncedFolderGetsTheProviderHint()
    {
        using var fx = new ProfileFixture("hint-synced");
        var synced = ProfileFixture.MakeFile(Path.Combine(fx.OneDrive, "Documents", "shared.docx"));
        // DisplayOnly contract: a never-touch location of docs/spec/02 with its own hint (the sync roots: intent decision 5)
        // gets it even within root (a), which LeanRules.Preview admits by path.
        Assert.IsTrue(LeanRules.Preview(synced, fx.Context), "root (a) by path: two components below the profile");
        Assert.AreEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(synced, fx.Context));
        Assert.AreEqual("Synced by OneDrive: manage it there", DisplayOnly.HintFor(synced, fx.Context));
        Assert.AreEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(fx.OneDrive, fx.Context), "the sync root itself");
    }

    // docs/spec/01 Windows notes on hints: the provider name comes from the sync root's registration, not from the folder name.
    [TestMethod]
    public void ProviderNameComesFromTheSyncRootNotTheFolderName()
    {
        using var temp = new TempFolder("hint-provider-name");
        var profile = temp.Folder("Vol", "Users", "me");
        var syncRoot = temp.Folder("Vol", "Users", "me", "My Files");
        var context = new PathContext(profile, temp.Folder("Vol", "Users", "me", "AppData", "Roaming"), temp.Folder("Vol", "Users", "me", "AppData", "Local"),
            temp.Sub("Vol"), temp.Folder("Vol", "Users"), [temp.Folder("Vol", "Windows")], [new SyncRoot(syncRoot, "Dropbox")]);
        var file = ProfileFixture.MakeFile(Path.Combine(syncRoot, "Work", "a.pdf"));
        Assert.AreEqual("Synced by Dropbox: manage it there", DisplayOnly.HintFor(file, context));
    }

    // docs/spec/01 Windows notes on hints: device backups "Manage in Apple Devices or iTunes".
    [TestMethod]
    public void DeviceBackupsGetTheAppleDevicesHint()
    {
        using var fx = new ProfileFixture("hint-backup");
        var itunes = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Apple Computer", "MobileSync", "Backup", "00008030-000A1B2C3D4E5F6G", "Manifest.db"));
        var appleDevices = ProfileFixture.MakeFile(fx.InProfile("Apple", "MobileSync", "Backup", "00008030-000A1B2C3D4E5F6G", "Manifest.db"));
        Assert.AreEqual(Text.HintDeviceBackups, DisplayOnly.HintFor(itunes, fx.Context), "iTunes (desktop) backups under %APPDATA%");
        Assert.AreEqual(Text.HintDeviceBackups, DisplayOnly.HintFor(appleDevices, fx.Context), "Apple Devices (Store) backups under the profile");
        // DisplayOnly contract: device backups are never-touch (intent decision 8) with a hint of their own, even within
        // root (a), which Preview admits by path.
        Assert.IsTrue(LeanRules.Preview(appleDevices, fx.Context), "root (a) by path");
    }

    // docs/spec/01 hint table: anything else outside the allowed roots "Outside the folders Lean may change".
    [TestMethod]
    public void AnotherVolumeIsOutsideTheFoldersLeanMayChange()
    {
        using var fx = new ProfileFixture("hint-other-volume");
        var elsewhere = ProfileFixture.MakeFile(Path.Combine(fx.OtherVolume, "Data", "big.iso"));
        Assert.IsFalse(LeanRules.Preview(elsewhere, fx.Context));
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(elsewhere, fx.Context));
    }

    // docs/spec/01 hint table: a file directly at the volume root (not inside any top folder) is outside.
    [TestMethod]
    public void FileDirectlyAtTheVolumeRootIsOutside()
    {
        using var fx = new ProfileFixture("hint-root-file");
        var file = ProfileFixture.MakeFile(fx.OnVolume("pagefile-like.bin"));
        Assert.IsFalse(LeanRules.Preview(file, fx.Context));
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(file, fx.Context));
    }

    // DisplayOnly contract (docs/spec/07: display-only items always say why): a hint exactly when Preview does not admit
    // the item, or when it lies in a never-touch location of docs/spec/02 with a hint of its own (a cloud-sync folder,
    // device backups), even within root (a); null for every other item.
    [TestMethod]
    public void HintExactlyWhenPreviewRefusesOrANeverTouchLocationHasItsOwnHint()
    {
        using var fx = new ProfileFixture("hint-consistency");
        var cases = new (string Path, bool NeverTouchWithHint)[]
        {
            (ProfileFixture.MakeFile(fx.InProfile("Documents", "a.pdf")), false),
            (ProfileFixture.MakeFile(fx.InProfile("Videos", "Holiday", "clip.mp4")), false),
            (ProfileFixture.MakeFile(fx.InProfile("b.pdf")), false),
            (ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "c", "d.bin")), false),
            (ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Temp", "e.tmp")), false),
            (ProfileFixture.MakeFile(fx.OnVolume("Data", "f.iso")), false),
            (ProfileFixture.MakeFile(Path.Combine(fx.ProgramFiles, "g", "h.exe")), false),
            (ProfileFixture.MakeFile(Path.Combine(fx.OtherVolume, "j.bin")), false),
            (ProfileFixture.MakeFile(Path.Combine(fx.OneDrive, "i.docx")), true),
            (ProfileFixture.MakeFile(fx.InProfile("Apple", "MobileSync", "Backup", "k", "Manifest.db")), true),
            (ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Apple Computer", "MobileSync", "Backup", "k", "Manifest.db")), true),
        };
        foreach (var (path, neverTouchWithHint) in cases)
        {
            var admitted = LeanRules.Preview(path, fx.Context);
            var hint = DisplayOnly.HintFor(path, fx.Context);
            if (!admitted || neverTouchWithHint)
            {
                Check.True(!string.IsNullOrWhiteSpace(hint), $"no hint for {path} (admitted by Preview: {admitted}, never-touch with a hint: {neverTouchWithHint})");
            }
            else
            {
                Assert.IsNull(hint, path);
            }
        }
    }

    // docs/spec/02 Comparison is done on canonical paths: a spelling with "..", a trailing separator or other case does not change the answer.
    [TestMethod]
    public void SpellingVariantsGiveTheSameAnswer()
    {
        using var fx = new ProfileFixture("hint-spelling");
        var file = ProfileFixture.MakeFile(fx.InProfile("Documents", "report.pdf"));
        Assert.IsTrue(LeanRules.Preview(file.ToUpperInvariant(), fx.Context), "case");
        Assert.IsFalse(LeanRules.Preview(fx.InProfile("Documents", "..", "report.pdf"), fx.Context), "'..' resolves to one component below the profile");
        Assert.IsFalse(LeanRules.Preview(Path.Combine(fx.RoamingAppData, "x", "..", "..", "..", "Documents", "..", "y.bin"), fx.Context), "'..' out of AppData into the profile root");
        var escaped = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Contoso", "x.bin"));
        Assert.IsTrue(File.Exists(escaped));
        Assert.IsFalse(LeanRules.Preview(Path.Combine(fx.LocalAppData, "Temp", "..", "Contoso", "x.bin"), fx.Context), "'Temp\\..' escapes the Temp exception into AppData");
        var folder = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Old"));
        Assert.AreEqual(LeanRules.Preview(folder, fx.Context), LeanRules.Preview(folder + @"\", fx.Context), "trailing separator");
    }

    // docs/spec/02 "Comparison is done on canonical paths ... so an alias spelling ... cannot slip past": an 8.3 short-name
    // spelling is judged like the long one, in both directions (admitted Documents, refused AppData).
    [TestMethod]
    public void ShortNameSpellingIsJudgedLikeTheLongOne()
    {
        using var fx = new ProfileFixture("hint-shortname");
        var allowed = ProfileFixture.MakeFile(fx.InProfile("Documents", "Long Report Name.pdf"));
        var refused = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Contoso Long Name", "settings.dat"));
        var shortAllowed = Native.ShortPathOf(allowed);
        var shortRefused = Native.ShortPathOf(refused);
        if (shortAllowed is null || shortRefused is null)
        {
            Assert.Inconclusive("the scratch volume keeps no 8.3 names for these paths");
            return;
        }

        Assert.IsTrue(LeanRules.Preview(shortAllowed, fx.Context), shortAllowed);
        Assert.IsNull(DisplayOnly.HintFor(shortAllowed, fx.Context), shortAllowed);
        Assert.IsFalse(LeanRules.Preview(shortRefused, fx.Context), shortRefused);
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(shortRefused, fx.Context), shortRefused);
    }

    // docs/spec/02 Windows note (b): an owner that cannot be read is not the user's (plan "Rules": an unknown owner is not
    // the user's). As the owner of a folder the standard user always keeps READ_CONTROL, so an unreadable owner cannot be
    // staged here; what can: a folder whose top-level ancestor the user does not own, on the real volume.
    [TestMethod]
    public void RealVolumeFoldersNotOwnedByTheUserAreRefused()
    {
        var context = PathContext.Current();
        var me = WindowsIdentity.GetCurrent().User!;
        var checkedAny = false;
        foreach (var top in new DirectoryInfo(context.ProfileVolumeRoot).EnumerateDirectories())
        {
            IdentityReference? owner;
            try
            {
                owner = top.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            }
            catch (UnauthorizedAccessException)
            {
                owner = null;
            }
            catch (IOException)
            {
                owner = null;
            }

            if (owner is not null && owner.Equals(me))
            {
                continue;
            }

            checkedAny = true;
            Assert.IsFalse(LeanRules.Preview(Path.Combine(top.FullName, "child", "item.bin"), context), top.FullName + " is not owned by this user");
        }

        Check.True(checkedAny, "no top-level folder of the volume is owned by someone else; nothing to check");
    }

    // PathContext.Current(): this user's folders (docs/spec/02 Windows note: the profile's volume; AppData; Users; system folders).
    [TestMethod]
    public void CurrentContextNamesThisUsersFolders()
    {
        var context = PathContext.Current();
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), context.ProfileFolder);
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), context.RoamingAppData);
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), context.LocalAppData);
        Assert.AreEqual(Path.GetPathRoot(context.ProfileFolder), context.ProfileVolumeRoot);
        Assert.IsTrue(string.Equals(context.UsersFolder, Path.GetDirectoryName(context.ProfileFolder), StringComparison.OrdinalIgnoreCase), context.UsersFolder);
        var expectedSystem = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var folder in expectedSystem)
        {
            Assert.IsTrue(context.SystemFolders.Contains(folder, StringComparer.OrdinalIgnoreCase), folder + " missing from " + string.Join("; ", context.SystemFolders));
        }
    }

    // docs/spec/02 Windows note (a) on the real machine: an item two levels below the profile passes; Program Files does not.
    [TestMethod]
    public void RealContextAdmitsAProfileItemAndRefusesProgramFiles()
    {
        var context = PathContext.Current();
        // A uniquely named folder directly in the real profile, made and removed here (never the user's own folders).
        var folder = Path.Combine(context.ProfileFolder, "LeanCoreTests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "item.txt");
        File.WriteAllText(file, "x");
        try
        {
            Assert.IsTrue(LeanRules.Preview(file, context), "two components below the profile");
            Assert.IsNull(DisplayOnly.HintFor(file, context));
            Assert.IsFalse(LeanRules.Preview(folder, context), "one component below the profile");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Contoso", "app.exe");
        Assert.IsFalse(LeanRules.Preview(programFiles, context));
        Assert.AreEqual(Text.HintSystemWide, DisplayOnly.HintFor(programFiles, context));
    }

    // docs/spec/01 hint table, "Anything else outside the allowed roots": a spelling Lean cannot use (an admin share, a
    // device or volume-GUID namespace, a name ending in a dot) gets "Outside the folders Lean may change", never an
    // exception, even when it names an item the plain spelling admits (brief M02). Control: the plain spelling has no hint.
    [TestMethod]
    public void UnusableSpellingGetsTheOutsideHint()
    {
        using var fx = new ProfileFixture("hint-unusable-spelling");
        var file = ProfileFixture.MakeFile(fx.InProfile("Documents", "report.pdf"));
        Assert.IsNull(DisplayOnly.HintFor(file, fx.Context), "control: the plain spelling is admitted");

        var root = Path.GetPathRoot(file)!;
        var rest = file[root.Length..];
        var spellings = new System.Collections.Generic.List<string>
        {
            @"\\localhost\C$\x",
            @"\\.\C:\x",
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x",
            @"C:\x\y.",
            @"\\localhost\" + root[0] + @"$\" + rest,
            @"\\.\" + root + rest,
            fx.InProfile("Documents", "y."),
            file + ".",
        };
        if (Native.VolumeGuidPathOf(root) is { } volume)
        {
            spellings.Add(volume + rest);
        }

        foreach (var spelling in spellings)
        {
            string? hint = null;
            try
            {
                hint = DisplayOnly.HintFor(spelling, fx.Context);
            }
            catch (Exception e)
            {
                Assert.Fail($"HintFor({spelling}) threw {e.GetType().Name}: {e.Message}");
            }

            Assert.AreEqual(Text.HintOutside, hint, spelling);
        }
    }

    // docs/spec/01 Windows notes on hints (device backups): only the MobileSync\Backup folders are device backups. Another
    // file in iTunes' MobileSync folder is in AppData (outside); one in the profile's Apple\MobileSync folder is a home item
    // with no hint (brief M10). Control: inside Backup, both get the device-backups hint.
    [TestMethod]
    public void OnlyTheBackupFolderIsDeviceBackups()
    {
        using var fx = new ProfileFixture("hint-backup-only");
        var itunesOther = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Apple Computer", "MobileSync", "other.txt"));
        var appleOther = ProfileFixture.MakeFile(fx.InProfile("Apple", "MobileSync", "x.txt"));
        var itunesBackup = ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Apple Computer", "MobileSync", "Backup", "d", "Manifest.db"));
        var appleBackup = ProfileFixture.MakeFile(fx.InProfile("Apple", "MobileSync", "Backup", "d", "Manifest.db"));

        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(itunesOther, fx.Context), "iTunes' MobileSync outside Backup is AppData");
        Assert.IsNull(DisplayOnly.HintFor(appleOther, fx.Context), "the profile's Apple\\MobileSync outside Backup is a home item");
        Assert.AreEqual(Text.HintDeviceBackups, DisplayOnly.HintFor(itunesBackup, fx.Context), "control: iTunes backups");
        Assert.AreEqual(Text.HintDeviceBackups, DisplayOnly.HintFor(appleBackup, fx.Context), "control: Apple Devices backups");
    }
}
