using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Intent criterion 1, the Recycle Bin rows a standard user can build (plan group 9: "every probe row of intent
/// criterion 1 refused before any move by the real gate"): the bin turned off for the volume (<c>NukeOnDelete</c>, File
/// Explorer's "Don't move files to the Recycle Bin"), an item larger than the bin's size (<c>MaxCapacity</c>, in MiB), and
/// an item that fits the size but not the room left, so the shell would purge older bin items. For each, Windows'
/// <c>IFileOperation</c> deletes for good while reporting success (the intent's facts; <c>the test-machine notes</c>), so only
/// the Kit's precondition, reached through Lean's real <see cref="KitItemGate"/> and the real <see cref="Trasher"/>,
/// stands in the way. The shell move is a <see cref="FailingRecycler"/>: reaching it fails the test. The
/// <c>NoRecycleFiles</c> policy is a policy key this user cannot write (probed 2026-10-18: ambytest has ReadKey on
/// <c>HKCU\...\Policies</c>), so that row is left to an admin-context setup, as the Kit's <c>windows/tests/admin</c> does.
/// Settings are this user's own for the profile's volume and are restored, and the restore checked, by each test. Console
/// session (the real gate refuses everything in session 0); one at a time, and SharedState: the one test user's HKCU and
/// Recycle Bin are shared with every other job on the machine.</summary>
[TestClass]
[DoNotParallelize]
[TestCategory("SharedState")]
public sealed class RecycleBinSettingsRefusalTests
{
    private const long MiB = 1024 * 1024;

    private static string VolumeRoot => RealGate.VolumeRoot;

    private static KitItemGate RealLeanGate() => new(LeanModule.Map, RealGate.NoItems);

    /// <summary>With the settings back as they were, Lean's real gate approves the fixture: the refusal under test came
    /// from the setting alone, not from anything else about the item.</summary>
    private static void AssertApprovedOnceRestored(string path)
    {
        var result = RealLeanGate().Check(RealGate.Request(path));
        Assert.IsNotNull(result.Approved, $"with the settings restored, the real gate still refuses {path}: {result.Message}");
    }

    // Bin off: NukeOnDelete = 1 for the volume (the Explorer setting "Don't move files to the Recycle Bin. Remove files
    // immediately when deleted"): refused as RecycleBinOff before any move.
    [TestMethod]
    [TestCategory("Console")]
    public async Task TheRecycleBinTurnedOffForTheVolumeIsRefused()
    {
        RealGateRefusalTests.RequireConsoleSession();
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(folder, "old.iso");
        TempFolder.WriteBytes(file, 64 * 1024, 1);

        var settings = new RecycleBinVolumeSettings(VolumeRoot);
        try
        {
            settings.SetNukeOnDelete(true);
            await RealGateRefusalTests.AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, RefusalReason.RecycleBinOff);
        }
        finally
        {
            settings.Restore();
        }

        settings.AssertRestored();
        AssertApprovedOnceRestored(file);
    }

    // Over quota: MaxCapacity (MiB) smaller than the item: refused as TooLargeForRecycleBin before any move.
    [TestMethod]
    [TestCategory("Console")]
    public async Task AnItemLargerThanTheRecycleBinsSizeIsRefused()
    {
        RealGateRefusalTests.RequireConsoleSession();
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(folder, "old.iso");
        TempFolder.WriteBytes(file, 2 * MiB, 2);

        var settings = new RecycleBinVolumeSettings(VolumeRoot);
        try
        {
            settings.SetMaxCapacityMegabytes(1);
            await RealGateRefusalTests.AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, RefusalReason.TooLargeForRecycleBin);
        }
        finally
        {
            settings.Restore();
        }

        settings.AssertRestored();
        AssertApprovedOnceRestored(file);
    }

    // Purge needed: the item fits the bin's size but not the room left (the shell would silently purge older items to
    // make room): refused as RecycleBinFull before any move. The room is made real by recycling one of the test's own
    // files first (so the bin holds something whatever its state), put back afterwards.
    [TestMethod]
    [TestCategory("Console")]
    public async Task AnItemThatWouldNeedOlderBinItemsPurgedIsRefused()
    {
        RealGateRefusalTests.RequireConsoleSession();
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(folder, "old.iso");
        TempFolder.WriteBytes(file, 4 * MiB, 3);
        var helper = Path.Combine(folder, "already-in-the-bin.bin");
        TempFolder.WriteBytes(helper, MiB, 4);

        var settings = new RecycleBinVolumeSettings(VolumeRoot);
        string? helperInBin = null;
        try
        {
            var helperReport = new ShellRecycler().Recycle(helper);
            helperInBin = helperReport.ReportedNewItemPath;
            Check.True(helperReport.Performed && helperInBin is not null && !File.Exists(helper), "the helper file did not reach the Recycle Bin: " + helperReport);
            var (hr, used, _) = Native.QueryRecycleBin(VolumeRoot);
            Assert.AreEqual(0, hr, "SHQueryRecycleBin(" + VolumeRoot + ")");
            Check.AtLeast(MiB, used, "the bin's use after recycling the 1 MiB helper");

            // Fits the whole size (capacity >= 4 MiB), not the room left (capacity - used < 4 MiB).
            var capacity = (int)Math.Max(4, (used + MiB - 1) / MiB + 2);
            settings.SetMaxCapacityMegabytes(capacity);
            var room = capacity * MiB - used;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bin on {VolumeRoot}: used {used} bytes, MaxCapacity set to {capacity} MiB, room {room} bytes, item {4 * MiB} bytes"));
            Check.True(4 * MiB <= capacity * MiB, "precondition: the item fits the bin's whole size");
            Check.True(4 * MiB > room, "precondition: the item does not fit the room left");

            await RealGateRefusalTests.AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, RefusalReason.RecycleBinFull);

            var (_, usedAfter, _) = Native.QueryRecycleBin(VolumeRoot);
            Assert.AreEqual(used, usedAfter, "the bin's use changed during the test (another job on the machine?): the row's arithmetic no longer holds");
        }
        finally
        {
            settings.Restore();
            ShellRecyclerConsoleTests.PutBackOrLeave(helperInBin, helper);
        }

        settings.AssertRestored();
        AssertApprovedOnceRestored(file);
    }

    // Policy: NoRecycleFiles (HKLM or HKCU Policies\Explorer) refuses as RecycleBinOff. A standard user cannot write
    // either hive (ambytest: ReadKey on HKCU\...\Policies), so this row stays Inconclusive here and belongs to an
    // admin-context setup; the attempt is kept so a machine where the user may write it covers the row.
    [TestMethod]
    [TestCategory("Console")]
    public async Task TheNoRecycleFilesPolicyIsRefusedWhereThisUserMaySetIt()
    {
        RealGateRefusalTests.RequireConsoleSession();
        using var scratch = new RealScratch();
        var folder = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(folder, "old.iso");
        TempFolder.WriteBytes(file, 64 * 1024, 5);

        UserPolicy policy;
        try
        {
            policy = UserPolicy.Open();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Assert.Inconclusive("NoRecycleFiles is a policy value this user cannot write (" + e.GetType().Name + "): the row needs an admin-context setup (the Kit's windows/tests/admin), not this user");
            return;
        }

        try
        {
            UserPolicy.SetNoRecycleFiles(true);
            await RealGateRefusalTests.AssertRefusedBeforeAnyMoveAsync(file, LeanModule.Map, RefusalReason.RecycleBinOff);
        }
        finally
        {
            policy.Restore();
        }

        policy.AssertRestored();
        AssertApprovedOnceRestored(file);
    }
}

