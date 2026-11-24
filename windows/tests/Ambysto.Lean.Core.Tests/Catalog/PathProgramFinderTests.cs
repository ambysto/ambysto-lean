using System;
using System.IO;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "Finding a program"): <see cref="PathProgramFinder"/> over a PATH value the test gives it.
/// The first entry that is absolute and local and holds the file wins; relative, empty and UNC entries and a file that is a
/// reparse point are skipped. The fixture programs are empty files (the finder reads attributes only). Session 0; written by
/// the test writer before the code, against a stub.</summary>
[TestClass]
public sealed class PathProgramFinderTests
{
    private static void AssertSamePath(string expected, string? actual) =>
        Assert.IsTrue(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), $"expected {expected}, got {actual ?? "(null)"}");

    [TestMethod]
    public void TheFirstEntryThatHoldsTheFileWins()
    {
        using var temp = new TempFolder("finder-order");
        var first = temp.File(@"a\tool.exe", 0);
        temp.File(@"b\tool.exe", 0);
        var finder = new PathProgramFinder(temp.Sub("a") + ";" + temp.Sub("b"));
        AssertSamePath(first, finder.Find("tool.exe"));
    }

    // The same two entries the other way round: the order of the PATH decides, not the folder names.
    [TestMethod]
    public void TheOrderOfThePathDecides()
    {
        using var temp = new TempFolder("finder-order2");
        temp.File(@"a\tool.exe", 0);
        var second = temp.File(@"b\tool.exe", 0);
        var finder = new PathProgramFinder(temp.Sub("b") + ";" + temp.Sub("a"));
        AssertSamePath(second, finder.Find("tool.exe"));
    }

    [TestMethod]
    public void AnEntryWithoutTheFileIsPassedOver()
    {
        using var temp = new TempFolder("finder-pass");
        temp.Folder("empty");
        temp.File(@"b\tool.exe", 0);
        temp.File(@"b\other.exe", 0);
        var finder = new PathProgramFinder(temp.Sub("empty") + ";" + temp.Sub("b"));
        AssertSamePath(temp.Sub("b", "tool.exe"), finder.Find("tool.exe"));
    }

    [TestMethod]
    public void AMissingFileGivesNull()
    {
        using var temp = new TempFolder("finder-missing");
        temp.File(@"a\other.exe", 0);
        var finder = new PathProgramFinder(temp.Sub("a") + ";" + temp.Sub("does-not-exist"));
        Assert.IsNull(finder.Find("tool.exe"));
    }

    [TestMethod]
    public void AnEmptyPathGivesNull()
    {
        Assert.IsNull(new PathProgramFinder(string.Empty).Find("tool.exe"));
    }

    // An empty entry (";;") is skipped, it does not mean the current folder.
    [TestMethod]
    public void EmptyEntriesAreSkipped()
    {
        using var temp = new TempFolder("finder-empty-entry");
        var file = temp.File(@"b\tool.exe", 0);
        var finder = new PathProgramFinder(";;" + temp.Sub("b") + ";");
        AssertSamePath(file, finder.Find("tool.exe"));
    }

    [TestMethod]
    public void AFileInTheCurrentFolderIsNotFoundThroughAnEmptyEntry()
    {
        var name = "cwd-tool-" + Guid.NewGuid().ToString("N")[..8] + ".exe";
        var file = Path.Combine(Directory.GetCurrentDirectory(), name);
        File.WriteAllBytes(file, []);
        try
        {
            Assert.IsNull(new PathProgramFinder(";").Find(name));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // A relative entry is skipped even when it resolves, from the current folder, to a folder that holds the file; the next
    // (absolute) entry wins.
    [TestMethod]
    public void ARelativeEntryIsSkippedEvenWhenItHoldsTheFile()
    {
        using var temp = new TempFolder("finder-relative");
        var absolute = temp.File(@"abs\tool.exe", 0);
        var relativeFolder = "catalog-rel-" + Guid.NewGuid().ToString("N")[..8];
        var relativeFull = Path.Combine(Directory.GetCurrentDirectory(), relativeFolder);
        Directory.CreateDirectory(relativeFull);
        File.WriteAllBytes(Path.Combine(relativeFull, "tool.exe"), []);
        try
        {
            AssertSamePath(absolute, new PathProgramFinder(relativeFolder + ";" + temp.Sub("abs")).Find("tool.exe"));
            Assert.IsNull(new PathProgramFinder(relativeFolder).Find("tool.exe"));
            Assert.IsNull(new PathProgramFinder(@".\" + relativeFolder).Find("tool.exe"));
        }
        finally
        {
            Directory.Delete(relativeFull, recursive: true);
        }
    }

    // A UNC entry is skipped. The loopback share of the test machine (\\localhost\ambynet, C:\AmbyNet) holds the decoy; the
    // test is Inconclusive where that share does not exist or cannot be written.
    [TestMethod]
    public void AUncEntryIsSkippedEvenWhenItHoldsTheFile()
    {
        using var temp = new TempFolder("finder-unc");
        var absolute = temp.File(@"abs\tool.exe", 0);
        var uncFolder = @"\\localhost\ambynet\catalog-finder-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(uncFolder);
            File.WriteAllBytes(Path.Combine(uncFolder, "tool.exe"), []);
        }
        catch (IOException e)
        {
            Assert.Inconclusive(@"\\localhost\ambynet is not available to write: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Assert.Inconclusive(@"\\localhost\ambynet is not available to write: " + e.Message);
        }

        try
        {
            AssertSamePath(absolute, new PathProgramFinder(uncFolder + ";" + temp.Sub("abs")).Find("tool.exe"));
            Assert.IsNull(new PathProgramFinder(uncFolder).Find("tool.exe"));
        }
        finally
        {
            Directory.Delete(uncFolder, recursive: true);
        }
    }

    // A file that is a reparse point (the Windows app execution alias for python.exe opens the Store) is skipped, so the next
    // real file wins and a PATH holding only the decoy finds nothing.
    [TestMethod]
    public void AReparsePointFileIsSkipped()
    {
        using var temp = new TempFolder("finder-reparse");
        var decoy = ReparseFiles.Require(temp, "tool.exe");
        var name = Path.GetFileName(decoy);
        var real = temp.File(@"real-first\" + name, 0);
        var decoyFolder = Path.GetDirectoryName(decoy)!;
        AssertSamePath(real, new PathProgramFinder(decoyFolder + ";" + temp.Sub("real-first")).Find(name));
        Assert.IsNull(new PathProgramFinder(decoyFolder).Find(name));
    }
}
