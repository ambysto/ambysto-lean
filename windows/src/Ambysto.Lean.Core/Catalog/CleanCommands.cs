using System;
using System.Collections.Generic;
using System.IO;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>One documented clean command (ASP-20 decision 12): the program's file name, the fixed arguments of the clean and
/// of the query that names the cache folder, and the images that mean the tool is running.</summary>
public sealed record CleanCommand(string EntryId, string Program, IReadOnlyList<string> CleanArguments, IReadOnlyList<string> LocationArguments, IReadOnlyList<string> BusyImages);

/// <summary>The fixed table of clean commands (plan "Commands"). Nothing read from the disk goes into an argument, except npm's
/// own script under the folder of the node.exe that was found (npm.cmd would need cmd.exe, a shell).</summary>
public static class CleanCommands
{
    private static readonly string[] NuGetBusy = ["dotnet.exe", "MSBuild.exe", "devenv.exe", "nuget.exe"];

    /// <summary>Every command, in catalog order.</summary>
    public static IReadOnlyList<CleanCommand> All { get; } =
    [
        new("npm", "node.exe", ["cache", "clean", "--force"], ["config", "get", "cache"], ["node.exe"]),
        new("pip", "python.exe", ["-m", "pip", "cache", "purge"], ["-m", "pip", "cache", "dir"], ["python.exe", "pythonw.exe"]),
        new("uv", "uv.exe", ["cache", "clean"], ["cache", "dir"], ["uv.exe", "uvx.exe"]),
        new("nuget-http", "dotnet.exe", ["nuget", "locals", "http-cache", "--clear"], ["nuget", "locals", "http-cache", "--list"], NuGetBusy),
        new("nuget-plugins", "dotnet.exe", ["nuget", "locals", "plugins-cache", "--clear"], ["nuget", "locals", "plugins-cache", "--list"], NuGetBusy),
    ];

    /// <summary>The clean arguments for the program found at <paramref name="programPath"/>.</summary>
    public static IReadOnlyList<string> CleanArgumentsFor(CleanCommand command, string programPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WithScript(command, programPath, command.CleanArguments);
    }

    /// <summary>The location query's arguments for the program found at <paramref name="programPath"/>.</summary>
    public static IReadOnlyList<string> LocationArgumentsFor(CleanCommand command, string programPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WithScript(command, programPath, command.LocationArguments);
    }

    /// <summary>The cache folder named by the location query's output; null unless it is exactly one absolute local path
    /// (a drive letter, a colon, a backslash and at least one component; no <c>..</c> or <c>.</c> component).</summary>
    public static string? ParseLocation(CleanCommand command, string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (standardOutput is null)
        {
            return null;
        }

        string? line = null;
        foreach (var raw in standardOutput.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (line is not null)
            {
                return null; // more than one line of output: not a plain answer
            }

            line = trimmed;
        }

        if (line is null)
        {
            return null;
        }

        // dotnet nuget locals <name> --list answers "<name>: <path>"; the name must be this entry's.
        if (DotnetPrefix(command) is { } prefix)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            line = line[prefix.Length..].Trim();
        }

        return IsAbsoluteLocalFolder(line) ? line.TrimEnd('\\') : null;
    }

    private static string? DotnetPrefix(CleanCommand command) => command.EntryId switch
    {
        "nuget-http" => "http-cache:",
        "nuget-plugins" => "plugins-cache:",
        _ => null,
    };

    private static List<string> WithScript(CleanCommand command, string programPath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        var result = new List<string>();
        if (command.EntryId == "npm")
        {
            var folder = Path.GetDirectoryName(programPath);
            if (string.IsNullOrEmpty(folder))
            {
                throw new ArgumentException("no folder in the program path", nameof(programPath));
            }

            result.Add(Path.Join(folder, "node_modules", "npm", "bin", "npm-cli.js"));
        }

        result.AddRange(arguments);
        return result;
    }

    private static bool IsAbsoluteLocalFolder(string path)
    {
        // "C:\x" at the least: a drive letter, a colon, a backslash, then a component. No UNC, no \\?\, no forward slash, no
        // second colon (an alternate stream).
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Contains('/', StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        var components = path[3..].TrimEnd('\\').Split('\\');
        if (components.Length == 0)
        {
            return false;
        }

        foreach (var component in components)
        {
            if (component.Length == 0 || component == "." || component == "..")
            {
                return false;
            }
        }

        return true;
    }
}
