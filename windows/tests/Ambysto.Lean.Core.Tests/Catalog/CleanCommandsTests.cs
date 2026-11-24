using System.Linq;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "Commands"): the fixed table of clean commands, row by row, the arguments built for a found
/// program, and <see cref="CleanCommands.ParseLocation"/>. Session 0; written by the test writer before the code, against a
/// stub. The table is written out in <see cref="PlanTable"/> from the plan, not read from the code under test.</summary>
[TestClass]
public sealed class CleanCommandsTests
{
    private const string NodePath = @"C:\Program Files\nodejs\node.exe";
    private const string NpmScript = @"C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js";

    private static CleanCommand Cmd(string id) => CleanCommands.All.Single(c => c.EntryId == id);

    [TestMethod]
    public void TheTableHoldsTheFiveCommandsInCatalogOrder()
    {
        Check.Sequence(CleanCommands.All.Select(c => c.EntryId), "npm", "pip", "uv", "nuget-http", "nuget-plugins");
    }

    // Every command belongs to a Command entry of the catalog, and every Command entry has its command.
    [TestMethod]
    public void TheCommandsAreTheCatalogsCommandEntries()
    {
        var commandIds = AppCatalog.Entries.Where(e => e.Kind == CatalogKind.Command).Select(e => e.Id);
        Check.Sequence(CleanCommands.All.Select(c => c.EntryId), [.. commandIds]);
    }

    [TestMethod]
    public void EachCommandRunsItsProgram()
    {
        foreach (var spec in PlanTable.All)
        {
            Assert.AreEqual(spec.Program, Cmd(spec.Id).Program, spec.Id);
        }
    }

    [TestMethod]
    public void EachCommandHasItsBusyImages()
    {
        foreach (var spec in PlanTable.All)
        {
            Check.Sequence(Cmd(spec.Id).BusyImages, spec.Busy);
        }
    }

    // pip, uv and the two dotnet rows carry their whole fixed argument lists in the table.
    [TestMethod]
    public void TheFixedCleanArgumentsAreExactlyThePlansTable()
    {
        foreach (var spec in PlanTable.All.Where(s => s.Id != "npm"))
        {
            Check.Sequence(Cmd(spec.Id).CleanArguments, spec.Clean);
        }
    }

    [TestMethod]
    public void TheFixedLocationArgumentsAreExactlyThePlansTable()
    {
        foreach (var spec in PlanTable.All.Where(s => s.Id != "npm"))
        {
            Check.Sequence(Cmd(spec.Id).LocationArguments, spec.Location);
        }
    }

    // npm's table row holds the part after the script path (the script path depends on where node.exe was found).
    [TestMethod]
    public void NpmsTableArgumentsEndWithTheCleanAndTheLocationWords()
    {
        var clean = Cmd("npm").CleanArguments;
        var location = Cmd("npm").LocationArguments;
        Check.Sequence(clean.Skip(clean.Count - 3), "cache", "clean", "--force");
        Check.Sequence(location.Skip(location.Count - 3), "config", "get", "cache");
    }

    // The clean command of an npm row starts with npm-cli.js under the folder of the node.exe that was found, then the words.
    [TestMethod]
    public void NpmCleanArgumentsStartWithTheScriptUnderNodesFolder()
    {
        Check.Sequence(CleanCommands.CleanArgumentsFor(Cmd("npm"), NodePath), NpmScript, "cache", "clean", "--force");
    }

    [TestMethod]
    public void NpmLocationArgumentsStartWithTheScriptUnderNodesFolder()
    {
        Check.Sequence(CleanCommands.LocationArgumentsFor(Cmd("npm"), NodePath), NpmScript, "config", "get", "cache");
    }

    // The script path follows the program that was found, whatever its folder or drive.
    [TestMethod]
    public void NpmScriptPathFollowsTheProgramsFolder()
    {
        var args = CleanCommands.CleanArgumentsFor(Cmd("npm"), @"D:\tools\node 22\node.exe");
        Assert.AreEqual(@"D:\tools\node 22\node_modules\npm\bin\npm-cli.js", args[0]);
    }

    // The other four ignore the program's path: the fixed list, item by item.
    [TestMethod]
    public void TheOtherCommandsReturnTheirFixedListsForAnyProgramPath()
    {
        foreach (var spec in PlanTable.All.Where(s => s.Id != "npm"))
        {
            var program = @"C:\Some Folder\" + spec.Program;
            Check.Sequence(CleanCommands.CleanArgumentsFor(Cmd(spec.Id), program), spec.Clean);
            Check.Sequence(CleanCommands.LocationArgumentsFor(Cmd(spec.Id), program), spec.Location);
        }
    }

