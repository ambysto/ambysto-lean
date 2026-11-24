using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>One leftover candidate (docs/spec/04): a folder in <c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c> or
/// <c>%LOCALAPPDATA%\Programs</c> that exactly matches the gone app. <see cref="Selectable"/> is false with a
/// <see cref="Note"/> when it is shown but may not be moved.</summary>
public sealed record LeftoverCandidate(string Path, string Rule, bool Selectable, string? Note);

/// <summary>What counts as a gone app's leftover (docs/spec/04 Windows note; HARD). Exact, never substring: the app's
/// install location when it is at least one component inside one of the three folders (never one of them itself, never
/// <c>%USERPROFILE%</c>), <c>&lt;Publisher&gt;\&lt;DisplayName&gt;</c> and <c>&lt;DisplayName&gt;</c> exactly. A generic
/// display name is never matched; a vendor folder another installed app may use is never offered; a candidate that two
/// installed or just-removed entries could own belongs to nobody. Never-touch always wins (the gate decides at move
/// time). Reads names only; nothing is opened for data.</summary>
public static class LeftoverMatcher
{
    /// <summary>The rule name of a candidate that is the app's install location.</summary>
    public const string InstallLocationRule = "installLocation";

    /// <summary>The rule name of a <c>&lt;Publisher&gt;\&lt;DisplayName&gt;</c> candidate.</summary>
    public const string PublisherAndNameRule = "publisherAndName";

    /// <summary>The rule name of a <c>&lt;DisplayName&gt;</c> candidate.</summary>
    public const string DisplayNameRule = "displayName";

