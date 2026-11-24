using System;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Rules;

/// <summary>Lean's allowed roots (intent decision 7; docs/spec/02 Windows note). Phase A has only the read-only
/// pre-check for display-only hints; the rules the Kit gate applies at move time (<c>ForDelete</c>,
/// <c>ForRestore</c>) arrive in phase B, and the gate stays the decider.</summary>
public static class LeanRules
{
    /// <summary>The Kit rule for moves from <paramref name="module"/> (decision 7, HARD): roots (a) and (b) as in
    /// <see cref="Preview"/>, judged on the Kit's <c>ItemFacts</c> (canonical path and owners of the item and every folder
    /// above it; an unknown owner is not the user's); and (c) module-bound: for <see cref="LeanModule.Uninstall"/> only, an
    /// item whose canonical path is exactly one of <paramref name="moduleItems"/> (the leftover candidates the matcher
    /// produced). The refusal message is <c>Copy.Text.HintOutside</c>.</summary>
    public static Ambysto.Kit.Safety.IAllowedRootRule ForDelete(LeanModule module, System.Collections.Generic.IReadOnlySet<string> moduleItems)
    {
        ArgumentNullException.ThrowIfNull(moduleItems);
        return new KitRule(module, moduleItems, PathContext.Current());
    }

    /// <summary>The Kit rule for Put back destinations (owner decision Q2 = A): the destination's parent may be in roots
    /// (a) or (b), or in the leftover folders of docs/spec/04 (<c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>,
    /// <c>%LOCALAPPDATA%\Programs</c>) or the user's <c>Start Menu\Programs</c> (not <c>Startup</c>), judged from the
    /// destination path alone, never from a journal line.</summary>
    public static Ambysto.Kit.Safety.IAllowedRootRule ForRestore() =>
        new KitRule(LeanModule.Map, new System.Collections.Generic.HashSet<string>(), PathContext.Current());

    /// <summary>True when, by path and owner alone, <paramref name="path"/> lies in root (a) or (b) of decision 7:
    /// inside the profile at least 2 components below it and not inside AppData (except <c>%LOCALAPPDATA%\Temp\*</c>
    /// and <c>%LOCALAPPDATA%\CrashDumps\*</c>), or inside a folder at the root of the profile's volume that the
    /// current user owns, with every folder down to it, at least one component inside, never a system folder or
    /// Users. Reads owners only; an owner that cannot be read is not the user's.</summary>
    public static bool Preview(string path, PathContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CanonicalPath.Of(path) is not { } item)
        {
            return false;
        }

        var folders = CanonicalContext.For(context);
        if (folders.Profile is null)
        {
            return false;
        }

        // Root (a): inside the profile. An item there is judged by (a) alone, whatever else holds true of its path.
        var belowProfile = CanonicalPath.Depth(folders.Profile, item);
        if (belowProfile >= 0)
        {
            if (belowProfile < 2)
            {
                return false;
            }

            return !folders.InAppData(item) || IsInsideContentsOf(folders.LocalTemp, item) || IsInsideContentsOf(folders.CrashDumps, item);
        }

