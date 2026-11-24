using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Replaces this user's settings file (<c>%LOCALAPPDATA%\Ambysto\Space\settings.jsonl</c>, plan "Settings file":
/// UTF-8 JSON lines, append-only, the last line that parses wins) with what a test writes, and puts the original back
/// byte for byte on dispose (or removes the file, and the folders the test made when they are empty, when there was none):
/// the same pattern as <see cref="JournalBackup"/>. Dispose only after the app has exited, so it cannot append in between.
/// Tests that use it are marked <c>[TestCategory("SharedState")]</c>.</summary>
internal sealed class SettingsBackup : IDisposable
{
    private readonly byte[]? original;
    private readonly DateTime originalWriteUtc;
    private readonly bool spaceFolderExisted;
    private readonly bool ambystoFolderExisted;

    public SettingsBackup()
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
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "settings.jsonl");

    /// <summary>The settings file now holds exactly one line (plan "Settings file"): <c>{"version":1,"protectedFolders":[…]}</c>.</summary>
    public static void WriteProtectedFolders(params string[] folders) => WriteRaw(Line(folders) + "\n");

    /// <summary>One settings line in the plan's format (camel case, version 1).</summary>
    public static string Line(IEnumerable<string> folders) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["version"] = 1, ["protectedFolders"] = folders.ToArray() });

    /// <summary>The settings file now holds exactly <paramref name="text"/> (UTF-8, no BOM).</summary>
    public static void WriteRaw(string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, text, new UTF8Encoding(false));
    }

    /// <summary>The bytes of the settings file now, or null when there is none.</summary>
    public static byte[]? Bytes() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;

    /// <summary>The protected folders of the last line of the file that parses (the plan's reading rule), read by the test
    /// itself; null when no line parses or there is no file.</summary>
    public static List<string>? LastProtectedFolders()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        string text;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("protectedFolders", out var folders)
                    && folders.ValueKind == JsonValueKind.Array)
                {
                    return folders.EnumerateArray().Select(f => f.GetString() ?? string.Empty).ToList();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
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
