using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A1, hard review round 2, item 1: <see cref="KitCommandEnvironment"/> over the fake gate. The only
/// answer that lets a command start is "a destination already exists" (the check after the environment's); every other answer
/// refuses, and so does an exception. Before the fix the mapping ended in <c>_ =&gt; null</c> and failed open. Session 0.</summary>
[TestClass]
public sealed class KitCommandEnvironmentMappingTests
{
    private static string? Ask(Func<string, GateResult> answer)
    {
        var gate = new FakeItemGate { OnRestore = answer };
        return new KitCommandEnvironment(gate).Refusal();
    }

    // Item 1: the one pass.
    [TestMethod]
    public void RestoreDestinationExistsIsThePass()
    {
        Assert.IsNull(Ask(_ => KitObjects.Refuse(RefusalReason.RestoreDestinationExists)));
    }

    // Item 1: the environment's own two refusals keep their own texts.
    [TestMethod]
    public void ElevatedRefusesAsAdministrator()
    {
        Assert.AreEqual(Text.CleanNotAsAdministrator, Ask(_ => KitObjects.Refuse(RefusalReason.Elevated)));
    }

    [TestMethod]
    public void NotInteractiveRefusesOutsideTheDesktop()
    {
        Assert.AreEqual(Text.CleanNotOutsideDesktop, Ask(_ => KitObjects.Refuse(RefusalReason.NotInteractive)));
    }

    // Item 1 (the fail-open): every other reason refuses with the "could not check" text. With `_ => null` each of these passed.
    [TestMethod]
    public void EveryOtherReasonRefusesAsUnknown()
    {
        var others = Enum.GetValues<RefusalReason>()
            .Where(r => r is not (RefusalReason.RestoreDestinationExists or RefusalReason.Elevated or RefusalReason.NotInteractive))
            .ToList();
        Assert.IsTrue(others.Count > 3, "the enum has more reasons than the three the mapping names");
        foreach (var reason in others)
        {
            Assert.AreEqual(Text.CleanEnvironmentUnknown, Ask(_ => KitObjects.Refuse(reason)), "reason " + reason);
        }
    }

    // Item 1: an approved answer is not the environment's pass either (it means the destination was free: the check the
    // environment would have refused in did not run). The fake produces one through KitObjects.ApproveNow.
    [TestMethod]
    public void AnApprovedAnswerRefusesAsUnknown()
    {
        using var temp = new TempFolder("env-approved");
        var path = temp.File("x.txt", 1);
        Assert.AreEqual(Text.CleanEnvironmentUnknown, Ask(_ => KitObjects.ApproveNow(path)));
    }

    // Item 1: an exception from the gate is a refusal, never a pass and never thrown.
    [TestMethod]
    public void AnExceptionFromTheGateRefusesAsUnknownAndDoesNotThrow()
    {
        foreach (Exception thrown in new Exception[] { new InvalidOperationException("x"), new UnauthorizedAccessException("y"), new IOException("z"), new NotSupportedException("w") })
        {
            string? refusal = null;
            try
            {
                refusal = Ask(_ => throw thrown);
            }
            catch (Exception e)
            {
                Assert.Fail("the environment threw " + e.GetType().Name + " for " + thrown.GetType().Name);
            }

            Assert.AreEqual(Text.CleanEnvironmentUnknown, refusal, thrown.GetType().Name);
        }
    }

    // Item 1: the path asked about exists (it is the one that makes "destination exists" the pass).
    [TestMethod]
    public void TheGateIsAskedAboutTheExistingLocalAppData()
    {
        string? asked = null;
        Ask(p =>
        {
            asked = p;
            return KitObjects.Refuse(RefusalReason.RestoreDestinationExists);
        });
        Assert.IsNotNull(asked);
        Assert.IsTrue(Directory.Exists(asked), asked);
    }
}

/// <summary>Round 2, items 2, 3 and 4 (and round 1 item 2 with a program that does end on a closed pipe): real programs that
/// the tests count and end by process name. Serial: no other test may start a process of these names meanwhile (nothing else
/// in the Catalog tests starts waitfor or python; the ping tests are serial too). Session 0.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CatalogRound2Tests
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static HashSet<int> Ids(string name)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            ids.Add(process.Id);
            process.Dispose();
        }

        return ids;
    }

    private static List<Process> NewOnes(string name, HashSet<int> before) =>
        Process.GetProcessesByName(name).Where(p => !before.Contains(p.Id)).ToList();

    private static void End(List<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    // Item 2 (the stream wait is bounded): cmd starts waitfor in the background and exits at once; waitfor holds the output
    // pipe for 30 s. .NET's WaitForExit(TimeSpan) does not bound the wait for the streams, so before the fix Run returned only
    // when waitfor ended (about 30 s). Now it returns at the 5 s limit, not timed out (the program itself exited), exit code 0.
    [TestMethod]
    public void AHelperHoldingThePipeDoesNotHoldRunBeyondTheLimit()
    {
        var signal = "LeanTest" + Guid.NewGuid().ToString("N");
        var before = Ids("waitfor");
        List<Process> left = [];
        try
        {
            var clock = Stopwatch.StartNew();
            var result = new ProcessCommandRunner().Run(
                Path.Combine(System32, "cmd.exe"),
                ["/c", "start", "/b", Path.Combine(System32, "waitfor.exe"), "/t", "30", signal],
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            clock.Stop();
            left = NewOnes("waitfor", before);
            Assert.AreEqual(1, left.Count, "the waitfor grandchild is running (the test's premise)");
            Check.AtMost(8000, clock.ElapsedMilliseconds, "Run returned near its 5 s limit, not after the grandchild's 30 s");
            Check.AtLeast(4000, clock.ElapsedMilliseconds, "and it did wait for the streams until the limit (the grandchild held the pipe)");
            Assert.IsFalse(result.TimedOut, "the program itself exited");
            Assert.AreEqual(0, result.ExitCode);
            Assert.IsFalse(result.NotStarted);
        }
        finally
        {
            if (left.Count == 0)
            {
                left = NewOnes("waitfor", before);
            }

            End(left);
        }
    }

    // Round 1 item 2, with a program that exits on a closed pipe (hard review round 2, item 4): python prints a line every
    // 0.2 s and dies of BrokenPipeError when its stdout is closed. Run returns at its 1 s limit (TimedOut); three seconds later
    // it must still be running, which holds only while Lean keeps reading its pipe. The test ends it itself.
    [TestMethod]
    public void ATimedOutProgramThatDiesOnAClosedPipeIsStillRunningAFewSecondsLater()
    {
        var python = new PathProgramFinder().Find("python.exe");
        if (python is null)
        {
            Assert.Inconclusive("no python.exe on this machine's PATH (the WindowsApps alias is skipped): this test needs a program that exits on a closed pipe");
        }

        var before = Ids("python");
        List<Process> left = [];
        try
        {
            var result = new ProcessCommandRunner().Run(
                python,
                ["-u", "-c", "import time\nfor i in range(150):\n print(i, flush=True)\n time.sleep(0.2)"],
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            left = NewOnes("python", before);
            Assert.IsTrue(result.TimedOut);
            Assert.IsNull(result.ExitCode);
            Assert.IsTrue(left.Count >= 1, "the program started is there");
            Thread.Sleep(3000);
            foreach (var process in left)
            {
                Assert.IsFalse(process.HasExited, "three seconds on, python still runs: Lean did not close its pipe (it would have died of BrokenPipeError)");
            }
        }
        finally
        {
            if (left.Count == 0)
            {
                left = NewOnes("python", before);
            }

            End(left);
        }
    }
}