/// <summary>This user's Recycle Bin settings for one volume, the Kit's test support's way (<c>RecycleBinVolumeSettings</c>
/// in Ambysto.Kit.Safety.Tests): the standard user may set its own <c>HKCU\...\Explorer\BitBucket\Volume\{GUID}</c>
/// values. <see cref="Restore"/> puts every value back, removes any value added, and removes a key that did not exist;
/// <see cref="AssertRestored"/> re-reads the key and fails the test on any difference. The before and after values are
/// written to the test output.</summary>
internal sealed class RecycleBinVolumeSettings
{
    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";
    public const string MaxCapacityName = "MaxCapacity";
    public const string NukeOnDeleteName = "NukeOnDelete";

    private readonly string keyPath;
    private readonly bool existed;
    private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> original = new(StringComparer.OrdinalIgnoreCase);

    public RecycleBinVolumeSettings(string driveRoot)
    {
        var guidPath = Native.VolumeGuidPathOf(driveRoot);
        Assert.IsNotNull(guidPath, "GetVolumeNameForVolumeMountPoint(" + driveRoot + ") failed");
        var open = guidPath!.IndexOf('{', StringComparison.Ordinal);
        var close = guidPath.IndexOf('}', StringComparison.Ordinal);
        Check.True(open >= 0 && close > open, "unexpected volume name " + guidPath);
        keyPath = BitBucket + "\\" + guidPath[open..(close + 1)];
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        existed = key is not null;
        if (key is not null)
        {
            foreach (var name in key.GetValueNames())
            {
                original[name] = (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name));
            }
        }

