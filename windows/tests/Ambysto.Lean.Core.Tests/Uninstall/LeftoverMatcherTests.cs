using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary><see cref="LeftoverMatcher.Match"/>: docs/spec/04 (Windows notes on leftover locations and match rules),
/// intent decision 10, Outcome 4 and criterion 6. Exact, never substring: in <c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c> and
/// <c>%LOCALAPPDATA%\Programs</c> only, the gone app's install location when it is at least one component inside one of
/// them, <c>&lt;Publisher&gt;\&lt;DisplayName&gt;</c> and <c>&lt;DisplayName&gt;</c>. Never one of those folders itself,
/// never <c>%USERPROFILE%</c>, never a generic display name, never a vendor folder another installed app may use; a
/// candidate two installed or just-removed apps could own belongs to nobody (rule 4). "Offered" here means listed and
/// selectable; a folder that must not be offered may be absent or listed as not selectable. Each test builds a pretend
/// profile in a temp folder (<see cref="ProfileFixture"/>) with the folders it names.</summary>
[TestClass]
public sealed class LeftoverMatcherTests
{
    private static readonly IReadOnlyList<InstalledApp> None = [];

    internal static InstalledApp App(string name, string? publisher, string? location, AppClass appClass = AppClass.PerUser) =>
        new(name, publisher, "1.0", location, appClass,
            new UninstallEntry("{" + name + "|" + publisher + "|" + location + "}", appClass == AppClass.NeedsAdmin ? UninstallScope.PerMachine : UninstallScope.PerUser,
                RegistryViewKind.Bits64, name, publisher, "1.0", location, false),
            null);

    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(path);

    internal static IReadOnlyList<LeftoverCandidate> Match(InstalledApp gone, IReadOnlyList<InstalledApp> inventory, IReadOnlyList<InstalledApp> justRemoved, ProfileFixture fx)
    {
        var result = LeftoverMatcher.Match(gone, inventory, justRemoved, fx.Context);
        Invariants(result, fx);
        return result;
    }

    /// <summary>What holds for every answer: each candidate is a folder at least one component inside one of the three
    /// leftover folders (never one of them, never the profile, never elsewhere), listed once, with the rule that matched it.</summary>
    internal static void Invariants(IReadOnlyList<LeftoverCandidate> result, ProfileFixture fx)
    {
        Assert.IsNotNull(result);
        var programs = Path.Combine(fx.LocalAppData, "Programs");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in result)
        {
            Assert.IsNotNull(candidate.Path);
            var full = Norm(Path.GetFullPath(candidate.Path));
            Assert.IsTrue(paths.Add(full), "listed twice: " + candidate.Path);
            Assert.IsFalse(string.IsNullOrWhiteSpace(candidate.Rule), "no rule named for " + candidate.Path);
            var inside = new[] { fx.RoamingAppData, fx.LocalAppData, programs }
                .Any(folder => full.StartsWith(Norm(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(inside, "outside the leftover folders: " + candidate.Path);
            Assert.IsFalse(string.Equals(full, Norm(programs), StringComparison.OrdinalIgnoreCase), "Programs itself: " + candidate.Path);
            Assert.IsTrue(Directory.Exists(full), "not an existing folder: " + candidate.Path);
            if (!candidate.Selectable)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(candidate.Note), "shown but not selectable without a note: " + candidate.Path);
            }
        }
    }

    internal static string[] Offered(IReadOnlyList<LeftoverCandidate> result) =>
        result.Where(c => c.Selectable).Select(c => Norm(Path.GetFullPath(c.Path))).ToArray();

    internal static void AssertOffered(IReadOnlyList<LeftoverCandidate> result, string what, params string[] expected)
    {
        var got = Offered(result).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var want = expected.Select(Norm).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(want, got, StringComparer.OrdinalIgnoreCase,
            $"{what}: expected offered {{{string.Join(", ", want)}}}, got {{{string.Join(", ", got)}}}; all listed: {Describe(result)}");
    }

    internal static void AssertNotOffered(IReadOnlyList<LeftoverCandidate> result, string path, string why) =>
        Assert.IsFalse(Offered(result).Contains(Norm(path), StringComparer.OrdinalIgnoreCase), $"{why}: {path} offered; all listed: {Describe(result)}");

    internal static void AssertNotListed(IReadOnlyList<LeftoverCandidate> result, string path, string why) =>
        Assert.IsFalse(result.Any(c => string.Equals(Norm(Path.GetFullPath(c.Path)), Norm(path), StringComparison.OrdinalIgnoreCase)), $"{why}: {path} listed; all listed: {Describe(result)}");

