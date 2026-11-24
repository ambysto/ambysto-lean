using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>Whether a Command row can be cleaned now.</summary>
public enum CommandRowState
{
    /// <summary>Program found, cache folder found and measured, tool not running.</summary>
    Ready,

    /// <summary>One of the tool's processes is running (decision 6).</summary>
    ToolRunning,

    /// <summary>The program is not on the user's PATH.</summary>
    ProgramMissing,

    /// <summary>The location query failed, or its answer is not an existing folder.</summary>
    LocationUnknown,

    /// <summary>The cache folder holds nothing.</summary>
    Empty,

    /// <summary>Lean runs as administrator, as SYSTEM or outside the desktop session: no command is started, not even the
    /// location query (hard review round 1, item 1).</summary>
    NotAllowedHere,
}

/// <summary>One Command row as read: the program found, the cache folder it names, its measured size and its state.</summary>
public sealed record CommandRow(CatalogEntry Entry, CleanCommand Command, string? ProgramPath, string? Location, long Bytes, CommandRowState State);

/// <summary>Reads the Command rows (plan "Command rows"); runs only the read-only location queries.</summary>
public static class CommandRows
{
    /// <summary>How long a location query is waited for.</summary>
    public static readonly TimeSpan LocationLimit = TimeSpan.FromSeconds(30);

    /// <summary>One row per command, in table order. Off the calling thread: the queries and the walk take time.</summary>
    public static Task<IReadOnlyList<CommandRow>> ReadAsync(ICommandEnvironment environment, IProgramFinder finder, ICommandRunner runner, Func<IReadOnlyList<string>> processImages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(processImages);
        return Task.Run<IReadOnlyList<CommandRow>>(() => Read(environment, finder, runner, processImages, cancellationToken), cancellationToken);
    }

    /// <summary>The cache folder the tool names now, or null (not started, timed out, an error code, an answer that is not one
    /// absolute local path, or a path that is not a plain folder). Used again by the clean runner at step 4.</summary>
    internal static string? QueryLocation(CleanCommand command, string programPath, ICommandRunner runner, CancellationToken cancellationToken)
    {
        var result = runner.Run(programPath, CleanCommands.LocationArgumentsFor(command, programPath), LocationLimit, cancellationToken);
        if (result.NotStarted || result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        return CleanCommands.ParseLocation(command, result.StandardOutput) is { } location && CacheFolder.IsPlainFolder(location) ? location : null;
    }

    private static List<CommandRow> Read(ICommandEnvironment environment, IProgramFinder finder, ICommandRunner runner, Func<IReadOnlyList<string>> processImages, CancellationToken cancellationToken)
    {
        var rows = new List<CommandRow>();
        var allowed = environment.Refusal() is null;
        foreach (var command in CleanCommands.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = AppCatalog.Entries.Single(e => e.Id == command.EntryId);
            if (!allowed)
            {
                rows.Add(new CommandRow(entry, command, null, null, 0, CommandRowState.NotAllowedHere));
                continue;
            }

            var program = finder.Find(command.Program);
            if (program is null)
            {
                rows.Add(new CommandRow(entry, command, null, null, 0, CommandRowState.ProgramMissing));
                continue;
            }

            if (QueryLocation(command, program, runner, cancellationToken) is not { } location)
            {
                rows.Add(new CommandRow(entry, command, program, null, 0, CommandRowState.LocationUnknown));
                continue;
            }

            var bytes = CacheFolder.Measure(location, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var state = bytes == 0 ? CommandRowState.Empty
                : CacheFolder.AnyRunning(command.BusyImages, processImages()) ? CommandRowState.ToolRunning
                : CommandRowState.Ready;
            rows.Add(new CommandRow(entry, command, program, location, bytes, state));
        }

        return rows;
    }
}
