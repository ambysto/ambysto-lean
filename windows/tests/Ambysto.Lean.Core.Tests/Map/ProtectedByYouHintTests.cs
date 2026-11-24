using System.IO;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Map;

/// <summary>docs/spec/02 and 07 Windows notes (owner decision 2026-10-18), plan Design row <c>Map.DisplayOnly.HintFor</c>:
/// a path inside, or holding, a folder of <see cref="PathContext.UserProtectedFolders"/> is display-only with
/// <see cref="Text.HintProtectedByYou"/>; every other path gets the hint it had without the list.</summary>
[TestClass]
public sealed class ProtectedByYouHintTests
{
    private static PathContext With(ProfileFixture fx, params string[] folders) => fx.Context with { UserProtectedFolders = folders };

    // A file or folder inside a protected folder, at any depth, and the folder itself.
    [TestMethod]
    public void APathInsideAProtectedFolderIsProtectedByYou()
    {
        using var fx = new ProfileFixture("hint-protected-inside");
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Keep"));
        var file = ProfileFixture.MakeFile(Path.Combine(keep, "report.pdf"));
        var deep = ProfileFixture.MakeFile(Path.Combine(keep, "a", "b", "c.zip"));
        var folder = ProfileFixture.MakeFolder(Path.Combine(keep, "a"));
        Assert.IsNull(DisplayOnly.HintFor(file, fx.Context), "control: movable without the list");

        var context = With(fx, keep);
        foreach (var path in new[] { keep, file, deep, folder })
        {
            Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(path, context), path);
        }
    }

    // A folder holding a protected folder (its parent, and higher up within root (a)).
    [TestMethod]
    public void APathHoldingAProtectedFolderIsProtectedByYou()
    {
        using var fx = new ProfileFixture("hint-protected-holding");
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Projects", "Client", "Keep"));
        var parent = Path.GetDirectoryName(keep)!;
        var grandparent = Path.GetDirectoryName(parent)!;
        Assert.IsNull(DisplayOnly.HintFor(parent, fx.Context), "control: movable without the list");

        var context = With(fx, keep);
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(parent, context), parent);
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(grandparent, context), grandparent);
    }

    // A path next to a protected folder (a sibling file, a sibling folder whose name starts the same) keeps its hint (none).
    [TestMethod]
    public void APathNextToAProtectedFolderIsUnchanged()
    {
        using var fx = new ProfileFixture("hint-protected-next");
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Projects", "Keep"));
        var sibling = ProfileFixture.MakeFile(fx.InProfile("Documents", "Projects", "next.pdf"));
        var prefix = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Projects", "Keep2"));
        var prefixFile = ProfileFixture.MakeFile(fx.InProfile("Documents", "Projects", "Keep.zip"));

        var context = With(fx, keep);
        foreach (var path in new[] { sibling, prefix, prefixFile })
        {
            Assert.IsNull(DisplayOnly.HintFor(path, context), path);
        }
    }

    // Every other path gets exactly the hint it had without the list (none, outside, system-wide, synced, Docker/WSL).
    [TestMethod]
    public void OtherPathsGetTheHintTheyHadWithoutTheList()
    {
        using var fx = new ProfileFixture("hint-protected-others");
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Keep"));
        string[] paths =
        [
            ProfileFixture.MakeFile(fx.InProfile("Documents", "report.pdf")),
            ProfileFixture.MakeFile(fx.InProfile("Videos", "Holiday", "clip.mp4")),
            ProfileFixture.MakeFile(fx.InProfile("loose.iso")),
            ProfileFixture.MakeFile(Path.Combine(fx.RoamingAppData, "Contoso", "settings.dat")),
            ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Temp", "old.tmp")),
            ProfileFixture.MakeFile(Path.Combine(fx.LocalAppData, "Docker", "disk.vhdx")),
            ProfileFixture.MakeFile(Path.Combine(fx.WindowsFolder, "Temp", "x.tmp")),
            ProfileFixture.MakeFile(Path.Combine(fx.OtherProfile, "Documents", "theirs.pdf")),
            ProfileFixture.MakeFile(Path.Combine(fx.OneDrive, "Synced", "a.docx")),
            ProfileFixture.MakeFile(fx.OnVolume("Data", "Archive", "old.iso")),
            ProfileFixture.MakeFile(Path.Combine(fx.OtherVolume, "elsewhere.bin")),
        ];

        var context = With(fx, keep);
        foreach (var path in paths)
        {
            Assert.AreEqual(DisplayOnly.HintFor(path, fx.Context), DisplayOnly.HintFor(path, context), path);
        }
    }

    // Several folders: each one protects its own paths only.
    [TestMethod]
    public void EachFolderOfTheListCounts()
    {
        using var fx = new ProfileFixture("hint-protected-several");
        var first = ProfileFixture.MakeFolder(fx.InProfile("Documents", "First"));
        var second = ProfileFixture.MakeFolder(fx.OnVolume("Data", "Second"));
        var inFirst = ProfileFixture.MakeFile(Path.Combine(first, "a.bin"));
        var inSecond = ProfileFixture.MakeFile(Path.Combine(second, "b.bin"));
        var neither = ProfileFixture.MakeFile(fx.InProfile("Documents", "c.bin"));

        var context = With(fx, first, second);
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(inFirst, context));
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(inSecond, context));
        Assert.IsNull(DisplayOnly.HintFor(neither, context));
    }

    // The list is compared on canonical paths: an entry or a path spelled with another case or a trailing separator.
    [TestMethod]
    public void SpellingDoesNotSlipPast()
    {
        using var fx = new ProfileFixture("hint-protected-spelling");
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Keep"));
        var file = ProfileFixture.MakeFile(Path.Combine(keep, "a.bin"));

        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(file, With(fx, keep.ToUpperInvariant() + "\\")));
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(file.ToUpperInvariant(), With(fx, keep)));
        Assert.AreEqual(Text.HintProtectedByYou, DisplayOnly.HintFor(Path.Combine(keep, "sub", "..", "a.bin"), With(fx, keep)));
    }
}
