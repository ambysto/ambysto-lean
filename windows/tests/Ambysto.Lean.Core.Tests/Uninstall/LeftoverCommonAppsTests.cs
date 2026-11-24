using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Intent criterion 6: "for ten common Windows apps with decoys (a shared vendor folder, a name prefix, a generic
/// DisplayName, an app re-registered under a new key, two entries with one name), every offered leftover is the app's own
/// and none belongs to another app". One pretend profile holds the per-user folders these apps leave, as they name them
/// (install locations and data folders), plus the decoys: the shared <c>Programs\Python</c> and <c>%APPDATA%\Contoso</c>
/// vendor folders, <c>Notes</c> next to <c>Contoso Notes</c>, the generic <c>Microsoft</c>, <c>Packages</c> and
/// <c>Temp</c> folders, data folders whose names are not the display name (<c>Code</c>, <c>Zoom</c>). Each app in turn is
/// the gone one, the others still installed; the expected set is written out per app from docs/spec/04's exact rules (an
/// app whose data folder is not named after it leaves that folder behind: the rule is exact, never a guess).</summary>
[TestClass]
public sealed class LeftoverCommonAppsTests
{
    private sealed record Common(string Name, string? Publisher, string? Location, string[] Own);

    // R = %APPDATA%, L = %LOCALAPPDATA%, P = %LOCALAPPDATA%\Programs.
    private static readonly Common[] Apps =
    [
        new("Microsoft Visual Studio Code (User)", "Microsoft Corporation", @"P\Microsoft VS Code", [@"P\Microsoft VS Code"]),
        new("Slack", "Slack Technologies Inc.", @"L\slack", [@"L\slack", @"R\Slack"]),
        new("Discord", "Discord Inc.", @"L\Discord", [@"L\Discord", @"R\Discord"]),
        new("Spotify", "Spotify AB", @"R\Spotify", [@"R\Spotify", @"L\Spotify"]),
        new("Zoom Workplace", "Zoom Video Communications, Inc.", @"R\Zoom\bin", [@"R\Zoom\bin"]),
        new("Notion", "Notion Labs, Inc.", @"P\Notion", [@"P\Notion", @"R\Notion"]),
        new("GitHub Desktop", "GitHub, Inc.", @"L\GitHubDesktop", [@"L\GitHubDesktop", @"R\GitHub Desktop"]),
        new("Python 3.12.7 (64-bit)", "Python Software Foundation", @"P\Python\Python312", [@"P\Python\Python312"]),
        new("Python 3.13.1 (64-bit)", "Python Software Foundation", @"P\Python\Python313", [@"P\Python\Python313"]),
        new("Postman", "Postman", @"L\Postman", [@"L\Postman", @"R\Postman"]),
        new("Figma", "Figma, Inc.", @"L\Figma", [@"L\Figma", @"R\Figma"]),
        new("Contoso Notes", "Contoso", @"P\Contoso Notes", [@"P\Contoso Notes", @"R\Contoso\Contoso Notes"]),
        new("Contoso Draw", "Contoso", @"P\Contoso Draw", [@"P\Contoso Draw", @"R\Contoso\Contoso Draw"]),
        new("Notes", "Notes Team", null, [@"R\Notes"]),
    ];

    // Folders no app may be offered: vendor folders, data folders not named after their app, generic folders.
    private static readonly string[] Decoys =
    [
        @"P\Python", @"R\Contoso", @"R\Contoso\shared", @"R\Code", @"R\Code\User", @"R\Zoom", @"R\Zoom\data", @"R\Microsoft", @"L\Microsoft",
        @"L\Packages", @"L\Temp", @"R\Microsoft\Windows",
    ];

    private static string Resolve(ProfileFixture fx, string token)
    {
        var parts = token.Split('\\');
        var root = parts[0] switch
        {
            "R" => fx.RoamingAppData,
            "L" => fx.LocalAppData,
            "P" => Path.Combine(fx.LocalAppData, "Programs"),
            _ => throw new ArgumentException(token),
        };
        return Path.Combine([root, .. parts[1..]]);
    }

