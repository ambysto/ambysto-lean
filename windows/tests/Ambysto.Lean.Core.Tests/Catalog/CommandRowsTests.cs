using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "Command rows"; intent decision 6): <see cref="CommandRows.ReadAsync"/> with a fake finder, a
/// fake runner and a fake process list. The locations are real temp folders (they are measured with Lean's Scanner). Reading
/// runs the location queries only, never a clean. Session 0; written by the test writer before the code, against a stub.</summary>
[TestClass]
public sealed class CommandRowsTests
{
    private static CommandRow RowOf(System.Collections.Generic.IReadOnlyList<CommandRow> rows, string id) => rows.Single(r => r.Entry.Id == id);

    [TestMethod]
    public async Task EveryRowIsReadyWhenTheProgramIsFoundTheFolderHoldsFilesAndNoToolRuns()
    {
        using var rig = new CatalogRig("rows-ready");
        var rows = await rig.ReadRows();
        Assert.AreEqual(5, rows.Count);
        foreach (var row in rows)
        {
            var id = row.Entry.Id;
            Assert.AreEqual(CommandRowState.Ready, row.State, id);
            Assert.AreEqual(rig.ProgramPath(id), row.ProgramPath, id);
            Assert.AreEqual(rig.Folders[id], row.Location, id);
            Check.AtLeast(20000, row.Bytes, id + " bytes");
        }

        Assert.AreEqual(0, rig.Runner.OtherCalls.Count(), "every call was the plan's location query");
    }

    [TestMethod]
    public async Task RowsComeInTableOrderEachWithItsEntryAndCommand()
    {
        using var rig = new CatalogRig("rows-order");
        var rows = await rig.ReadRows();
        Check.Sequence(rows.Select(r => r.Entry.Id), "npm", "pip", "uv", "nuget-http", "nuget-plugins");
        foreach (var row in rows)
        {
            Assert.AreEqual(row.Entry.Id, row.Command.EntryId);
            Assert.AreEqual(CatalogKind.Command, row.Entry.Kind);
        }
    }

    // Reading is read-only: the location queries run (30 s limit each, with the found program's path), the clean never.
    [TestMethod]
    public async Task ReadingRunsTheLocationQueryOfEachRowAndNeverAClean()
    {
        using var rig = new CatalogRig("rows-readonly");
        await rig.ReadRows();
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command during a read");
        Check.Set(rig.Runner.LocateCalls.Select(c => c.Id!), [.. PlanTable.Ids]);
        foreach (var call in rig.Runner.LocateCalls)
        {
            Assert.AreEqual(rig.ProgramPath(call.Id!), call.Program, call.Id);
            Assert.AreEqual(TimeSpan.FromSeconds(30), call.Timeout, call.Id);
        }
    }

    // A temp folder with a file: Bytes is the measured size, more than 0; it is what the Scanner measures, not the tool's number.
    [TestMethod]
    public async Task BytesAreTheMeasuredSizeOfTheFolder()
    {
        using var rig = new CatalogRig("rows-bytes");
        File.WriteAllBytes(Path.Combine(rig.Folders["uv"], "big.bin"), new byte[300000]);
        var rows = await rig.ReadRows();
        Check.AtLeast(320000, RowOf(rows, "uv").Bytes, "uv bytes");
        Assert.IsTrue(RowOf(rows, "pip").Bytes < RowOf(rows, "uv").Bytes, "pip's folder holds less than uv's");
    }

    [TestMethod]
    public async Task ARowWithoutTheProgramIsProgramMissingAndRunsNothing()
    {
        using var rig = new CatalogRig("rows-missing");
        rig.Finder.Map["node.exe"] = null;
        var rows = await rig.ReadRows();
        var npm = RowOf(rows, "npm");
        Assert.AreEqual(CommandRowState.ProgramMissing, npm.State);
        Assert.IsNull(npm.ProgramPath);
        Assert.IsNull(npm.Location);
        Assert.AreEqual(0, npm.Bytes);
        Assert.IsFalse(rig.Runner.Calls.Any(c => c.Id == "npm"), "no query for a program that is not there");
        foreach (var row in rows.Where(r => r.Entry.Id != "npm"))
        {
            Assert.AreEqual(CommandRowState.Ready, row.State, row.Entry.Id);
        }
    }

    // One missing dotnet.exe gives both NuGet rows ProgramMissing.
    [TestMethod]
    public async Task MissingDotnetMakesBothNugetRowsProgramMissing()
    {
        using var rig = new CatalogRig("rows-missing-dotnet");
        rig.Finder.Map["dotnet.exe"] = null;
        var rows = await rig.ReadRows();
        Assert.AreEqual(CommandRowState.ProgramMissing, RowOf(rows, "nuget-http").State);
        Assert.AreEqual(CommandRowState.ProgramMissing, RowOf(rows, "nuget-plugins").State);
        Assert.AreEqual(CommandRowState.Ready, RowOf(rows, "uv").State);
    }

