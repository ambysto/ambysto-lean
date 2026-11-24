using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary><see cref="LeftoverMatcher.Match"/> in the phase B gaps (briefs U1–U3): docs/spec/04 Windows notes ("a vendor
/// folder another installed app may use is never offered"; "An empty install location matches nothing; locations compare
/// as paths"; the install location counts only "when it is at least one component inside one of these folders"), docs/spec/02
/// Windows note (cloud-sync roots never touched in v1, local files included), docs/spec/07 ("Display-only items always say
/// why and where to manage them"), intent criterion 6, plan notes "Leftover matcher". Each test builds a pretend profile
/// in a temp folder (<see cref="ProfileFixture"/>).</summary>
[TestClass]
public sealed class LeftoverMatcherPhaseBGapTests
{
    private static readonly IReadOnlyList<InstalledApp> None = [];

    // U1 [top]: the gone app "Contoso" (no publisher) has %APPDATA%\Contoso and %LOCALAPPDATA%\Contoso as name candidates;
    // an installed app whose publisher is Contoso may use them as its vendor folder, even when its own display name gives no
    // folder candidate (generic, empty, or not a plain folder name): neither is offered. Control: without that app, both
    // are offered by the display-name rule.
    [TestMethod]
    [DataRow("Update")]
    [DataRow("")]
    [DataRow("Contoso/Helper")]
    public void AnotherAppsVendorFolderIsNeverOfferedEvenWithoutItsNameCandidate(string installedName)
    {
        using var fx = new ProfileFixture("leftover-vendor-noname");
        var roaming = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso"));
        var local = ProfileFixture.MakeFolder(Path.Combine(fx.LocalAppData, "Contoso"));
        var gone = LeftoverMatcherTests.App("Contoso", null, null);

        var alone = LeftoverMatcherTests.Match(gone, None, None, fx);
        LeftoverMatcherTests.AssertOffered(alone, "control: no other app", roaming, local);
        Check.True(alone.All(c => c.Rule == LeftoverMatcher.DisplayNameRule), "control: by the display-name rule");

        var installed = LeftoverMatcherTests.App(installedName, "Contoso", null);
        var result = LeftoverMatcherTests.Match(gone, [installed], None, fx);

        LeftoverMatcherTests.AssertNotOffered(result, roaming, $"the vendor folder of the installed \"{installedName}\" by Contoso");
        LeftoverMatcherTests.AssertNotOffered(result, local, $"the vendor folder of the installed \"{installedName}\" by Contoso");
    }

    // U2 [normal]: a leftover inside a cloud-sync root is shown, not selectable, with the sync hint "Synced by Dropbox:
    // manage it there". Rows: the sync root is the candidate itself, and a sync root that holds it. Control: the same app's
    // candidate in %LOCALAPPDATA%, outside every sync root, is selectable with no note.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ALeftoverInsideASyncRootIsShownNotSelectable(bool rootIsTheCandidate)
    {
        using var fx = new ProfileFixture("leftover-syncroot");
        var roaming = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso"));
        var local = ProfileFixture.MakeFolder(Path.Combine(fx.LocalAppData, "Contoso"));
        var syncRoot = rootIsTheCandidate ? roaming : fx.RoamingAppData;
        var context = fx.Context with { SyncRoots = [new SyncRoot(syncRoot, "Dropbox")] };
        var gone = LeftoverMatcherTests.App("Contoso", null, null);

        var result = LeftoverMatcher.Match(gone, None, None, context);
        LeftoverMatcherTests.Invariants(result, fx);

        var synced = result.SingleOrDefault(c => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.Path)), roaming, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(synced, "the candidate inside the sync root is shown: " + string.Join("; ", result.Select(c => c.Path)));
        Check.False(synced.Selectable, "not selectable: " + synced.Path);
        Assert.AreEqual(Text.HintSynced("Dropbox"), synced.Note, "the sync hint");
        Assert.AreEqual("Synced by Dropbox: manage it there", synced.Note);

        var outside = result.SingleOrDefault(c => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.Path)), local, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(outside, "control: the candidate outside every sync root is listed");
        Check.True(outside.Selectable, "control: selectable");
        Assert.IsNull(outside.Note, "control: no note");
    }

    // U3 [low]: an install location that is not a full path matches nothing, whatever the current directory: relative,
    // rooted without a drive, and drive-relative spellings of %LOCALAPPDATA%\Contoso (the current directory is
    // %LOCALAPPDATA% during the test, restored after). Control: the full path is offered by the install-location rule.
    [TestMethod]
    [DoNotParallelize]
    public void AnInstallLocationThatIsNotAFullPathMatchesNothing()
    {
        using var fx = new ProfileFixture("leftover-relative-location");
        var contoso = ProfileFixture.MakeFolder(Path.Combine(fx.LocalAppData, "Contoso"));
        var drive = Path.GetPathRoot(contoso)!.TrimEnd('\\');
        Assert.AreEqual(2, drive.Length, "precondition: the fixture is on a drive letter: " + contoso);
        var spellings = new[]
        {
            "Contoso",
            contoso[drive.Length..],
            drive + "Contoso",
        };
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fx.LocalAppData;
            foreach (var spelling in spellings)
            {
                Check.True(string.Equals(Path.GetFullPath(spelling), contoso, StringComparison.OrdinalIgnoreCase), $"precondition: {spelling} resolves to {contoso} here");
                var gone = LeftoverMatcherTests.App("Other App", null, spelling);
                var result = LeftoverMatcherTests.Match(gone, None, None, fx);
                Check.False(result.Any(c => c.Rule == LeftoverMatcher.InstallLocationRule),
                    $"{spelling}: an install-location candidate: {string.Join("; ", result.Select(c => c.Path + " [" + c.Rule + "]"))}");
                LeftoverMatcherTests.AssertNotOffered(result, contoso, "the install location " + spelling);
            }

            var full = LeftoverMatcherTests.Match(LeftoverMatcherTests.App("Other App", null, contoso), None, None, fx);
            LeftoverMatcherTests.AssertOffered(full, "control: the full path", contoso);
            Check.True(full.Single().Rule == LeftoverMatcher.InstallLocationRule, "control: by the install-location rule");
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }
}
