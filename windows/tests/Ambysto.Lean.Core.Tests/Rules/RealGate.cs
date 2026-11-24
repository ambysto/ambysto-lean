using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary>Lean's rules used through the real Kit gate, for this user (plan Design "Rules"; the Kit's
/// <c>docs/spec/safety.md</c>, Windows order). The gate's step 1 refuses outside an interactive session, so every test that
/// uses it is in the <c>Console</c> category; each check here fails with that reason named, never passes on it. Every
/// request's stamp is read with <see cref="FileFacts.Read"/> before the gate is built, so a refusal is the gate's answer
/// on an unchanged item, never "Changed since the scan".</summary>
internal static class RealGate
{
    private static readonly Lazy<SafetyContext> SharedContext = new(() => SafetyContext.ForCurrentUser([JournalFolder]), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Lean's journal folder (docs/spec/02 Windows note on never-touch; plan "Journal"), which the app passes to the Kit.</summary>
    public static string JournalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space");

    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The literal <c>%LOCALAPPDATA%\Temp</c> (intent decision 7 (a): "by these literal paths, whatever %TEMP% says").</summary>
    public static string LocalTemp => Path.Combine(Local, "Temp");

    public static string CrashDumps => Path.Combine(Local, "CrashDumps");

    public static string LocalPrograms => Path.Combine(Local, "Programs");

    /// <summary>The user's own <c>Start Menu\Programs</c>.</summary>
    public static string StartMenuPrograms => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    /// <summary>The user's own <c>Start Menu\Programs\Startup</c>.</summary>
    public static string Startup => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    /// <summary>The root of the profile's volume (C:\ on a standard install).</summary>
    public static string VolumeRoot => Path.GetPathRoot(Profile)!;

    public static SafetyContext Context => SharedContext.Value;

    /// <summary>The empty item set (no module-bound items).</summary>
    public static IReadOnlySet<string> NoItems { get; } = new HashSet<string>();

    /// <summary>The gate for a move from <paramref name="module"/>, as the app builds it: the Kit's context for this user with
    /// Lean's journal folder, and <see cref="LeanRules.ForDelete"/>.</summary>
    public static SafetyGate ForDelete(LeanModule module, IReadOnlySet<string> moduleItems) => new(Context, LeanRules.ForDelete(module, moduleItems));

    /// <summary>The gate for a put-back destination, with <see cref="LeanRules.ForRestore"/>.</summary>
    public static SafetyGate ForRestore() => new(Context, LeanRules.ForRestore());

    /// <summary>The facts of an existing item, read as the scanner records them (fails the test's setup if unreadable).</summary>
    public static FileFactsResult Facts(string path)
    {
        var facts = FileFacts.Read(path);
        Assert.IsTrue(facts.Succeeded, $"setup: FileFacts.Read({path}) failed: {facts.FailureReason} {facts.Detail}");
        return facts;
    }

    /// <summary>A request for <paramref name="path"/> with its stamp read now.</summary>
    public static GateRequest Request(string path) => new(path, Facts(path).Stamp!.Value);

    /// <summary>A request spelled <paramref name="spelling"/> for the item at <paramref name="path"/> (same stamp).</summary>
    public static GateRequest Request(string spelling, string path) => new(spelling, Facts(path).Stamp!.Value);

    /// <summary>The canonical path the Kit reads for an existing item (the form the matcher's items are compared in).</summary>
    public static string Canonical(string path) => Facts(path).CanonicalPath!;

    /// <summary>The item set of a module: the canonical paths of <paramref name="paths"/>.</summary>
    public static IReadOnlySet<string> Items(params string[] paths) => paths.Select(Canonical).ToHashSet();

    public static void Approved(GateResult result, string what)
    {
        NotTheEnvironment(result, what);
        Assert.IsTrue(result.IsApproved, $"{what}: refused with {result.Reason}: {result.Message} [{result.Detail}]");
    }

    /// <summary>Refused by Lean's own rule (the gate's last step): <see cref="RefusalReason.NotAllowed"/> with
    /// <see cref="Text.HintOutside"/> as the message (stub contract of <see cref="LeanRules.ForDelete"/>).</summary>
    public static void RefusedByTheRule(GateResult result, string what)
    {
        NotTheEnvironment(result, what);
        Assert.IsFalse(result.IsApproved, what + ": approved");
        Assert.AreEqual(RefusalReason.NotAllowed, result.Reason, $"{what}: refused with {result.Reason} ({result.Message} [{result.Detail}]), not by Lean's rule");
        Assert.AreEqual(Text.HintOutside, result.Message, what + ": the rule's message");
    }

    /// <summary>Refused, by any step of the gate (an earlier step may refuse before Lean's rule runs), but not because the
    /// stamp was wrong or the session was not interactive.</summary>
    public static void Refused(GateResult result, string what)
    {
        NotTheEnvironment(result, what);
        Assert.IsFalse(result.IsApproved, what + ": approved");
        Assert.AreNotEqual(RefusalReason.ChangedSinceScan, result.Reason, $"{what}: the test's stamp was stale ({result.Message})");
    }

    private static void NotTheEnvironment(GateResult result, string what)
    {
        if (result.Reason is RefusalReason.NotInteractive or RefusalReason.Elevated)
        {
            Assert.Fail($"{what}: the gate refused the environment ({result.Message}); run this test in the console session as the standard user (Console category)");
        }
    }
}

/// <summary>Uniquely named folders and files this test makes in the real profile and on the real volumes, removed on
/// dispose; a folder it had to create above them (for example <c>%LOCALAPPDATA%\Programs</c> or <c>CrashDumps</c> when
/// missing) is removed too when it is empty again. Never touches anything it did not make.</summary>
internal sealed class RealScratch : IDisposable
{
    public const string Prefix = "LeanGateTests-";

