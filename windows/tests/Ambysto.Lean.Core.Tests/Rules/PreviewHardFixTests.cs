using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary>Pins the HARD fixes to <see cref="LeanRules.Preview"/> and its helpers (plan, "Build, group 5 core, round 1";
/// docs/spec/02 Windows note on root (b)): a cloud placeholder is never opened and never admitted; a link in root (b),
/// the item itself included, is refused; canonical spelling fails closed (<c>\\?\</c> with "..", names ending in a dot or
/// a space, UNC and device spellings); sync roots come from the machine hive by the user's SID; a business OneDrive
/// folder is a sync root. Real roots (b) are top folders this test makes at the root of the profile's volume (the
/// standard user may, and owns them). Not in parallel: one test edits this process's environment.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PreviewHardFixTests
{
    private const string TopFolderPrefix = "LeanRulesTests-";
    private const string SyncRootManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    private const string Control = "control.bin";

    [ClassInitialize]
    public static void SweepStaleTopFolders(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var volumeRoot = PathContext.Current().ProfileVolumeRoot;
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRootsUnder(volumeRoot, TopFolderPrefix + "*");
        }

        foreach (var stale in Directory.EnumerateDirectories(volumeRoot, TopFolderPrefix + "*"))
        {
            TryDelete(stale);
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

    // docs/spec/02 Windows note (b) and plan round 1 MAJOR: the owner read must never open a cloud placeholder; a
    // placeholder (recall attribute) inside root (b) is not admitted, and nothing is fetched. The positive control shows
    // the fetch log can record a read by this process.
    [TestMethod]
    [TestCategory("Console")]
    public void CloudPlaceholderInsideRootBIsNotAdmittedAndNeverFetched()
    {
        CloudRoot.RequireInstalled();
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        using var cloud = CloudRoot.Register(top.Sub("cloud"), top.Sub("fetch.log"));
        cloud.Populate(("a.bin", ByteUnits.MB), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly("a.bin");
        var plain = top.Sub("plain.bin");
        File.WriteAllBytes(plain, new byte[4096]);

        // Positive control: this process reads one byte of the control placeholder; the log must show it with this pid.
        using (var stream = new FileStream(cloud.Combine(Control), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.True(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        var pid = Environment.ProcessId;
        var mine = cloud.FetchesOf(Control).Where(line => line.Contains("\"pid\":" + pid + ",", StringComparison.Ordinal) || line.Contains("\"pid\":" + pid + "}", StringComparison.Ordinal)).ToList();
        Check.True(mine.Count > 0, $"the fetch log did not record this process's (pid {pid}) read of {Control}; fetches: [{string.Join(" | ", cloud.Fetches())}]");
        cloud.MarkFetchBaseline();

        Assert.IsTrue(LeanRules.Preview(plain, context), "a plain file one level inside the user's own top folder is root (b)");
        Assert.IsFalse(LeanRules.Preview(cloud.Combine("a.bin"), context), "an online-only placeholder inside root (b) is never admitted by the pre-check");
        // The hydrated control carries no recall flag any more: the pre-check reads it like any file (a sync root registered
        // through the classic API is not listed anywhere, so only the Kit's gate refuses it, at move time).
        cloud.AssertNoFetch("LeanRules.Preview on the placeholders");
        Assert.AreEqual(0L, cloud.Status("a.bin").OnDisk, "Preview fetched a.bin");
    }

    // docs/spec/02 Windows note (b): "none of them a link (junction, symbolic link or mount point; the item itself
    // neither)": a junction the user made inside its own top folder is refused, as is anything through it; the plain
    // folder next to it is admitted.
    [TestMethod]
    public void JunctionInsideRootBIsNotAdmittedButThePlainFolderNextToItIs()
    {
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        var real = top.Sub("real");
        Directory.CreateDirectory(real);
        File.WriteAllBytes(top.Sub("real", "x.bin"), new byte[4096]);
        Native.CreateJunction(top.Sub("jump"), real);

        Assert.IsTrue(LeanRules.Preview(real, context), "the plain folder (control)");
        Assert.IsTrue(LeanRules.Preview(top.Sub("real", "x.bin"), context), "a file in the plain folder (control)");
        Assert.IsFalse(LeanRules.Preview(top.Sub("jump"), context), "the item itself is a junction");
        Assert.IsFalse(LeanRules.Preview(top.Sub("jump", "x.bin"), context), "a junction in the chain down to the item");
        Assert.IsNotNull(DisplayOnly.HintFor(top.Sub("jump"), context), "a refused item says why");
    }

    // docs/spec/02 "Comparison is done on canonical paths": a \\?\ spelling resolves ".." like the plain one, so a path
    // that climbs out of the profile into Windows is refused, and one into the user's own top folder is admitted.
    [TestMethod]
    public void ExtendedSpellingResolvesDotDotLikeThePlainOne()
    {
        var context = PathContext.Current();
        var escaped = @"\\?\" + Path.Combine(context.ProfileFolder, "Documents", "a", "..", "..", "..", "..", "Windows", "f");
        Assert.IsFalse(LeanRules.Preview(escaped, context), escaped + " resolves to C:\\Windows\\f");

        using var top = new OwnTopFolder(context);
        var x = top.Sub("sub", "x");
        Directory.CreateDirectory(top.Sub("sub"));
        File.WriteAllBytes(x, new byte[16]);
        Assert.IsTrue(LeanRules.Preview(x, context), "plain spelling");
        Assert.IsTrue(LeanRules.Preview(@"\\?\" + x, context), "\\\\?\\ spelling of the same item");
        Assert.AreEqual(DisplayOnly.HintFor(x, context), DisplayOnly.HintFor(@"\\?\" + x, context));
    }

    // docs/spec/02 canonical paths: a name ending in a dot or a space is another item to NTFS than its trimmed spelling;
    // the pre-check fails closed on such a spelling (given as text; nothing is created).
    [TestMethod]
    public void NameEndingInADotOrASpaceIsNotAdmitted()
    {
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        Directory.CreateDirectory(top.Sub("sub"));
        File.WriteAllBytes(top.Sub("sub", "x"), new byte[16]);
        Assert.IsTrue(LeanRules.Preview(top.Sub("sub", "x"), context), "the plain name (control)");
        Assert.IsFalse(LeanRules.Preview(top.Sub("sub", "x."), context), "trailing dot");
        Assert.IsFalse(LeanRules.Preview(top.Sub("sub", "x "), context), "trailing space");
        Assert.IsFalse(LeanRules.Preview(top.Sub("sub.", "x"), context), "trailing dot on a folder name");
        Assert.IsFalse(LeanRules.Preview(@"\\?\" + top.Sub("sub", "x."), context), "trailing dot behind \\\\?\\");
    }

    // docs/spec/02 canonical paths: UNC and device spellings are no usable path for the pre-check: not admitted.
    [TestMethod]
    public void UncAndDeviceSpellingsAreNotAdmitted()
    {
        var context = PathContext.Current();
        using var top = new OwnTopFolder(context);
        Directory.CreateDirectory(top.Sub("sub"));
        File.WriteAllBytes(top.Sub("sub", "x"), new byte[16]);
        var drive = context.ProfileVolumeRoot.TrimEnd('\\');
        var relative = top.Sub("sub", "x")[(drive.Length + 1)..];
        string[] spellings =
        [
            @"\\?\UNC\localhost\" + drive[0] + "$\\" + relative,
            @"\\localhost\" + drive[0] + "$\\" + relative,
            @"\\?\UNC\localhost\ambynet\x",
            @"\\.\" + top.Sub("sub", "x"),
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" + relative,
            @"\\?\GLOBALROOT\Device\HarddiskVolume1\" + relative,
        ];
        foreach (var spelling in spellings)
        {
            Assert.IsFalse(LeanRules.Preview(spelling, context), spelling);
        }
    }

    /// <summary>The OneDrive registration for this user in the machine hive: the root path, read as an oracle
    /// independent of the code under test (key "&lt;Provider&gt;!&lt;SID&gt;!…", value named by the SID).</summary>
    private static (string Provider, string Root)? RegisteredSyncRoot(string provider)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var manager = Registry.LocalMachine.OpenSubKey(SyncRootManagerKey);
        if (manager is null)
        {
            return null;
        }

        foreach (var name in manager.GetSubKeyNames())
        {
            var separator = name.IndexOf('!', StringComparison.Ordinal);
            var keyProvider = separator > 0 ? name[..separator] : name;
            if (!string.Equals(keyProvider, provider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var userRoots = manager.OpenSubKey(name + @"\UserSyncRoots");
            if (userRoots?.GetValue(sid) is string root && root.Length > 0)
            {
                return (keyProvider, root.TrimEnd('\\'));
            }
        }

        return null;
    }

    // docs/spec/01 Windows notes on hints ("Synced by <provider>") and the fix "sync roots from the machine hive": a path
    // inside the root registered for this user's SID under HKLM ...\SyncRootManager gets the provider from the key name.
    [TestMethod]
    public void FileInsideTheRegisteredSyncRootGetsTheProviderHint()
    {
        var registered = RegisteredSyncRoot("OneDrive");
        if (registered is null)
        {
            Assert.Inconclusive("no OneDrive registration for this user's SID under HKLM\\" + SyncRootManagerKey);
        }

        var (provider, root) = registered.Value;
        var context = PathContext.Current();
        Assert.IsTrue(context.SyncRoots.Any(r => string.Equals(r.Path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) && r.ProviderName == provider),
            $"the context does not list {root} as {provider}: [{string.Join("; ", context.SyncRoots.Select(r => r.Path + " (" + r.ProviderName + ")"))}]");
        // Judged by path: nothing is created inside the real, syncing root.
        var inside = Path.Combine(root, "LeanRulesTests", "hint.txt");
        Assert.AreEqual(Text.HintSynced(provider), DisplayOnly.HintFor(inside, context));
    }

    // The fix itself, isolated from the other sources (the %OneDrive% variables and the profile's OneDrive folder, which
    // name the same root on this machine): with those variables cleared in this process and a profile folder that holds
    // no sync folder, the reader still finds the registered root, from the machine hive, by this user's SID.
    [TestMethod]
    public void RegisteredSyncRootIsReadFromTheMachineHiveBySid()
    {
        var registered = RegisteredSyncRoot("OneDrive");
        if (registered is null)
        {
            Assert.Inconclusive("no OneDrive registration for this user's SID under HKLM\\" + SyncRootManagerKey);
        }

        var (provider, root) = registered.Value;
        string[] variables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
        var saved = variables.ToDictionary(v => v, v => Environment.GetEnvironmentVariable(v), StringComparer.Ordinal);
        using var emptyProfile = new TempFolder("rules-empty-profile");
        try
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }

            IReadOnlyList<SyncRoot> roots = SyncRootReader.Read(emptyProfile.Path);
            Assert.IsTrue(roots.Any(r => string.Equals(r.Path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) && r.ProviderName == provider),
                $"the registered root {root} ({provider}) is missing: [{string.Join("; ", roots.Select(r => r.Path + " (" + r.ProviderName + ")"))}]");
        }
        finally
        {
            foreach (var (variable, value) in saved)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }
    }

    // docs/spec/01 Windows notes on hints: a business OneDrive folder "%USERPROFILE%\OneDrive - <Tenant>" is a sync root
    // (made empty here, removed after; nothing syncs an empty folder that no client knows).
    [TestMethod]
    public void BusinessOneDriveFolderInTheProfileIsASyncRoot()
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
            Assert.IsTrue(context.SyncRoots.Any(r => string.Equals(r.Path.TrimEnd('\\'), business, StringComparison.OrdinalIgnoreCase) && r.ProviderName == "OneDrive"),
                $"{business} is not a sync root: [{string.Join("; ", context.SyncRoots.Select(r => r.Path + " (" + r.ProviderName + ")"))}]");
            Assert.AreEqual(Text.HintSynced("OneDrive"), DisplayOnly.HintFor(Path.Combine(business, "Documents", "plan.docx"), context));
        }
        finally
        {
            TryDelete(business);
        }
    }
}
