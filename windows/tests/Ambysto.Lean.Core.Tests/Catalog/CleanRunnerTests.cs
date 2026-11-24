using System;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "The clean runner"): <see cref="CleanRunner.Run"/> over a fake finder, a fake runner, a fake
/// process list and a command log in a temp folder; the cache folders are real. Each refusal step 1-6 is tested alone: every
/// other condition is in order, so deleting that one check from the code makes its test fail (the clean command would run, and
/// the test asserts that it did not). A refusal starts no clean command and writes no log line. Session 0; written by the test
/// writer before the code, against a stub.</summary>
[TestClass]
public sealed class CleanRunnerTests
{
    private const string Id = "uv";

    private static CleanOutcome Run(CatalogRig rig, CommandRow row, CommandLog? log = null) =>
        rig.NewCleanRunner(log).Run(row, CancellationToken.None);

    private static void AssertRefusedWithNothingStarted(CatalogRig rig, CleanOutcome outcome, string id = Id)
    {
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(id, outcome.EntryId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Reason), "a refusal says why");
        Assert.IsNull(outcome.ExitCode);
        Assert.IsNull(outcome.BytesAfter);
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command was started");
        Assert.AreEqual(0, rig.Runner.OtherCalls.Count(), "nothing else was started either");
        Assert.AreEqual(0, rig.LogLines().Length, "a refusal writes no log line");
        Assert.AreEqual(0, rig.Log.Read().Count);
    }

    // Step 1 (guards "the row was Ready when approved"): a row in any other state is refused, though the program, the folder
    // and the processes are all in order now. A row that was not Ready runs nothing at all, not even the location query.
    [TestMethod]
    [DataRow(CommandRowState.ToolRunning)]
    [DataRow(CommandRowState.ProgramMissing)]
    [DataRow(CommandRowState.LocationUnknown)]
    [DataRow(CommandRowState.Empty)]
    public void ARowThatWasNotReadyIsRefused(CommandRowState state)
    {
        using var rig = new CatalogRig("run-step1");
        var outcome = Run(rig, rig.Row(Id, state));
        AssertRefusedWithNothingStarted(rig, outcome);
        Assert.AreEqual(0, rig.Runner.Calls.Count, "nothing was run, not even a query");
    }

    // Step 2 (guards "none of its BusyImages runs now"): the row was Ready, the tool has started since.
    [TestMethod]
    [DataRow(@"C:\Users\me\.local\bin\uv.exe")]
    [DataRow(@"C:\Users\me\.local\bin\UVX.EXE")]
    public void AToolThatIsRunningNowRefusesTheClean(string image)
    {
        using var rig = new CatalogRig("run-step2");
        var row = rig.Row(Id);
        rig.Images.Add(image);
        AssertRefusedWithNothingStarted(rig, Run(rig, row));
    }

    // Step 2 for every command: the first busy image of each table row refuses its row.
    [TestMethod]
    public void EveryCommandsBusyImageRefusesItsClean()
    {
        foreach (var spec in PlanTable.All)
        {
            foreach (var image in spec.Busy)
            {
                using var rig = new CatalogRig("run-step2-all");
                var row = rig.Row(spec.Id);
                rig.Images.Add(@"C:\Elsewhere\" + image);
                AssertRefusedWithNothingStarted(rig, Run(rig, row), spec.Id);
            }
        }
    }

    // Step 3a (guards "the program found now is the approved path"): another program of the same name is first on the PATH now.
    [TestMethod]
    public void AProgramFoundAtAnotherPathNowIsRefused()
    {
        using var rig = new CatalogRig("run-step3a");
        var row = rig.Row(Id);
        rig.Finder.Map["uv.exe"] = rig.Temp.File(@"planted\uv.exe", 0);
        AssertRefusedWithNothingStarted(rig, Run(rig, row));
    }

    // Step 3a: the program is gone from the PATH now.
    [TestMethod]
    public void AProgramNoLongerFoundIsRefused()
    {
        using var rig = new CatalogRig("run-step3a-gone");
        var row = rig.Row(Id);
        rig.Finder.Map["uv.exe"] = null;
        AssertRefusedWithNothingStarted(rig, Run(rig, row));
    }

    // The path is compared without regard to case (Windows paths): the same file found with other letter cases is the same program.
    [TestMethod]
    public void TheSameProgramFoundWithAnotherLetterCaseIsStillApproved()
    {
        using var rig = new CatalogRig("run-step3-case");
        var row = rig.Row(Id);
        rig.Finder.Map["uv.exe"] = row.ProgramPath!.ToUpperInvariant();
        var outcome = Run(rig, row);
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result, outcome.Reason);
    }

    // Step 3b (guards "still not a reparse point"): the approved path is now a reparse point (an app execution alias or a link
    // swapped in), found at the same path. Needs a reparse point: a symbolic link, or an alias that exists on the machine.
    [TestMethod]
    public void AProgramThatIsNowAReparsePointIsRefused()
    {
        using var rig = new CatalogRig("run-step3b");
        var decoy = ReparseFiles.Require(rig.Temp, "uv.exe");
        var row = rig.Row(Id) with { ProgramPath = decoy };
        rig.Finder.Map["uv.exe"] = decoy;
        AssertRefusedWithNothingStarted(rig, Run(rig, row));
    }

    // Step 4 (guards "the location command, run again, gives the approved location"): the tool now names another folder.
    [TestMethod]
    public void ALocationThatChangedSinceApprovalIsRefused()
    {
        using var rig = new CatalogRig("run-step4");
        var row = rig.Row(Id);
        var elsewhere = rig.Temp.Folder("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "precious.bin"), new byte[5000]);
        rig.Runner.Locate[Id] = () => new CommandRunResult(0, PlanTable.LocationOutput(Id, elsewhere), false);
        var outcome = Run(rig, row);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command was started");
        Assert.AreEqual(0, rig.LogLines().Length);
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "precious.bin")));
    }

    // Step 4 for the shared-program rows: each NuGet row asks its own location query again, not its sibling's.
    [TestMethod]
    public void TheNugetRowsEachCheckTheirOwnLocation()
    {
        using var rig = new CatalogRig("run-step4-nuget");
        var row = rig.Row("nuget-plugins");
        var elsewhere = rig.Temp.Folder("elsewhere");
        rig.Runner.Locate["nuget-plugins"] = () => new CommandRunResult(0, PlanTable.LocationOutput("nuget-plugins", elsewhere), false);
        AssertRefusedWithNothingStarted(rig, Run(rig, row), "nuget-plugins");
    }

    // Step 4: the location query fails now (or times out, or prints something that is not a folder): the folder cannot be confirmed.
    [TestMethod]
    [DataRow("nonzero")]
    [DataRow("timeout")]
    [DataRow("garbage")]
    public void ALocationQueryThatFailsNowRefusesTheClean(string how)
    {
        using var rig = new CatalogRig("run-step4-fail");
        var row = rig.Row(Id);
        var valid = PlanTable.LocationOutput(Id, rig.Folders[Id]);
        rig.Runner.Locate[Id] = how switch
        {
            "nonzero" => () => new CommandRunResult(1, valid, false),
            "timeout" => () => new CommandRunResult(null, valid, true),
            _ => () => new CommandRunResult(0, "no folder here", false),
        };
        var outcome = Run(rig, row);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command was started");
        Assert.AreEqual(0, rig.LogLines().Length);
    }

    // Step 6 (guards "the log can be written"): a log whose folder cannot be created (its parent is a file) refuses the clean
    // before anything is started; a clean that cannot be written down is not run.
    [TestMethod]
    public void ALogThatCannotBeWrittenRefusesTheCleanBeforeItRuns()
    {
        using var rig = new CatalogRig("run-step6");
        var blocker = rig.Temp.File("not-a-folder.txt", 10);
        var log = CommandLog.Open(Path.Combine(blocker, "sub", "commands.jsonl"));
        var row = rig.Row(Id);
        var outcome = Run(rig, row, log);
        Assert.AreEqual(CleanResult.Refused, outcome.Result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Reason));
        Assert.AreEqual(0, rig.Runner.CleanCalls.Count(), "no clean command was started");
        Assert.IsTrue(File.Exists(Path.Combine(rig.Folders[Id], "cache.bin")), "the cache is untouched");
    }

    // Step 5 (measure now): BytesBefore is measured at run time, not copied from the row handed over.
    [TestMethod]
    public void BytesBeforeIsMeasuredNowNotTakenFromTheRow()
    {
        using var rig = new CatalogRig("run-before");
        var row = rig.Row(Id) with { Bytes = 5 };
        File.WriteAllBytes(Path.Combine(rig.Folders[Id], "more.bin"), new byte[100000]);
        var outcome = Run(rig, row);
        Check.AtLeast(120000, outcome.BytesBefore, "BytesBefore");
    }

    // Steps 7-9, exit 0: Cleaned, with the clean command's own arguments, the five-minute limit, one log entry.
    [TestMethod]
    public void ExitCodeZeroIsCleanedWithOneLogEntry()
    {
        using var rig = new CatalogRig("run-cleaned");
        var row = rig.Row(Id);
        var before = DateTimeOffset.UtcNow;
        var outcome = Run(rig, row);
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result, outcome.Reason);
        Assert.AreEqual(Id, outcome.EntryId);
        Assert.AreEqual(0, outcome.ExitCode);
        Check.AtLeast(20000, outcome.BytesBefore, "BytesBefore");
        Assert.AreEqual(0L, outcome.BytesAfter, "the fake clean emptied the folder");

        var clean = rig.Runner.CleanCalls.Single();
        Assert.AreEqual(row.ProgramPath, clean.Program);
        Check.Sequence(clean.Arguments, PlanTable.Of(Id).Clean);
        Assert.AreEqual(TimeSpan.FromMinutes(5), clean.Timeout);

        Assert.AreEqual(1, rig.LogLines().Length);
        var entry = rig.Log.Read().Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Id));
        Assert.AreEqual(Id, entry.EntryId);
        Assert.AreEqual(row.ProgramPath, entry.Program);
        Check.Sequence(entry.Arguments, PlanTable.Of(Id).Clean);
        Assert.AreEqual(row.Location, entry.Location);
        Assert.AreEqual(outcome.BytesBefore, entry.BytesBefore);
        Assert.AreEqual(outcome.BytesAfter, entry.BytesAfter);
        Assert.AreEqual(0, entry.ExitCode);
        Assert.IsFalse(entry.TimedOut);
        Assert.AreEqual(CleanResult.Cleaned, entry.Result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.AppVersion));
        Assert.IsTrue(entry.Time >= before.AddSeconds(-5) && entry.Time <= DateTimeOffset.UtcNow.AddSeconds(5), "the entry carries the time of the run: " + entry.Time);
    }

    // Each of the five commands cleans with its own program and its own arguments (npm's with the script under node's folder).
    [TestMethod]
    public void EveryCommandRunsItsOwnArgumentsAndIsLoggedWithThem()
    {
        foreach (var spec in PlanTable.All)
        {
            using var rig = new CatalogRig("run-each");
            var row = rig.Row(spec.Id);
            var outcome = Run(rig, row);
            Assert.AreEqual(CleanResult.Cleaned, outcome.Result, spec.Id + ": " + outcome.Reason);
            var clean = rig.Runner.CleanCalls.Single();
            Assert.AreEqual(spec.Id, clean.Id);
            Assert.AreEqual(row.ProgramPath, clean.Program, spec.Id);
            Check.Sequence(clean.Arguments, PlanTable.Resolve(spec.Clean, row.ProgramPath!));
            Check.Sequence(rig.Log.Read().Single().Arguments, PlanTable.Resolve(spec.Clean, row.ProgramPath!));
            Assert.AreEqual(spec.Id, rig.Log.Read().Single().EntryId);
        }
    }

    // BytesAfter is measured after the clean: a clean that does nothing leaves the size as it was.
    [TestMethod]
    public void BytesAfterIsMeasuredAfterNotAssumedZero()
    {
        using var rig = new CatalogRig("run-after-same");
        rig.Runner.Clean[Id] = () => new CommandRunResult(0, string.Empty, false);
        var outcome = Run(rig, rig.Row(Id));
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result);
        Assert.AreEqual(outcome.BytesBefore, outcome.BytesAfter);
        Assert.AreEqual(outcome.BytesAfter, rig.Log.Read().Single().BytesAfter);
    }

    // A clean that removes some of it: the difference is what was measured.
    [TestMethod]
    public void BytesAfterIsWhatIsLeft()
    {
        using var rig = new CatalogRig("run-after-part");
        File.WriteAllBytes(Path.Combine(rig.Folders[Id], "second.bin"), new byte[200000]);
        rig.Runner.Clean[Id] = () =>
        {
            File.Delete(Path.Combine(rig.Folders[Id], "second.bin"));
            return new CommandRunResult(0, string.Empty, false);
        };
        var outcome = Run(rig, rig.Row(Id));
        Check.AtLeast(220000, outcome.BytesBefore, "BytesBefore");
        Check.AtLeast(20000, outcome.BytesAfter!.Value, "BytesAfter");
        Check.AtMost(outcome.BytesBefore - 200000, outcome.BytesAfter.Value, "BytesAfter");
    }

    // A location that the clean removed counts 0.
    [TestMethod]
    public void AFolderThatIsGoneAfterTheCleanCountsZero()
    {
        using var rig = new CatalogRig("run-after-gone");
        rig.Runner.Clean[Id] = () =>
        {
            Directory.Delete(rig.Folders[Id], recursive: true);
            return new CommandRunResult(0, string.Empty, false);
        };
        var outcome = Run(rig, rig.Row(Id));
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result, outcome.Reason);
        Assert.AreEqual(0L, outcome.BytesAfter);
        Assert.AreEqual(0L, rig.Log.Read().Single().BytesAfter);
    }

    // A nonzero exit code is Failed (not Cleaned), with the code, the sizes and one log entry.
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(-1)]
    public void ANonzeroExitCodeIsFailedWithOneLogEntry(int exitCode)
    {
        using var rig = new CatalogRig("run-failed");
        rig.Runner.Clean[Id] = () => new CommandRunResult(exitCode, "error: nope", false);
        var outcome = Run(rig, rig.Row(Id));
        Assert.AreEqual(CleanResult.Failed, outcome.Result);
        Assert.AreEqual(exitCode, outcome.ExitCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Reason), "a failure says why");
        Check.AtLeast(20000, outcome.BytesBefore, "BytesBefore");
        Assert.AreEqual(outcome.BytesBefore, outcome.BytesAfter, "the failed clean left the folder as it was");

        Assert.AreEqual(1, rig.LogLines().Length);
        var entry = rig.Log.Read().Single();
        Assert.AreEqual(CleanResult.Failed, entry.Result);
        Assert.AreEqual(exitCode, entry.ExitCode);
        Assert.IsFalse(entry.TimedOut);
        Assert.AreEqual(outcome.BytesBefore, entry.BytesBefore);
        Assert.AreEqual(outcome.BytesAfter, entry.BytesAfter);
    }

    // A timeout is TimedOut: no exit code, the program is left running, one log entry with timedOut true.
    [TestMethod]
    public void ATimeoutIsTimedOutWithNoExitCodeAndOneLogEntry()
    {
        using var rig = new CatalogRig("run-timeout");
        rig.Runner.Clean[Id] = () => new CommandRunResult(null, string.Empty, true);
        var outcome = Run(rig, rig.Row(Id));
        Assert.AreEqual(CleanResult.TimedOut, outcome.Result);
        Assert.IsNull(outcome.ExitCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Reason));
        Assert.IsNotNull(outcome.BytesAfter, "the folder is measured after a timeout too");

        Assert.AreEqual(1, rig.LogLines().Length);
        var entry = rig.Log.Read().Single();
        Assert.AreEqual(CleanResult.TimedOut, entry.Result);
        Assert.IsTrue(entry.TimedOut);
        Assert.IsNull(entry.ExitCode);
        Assert.AreEqual(outcome.BytesBefore, entry.BytesBefore);
        Assert.AreEqual(outcome.BytesAfter, entry.BytesAfter);
    }

    // Step 9: a log write that fails after the clean ran is reported in the reason; the run still ends with the clean's result
    // and does not throw. The fake clean holds the log file open exclusively, so the append after it cannot write.
    [TestMethod]
    public void ALogWriteThatFailsAfterTheCleanIsReportedInTheReason()
    {
        using var rig = new CatalogRig("run-logfail");
        FileStream? held = null;
        rig.Runner.Clean[Id] = () =>
        {
            rig.EmptyFolder(Id);
            held = new FileStream(rig.LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new CommandRunResult(0, string.Empty, false);
        };
        try
        {
            var outcome = Run(rig, rig.Row(Id));
            Assert.AreEqual(CleanResult.Cleaned, outcome.Result, "the clean did run and exit 0");
            Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Reason), "the lost log entry is reported");
        }
        finally
        {
            held?.Dispose();
        }

        Assert.AreEqual(0, rig.Log.Read().Count);
    }

    // Two runs, two entries, oldest first: the log only ever grows.
    [TestMethod]
    public void EachRunAppendsOneEntry()
    {
        using var rig = new CatalogRig("run-twice");
        Run(rig, rig.Row("uv"));
        Run(rig, rig.Row("pip"));
        Check.Sequence(rig.Log.Read().Select(e => e.EntryId), "uv", "pip");
        Assert.AreEqual(2, rig.LogLines().Length);
    }
}
