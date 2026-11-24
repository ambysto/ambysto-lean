using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>What a started program did: its exit code (null on a timeout), its standard output (at most 64 KiB) and whether
/// Lean stopped waiting for it.</summary>
public sealed record CommandRunResult(int? ExitCode, string StandardOutput, bool TimedOut)
{
    /// <summary>True when Windows did not start the program (blocked by policy, not found, not a program): nothing ran.</summary>
    public bool NotStarted { get; init; }
}

/// <summary>Starts a program and waits for it.</summary>
public interface ICommandRunner
{
    /// <summary>Runs <paramref name="programPath"/> with <paramref name="arguments"/>, each passed as one argument.</summary>
    CommandRunResult Run(string programPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The only place Lean starts a process (scripts/windows-choke-points.sh). No shell, an argument list, no window; a
/// program still running at the timeout or at a cancel is left running, never ended: Lean only stops waiting, and keeps reading
/// its output until it exits, so it never meets a closed pipe either (hard review round 1, item 2), without holding a thread. Every tool is asked for
/// UTF-8 output (item 6): Python through PYTHONUTF8 and PYTHONIOENCODING, the .NET CLI through DOTNET_CLI_FORCE_UTF8_ENCODING;
/// node and uv write UTF-8 already.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    // Programs Lean stopped waiting for: held, with their readers, until they exit, then disposed.
    private static readonly ConcurrentDictionary<Held, byte> LeftRunning = new();

    /// <summary>The most standard output kept.</summary>
    public const int OutputLimit = 64 * 1024;

    /// <inheritdoc/>
    public CommandRunResult Run(string programPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Path.IsPathFullyQualified(programPath) || !programPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || programPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("a full local path to an .exe", nameof(programPath));
        }

        var start = new ProcessStartInfo(programPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(programPath) ?? string.Empty,
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "true";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var outputEnded = new ManualResetEventSlim();
        var errorEnded = new ManualResetEventSlim();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                Ended(outputEnded); // end of the stream: every holder of the pipe has closed it
                return;
            }

            lock (output)
            {
                // Each line and its line break count toward the limit; what does not fit is dropped.
                var room = OutputLimit - output.Length;
                if (room > 0)
                {
                    output.Append(e.Data, 0, Math.Min(e.Data.Length, room));
                    if (output.Length < OutputLimit)
                    {
                        output.Append('\n');
                    }
                }
            }
        };

        // Read so a full error pipe never blocks the program; only its end matters.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                Ended(errorEnded);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Blocked by policy, removed since it was found, or not a program: nothing ran.
            process.Dispose();
            outputEnded.Dispose();
            errorEnded.Dispose();
            return new CommandRunResult(null, string.Empty, false) { NotStarted = true };
        }

        var clock = Stopwatch.StartNew();
        process.StandardInput.Close(); // a tool that asks a question gets end-of-input, never waits for an answer
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var held = new Held(process, outputEnded, errorEnded);
        if (WaitHandle.WaitAny([held.Exited, cancellationToken.WaitHandle], limit) != 0)
        {
            // Timed out or cancelled: the program keeps running; Lean stops waiting and reports it.
            KeepUntilExit(held);
            return new CommandRunResult(null, Snapshot(output), true);
        }

        // It has exited. Its output ends when every holder of the pipe is gone, and a helper it started may hold it: the
        // streams are waited for with what is left of the limit only, never longer (hard review round 2, item 2; .NET's own
        // WaitForExit(TimeSpan) does not bound this wait, measured on .NET 10).
        var exitCode = process.ExitCode;
        if (!outputEnded.Wait(Left(limit, clock), CancellationToken.None) || !errorEnded.Wait(Left(limit, clock), CancellationToken.None))
        {
            KeepUntilExit(held);
            return new CommandRunResult(exitCode, Snapshot(output), false);
        }

        var text = Snapshot(output);
        held.Dispose();
        return new CommandRunResult(exitCode, text, false);
    }

    /// <summary>How long a program that has exited keeps its output readers after Lean stopped waiting, for a helper that
    /// still holds its pipe; after that the readers are closed.</summary>
    internal static readonly TimeSpan HelperGrace = TimeSpan.FromMinutes(1);

    // A reader's last event can come while the held program is being disposed (after the grace): too late to matter.
    private static void Ended(ManualResetEventSlim ended)
    {
        try
        {
            ended.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan Left(TimeSpan limit, Stopwatch clock)
    {
        var left = limit - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    // Holds the process (so its pipes and readers stay open) until it exits, without a thread: a registered wait on its
    // handle. After the exit, its output gets HelperGrace to end, then everything is disposed. Never ends the program.
    // LeftRunning is the root that keeps a held Process from being collected (its finalizer would close the pipes).
    private static void KeepUntilExit(Held held)
    {
        LeftRunning.TryAdd(held, 0);
        held.Registration = ThreadPool.RegisterWaitForSingleObject(
            held.Exited,
            (_, _) =>
            {
                held.OutputEnded.Wait(HelperGrace);
                held.ErrorEnded.Wait(HelperGrace);
                LeftRunning.TryRemove(held, out _);
                held.Registration?.Unregister(null);
                held.Dispose();
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    // The process handle as a wait handle, without taking ownership of it (the Process disposes it).
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(Process process)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false);
        }
    }

    // A started program with what waits on it.
    private sealed class Held(Process process, ManualResetEventSlim outputEnded, ManualResetEventSlim errorEnded) : IDisposable
    {
        public WaitHandle Exited { get; } = new ProcessWaitHandle(process);

        public ManualResetEventSlim OutputEnded => outputEnded;

        public ManualResetEventSlim ErrorEnded => errorEnded;

        public RegisteredWaitHandle? Registration { get; set; }

        public void Dispose()
        {
            Exited.Dispose();
            process.Dispose();
            outputEnded.Dispose();
            errorEnded.Dispose();
        }
    }
}
