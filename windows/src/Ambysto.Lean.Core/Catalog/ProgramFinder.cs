using System;
using System.Collections.Generic;
using System.IO;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>Finds a program the way the user's terminal would (plan "Finding a program").</summary>
public interface IProgramFinder
{
    /// <summary>The full path of <paramref name="fileName"/>, or null when it is not found.</summary>
    string? Find(string fileName);
}

/// <summary>Searches the PATH given (the user's own by default), in order; skips relative, empty and UNC entries and a file
/// that is a reparse point (an app execution alias such as <c>WindowsApps\python.exe</c>, which opens the Store). Reads
/// attributes only; no file is opened.</summary>
public sealed class PathProgramFinder : IProgramFinder
{
    private readonly IReadOnlyList<string> folders;

    /// <summary>A finder over the current process's PATH.</summary>
    public PathProgramFinder() : this(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
    {
    }

    /// <summary>A finder over <paramref name="path"/> (a PATH value, entries separated by ';').</summary>
    public PathProgramFinder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var list = new List<string>();
        foreach (var raw in path.Split(';'))
        {
            // A quoted entry is legal in PATH; the quotes are not part of the folder.
            var entry = raw.Trim().Trim('"');
            if (IsLocalAbsolute(entry))
            {
                list.Add(entry);
            }
        }

        folders = list;
    }

    /// <inheritdoc/>
    public string? Find(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (fileName.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new ArgumentException("a file name, not a path", nameof(fileName));
        }

        foreach (var folder in folders)
        {
            var candidate = Path.Join(folder, fileName);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue; // missing or unreadable: the terminal would go on to the next entry as well
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue; // an app execution alias or a link: never run
            }

            return candidate;
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> is a file that is a reparse point now (step 3 of the clean runner).</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // cannot tell: treated as one (failing closed)
        }
    }

    // "C:\…": a drive letter, a colon and a backslash; never relative, never UNC or \\?\.
    private static bool IsLocalAbsolute(string entry) =>
        entry.Length >= 3 && char.IsAsciiLetter(entry[0]) && entry[1] == ':' && entry[2] == '\\';
}
