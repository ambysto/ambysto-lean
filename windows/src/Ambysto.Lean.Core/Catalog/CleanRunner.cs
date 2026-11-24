using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Ambysto.Lean.Core.Copy;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>The outcome of one clean.</summary>
public enum CleanResult
{
    /// <summary>The command ran and exited with 0.</summary>
    Cleaned,

    /// <summary>A check before the run failed; nothing was started.</summary>
    Refused,

    /// <summary>The command ran and exited with another code.</summary>
    Failed,

    /// <summary>Lean stopped waiting after the time limit; the program was left running.</summary>
    TimedOut,
}

/// <summary>What one clean did: the result, the reason (refusals and failures), the sizes measured before and after, and the exit code.</summary>
public sealed record CleanOutcome(string EntryId, CleanResult Result, string? Reason, long BytesBefore, long? BytesAfter, int? ExitCode);

/// <summary>Runs one approved Command row (plan "The clean runner"). Steps 0-6 each refuse, before anything is started but the
/// read-only location query: the environment allows it (not administrator, SYSTEM or outside the desktop session); the row was
/// ready; its tool is not running; the program found now is the approved one and not a link; the tool names the approved
/// cache folder; the folder is measured; the log can be written; the tool is still not running. Then the command (step 7; one
/// Windows did not start is refused), the measure after (step 8) and exactly one log entry (step 9). A program still running
/// at the limit is left running.</summary>
public sealed class CleanRunner
{
    /// <summary>The time the clean command is waited for.</summary>
    public static readonly TimeSpan CleanLimit = TimeSpan.FromMinutes(5);

    private static readonly string AppVersion =
        typeof(CleanRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private readonly ICommandEnvironment environment;
    private readonly IProgramFinder finder;
    private readonly ICommandRunner runner;
    private readonly Func<IReadOnlyList<string>> processImages;
    private readonly CommandLog log;

    /// <summary>A runner over its seams.</summary>
    public CleanRunner(ICommandEnvironment environment, IProgramFinder finder, ICommandRunner runner, Func<IReadOnlyList<string>> processImages, CommandLog log)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(processImages);
        ArgumentNullException.ThrowIfNull(log);
        this.environment = environment;
        this.finder = finder;
        this.runner = runner;
        this.processImages = processImages;
        this.log = log;
    }

    /// <summary>Cleans <paramref name="approved"/>.</summary>
    public CleanOutcome Run(CommandRow approved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var command = approved.Command;

        // 0. Never as administrator, as SYSTEM or outside the desktop session (hard review round 1, item 1).
        if (environment.Refusal() is { } refusal)
        {
            return Refused(approved, refusal);
        }

        // 1. Only a row that was ready when the user approved it.
        if (approved.State != CommandRowState.Ready || approved.ProgramPath is not { } program || approved.Location is not { } location)
        {
            return Refused(approved, Text.CleanNotReady);
        }

        // 2. The tool is not running now (decision 6).
        if (CacheFolder.AnyRunning(command.BusyImages, processImages()))
        {
            return Refused(approved, ToolRunning(approved));
        }

        // 3. The program found now is the one shown, and still not a link.
        if (finder.Find(command.Program) is not { } found || !string.Equals(found, program, StringComparison.OrdinalIgnoreCase)
            || PathProgramFinder.IsReparsePoint(found))
        {
            return Refused(approved, Text.CleanProgramChanged);
        }

        // 4. The tool still names the cache folder shown (a plain folder).
        if (CommandRows.QueryLocation(command, program, runner, cancellationToken) is not { } now
            || !string.Equals(now, location, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(approved, Text.CleanLocationChanged);
        }

        // 5. The size before, measured now.
        var before = CacheFolder.Measure(location, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // 6. The run can be recorded.
        if (!log.CanWrite())
        {
            return new CleanOutcome(approved.Entry.Id, CleanResult.Refused, Text.CleanLogUnavailable, before, null, null);
        }

        // 6b. The tool is still not running: steps 4 and 5 can take minutes on a large cache (hard review round 1, item 5).
        if (CacheFolder.AnyRunning(command.BusyImages, processImages()))
        {
            return new CleanOutcome(approved.Entry.Id, CleanResult.Refused, ToolRunning(approved), before, null, null);
        }

        // 7. The documented command, with its fixed arguments. From here on a cancel is not honoured: the run is waited for
        // (up to the limit) so that its result is measured and recorded.
        var arguments = CleanCommands.CleanArgumentsFor(command, program);
        var result = runner.Run(program, arguments, CleanLimit, CancellationToken.None);
        if (result.NotStarted)
        {
            // Windows did not start it: nothing ran, so nothing is recorded (hard review round 1, item 4).
            return new CleanOutcome(approved.Entry.Id, CleanResult.Refused, Text.CleanCouldNotStart, before, null, null);
        }

        // 8. The size after; a folder the tool removed counts 0.
        long? after = CacheFolder.IsGone(location) ? 0 : CacheFolder.IsPlainFolder(location) ? CacheFolder.Measure(location, CancellationToken.None) : null;

        var (outcome, reason) = result.TimedOut ? (CleanResult.TimedOut, Text.CleanTimedOut)
            : result.ExitCode == 0 ? (CleanResult.Cleaned, (string?)null)
            : (CleanResult.Failed, Text.CleanFailed(result.ExitCode ?? -1));

        // 9. Exactly one entry for a run that started.
        try
        {
            log.Append(new CommandLogEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, approved.Entry.Id, program, arguments, location,
                before, after, result.TimedOut ? null : result.ExitCode, result.TimedOut, outcome, reason, AppVersion));
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            // "Cleared" only after a run that did clear; a failure or a timeout keeps its own words (hard review round 3).
            reason = reason is null ? Text.CleanLogWriteFailed : reason + " — " + Text.CleanNotRecorded;
        }

        return new CleanOutcome(approved.Entry.Id, outcome, reason, before, after, result.TimedOut ? null : result.ExitCode);
    }

    // NuGet's tools are .NET processes, and a build server can stay for minutes after a build: say what to close.
    private static string ToolRunning(CommandRow approved) =>
        approved.Command.Program == "dotnet.exe" ? Text.CleanDotnetRunning : Text.CleanToolRunning(approved.Entry.AppName);

    private static CleanOutcome Refused(CommandRow approved, string reason) =>
        new(approved.Entry.Id, CleanResult.Refused, reason, approved.Bytes, null, null);
}
