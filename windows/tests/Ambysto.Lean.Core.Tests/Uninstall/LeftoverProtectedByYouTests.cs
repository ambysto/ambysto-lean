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

/// <summary><see cref="LeftoverMatcher.Match"/> with the user's protected folders (docs/spec/02 Windows note, owner
/// decision 2026-10-18: an item inside or holding one is refused at selection, display-only, "Protected by you"; docs/spec/04
/// leftovers; docs/spec/07 display-only items say why): a candidate inside, equal to, or holding a folder of
/// <see cref="PathContext.UserProtectedFolders"/> is listed, not selectable, with the note
/// <see cref="Text.HintProtectedByYou"/>; a candidate beside it is selectable with no note. Pretend profile in a temp
/// folder (<see cref="ProfileFixture"/>).</summary>
[TestClass]
public sealed class LeftoverProtectedByYouTests
{
    private static readonly IReadOnlyList<InstalledApp> None = [];

    private static LeftoverCandidate? At(IReadOnlyList<LeftoverCandidate> result, string path) =>
        result.SingleOrDefault(c => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.Path)), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));

    private static string Describe(IReadOnlyList<LeftoverCandidate> result) =>
        "[" + string.Join("; ", result.Select(c => $"{c.Path} ({c.Rule}, {(c.Selectable ? "selectable" : "not selectable")}, {c.Note ?? "no note"})")) + "]";

    // The gone app "Contoso" has %APPDATA%\Contoso and %LOCALAPPDATA%\Contoso by the display-name rule. Rows: the protected
    // folder is the roaming candidate itself, holds it (%APPDATA%), or lies inside it (%APPDATA%\Contoso\Keep). The roaming
    // candidate is listed, not selectable, with "Protected by you"; the local one beside it stays selectable with no note.
    // Control: without the list both are selectable with no note.
    [TestMethod]
    [DataRow("candidate")]
    [DataRow("holds the candidate")]
    [DataRow("inside the candidate")]
    public void ALeftoverInsideOrHoldingAProtectedFolderIsShownNotSelectable(string where)
    {
        using var fx = new ProfileFixture("leftover-protected-" + where.Replace(' ', '-'));
        var roaming = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso"));
        var inner = ProfileFixture.MakeFolder(Path.Combine(roaming, "Keep"));
        ProfileFixture.MakeFile(Path.Combine(inner, "settings.json"));
        var local = ProfileFixture.MakeFolder(Path.Combine(fx.LocalAppData, "Contoso"));
        var gone = LeftoverMatcherTests.App("Contoso", null, null);

        var control = LeftoverMatcherTests.Match(gone, None, None, fx);
        LeftoverMatcherTests.AssertOffered(control, "control: no protected folders", roaming, local);
        Check.True(control.All(c => c.Note is null), "control: no note without protected folders: " + Describe(control));

        var protectedFolder = where switch
        {
            "candidate" => roaming,
            "holds the candidate" => fx.RoamingAppData,
            _ => inner,
        };
        var context = fx.Context with { UserProtectedFolders = [protectedFolder] };
        var result = LeftoverMatcher.Match(gone, None, None, context);
        LeftoverMatcherTests.Invariants(result, fx);

        var shown = At(result, roaming);
        Assert.IsNotNull(shown, $"the candidate ({where} protected folder {protectedFolder}) is not listed: {Describe(result)}");
        Check.False(shown.Selectable, "selectable although protected by the user: " + shown.Path);
        Assert.AreEqual(Text.HintProtectedByYou, shown.Note, "the note of a protected leftover");
        Assert.AreEqual("Protected by you", shown.Note);

        var beside = At(result, local);
        Assert.IsNotNull(beside, "the candidate beside the protected folder is not listed: " + Describe(result));
        Check.True(beside.Selectable, "the candidate beside the protected folder is not selectable: " + beside.Path);
        Assert.IsNull(beside.Note, "the candidate beside the protected folder has a note: " + beside.Note);
        LeftoverMatcherTests.AssertOffered(result, "only the candidate outside the protected folder", local);
    }

    // A protected folder next to a candidate (a sibling whose name starts the same, %APPDATA%\Contoso Backup and
    // %APPDATA%\Contoso2) does not touch it: both candidates stay selectable with no note.
    [TestMethod]
    public void AProtectedFolderBesideALeftoverChangesNothing()
    {
        using var fx = new ProfileFixture("leftover-protected-beside");
        var roaming = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso"));
        var local = ProfileFixture.MakeFolder(Path.Combine(fx.LocalAppData, "Contoso"));
        var backup = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso Backup"));
        var prefix = ProfileFixture.MakeFolder(Path.Combine(fx.RoamingAppData, "Contoso2"));
        var gone = LeftoverMatcherTests.App("Contoso", null, null);

        var context = fx.Context with { UserProtectedFolders = [backup, prefix] };
        var result = LeftoverMatcher.Match(gone, None, None, context);
        LeftoverMatcherTests.Invariants(result, fx);

        LeftoverMatcherTests.AssertOffered(result, "protected folders beside the candidates", roaming, local);
        Check.True(result.All(c => c.Note is null), "a note from a protected folder beside the candidates: " + Describe(result));
    }
}
