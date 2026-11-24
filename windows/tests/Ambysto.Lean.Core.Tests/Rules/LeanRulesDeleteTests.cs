using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary><see cref="LeanRules.ForDelete"/> through the real Kit gate (intent decision 7, criterion 7; docs/spec/02
/// Windows note on allowed roots; plan Design "Rules"): (a) the profile, at least 2 components below it, AppData refused
/// except the literal <c>%LOCALAPPDATA%\Temp\*</c> and <c>%LOCALAPPDATA%\CrashDumps\*</c>; (b) folders at the root of the
/// profile's volume that the user owns, with every folder down to the item, one component inside at least, never
/// <c>Users</c> or a system location; (c) for the Uninstall module only, exactly the items its matcher produced. A refusal
/// by the rule is <see cref="RefusalReason.NotAllowed"/> with <c>Copy.Text.HintOutside</c>. Items are made in this user's
/// real profile and volumes (uniquely named, removed after); the folders owned by someone else are the real ones of the
/// volume (no administrator setup). Console category: the gate refuses every move outside the desktop session.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LeanRulesDeleteTests
{
    [ClassInitialize]
    public static void Sweep(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RealScratch.SweepStale();
    }

    // Decision 7 (a): two components or more below the profile may move, a file and a folder, at any depth.
    [TestMethod]
    [TestCategory("Console")]
    public void ProfileItemsTwoComponentsBelowAreApproved()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var file = RealScratch.File(Path.Combine(top, "item.bin"));
        var folder = RealScratch.Folder(Path.Combine(top, "Old"));
        var deep = RealScratch.File(Path.Combine(top, "Old", "2019", "deep.zip"));
        var requests = new[] { RealGate.Request(file), RealGate.Request(folder), RealGate.Request(deep) };

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.Approved(gate.Check(request), request.Path);
        }
    }

    // Decision 7 (a): "at least 2 path components below it": a file or folder directly in the profile is refused by the
    // rule; the profile itself is refused (by the gate, before the rule).
    [TestMethod]
    [TestCategory("Console")]
    public void ItemsDirectlyInTheProfileAreRefused()
    {
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.Profile);
        RealScratch.File(Path.Combine(folder, "inside.bin"));
        var file = scratch.NewFile(RealGate.Profile);
        var folderRequest = RealGate.Request(folder);
        var fileRequest = RealGate.Request(file);
        var profileRequest = RealGate.Request(RealGate.Profile);

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        RealGate.RefusedByTheRule(gate.Check(folderRequest), "a folder directly in the profile");
        RealGate.RefusedByTheRule(gate.Check(fileRequest), "a file directly in the profile");
        RealGate.Refused(gate.Check(profileRequest), "the profile itself");
    }

    // Decision 7 (a): "AppData treated like ~/Library: refused": Roaming, Local, LocalLow, AppData itself, and
    // %LOCALAPPDATA%\Programs, for every module that is not module-bound.
    [TestMethod]
    [TestCategory("Console")]
    public void AppDataItemsAreRefused()
    {
        using var scratch = new RealScratch();
        var roaming = scratch.NewFolder(RealGate.Roaming);
        var local = scratch.NewFolder(RealGate.Local);
        var programs = scratch.NewFolder(RealGate.LocalPrograms);
        var lowParent = Path.Combine(RealGate.Profile, "AppData", "LocalLow");
        var low = scratch.NewFolder(lowParent);
        var appData = scratch.NewFolder(Path.Combine(RealGate.Profile, "AppData"));
        var items = new List<string>();
        foreach (var folder in new[] { roaming, local, programs, low, appData })
        {
            items.Add(folder);
            items.Add(RealScratch.File(Path.Combine(folder, "settings.dat")));
        }

        var requests = items.Select(RealGate.Request).ToList();
        foreach (var module in new[] { LeanModule.Map, LeanModule.LargeOld, LeanModule.Duplicates })
        {
            var gate = RealGate.ForDelete(module, RealGate.NoItems);
            foreach (var request in requests)
            {
                RealGate.RefusedByTheRule(gate.Check(request), module + ": " + request.Path);
            }
        }
    }

    // Decision 7 (a): except %LOCALAPPDATA%\Temp\* and %LOCALAPPDATA%\CrashDumps\*: what is inside them, at any depth, a
    // file or a folder, is approved.
    [TestMethod]
    [TestCategory("Console")]
    public void LocalTempAndCrashDumpsContentsAreApproved()
    {
        using var scratch = new RealScratch();
        var tempFile = scratch.NewFile(RealGate.LocalTemp, ".tmp");
        var tempFolder = scratch.NewFolder(RealGate.LocalTemp);
        var tempDeep = RealScratch.File(Path.Combine(tempFolder, "sub", "deep.tmp"));
        var dump = scratch.NewFile(RealGate.CrashDumps, ".dmp");
        var dumpFolder = scratch.NewFolder(RealGate.CrashDumps);
        RealScratch.File(Path.Combine(dumpFolder, "app.exe.1234.dmp"));
        var requests = new[] { tempFile, tempFolder, tempDeep, dump, dumpFolder }.Select(RealGate.Request).ToList();

        var gate = RealGate.ForDelete(LeanModule.LargeOld, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.Approved(gate.Check(request), request.Path);
        }
    }

    // Decision 7 (a): "by these literal paths": a Temp or CrashDumps folder elsewhere in AppData is not the exception. (A
    // ".." spelling never reaches the rule: the Kit refuses it as a path form, step 4.)
    [TestMethod]
    [TestCategory("Console")]
    public void TempOrCrashDumpsElsewhereInAppDataIsNotTheException()
    {
        using var scratch = new RealScratch();
        var vendor = scratch.NewFolder(RealGate.Local);
        var nestedTemp = RealScratch.File(Path.Combine(vendor, "Temp", "x.tmp"));
        var roamingVendor = scratch.NewFolder(RealGate.Roaming);
        var nestedDumps = RealScratch.File(Path.Combine(roamingVendor, "CrashDumps", "x.dmp"));
        var requests = new[] { RealGate.Request(nestedTemp), RealGate.Request(nestedDumps) };

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), request.Path);
        }
    }

    // docs/spec/02 "Comparison is done on canonical paths": the gate judges the canonical path, so another letter case
    // and an 8.3 short name give the same answer as the plain spelling.
    [TestMethod]
    [TestCategory("Console")]
    public void SpellingsOfOneItemGetOneAnswer()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var allowed = RealScratch.File(Path.Combine(top, "Long Report Name.pdf"));
        var refused = RealScratch.File(Path.Combine(scratch.NewFolder(RealGate.Roaming), "Contoso Long Name", "settings.dat"));
        var allowedRequests = new List<GateRequest> { RealGate.Request(allowed.ToUpperInvariant(), allowed) };
        var refusedRequests = new List<GateRequest> { RealGate.Request(refused.ToUpperInvariant(), refused) };
        var shortAllowed = Native.ShortPathOf(allowed);
        var shortRefused = Native.ShortPathOf(refused);
        if (shortAllowed is not null)
        {
            allowedRequests.Add(RealGate.Request(shortAllowed, allowed));
        }

        if (shortRefused is not null)
        {
            refusedRequests.Add(RealGate.Request(shortRefused, refused));
        }

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in allowedRequests)
        {
            RealGate.Approved(gate.Check(request), request.Path);
        }

        foreach (var request in refusedRequests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), request.Path);
        }
    }

    // Decision 7 (b): a folder the user made at the root of the profile's volume: "C:\Data\old.iso may move", deeper
    // files and folders too; "C:\Data itself may not"; the volume root never.
    [TestMethod]
    [TestCategory("Console")]
    public void UserMadeTopFolderIsApprovedOneLevelInsideButNotItself()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.VolumeRoot);
        var file = RealScratch.File(Path.Combine(top, "old.iso"));
        var folder = RealScratch.Folder(Path.Combine(top, "Archive"));
        var deep = RealScratch.File(Path.Combine(top, "Archive", "2019", "old.zip"));
        var deepFolder = Path.Combine(top, "Archive", "2019");
        var inside = new[] { file, folder, deep, deepFolder }.Select(RealGate.Request).ToList();
        var topRequest = RealGate.Request(top);
        var rootRequest = RealGate.Request(RealGate.VolumeRoot);

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in inside)
        {
            RealGate.Approved(gate.Check(request), request.Path);
        }

        RealGate.RefusedByTheRule(gate.Check(topRequest), "the top folder itself");
        RealGate.Refused(gate.Check(rootRequest), "the volume root");
    }

    // Decision 7 (b), criterion 7: "an Administrators-owned folder ... refused": the real top folders of the volume that
    // this user does not own (made by an installer or from an elevated prompt, or the system's) refuse what is inside
    // them, whoever owns the item. At least one of them reaches Lean's rule (the earlier steps pass) and is refused by it.
    [TestMethod]
    [TestCategory("Console")]
    public void ItemsInRealTopFoldersTheUserDoesNotOwnAreRefused()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var cases = new List<(GateRequest Request, string Owner)>();
        foreach (var top in Directory.EnumerateDirectories(RealGate.VolumeRoot))
        {
            var owner = OwnerOf(top);
            // Users holds this user's own profile (root (a)); it has a test of its own below.
            if (owner is null || owner.Equals(me) || IsLink(top) || IsUsers(top))
            {
                continue;
            }

            if (FirstReadableFileInside(top) is { } item && FileFacts.Read(item) is { Succeeded: true } facts)
            {
                cases.Add((new GateRequest(item, facts.Stamp!.Value), Name(owner)));
            }
        }

        Check.True(cases.Count > 0, "no readable item inside a top folder of " + RealGate.VolumeRoot + " that this user does not own");
        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        var byTheRule = new List<string>();
        foreach (var (request, owner) in cases)
        {
            var result = gate.Check(request);
            RealGate.Refused(result, $"{request.Path} (top folder owned by {owner})");
            if (result.Reason == RefusalReason.NotAllowed)
            {
                Assert.AreEqual(Copy.Text.HintOutside, result.Message, request.Path);
                byTheRule.Add($"{request.Path} ({owner})");
            }
        }

        Check.True(byTheRule.Any(c => c.Contains("Administrators", StringComparison.OrdinalIgnoreCase)),
            "no item in an Administrators-owned top folder reached Lean's rule; cases: " + string.Join("; ", cases.Select(c => c.Request.Path + " (" + c.Owner + ")")));
    }

    // Decision 7 (b): "the top folder and every folder down to the item owned by the current user": the user's own file
    // and folder inside a top folder it does not own (one it may write to, as C:\Temp made by an installer) are refused.
    [TestMethod]
    [TestCategory("Console")]
    public void UsersOwnItemsInsideATopFolderItDoesNotOwnAreRefused()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        using var scratch = new RealScratch();
        string? folder = null;
        string? owner = null;
        foreach (var top in Directory.EnumerateDirectories(RealGate.VolumeRoot))
        {
            var topOwner = OwnerOf(top);
            if (topOwner is null || topOwner.Equals(me) || IsLink(top) || IsSystemOrUsers(top))
            {
                continue;
            }

            try
            {
                folder = scratch.NewFolder(top);
                owner = Name(topOwner);
                break;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        if (folder is null)
        {
            Assert.Inconclusive("no top folder of " + RealGate.VolumeRoot + " owned by someone else lets this user make a folder in it");
            return;
        }

        var file = RealScratch.File(Path.Combine(folder, "mine.bin"));
        Assert.IsTrue(OwnerOf(folder)!.Equals(me), "setup: the user owns the folder it made");
        var requests = new[] { RealGate.Request(folder), RealGate.Request(file) };

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), $"{request.Path} (top folder owned by {owner})");
        }
    }

    // Decision 7 (b): "never a system location or Users": the system folders (refused by the gate's protection, before the
    // rule) and Users outside this user's profile.
    [TestMethod]
    [TestCategory("Console")]
    public void SystemFoldersAndUsersAreRefused()
    {
        var candidates = new List<string>();
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(RealGate.Profile)!,
        })
        {
            // Never this user's own profile, which is root (a).
            if (FirstReadableFileInside(folder, 3, RealGate.Profile) is { } item)
            {
                candidates.Add(item);
            }
        }

        candidates.Add(Path.GetDirectoryName(RealGate.Profile)!);
        var requests = candidates.Where(c => FileFacts.Read(c).Succeeded).Select(c => RealGate.Request(c)).ToList();
        Check.True(requests.Count >= 4, "setup: too few readable system items: " + string.Join("; ", candidates));

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.Refused(gate.Check(request), request.Path);
        }
    }

    // Decision 7: "only on the volume that holds the user's profile ... Other volumes are shown, never changed": a folder
    // the user made at the root of another fixed NTFS volume passes the gate's earlier steps and is refused by the rule.
    [TestMethod]
    [TestCategory("Console")]
    public void UserMadeTopFolderOnAnotherVolumeIsRefused()
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

        var file = RealScratch.File(Path.Combine(top, "old.iso"));
        var folder = RealScratch.Folder(Path.Combine(top, "Archive"));
        var requests = new[] { RealGate.Request(file), RealGate.Request(folder) };

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), request.Path);
        }
    }

    // Criterion 7 ("a junction [at the root of C:] is refused") and docs/spec/02 Windows note (b), "none of them a link":
    // a junction at the volume root and one inside the user's own top folder are refused; an item reached through a
    // junction from root (b) into AppData is judged where it really is, and refused.
    [TestMethod]
    [TestCategory("Console")]
    public void JunctionsAreRefusedAndDoNotCarryAnAppDataItemIntoRootB()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.VolumeRoot);
        var real = RealScratch.Folder(Path.Combine(top, "real"));
        RealScratch.File(Path.Combine(real, "x.bin"));
        var inner = Path.Combine(top, "jump");
        Native.CreateJunction(inner, real);
        var atRoot = RealScratch.NewName(RealGate.VolumeRoot);
        Native.CreateJunction(atRoot, top);
        scratch.Track(atRoot);
        var appData = scratch.NewFolder(RealGate.Roaming);
        var hidden = RealScratch.File(Path.Combine(appData, "settings.dat"));
        var intoAppData = Path.Combine(top, "appdata");
        Native.CreateJunction(intoAppData, appData);
        var junctionRequests = new[] { new GateRequest(inner, RealGate.Facts(real).Stamp!.Value), new GateRequest(atRoot, RealGate.Facts(top).Stamp!.Value) };
        var through = RealGate.Request(Path.Combine(intoAppData, "settings.dat"), hidden);
        var control = RealGate.Request(Path.Combine(real, "x.bin"));

        var gate = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems);
        RealGate.Approved(gate.Check(control), "control: the plain file next to the junctions");
        foreach (var request in junctionRequests)
        {
            var result = gate.Check(request);
            Assert.IsFalse(result.IsApproved, request.Path + " is a junction");
            Assert.AreEqual(RefusalReason.LinkOrSpecialFile, result.Reason, request.Path + ": " + result.Message);
        }

        Assert.IsFalse(gate.Check(through).IsApproved, through.Path + " is in AppData, reached through a junction in root (b)");
    }

    // Decision 7 (c), plan "Rules": an Uninstall item whose canonical path is exactly one of the matcher's items is
    // approved, in each leftover folder (docs/spec/04: %APPDATA%, %LOCALAPPDATA%, %LOCALAPPDATA%\Programs, and
    // <Publisher>\<DisplayName>), a folder or a file, whatever the spelling of the request.
    [TestMethod]
    [TestCategory("Console")]
    public void UninstallItemsExactlyInTheModuleSetAreApproved()
    {
        using var scratch = new RealScratch();
        var roaming = scratch.NewFolder(RealGate.Roaming);
        RealScratch.File(Path.Combine(roaming, "settings.json"));
        var local = scratch.NewFolder(RealGate.Local);
        RealScratch.File(Path.Combine(local, "cache.bin"));
        var programs = scratch.NewFolder(RealGate.LocalPrograms);
        RealScratch.File(Path.Combine(programs, "app.exe"));
        var vendorApp = RealScratch.Folder(Path.Combine(scratch.NewFolder(RealGate.Roaming), "Contoso Notes"));
        RealScratch.File(Path.Combine(vendorApp, "notes.db"));
        var file = scratch.NewFile(RealGate.Local, ".log");
        string[] items = [roaming, local, programs, vendorApp, file];
        var set = RealGate.Items(items);
        var requests = items.Select(RealGate.Request).ToList();
        requests.Add(RealGate.Request(roaming.ToUpperInvariant(), roaming));

        var gate = RealGate.ForDelete(LeanModule.Uninstall, set);
        foreach (var request in requests)
        {
            RealGate.Approved(gate.Check(request), "Uninstall: " + request.Path);
        }
    }

    // Decision 7 (c): "each only for the exact items its module produced": an item inside a module item, a sibling, or the
    // vendor folder above a <Publisher>\<DisplayName> item is not in the set and is refused.
    [TestMethod]
    [TestCategory("Console")]
    public void UninstallItemsNotExactlyInTheSetAreRefused()
    {
        using var scratch = new RealScratch();
        var leftover = scratch.NewFolder(RealGate.Roaming);
        var inside = RealScratch.File(Path.Combine(leftover, "settings.json"));
        var insideFolder = RealScratch.Folder(Path.Combine(leftover, "Cache"));
        RealScratch.File(Path.Combine(insideFolder, "c.bin"));
        var sibling = scratch.NewFolder(RealGate.Roaming);
        RealScratch.File(Path.Combine(sibling, "other.json"));
        var vendor = scratch.NewFolder(RealGate.Local);
        var vendorApp = RealScratch.Folder(Path.Combine(vendor, "Contoso Notes"));
        RealScratch.File(Path.Combine(vendorApp, "notes.db"));
        var set = RealGate.Items(leftover, vendorApp);
        var requests = new[] { inside, insideFolder, sibling, vendor }.Select(RealGate.Request).ToList();

        var gate = RealGate.ForDelete(LeanModule.Uninstall, set);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), "Uninstall: " + request.Path);
        }
    }

    // Decision 7 (c): "LeanRules.ForDelete(module) admits (c) only for that module": the same items, given to the map,
    // large and old, or duplicates, are refused; the Uninstall module with no items admits none.
    [TestMethod]
    [TestCategory("Console")]
    public void ModuleItemsAreNeverApprovedForAnotherModule()
    {
        using var scratch = new RealScratch();
        var leftover = scratch.NewFolder(RealGate.Roaming);
        RealScratch.File(Path.Combine(leftover, "settings.json"));
        var programs = scratch.NewFolder(RealGate.LocalPrograms);
        RealScratch.File(Path.Combine(programs, "app.exe"));
        var set = RealGate.Items(leftover, programs);
        var requests = new[] { RealGate.Request(leftover), RealGate.Request(programs) };

        foreach (var module in new[] { LeanModule.Map, LeanModule.LargeOld, LeanModule.Duplicates })
        {
            var gate = RealGate.ForDelete(module, set);
            foreach (var request in requests)
            {
                RealGate.RefusedByTheRule(gate.Check(request), module + ": " + request.Path);
            }
        }

        var empty = RealGate.ForDelete(LeanModule.Uninstall, RealGate.NoItems);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(empty.Check(request), "Uninstall with no items: " + request.Path);
        }
    }

    // Decision 7 (c), docs/spec/02 Windows note (c): "exactly an item a module produced in its module-bound root"; the
    // Uninstall module's root is the leftover folders of docs/spec/04, and %USERPROFILE%\<App> "is never moved". So an
    // item given in the set outside them (directly in the profile; in a top folder of the volume the user does not own)
    // is not admitted by (c).
    [TestMethod]
    [TestCategory("Console")]
    public void ModuleItemsOutsideTheLeftoverFoldersAreRefused()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        using var scratch = new RealScratch();
        var inProfile = scratch.NewFolder(RealGate.Profile);
        RealScratch.File(Path.Combine(inProfile, "app.exe"));
        var items = new List<string> { inProfile };
        foreach (var top in Directory.EnumerateDirectories(RealGate.VolumeRoot))
        {
            var owner = OwnerOf(top);
            if (owner is null || owner.Equals(me) || IsLink(top) || IsSystemOrUsers(top))
            {
                continue;
            }

            try
            {
                var elsewhere = scratch.NewFolder(top);
                RealScratch.File(Path.Combine(elsewhere, "app.exe"));
                items.Add(elsewhere);
                break;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        var set = RealGate.Items([.. items]);
        var requests = items.Select(RealGate.Request).ToList();

        var gate = RealGate.ForDelete(LeanModule.Uninstall, set);
        foreach (var request in requests)
        {
            RealGate.RefusedByTheRule(gate.Check(request), "Uninstall item outside the leftover folders: " + request.Path);
        }
    }

    // Plan "Rules": roots (a) and (b) hold for every module, the Uninstall module included.
    [TestMethod]
    [TestCategory("Console")]
    public void RootsAAndBHoldForEveryModule()
    {
        using var scratch = new RealScratch();
        var inProfile = RealScratch.File(Path.Combine(scratch.NewFolder(RealGate.Profile), "a.bin"));
        var inTop = RealScratch.File(Path.Combine(scratch.NewFolder(RealGate.VolumeRoot), "b.bin"));
        var requests = new[] { RealGate.Request(inProfile), RealGate.Request(inTop) };

        foreach (var module in Enum.GetValues<LeanModule>())
        {
            var gate = RealGate.ForDelete(module, RealGate.NoItems);
            foreach (var request in requests)
            {
                if (module == LeanModule.Installers)
                    RealGate.RefusedByTheRule(gate.Check(request), module + ": " + request.Path);
                else
                    RealGate.Approved(gate.Check(request), module + ": " + request.Path);
            }
        }
    }

    // The approved item is the canonical path the rule saw (Kit: "the app moves exactly this path").
    [TestMethod]
    [TestCategory("Console")]
    public void ApprovalCarriesTheCanonicalPathTheRuleSaw()
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var file = RealScratch.File(Path.Combine(top, "item.bin"));
        var request = RealGate.Request(file.ToUpperInvariant(), file);
        var canonical = RealGate.Canonical(file);

        var result = RealGate.ForDelete(LeanModule.Map, RealGate.NoItems).Check(request);
        RealGate.Approved(result, request.Path);
        Assert.AreEqual(canonical, result.Approved!.CanonicalPath);
        Assert.AreEqual(canonical, result.Approved.Facts.CanonicalPath);
    }

    internal static SecurityIdentifier? OwnerOf(string path)
    {
        try
        {
            return new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static bool IsSystemOrUsers(string top)
    {
        var system = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(RealGate.Profile)!,
            Path.Combine(RealGate.VolumeRoot, "Recovery"),
            Path.Combine(RealGate.VolumeRoot, "System Volume Information"),
            Path.Combine(RealGate.VolumeRoot, "$Recycle.Bin"),
        };
        return system.Any(s => string.Equals(Path.TrimEndingDirectorySeparator(s), Path.TrimEndingDirectorySeparator(top), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsUsers(string top) =>
        string.Equals(Path.TrimEndingDirectorySeparator(top), Path.GetDirectoryName(RealGate.Profile), StringComparison.OrdinalIgnoreCase);

    private static string Name(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    // The first plain file (no reparse point) the user can read inside the folder, at most 3 levels down, never inside
    // the folder to leave out.
    internal static string? FirstReadableFileInside(string folder, int depth = 3, string? leaveOut = null)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !attributes.HasFlag(FileAttributes.Offline))
                    {
                        return file;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (depth == 0)
            {
                return null;
            }

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (leaveOut is not null && string.Equals(Path.TrimEndingDirectorySeparator(sub), Path.TrimEndingDirectorySeparator(leaveOut), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsLink(sub) && FirstReadableFileInside(sub, depth - 1, leaveOut) is { } found)
                {
                    return found;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