    /// <summary>Display names that are generic folder names: never matched as <c>&lt;DisplayName&gt;</c> or as the name
    /// part of <c>&lt;Publisher&gt;\&lt;DisplayName&gt;</c>, for the gone app or for any other app.</summary>
    internal static readonly IReadOnlySet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Tmp", "Programs", "Program Files", "Packages", "Microsoft", "Windows", "WindowsApps", "Apps", "Application Data",
        "Local", "LocalLow", "Roaming", "AppData", "Common Files", "Cache", "Caches", "Logs", "Log", "Data", "Config", "Settings",
        "Update", "Updates", "Updater", "Installer", "Setup", "Uninstall", "Downloads", "Documents", "Desktop", "Default", "User Data",
        "Users", "Public", "VirtualStore", "CrashDumps", "Crashpad", "D3DSCache", "History", "Publishers", "Comms", "Low",
        "ConnectedDevicesPlatform", "PlaceholderTileLogoFolder", "SquirrelTemp", "Squirrel", "Ambysto",
    };

    /// <summary>The leftovers of <paramref name="gone"/> (an app the watcher found gone), judged against the apps still in
    /// <paramref name="inventory"/> and the other just-removed apps in <paramref name="justRemoved"/>. The caller passes
    /// every entry it knows of in <paramref name="inventory"/>, hidden ones (system components, entries without a display
    /// name) included, since a hidden entry can own a folder too. A candidate that overlaps (is, holds, or lies inside)
    /// any path another app could own, or that is or holds another app's vendor folder, is not offered; an other app's
    /// install location that is a whole leftover folder or a drive therefore blocks every candidate there (failing
    /// closed).</summary>
    public static IReadOnlyList<LeftoverCandidate> Match(InstalledApp gone, IReadOnlyList<InstalledApp> inventory,
        IReadOnlyList<InstalledApp> justRemoved, PathContext context)
    {
        ArgumentNullException.ThrowIfNull(gone);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(justRemoved);
        ArgumentNullException.ThrowIfNull(context);

        var folders = LeftoverFolders(context);
        var profile = CanonicalPath.Of(context.ProfileFolder);
        if (folders.Count == 0 || profile is null)
        {
            return [];
        }

        // Every other app that could own a folder: the installed ones and the other just-removed ones (gone itself
        // excluded by reference; a second entry for the same app is another owner, so a tie belongs to nobody).
        var others = inventory.Concat(justRemoved).Where(app => !ReferenceEquals(app, gone)).ToList();
        var claimed = new List<string>();
        var vendorFolders = new List<string>();
        foreach (var other in others)
        {
            claimed.AddRange(OwnPaths(other, folders));
            if (Component(other.Publisher) is { } publisher)
            {
                vendorFolders.AddRange(folders.Select(folder => folder + "\\" + publisher));
            }
        }

        var result = new List<LeftoverCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, rule) in Candidates(gone, folders))
        {
            if (!seen.Add(path)
                || string.Equals(path, profile, StringComparison.OrdinalIgnoreCase)
                || folders.Any(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase))
                || claimed.Any(other => Overlaps(path, other))
                || vendorFolders.Any(vendor => CanonicalPath.Depth(path, vendor) >= 0)
                || !Directory.Exists(path))
            {
                continue;
            }

            // The user's protected folders (docs/spec/02 Windows note): shown, never selectable.
            if (context.UserProtectedFolders.Any(folder => CanonicalPath.Of(folder) is { } p && Overlaps(path, p)))
            {
                result.Add(new LeftoverCandidate(path, rule, false, Copy.Text.HintProtectedByYou));
                continue;
            }

            var synced = context.SyncRoots.FirstOrDefault(root => CanonicalPath.Of(root.Path) is { } r && Overlaps(path, r));
            result.Add(synced is null
                ? new LeftoverCandidate(path, rule, true, null)
                : new LeftoverCandidate(path, rule, false, Copy.Text.HintSynced(synced.ProviderName)));
        }

        return result;
    }

    // %APPDATA%, %LOCALAPPDATA% and %LOCALAPPDATA%\Programs, canonical.
    private static List<string> LeftoverFolders(PathContext context)
    {
        var folders = new List<string>();
        foreach (var folder in new[] { context.RoamingAppData, context.LocalAppData, Path.Join(context.LocalAppData, "Programs") })
        {
            if (CanonicalPath.Of(folder) is { } canonical && !folders.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(canonical);
            }
        }

        return folders;
    }

    // The gone app's candidates, in the order they are shown: the install location (at least one component inside one of
    // the folders), then <Publisher>\<DisplayName> and <DisplayName> in each folder.
    private static IEnumerable<(string Path, string Rule)> Candidates(InstalledApp app, List<string> folders)
    {
        // A location whose own name is generic (Temp, Microsoft, …) is never offered either (interim review MINOR 3).
        if (Location(app.InstallLocation) is { } location && folders.Any(folder => CanonicalPath.Depth(folder, location) >= 1)
            && !GenericNames.Contains(Path.GetFileName(location)))
        {
            yield return (location, InstallLocationRule);
        }

        var name = NameComponent(app.DisplayName);
        if (name is null)
        {
            yield break;
        }

        if (Component(app.Publisher) is { } publisher)
        {
            foreach (var folder in folders)
            {
                yield return (folder + "\\" + publisher + "\\" + name, PublisherAndNameRule);
            }
        }

        foreach (var folder in folders)
        {
            yield return (folder + "\\" + name, DisplayNameRule);
        }
    }

    // Everything another app could own: its install location wherever it is, and its own name candidates.
    private static IEnumerable<string> OwnPaths(InstalledApp app, List<string> folders)
    {
        if (Location(app.InstallLocation) is { } location)
        {
            yield return location;
        }

        foreach (var (path, rule) in Candidates(app, folders))
        {
            if (rule != InstallLocationRule)
            {
                yield return path;
            }
        }
    }

    // The same folder, or one inside the other.
    private static bool Overlaps(string a, string b) => CanonicalPath.Depth(a, b) >= 0 || CanonicalPath.Depth(b, a) >= 0;

    // An install location as a canonical, fully qualified path (quotes and spaces around it trimmed); null when empty or
    // not a usable path (an empty location matches nothing).
    private static string? Location(string? text)
    {
        var trimmed = text?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed) ? null : CanonicalPath.Of(trimmed);
    }

    // A display name usable as one folder name and not generic.
    private static string? NameComponent(string? text) => Component(text) is { } name && !GenericNames.Contains(name) ? name : null;

    // Text usable as exactly one folder name: no separator, no invalid character, not "." or "..", no trailing dot or space.
    private static string? Component(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim() || text is "." or ".." || text.EndsWith('.')
            || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || text.Contains('\\') || text.Contains('/'))
        {
            return null;
        }

        return text;
    }
}
