using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-17 phase A (intent/2026-10-20-asp-17-suggest.md): <see cref="Suggest.LargeOld"/>. Two kinds of file:
/// <see cref="SuggestionKind.OldInstaller"/> (strictly inside <c>downloads</c> by canonical path on a component boundary,
/// one of eleven extensions ignoring case, last write more than 90 days before now by LargeOldFinder's minute rule) and
/// <see cref="SuggestionKind.LargeUnused"/> (AllocatedBytes at least 1 GiB, more than 365 days). Both: listed once, as
/// OldInstaller. Never a folder, a node marked Link, Unreadable, CloudNotDownloaded or HardLinkCountedElsewhere, nor a
/// node with a DisplayOnly hint. Largest AllocatedBytes first, then path ignoring case, then ordinal. Session 0 (no Console
/// category); written by the test writer before the rule, against a stub.</summary>
[TestClass]
public sealed class SuggestLargeOldTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const long GiB = ByteUnits.GB;

    private static long Ago(TimeSpan age) => Trees.Minutes(Now - age);

    private static long DaysAgo(int days) => Ago(TimeSpan.FromDays(days));

    private static string[] Names(IEnumerable<SuggestedFile> rows) => rows.Select(r => r.Node.Name).ToArray();

    private static string[] Paths(IEnumerable<SuggestedFile> rows) => rows.Select(r => r.Node.Path).ToArray();

    /// <summary>A scan tree holding <paramref name="nodes"/> under one root (each node keeps the path it was made with).</summary>
    private static ScanTree TreeOf(ProfileFixture fx, params ScanNode[] nodes)
    {
        var root = ScanNode.Folder(fx.VolumeRoot);
        foreach (var node in nodes)
        {
            root.Add(node);
        }

        return Trees.Tree(root, nodes.Sum(n => Trees.Files(n).Sum(f => f.AllocatedBytes)));
    }

    private static string Downloads(ProfileFixture fx) => fx.InProfile("Downloads");

    private static ScanNode InDownloads(ProfileFixture fx, string name, long bytes, long minutes, NodeMarks marks = NodeMarks.None) =>
        ScanNode.File(Path.Combine(Downloads(fx), name), bytes, bytes, minutes, null, marks);

    private static ScanNode InDocuments(ProfileFixture fx, string name, long bytes, long minutes, NodeMarks marks = NodeMarks.None) =>
        ScanNode.File(Path.Combine(fx.Documents, name), bytes, bytes, minutes, null, marks);

    private static IReadOnlyList<SuggestedFile> Run(ProfileFixture fx, ScanTree tree, string? downloads, DateTimeOffset? now = null, PathContext? context = null) =>
        Suggest.LargeOld(tree, now ?? Now, context ?? fx.Context, downloads);

    // ---- OldInstaller: the rule ----

    [TestMethod]
    public void AnOldSetupFileInDownloadsIsSuggestedAsAnOldInstaller()
    {
        using var fx = new ProfileFixture("suggest-installer");
        var setup = InDownloads(fx, "setup.exe", 5 * ByteUnits.MB, DaysAgo(120));
        var result = Run(fx, TreeOf(fx, setup), Downloads(fx));
        Assert.AreEqual(1, result.Count, string.Join(", ", Paths(result)));
        var row = result[0];
        Assert.AreSame(setup, row.Node);
        Assert.AreEqual(SuggestionKind.OldInstaller, row.Kind);
        Assert.AreEqual(Trees.FromMinutes(setup.LastWriteMinutes), row.LastModified);
        Assert.AreEqual(TypeGroup.Of(setup.Name), row.Type);
    }

    // An installer is any size: a 1 KB one counts (the size rule is LargeUnused's).
    [TestMethod]
    public void AnInstallerIsAnySize()
    {
        using var fx = new ProfileFixture("suggest-installer-size");
        var tree = TreeOf(fx, InDownloads(fx, "tiny.msi", 1024, DaysAgo(200)), InDownloads(fx, "huge.iso", 3 * GiB, DaysAgo(100)));
        var result = Run(fx, tree, Downloads(fx));
        Check.Sequence(Names(result), "huge.iso", "tiny.msi");
        Assert.IsTrue(result.All(r => r.Kind == SuggestionKind.OldInstaller));
    }

    // The eleven extensions, each suggested; ignoring case.
    [TestMethod]
    public void EveryListedExtensionIsAnInstallerIgnoringCase()
    {
        using var fx = new ProfileFixture("suggest-ext");
        string[] extensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso", ".img", ".zip", ".7z", ".rar"];
        var nodes = new List<ScanNode>();
        foreach (var ext in extensions)
        {
            nodes.Add(InDownloads(fx, "lower" + ext, 4096, DaysAgo(200)));
            nodes.Add(InDownloads(fx, "UPPER" + ext.ToUpperInvariant(), 4096, DaysAgo(200)));
            nodes.Add(InDownloads(fx, "Mixed." + char.ToUpperInvariant(ext[1]) + ext[2..], 4096, DaysAgo(200)));
        }

        var result = Run(fx, TreeOf(fx, [.. nodes]), Downloads(fx));
        Check.Set(Names(result), nodes.Select(n => n.Name).ToArray());
        Assert.IsTrue(result.All(r => r.Kind == SuggestionKind.OldInstaller));
    }

    // Other extensions are not installers (and a longer or embedded extension is not a match).
    [TestMethod]
    public void OtherExtensionsAreNotInstallers()
    {
        using var fx = new ProfileFixture("suggest-ext-other");
        string[] names = ["notes.txt", "photo.jpg", "doc.pdf", "image.dmg", "tool.jar", "pack.tar", "pack.gz", "pack.tgz", "a.zipx", "a.isox", "a.exe.txt",
            "a.msixx", "a.7zip", "a.rar5", "a.exe~", "a.dll", "a.bat", "a.ps1", "a.vhdx", "setup", "exe", "zip"];
        var tree = TreeOf(fx, [.. names.Select(n => InDownloads(fx, n, 4096, DaysAgo(500))), InDownloads(fx, "control.exe", 4096, DaysAgo(500))]);
        var result = Run(fx, tree, Downloads(fx));
        Check.Sequence(Names(result), "control.exe");
    }

    // Only inside Downloads: the same old setup file elsewhere is not an installer (and is too small for LargeUnused).
    [TestMethod]
    public void AnInstallerOutsideDownloadsIsNotSuggested()
    {
        using var fx = new ProfileFixture("suggest-installer-elsewhere");
        var tree = TreeOf(fx,
            InDocuments(fx, "setup.exe", 4096, DaysAgo(500)),
            ScanNode.File(fx.InProfile("Desktop", "setup.msi"), 4096, 4096, DaysAgo(500)),
            InDownloads(fx, "control.exe", 4096, DaysAgo(500)));
        Check.Sequence(Names(Run(fx, tree, Downloads(fx))), "control.exe");
    }

    // Component boundary: Downloads2, DownloadsX, Downloads.old and MyDownloads are not Downloads.
    [TestMethod]
    public void DownloadsIsComparedByWholeComponentsNotByPrefix()
    {
        using var fx = new ProfileFixture("suggest-downloads-boundary");
        var tree = TreeOf(fx,
            ScanNode.File(fx.InProfile("Downloads2", "old.iso"), 4096, 4096, DaysAgo(200)),
            ScanNode.File(fx.InProfile("DownloadsX", "old.iso"), 4096, 4096, DaysAgo(200)),
            ScanNode.File(fx.InProfile("Downloads.old", "old.iso"), 4096, 4096, DaysAgo(200)),
            ScanNode.File(fx.InProfile("MyDownloads", "old.iso"), 4096, 4096, DaysAgo(200)),
            ScanNode.File(fx.InProfile("Downloads", "real.iso"), 4096, 4096, DaysAgo(200)));
        Check.Sequence(Paths(Run(fx, tree, Downloads(fx))), fx.InProfile("Downloads", "real.iso"));
    }

    // Strictly inside includes any depth: a file in a folder in a folder of Downloads is inside.
    [TestMethod]
    public void AFileInANestedFolderOfDownloadsIsInside()
    {
        using var fx = new ProfileFixture("suggest-downloads-nested");
        var tree = TreeOf(fx,
            ScanNode.File(fx.InProfile("Downloads", "Games", "Mods", "mod.zip"), 4096, 4096, DaysAgo(200)),
            ScanNode.File(fx.InProfile("Downloads", "Games", "readme.txt"), 4096, 4096, DaysAgo(200)));
        Check.Sequence(Names(Run(fx, tree, Downloads(fx))), "mod.zip");
    }

    // The comparison is on canonical paths: case, a trailing separator and ".." in the downloads text do not matter.
    [TestMethod]
    public void DownloadsIsComparedAsACanonicalPath()
    {
        using var fx = new ProfileFixture("suggest-downloads-canonical");
        var setup = InDownloads(fx, "setup.exe", 4096, DaysAgo(200));
        var tree = TreeOf(fx, setup);
        string[] spellings =
        [
            fx.InProfile("DOWNLOADS"),
            Downloads(fx) + Path.DirectorySeparatorChar,
            Path.Combine(fx.Documents, "..", "Downloads"),
            Path.Combine(fx.Profile, ".", "Downloads"),
        ];
        foreach (var spelling in spellings)
        {
            Check.Sequence(Names(Run(fx, tree, spelling)), "setup.exe");
        }

        // The node's own spelling differs in case too.
        var upper = TreeOf(fx, ScanNode.File(fx.InProfile("DOWNLOADS", "Setup.EXE"), 4096, 4096, DaysAgo(200)));
        Check.Sequence(Names(Run(fx, upper, Downloads(fx))), "Setup.EXE");
    }

    // "Strictly inside": the Downloads path itself is not inside it (here a node standing at that very path).
    [TestMethod]
    public void TheDownloadsPathItselfIsNotInsideIt()
    {
        using var fx = new ProfileFixture("suggest-downloads-itself");
        var node = ScanNode.File(fx.InProfile("Downloads", "x.zip"), 4096, 4096, DaysAgo(200));
        var tree = TreeOf(fx, node);
        Assert.AreEqual(1, Run(fx, tree, Downloads(fx)).Count, "precondition: inside the real Downloads folder it is an installer");
        Assert.AreEqual(0, Run(fx, tree, node.Path).Count);
    }

    // Null downloads: no installer rule at all (an old installer elsewhere stays an ordinary file: here 4 KB, so nothing).
    [TestMethod]
    public void WithoutADownloadsFolderThereIsNoInstallerRule()
    {
        using var fx = new ProfileFixture("suggest-downloads-null");
        var tree = TreeOf(fx, InDownloads(fx, "setup.exe", 4096, DaysAgo(500)), InDownloads(fx, "disk.iso", 500 * ByteUnits.MB, DaysAgo(500)));
        Assert.AreEqual(2, Run(fx, tree, Downloads(fx)).Count, "precondition: with Downloads both are installers");
        Assert.AreEqual(0, Run(fx, tree, downloads: null).Count);
    }

    // Null downloads leaves LargeUnused as it is: a 2 GiB file old enough is still LargeUnused, never OldInstaller.
    [TestMethod]
    public void WithoutADownloadsFolderALargeUnusedFileIsStillSuggested()
    {
        using var fx = new ProfileFixture("suggest-downloads-null-large");
        var tree = TreeOf(fx, InDownloads(fx, "disk.iso", 2 * GiB, DaysAgo(500)));
        var result = Run(fx, tree, downloads: null);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(SuggestionKind.LargeUnused, result[0].Kind);
    }

    // A downloads text that does not canonicalise gives no installer rule (nothing is "inside" it): empty, blank, a
    // device-namespace spelling, a name ending in a dot.
    [TestMethod]
    public void ADownloadsPathThatDoesNotCanonicaliseGivesNoInstallerRule()
    {
        using var fx = new ProfileFixture("suggest-downloads-bad");
        var tree = TreeOf(fx, InDownloads(fx, "setup.exe", 4096, DaysAgo(500)));
        Assert.AreEqual(1, Run(fx, tree, Downloads(fx)).Count, "precondition: with the real Downloads it is an installer");
        foreach (var bad in new[] { "", "   ", @"\\.\C:\Users", @"\\?\UNC\server\share", fx.InProfile("Downloads.") })
        {
            Assert.AreEqual(0, Run(fx, tree, bad).Count, "downloads = \"" + bad + "\"");
        }
    }

    // ---- OldInstaller: the 90-day boundary (LargeOldFinder's minute rule) ----

    // More than 90 days: the same minute as now - 90 days is not more; one minute earlier is. Judged at a whole minute and
    // at half a minute past it.
    [TestMethod]
    public void InstallerAgeBoundaryIsNinetyDaysByTheMinuteRule()
    {
        using var fx = new ProfileFixture("suggest-installer-age");
        foreach (var now in new[] { Now, Now.AddSeconds(30) })
        {
            var threshold = Trees.Minutes(now - TimeSpan.FromDays(90));
            var tree = TreeOf(fx,
                InDownloads(fx, "exactly.exe", 4096, threshold),
                InDownloads(fx, "minute-earlier.exe", 4096, threshold - 1),
                InDownloads(fx, "minute-later.exe", 4096, threshold + 1));
            Check.Sequence(Names(Run(fx, tree, Downloads(fx), now)), "minute-earlier.exe");
        }
    }

    [TestMethod]
    public void AnInstallerOfEightyNineDaysIsNotSuggestedAndOneOfNinetyOneIs()
    {
        using var fx = new ProfileFixture("suggest-installer-age-days");
        var tree = TreeOf(fx, InDownloads(fx, "d89.zip", 4096, DaysAgo(89)), InDownloads(fx, "d91.zip", 4096, DaysAgo(91)), InDownloads(fx, "today.zip", 4096, DaysAgo(0)));
        Check.Sequence(Names(Run(fx, tree, Downloads(fx))), "d91.zip");
    }

    // ---- LargeUnused ----

    [TestMethod]
    public void AFileOfOneGiBOrMoreUnusedForAYearIsSuggestedAsLargeUnused()
    {
        using var fx = new ProfileFixture("suggest-large");
        var video = InDocuments(fx, "film.mkv", 2 * GiB, DaysAgo(500));
        var result = Run(fx, TreeOf(fx, video), Downloads(fx));
        Assert.AreEqual(1, result.Count);
        Assert.AreSame(video, result[0].Node);
        Assert.AreEqual(SuggestionKind.LargeUnused, result[0].Kind);
        Assert.AreEqual(Trees.FromMinutes(video.LastWriteMinutes), result[0].LastModified);
        Assert.AreEqual(TypeGroup.Of("film.mkv"), result[0].Type);
        Assert.AreEqual(TypeGroupKind.Video, result[0].Type);
    }

    // Size boundary: 1 GiB - 1 is not enough, 1 GiB is (AllocatedBytes, not the length).
    [TestMethod]
    public void SizeBoundaryIsOneGiBOfAllocatedBytes()
    {
        using var fx = new ProfileFixture("suggest-large-size");
        var tree = TreeOf(fx,
            InDocuments(fx, "one-less.bin", GiB - 1, DaysAgo(500)),
            InDocuments(fx, "exactly.bin", GiB, DaysAgo(500)),
            InDocuments(fx, "one-more.bin", GiB + 1, DaysAgo(500)),
            ScanNode.File(Path.Combine(fx.Documents, "sparse.bin"), 5 * GiB, 64 * ByteUnits.KB, DaysAgo(500)),
            ScanNode.File(Path.Combine(fx.Documents, "rounded-up.bin"), GiB - 4096, GiB, DaysAgo(500)));
        Check.Set(Names(Run(fx, tree, Downloads(fx))), "exactly.bin", "one-more.bin", "rounded-up.bin");
    }

    // Age boundary: more than 365 days by the minute rule; judged at a whole minute and at half a minute past it.
    [TestMethod]
    public void LargeUnusedAgeBoundaryIsThreeHundredSixtyFiveDaysByTheMinuteRule()
    {
        using var fx = new ProfileFixture("suggest-large-age");
        foreach (var now in new[] { Now, Now.AddSeconds(30) })
        {
            var threshold = Trees.Minutes(now - TimeSpan.FromDays(365));
            var tree = TreeOf(fx,
                InDocuments(fx, "exactly.bin", GiB, threshold),
                InDocuments(fx, "minute-earlier.bin", GiB, threshold - 1),
                InDocuments(fx, "minute-later.bin", GiB, threshold + 1));
            Check.Sequence(Names(Run(fx, tree, Downloads(fx), now)), "minute-earlier.bin");
        }
    }

    [TestMethod]
    public void ALargeFileOfThreeHundredSixtyFourDaysIsNotSuggested()
    {
        using var fx = new ProfileFixture("suggest-large-age-days");
        var tree = TreeOf(fx, InDocuments(fx, "d364.bin", 2 * GiB, DaysAgo(364)), InDocuments(fx, "d366.bin", 2 * GiB, DaysAgo(366)),
            InDocuments(fx, "d100.bin", 2 * GiB, DaysAgo(100)));
        Check.Sequence(Names(Run(fx, tree, Downloads(fx))), "d366.bin");
    }

    // A large file in Downloads that is not a listed extension is LargeUnused, and a large one that is an installer but only
    // 100 days old is an OldInstaller (it is not 365 days old, so not LargeUnused).
    [TestMethod]
    public void TheKindFollowsTheRuleThatHolds()
    {
        using var fx = new ProfileFixture("suggest-kinds");
        var tree = TreeOf(fx,
            InDownloads(fx, "film.mkv", 2 * GiB, DaysAgo(400)),
            InDownloads(fx, "installer.iso", 2 * GiB, DaysAgo(100)),
            InDownloads(fx, "recent-film.mkv", 2 * GiB, DaysAgo(100)),
            InDocuments(fx, "backup.iso", 2 * GiB, DaysAgo(400)));
        var result = Run(fx, tree, Downloads(fx)).ToDictionary(r => r.Node.Name, r => r.Kind, StringComparer.Ordinal);
        Assert.AreEqual(3, result.Count, string.Join(", ", result.Keys));
        Assert.AreEqual(SuggestionKind.LargeUnused, result["film.mkv"]);
        Assert.AreEqual(SuggestionKind.OldInstaller, result["installer.iso"]);
        Assert.AreEqual(SuggestionKind.LargeUnused, result["backup.iso"]);
    }

    // ---- both kinds ----

    // A file that is both is listed once, as OldInstaller (also at the edge: 1 GiB exactly, 366 days).
    [TestMethod]
    public void AFileThatIsBothIsListedOnceAsAnOldInstaller()
    {
        using var fx = new ProfileFixture("suggest-both");
        var both = InDownloads(fx, "big.iso", 3 * GiB, DaysAgo(500));
        var edge = InDownloads(fx, "edge.zip", GiB, DaysAgo(366));
        var result = Run(fx, TreeOf(fx, both, edge), Downloads(fx));
        Assert.AreEqual(2, result.Count, string.Join(", ", Paths(result)));
        Assert.IsTrue(result.All(r => r.Kind == SuggestionKind.OldInstaller), string.Join(", ", result.Select(r => r.Kind)));
        Check.Sequence(Names(result), "big.iso", "edge.zip");
    }

    // ---- exclusions: every one for both kinds ----

    // A folder is never suggested, however large and old and whatever it is named; the files inside still are.
    [TestMethod]
    public void AFolderIsNeverSuggested()
    {
        using var fx = new ProfileFixture("suggest-folder");
        var oldMinutes = DaysAgo(500);
        var installerFolder = ScanNode.Folder(Path.Combine(Downloads(fx), "pack.zip"), oldMinutes)
            .Add(ScanNode.File(Path.Combine(Downloads(fx), "pack.zip", "inner.txt"), 10, 10, DaysAgo(1)));
        var largeFolder = ScanNode.Folder(Path.Combine(fx.Documents, "Archive"), oldMinutes)
            .Add(ScanNode.File(Path.Combine(fx.Documents, "Archive", "part.bin"), 2 * GiB, 2 * GiB, DaysAgo(1)));
        var result = Run(fx, TreeOf(fx, installerFolder, largeFolder), Downloads(fx));
        Assert.AreEqual(0, result.Count, string.Join(", ", Paths(result)));

        // A folder holding a file that qualifies: only the file is listed.
        var holder = ScanNode.Folder(Path.Combine(fx.Documents, "Old"), oldMinutes)
            .Add(ScanNode.File(Path.Combine(fx.Documents, "Old", "film.mkv"), 2 * GiB, 2 * GiB, oldMinutes));
        Check.Sequence(Paths(Run(fx, TreeOf(fx, holder), Downloads(fx))), Path.Combine(fx.Documents, "Old", "film.mkv"));
    }

    // An EMPTY folder is still a folder (it has no children, so only IsFolder tells it from a file): never suggested,
    // whatever its name, size or age. A control file in the same tree is suggested, so an empty list proves nothing here.
    [TestMethod]
    public void AnEmptyFolderIsNeverSuggested()
    {
        using var fx = new ProfileFixture("suggest-empty-folder");
        var installerFolder = ScanNode.Folder(Path.Combine(Downloads(fx), "old.iso"), DaysAgo(200));
        var setupFolder = ScanNode.Folder(Path.Combine(Downloads(fx), "setup.exe"), DaysAgo(200));
        var largeFolder = ScanNode.Folder(Path.Combine(fx.Documents, "Empty"), DaysAgo(400));
        var largeInstallerFolder = ScanNode.Folder(Path.Combine(Downloads(fx), "big.zip"), DaysAgo(400));
        var control = InDownloads(fx, "control.exe", 4096, DaysAgo(200));
        var tree = TreeOf(fx, installerFolder, setupFolder, largeFolder, largeInstallerFolder, control);

        // The walk totals an empty folder to 0; give two of them a size the way a measured one would have (Core internals).
        largeFolder.AllocatedBytes = 2 * GiB;
        largeInstallerFolder.AllocatedBytes = 2 * GiB;
        Assert.AreEqual(0, installerFolder.Children.Count, "precondition: the folder is empty");
        Assert.IsTrue(largeFolder.IsFolder && largeFolder.Children.Count == 0 && largeFolder.AllocatedBytes >= GiB, "precondition: a large empty folder");

        Check.Sequence(Paths(Run(fx, tree, Downloads(fx))), control.Path);
    }

    // Never a node marked Link, Unreadable, CloudNotDownloaded or HardLinkCountedElsewhere (alone or with another mark), for
    // an installer and for a large file alike; an unmarked twin and a Hidden one are suggested.
    [TestMethod]
    public void AMarkedFileIsNeverSuggested()
    {
        using var fx = new ProfileFixture("suggest-marks");
        var oldMinutes = DaysAgo(500);
        var nodes = new List<ScanNode>
        {
            InDownloads(fx, "twin.exe", 4096, oldMinutes),
            InDownloads(fx, "hidden.exe", 4096, oldMinutes, NodeMarks.Hidden),
            InDocuments(fx, "twin.bin", 2 * GiB, oldMinutes),
            InDocuments(fx, "hidden.bin", 2 * GiB, oldMinutes, NodeMarks.Hidden),
        };
        var marks = new (string Name, NodeMarks Marks)[]
        {
            ("link", NodeMarks.Link),
            ("unreadable", NodeMarks.Unreadable),
            ("cloud", NodeMarks.CloudNotDownloaded),
            ("hardlink", NodeMarks.HardLinkCountedElsewhere),
            ("link-hidden", NodeMarks.Link | NodeMarks.Hidden),
            ("cloud-hidden", NodeMarks.CloudNotDownloaded | NodeMarks.Hidden),
            ("unreadable-cloud", NodeMarks.Unreadable | NodeMarks.CloudNotDownloaded),
        };
        foreach (var (name, mark) in marks)
        {
            nodes.Add(InDownloads(fx, name + ".exe", 4096, oldMinutes, mark));
            nodes.Add(InDocuments(fx, name + ".bin", 2 * GiB, oldMinutes, mark));
        }

        var result = Run(fx, TreeOf(fx, [.. nodes]), Downloads(fx));
        Check.Set(Names(result), "twin.exe", "hidden.exe", "twin.bin", "hidden.bin");
    }

    // Never a node with a DisplayOnly hint: protected by you, system, other users, sync roots, device backups, AppData, outside
    // the allowed roots. Each is a large old file; the one in Documents is the control.
    [TestMethod]
    public void ALargeFileWithADisplayOnlyHintIsNeverSuggested()
    {
        using var fx = new ProfileFixture("suggest-hints-large");
        var oldMinutes = DaysAgo(500);
        var protectedFolder = Path.Combine(fx.Documents, "Keep");
        var context = fx.Context with { UserProtectedFolders = [protectedFolder] };
        var hinted = new[]
        {
            Path.Combine(protectedFolder, "protected.bin"),
            Path.Combine(fx.OneDrive, "Videos", "synced.bin"),
            Path.Combine(fx.ProgramFiles, "Contoso", "system.bin"),
            Path.Combine(fx.WindowsFolder, "big.bin"),
            Path.Combine(fx.OtherProfile, "Documents", "others.bin"),
            Path.Combine(fx.LocalAppData, "Contoso", "Cache", "blob.bin"),
            fx.InProfile("loose.bin"),
            fx.InProfile("Apple", "MobileSync", "Backup", "00008030-000A1B2C3D4E5F6G", "Manifest.db"),
        };
        foreach (var path in hinted)
        {
            Assert.IsNotNull(DisplayOnly.HintFor(path, context), "precondition: " + path + " has a hint");
        }

        var control = InDocuments(fx, "control.bin", 2 * GiB, oldMinutes);
        Assert.IsNull(DisplayOnly.HintFor(control.Path, context), "precondition: the control has no hint");
        var tree = TreeOf(fx, [control, .. hinted.Select(p => ScanNode.File(p, 2 * GiB, 2 * GiB, oldMinutes))]);
        Check.Sequence(Names(Run(fx, tree, Downloads(fx), context: context)), "control.bin");
    }

    // The same for an installer: a Downloads folder inside a sync root, and a protected folder inside Downloads.
    [TestMethod]
    public void AnInstallerWithADisplayOnlyHintIsNeverSuggested()
    {
        using var fx = new ProfileFixture("suggest-hints-installer");
        var oldMinutes = DaysAgo(200);
        var protectedFolder = Path.Combine(Downloads(fx), "Keep");
        var context = fx.Context with { UserProtectedFolders = [protectedFolder] };
        var tree = TreeOf(fx,
            InDownloads(fx, "control.exe", 4096, oldMinutes),
            ScanNode.File(Path.Combine(protectedFolder, "protected.exe"), 4096, 4096, oldMinutes));
        Assert.IsNotNull(DisplayOnly.HintFor(Path.Combine(protectedFolder, "protected.exe"), context), "precondition");
        Check.Sequence(Names(Run(fx, tree, Downloads(fx), context: context)), "control.exe");

        // Downloads redirected into OneDrive (a sync root): every file there carries the sync hint.
        var syncedDownloads = Path.Combine(fx.OneDrive, "Downloads");
        var synced = ScanNode.File(Path.Combine(syncedDownloads, "setup.exe"), 4096, 4096, oldMinutes);
        Assert.IsNotNull(DisplayOnly.HintFor(synced.Path, fx.Context), "precondition");
        Assert.AreEqual(0, Run(fx, TreeOf(fx, synced), syncedDownloads).Count);

        // Downloads inside another user's folder or a system folder is no Downloads we may touch either.
        var other = ScanNode.File(Path.Combine(fx.OtherProfile, "Downloads", "setup.exe"), 4096, 4096, oldMinutes);
        Assert.AreEqual(0, Run(fx, TreeOf(fx, other), Path.Combine(fx.OtherProfile, "Downloads")).Count);
        var system = ScanNode.File(Path.Combine(fx.ProgramFiles, "Downloads", "setup.exe"), 4096, 4096, oldMinutes);
        Assert.AreEqual(0, Run(fx, TreeOf(fx, system), Path.Combine(fx.ProgramFiles, "Downloads")).Count);
    }

    // ---- order ----

    // Largest AllocatedBytes first (both kinds mixed), then path ignoring case.
    [TestMethod]
    public void RowsAreLargestFirstThenByPathIgnoringCase()
    {
        using var fx = new ProfileFixture("suggest-order");
        var nodes = new[]
        {
            InDownloads(fx, "small.zip", 1024, DaysAgo(100)),
            InDocuments(fx, "B.bin", 2 * GiB, DaysAgo(400)),
            InDownloads(fx, "b.iso", 2 * GiB, DaysAgo(100)),
            InDocuments(fx, "big.mkv", 5 * GiB, DaysAgo(400)),
            InDocuments(fx, "a.bin", 2 * GiB, DaysAgo(400)),
            InDownloads(fx, "mid.exe", GiB + 5, DaysAgo(100)),
        };
        string[] expected = ["big.mkv", "a.bin", "B.bin", "b.iso", "mid.exe", "small.zip"];
        Check.Sequence(Names(Run(fx, TreeOf(fx, nodes), Downloads(fx))), expected);
        Check.Sequence(Names(Run(fx, TreeOf(fx, [.. Enumerable.Reverse(nodes).Select(Clone)]), Downloads(fx))), expected);
    }

    // The order uses AllocatedBytes, not the length.
    [TestMethod]
    public void RowsAreOrderedByAllocatedBytesNotLength()
    {
        using var fx = new ProfileFixture("suggest-order-allocated");
        var tree = TreeOf(fx,
            ScanNode.File(Path.Combine(fx.Documents, "long-but-small.bin"), 9 * GiB, 2 * GiB, DaysAgo(500)),
            ScanNode.File(Path.Combine(fx.Documents, "short-but-big.bin"), 2 * GiB, 3 * GiB, DaysAgo(500)));
        Check.Sequence(Names(Run(fx, tree, Downloads(fx))), "short-but-big.bin", "long-but-small.bin");
    }

    // Equal size and equal path ignoring case: ordinal order (an uppercase letter sorts before its lowercase one).
    [TestMethod]
    public void EqualPathsIgnoringCaseAreOrderedOrdinally()
    {
        using var fx = new ProfileFixture("suggest-order-ordinal");
        var lower = InDocuments(fx, "x.bin", 2 * GiB, DaysAgo(500));
        var upper = InDocuments(fx, "X.bin", 2 * GiB, DaysAgo(500));
        var other = InDocuments(fx, "y.bin", 2 * GiB, DaysAgo(500));
        Check.Sequence(Names(Run(fx, TreeOf(fx, lower, upper, other), Downloads(fx))), "X.bin", "x.bin", "y.bin");
        var lower2 = InDocuments(fx, "x.bin", 2 * GiB, DaysAgo(500));
        var upper2 = InDocuments(fx, "X.bin", 2 * GiB, DaysAgo(500));
        var other2 = InDocuments(fx, "y.bin", 2 * GiB, DaysAgo(500));
        Check.Sequence(Names(Run(fx, TreeOf(fx, other2, upper2, lower2), Downloads(fx))), "X.bin", "x.bin", "y.bin");
    }

    // ---- Why ----

    // Why names the kind and the last-write date: Text.WhyOldInstaller / Text.WhyLargeUnused. The test times are noon UTC;
    // the date is accepted as the UTC day or the local day (the contract does not say which: see the questions).
    [TestMethod]
    public void WhyNamesTheKindAndTheLastWriteDate()
    {
        using var fx = new ProfileFixture("suggest-why");
        var written = new DateTimeOffset(2025, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var installer = InDownloads(fx, "setup.exe", 4096, Trees.Minutes(written));
        var large = InDocuments(fx, "film.mkv", 2 * GiB, Trees.Minutes(written));
        var result = Run(fx, TreeOf(fx, installer, large), Downloads(fx)).ToDictionary(r => r.Node.Name, r => r, StringComparer.Ordinal);
        Assert.AreEqual(2, result.Count);
        SuggestDates.AssertWhy(result["setup.exe"].Why, "Old download, unchanged since ", written);
        SuggestDates.AssertWhy(result["film.mkv"].Why, "1 GB or more, unchanged since ", written);
        Assert.AreEqual(Text.WhyOldInstaller(result["setup.exe"].LastModified), result["setup.exe"].Why);
        Assert.AreEqual(Text.WhyLargeUnused(result["film.mkv"].LastModified), result["film.mkv"].Why);
    }

    // The date is the LOCAL day: at a moment whose local day differs from its UTC day, both Why texts name the local one.
    [TestMethod]
    public void WhyNamesTheLocalDayNotTheUtcDay()
    {
        var written = SuggestDates.AMomentWhoseLocalDayDiffers();
        using var fx = new ProfileFixture("suggest-why-local");
        var minutes = Trees.Minutes(new DateTimeOffset(written));
        var result = Run(fx, TreeOf(fx, InDownloads(fx, "setup.exe", 4096, minutes), InDocuments(fx, "film.mkv", 2 * GiB, minutes)), Downloads(fx))
            .ToDictionary(r => r.Node.Name, r => r.Why, StringComparer.Ordinal);
        Assert.AreEqual(2, result.Count);
        var installer = "Old download, unchanged since ";
        var large = "1 GB or more, unchanged since ";
        Assert.AreEqual(installer + SuggestDates.LocalDay(written), result["setup.exe"]);
        Assert.AreEqual(large + SuggestDates.LocalDay(written), result["film.mkv"]);
        Assert.AreNotEqual(installer + SuggestDates.UtcDay(written), result["setup.exe"]);
        Assert.AreNotEqual(large + SuggestDates.UtcDay(written), result["film.mkv"]);
        Assert.AreEqual(installer + SuggestDates.LocalDay(written), Text.WhyOldInstaller(new DateTimeOffset(written)));
        Assert.AreEqual(large + SuggestDates.LocalDay(written), Text.WhyLargeUnused(new DateTimeOffset(written)));
    }

    // Downloads reached through an 8.3 short name on the way: a file whose node path spells a folder of Downloads (and the
    // folders above it) in short names is still inside Downloads. The short name comes from GetShortPathName; a volume
    // without short names makes the test Inconclusive. Controls: the same folder name outside Downloads, and in a sibling
    // folder "Downloads2", reached the same way, stay unsuggested; a long-name file in Downloads is suggested.
    [TestMethod]
    public void AFileInDownloadsReachedByAShortPathIsStillAnOldInstaller()
    {
        using var fx = new ProfileFixture("suggest-short-path");
        const string LongName = "Long Folder Name For Short Path";
        var inDownloads = ProfileFixture.MakeFolder(Path.Combine(Downloads(fx), LongName));
        var inSibling = ProfileFixture.MakeFolder(fx.InProfile("Downloads2", LongName));
        var inDocuments = ProfileFixture.MakeFolder(Path.Combine(fx.Documents, LongName));
        var shortDownloads = Native.ShortPathOf(inDownloads);
        var shortSibling = Native.ShortPathOf(inSibling);
        var shortDocuments = Native.ShortPathOf(inDocuments);
        if (shortDownloads is null || shortSibling is null || shortDocuments is null || !shortDownloads.Contains('~', StringComparison.Ordinal))
        {
            Assert.Inconclusive("The volume of " + fx.Profile + " keeps no 8.3 short names, so a short path cannot be built.");
        }

        var minutes = DaysAgo(200);
        var shortSetup = ScanNode.File(shortDownloads + @"\old.iso", 4096, 4096, minutes);
        var tree = TreeOf(fx,
            shortSetup,
            ScanNode.File(inDownloads + @"\long.zip", 4096, 4096, minutes),
            ScanNode.File(shortSibling + @"\sibling.iso", 4096, 4096, minutes),
            ScanNode.File(shortDocuments + @"\documents.zip", 4096, 4096, minutes),
            ScanNode.File(inDocuments + @"\documents-long.zip", 4096, 4096, minutes));

        var result = Run(fx, tree, Downloads(fx));
        Check.Set(Names(result), "old.iso", "long.zip");
        Assert.IsTrue(result.All(r => r.Kind == SuggestionKind.OldInstaller));
        Assert.AreSame(shortSetup, result.Single(r => r.Node.Name == "old.iso").Node);
    }

    [TestMethod]
    public void AnEmptyTreeGivesNothing()
    {
        using var fx = new ProfileFixture("suggest-empty");
        Assert.AreEqual(0, Run(fx, Trees.Tree(ScanNode.Folder(fx.Profile), 0), Downloads(fx)).Count);
    }

    // The decision is made at the now it is given, not at the clock: the same tree is empty at one now and full at a later one.
    [TestMethod]
    public void TheDecisionIsMadeAtTheGivenNow()
    {
        using var fx = new ProfileFixture("suggest-now");
        var tree = TreeOf(fx, InDownloads(fx, "setup.exe", 4096, DaysAgo(30)), InDocuments(fx, "film.mkv", 2 * GiB, DaysAgo(300)));
        Assert.AreEqual(0, Run(fx, tree, Downloads(fx)).Count);
        Check.Sequence(Names(Run(fx, tree, Downloads(fx), Now.AddDays(70))), "film.mkv", "setup.exe");
    }

    // A fresh copy of a node (a node belongs to one tree).
    private static ScanNode Clone(ScanNode n) => ScanNode.File(n.Path, n.LogicalBytes, n.AllocatedBytes, n.LastWriteMinutes, null, n.Flags);
}