        Console.WriteLine("before: " + Describe());
    }

    public string KeyPath => @"HKCU\" + keyPath;

    public void SetMaxCapacityMegabytes(int megabytes) => Set(MaxCapacityName, megabytes);

    public void SetNukeOnDelete(bool on) => Set(NukeOnDeleteName, on ? 1 : 0);

    private void Set(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
        Console.WriteLine("set: " + Describe());
    }

    /// <summary>Every value back as read at construction; a key that did not exist is removed.</summary>
    public void Restore()
    {
        if (!existed)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Console.WriteLine("after: " + Describe());
            return;
        }

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
        {
            foreach (var name in key.GetValueNames().Where(n => !original.ContainsKey(n)).ToList())
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            foreach (var (name, (value, kind)) in original)
            {
                key.SetValue(name, value, kind);
            }
        }

        Console.WriteLine("after: " + Describe());
    }

    /// <summary>Fails unless the key now holds exactly the values read at construction (or is absent, when it was).</summary>
    public void AssertRestored()
    {
        var now = Current();
        if (!existed)
        {
            Assert.IsNull(now, KeyPath + " exists after the test; it did not before");
            return;
        }

        Assert.IsNotNull(now, KeyPath + " is gone after the test");
        Check.Set(now!.Keys, original.Keys.ToArray());
        foreach (var (name, (value, kind)) in original)
        {
            Assert.AreEqual(kind, now[name].Kind, KeyPath + "\\" + name + " kind");
            Assert.AreEqual(Convert.ToString(value, CultureInfo.InvariantCulture), Convert.ToString(now[name].Value, CultureInfo.InvariantCulture), KeyPath + "\\" + name + " value");
        }
    }

    private Dictionary<string, (object Value, RegistryValueKind Kind)>? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var values = new Dictionary<string, (object Value, RegistryValueKind Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
        {
            values[name] = (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name));
        }

        return values;
    }

    private string Describe()
    {
        var now = Current();
        if (now is null)
        {
            return KeyPath + " (absent)";
        }

        return KeyPath + " " + string.Join(", ", now.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + Convert.ToString(p.Value.Value, CultureInfo.InvariantCulture)));
    }
}

/// <summary>The user hive's Explorer policy key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer</c>),
/// opened for writing at <see cref="Open"/> (which throws when this user may not: the normal case for a standard user,
/// whose Policies keys are read-only to it). <see cref="Restore"/> puts <c>NoRecycleFiles</c> back as it was and removes
/// a key that did not exist.</summary>
internal sealed class UserPolicy
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string NoRecycleFilesName = "NoRecycleFiles";

    private readonly bool existed;
    private readonly object? originalValue;
    private readonly RegistryValueKind? originalKind;

    private UserPolicy(bool existed, object? originalValue, RegistryValueKind? originalKind)
    {
        this.existed = existed;
        this.originalValue = originalValue;
        this.originalKind = originalKind;
    }

    public static UserPolicy Open()
    {
        bool existed;
        object? value = null;
        RegistryValueKind? kind = null;
        using (var read = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: false))
        {
            existed = read is not null;
            if (read is not null && read.GetValue(NoRecycleFilesName) is { } v)
            {
                value = v;
                kind = read.GetValueKind(NoRecycleFilesName);
            }
        }

        // Proves the write right now, before anything is changed; throws for a user that may not write here.
        Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true).Dispose();
        Console.WriteLine("before: " + Describe());
        return new UserPolicy(existed, value, kind);
    }

    public static void SetNoRecycleFiles(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
        key.SetValue(NoRecycleFilesName, on ? 1 : 0, RegistryValueKind.DWord);
        Console.WriteLine("set: " + Describe());
    }

    public void Restore()
    {
        if (!existed)
        {
            Registry.CurrentUser.DeleteSubKeyTree(PolicyKey, throwOnMissingSubKey: false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
            if (originalValue is null)
            {
                key.DeleteValue(NoRecycleFilesName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(NoRecycleFilesName, originalValue, originalKind!.Value);
            }
        }

        Console.WriteLine("after: " + Describe());
    }

    public void AssertRestored()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: false);
        if (!existed)
        {
            Assert.IsNull(key, @"HKCU\" + PolicyKey + " exists after the test; it did not before");
            return;
        }

        Assert.IsNotNull(key, @"HKCU\" + PolicyKey + " is gone after the test");
        Assert.AreEqual(Convert.ToString(originalValue, CultureInfo.InvariantCulture), Convert.ToString(key!.GetValue(NoRecycleFilesName), CultureInfo.InvariantCulture), NoRecycleFilesName);
    }

    private static string Describe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: false);
        return key is null ? @"HKCU\" + PolicyKey + " (absent)" : @"HKCU\" + PolicyKey + " " + NoRecycleFilesName + "=" + Convert.ToString(key.GetValue(NoRecycleFilesName), CultureInfo.InvariantCulture);
    }
}
