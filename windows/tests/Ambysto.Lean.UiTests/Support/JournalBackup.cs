using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Replaces this user's journal (<c>%LOCALAPPDATA%\Ambysto\Space\journal.jsonl</c>, docs/spec/05) with the lines a
/// test writes, and puts the original back byte for byte on dispose (or removes the file, and the folders the test made,
/// when there was none). Dispose only after the app has exited, so it cannot append in between.</summary>
internal sealed class JournalBackup : IDisposable
{
    private readonly byte[]? original;
    private readonly DateTime originalWriteUtc;
    private readonly bool spaceFolderExisted;
    private readonly bool ambystoFolderExisted;

    public JournalBackup()
    {
        var space = System.IO.Path.GetDirectoryName(Path)!;
        ambystoFolderExisted = Directory.Exists(System.IO.Path.GetDirectoryName(space)!);
        spaceFolderExisted = Directory.Exists(space);
        if (File.Exists(Path))
        {
            original = File.ReadAllBytes(Path);
            originalWriteUtc = File.GetLastWriteTimeUtc(Path);
        }
    }

    public static string Path { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "journal.jsonl");

    /// <summary>The journal now holds exactly <paramref name="lines"/>, one JSON object per line.</summary>
    public static void Write(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
    }

    /// <summary>One journal line in docs/spec/05's format (field names, ISO 8601 UTC time, <c>mode</c> <c>trash</c>);
    /// the optional fields are left out when null.</summary>
    public static string Line(string id, string sessionId, DateTimeOffset time, string module, string originalPath, string result,
        string? reason = null, string? refersTo = null, long? bytes = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sessionId"] = sessionId,
            ["time"] = time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            ["module"] = module,
            ["originalPath"] = originalPath,
            ["originalParentRealPath"] = System.IO.Path.GetDirectoryName(originalPath),
            ["approvedRealPath"] = originalPath,
            ["bytes"] = bytes,
            ["mode"] = "trash",
            ["result"] = result,
            ["reason"] = reason,
            ["appVersion"] = "0.0.0-uitest",
            ["refersTo"] = refersTo,
        };
        return JsonSerializer.Serialize(fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value));
    }

    public void Dispose()
    {
        if (original is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllBytes(Path, original);
            File.SetLastWriteTimeUtc(Path, originalWriteUtc);
            return;
        }

        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        var space = System.IO.Path.GetDirectoryName(Path)!;
        if (!spaceFolderExisted && Directory.Exists(space) && !Directory.EnumerateFileSystemEntries(space).Any())
        {
            Directory.Delete(space);
        }

        var ambysto = System.IO.Path.GetDirectoryName(space)!;
        if (!ambystoFolderExisted && Directory.Exists(ambysto) && !Directory.EnumerateFileSystemEntries(ambysto).Any())
        {
            Directory.Delete(ambysto);
        }
    }
}
