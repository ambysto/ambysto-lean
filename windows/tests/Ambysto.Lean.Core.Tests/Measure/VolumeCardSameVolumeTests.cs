using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>ASP-14 fix round 3 (intent/2026-10-18-asp-14-start-and-sidebar.md, "Review of fix round 2" and the owner's
/// amendments): "every scan start refuses a folder whose volume (after mount points) is not the profile's volume; <c>\\?\</c>
/// paths on the profile's volume are accepted". <see cref="VolumeCard.IsOnSameVolume"/> is the question the app asks. Reading
/// of the contract for a folder that does not exist: the contract speaks of "any folder on the profile's drive", and a missing
/// folder typed or passed as <c>--scan</c> is on the drive its path names, so it is on the same volume as that drive's root
/// (the scan then fails for its own reason, not with "other drives come after v1"). Read only; the one mount-point test makes
/// a mount point on C: and removes it again (category Admin: setting a mount point needs an elevated token).</summary>
[TestClass]
public sealed partial class VolumeCardSameVolumeTests
{
    private static string ProfileFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ProfileVolumeRoot() => Path.GetPathRoot(ProfileFolder())!;

    /// <summary>The roots of the other drives of this machine (E:, F:, G:, H: on the test machine), not the profile's.</summary>
    private static string[] OtherDriveRoots() =>
        DriveInfo.GetDrives().Select(d => d.Name).Where(n => !string.Equals(n, ProfileVolumeRoot(), StringComparison.OrdinalIgnoreCase)).ToArray();

    // "\\?\C:\x" asks for the same volume as "C:\x" (the amendment: \\?\ paths on the profile's volume are accepted).
    [TestMethod]
    public void AnExtendedPathIsOnTheSameVolumeAsThePlainOne()
    {
        var plain = ProfileFolder();
        var extended = @"\\?\" + plain;
        Assert.IsTrue(VolumeCard.IsOnSameVolume(plain, ProfileVolumeRoot()), $"control: {plain} is on {ProfileVolumeRoot()}");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(extended, plain), $"{extended} is not on the volume of {plain}");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(extended, ProfileVolumeRoot()), $"{extended} is not on the volume of {ProfileVolumeRoot()}");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(plain, extended), $"{plain} is not on the volume of {extended} (the other way round)");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(@"\\?\" + ProfileVolumeRoot(), ProfileVolumeRoot()), $@"\\?\{ProfileVolumeRoot()} is not on {ProfileVolumeRoot()}");
    }

    // The drive letter's case does not matter.
    [TestMethod]
    public void TheDriveLetterCaseDoesNotMatter()
    {
        var root = ProfileVolumeRoot();
        Assert.IsTrue(VolumeCard.IsOnSameVolume(root.ToLowerInvariant() + @"Windows", root.ToUpperInvariant()), "c:\\Windows is not on C:\\");
    }

    // Another drive is another volume, plain or in the \\?\ form, whichever way round, whether the folder exists or not.
    [TestMethod]
    public void AnotherDriveIsNotOnTheProfilesVolume()
    {
        var others = OtherDriveRoots();
        if (others.Length == 0)
        {
            Assert.Inconclusive("no drive other than the profile's on this machine");
        }

        var profile = ProfileVolumeRoot();
        foreach (var root in others)
        {
            Assert.IsFalse(VolumeCard.IsOnSameVolume(root, profile), $"{root} is reported to be on {profile}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(profile, root), $"{profile} is reported to be on {root}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(@"\\?\" + root, profile), $@"\\?\{root} is reported to be on {profile}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(root + @"no-such-folder-asp14", profile), $"a missing folder on {root} is reported to be on {profile}");
        }
    }

    // A missing folder on the profile's drive is on the profile's drive: it is not "another drive". (The reading is in the class
    // summary.) A missing folder deep under a missing parent too.
    [TestMethod]
    public void AFolderThatDoesNotExistIsOnTheVolumeOfItsDrive()
    {
        var profile = ProfileVolumeRoot();
        var missing = Path.Combine(profile, "no-such-folder-asp14-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.IsFalse(Directory.Exists(missing), "precondition: the folder exists");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(missing, profile), $"the missing {missing} is not on {profile}");
        var deep = Path.Combine(missing, "a", "b", "c");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(deep, profile), $"the missing {deep} is not on {profile}");
        Assert.IsTrue(VolumeCard.IsOnSameVolume(@"\\?\" + deep, profile), $@"the missing \\?\{deep} is not on {profile}");
    }

    // A network path is not on the profile's volume (a share is another volume, and the check cannot tell one: false).
    [TestMethod]
    public void ANetworkSharePathIsNotOnTheProfilesVolume()
    {
        const string Share = @"\\localhost\ambynet";
        if (!Directory.Exists(Share))
        {
            Assert.Inconclusive(Share + " is not there (the test machine's loopback share)");
        }

        var profile = ProfileVolumeRoot();
        Assert.IsFalse(VolumeCard.IsOnSameVolume(Share + @"\x", profile), Share + @"\x is reported to be on " + profile);
        Assert.IsFalse(VolumeCard.IsOnSameVolume(Share + @"\", profile), Share + @"\ is reported to be on " + profile);
        Assert.IsFalse(VolumeCard.IsOnSameVolume(@"\\?\UNC\localhost\ambynet\x", profile), @"\\?\UNC\localhost\ambynet\x is reported to be on " + profile);
        Assert.IsFalse(VolumeCard.IsOnSameVolume(profile, Share + @"\x"), profile + " is reported to be on " + Share + @"\x");
    }

    // After mount points: a folder on C: that is a mount point of another volume is on that other volume, and so is a path inside
    // it, while its siblings stay on C:. Needs SetVolumeMountPoint, which a standard user may not call: category Admin
    // (elevated over SSH); Inconclusive when the token lacks the right or no other volume can be mounted. The mount point is
    // always removed in the cleanup, even when an assertion fails; if it cannot be removed the scratch folder is left as it is
    // (never deleted recursively through a mount point) and the test fails saying so.
    [TestMethod]
    [TestCategory("Admin")]
    public void AMountPointOfAnotherVolumeOnTheProfileDriveIsNotOnTheProfilesVolume()
    {
        var profile = ProfileVolumeRoot();
        string? otherRoot = null;
        string? otherGuid = null;
        foreach (var root in OtherDriveRoots())
        {
            if (Native.VolumeGuidPathOf(root) is { } guid && Native.VolumeGuidPathOf(profile) != guid)
            {
                otherRoot = root;
                otherGuid = guid;
                break;
            }
        }

        if (otherRoot is null || otherGuid is null)
        {
            Assert.Inconclusive("no other volume on this machine to mount on C:");
        }

        var temp = new TempFolder("sameVolume-mount");
        var tempRoot = Path.GetPathRoot(temp.Path)!;
        if (!string.Equals(tempRoot, profile, StringComparison.OrdinalIgnoreCase))
        {
            temp.Dispose();
            Assert.Inconclusive($"the scratch folder {temp.Path} is on {tempRoot}, not on the profile's {profile}");
        }

        var sibling = temp.Folder("sibling");
        var mount = temp.Folder("mnt");
        var mounted = false;
        try
        {
            if (!MountPoints.Set(mount, otherGuid, out var error))
            {
                Assert.Inconclusive($"SetVolumeMountPoint({mount}) failed: {error} (needs an elevated token)");
            }

            mounted = true;
            Assert.AreEqual(otherGuid, Native.VolumeGuidPathOf(mount + @"\"), "precondition: the mount point names the other volume");

            Assert.IsTrue(VolumeCard.IsOnSameVolume(sibling, profile), $"control: the sibling {sibling} (no mount point) is not on {profile}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(mount, profile), $"the mount point {mount} of {otherRoot} is reported to be on {profile}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(profile, mount), $"{profile} is reported to be on the mount point {mount} of {otherRoot}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(Path.Combine(mount, "inside", "deeper"), profile), $"a path inside the mount point {mount} is reported to be on {profile}");
            Assert.IsFalse(VolumeCard.IsOnSameVolume(@"\\?\" + mount, profile), $@"\\?\{mount} is reported to be on {profile}");
            Assert.IsTrue(VolumeCard.IsOnSameVolume(mount, otherRoot), $"the mount point {mount} is not on the volume of {otherRoot}");
        }
        finally
        {
            if (mounted)
            {
                MountPoints.Remove(mount);
            }

            if (Native.VolumeGuidPathOf(mount + @"\") == otherGuid && mounted)
            {
                // Not removed: leave the folder alone (a recursive delete must never walk into a mounted volume).
                Assert.Fail($"the mount point {mount} of {otherRoot} could not be removed; remove it with: mountvol \"{mount}\" /D");
            }
            else
            {
                temp.Dispose();
            }
        }
    }

    /// <summary>SetVolumeMountPoint and DeleteVolumeMountPoint, for the one test above. Test project only.</summary>
    private static partial class MountPoints
    {
        public static bool Set(string folder, string volumeGuidPath, out string error)
        {
            error = string.Empty;
            if (SetVolumeMountPointW(folder + @"\", volumeGuidPath))
            {
                return true;
            }

            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }

        public static void Remove(string folder)
        {
            // Retries: a handle on the new mount point (the shell, Defender) can hold the removal for a moment.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (DeleteVolumeMountPointW(folder + @"\"))
                {
                    return;
                }

                System.Threading.Thread.Sleep(300);
            }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetVolumeMountPointW(string volumeMountPoint, string volumeName);

        [LibraryImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteVolumeMountPointW(string volumeMountPoint);
    }
}