    // ParseLocation, accepted: the trimmed single line.
    [TestMethod]
    public void ParseLocationAcceptsOnePathLineWithWhitespaceAndLineEnds()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            var cmd = Cmd(id);
            Assert.AreEqual(@"C:\Users\me\AppData\Local\cache", CleanCommands.ParseLocation(cmd, @"C:\Users\me\AppData\Local\cache"), id);
            Assert.AreEqual(@"C:\Users\me\AppData\Local\cache", CleanCommands.ParseLocation(cmd, "C:\\Users\\me\\AppData\\Local\\cache\r\n"), id);
            Assert.AreEqual(@"C:\Users\me\AppData\Local\cache", CleanCommands.ParseLocation(cmd, "  \tC:\\Users\\me\\AppData\\Local\\cache  \r\n"), id);
            Assert.AreEqual(@"C:\Users\me\AppData\Local\cache", CleanCommands.ParseLocation(cmd, "C:\\Users\\me\\AppData\\Local\\cache\n"), id);
            Assert.AreEqual(@"C:\Users\me\AppData\Local\cache", CleanCommands.ParseLocation(cmd, "\r\nC:\\Users\\me\\AppData\\Local\\cache\r\n\r\n"), id);
        }
    }

    [TestMethod]
    public void ParseLocationKeepsSpacesInsideThePath()
    {
        Assert.AreEqual(@"C:\Users\Jo Doe\AppData\Local\pip\Cache", CleanCommands.ParseLocation(Cmd("pip"), "C:\\Users\\Jo Doe\\AppData\\Local\\pip\\Cache\r\n"));
    }

    // dotnet prints "http-cache: <path>"; the prefix must be this entry's.
    [TestMethod]
    public void ParseLocationAcceptsTheHttpCachePrefixForNugetHttpOnly()
    {
        Assert.AreEqual(@"C:\x\v3-cache", CleanCommands.ParseLocation(Cmd("nuget-http"), @"http-cache: C:\x\v3-cache"));
        Assert.AreEqual(@"C:\x\v3-cache", CleanCommands.ParseLocation(Cmd("nuget-http"), "http-cache: C:\\x\\v3-cache\r\n"));
        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-plugins"), @"http-cache: C:\x\v3-cache"));
    }

    [TestMethod]
    public void ParseLocationAcceptsThePluginsCachePrefixForNugetPluginsOnly()
    {
        Assert.AreEqual(@"C:\x\plugins-cache", CleanCommands.ParseLocation(Cmd("nuget-plugins"), @"plugins-cache: C:\x\plugins-cache"));
        Assert.AreEqual(@"C:\x\plugins-cache", CleanCommands.ParseLocation(Cmd("nuget-plugins"), "plugins-cache: C:\\x\\plugins-cache\r\n"));
        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-http"), @"plugins-cache: C:\x\plugins-cache"));
    }

    [TestMethod]
    public void ParseLocationRejectsADotnetLineWithAnotherOrNoPrefix()
    {
        foreach (var id in new[] { "nuget-http", "nuget-plugins" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"C:\x\cache"), id + " bare path");
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"global-packages: C:\x\cache"), id + " other prefix");
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"temp: C:\x\cache"), id + " other prefix");
        }
    }

    // The other three print the folder only: a "name: path" line is not a path.
    [TestMethod]
    public void ParseLocationRejectsAPrefixedLineForTheOtherTools()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"http-cache: C:\x\cache"), id);
        }
    }

    [TestMethod]
    public void ParseLocationRejectsEmptyOutput()
    {
        foreach (var cmd in CleanCommands.All)
        {
            Assert.IsNull(CleanCommands.ParseLocation(cmd, string.Empty), cmd.EntryId);
            Assert.IsNull(CleanCommands.ParseLocation(cmd, "  \r\n \r\n"), cmd.EntryId);
        }
    }

    [TestMethod]
    public void ParseLocationRejectsSeveralNonEmptyLines()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), "C:\\x\\cache\r\nC:\\y\\other\r\n"), id);
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), "warning: something\r\nC:\\x\\cache\r\n"), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-http"), "http-cache: C:\\x\\a\r\nhttp-cache: C:\\x\\b\r\n"));
        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-plugins"), "info: x\r\nplugins-cache: C:\\x\\a\r\n"));
    }

    [TestMethod]
    public void ParseLocationRejectsARelativePath()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"cache\npm"), id);
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @".\cache"), id);
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"..\cache"), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-http"), @"http-cache: v3-cache"));
        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-plugins"), @"plugins-cache: .\plugins"));
    }

    [TestMethod]
    public void ParseLocationRejectsAUncPath()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"\\server\share\cache"), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-http"), @"http-cache: \\server\share\v3-cache"));
    }

    [TestMethod]
    public void ParseLocationRejectsAnExtendedLengthPrefix()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"\\?\C:\x\cache"), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-plugins"), @"plugins-cache: \\?\C:\x\plugins"));
    }

    [TestMethod]
    public void ParseLocationRejectsADriveRoot()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"C:\"), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-http"), @"http-cache: D:\"));
    }

    [TestMethod]
    public void ParseLocationRejectsADotDotComponent()
    {
        foreach (var id in new[] { "npm", "pip", "uv" })
        {
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"C:\x\..\y"), id);
            Assert.IsNull(CleanCommands.ParseLocation(Cmd(id), @"C:\x\cache\.."), id);
        }

        Assert.IsNull(CleanCommands.ParseLocation(Cmd("nuget-plugins"), @"plugins-cache: C:\x\..\plugins"));
    }
}
