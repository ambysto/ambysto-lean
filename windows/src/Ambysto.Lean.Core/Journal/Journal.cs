using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Journal;

/// <summary>The append-only journal (docs/spec/05; plan Design "Journal"): one JSON object per line, flushed to disk after
/// each entry; a torn last line is ignored when reading; never uploaded. The default store is
/// <c>%LOCALAPPDATA%\Ambysto\Space\journal.jsonl</c>; tests give a path in their own temp folder.</summary>
public sealed class JournalStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private JournalStore(string path)
    {
        Path = path;
    }

    /// <summary>The default store of this user.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "journal.jsonl");

    /// <summary>The journal stored at <paramref name="path"/> (created on the first write, with its folder).</summary>
    public static JournalStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new JournalStore(System.IO.Path.GetFullPath(path));
    }

    /// <summary>The store's path.</summary>
    public string Path { get; }

    /// <summary>docs/spec/02 step 5: true when a write is accepted right now; nothing more is moved in a session when false.
    /// Opens the store for append (creating it and its folder if needed) and writes nothing.</summary>
    public bool CanWrite()
    {
        try
        {
            using var stream = OpenForAppend();
            return stream.CanWrite;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Appends <paramref name="entry"/> and flushes it to disk before returning; throws when the write fails.</summary>
    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(Line.From(entry), Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        using var stream = OpenForAppend();

        // A torn last line from an earlier failed write must not swallow this entry: start on a new line.
        if (stream.Length > 0 && LastByte() is { } last && last != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
        }

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Every complete entry, oldest first; a torn last line is ignored, and an unreadable line in the middle is
    /// skipped (it never stops the read).</summary>
    public IReadOnlyList<JournalEntry> Read()
    {
        var entries = new List<JournalEntry>();
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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    // The line as written: the spec's field names; the file id as 32 hex digits (no JSON number holds 128 bits).
    private sealed record Line(
        string Id,
        string SessionId,
        DateTimeOffset Time,
        LeanModule Module,
        string OriginalPath,
        string? OriginalParentRealPath,
        string? ApprovedRealPath,
        long? Bytes,
        string Mode,
        string? TrashPath,
        IdentityLine? TrashedIdentity,
        JournalResult Result,
        string? Reason,
        string AppVersion,
        string? RefersTo)
    {
        public static Line From(JournalEntry e) => new(
            e.Id, e.SessionId, e.Time.ToUniversalTime(), e.Module, e.OriginalPath, e.OriginalParentRealPath, e.ApprovedRealPath,
            e.Bytes, JournalEntry.Mode, e.TrashPath,
            e.TrashedIdentity is { } t ? new IdentityLine(t.VolumeSerial, t.FileId.ToString("x32", CultureInfo.InvariantCulture), t.LogicalBytes) : null,
            e.Result, e.Reason, e.AppVersion, e.RefersTo);

        public JournalEntry? ToEntry()
        {
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(SessionId) || string.IsNullOrEmpty(OriginalPath)
                || !string.Equals(Mode, JournalEntry.Mode, StringComparison.Ordinal))
            {
                return null;
            }

            TrashedIdentity? identity = TrashedIdentity is { } t
                ? new TrashedIdentity(t.VolumeSerial, UInt128.Parse(t.FileId, NumberStyles.HexNumber, CultureInfo.InvariantCulture), t.LogicalBytes)
                : null;
            return new JournalEntry(Id, SessionId, Time, Module, OriginalPath, OriginalParentRealPath, ApprovedRealPath, Bytes,
                TrashPath, identity, Result, Reason, AppVersion ?? string.Empty) { RefersTo = RefersTo };
        }
    }

    private sealed record IdentityLine(ulong VolumeSerial, string FileId, long LogicalBytes);
}
