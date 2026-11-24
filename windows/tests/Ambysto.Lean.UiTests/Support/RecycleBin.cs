using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>The test's own reading of this user's Recycle Bin on the profile's volume: the <c>$I</c> records in
/// <c>&lt;volume&gt;\$Recycle.Bin\&lt;user SID&gt;</c> (each names the original path) and their <c>$R</c> items. Reads only;
/// never empties, restores or deletes anything there.</summary>
internal static class RecycleBin
{
    /// <summary>One <c>$I</c> record and its <c>$R</c> item.</summary>
    public sealed record Entry(string InfoPath, string ItemPath, string OriginalPath, long Size, DateTime DeletedUtc)
    {
        /// <summary>True while the <c>$R</c> item is there (Put back moves it out; the <c>$I</c> record stays).</summary>
        public bool ItemExists => File.Exists(ItemPath) || Directory.Exists(ItemPath);
    }

    /// <summary>This user's bin folder on the volume of <paramref name="anyPath"/>.</summary>
    public static string UserFolder(string anyPath)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(anyPath))!;
        return System.IO.Path.Combine(root, "$Recycle.Bin", WindowsIdentity.GetCurrent().User!.Value);
    }

    /// <summary>Every readable record of this user's bin on the volume of <paramref name="anyPath"/>.</summary>
    public static List<Entry> Entries(string anyPath)
    {
        var folder = UserFolder(anyPath);
        var entries = new List<Entry>();
        if (!Directory.Exists(folder))
        {
            return entries;
        }

        foreach (var info in Directory.EnumerateFiles(folder, "$I*"))
        {
            if (Parse(info) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>The newest record for <paramref name="originalPath"/> whose <c>$R</c> item is still in the bin, or null.</summary>
    public static Entry? Holding(string originalPath) =>
        Entries(originalPath).Where(e => e.ItemExists && string.Equals(e.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.DeletedUtc).FirstOrDefault();

    // $I layout: version (int64: 1 before Windows 10, 2 since), size (int64), deletion time (FILETIME); version 2: the
    // path length in characters (int32) and the path (UTF-16, with its terminator); version 1: the path in 260 characters.
    private static Entry? Parse(string infoPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(infoPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length < 28)
        {
            return null;
        }

        var version = BitConverter.ToInt64(bytes, 0);
        var size = BitConverter.ToInt64(bytes, 8);
        var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
        string path;
        if (version == 2)
        {
            var chars = BitConverter.ToInt32(bytes, 24);
            if (chars <= 0 || 28 + (chars * 2) > bytes.Length)
            {
                return null;
            }

            path = Encoding.Unicode.GetString(bytes, 28, chars * 2).TrimEnd('\0');
        }
        else if (version == 1 && bytes.Length >= 24 + 520)
        {
            path = Encoding.Unicode.GetString(bytes, 24, 520).Split('\0')[0];
        }
        else
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(infoPath);
        var item = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
        return new Entry(infoPath, item, path, size, deleted);
    }
}
