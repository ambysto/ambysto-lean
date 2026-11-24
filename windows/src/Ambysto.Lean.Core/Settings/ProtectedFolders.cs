using System;
using System.IO;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Settings;

/// <summary>What <see cref="ProtectedFolders.Add"/> did.</summary>
public enum AddResult
{
    /// <summary>The folder was added (entries inside it were replaced by it).</summary>
    Added,

    /// <summary>The folder is the same as, or inside, a folder already in the list: nothing changed.</summary>
    AlreadyCovered,

    /// <summary>The path is not an existing folder: nothing changed.</summary>
    NotAFolder,

    /// <summary>The text is not a usable local path: nothing changed.</summary>
    NotUsable,
}

/// <summary>The user's protected folders (docs/spec/02 Windows note, owner decision 2026-10-18): Lean never moves anything
/// inside, or holding, one of them. They only add protection. Adding a folder that holds entries already listed replaces
/// those entries; a folder already covered is not added again.</summary>
public static class ProtectedFolders
{
    /// <summary>Adds <paramref name="folder"/> (canonical spelling) to <paramref name="settings"/>; does not save.</summary>
    public static AddResult Add(LeanSettings settings, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A local folder only: a network or device spelling is refused before the disk is asked.
        if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) || CanonicalPath.Of(folder) is not { } canonical
            || canonical.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return AddResult.NotUsable;
        }

        if (!Directory.Exists(canonical))
        {
            return AddResult.NotAFolder;
        }

        var list = settings.MutableProtectedFolders;
        if (list.Exists(existing => CanonicalPath.Depth(existing, canonical) >= 0))
        {
            return AddResult.AlreadyCovered;
        }

        // Entries inside the new folder are covered by it from now on.
        list.RemoveAll(existing => CanonicalPath.Depth(canonical, existing) >= 1);
        list.Add(canonical);
        return AddResult.Added;
    }

    /// <summary>Removes the entry equal to <paramref name="folder"/> (ignoring case); false when there is none. Does not save.</summary>
    public static bool Remove(LeanSettings settings, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var canonical = CanonicalPath.Of(folder) ?? folder;
        return settings.MutableProtectedFolders.RemoveAll(existing => string.Equals(existing, canonical, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
