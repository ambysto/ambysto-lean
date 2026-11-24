using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>One clean command run (plan "The clean runner and its log").</summary>
public sealed record CommandLogEntry(
    string Id,
    DateTimeOffset Time,
    string EntryId,
    string Program,
    IReadOnlyList<string> Arguments,
    string Location,
    long BytesBefore,
    long? BytesAfter,
    int? ExitCode,
    bool TimedOut,
    CleanResult Result,
    string? Reason,
    string AppVersion);

/// <summary>The command log, <c>%LOCALAPPDATA%\Ambysto\Space\commands.jsonl</c>: one JSON object per line, separate from the
/// trash journal, so Put back never sees a command run.</summary>
public sealed class CommandLog
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private CommandLog(string path) => Path = path;

    /// <summary>The default log file, beside the trash journal.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "commands.jsonl");

    /// <summary>The log file.</summary>
    public string Path { get; }

    /// <summary>A log at <paramref name="path"/> (created on the first write, with its folder).</summary>
    public static CommandLog Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new CommandLog(System.IO.Path.GetFullPath(path));
    }

    /// <summary>True when a line can be appended now: opens the file for append (creating it and its folder) and writes nothing.</summary>
    public bool CanWrite()
    {
        try
        {
            using var stream = OpenForAppend();
            return stream.CanWrite;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Appends one entry and flushes it to disk; throws when the write fails.</summary>
    public void Append(CommandLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Line.From(entry), Options) + "\n");
        using var stream = OpenForAppend();

        // A torn last line from an earlier failed write must not swallow this entry: start on a new line.
        if (stream.Length > 0 && LastByte() is { } last && last != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
        }

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Every entry, oldest first; a line that cannot be parsed, or that holds a null argument or an empty program, entry id or
    /// location, is skipped.</summary>
    public IReadOnlyList<CommandLogEntry> Read()
    {
        var entries = new List<CommandLogEntry>();
        string text;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return entries;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<Line>(line, Options)?.ToEntry() is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or OverflowException)
            {
                // A torn or damaged line: skipped, never fatal.
            }
        }

        return entries;
    }

    private FileStream OpenForAppend()
    {
        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        return new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private byte? LastByte()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return null;
        }

        stream.Seek(-1, SeekOrigin.End);
        var value = stream.ReadByte();
        return value < 0 ? null : (byte)value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    // The line as written: the plan's field names, every field present (null when unknown); the time as a UTC DateTime, so
    // it is written with a Z.
    private sealed record Line(
        string Id,
        DateTime Time,
        string EntryId,
        string Program,
        IReadOnlyList<string> Arguments,
        string Location,
        long BytesBefore,
        long? BytesAfter,
        int? ExitCode,
        bool TimedOut,
        CleanResult Result,
        string? Reason,
        string AppVersion)
    {
        public static Line From(CommandLogEntry e) => new(
            e.Id, e.Time.UtcDateTime, e.EntryId, e.Program, e.Arguments, e.Location, e.BytesBefore, e.BytesAfter, e.ExitCode,
            e.TimedOut, e.Result, e.Reason, e.AppVersion);

        public CommandLogEntry? ToEntry()
        {
            // A damaged line (a null argument, an empty program, entry id or location) is skipped, not shown.
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(EntryId) || string.IsNullOrEmpty(Program) || string.IsNullOrEmpty(Location)
                || Arguments is null || Arguments.Any(a => a is null))
            {
                return null;
            }

            return new CommandLogEntry(Id, new DateTimeOffset(DateTime.SpecifyKind(Time.ToUniversalTime(), DateTimeKind.Utc)), EntryId, Program, Arguments, Location, BytesBefore, BytesAfter, ExitCode, TimedOut, Result, Reason, AppVersion ?? string.Empty);
        }
    }
}
