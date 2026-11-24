using System.IO;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Ambysto.Lean.Core.Tests.Acceptance.Uninstall.LeftoverMatcherTests;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Interim review fix 3 (plan "Review log", groups 10–11; docs/spec/04 Windows note: "A display name that is a
/// generic folder name (Temp, Programs, Packages, Microsoft, …) is never matched"; plan "Groups 10 and 11:
/// implementation notes", Leftover matcher): an install location whose own name is generic is never offered, whatever the
/// app is called, while an app's own folder below a generic one is. Pretend profiles in temp folders
/// (<see cref="ProfileFixture"/>).</summary>
[TestClass]
public sealed class LeftoverGenericLocationTests
{
    // %LOCALAPPDATA%\Temp, %LOCALAPPDATA%\Microsoft, %APPDATA%\Microsoft (and %LOCALAPPDATA%\Packages, named in 04), in
    // any spelling, as the gone app's install location: not offered.
    [TestMethod]
    [DataRow("local", "Temp")]
    [DataRow("local", "temp\\")]
    [DataRow("local", "Microsoft")]
    [DataRow("local", "MICROSOFT")]
    [DataRow("roaming", "Microsoft")]
    [DataRow("local", "Packages")]
    public void AGenericInstallLocationIsNeverOffered(string root, string name)
    {
        using var fx = new ProfileFixture("leftover-generic-location");
        var folder = root == "local" ? fx.LocalAppData : fx.RoamingAppData;
        var location = ProfileFixture.MakeFolder(Path.Combine(folder, name.TrimEnd('\\')));
        ProfileFixture.MakeFile(Path.Combine(location, "something.dat"));
        var gone = App("Contoso Widget", "Contoso", Path.Combine(folder, name));

        var result = Match(gone, [], [], fx);

        AssertNotOffered(result, location, "generic install location " + name);
        AssertOffered(result, "nothing else matches " + name);
    }

    // The app's own folder below a generic one (%LOCALAPPDATA%\Microsoft\SomeApp) is offered as its install location.
    [TestMethod]
    [DataRow("local")]
    [DataRow("roaming")]
    public void AnAppsOwnFolderBelowAGenericOneIsOffered(string root)
    {
        using var fx = new ProfileFixture("leftover-generic-below");
        var folder = root == "local" ? fx.LocalAppData : fx.RoamingAppData;
        var location = ProfileFixture.MakeFolder(Path.Combine(folder, "Microsoft", "SomeApp"));
        ProfileFixture.MakeFile(Path.Combine(location, "app.dat"));
        var gone = App("Contoso Widget", "Contoso", location);

        AssertOffered(Match(gone, [], [], fx), "install location " + location, location);
    }
}