    private static string Describe(IReadOnlyList<LeftoverCandidate> result) =>
        result.Count == 0 ? "(none)" : string.Join("; ", result.Select(c => $"{c.Path} [{c.Rule}, selectable {c.Selectable}, {c.Note}]"));

    private static string Programs(ProfileFixture fx, params string[] parts) => Path.Combine([fx.LocalAppData, "Programs", .. parts]);

    private static string Roaming(ProfileFixture fx, params string[] parts) => Path.Combine([fx.RoamingAppData, .. parts]);

    private static string Local(ProfileFixture fx, params string[] parts) => Path.Combine([fx.LocalAppData, .. parts]);

    private static string Folder(string path) => ProfileFixture.MakeFolder(path);

    // docs/spec/04 Windows note: the install location when it is at least one component inside a leftover folder, in each
    // of the three, at depth one or deeper, written with a trailing separator or another letter case.
    [TestMethod]
    public void InstallLocationInsideALeftoverFolderIsOffered()
    {
        using var fx = new ProfileFixture("leftover-location");
        var cases = new (string Location, string Written)[]
        {
            (Folder(Programs(fx, "Contoso Notes")), Programs(fx, "Contoso Notes") + @"\"),
            (Folder(Local(fx, "contoso-notes")), Local(fx, "contoso-notes")),
            (Folder(Roaming(fx, "Contoso", "Notes", "bin")), Roaming(fx, "Contoso", "Notes", "bin")),
            (Folder(Programs(fx, "Vendor", "App")), Programs(fx, "Vendor", "App").ToUpperInvariant()),
        };
        foreach (var (location, written) in cases)
        {
            var gone = App("Unrelated Name " + Path.GetFileName(location), null, written);
            AssertOffered(Match(gone, None, None, fx), written, location);
        }
    }

    // docs/spec/04 Windows note: "never one of these folders itself, and never %USERPROFILE% itself; an install location
    // anywhere else, such as Program Files, is never a leftover"; an app installed directly at %USERPROFILE%\<App> is
    // never moved. A ".." in the registry value is judged where it lands.
    [TestMethod]
    public void InstallLocationThatIsALeftoverFolderTheProfileOrElsewhereIsNeverMatched()
    {
        using var fx = new ProfileFixture("leftover-location-never");
        Folder(Programs(fx));
        var direct = Folder(fx.InProfile("Contoso"));
        var programFiles = Folder(Path.Combine(fx.ProgramFiles, "Contoso"));
        var documents = Folder(Path.Combine(fx.Documents, "Contoso"));
        var otherVolume = Folder(Path.Combine(fx.OtherVolume, "Contoso"));
        string[] locations =
        [
            fx.RoamingAppData, fx.LocalAppData, Programs(fx), Programs(fx) + @"\", fx.Profile, direct, programFiles, documents, otherVolume,
            Path.Combine(Programs(fx), "Contoso", ".."), Path.Combine(Programs(fx), "..", "..", "Roaming"), Path.Combine(fx.LocalAppData, "..", "..", "Contoso"),
            fx.VolumeRoot, "Contoso", "", "   ",
        ];
        foreach (var location in locations)
        {
            var gone = App("Unrelated " + Guid.NewGuid().ToString("N")[..6], null, location);
            AssertOffered(Match(gone, None, None, fx), "install location " + location);
        }
    }

    // docs/spec/04 Windows note: <Publisher>\<DisplayName> and <DisplayName> exactly, in each of the three folders.
    [TestMethod]
    public void DisplayNameAndPublisherDisplayNameAreOfferedInEachFolder()
    {
        using var fx = new ProfileFixture("leftover-names");
        string[] expected =
        [
            Folder(Roaming(fx, "Contoso Notes")),
            Folder(Local(fx, "Contoso Notes")),
            Folder(Programs(fx, "Contoso Notes")),
            Folder(Roaming(fx, "Contoso", "Contoso Notes")),
            Folder(Local(fx, "Contoso", "Contoso Notes")),
            Folder(Programs(fx, "Contoso", "Contoso Notes")),
        ];
        var gone = App("Contoso Notes", "Contoso", null);
        var result = Match(gone, None, None, fx);
        AssertOffered(result, "Contoso Notes by Contoso", expected);
        AssertNotListed(result, Roaming(fx, "Contoso"), "the publisher's folder itself");
    }

    // docs/spec/04 Windows note: the install location and the display name naming one folder give one candidate.
    [TestMethod]
    public void OneFolderMatchedTwiceIsListedOnce()
    {
        using var fx = new ProfileFixture("leftover-once");
        var folder = Folder(Programs(fx, "Contoso Notes"));
        var result = Match(App("Contoso Notes", "Contoso", folder), None, None, fx);
        AssertOffered(result, "install location = display name folder", folder);
        Assert.AreEqual(1, result.Count, "one candidate");
    }

    // docs/spec/04: only folders that are there are candidates.
    [TestMethod]
    public void FoldersThatDoNotExistAreNotCandidates()
    {
        using var fx = new ProfileFixture("leftover-absent");
        Folder(Programs(fx));
        var result = Match(App("Contoso Notes", "Contoso", Programs(fx, "Contoso Notes")), None, None, fx);
        Assert.AreEqual(0, result.Count, "nothing exists: nothing listed");
    }

    // docs/spec/04 "Match rules (exact, never substring)": "Notes" never takes "Notes Viewer", "Contoso Notes", "MyNotes",
    // "Notes2" or "Notes.old"; it takes "Notes" and "Contoso\Notes" only.
    [TestMethod]
    public void SubstringAndPrefixNamesAreNeverMatched()
    {
        using var fx = new ProfileFixture("leftover-prefix");
        var own = Folder(Roaming(fx, "Notes"));
        var ownByPublisher = Folder(Roaming(fx, "Contoso", "Notes"));
        string[] decoys =
        [
            Folder(Roaming(fx, "Notes Viewer")), Folder(Roaming(fx, "Contoso Notes")), Folder(Roaming(fx, "MyNotes")), Folder(Roaming(fx, "Notes2")),
            Folder(Roaming(fx, "Notes.old")), Folder(Local(fx, "Notes Viewer")), Folder(Programs(fx, "Notes Viewer")), Folder(Roaming(fx, "Contoso", "Notes Viewer")),
            Folder(Roaming(fx, "Contoso Ltd", "Notes")),
        ];
        var gone = App("Notes", "Contoso", null);
        var viewer = App("Notes Viewer", "Contoso", Programs(fx, "Notes Viewer"));
        var result = Match(gone, [viewer], None, fx);
        AssertOffered(result, "Notes", own, ownByPublisher);
        foreach (var decoy in decoys)
        {
            AssertNotListed(result, decoy, "not an exact name");
        }
    }

    // Intent Outcome 4, docs/spec/04: "a DisplayName that is a generic folder name (Temp, Programs, Packages, Microsoft, …)
    // is never matched", directly or under its publisher.
    [TestMethod]
    public void GenericDisplayNamesAreNeverMatched()
    {
        using var fx = new ProfileFixture("leftover-generic");
        Folder(Programs(fx));
        Folder(Local(fx, "Packages"));
        Folder(Roaming(fx, "Microsoft"));
        Folder(Local(fx, "Microsoft"));
        Folder(Roaming(fx, "Temp"));
        Folder(Roaming(fx, "Programs"));
        Folder(Programs(fx, "Temp"));
        foreach (var name in new[] { "Temp", "Programs", "Packages", "Microsoft" })
        {
            Folder(Roaming(fx, "Acme", name));
            Folder(Local(fx, "Acme", name));
            foreach (var publisher in new[] { null, "Acme", "Microsoft Corporation" })
            {
                var result = Match(App(name, publisher, null), None, None, fx);
                AssertOffered(result, $"generic display name {name} (publisher {publisher})");
            }
        }
    }

    // Decision 10, docs/spec/04: "Never a vendor folder another installed app may use": the gone app "Fabrikam" by
    // Fabrikam names %APPDATA%\Fabrikam exactly, but that folder is the publisher folder of the installed "Fabrikam
    // Studio"; its own Fabrikam\Fabrikam is still offered.
    [TestMethod]
    public void VendorFolderAnotherInstalledAppMayUseIsNeverOffered()
    {
        using var fx = new ProfileFixture("leftover-vendor");
        var vendor = Folder(Roaming(fx, "Fabrikam"));
        Folder(Roaming(fx, "Fabrikam", "Fabrikam Studio"));
        var own = Folder(Roaming(fx, "Fabrikam", "Fabrikam"));
        var localVendor = Folder(Local(fx, "Fabrikam"));
        var studio = App("Fabrikam Studio", "Fabrikam", Folder(Programs(fx, "Fabrikam Studio")));
        var result = Match(App("Fabrikam", "Fabrikam", null), [studio], None, fx);
        AssertNotOffered(result, vendor, "the installed Fabrikam Studio's publisher folder");
        AssertNotOffered(result, localVendor, "the installed Fabrikam Studio's publisher folder");
        AssertOffered(result, "Fabrikam", own);
    }

    // Decision 10, rule 4: a candidate that holds, or lies inside, an installed app's install location is that app's too,
    // so it belongs to nobody: the gone suite's folder holds the installed Notes; the gone plug-in lies inside the
    // installed host.
    [TestMethod]
    public void CandidateHoldingOrInsideAnInstalledAppsLocationIsNeverOffered()
    {
        using var fx = new ProfileFixture("leftover-nested");
        var suite = Folder(Local(fx, "Contoso"));
        var notes = App("Contoso Notes", "Contoso", Folder(Local(fx, "Contoso", "Notes")));
        AssertOffered(Match(App("Contoso Suite", "Contoso Inc", suite), [notes], None, fx), "suite holding an installed app");

        var host = App("Host", "Fabrikam", Folder(Programs(fx, "Host")));
        var plugin = Folder(Programs(fx, "Host", "plugins", "Plug"));
        AssertOffered(Match(App("Plug", "Someone", plugin), [host], None, fx), "plug-in inside an installed app");
    }

    // Criterion 6 decoy "an app re-registered under a new key": another entry still names the same install location and
    // display name; nothing of it is offered.
    [TestMethod]
    public void ReRegisteredAppOffersNothing()
    {
        using var fx = new ProfileFixture("leftover-reregistered");
        var location = Folder(Programs(fx, "Contoso Notes"));
        Folder(Roaming(fx, "Contoso Notes"));
        Folder(Roaming(fx, "Contoso", "Contoso Notes"));
        var gone = App("Contoso Notes", "Contoso", location);
        var again = gone with { Version = "2.0", Entry = gone.Entry! with { KeyName = "{NEW-KEY}", DisplayVersion = "2.0" } };
        AssertOffered(Match(gone, [again], None, fx), "re-registered under a new key");
    }

    // Criterion 6 decoy "two entries with one name", docs/spec/04 rule 4: when another installed entry, or another
    // just-removed one, has the same display name (and publisher), the name folders belong to nobody; the gone app's own
    // install location, which only it names, is still offered.
    [TestMethod]
    public void TwoEntriesWithOneNameOwnTheNameFoldersTogetherSoNobodyDoes()
    {
        using var fx = new ProfileFixture("leftover-two-names");
        var ownLocation = Folder(Programs(fx, "contoso-notes-user"));
        string[] shared = [Folder(Roaming(fx, "Contoso Notes")), Folder(Local(fx, "Contoso Notes")), Folder(Roaming(fx, "Contoso", "Contoso Notes"))];
        var gone = App("Contoso Notes", "Contoso", ownLocation);
        var machineWide = App("Contoso Notes", "Contoso", Folder(Path.Combine(fx.ProgramFiles, "Contoso Notes")), AppClass.NeedsAdmin);
        var otherCopy = App("Contoso Notes", "Contoso", Folder(Programs(fx, "contoso-notes-old")));

        var whileInstalled = Match(gone, [machineWide], None, fx);
        var withJustRemoved = Match(gone, None, [otherCopy], fx);
        foreach (var (result, what) in new[] { (whileInstalled, "a same-name entry still installed"), (withJustRemoved, "a same-name entry just removed") })
        {
            foreach (var folder in shared)
            {
                AssertNotOffered(result, folder, what);
            }

            AssertOffered(result, what, ownLocation);
        }

        AssertNotOffered(withJustRemoved, Programs(fx, "contoso-notes-old"), "the other just-removed copy's install location");
    }

    // Rule 4: a just-removed app whose install location is a gone app's name folder makes it a tie.
    [TestMethod]
    public void JustRemovedAppsCountAsOwners()
    {
        using var fx = new ProfileFixture("leftover-just-removed");
        var folder = Folder(Programs(fx, "Contoso Notes"));
        var gone = App("Contoso Notes", "Contoso", null);
        var other = App("Notes Classic", "Fabrikam", folder);
        AssertOffered(Match(gone, None, [other], fx), "the install location of another just-removed app");
        AssertOffered(Match(gone, None, None, fx), "control: alone", folder);
    }

    // docs/spec/04 Windows note, "Plan and run": a per-machine ("needs admin") entry's per-user leftovers are offered once
    // it is gone, like any other.
    [TestMethod]
    public void PerMachineAppsPerUserLeftoversAreOffered()
    {
        using var fx = new ProfileFixture("leftover-per-machine");
        var data = Folder(Roaming(fx, "Contoso Notes"));
        var gone = App("Contoso Notes", "Contoso", Path.Combine(fx.ProgramFiles, "Contoso Notes"), AppClass.NeedsAdmin);
        AssertOffered(Match(gone, None, None, fx), "per-machine app gone", data);
    }

    // Exact names from the registry are names, never paths: a display name or publisher with "..", "." or a separator
    // never reaches outside the three folders, nor one of them.
    [TestMethod]
    public void NamesThatAreNotPlainFolderNamesMatchNothing()
    {
        using var fx = new ProfileFixture("leftover-hostile-names");
        Folder(Programs(fx));
        Folder(fx.InProfile("Documents", "Secret"));
        Folder(Roaming(fx, "Contoso", "Notes"));
        var cases = new (string Name, string? Publisher)[]
        {
            ("..", null), (".", null), (@"..\..\Documents", null), (@"..\..\..\Documents\Secret", null), ("Local", ".."), ("Programs", null),
            (@"Contoso\Notes", null), ("Notes", @"..\Roaming\Contoso"), ("Roaming", ".."), (@"\", null), ("C:", null), ("", "Contoso"), ("Notes", ""),
        };
        foreach (var (name, publisher) in cases)
        {
            var result = Match(App(name, publisher, null), None, None, fx);
            AssertOffered(result, $"display name '{name}', publisher '{publisher}'");
        }
    }

    // docs/spec/02 Selection rules, docs/spec/04 "leftovers are not" pre-selected: a candidate carries no selection; it is
    // only offered (selectable) or shown with a note.
    [TestMethod]
    public void CandidatesCarryNoPreSelection()
    {
        var properties = typeof(LeftoverCandidate).GetProperties();
        Assert.IsFalse(properties.Any(p => p.Name.Contains("Selected", StringComparison.OrdinalIgnoreCase)), string.Join(", ", properties.Select(p => p.Name)));
        Assert.AreEqual(1, properties.Count(p => p.PropertyType == typeof(bool)), "Selectable is the only flag");

        using var fx = new ProfileFixture("leftover-no-preselect");
        var data = Folder(Roaming(fx, "Contoso Notes"));
        var result = Match(App("Contoso Notes", "Contoso", null), None, None, fx);
        AssertOffered(result, "control", data);
    }

    // Decision 10, LeftoverMatcher contract "Reads names only; nothing is opened for data": a candidate holding a file no
    // one may open (held exclusively by this test) is found as usual, and the file is untouched.
    [TestMethod]
    public void CandidateIsFoundWithoutOpeningItsFiles()
    {
        using var fx = new ProfileFixture("leftover-no-open");
        var folder = Folder(Roaming(fx, "Contoso Notes"));
        var file = ProfileFixture.MakeFile(Path.Combine(folder, "notes.db"));
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = Match(App("Contoso Notes", "Contoso", null), None, None, fx);
            AssertOffered(result, "with an exclusively held file inside", folder);
        }

        Assert.AreEqual(4096L, new FileInfo(file).Length);
    }

    // Decision 10, "nothing is opened for data", with the test machine's cloud provider as the oracle: a leftover folder
    // that holds an online-only placeholder is matched without one fetch (positive control: this process's own read is
    // logged). The fixture is named "cloud-*" with its root "cloud", so the cloud tests' sweep removes it after a crash.
    [TestMethod]
    [TestCategory("Console")]
    public void CandidateHoldingAnOnlineOnlyFileIsMatchedWithoutAFetch()
    {
        CloudRoot.RequireInstalled();
        using var fx = new ProfileFixture("cloud-leftover");
        var folder = Folder(Programs(fx, "Contoso Notes"));
        using var cloud = CloudRoot.Register(Path.Combine(folder, "cloud"), Path.Combine(fx.OtherVolume, "fetch.log"));
        cloud.Populate(("data.bin", ByteUnits.MB), ("control.bin", 4 * ByteUnits.KB), ("sub\\", 0), ("sub\\more.bin", ByteUnits.MB));
        cloud.Serve();
        cloud.RequireOnlineOnly("data.bin");
        using (var stream = new FileStream(cloud.Combine("control.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.True(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        var pid = Environment.ProcessId;
        Check.True(cloud.FetchesOf("control.bin").Any(l => l.Contains("\"pid\":" + pid + ",", StringComparison.Ordinal) || l.Contains("\"pid\":" + pid + "}", StringComparison.Ordinal)),
            $"the fetch log did not record this process's (pid {pid}) read; fetches: [{string.Join(" | ", cloud.Fetches())}]");
        cloud.MarkFetchBaseline();

        var result = Match(App("Contoso Notes", "Contoso", folder), None, None, fx);
        AssertOffered(result, "install location holding a cloud folder", folder);
        cloud.AssertNoFetch("LeftoverMatcher.Match");
        Assert.AreEqual(0L, cloud.Status("data.bin").OnDisk, "data.bin was fetched");
    }
}