    private readonly List<string> made = [];
    private readonly List<string> ensured = [];

    /// <summary>The parents tests make their folders in; swept of stale <see cref="Prefix"/> folders by
    /// <see cref="SweepStale"/>.</summary>
    public static IEnumerable<string> Parents()
    {
        yield return RealGate.Profile;
        yield return RealGate.Roaming;
        yield return RealGate.Local;
        yield return RealGate.LocalTemp;
        yield return RealGate.CrashDumps;
        yield return RealGate.LocalPrograms;
        yield return RealGate.StartMenuPrograms;
        yield return RealGate.Startup;
        yield return Path.Combine(RealGate.Profile, "AppData", "LocalLow");
        yield return Path.Combine(RealGate.Profile, "AppData");
        yield return RealGate.VolumeRoot;
        foreach (var top in Directory.EnumerateDirectories(RealGate.VolumeRoot))
        {
            yield return top;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>Removes <see cref="Prefix"/> entries a crashed run left behind.</summary>
    public static void SweepStale()
    {
        foreach (var parent in Parents().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> stale;
            try
            {
                stale = Directory.EnumerateFileSystemEntries(parent, Prefix + "*").ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in stale)
            {
                TryDelete(entry);
            }
        }
    }

    /// <summary>A unique name in <paramref name="parent"/>, not created (a put-back destination).</summary>
    public static string NewName(string parent, string extension = "") => Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N")[..8] + extension);

    /// <summary>Creates a unique folder in <paramref name="parent"/> (made first if missing) and returns it.</summary>
    public string NewFolder(string parent)
    {
        EnsureFolder(parent);
        var path = NewName(parent);
        Directory.CreateDirectory(path);
        made.Add(path);
        return path;
    }

    /// <summary>Creates a unique file of 4 KiB in <paramref name="parent"/> (made first if missing) and returns it.</summary>
    public string NewFile(string parent, string extension = ".bin")
    {
        EnsureFolder(parent);
        var path = NewName(parent, extension);
        TempFolder.WriteBytes(path, 4096, 1);
        made.Add(path);
        return path;
    }

    /// <summary>Writes a 4 KiB file at <paramref name="path"/> inside a folder this scratch made, and returns it.</summary>
    public static string File(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TempFolder.WriteBytes(path, 4096, 1);
        return path;
    }

    /// <summary>Creates <paramref name="path"/> (inside a folder this scratch made) and returns it.</summary>
    public static string Folder(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Makes <paramref name="path"/> and any missing folder above it; each one made is removed on dispose when empty.</summary>
    public void EnsureFolder(string path)
    {
        var missing = new List<string>();
        for (var folder = path; !string.IsNullOrEmpty(folder) && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            missing.Add(folder);
        }

        Directory.CreateDirectory(path);
        missing.Reverse();
        ensured.AddRange(missing);
    }

    /// <summary>Records an entry made by other means (a junction) for removal.</summary>
    public void Track(string path) => made.Add(path);

    public void Dispose()
    {
        for (var i = made.Count - 1; i >= 0; i--)
        {
            TryDelete(made[i]);
        }

        for (var i = ensured.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(ensured[i], recursive: false);
            }
            catch (IOException)
            {
                // Not empty any more (something else uses it now): left alone.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Directory.Delete(path, recursive: false); // a junction: the link only, never its target
                    }
                    else
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                else if (info.Exists)
                {
                    System.IO.File.Delete(path);
                }

                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
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
