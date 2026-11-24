using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A1, hard review round 1 (plan "Hard review round 1", items 1, 4, 5, 7 and 8): the clean runner and
/// the Command rows over fakes. Each test says which item it guards. Session 0.</summary>
[TestClass]
public sealed class CatalogHardFixTests
{
    private const string Id = "uv";
    private const string Reason = "Not cleared: test environment says no";

    // Item 1 (environment refusal), clean runner: step 0 refuses with the environment's reason before anything at all is run,
    // the location query included, and writes no log line. Without the step the row (Ready, all in order) would be cleaned.
    [TestMethod]
    public void ARefusingEnvironmentRefusesTheCleanWithItsReasonAndRunsNothing()
    {
        using var rig = new CatalogRig("env-clean");
        rig.Env.Reason = Reason;
        var outcome = rig.NewCleanRunner().Run(rig.Row(Id), CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(Reason, outcome.Reason);
        Assert.AreEqual(0, rig.Runner.Calls.Count, "no program was started, not even the location query");
        Assert.AreEqual(0, rig.LogLines().Length, "a refusal writes no log line");
        Assert.AreEqual(0, rig.Log.Read().Count);
    }

    // Item 1, the administrator and desktop reasons reach the outcome unchanged (whatever the environment says is what the user reads).
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheEnvironmentsReasonIsTheOutcomesReason(bool administrator)
    {
        using var rig = new CatalogRig("env-reason");
        var reason = administrator ? Text.CleanNotAsAdministrator : Text.CleanNotOutsideDesktop;
        rig.Env.Reason = reason;
        var outcome = rig.NewCleanRunner().Run(rig.Row(Id), CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(reason, outcome.Reason);
        Assert.AreEqual(0, rig.Runner.Calls.Count);
    }

    // Item 1, rows: a refusing environment gives every row NotAllowedHere and starts nothing (no query, no finder call).
    [TestMethod]
    public async Task ARefusingEnvironmentGivesEveryRowNotAllowedHereAndStartsNothing()
    {
        using var rig = new CatalogRig("env-rows");
        rig.Env.Reason = Reason;
        var rows = await rig.ReadRows();
        Assert.AreEqual(PlanTable.All.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.AreEqual(CommandRowState.NotAllowedHere, row.State, row.Entry.Id);
            Assert.IsNull(row.ProgramPath, row.Entry.Id);
            Assert.IsNull(row.Location, row.Entry.Id);
        }

        Assert.AreEqual(0, rig.Runner.Calls.Count, "no location query was run");
        Assert.AreEqual(0, rig.Finder.Asked.Count, "no program was looked for");
    }

    // Item 1, control: an allowing environment is not NotAllowedHere (the refusal above is the environment's, not the rig's).
    [TestMethod]
    public async Task AnAllowingEnvironmentReadsTheRowsAsBefore()
    {
        using var rig = new CatalogRig("env-allow");
        var rows = await rig.ReadRows();
        Assert.IsTrue(rows.All(r => r.State == CommandRowState.Ready), string.Join(",", rows.Select(r => r.State)));
        Assert.IsTrue(rig.Runner.LocateCalls.Any());
    }

    // Item 4 (a program Windows does not start), rows: NotStarted on the location query is LocationUnknown, no throw.
    [TestMethod]
    public async Task ALocationQueryThatWindowsDidNotStartIsLocationUnknown()
    {
        using var rig = new CatalogRig("notstarted-rows");
        rig.Runner.Locate[Id] = () => new CommandRunResult(null, string.Empty, false) { NotStarted = true };
        var rows = await rig.ReadRows();
        var row = rows.Single(r => r.Entry.Id == Id);
        Assert.AreEqual(CommandRowState.LocationUnknown, row.State);
        Assert.IsNull(row.Location);
        Assert.AreEqual(CommandRowState.Ready, rows.Single(r => r.Entry.Id == "pip").State, "the other rows are not affected");
    }

    // Item 4, clean runner, the location query at step 4 did not start: the folder cannot be confirmed, so Refused, nothing cleaned.
    [TestMethod]
    public void ALocationQueryThatWindowsDidNotStartRefusesTheClean()
    {
        using var rig = new CatalogRig("notstarted-locate");
        var row = rig.Row(Id);
        rig.Runner.Locate[Id] = () => new CommandRunResult(null, string.Empty, false) { NotStarted = true };
        var outcome = rig.NewCleanRunner().Run(row, CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command was started");
        Assert.AreEqual(0, rig.LogLines().Length);
    }

    // Item 4, clean runner: the clean command itself was not started by Windows: Refused with Text.CleanCouldNotStart, and
    // no log line (nothing ran, so nothing is recorded).
    [TestMethod]
    public void ACleanThatWindowsDidNotStartIsRefusedWithNoLogLine()
    {
        using var rig = new CatalogRig("notstarted-clean");
        rig.Runner.Clean[Id] = () => new CommandRunResult(null, string.Empty, false) { NotStarted = true };
        var outcome = rig.NewCleanRunner().Run(rig.Row(Id), CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(Text.CleanCouldNotStart, outcome.Reason);
        Assert.IsNull(outcome.ExitCode);
        Assert.AreEqual(1, rig.Runner.CleanCalls.Count(), "the clean was attempted");
        Assert.AreEqual(0, rig.LogLines().Length, "nothing ran, nothing is logged");
        Assert.AreEqual(0, rig.Log.Read().Count);
    }

    // Item 5 (busy check again before the run): the tool starts while the location query runs (step 4); step 6b refuses.
    // Without 6b the clean would run. The check before step 4 passed (nothing was running then).
    [TestMethod]
    public void AToolThatStartsAfterTheLocationQueryRefusesTheCleanAndLogsNothing()
    {
        using var rig = new CatalogRig("busy-after-locate");
        var row = rig.Row(Id);
        var original = rig.Runner.Locate[Id];
        rig.Runner.Locate[Id] = () =>
        {
            var result = original();
            rig.Images.Add(@"C:\Users\me\.local\bin\uv.exe");
            return result;
        };
        var outcome = rig.NewCleanRunner().Run(row, CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(Text.CleanToolRunning(row.Entry.AppName), outcome.Reason);
        Assert.AreEqual(1, rig.Runner.LocateCalls.Count(), "it was the location query that let the tool start");
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "the clean never ran");
        Assert.AreEqual(0, rig.LogLines().Length);
    }

    // Item 8 (NuGet's busy text): a busy dotnet.exe refuses a NuGet row with the .NET text, both before and after the query.
    [TestMethod]
    [DataRow("nuget-http", false)]
    [DataRow("nuget-plugins", false)]
    [DataRow("nuget-http", true)]
    [DataRow("nuget-plugins", true)]
    public void ABusyDotnetRefusesANugetRowWithTheDotnetText(string id, bool startsAfterTheQuery)
    {
        using var rig = new CatalogRig("busy-dotnet");
        var row = rig.Row(id);
        if (startsAfterTheQuery)
        {
            var original = rig.Runner.Locate[id];
            rig.Runner.Locate[id] = () =>
            {
                var result = original();
                rig.Images.Add(@"C:\Program Files\dotnet\dotnet.exe");
                return result;
            };
        }
        else
        {
            rig.Images.Add(@"C:\Program Files\dotnet\dotnet.exe");
        }

        var outcome = rig.NewCleanRunner().Run(row, CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(Text.CleanDotnetRunning, outcome.Reason);
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count());
    }

    // Item 8, control: the other tools keep the generic text with their own name.
    [TestMethod]
    public void ABusyNpmNodeRefusesWithTheGenericTextNamingNpm()
    {
        using var rig = new CatalogRig("busy-npm");
        var row = rig.Row("npm");
        rig.Images.Add(@"C:\Program Files\nodejs\node.exe");
        var outcome = rig.NewCleanRunner().Run(row, CancellationToken.None);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(Text.CleanToolRunning("npm"), outcome.Reason);
        Assert.AreNotEqual(Text.CleanDotnetRunning, outcome.Reason);
    }

    // Item 7 (ParseLocation gaps): not one absolute local path in the plain form: forward slashes, an alternate data stream
    // and a dot segment are all refused, for every command (the prefix of the dotnet ones is added).
    [TestMethod]
    [DataRow("C:/x/y")]
    [DataRow(@"C:\x:stream")]
    [DataRow(@"C:\x\.\y")]
    public void ParseLocationRejectsPathsThatAreNotInPlainForm(string path)
    {
        foreach (var command in CleanCommands.All)
        {
            var line = command.EntryId switch
            {
                "nuget-http" => "http-cache: " + path,
                "nuget-plugins" => "plugins-cache: " + path,
                _ => path,
            };
            Assert.IsNull(CleanCommands.ParseLocation(command, line + "\r\n"), command.EntryId + ": " + line);
        }
    }

    // Item 7, control: the plain form beside them is accepted, so the three above are refused for their own reason.
    [TestMethod]
    public void ParseLocationStillAcceptsAPlainPath()
    {
        foreach (var command in CleanCommands.All.Where(c => !c.EntryId.StartsWith("nuget", StringComparison.Ordinal)))
        {
            Assert.AreEqual(@"C:\x\y", CleanCommands.ParseLocation(command, "C:\\x\\y\r\n"), command.EntryId);
        }
    }

    // Item 7 (a folder named like the program on PATH): a FOLDER called uv.exe is not the program; the file in a later entry is.
    [TestMethod]
    public void AFolderNamedLikeTheProgramIsSkipped()
    {
        using var temp = new TempFolder("finder-folder");
        temp.Folder("a", "uv.exe");
        var file = temp.File(@"b\uv.exe", 0);
        var found = new PathProgramFinder(temp.Sub("a") + ";" + temp.Sub("b")).Find("uv.exe");
        Assert.IsTrue(string.Equals(file, found, StringComparison.OrdinalIgnoreCase), $"expected {file}, got {found ?? "(null)"}");
    }

    // Item 7: a folder named like the program and nothing else is not found.
    [TestMethod]
    public void AFolderNamedLikeTheProgramAloneIsNotFound()
    {
        using var temp = new TempFolder("finder-folder-only");
        temp.Folder("a", "uv.exe");
        Assert.IsNull(new PathProgramFinder(temp.Sub("a")).Find("uv.exe"));
    }

    // Item 7 (quoted PATH entries): the quotes are not part of the folder.
    [TestMethod]
    public void AQuotedPathEntryIsFound()
    {
        using var temp = new TempFolder("finder-quoted");
        var file = temp.File(@"my tools\bin\uv.exe", 0);
        var path = "\"" + temp.Sub("my tools", "bin") + "\"";
        var found = new PathProgramFinder(path).Find("uv.exe");
        Assert.IsTrue(string.Equals(file, found, StringComparison.OrdinalIgnoreCase), $"expected {file}, got {found ?? "(null)"}");
        var found2 = new PathProgramFinder(temp.Sub("nothing") + ";" + path + ";").Find("uv.exe");
        Assert.IsTrue(string.Equals(file, found2, StringComparison.OrdinalIgnoreCase), $"expected {file}, got {found2 ?? "(null)"}");
    }

    // Item 7 (step 8's unmeasurable folder): the clean replaced the cache folder by a file; its size after cannot be taken (null),
    // yet the run is logged exactly once, with a null BytesAfter.
    [TestMethod]
    public void AFolderReplacedByAFileAfterTheCleanHasNoSizeAfterAndIsStillLoggedOnce()
    {
        using var rig = new CatalogRig("step8-file");
        rig.Runner.Clean[Id] = () =>
        {
            Directory.Delete(rig.Folders[Id], recursive: true);
            File.WriteAllText(rig.Folders[Id], "not a folder any more");
            return new CommandRunResult(0, string.Empty, false);
        };
        var outcome = rig.NewCleanRunner().Run(rig.Row(Id), CancellationToken.None);
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result, outcome.Reason);
        Assert.IsNull(outcome.BytesAfter);
        Assert.AreEqual(1, rig.LogLines().Length);
        Assert.IsNull(rig.Log.Read().Single().BytesAfter);
    }
}

/// <summary>Item 1 with the Kit's real gate: <see cref="KitCommandEnvironment"/> in the context this test runs in. The test
/// has no Console category, so it runs in the session-0 core suite, where the environment is not interactive and the refusal
/// must be <see cref="Text.CleanNotOutsideDesktop"/>. A test cannot choose where it runs, so it also holds in the other
/// contexts: in an interactive session (a developer's console run) the refusal is none, or the administrator one when elevated.</summary>
[TestClass]
public sealed class KitCommandEnvironmentTests
{
    [TestMethod]
    public void TheKitEnvironmentRefusesOutsideTheDesktopSession()
    {
        var environment = new KitCommandEnvironment(new KitItemGate(LeanModule.Map, new HashSet<string>()));
        var refusal = environment.Refusal();
        var interactive = Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0;
        if (interactive)
        {
            Assert.IsTrue(refusal is null || refusal == Text.CleanNotAsAdministrator, "interactive session: " + (refusal ?? "(null)"));
        }
        else
        {
            Assert.IsNotNull(refusal, "session 0 or not interactive: a command must be refused");
            Assert.AreEqual(Text.CleanNotOutsideDesktop, refusal);
        }
    }

    [TestMethod]
    public void TheKitEnvironmentNeedsAGate()
    {
        Assert.Throws<ArgumentNullException>(() => new KitCommandEnvironment(null!));
    }
}

/// <summary>Items 4 and 6 with real programs (session 0). Serial: item 6 changes a process environment variable for the whole
/// process. Round 1 item 2 (a timed-out program keeps its pipes) is now proved with python in <see cref="CatalogRound2Tests"/>:
/// ping does not die of a closed pipe, so it proved nothing.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CommandRunnerHardFixTests
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    // Item 4: a file Windows cannot start (a text file named x.exe) is NotStarted, never an exception.
    [TestMethod]
    public void ANonProgramNamedExeIsNotStartedAndDoesNotThrow()
    {
        using var temp = new TempFolder("runner-notexe");
        var program = temp.Sub("x.exe");
        File.WriteAllText(program, "this is not a program");
        var result = new ProcessCommandRunner().Run(program, ["a"], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.IsTrue(result.NotStarted);
        Assert.IsFalse(result.TimedOut);
        Assert.IsNull(result.ExitCode);
    }

    // Item 4, control: a real program is not NotStarted.
    [TestMethod]
    public void ARealProgramIsNotMarkedNotStarted()
    {
        var result = new ProcessCommandRunner().Run(Path.Combine(System32, "PING.EXE"), ["-n", "1", "127.0.0.1"], TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.IsFalse(result.NotStarted);
    }

    // Item 6 (UTF-8 output): dotnet writes the plugins-cache folder, whose name holds non-ASCII letters, and Lean reads it back
    // the same. Real finder, real runner; the row's location must be that folder. The environment variable is the process's
    // and is restored.
    [TestMethod]
    public async Task ThePluginsCacheRowReadsAFolderNameWithNonAsciiLettersBack()
    {
        var program = new PathProgramFinder().Find("dotnet.exe");
        if (program is null)
        {
            Assert.Inconclusive("dotnet.exe is not on this machine's PATH");
        }

        const string Variable = "NUGET_PLUGINS_CACHE_PATH";
        using var temp = new TempFolder("Tuấn-ñ-日本");
        var folder = temp.Folder("plugins");
        var saved = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, folder);
            var rows = await CommandRows.ReadAsync(new FakeCommandEnvironment(), new PathProgramFinder(), new ProcessCommandRunner(), () => [], CancellationToken.None);
            var row = rows.Single(r => r.Entry.Id == "nuget-plugins");
            Assert.IsNotNull(row.Location, "state " + row.State);
            Assert.AreEqual(folder.TrimEnd('\\'), row.Location!.TrimEnd('\\'), ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, saved);
        }
    }
}
