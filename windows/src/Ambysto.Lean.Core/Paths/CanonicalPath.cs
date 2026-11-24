using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Paths;

/// <summary>The canonical spelling the pre-check compares (docs/spec/02: comparison on canonical paths, so "..", a trailing
/// separator, case and an 8.3 short name cannot slip past). Reads names only; nothing is opened, so a subst letter, a
/// mapped drive or a link is not resolved: such a spelling matches no allowed root (fails closed). Phase B's rules take the
/// canonical path from the Kit's facts, not from here.</summary>
internal static partial class CanonicalPath
{
    /// <summary>The full path with ".." resolved, no trailing separator (except a volume root, which keeps none either:
    /// <c>C:</c>), and every short name expanded; null when the text is not a usable path.</summary>
    public static string? Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // \\?\C:\x is the same item as C:\x: resolved like it (so ".." is resolved too); any other device or namespace
        // spelling (\\?\UNC, \\?\Volume{...}, \\.\) is not a usable path here.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            if (path.Length < 7 || path[5] != ':' || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return null;
            }

            path = path[4..];
        }

        // A name ending in a dot or a space is a different item to NTFS than the trimmed name GetFullPath would give.
        foreach (var name in path.Split('\\', '/'))
        {
            if (name.Length > 0 && name is not "." and not ".." && (name.EndsWith('.') || name.EndsWith(' ')))
            {
                return null;
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        return Trim(ExpandShortNames(full));
    }

    /// <summary>How many path components <paramref name="child"/> lies below <paramref name="parent"/> (both canonical,
    /// from <see cref="Of"/>): 0 when they are the same item, -1 when <paramref name="child"/> is not inside.</summary>
    public static int Depth(string parent, string child)
    {
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (child.Length == parent.Length)
        {
            return 0;
        }

        if (child[parent.Length] != '\\')
        {
            return -1;
        }

        var count = 1;
        for (var i = parent.Length + 1; i < child.Length; i++)
        {
            if (child[i] == '\\')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The folders from <paramref name="top"/> (exclusive of anything above it) down to <paramref name="item"/>,
    /// each as a canonical path, <paramref name="top"/> first and <paramref name="item"/> last.</summary>
    public static List<string> Chain(string top, string item)
    {
        var chain = new List<string> { top };
        var index = top.Length;
        while (index < item.Length)
        {
            var next = item.IndexOf('\\', index + 1);
            chain.Add(next < 0 ? item : item[..next]);
            if (next < 0)
            {
                break;
            }

            index = next;
        }

        return chain;
    }

    private static string Trim(string path)
    {
        var trimmed = path.TrimEnd('\\');
        return trimmed.Length == 0 ? path : trimmed;
    }

    // Expands 8.3 names of the longest part of the path that exists, then appends the rest as written.
    private static string ExpandShortNames(string full)
    {
        if (TryLongPath(full) is { } whole)
        {
            return whole;
        }

        var rest = new Stack<string>();
        var current = Trim(full);
        while (true)
        {
            var parent = Path.GetDirectoryName(current);
            var name = Path.GetFileName(current);
            if (parent is null || name.Length == 0)
            {
                return full;
            }

            rest.Push(name);
            current = parent;
            if (TryLongPath(current) is { } expanded)
            {
                var builder = new StringBuilder(Trim(expanded));
                while (rest.Count > 0)
                {
                    builder.Append('\\').Append(rest.Pop());
                }

                return builder.ToString();
            }
        }
    }

    private static unsafe string? TryLongPath(string path)
    {
        var extended = MeasureNative.ToExtendedPath(path);
        var buffer = new char[extended.Length + 64];
        while (true)
        {
            int length;
            fixed (char* pointer = buffer)
            {
                length = (int)GetLongPathName(extended, pointer, (uint)buffer.Length);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                var result = new string(buffer, 0, length);
                if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                {
                    return @"\\" + result[8..];
                }

                return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
            }

            buffer = new char[length + 1];
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial uint GetLongPathName(string shortPath, char* longPath, uint bufferLength);
}
