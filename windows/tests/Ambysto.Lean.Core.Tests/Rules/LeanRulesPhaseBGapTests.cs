using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary>Lean's allowed-root rule, root (b), in the phase B gaps (briefs R1–R4 and R6): docs/spec/02 Windows note (b)
/// "inside a folder at the root of that volume that the current user owns, and every folder down to the item too (a
/// folder owned by <c>Administrators</c> does not count; the item itself, when it is a folder, too; a file item only has to
/// be readable: owner decision 2026-10-17), none of them a link …, never a system location or <c>Users</c>", "Never an item
/// on another volume" ("only on the volume that holds the profile"); plan "Rules" ("an unknown owner is not the user's")
/// and the principle "Fail closed, and say so"; plan "Display-only hints in phase A" (<see cref="LeanRules.Preview"/>).
/// The rule is called directly with the Kit's facts built by <see cref="KitObjects.Facts"/> (owners from the target up to
/// the volume root); the rule asks the real file system whether the target is a folder, so the targets are real, in a top
/// folder this test makes at the root of the profile's volume (owned by this user), removed after.</summary>
[TestClass]
public sealed partial class LeanRulesPhaseBGapTests
{
    private const string TopFolderPrefix = "LeanRulesTests-"; // swept by PreviewHardFixTests when a run left one behind
    private const string Administrators = "S-1-5-32-544";
    private const string LocalSystem = "S-1-5-18";

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static string Me => WindowsIdentity.GetCurrent().User!.Value;

