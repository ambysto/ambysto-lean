using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A1, hard review round 3 (plan "Hard review round 3", MINOR 2): a run whose history line cannot be
/// written after the log passed its step-6 check. The fake clean holds the log file exclusively (steps 6 to 9), so the append
/// at step 9 fails. A failed or timed-out run keeps its own words and adds "could not write"; "Cleared" is said only after a
/// run that did clear. Session 0.</summary>
[TestClass]
public sealed class CleanLogWriteFailureReasonTests
{
    private const string Id = "uv";

    private static CleanOutcome RunWithLogHeldDuringTheClean(string name, CommandRunResult result)
    {
        using var rig = new CatalogRig(name);
        FileStream? held = null;
        rig.Runner.Clean[Id] = () =>
        {
            held = new FileStream(rig.LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return result;
        };
        try
        {
            var outcome = rig.NewCleanRunner().Run(rig.Row(Id), CancellationToken.None);
            Assert.IsNotNull(held, "the clean ran, so the log was held when the entry was appended");
            return outcome;
        }
        finally
        {
            held?.Dispose();
        }
    }

    // MINOR 2: error exit 3 and no history line: the reason says the command failed and that it was not recorded, never "Cleared".
    [TestMethod]
    public void AFailedRunWhoseHistoryCannotBeWrittenSaysItFailedAndWasNotRecorded()
    {
        var outcome = RunWithLogHeldDuringTheClean("log3-failed", new CommandRunResult(3, string.Empty, false));
        Assert.AreEqual(CleanResult.Failed, outcome.Result);
        Assert.AreEqual(3, outcome.ExitCode);
        Assert.AreEqual(Text.CleanFailed(3) + " — " + Text.CleanNotRecorded, outcome.Reason);
        Assert.IsFalse(outcome.Reason!.Contains("Cleared", StringComparison.OrdinalIgnoreCase), outcome.Reason);
    }

    // MINOR 2: a timeout and no history line: it keeps the timeout words and adds that it was not recorded.
    [TestMethod]
    public void ATimedOutRunWhoseHistoryCannotBeWrittenSaysItTimedOutAndWasNotRecorded()
    {
        var outcome = RunWithLogHeldDuringTheClean("log3-timeout", new CommandRunResult(null, string.Empty, true));
        Assert.AreEqual(CleanResult.TimedOut, outcome.Result);
        Assert.IsNull(outcome.ExitCode);
        Assert.AreEqual(Text.CleanTimedOut + " — " + Text.CleanNotRecorded, outcome.Reason);
        Assert.IsFalse(outcome.Reason!.Contains("Cleared", StringComparison.OrdinalIgnoreCase), outcome.Reason);
    }

    // MINOR 2, control: exit 0 and no history line is the one case that says "Cleared, but ... could not write".
    [TestMethod]
    public void AClearedRunWhoseHistoryCannotBeWrittenSaysClearedButNotRecorded()
    {
        var outcome = RunWithLogHeldDuringTheClean("log3-cleared", new CommandRunResult(0, string.Empty, false));
        Assert.AreEqual(CleanResult.Cleaned, outcome.Result);
        Assert.AreEqual(0, outcome.ExitCode);
        Assert.AreEqual(Text.CleanLogWriteFailed, outcome.Reason);
    }
}

/// <summary>Hard review round 3, MINOR 1: the Kit's real gate allows a clean on the desktop. This is the only test that
/// proves <see cref="KitCommandEnvironment"/> answers "allowed" for the one context the app runs in; the session-0 test
/// proves the refusal. If a future Kit answered otherwise, every row would read "could not check" with the rest of the suite
/// green. Console category: it runs in the interactive desktop session as the standard user.</summary>
[TestClass]
public sealed class KitCommandEnvironmentOnTheDesktopTests
{
    // The mapping's premise: the gate's restore check on %LOCALAPPDATA% (which exists) answers "a destination already
    // exists", the one answer that comes after the environment's checks.
    [TestMethod]
    [TestCategory("Console")]
    public void TheRealGateAnswersRestoreDestinationExistsForLocalAppData()
    {
        var gate = new KitItemGate(LeanModule.Map, new HashSet<string>());
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.IsTrue(Directory.Exists(local));
        var result = gate.CheckRestoreDestination(local);
        Assert.AreEqual(RefusalReason.RestoreDestinationExists, result.Reason, "the gate said " + result.Reason);
    }

    // On the desktop, as the standard user, a clean is allowed: no refusal.
    [TestMethod]
    [TestCategory("Console")]
    public void TheKitEnvironmentAllowsACleanOnTheDesktopAsAStandardUser()
    {
        var environment = new KitCommandEnvironment(new KitItemGate(LeanModule.Map, new HashSet<string>()));
        Assert.IsNull(environment.Refusal());
    }
}
