using System;
using System.IO;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Map;

/// <summary>Where-to-manage hints for display-only items (docs/spec/01 hint table and its Windows notes).</summary>
public static class DisplayOnly
{
    /// <summary>The hint for an item Lean may not move, by path and owner alone (the gate decides at move time): an item
    /// <see cref="Rules.LeanRules.Preview"/> does not admit, or one inside a never-touch location of docs/spec/02 that has
    /// its own hint (a cloud-sync folder, device backups) even within root (a). Null for any other item.</summary>
    public static string? HintFor(string path, PathContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var item = CanonicalPath.Of(path);
        if (item is null)
        {
            return Text.HintOutside;
        }

        var folders = CanonicalContext.For(context);

        // The user's own protected folders come first: inside or holding one (docs/spec/02 Windows note).
        foreach (var protectedFolder in folders.UserProtected)
        {
            if (CanonicalContext.Inside(protectedFolder, item) || CanonicalPath.Depth(item, protectedFolder) >= 0)
            {
                return Text.HintProtectedByYou;
            }
        }

        // A context whose profile cannot be spelled admits nothing, and without it the user's own folder cannot be told
        // from another user's: the neutral words, never "system-wide" (Stryker triage H04, 2026-10-18).
        if (folders.Profile is null)
        {
            return Text.HintOutside;
        }

        // Never-touch locations with a hint of their own, whatever root (a) or (b) would say about the path.
        foreach (var (root, provider) in folders.Sync)
        {
            if (CanonicalContext.Inside(root, item))
            {
                return Text.HintSynced(provider);
            }
        }

        foreach (var backups in folders.DeviceBackups)
        {
            if (CanonicalContext.Inside(backups, item))
            {
                return Text.HintDeviceBackups;
            }
        }

        // System-wide: the system folders, Users and so every other user's folder (the user's own profile is not "other").
        var insideProfile = CanonicalContext.Inside(folders.Profile, item);
        foreach (var system in folders.System)
        {
            if (CanonicalContext.Inside(system, item))
            {
                return Text.HintSystemWide;
            }
        }

        if (!insideProfile && CanonicalContext.Inside(folders.Users, item))
        {
            return Text.HintSystemWide;
        }

        if (string.Equals(Path.GetExtension(item), ".vhdx", StringComparison.OrdinalIgnoreCase) && folders.InAppData(item))
        {
            return Text.HintDockerWsl;
        }

        return LeanRules.Preview(path, context) ? null : Text.HintOutside;
    }
}