    // The location query's failures and the answers that are not a folder all give LocationUnknown; the other rows are unaffected.
    [TestMethod]
    [DataRow("timeout")]
    [DataRow("nonzero")]
    [DataRow("garbage")]
    [DataRow("several-lines")]
    [DataRow("empty-output")]
    [DataRow("relative")]
    [DataRow("missing-folder")]
    [DataRow("a-file")]
    [DataRow("a-link")]
    public async Task ALocationThatCannotBeTrustedIsLocationUnknown(string failure)
    {
        using var rig = new CatalogRig("rows-locunknown");
        var valid = PlanTable.LocationOutput("npm", rig.Folders["npm"]);
        switch (failure)
        {
            case "timeout":
                // The limit passed, whatever the process printed: the answer is not used.
                rig.Runner.Locate["npm"] = () => new CommandRunResult(null, valid, true);
                break;
            case "nonzero":
                // A valid-looking answer with a failing exit code is not used.
                rig.Runner.Locate["npm"] = () => new CommandRunResult(1, valid, false);
                break;
            case "garbage":
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, "npm ERR! something went wrong", false);
                break;
            case "several-lines":
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, "warning\r\n" + valid, false);
                break;
            case "empty-output":
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, string.Empty, false);
                break;
            case "relative":
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, "npm-cache\r\n", false);
                break;
            case "missing-folder":
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, rig.Temp.Sub("no-such-cache") + "\r\n", false);
                break;
            case "a-file":
                var file = rig.Temp.File("cache-is-a-file.txt", 100);
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, file + "\r\n", false);
                break;
            case "a-link":
                // The named folder is a junction to a real folder: never followed, so the row is not Ready.
                var link = rig.Temp.Sub("link-to-cache");
                Native.CreateJunction(link, rig.Folders["npm"]);
                rig.Runner.Locate["npm"] = () => new CommandRunResult(0, link + "\r\n", false);
                break;
            default:
                Assert.Fail("unknown case " + failure);
                break;
        }

        var rows = await rig.ReadRows();
        Assert.AreEqual(CommandRowState.LocationUnknown, RowOf(rows, "npm").State);
        foreach (var row in rows.Where(r => r.Entry.Id != "npm"))
        {
            Assert.AreEqual(CommandRowState.Ready, row.State, row.Entry.Id);
        }
    }

    [TestMethod]
    public async Task AnEmptyFolderIsEmpty()
    {
        using var rig = new CatalogRig("rows-empty");
        rig.EmptyFolder("pip");
        var rows = await rig.ReadRows();
        var pip = RowOf(rows, "pip");
        Assert.AreEqual(CommandRowState.Empty, pip.State);
        Assert.AreEqual(rig.Folders["pip"], pip.Location);
        Assert.AreEqual(0, pip.Bytes);
    }

    // Decision 6: any running process whose image file name is in the command's BusyImages makes the row ToolRunning, compared
    // without regard to case on the file name of the full image path; the other rows are not touched. The folder is still
    // measured (the plan measures first, then looks at the processes).
    [TestMethod]
    public async Task EachBusyImageMakesItsOwnRowsToolRunningIgnoringCase()
    {
        using var rig = new CatalogRig("rows-busy");
        var images = PlanTable.All.SelectMany(s => s.Busy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var image in images)
        {
            rig.Images.Clear();
            rig.Images.Add(@"C:\Some Folder\Deep\" + image.ToUpperInvariant());
            var rows = await rig.ReadRows();
            foreach (var spec in PlanTable.All)
            {
                var busy = spec.Busy.Contains(image, StringComparer.OrdinalIgnoreCase);
                var row = RowOf(rows, spec.Id);
                Assert.AreEqual(busy ? CommandRowState.ToolRunning : CommandRowState.Ready, row.State, $"{spec.Id} with {image} running");
                Assert.AreEqual(rig.Folders[spec.Id], row.Location, $"{spec.Id} with {image} running");
                Check.AtLeast(20000, row.Bytes, $"{spec.Id} with {image} running");
            }
        }
    }

    [TestMethod]
    public async Task AnImageIsMatchedOnItsWholeFileNameNotAPartOfThePath()
    {
        using var rig = new CatalogRig("rows-busy-exact");
        rig.Images.AddRange(
        [
            @"C:\x\mynode.exe",
            @"C:\x\node.exe.old",
            @"C:\node.exe\other.exe",
            @"C:\x\node.txt",
            @"C:\x\uv.exe.bak",
            @"C:\tools\python3.exe",
            @"C:\x\notdotnet.exe",
        ]);
        var rows = await rig.ReadRows();
        foreach (var row in rows)
        {
            Assert.AreEqual(CommandRowState.Ready, row.State, row.Entry.Id);
        }
    }

    [TestMethod]
    public async Task OtherRunningProgramsDoNotMatter()
    {
        using var rig = new CatalogRig("rows-busy-other");
        rig.Images.AddRange([@"C:\Windows\explorer.exe", @"C:\Windows\System32\svchost.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe"]);
        var rows = await rig.ReadRows();
        Assert.IsTrue(rows.All(r => r.State == CommandRowState.Ready));
    }
}
