using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Uninstall;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>One installed app's space (ASP-20 phase C, read-only): its install folder's size (measured, or the uninstall entry's
/// estimate, or unknown) plus the Guide rows that belong to it, matched exactly.</summary>
public sealed record AppSpaceRow(string Name, string? Publisher, string? InstallFolder, long? InstallBytes, bool InstallEstimated,
    IReadOnlyList<GuideRow> CatalogRows, long TotalBytes);

/// <summary>Space by app (plan "Phase C"): one row per installed app, largest first. Reads and measures only.</summary>
public static class SpaceByApp
{
    /// <summary>The rows for <paramref name="apps"/>, with the <paramref name="guideRows"/> that belong to each.</summary>
    public static Task<IReadOnlyList<AppSpaceRow>> ReadAsync(IReadOnlyList<InstalledApp> apps, IReadOnlyList<GuideRow> guideRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(guideRows);
        return Task.Run<IReadOnlyList<AppSpaceRow>>(() => Read(apps, guideRows, cancellationToken), cancellationToken);
    }

    private static List<AppSpaceRow> Read(IReadOnlyList<InstalledApp> apps, IReadOnlyList<GuideRow> guideRows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalogRows = AttachCatalogRows(apps, guideRows);
        var claimed = new List<(string Folder, long Owned)>();
        var rows = new List<AppSpaceRow>();
        for (var i = 0; i < apps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var app = apps[i];
            var (folder, bytes, estimated) = Install(app, claimed, cancellationToken);
            var attached = catalogRows[i] ?? [];
            var total = (bytes ?? 0) + attached.Sum(r => r.Bytes);
            if (total > 0)
            {
                rows.Add(new AppSpaceRow(app.DisplayName, app.Publisher, folder, bytes, estimated, attached, total));
            }
        }

        return [.. rows.OrderByDescending(r => r.TotalBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>Each Guide row goes to the first app whose uninstall entry key is exactly the row's key (the catalog entry's for
    /// a browser or CapCut, the row's own for an updater); a row without a key, or whose key is not installed, goes to nobody.
    /// A Store app has no uninstall entry and never gets one.</summary>
    private static List<GuideRow>?[] AttachCatalogRows(IReadOnlyList<InstalledApp> apps, IReadOnlyList<GuideRow> guideRows)
    {
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < apps.Count; i++)
        {
            if (apps[i].Class != AppClass.StoreApp && apps[i].Entry is { } entry)
            {
                owner.TryAdd(entry.KeyName, i);
            }
        }

        var attached = new List<GuideRow>?[apps.Count];
        foreach (var row in guideRows)
        {
            if ((row.Entry.UninstallKeyName ?? row.UninstallKeyName) is { } key && owner.TryGetValue(key, out var index))
            {
                (attached[index] ??= []).Add(row);
            }
        }

        return attached;
    }

    /// <summary>The install folder as shown, its bytes and whether they are the entry's estimate. A plain, full, local folder
    /// is measured; a folder already counted for an earlier app (the same folder, or one inside it) counts nothing; a folder
    /// that holds an earlier app's folder counts only the rest. Anything else falls back to the entry's estimate (also for a
    /// folder that measures 0); a Store app is never measured.</summary>
    private static (string? Folder, long? Bytes, bool Estimated) Install(InstalledApp app, List<(string Folder, long Owned)> claimed, CancellationToken cancellationToken)
    {
        var location = string.IsNullOrWhiteSpace(app.InstallLocation) ? null : app.InstallLocation.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(location))
        {
            location = null;
        }

        if (app.Class == AppClass.StoreApp)
        {
            return (location, null, false);
        }

        if (GuideRows.FullLocalPath(location ?? string.Empty) is { } full && IsMeasurable(full) && CacheFolder.IsPlainFolder(full))
        {
            var folder = TrimSeparator(full);
            if (claimed.Any(c => IsSameOrInside(folder, c.Folder)))
            {
                return (folder, null, false); // its bytes are counted for the first app
            }

            var measured = CacheFolder.Measure(folder, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); // the walker returns the size so far when cancelled
            var owned = Math.Max(0, measured - claimed.Where(c => IsSameOrInside(c.Folder, folder)).Sum(c => c.Owned));
            claimed.Add((folder, owned));
            if (measured > 0 || app.SizeBytes is null)
            {
                return (folder, owned, false);
            }
        }

        return app.SizeBytes is { } estimate ? (location, estimate, true) : (location, null, false);
    }

    /// <summary>False for a volume root, a folder fewer than two components below its root, and the shared folders an installer
    /// may name (Program Files, Program Files (x86), ProgramData, Windows, Users, the profile, AppData, Roaming, LocalAppData,
    /// LocalAppData\Programs): such a folder holds more than one app, so its size is no app's. Full paths, any case.</summary>
    private static bool IsMeasurable(string full)
    {
        var folder = TrimSeparator(full);
        var root = Path.GetPathRoot(folder) ?? string.Empty;
        if (folder.Length <= root.Length || folder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return false;
        }

        return !SharedFolders().Any(shared => string.Equals(shared, folder, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SharedFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
        };
        if (profile.Length > 0)
        {
            candidates.Add(Path.GetDirectoryName(profile)); // Users
            candidates.Add(Path.Combine(profile, "AppData"));
        }

        if (local.Length > 0)
        {
            candidates.Add(Path.Combine(local, "Programs"));
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && GuideRows.FullLocalPath(candidate) is { } full)
            {
                yield return TrimSeparator(full);
            }
        }
    }

    private static string TrimSeparator(string path) =>
        path.Length > 3 && path[^1] == '\\' ? path.TrimEnd('\\') : path;

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (whole names, any case).</summary>
    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && (folder[^1] == '\\' || path[folder.Length] == '\\'));
}