        return IsInUserFolderAtVolumeRoot(item, folders);
    }

    // Root (b): a folder at the root of the profile's volume that the current user owns, with every folder down to the
    // item; at least one component inside it; never a system location, Users or AppData.
    private static bool IsInUserFolderAtVolumeRoot(string item, CanonicalContext folders)
    {
        if (folders.VolumeRoot is null || CanonicalPath.Depth(folders.VolumeRoot, item) < 2)
        {
            return false;
        }

        if (folders.InAppData(item) || CanonicalContext.Inside(folders.Users, item))
        {
            return false;
        }

        foreach (var system in folders.System)
        {
            if (CanonicalContext.Inside(system, item))
            {
                return false;
            }
        }

        // The top folder first, then each folder below it, down to the item: all owned by the user, none a link (a
        // junction or a symbolic link would put the item somewhere else than its path says). The item itself is never a
        // link either (the gate refuses one at step 3); it needs an owner only when it is a folder, and a file only has to
        // be readable (owner decision 2026-10-17, docs/spec/02). A folder above the item with any reparse point is refused
        // (fails closed); the item itself may be a compressed (WOF) or deduplicated file, which is not a link.
        var topEnd = item.IndexOf('\\', folders.VolumeRoot.Length + 1);
        var chain = CanonicalPath.Chain(item[..topEnd], item);
        for (var i = 0; i < chain.Count; i++)
        {
            if (OwnerFacts.Read(chain[i]) is not { } facts)
            {
                return false;
            }

            var isItem = i == chain.Count - 1;
            if (facts.IsLink)
            {
                return false;
            }

            if (isItem)
            {
                if (facts.IsDirectory && !facts.IsOwnedByCurrentUser)
                {
                    return false;
                }
            }
            else if (!facts.IsDirectory || facts.IsReparsePoint || !facts.IsOwnedByCurrentUser)
            {
                return false;
            }
        }

        return true;
    }

    // True for something inside the folder, not the folder itself (the literal-path exceptions of root (a)).
    private static bool IsInsideContentsOf(string? folder, string item) => folder is not null && CanonicalPath.Depth(folder, item) >= 1;

    // Decision 7 on the Kit's facts. Paths from the Kit are canonical (final paths through a handle); Lean's folders are
    // made canonical once, by CanonicalContext. Every refusal gives the same display-only words as the hints.
    private sealed class KitRule(LeanModule module, System.Collections.Generic.IReadOnlySet<string> moduleItems, PathContext context)
        : Ambysto.Kit.Safety.IAllowedRootRule
    {
        private readonly CanonicalContext folders = CanonicalContext.For(context);
        private readonly string? startMenuPrograms = CanonicalPath.Of(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        private readonly string? startup = CanonicalPath.Of(Environment.GetFolderPath(Environment.SpecialFolder.Startup));

        public Ambysto.Kit.Safety.AllowedRootDecision Evaluate(Ambysto.Kit.Safety.ItemFacts item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!item.IsSystemLocation && CanonicalPath.Of(item.CanonicalPath) is { } path)
            {
                // (c) module-bound: exactly an item the module produced, only for that module, and only inside its
                // module-bound root (a leftover at least one component inside %APPDATA% or %LOCALAPPDATA%, which holds
                // %LOCALAPPDATA%\Programs; never inside Startup).
                if (module == LeanModule.Uninstall && IsModuleItem(path) && InsideLeftoverFolder(path))
                {
                    return Ambysto.Kit.Safety.AllowedRootDecision.Allow();
                }

                if (module == LeanModule.Installers)
                {
                    return IsModuleItem(path) && Catalog.InstallerVersions.CanMoveNow(path, context, new Uninstall.RegistryUninstallSource())
                        ? Ambysto.Kit.Safety.AllowedRootDecision.Allow()
                        : Ambysto.Kit.Safety.AllowedRootDecision.Refuse(Copy.Text.HintOutside);
                }

                if (InRootA(path) || InRootB(path, item, itemIsTheTarget: true))
                {
                    return Ambysto.Kit.Safety.AllowedRootDecision.Allow();
                }
            }

            return Ambysto.Kit.Safety.AllowedRootDecision.Refuse(Copy.Text.HintOutside);
        }

        public Ambysto.Kit.Safety.AllowedRootDecision EvaluateRestore(Ambysto.Kit.Safety.ItemFacts destinationParent)
        {
            ArgumentNullException.ThrowIfNull(destinationParent);
            if (!destinationParent.IsSystemLocation && CanonicalPath.Of(destinationParent.CanonicalPath) is { } parent)
            {
                // Judged from the destination path alone (owner decision Q2 = A): the item would sit one component below
                // the parent, so the parent is checked as the folder that holds it.
                if (ParentInRootA(parent) || InRootB(parent, destinationParent, itemIsTheTarget: false) || InLeftoverFolder(parent)
                    || InStartMenuPrograms(parent))
                {
                    return Ambysto.Kit.Safety.AllowedRootDecision.Allow();
                }
            }

            return Ambysto.Kit.Safety.AllowedRootDecision.Refuse(Copy.Text.HintOutside);
        }

        private bool IsModuleItem(string path)
        {
            foreach (var candidate in moduleItems)
            {
                if (CanonicalPath.Of(candidate) is { } c && string.Equals(c, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // (a): inside the profile, at least 2 components below it, not in AppData except the contents of the two folders.
        private bool InRootA(string path)
        {
            if (folders.Profile is null || CanonicalPath.Depth(folders.Profile, path) < 2)
            {
                return false;
            }

            return !folders.InAppData(path) || IsInsideContentsOf(folders.LocalTemp, path) || IsInsideContentsOf(folders.CrashDumps, path);
        }

        // (a) for a destination's parent: the destination would be at least 2 below the profile, outside AppData (or in the
        // two exception folders' contents).
        private bool ParentInRootA(string parent)
        {
            if (folders.Profile is null || CanonicalPath.Depth(folders.Profile, parent) < 1)
            {
                return false;
            }

            return !folders.InAppData(parent) || CanonicalContext.Inside(folders.LocalTemp, parent) || CanonicalContext.Inside(folders.CrashDumps, parent);
        }

        // (b): on the profile's volume, inside a top folder the user owns, with every folder down to the target owned by the
        // user (the Kit's owner facts; an unknown owner is not the user's). The target itself must be owned when it is a
        // folder (owner decision 2026-10-17); a destination's parent always is a folder. Never Users, a system location or
        // AppData. For a delete, at least one component inside the top folder.
        private bool InRootB(string path, Ambysto.Kit.Safety.ItemFacts facts, bool itemIsTheTarget)
        {
            if (folders.VolumeRoot is null || facts.VolumeSerial != facts.SystemVolumeSerial)
            {
                return false;
            }

            var depth = CanonicalPath.Depth(folders.VolumeRoot, path);
            if (depth < (itemIsTheTarget ? 2 : 1) || folders.InAppData(path) || CanonicalContext.Inside(folders.Users, path)
                || (folders.Profile is not null && CanonicalPath.Depth(folders.Profile, path) >= 0))
            {
                return false;
            }

            foreach (var system in folders.System)
            {
                if (CanonicalContext.Inside(system, path))
                {
                    return false;
                }
            }

            // Owners run from the target up to the volume root; every one but the volume root's must be the user's, except
            // the target itself when it is a file (it only has to be readable, which the Kit already required).
            var owners = facts.Owners;
            if (owners.Count < 2)
            {
                return false;
            }

            for (var i = 0; i < owners.Count - 1; i++)
            {
                var isTarget = i == 0;
                if (isTarget && itemIsTheTarget && !IsFolder(path))
                {
                    continue;
                }

                if (!owners[i].OwnedByCurrentUser)
                {
                    return false;
                }
            }

            return true;
        }

        // A destination's parent in a leftover folder (the folder itself or below), never in Startup, which lies inside
        // %APPDATA% and is excluded from every module-bound root (docs/spec/05).
        private bool InLeftoverFolder(string parent) =>
            (CanonicalContext.Inside(folders.Roaming, parent) || CanonicalContext.Inside(folders.Local, parent)) && !InStartup(parent);

        private bool InsideLeftoverFolder(string path) =>
            (IsInsideContentsOf(folders.Roaming, path) || IsInsideContentsOf(folders.Local, path)) && !InStartup(path);

        private bool InStartup(string path) => startup is not null && CanonicalContext.Inside(startup, path);

        private bool InStartMenuPrograms(string parent) =>
            startMenuPrograms is not null && CanonicalContext.Inside(startMenuPrograms, parent)
            && !InStartup(parent);

        private static bool IsFolder(string path)
        {
            try
            {
                // Directory.Exists answers false on an error; the attributes throw, so an unknown kind fails closed below.
                return (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.Directory) != 0;
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true; // unknown kind: judged as a folder, which needs its owner (failing closed)
            }
        }
    }
}
