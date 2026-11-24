using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Settings;

/// <summary>The outcome of <see cref="LeanSettings.Load"/>: the settings, and whether the file was there but unreadable
/// or broken (then the defaults are used and the file is left as it is, never overwritten silently).</summary>
public sealed record LoadResult(LeanSettings Settings, bool WasBroken);

/// <summary>Lean's settings file (plan "v1 additions", Design): UTF-8 JSON lines, append-only, as the journal: each save
/// appends one line <c>{"version":1,"protectedFolders":[...]}</c> and flushes; reading takes the last line that parses.
/// Nothing is ever replaced or deleted (Lean's tripwire). It lives in Lean's own protected folder beside the journal and
/// holds the user's protected folders (docs/spec/02 Windows note), canonical, ordered, without duplicates.</summary>
public sealed class LeanSettings
{
    private readonly List<string> protectedFolders = [];

    /// <summary><c>%LOCALAPPDATA%\Ambysto\Space\settings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "settings.jsonl");

    /// <summary>The user's protected folders, in the order they were added.</summary>
    public IReadOnlyList<string> ProtectedFolders => protectedFolders;

    internal List<string> MutableProtectedFolders => protectedFolders;

    /// <summary>Reads the last line of <paramref name="path"/> that parses; a missing file gives the defaults; a file with
    /// content but no line that parses gives the defaults with <see cref="LoadResult.WasBroken"/> set (the file is left as
    /// it is).</summary>
    public static LoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new LoadResult(new LeanSettings(), false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new LoadResult(new LeanSettings(), true);
        }

        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (Parse(lines[i].TrimEnd('\r')) is { } settings)
            {
                return new LoadResult(settings, false);
            }
        }

        return new LoadResult(new LeanSettings(), text.Trim().Length > 0);
    }

    /// <summary>Appends one line with the whole settings to <paramref name="path"/> (a new line first when the file does not
    /// end with one) and flushes it to disk; a crash leaves at worst a torn last line, which reading ignores.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var line = JsonSerializer.Serialize(new Line(CurrentVersion, [.. protectedFolders]), Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // A torn last line from an earlier failed save must not swallow this one: start on a new line.
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // One line; a line of another version, or with a folder that cannot be spelled, is not this file's (null).
    private static LeanSettings? Parse(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<Line>(line, Options) is not { Version: CurrentVersion, ProtectedFolders: { } folders })
            {
                return null;
            }

            var settings = new LeanSettings();
            foreach (var folder in folders)
            {
                if (CanonicalPath.Of(folder) is { } canonical && !settings.protectedFolders.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    settings.protectedFolders.Add(canonical);
                }
            }

            return settings;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record Line(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("protectedFolders")] IReadOnlyList<string>? ProtectedFolders);
}
