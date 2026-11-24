using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "Starting a process"): <see cref="ProcessCommandRunner"/> with real programs, in session 0 (no
/// Console category). Read-only programs only: <c>dotnet.exe --version</c>, Windows PowerShell running a script this test
/// writes (an echo of its arguments, its working folder, its output volume and its exit code, since no program of the machine
/// echoes its arguments reliably) and <c>ping.exe</c> as a program that waits. Nothing here runs a clean command. Written by the
/// test writer before the code, against a stub.</summary>
[TestClass]
public sealed class ProcessCommandRunnerTests
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static string PowerShell => Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(60);

    // dotnet.exe found by reading the PATH here, not through the finder under test.
    private static string DotnetOnPath()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.IsPathRooted(entry) && File.Exists(Path.Combine(entry, "dotnet.exe")))
            {
                return Path.Combine(entry, "dotnet.exe");
            }
        }

        Assert.Inconclusive("dotnet.exe is not on this machine's PATH");
        return string.Empty;
    }

    private static string Script(TempFolder temp, string body)
    {
        var path = temp.Sub("probe.ps1");
        File.WriteAllText(path, body);
        return path;
    }

    private static string[] RunScript(string script, params string[] arguments) =>
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments];

    [TestMethod]
    public void DotnetVersionRunsAndPrintsItsVersion()
    {
        var result = new ProcessCommandRunner().Run(DotnetOnPath(), ["--version"], Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(result.TimedOut);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.IsTrue(char.IsDigit(result.StandardOutput.Trim()[0]), "a version number: " + result.StandardOutput);
    }

    // Each argument reaches the program as one argument, never re-split or re-joined: one with a space, one with quotes, one
    // that looks like a switch, one with a trailing backslash.
    [TestMethod]
    public void ArgumentsReachTheProgramUnjoined()
    {
        using var temp = new TempFolder("runner-args");
        var script = Script(temp, "$args.Count\r\nforeach ($a in $args) { '[' + $a + ']' }\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script, "a b", "say \"hi\"", "--flag=1", @"C:\x y\", "plain"), Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput);
        var lines = result.StandardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Check.Sequence(lines, "5", "[a b]", "[say \"hi\"]", "[--flag=1]", @"[C:\x y\]", "[plain]");
    }

    // An argument that holds a shell metacharacter is data: no shell is involved.
    [TestMethod]
    public void NoShellIsInvolved()
    {
        using var temp = new TempFolder("runner-shell");
        var marker = temp.Sub("marker.txt");
        var script = Script(temp, "$args[0]\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script, "x & echo pwned > \"" + marker + "\""), Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput);
        Assert.IsFalse(File.Exists(marker), "an ampersand in an argument must not start anything");
        Check.Contains(result.StandardOutput, "x & echo pwned");
    }

    [TestMethod]
    public void TheExitCodeOfTheProgramIsReturned()
    {
        using var temp = new TempFolder("runner-exit");
        var script = Script(temp, "'out text'\r\nexit 7\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script), Plenty, CancellationToken.None);
        Assert.AreEqual(7, result.ExitCode);
        Assert.IsFalse(result.TimedOut);
        Check.Contains(result.StandardOutput, "out text");
    }

    // The working folder is the program's own folder.
    [TestMethod]
    public void TheWorkingFolderIsTheProgramsFolder()
    {
        using var temp = new TempFolder("runner-cwd");
        var script = Script(temp, "(Get-Location).Path\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script), Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput);
        Assert.AreEqual(Path.GetDirectoryName(PowerShell), result.StandardOutput.Trim(), ignoreCase: true);
    }

    // Output is kept up to 64 KiB, and the program is not held up by a full pipe: it runs to its end with exit 0.
    [TestMethod]
    public void OutputIsKeptUpTo64KiBAndTheProgramStillFinishes()
    {
        using var temp = new TempFolder("runner-bigout");
        var script = Script(temp, "[Console]::Out.Write(('x' * 400000))\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script), Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(result.TimedOut);
        Check.AtLeast(1000, result.StandardOutput.Length, "some output is kept");
        Check.AtMost(64 * 1024, result.StandardOutput.Length, "at most 64 KiB is kept");
    }

    // Standard error is read too: a program that fills its error pipe is not left blocked.
    [TestMethod]
    public void ALargeStandardErrorDoesNotBlockTheProgram()
    {
        using var temp = new TempFolder("runner-bigerr");
        var script = Script(temp, "[Console]::Error.Write(('y' * 400000))\r\n'done'\r\n");
        var result = new ProcessCommandRunner().Run(PowerShell, RunScript(script), Plenty, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(result.TimedOut);
        Check.Contains(result.StandardOutput, "done");
    }

    // The program path must be absolute and end in .exe; anything else throws before anything is started.
    [TestMethod]
    public void ARelativeProgramPathThrowsBeforeStarting()
    {
        Assert.Throws<ArgumentException>(() => new ProcessCommandRunner().Run("dotnet.exe", ["--version"], Plenty, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new ProcessCommandRunner().Run(@".\dotnet.exe", ["--version"], Plenty, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new ProcessCommandRunner().Run(@"tools\dotnet.exe", ["--version"], Plenty, CancellationToken.None));
    }

    // A script that would leave a mark if it ran: it is not started, because it is not an .exe.
    [TestMethod]
    [DataRow(".bat")]
    [DataRow(".cmd")]
    [DataRow(".ps1")]
    [DataRow("")]
    public void ANonExeProgramPathThrowsBeforeStarting(string extension)
    {
        using var temp = new TempFolder("runner-nonexe");
        var marker = temp.Sub("marker.txt");
        var program = temp.Sub("tool" + extension);
        File.WriteAllText(program, "@echo off\r\necho ran > \"" + marker + "\"\r\n");
        Assert.Throws<ArgumentException>(() => new ProcessCommandRunner().Run(program, [], Plenty, CancellationToken.None));
        Assert.IsFalse(File.Exists(marker), "the file must not have been started");
    }

    // The timeout only stops waiting: TimedOut, no exit code, and the program is still running afterwards (production code never
    // ends a process). The test ends it itself, by the id of the one new ping process.
    // Hard review round 2, item 3: serial. It counts new PING processes and kills them, so a ping another test started in the
    // same moment (the quick ones below included) would be counted and killed too; a serial test runs alone.
    [TestMethod]
    [DoNotParallelize]
    public void ATimeoutStopsWaitingAndLeavesTheProgramRunning()
    {
        var before = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
        var started = Stopwatch.StartNew();
        var result = new ProcessCommandRunner().Run(Path.Combine(System32, "PING.EXE"), ["-n", "37", "127.0.0.1"], TimeSpan.FromSeconds(1), CancellationToken.None);
        started.Stop();
        var left = Process.GetProcessesByName("PING").Where(p => !before.Contains(p.Id)).ToList();
        try
        {
            Assert.IsTrue(result.TimedOut);
            Assert.IsNull(result.ExitCode);
            Check.AtMost(15000, started.ElapsedMilliseconds, "returned soon after the 1 s limit, not after the ping's 37 s");
            Check.AtLeast(900, started.ElapsedMilliseconds, "waited for the limit");
            Assert.AreEqual(1, left.Count, "the program started is still there");
            Assert.IsFalse(left[0].HasExited, "and still running");
        }
        finally
        {
            foreach (var process in left)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    // A program that finishes inside the limit is not a timeout.
    [TestMethod]
    public void AProgramThatFinishesInTimeIsNotATimeout()
    {
        var result = new ProcessCommandRunner().Run(Path.Combine(System32, "PING.EXE"), ["-n", "1", "127.0.0.1"], Plenty, CancellationToken.None);
        Assert.IsFalse(result.TimedOut);
        Assert.IsNotNull(result.ExitCode);
    }
}
