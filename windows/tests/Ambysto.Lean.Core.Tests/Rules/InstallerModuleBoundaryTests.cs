using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Rules;

/// <summary>ASP-30's new authority is an exact module-bound leaf, never the general roots A/B.
/// These tests call the rule directly, without metadata parsing, a move, or the Kit's earlier refusals masking it.</summary>
[TestClass]
[DoNotParallelize]
public sealed class InstallerModuleBoundaryTests
{
    private static HashSet<string> Items(string? path) => path is null
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

    private static ItemFacts Facts(string path)
    {
        var canonical = Path.GetFullPath(path);
        var serial = KitObjects.RealStamp(RealGate.VolumeRoot).Identity.VolumeSerial;
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var owners = new List<OwnerFact>();
        for (string? cursor = canonical; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            // Deliberately favorable ownership: a refusal must be the new module's boundary.
            owners.Add(new OwnerFact(cursor, me, true));
        }
        return KitObjects.Facts(canonical, serial, owners, isSystemLocation: false);
    }

    private static void Refused(ItemFacts facts, bool explicitlyListed)
    {
        var result = LeanRules.ForDelete(LeanModule.Installers, Items(explicitlyListed ? facts.CanonicalPath : null)).Evaluate(facts);
        Assert.IsFalse(result.IsAllowed, "Installers granted a general-root or malformed module target: " + facts.CanonicalPath);
        Assert.AreEqual(Text.HintOutside, result.Message, "the module must give its own definitive refusal");
    }

    [TestMethod]
    [DataRow("Documents", false)] [DataRow("Documents", true)]
    [DataRow("Downloads", false)] [DataRow("Downloads", true)]
    [DataRow("Codex", false)] [DataRow("Codex", true)]
    public void GeneralProfileRootNeverRescuesAnUnlistedOrExplicitDecoyInstaller(string location, bool explicitlyListed)
    {
        using var scratch = new RealScratch();
        var parent = location switch
        {
            "Documents" => Path.Combine(RealGate.Profile, "Documents"),
            "Downloads" => Path.Combine(RealGate.Profile, "Downloads"),
            _ => Path.Combine(RealGate.Profile, ".codex", "releases"),
        };
        var target = scratch.NewFolder(parent);
        var facts = Facts(RealGate.Canonical(target));
        Assert.IsTrue(LeanRules.ForDelete(LeanModule.Map, Items(null)).Evaluate(facts).IsAllowed,
            "control: the exact same facts really qualify under general root A");
        Refused(facts, explicitlyListed);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void GeneralOwnedVolumeRootNeverRescuesAnUnlistedOrExplicitDecoyInstaller(bool explicitlyListed)
    {
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.VolumeRoot);
        var target = RealScratch.Folder(Path.Combine(top, "old-version"));
        var facts = Facts(RealGate.Canonical(target));
        Assert.IsTrue(LeanRules.ForDelete(LeanModule.Map, Items(null)).Evaluate(facts).IsAllowed,
            "control: the exact same facts really qualify under general root B");
        Refused(facts, explicitlyListed);
    }

    [TestMethod]
    [DataRow("CapCut")]
    [DataRow("CapCut/Apps")]
    [DataRow("CapCut/User Data")]
    [DataRow("CapCut/Apps/7.3.0/payload.bin")]
    [DataRow("CapCut/Apps/7.3.0/subfolder")]
    [DataRow("CapCut/Apps/project.backup")]
    public void ExplicitListCannotGrantVendorParentsUserDataOrNestedPayloads(string relative)
    {
        // Path facts only: do not create, open, alter or remove the owner's real vendor files.
        var path = Path.Combine(RealGate.Local, relative.Replace('/', Path.DirectorySeparatorChar));
        Refused(Facts(path), explicitlyListed: true);
    }
}