    private static AllowedRootDecision Evaluate(ItemFacts facts) =>
        LeanRules.ForDelete(LeanModule.Map, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Evaluate(facts);

    private static OwnerFact Mine(string path) => new(path, Me, true);

    private static OwnerFact Other(string path) => new(path, Administrators, false);

    private static OwnerFact SystemOwned(string path) => new(path, LocalSystem, false);

    private static void Allowed(AllowedRootDecision decision, string what) =>
        Assert.IsTrue(decision.IsAllowed, what + ": refused with " + decision.Message);

    private static void RefusedByTheRule(AllowedRootDecision decision, string what)
    {
        Assert.IsFalse(decision.IsAllowed, what + ": allowed");
        Assert.AreEqual(Text.HintOutside, decision.Message, what);
    }

    /// <summary><c>&lt;volume root&gt;\LeanRulesTests-…\sub\file.bin</c> and <c>…\sub\folder\</c>, made by this user.</summary>
    private sealed class World : IDisposable
    {
        public World()
        {
            Root = PathContext.Current().ProfileVolumeRoot;
            Top = Path.Combine(Root, TopFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
            Sub = Path.Combine(Top, "sub");
            Folder = Path.Combine(Sub, "folder");
            Directory.CreateDirectory(Folder);
            File = Path.Combine(Sub, "file.bin");
            TempFolder.WriteBytes(File, 4096, 81);
            Serial = KitObjects.RealStamp(Top).Identity.VolumeSerial;
        }

        public string Root { get; }

        public string Top { get; }

        public string Sub { get; }

        public string Folder { get; }

        public string File { get; }

        public ulong Serial { get; }

        /// <summary>Owners [target, sub, top, volume root]: the target's and sub's as given, top the user's, the root SYSTEM's.</summary>
        public IReadOnlyList<OwnerFact> Chain(string target, bool targetMine, bool subMine = true) =>
        [
            targetMine ? Mine(target) : Other(target),
            subMine ? Mine(Sub) : Other(Sub),
            Mine(Top),
            SystemOwned(Root),
        ];

        public ItemFacts Facts(string target, IReadOnlyList<OwnerFact> owners, ulong? systemVolumeSerial = null) =>
            KitObjects.Facts(target, Serial, owners, isSystemLocation: false, systemVolumeSerial: systemVolumeSerial);

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(Top); attempt++)
            {
                try
                {
                    Directory.Delete(Top, recursive: true);
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
    }

    // R1 [top]: root (b): a file item only has to be readable (owned by another account: allowed), a folder item must be
    // the user's (owned by another account: refused by the rule). Controls: both owned by the user are allowed; a sub
    // folder owned by another account refuses both.
    [TestMethod]
    public void AFileItemNeedsNoOwnerButAFolderItemMustBeTheUsers()
    {
        using var w = new World();

        Allowed(Evaluate(w.Facts(w.File, w.Chain(w.File, targetMine: false))), "a readable file another account owns");
        RefusedByTheRule(Evaluate(w.Facts(w.Folder, w.Chain(w.Folder, targetMine: false))), "a folder another account owns");

        Allowed(Evaluate(w.Facts(w.File, w.Chain(w.File, targetMine: true))), "control: the user's file");
        Allowed(Evaluate(w.Facts(w.Folder, w.Chain(w.Folder, targetMine: true))), "control: the user's folder");
        RefusedByTheRule(Evaluate(w.Facts(w.File, w.Chain(w.File, targetMine: true, subMine: false))), "control: a file below a folder another account owns");
        RefusedByTheRule(Evaluate(w.Facts(w.Folder, w.Chain(w.Folder, targetMine: true, subMine: false))), "control: a folder below a folder another account owns");
    }

    // R2 [normal]: an item whose volume is not the profile's is refused by the rule itself. Control: equal serials.
    [TestMethod]
    public void AnItemOffTheProfilesVolumeIsRefusedByTheRule()
    {
        using var w = new World();
        var owners = w.Chain(w.File, targetMine: true);

        RefusedByTheRule(Evaluate(w.Facts(w.File, owners, systemVolumeSerial: w.Serial + 1)), "an item off the profile's volume");
        Allowed(Evaluate(w.Facts(w.File, owners, systemVolumeSerial: w.Serial)), "control: the profile's volume");
    }

    // R3 [low]: (a) a system location is refused by Lean's rule even when the Kit's facts do not flag it (the Kit refuses
    // these earlier; this pins Lean's own line of defence), owners all the user's. (b) An owner chain of 0 entries, or of 1
    // entry, is refused (fail closed). Control: the full chain, all the user's, is allowed.
    [TestMethod]
    public void TheRuleRefusesASystemLocationAndAnIncompleteOwnerChain()
    {
        using var w = new World();
        var name = "LeanRulesTests-" + Guid.NewGuid().ToString("N")[..8];
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", name + ".bin");
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name, "y.bin");
        foreach (var path in new[] { windowsTemp, programFiles })
        {
            var owners = new List<OwnerFact>();
            for (var p = path; p is not null; p = Path.GetDirectoryName(p))
            {
                owners.Add(Mine(p));
            }

            RefusedByTheRule(Evaluate(KitObjects.Facts(path, w.Serial, owners, isSystemLocation: false)), "a system location: " + path);
        }

        RefusedByTheRule(Evaluate(w.Facts(w.File, [])), "no owners at all");
        RefusedByTheRule(Evaluate(w.Facts(w.File, [Mine(w.File)])), "only the item's own owner");
        Allowed(Evaluate(w.Facts(w.File, [Mine(w.File), Mine(w.Sub), Mine(w.Top), Mine(w.Root)])), "control: the full chain, all the user's");
    }

    private static SecurityIdentifier? OwnerOf(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? FirstReadableFile(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).Take(50).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) == 0
                    && (File.GetAttributes(Path.GetDirectoryName(file)!) & FileAttributes.ReparsePoint) == 0)
                {
                    return file;
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    // R4 (a) [normal]: the pre-check of an existing, readable file inside a real top folder of the profile's volume that
    // the user does not own (an Administrators-owned one, such as C:\Ambysto) is false: display-only. Control: a file below
    // a plain folder of the user's own top folder is admitted.
    [TestMethod]
    public void PreCheckRefusesAnItemBelowAForeignFolder()
    {
        var context = PathContext.Current();
        var me = WindowsIdentity.GetCurrent().User!;
        var system = context.SystemFolders.Select(f => Path.TrimEndingDirectorySeparator(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cases = new List<(string File, SecurityIdentifier Owner)>();
        foreach (var top in Directory.EnumerateDirectories(context.ProfileVolumeRoot))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(top);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // another test's top folder, removed meanwhile
            }

            if (top.Contains(TopFolderPrefix, StringComparison.OrdinalIgnoreCase)
                || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0
                || system.Contains(Path.TrimEndingDirectorySeparator(top))
                || string.Equals(Path.TrimEndingDirectorySeparator(top), Path.TrimEndingDirectorySeparator(context.UsersFolder), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (OwnerOf(top) is { } owner && !owner.Equals(me) && FirstReadableFile(top) is { } file)
            {
                cases.Add((file, owner));
            }
        }

        Check.True(cases.Any(c => c.Owner.Value == Administrators),
            "no readable file inside an Administrators-owned top folder of " + context.ProfileVolumeRoot + "; cases: " + string.Join("; ", cases.Select(c => c.File)));
        foreach (var (file, owner) in cases)
        {
            Assert.IsFalse(LeanRules.Preview(file, context), $"{file} (top folder owned by {owner.Value}) is admitted");
        }

        using var w = new World();
        Assert.IsTrue(LeanRules.Preview(w.File, context), "control: a file below a plain folder of the user's own top folder");
    }

    // R4 (b) [normal]: the pre-check of a plain file below a folder that carries a reparse point that is not a link (a
    // third-party tag the standard user sets; inconclusive when it may not) is false. Control: the same file below a plain
    // folder is admitted.
    [TestMethod]
    public void PreCheckRefusesAnItemBelowANonLinkReparseFolder()
    {
        var context = PathContext.Current();
        using var w = new World();
        var folder = Path.Combine(w.Sub, "tagged");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "plain.bin");
        TempFolder.WriteBytes(file, 4096, 82);
        Assert.IsTrue(LeanRules.Preview(file, context), "control: the file below the plain folder is admitted");

        var set = Reparse.TrySetThirdParty(folder, out var why);
        if (!set)
        {
            Assert.Inconclusive("this user cannot set a non-link reparse point on a folder with children: " + why);
        }

        try
        {
            var attributes = File.GetAttributes(folder);
            if ((attributes & FileAttributes.ReparsePoint) == 0 || new DirectoryInfo(folder).LinkTarget is not null || !File.Exists(file))
            {
                Assert.Inconclusive($"the fixture is not a non-link reparse folder holding a readable file (attributes {attributes}, file exists {File.Exists(file)})");
            }

            Assert.IsFalse(LeanRules.Preview(file, context), "a file below a folder with a reparse point that is not a link");
        }
        finally
        {
            Reparse.Remove(folder);
        }
    }

    // R6 [low]: a target whose kind cannot be read is judged as a folder (fails closed): owners [another account (target),
    // user, user, SYSTEM]. (a) The target does not exist (its folder does): refused, no exception. (b) The user may not read
    // the target's attributes (read-attributes denied on the file, list-folder denied on its folder; restored after):
    // refused, no exception. Control: the same owners on a readable file are allowed (R1 (a)).
    [TestMethod]
    [DoNotParallelize]
    public void ATargetOfUnknownKindIsJudgedAsAFolder()
    {
        using var w = new World();
        var gone = Path.Combine(w.Sub, "gone.bin");
        RefusedByTheRule(Evaluate(w.Facts(gone, w.Chain(gone, targetMine: false))), "a target that does not exist");

        var locked = Path.Combine(w.Sub, "locked.bin");
        TempFolder.WriteBytes(locked, 4096, 83);
        var me = WindowsIdentity.GetCurrent().User!;
        var fileRule = new FileSystemAccessRule(me, FileSystemRights.ReadAttributes, AccessControlType.Deny);
        var folderRule = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var fileInfo = new FileInfo(locked);
        var folderInfo = new DirectoryInfo(w.Sub);
        var fileSecurity = fileInfo.GetAccessControl();
        fileSecurity.AddAccessRule(fileRule);
        fileInfo.SetAccessControl(fileSecurity);
        var folderSecurity = folderInfo.GetAccessControl();
        folderSecurity.AddAccessRule(folderRule);
        folderInfo.SetAccessControl(folderSecurity);
        try
        {
            var readable = true;
            try
            {
                _ = File.GetAttributes(locked);
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }

            Check.False(readable, "precondition: the user cannot read the target's attributes");
            RefusedByTheRule(Evaluate(w.Facts(locked, w.Chain(locked, targetMine: false))), "a target whose attributes cannot be read");
        }
        finally
        {
            var currentFolder = folderInfo.GetAccessControl();
            currentFolder.RemoveAccessRuleSpecific(folderRule);
            folderInfo.SetAccessControl(currentFolder);
            var currentFile = fileInfo.GetAccessControl();
            currentFile.RemoveAccessRuleSpecific(fileRule);
            fileInfo.SetAccessControl(currentFile);
        }

        Allowed(Evaluate(w.Facts(w.File, w.Chain(w.File, targetMine: false))), "control: a readable file with the same owners");
    }

    /// <summary>A third-party (non-Microsoft) reparse point on a folder: <c>REPARSE_GUID_DATA_BUFFER</c> with a tag that
    /// has the directory bit, so the folder may keep its children. Test-project only.</summary>
    private static partial class Reparse
    {
        private const uint GenericWrite = 0x40000000;
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint SetReparsePoint = 0x000900A4;
        private const uint DeleteReparsePoint = 0x000900AC;
        private const uint Tag = 0x10000B0B; // not Microsoft's, directory bit set
        private static readonly Guid TagGuid = new("6d1c3b84-7f0e-4c3a-9a51-0b0b4c65616e");

        public static bool TrySetThirdParty(string folder, out string why)
        {
            var data = new byte[] { 1, 2, 3, 4 };
            var buffer = new byte[24 + data.Length];
            BitConverter.TryWriteBytes(buffer.AsSpan(0), Tag);
            BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)data.Length);
            TagGuid.TryWriteBytes(buffer.AsSpan(8));
            data.CopyTo(buffer, 24);
            return Control(folder, SetReparsePoint, buffer, out why);
        }

        public static void Remove(string folder)
        {
            var buffer = new byte[24];
            BitConverter.TryWriteBytes(buffer.AsSpan(0), Tag);
            TagGuid.TryWriteBytes(buffer.AsSpan(8));
            Control(folder, DeleteReparsePoint, buffer, out _);
        }

        private static unsafe bool Control(string folder, uint code, byte[] buffer, out string why)
        {
            using var handle = CreateFileW(folder, GenericRead | GenericWrite, ShareAll, nint.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, nint.Zero);
            if (handle.IsInvalid)
            {
                why = new Win32Exception(Marshal.GetLastPInvokeError()).Message + " (open)";
                return false;
            }

            fixed (byte* p = buffer)
            {
                if (!DeviceIoControl(handle, code, (nint)p, (uint)buffer.Length, nint.Zero, 0, out _, nint.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    why = $"{new Win32Exception(error).Message} ({error})";
                    return false;
                }
            }

            why = "";
            return true;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
    }
}
