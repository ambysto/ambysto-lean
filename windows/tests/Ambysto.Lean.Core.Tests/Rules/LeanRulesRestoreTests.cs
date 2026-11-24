using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary><see cref="LeanRules.ForRestore"/> through the real Kit gate's <see cref="SafetyGate.CheckRestoreDestination"/>
/// (plan Design "Rules", owner decision Q2 = A; docs/spec/05 and docs/spec/02: "A put-back destination is judged from its
/// path"): a destination may be where roots (a) or (b) would let an item be, or in the leftover folders of docs/spec/04
/// (<c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>, <c>%LOCALAPPDATA%\Programs</c>) or the user's <c>Start Menu\Programs</c>,
/// never <c>Startup</c>; for every entry, from the destination path alone. Destinations do not exist (an item is put back
/// where it was); their folders do. Reading pinned here: a destination is admitted by roots (a) and (b) exactly when an
/// item at that path would be (so <c>%USERPROFILE%\Documents\report.pdf</c> may come back, <c>%USERPROFILE%\x.bin</c> may
/// not, <c>C:\Data\old.iso</c> may, <c>C:\x.bin</c> may not). Console category: the gate checks the environment first.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LeanRulesRestoreTests
{
    private static readonly bool[] ExpectedAnswers = [true, true, false, false];

    [ClassInitialize]
    public static void Sweep(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RealScratch.SweepStale();
    }

    // Root (a): a destination two components or more below the profile, its folder one or more below.
    [TestMethod]
    [TestCategory("Console")]
    public void DestinationsInRootAAreApproved()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var sub = RealScratch.Folder(Path.Combine(top, "sub"));
        string[] destinations = [Path.Combine(top, "report.pdf"), Path.Combine(top, "Old"), Path.Combine(sub, "deep.zip")];

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.Approved(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Root (a): the AppData exceptions by literal path: an item put back into %LOCALAPPDATA%\Temp or CrashDumps.
    [TestMethod]
    [TestCategory("Console")]
    public void DestinationsInLocalTempAndCrashDumpsAreApproved()
    {
        using var scratch = new RealScratch();
        scratch.EnsureFolder(RealGate.CrashDumps);
        var tempFolder = scratch.NewFolder(RealGate.LocalTemp);
        string[] destinations =
        [
            RealScratch.NewName(RealGate.LocalTemp, ".tmp"),
            Path.Combine(tempFolder, "deep.tmp"),
            RealScratch.NewName(RealGate.CrashDumps, ".dmp"),
        ];

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.Approved(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Root (b): a destination one component or more inside a top folder the user made at the volume root.
    [TestMethod]
    [TestCategory("Console")]
    public void DestinationsInRootBAreApproved()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.VolumeRoot);
        var archive = RealScratch.Folder(Path.Combine(top, "Archive"));
        string[] destinations = [Path.Combine(top, "old.iso"), Path.Combine(top, "Old"), Path.Combine(archive, "2019.zip")];

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.Approved(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Owner decision Q2 = A: the leftover folders of docs/spec/04 are restore parents for every entry: an app's folder
    // directly in %APPDATA%, %LOCALAPPDATA% or %LOCALAPPDATA%\Programs, a <Publisher>\<DisplayName> folder, and an install
    // location deeper inside Programs.
    [TestMethod]
    [TestCategory("Console")]
    public void LeftoverFolderDestinationsAreApproved()
    {
        using var scratch = new RealScratch();
        scratch.EnsureFolder(RealGate.LocalPrograms);
        var vendor = scratch.NewFolder(RealGate.Roaming);
        var programsVendor = scratch.NewFolder(RealGate.LocalPrograms);
        string[] destinations =
        [
            RealScratch.NewName(RealGate.Roaming),
            RealScratch.NewName(RealGate.Local),
            RealScratch.NewName(RealGate.LocalPrograms),
            Path.Combine(vendor, "Contoso Notes"),
            Path.Combine(programsVendor, "App"),
        ];

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.Approved(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Plan "Rules": the user's own Start Menu\Programs is a restore parent (a shortcut, or one in a folder there).
    [TestMethod]
    [TestCategory("Console")]
    public void StartMenuProgramsDestinationsAreApproved()
    {
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.StartMenuPrograms);
        string[] destinations = [RealScratch.NewName(RealGate.StartMenuPrograms, ".lnk"), Path.Combine(folder, "Contoso Notes.lnk")];

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.Approved(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Plan "Rules", intent decision 12: "not Startup": although Startup lies inside %APPDATA% and inside Start Menu\Programs,
    // nothing is put back there (a shortcut there would start at the next login).
    [TestMethod]
    [TestCategory("Console")]
    public void StartupDestinationsAreRefused()
    {
        var destination = RealScratch.NewName(RealGate.Startup, ".lnk");
        Check.True(Directory.Exists(RealGate.Startup), "setup: " + RealGate.Startup + " exists");
        Check.True(RealGate.Startup.StartsWith(RealGate.Roaming + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "setup: Startup is inside %APPDATA%");

        var gate = RealGate.ForRestore();
        RealGate.RefusedByTheRule(gate.CheckRestoreDestination(destination), destination);
    }

    // Root (a) depth: a destination directly in the profile (its folder is the profile itself) is refused by the rule; the
    // profile itself is refused (by the gate: it exists).
    [TestMethod]
    [TestCategory("Console")]
    public void DestinationsDirectlyInTheProfileAndTheProfileItselfAreRefused()
    {
        var direct = RealScratch.NewName(RealGate.Profile, ".bin");
        var directFolder = RealScratch.NewName(RealGate.Profile);

        var gate = RealGate.ForRestore();
        RealGate.RefusedByTheRule(gate.CheckRestoreDestination(direct), direct);
        RealGate.RefusedByTheRule(gate.CheckRestoreDestination(directFolder), directFolder);
        RealGate.Refused(gate.CheckRestoreDestination(RealGate.Profile), "the profile itself");
    }

    // AppData outside the leftover folders and the literal exceptions: LocalLow and AppData itself are not restore parents.
    [TestMethod]
    [TestCategory("Console")]
    public void OtherAppDataDestinationsAreRefused()
    {
        var lowParent = Path.Combine(RealGate.Profile, "AppData", "LocalLow");
        var destinations = new List<string> { RealScratch.NewName(Path.Combine(RealGate.Profile, "AppData")) };
        if (Directory.Exists(lowParent))
        {
            destinations.Add(RealScratch.NewName(lowParent));
        }

        var gate = RealGate.ForRestore();
        foreach (var destination in destinations)
        {
            RealGate.RefusedByTheRule(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Root (b) depth and ownership: directly at the volume root, Users outside the profile, and a top folder this user
    // does not own are refused; system folders are refused (by protection or by the rule).
    [TestMethod]
    [TestCategory("Console")]
    public void VolumeRootUsersSystemAndForeignTopFoldersAreRefused()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var byTheRule = new List<string> { RealScratch.NewName(RealGate.VolumeRoot, ".iso"), RealScratch.NewName(RealGate.VolumeRoot) };
        foreach (var top in Directory.EnumerateDirectories(RealGate.VolumeRoot))
        {
            var owner = LeanRulesDeleteTests.OwnerOf(top);
            if (owner is not null && !owner.Equals(me) && !LeanRulesDeleteTests.IsLink(top) && !LeanRulesDeleteTests.IsSystemOrUsers(top))
            {
                byTheRule.Add(RealScratch.NewName(top, ".bin"));
            }
        }

        var refused = new List<string>
        {
            RealScratch.NewName(Path.GetDirectoryName(RealGate.Profile)!),
            RealScratch.NewName(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ".dll"),
            RealScratch.NewName(Environment.GetFolderPath(Environment.SpecialFolder.System), ".dll"),
            RealScratch.NewName(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            RealScratch.NewName(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            RealScratch.NewName(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        };

        var gate = RealGate.ForRestore();
        foreach (var destination in byTheRule)
        {
            RealGate.RefusedByTheRule(gate.CheckRestoreDestination(destination), destination);
        }

        foreach (var destination in refused)
        {
            RealGate.Refused(gate.CheckRestoreDestination(destination), destination);
        }
    }

    // Decision 7: other volumes are never changed: a destination in a folder the user made on another fixed NTFS volume.
    [TestMethod]
    [TestCategory("Console")]
    public void DestinationOnAnotherVolumeIsRefused()
    {
        var other = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && d.DriveFormat == "NTFS"
            && !string.Equals(d.RootDirectory.FullName, RealGate.VolumeRoot, StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            Assert.Inconclusive("no second fixed NTFS volume");
            return;
        }

        using var scratch = new RealScratch();
        string top;
        try
        {
            top = scratch.NewFolder(other.RootDirectory.FullName);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("this user may not make a folder at " + other.RootDirectory.FullName);
            return;
        }

        var destination = Path.Combine(top, "old.iso");
        RealGate.RefusedByTheRule(RealGate.ForRestore().CheckRestoreDestination(destination), destination);
    }

    // Owner decision Q2 = A, docs/spec/05: "judged from the destination path itself, never from the journal line": the
    // restore rule takes no module and no item set, admits a leftover folder that a delete from the map refuses at the
    // same path, and gives the same answers from every instance, whichever deletes came before.
    [TestMethod]
    [TestCategory("Console")]
    public void EachDestinationIsJudgedByItsPathAlone()
    {
        var factory = typeof(LeanRules).GetMethod(nameof(LeanRules.ForRestore))!;
        Assert.AreEqual(0, factory.GetParameters().Length, "ForRestore takes nothing an entry could carry");

        using var scratch = new RealScratch();
        var existing = scratch.NewFolder(RealGate.Roaming);
        RealScratch.File(Path.Combine(existing, "settings.json"));
        var deleteRequest = RealGate.Request(existing);
        string[] destinations =
        [
            RealScratch.NewName(RealGate.Roaming),
            Path.Combine(scratch.NewFolder(RealGate.Profile), "report.pdf"),
            RealScratch.NewName(RealGate.Startup, ".lnk"),
            RealScratch.NewName(RealGate.Profile, ".bin"),
        ];

        RealGate.RefusedByTheRule(RealGate.ForDelete(LeanModule.Map, RealGate.NoItems).Check(deleteRequest), "control: a delete from the map in %APPDATA%");
        var first = RealGate.ForRestore();
        var answers = destinations.Select(d => first.CheckRestoreDestination(d).IsApproved).ToArray();
        RealGate.Approved(first.CheckRestoreDestination(destinations[0]), destinations[0]);

        // Moves of every module, approved or not, change nothing for a restore rule built before or after them.
        foreach (var module in Enum.GetValues<LeanModule>())
        {
            RealGate.ForDelete(module, RealGate.Items(existing)).Check(deleteRequest);
        }

        var second = RealGate.ForRestore();
        for (var i = 0; i < destinations.Length; i++)
        {
            Assert.AreEqual(answers[i], first.CheckRestoreDestination(destinations[i]).IsApproved, "first rule, again: " + destinations[i]);
            Assert.AreEqual(answers[i], second.CheckRestoreDestination(destinations[i]).IsApproved, "a second rule: " + destinations[i]);
        }

        CollectionAssert.AreEqual(ExpectedAnswers, answers, string.Join("; ", destinations));
    }
}