    private static (ProfileFixture Fx, Dictionary<string, InstalledApp> Installed) Build(string name)
    {
        var fx = new ProfileFixture(name);
        foreach (var token in Apps.SelectMany(a => a.Own).Concat(Decoys))
        {
            ProfileFixture.MakeFile(Path.Combine(Resolve(fx, token), "data.bin"));
        }

        var installed = Apps.ToDictionary(a => a.Name, a => LeftoverMatcherTests.App(a.Name, a.Publisher, a.Location is null ? null : Resolve(fx, a.Location) + @"\"));
        return (fx, installed);
    }

    [TestMethod]
    public void EachCommonAppGoneAloneIsOfferedExactlyItsOwnFolders()
    {
        var (fx, installed) = Build("leftover-common");
        using (fx)
        {
            foreach (var app in Apps)
            {
                var others = installed.Values.Where(a => a.DisplayName != app.Name).ToList();
                var result = LeftoverMatcherTests.Match(installed[app.Name], others, [], fx);
                var offered = LeftoverMatcherTests.Offered(result);
                foreach (var other in Apps.Where(a => a.Name != app.Name))
                {
                    foreach (var theirs in other.Own.Select(t => Resolve(fx, t)))
                    {
                        Assert.IsFalse(offered.Contains(theirs, StringComparer.OrdinalIgnoreCase), $"{app.Name} was offered {theirs}, which is {other.Name}'s");
                    }
                }

                foreach (var decoy in Decoys.Select(t => Resolve(fx, t)))
                {
                    Assert.IsFalse(offered.Contains(decoy, StringComparer.OrdinalIgnoreCase), $"{app.Name} was offered the decoy {decoy}");
                }

                LeftoverMatcherTests.AssertOffered(result, app.Name, [.. app.Own.Select(t => Resolve(fx, t))]);
            }
        }
    }

    // Criterion 6 "a shared vendor folder": both Pythons removed together; each keeps only its own version folder, and
    // Programs\Python, which both use, belongs to neither. The same for the two Contoso apps and %APPDATA%\Contoso.
    [TestMethod]
    public void AppsRemovedTogetherNeverShareTheirVendorFolder()
    {
        var (fx, installed) = Build("leftover-common-together");
        using (fx)
        {
            foreach (var (first, second) in new[] { ("Python 3.12.7 (64-bit)", "Python 3.13.1 (64-bit)"), ("Contoso Notes", "Contoso Draw") })
            {
                var others = installed.Values.Where(a => a.DisplayName != first && a.DisplayName != second).ToList();
                foreach (var (gone, alsoGone) in new[] { (first, second), (second, first) })
                {
                    var result = LeftoverMatcherTests.Match(installed[gone], others, [installed[alsoGone]], fx);
                    LeftoverMatcherTests.AssertOffered(result, gone + " with " + alsoGone + " just removed", [.. Apps.Single(a => a.Name == gone).Own.Select(t => Resolve(fx, t))]);
                    LeftoverMatcherTests.AssertNotOffered(result, Resolve(fx, @"P\Python"), "shared vendor folder");
                    LeftoverMatcherTests.AssertNotOffered(result, Resolve(fx, @"R\Contoso"), "shared vendor folder");
                }
            }
        }
    }

    // Criterion 6 "an app re-registered under a new key" and "two entries with one name", among the common apps: while a
    // second Slack entry with the same location and name is still installed, nothing of Slack is offered.
    [TestMethod]
    public void ACommonAppStillRegisteredUnderAnotherKeyOffersNothing()
    {
        var (fx, installed) = Build("leftover-common-again");
        using (fx)
        {
            var slack = installed["Slack"];
            var again = slack with { Version = "4.41", Entry = slack.Entry! with { KeyName = "slack-new", DisplayVersion = "4.41" } };
            var others = installed.Values.Where(a => a.DisplayName != "Slack").Append(again).ToList();
            var result = LeftoverMatcherTests.Match(slack, others, [], fx);
            LeftoverMatcherTests.AssertOffered(result, "Slack re-registered");
        }
    }
}
