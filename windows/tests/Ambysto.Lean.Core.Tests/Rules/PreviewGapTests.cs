using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary>Gap briefs H01-H07 (Stryker run 1): behaviours of the read-only pre-check (<see cref="LeanRules.Preview"/>)
/// and its hint (<see cref="DisplayOnly.HintFor"/>) that no test pinned, written from docs/spec/02 (Windows note on
/// allowed roots, "Comparison is done on canonical paths") and docs/spec/01 (Windows notes on links and hints). Roots (b)
/// on the real volume are top folders this test makes at the root of the profile's volume (the standard user may, and owns
/// them), removed afterwards. Not in parallel: some tests create folders in the real profile.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PreviewGapTests
{
    private const string TopFolderPrefix = "LeanRulesGapTests-";

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [ClassInitialize]
    public static void SweepStaleFolders(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var volumeRoot = PathContext.Current().ProfileVolumeRoot;
        foreach (var top in new[] { volumeRoot }.Concat(AdministratorsTopFolders(volumeRoot)))
        {
            try
            {
                foreach (var stale in Directory.EnumerateDirectories(top, TopFolderPrefix + "*"))
                {
                    TryDelete(stale);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static void TryDelete(string folder)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static SecurityIdentifier? OwnerOf(string path)
    {
        try
        {
            var security = Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsOwnedBy(string path, SecurityIdentifier owner) => OwnerOf(path) is { } actual && actual.Equals(owner);

    /// <summary>Top folders of <paramref name="volumeRoot"/> owned by BUILTIN\Administrators that are no system folder,
    /// not Users and not another Ambysto test bed folder, the conventional scratch names first.</summary>
    private static IEnumerable<string> AdministratorsTopFolders(string volumeRoot)
    {
        var context = PathContext.Current();
        var excluded = new HashSet<string>(context.SystemFolders.Select(f => f.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase) { context.UsersFolder.TrimEnd('\\') };
        IEnumerable<string> tops;
        try
        {
            tops = Directory.EnumerateDirectories(volumeRoot).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        string[] preferred = ["Temp", "Tools", "work"];
        foreach (var top in tops.OrderBy(t => Array.FindIndex(preferred, p => string.Equals(p, Path.GetFileName(t), StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : preferred.Length))
        {
            var name = Path.GetFileName(top);
            if (excluded.Contains(top.TrimEnd('\\')) || name.StartsWith("Ambysto", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AmbyNet", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith('$') || new DirectoryInfo(top).LinkTarget is not null)
            {
                continue;
            }

            if (IsOwnedBy(top, Administrators))
            {
                yield return top;
            }
        }
    }

    /// <summary>A top folder at the root of the profile's volume, made by (and so owned by) the current user: root (b).</summary>
    private sealed class OwnTopFolder : IDisposable
    {
        public OwnTopFolder(PathContext context)
        {
            Path = System.IO.Path.Combine(context.ProfileVolumeRoot, TopFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose() => TryDelete(Path);
    }

    // Gap brief H01. docs/spec/02 Windows note (a): AppData is refused "except %LOCALAPPDATA%\Temp\* and
    // %LOCALAPPDATA%\CrashDumps\* (by these literal paths)"; "Comparison is done on canonical paths". A sibling whose
    // name starts with a protected or excepted folder's name is another folder, judged by its own name.
    [TestMethod]
    public void PrefixSiblingsAreJudgedByTheirOwnName()
    {
        using var fx = new ProfileFixture("gap-prefix-siblings");
        var tempArchive = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "TempArchive", "x.bin"));
        var crashDumpsOld = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "CrashDumpsOld", "x.bin"));
        foreach (var refused in new[] { tempArchive, crashDumpsOld })
        {
            Assert.IsFalse(LeanRules.Preview(refused, fx.Context), refused + " is in AppData, not in the Temp or CrashDumps exception");
            Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(refused, fx.Context), refused);
        }

        var inTemp = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Temp", "x.bin"));
        Assert.IsTrue(LeanRules.Preview(inTemp, fx.Context), "control: %LOCALAPPDATA%\\Temp\\x.bin is the exception");
        Assert.IsNull(DisplayOnly.HintFor(inTemp, fx.Context));

        var appDataBackup = ProfileFixture.MakeFile(fx.InProfile("AppDataBackup", "a", "x.bin"));
        Assert.IsTrue(LeanRules.Preview(appDataBackup, fx.Context), "AppDataBackup is not AppData");
        Assert.IsNull(DisplayOnly.HintFor(appDataBackup, fx.Context), appDataBackup);

        var oneDriveOld = ProfileFixture.MakeFile(fx.InProfile("OneDriveOld", "x"));
        Assert.AreNotEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(oneDriveOld, fx.Context), "OneDriveOld is not the sync root OneDrive");
        Assert.IsTrue(LeanRules.Preview(oneDriveOld, fx.Context), "two components below the profile");
        Assert.IsNull(DisplayOnly.HintFor(oneDriveOld, fx.Context), oneDriveOld);

        var usersData = ProfileFixture.MakeFile(fx.OnVolume("UsersData", "x.bin"));
        Assert.IsTrue(LeanRules.Preview(usersData, fx.Context), "an own top folder named UsersData is not Users");
        Assert.IsNull(DisplayOnly.HintFor(usersData, fx.Context), usersData);

        var programDataOld = ProfileFixture.MakeFile(fx.OnVolume("ProgramDataOld", "x.bin"));
        Assert.IsTrue(LeanRules.Preview(programDataOld, fx.Context), "an own top folder named ProgramDataOld is not ProgramData");
        Assert.IsNull(DisplayOnly.HintFor(programDataOld, fx.Context), programDataOld);
    }

    // Gap brief H02. docs/spec/02 Windows note (b): "inside a folder at the root of that volume that the current user
    // owns, and every folder down to the item too (a folder owned by Administrators does not count)"; intent criterion 7.
    // A folder and a file the user made (so owns) inside a top folder owned by Administrators are refused and get the
    // outside hint. Control: the same layout inside a top folder the user made is admitted. The fixture is an existing
    // top folder owned by Administrators in which this user may create folders (the test bed's C:\Temp and the like).
    [TestMethod]
    public void UserOwnedChildOfAnAdministratorsTopFolderIsRefused()
    {
        var context = PathContext.Current();
        var me = WindowsIdentity.GetCurrent().User!;
        string? made = null;
        foreach (var top in AdministratorsTopFolders(context.ProfileVolumeRoot))
        {
            var candidate = Path.Combine(top, TopFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(candidate, "sub"));
                made = candidate;
                break;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        if (made is null)
        {
            Assert.Inconclusive("no top folder of " + context.ProfileVolumeRoot + " is owned by Administrators and lets this user create a folder");
            return;
        }

        try
        {
            var folder = Path.Combine(made, "sub");
            var file = Path.Combine(made, "sub", "x.bin");
            File.WriteAllBytes(file, new byte[4096]);
            var top = Path.GetDirectoryName(made)!;
            Assert.IsTrue(IsOwnedBy(top, Administrators), "fixture: " + top + " is owned by Administrators");
            Assert.IsTrue(IsOwnedBy(made, me) && IsOwnedBy(folder, me) && IsOwnedBy(file, me), "fixture: everything below " + top + " is the user's");
            foreach (var item in new[] { folder, file, made })
            {
                Assert.IsFalse(LeanRules.Preview(item, context), item + " lies in a top folder owned by Administrators");
                Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(item, context), item);
            }
        }
        finally
        {
            TryDelete(made);
        }

        using var own = new OwnTopFolder(context);
        Directory.CreateDirectory(own.Sub("x", "sub"));
        File.WriteAllBytes(own.Sub("x", "sub", "x.bin"), new byte[4096]);
        Assert.IsTrue(LeanRules.Preview(own.Sub("x", "sub"), context), "control: the folder in the user's own top folder");
        Assert.IsTrue(LeanRules.Preview(own.Sub("x", "sub", "x.bin"), context), "control: the file in the user's own top folder");
        Assert.IsNull(DisplayOnly.HintFor(own.Sub("x", "sub", "x.bin"), context));
    }

    /// <summary>The admin-made fixture of brief H03 (never by a standard-user test): run
    /// windows/tests/fixtures/Set-OtherOwnerFixture.ps1 elevated, then this test as the standard user,
    /// then the script with -Remove. It creates a fresh fixture and never changes existing settings. A top folder at the root of
    /// the profile's volume owned by the test user, holding a folder and a readable file owned by BUILTIN\Administrators:
    /// <code>
    /// mkdir C:\LeanRulesAdminFixture\AdminFolder
    /// fsutil file createnew C:\LeanRulesAdminFixture\admin-file.bin 4096
    /// icacls C:\LeanRulesAdminFixture\AdminFolder /setowner Administrators
    /// icacls C:\LeanRulesAdminFixture\admin-file.bin /setowner Administrators
    /// icacls C:\LeanRulesAdminFixture /grant ambytest:(OI)(CI)RX
    /// icacls C:\LeanRulesAdminFixture /setowner ambytest
    /// </code>
    /// (no /T: never walk a tree with icacls).</summary>
    private const string AdminFixtureName = "LeanRulesAdminFixture";

    // Gap brief H03. docs/spec/02 (b): "(…the item itself, when it is a folder, too; a file item only has to be readable:
    // owner decision 2026-10-17)". Inside a top folder the user owns, a folder owned by Administrators is refused and gets
    // the outside hint; a readable file owned by Administrators next to it is admitted. Needs the admin-made fixture above;
    // inconclusive without it (a standard user cannot give an item to another owner).
    [TestMethod]
    public void FolderItemOwnedBySomeoneElseIsRefusedButAReadableFileIsNot()
    {
        var context = PathContext.Current();
        var me = WindowsIdentity.GetCurrent().User!;
        var top = Path.Combine(context.ProfileVolumeRoot, AdminFixtureName);
        var folder = Path.Combine(top, "AdminFolder");
        var file = Path.Combine(top, "admin-file.bin");
        if (!Directory.Exists(folder) || !File.Exists(file))
        {
            Assert.Inconclusive("the admin fixture " + top + " is missing (run windows/tests/fixtures/Set-OtherOwnerFixture.ps1 elevated, then test as the standard user)");
            return;
        }

        if (!IsOwnedBy(top, me) || !IsOwnedBy(folder, Administrators) || !IsOwnedBy(file, Administrators))
        {
            Assert.Inconclusive($"the admin fixture {top} is not as described: owners {OwnerOf(top)}, {OwnerOf(folder)}, {OwnerOf(file)}");
            return;
        }

        Assert.AreEqual(new FileInfo(file).Length, File.ReadAllBytes(file).LongLength, "fixture: the file is readable by this user");

        Assert.IsFalse(LeanRules.Preview(folder, context), "a folder item owned by Administrators");
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(folder, context));
        Assert.IsTrue(LeanRules.Preview(file, context), "a readable file item owned by Administrators (owner decision 2026-10-17)");
        Assert.IsNull(DisplayOnly.HintFor(file, context));
    }

    /// <summary>A context with real folders from <paramref name="fx"/>, and <paramref name="profile"/> and
    /// <paramref name="local"/> as given (an unusable spelling in the rows).</summary>
    private static PathContext ContextWith(ProfileFixture fx, string profile, string local) =>
        new(profile, fx.RoamingAppData, local, fx.VolumeRoot, fx.UsersFolder,
            [fx.WindowsFolder, fx.ProgramFiles, fx.ProgramFilesX86, fx.ProgramData], [new SyncRoot(fx.OneDrive, "OneDrive")]);

    // Gap brief H04. docs/spec/02 allowed roots; plan Principles "Fail closed, and say so": a context whose profile folder
    // or local AppData folder cannot be spelled admits nothing (by that root), says "Outside the folders Lean may change",
    // and never throws; the duplicates search with an unusable profile finds no group.
    [TestMethod]
    [DataRow("empty")]
    [DataRow("dot")]
    [DataRow("unc")]
    public async Task ContextWithAnUnusableProfileOrLocalFolderAdmitsNothing(string spelling)
    {
        using var fx = new ProfileFixture("gap-unusable-" + spelling);
        var drive = Path.GetPathRoot(fx.Profile)!.TrimEnd('\\');
        string Unusable(string real) => spelling switch
        {
            "empty" => string.Empty,
            "dot" => real + ".",
            _ => @"\\localhost\" + drive[0] + "$" + real[drive.Length..],
        };

        var document = ProfileFixture.MakeFile(fx.InProfile("Documents", "report.pdf"), 2 * ByteUnits.MB, seed: 71);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "copy.pdf"), 2 * ByteUnits.MB, seed: 71);
        var temp = ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Temp", "installer.tmp"));
        Assert.IsTrue(LeanRules.Preview(document, fx.Context), "control: the real context admits the document");
        Assert.IsTrue(LeanRules.Preview(temp, fx.Context), "control: the real context admits the Temp file");

        var noProfile = ContextWith(fx, Unusable(fx.Profile), fx.LocalAppData);
        var noLocal = ContextWith(fx, fx.Profile, Unusable(fx.LocalAppData));
        Assert.IsFalse(LeanRules.Preview(document, noProfile), "profile " + noProfile.ProfileFolder + ": the document");
        Assert.IsFalse(LeanRules.Preview(temp, noProfile), "profile " + noProfile.ProfileFolder + ": the Temp file");
        Assert.IsFalse(LeanRules.Preview(temp, noLocal), "local AppData " + noLocal.LocalAppData + ": the Temp file");

        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        var control = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(1, control.Count, "control: the real context finds the pair");
        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, noProfile, CancellationToken.None);
        Assert.AreEqual(0, groups.Count, "an unusable profile admits no candidate");

        // The hints last, so a wrong hint is reported after every admission check above has held.
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(temp, noLocal), "local AppData " + noLocal.LocalAppData + ": the Temp file's hint");
        Assert.AreEqual(Text.HintOutside, DisplayOnly.HintFor(document, noProfile), "profile " + noProfile.ProfileFolder + ": the document's hint");
    }

    // Gap brief H05. docs/spec/02 "Comparison is done on canonical paths": "." and ".." in a spelling that stays inside the
    // item's own folders resolve to the same item, plain and with \\?\, and get the same hint.
    [TestMethod]
    public void DotAndDotDotComponentsResolveToTheSameItem()
    {
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        Directory.CreateDirectory(top.Sub("sub"));
        var x = top.Sub("sub", "x");
        File.WriteAllBytes(x, new byte[16]);
        Assert.IsTrue(LeanRules.Preview(x, context), "control: the plain spelling");
        var hint = DisplayOnly.HintFor(x, context);
        string[] spellings =
        [
            top.Path + @"\sub\..\sub\x",
            top.Path + @"\.\sub\x",
            top.Path + @"\sub\.\x",
            @"\\?\" + top.Path + @"\sub\..\sub\x",
            @"\\?\" + top.Path + @"\.\sub\x",
        ];
        foreach (var spelling in spellings)
        {
            Assert.IsTrue(LeanRules.Preview(spelling, context), spelling);
            Assert.AreEqual(hint, DisplayOnly.HintFor(spelling, context), spelling);
        }
    }

    // Gap brief H06. docs/spec/02 "…so an alias spelling … cannot slip past" (plan: 8.3 names expanded): the short
    // spelling of a protected place whose own name is long is judged like the long one: a business OneDrive folder, Program
    // Files, and a deep path whose long form is much longer (more than 64 characters) than its short form.
    [TestMethod]
    public void ShortSpellingOfALongNamedProtectedFolderIsJudgedLikeTheLongOne()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var business = Path.Combine(profile, "OneDrive - Contoso");
        if (Directory.Exists(business))
        {
            Assert.Inconclusive(business + " already exists; not touching it");
        }

        Directory.CreateDirectory(business);
        try
        {
            var context = PathContext.Current();
            var shortBusiness = Native.ShortPathOf(business);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var shortProgramFiles = Native.ShortPathOf(programFiles);
            if (shortBusiness is null || shortProgramFiles is null)
            {
                Assert.Inconclusive("the profile's volume keeps no 8.3 names for " + business + " or " + programFiles);
                return;
            }

            Check.True(!shortBusiness.Contains("OneDrive - Contoso", StringComparison.OrdinalIgnoreCase), "fixture: a short spelling: " + shortBusiness);
            var plan = Path.Combine(shortBusiness, "Documents", "plan.docx");
            Assert.AreEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(Path.Combine(business, "Documents", "plan.docx"), context), "control: the long spelling");
            Assert.AreEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(plan, context), plan);

            var inProgramFiles = Path.Combine(shortProgramFiles, "x.bin");
            Assert.IsFalse(LeanRules.Preview(inProgramFiles, context), inProgramFiles);
            Assert.AreEqual(Text.HintSystemWide, DisplayOnly.HintFor(inProgramFiles, context), inProgramFiles);
        }
        finally
        {
            TryDelete(business);
        }

        // A deep path of long-named folders: admitted in the long form (root (a), Documents), and so in the short one.
        using var fx = new ProfileFixture("gap-short-deep");
        var deep = ProfileFixture.MakeFile(fx.InProfile("Documents", "Quarterly Reports Archive 01", "Quarterly Reports Archive 02",
            "Quarterly Reports Archive 03", "Quarterly Reports Archive 04", "Final Summary Document.pdf"));
        var shortDeep = Native.ShortPathOf(deep);
        if (shortDeep is null)
        {
            Assert.Inconclusive("the scratch volume keeps no 8.3 names for " + deep);
            return;
        }

        Check.True(deep.Length - shortDeep.Length > 64, $"fixture: the long form ({deep.Length}) is more than 64 characters longer than the short one ({shortDeep.Length})");
        Assert.IsTrue(LeanRules.Preview(deep, fx.Context), "control: the long form");
        var answer = Task.Run(() => (LeanRules.Preview(shortDeep, fx.Context), DisplayOnly.HintFor(shortDeep, fx.Context)));
        Assert.IsTrue(answer.Wait(TimeSpan.FromSeconds(10)), "the pre-check of the short spelling returns");
        Assert.IsTrue(answer.Result.Item1, shortDeep + " is judged like " + deep);
        Assert.IsNull(answer.Result.Item2, shortDeep);
    }

    // Gap brief H07. docs/spec/02 (b): "none of them a link (junction, symbolic link or mount point; the item itself
    // neither)"; docs/spec/01 Windows note (2): "Other reparse points are files, not links". In an own top folder, a file
    // that is a reparse point without the name-surrogate bit (an AF_UNIX socket file, which a standard user can make) is
    // admitted; a junction next to it is refused (control).
    [TestMethod]
    public void NonLinkReparsePointItemIsNotALink()
    {
        const uint nameSurrogate = 0x20000000;
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        var socketPath = top.Sub("lean.sock");
        // The socket stays bound for the whole test: .NET removes the socket file when the socket is disposed (measured
        // 2026-10-18), and the bound file opens for attributes like any file.
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        var (attributes, tag) = Native.AttributeTagOf(socketPath);
        Check.True(attributes.HasFlag(FileAttributes.ReparsePoint), "fixture: the socket file is a reparse point (attributes " + attributes + ")");
        Check.True((tag & nameSurrogate) == 0, $"fixture: tag 0x{tag:X8} has no name-surrogate bit");
        Check.False(attributes.HasFlag(FileAttributes.Directory), "fixture: the socket is a file");

        Directory.CreateDirectory(top.Sub("real"));
        Native.CreateJunction(top.Sub("jump"), top.Sub("real"));

        Assert.IsTrue(LeanRules.Preview(socketPath, context), "a reparse point that is not a link is a file, not a link");
        Assert.IsNull(DisplayOnly.HintFor(socketPath, context));
        Assert.IsFalse(LeanRules.Preview(top.Sub("jump"), context), "control: the junction next to it");
    }
}
