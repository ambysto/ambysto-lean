using System;
using System.Collections.Generic;
using System.Linq;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>How an app is shown (docs/spec/04 Windows note).</summary>
public enum AppClass
{
    /// <summary>Per-user: "Uninstall…" hands off to Settings.</summary>
    PerUser,

    /// <summary>Per-machine: "needs admin"; the hand-off still names it, and Lean asks for no password.</summary>
    NeedsAdmin,

    /// <summary>A Store (MSIX) app: display-only.</summary>
    StoreApp,
}

/// <summary>One app of the inventory: an uninstall entry or a Store package.</summary>
public sealed record InstalledApp(string DisplayName, string? Publisher, string? Version, string? InstallLocation, AppClass Class, UninstallEntry? Entry, StorePackage? Package)
{
    /// <summary>ASP-16: the entry's <see cref="UninstallEntry.EstimatedSizeBytes"/>; null for a Store app (Lean does not measure package folders).</summary>
    public long? SizeBytes => Entry?.EstimatedSizeBytes;

    /// <summary>ASP-16: the entry's <see cref="UninstallEntry.InstallDate"/>, or the package's <see cref="StorePackage.InstalledDate"/> as a date in local time.</summary>
    public DateOnly? InstalledOn => Entry is not null
        ? Entry.InstallDate
        : Package?.InstalledDate is { } installed ? DateOnly.FromDateTime(installed.ToLocalTime().DateTime) : null;

    /// <summary>ASP-16: the entry's <see cref="UninstallEntry.LastWritten"/>; null for a Store app.</summary>
    public DateTimeOffset? LastUpdated => Entry?.LastWritten;
}

/// <summary>The Uninstall module's inventory (docs/spec/04): registered uninstallers of both hives and both views,
/// system components and entries without a display name hidden, Store apps display-only. Reads only.</summary>
public static class UninstallInventory
{
    /// <summary>This user's inventory, read from the real source.</summary>
    public static IReadOnlyList<InstalledApp> Read() => Read(new RegistryUninstallSource());

    /// <summary>The inventory from <paramref name="source"/>, sorted by display name.</summary>
    public static IReadOnlyList<InstalledApp> Read(IUninstallSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var apps = new List<InstalledApp>();
        foreach (var entry in source.ReadEntries())
        {
            // Windows does not list system components or entries without a display name either.
            if (entry.SystemComponent || string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                continue;
            }

            apps.Add(FromEntry(entry));
        }

        apps.AddRange(StoreApps(source));

        // OrderBy is stable: equal names keep the source order (entries, then packages).
        return apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every app that could own a folder (docs/spec/04 Windows note): the inventory of <see cref="Read(IUninstallSource)"/>
    /// with nothing hidden, so system components and entries without a display name are included (their
    /// <see cref="InstalledApp.DisplayName"/> is empty). It is what the leftover matcher is given as "every entry the source
    /// lists", never what the user is shown.</summary>
    public static IReadOnlyList<InstalledApp> ReadOwners(IUninstallSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var apps = new List<InstalledApp>();
        foreach (var entry in source.ReadEntries())
        {
            apps.Add(FromEntry(entry));
        }

        apps.AddRange(StoreApps(source));
        return apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static InstalledApp FromEntry(UninstallEntry entry)
    {
        var appClass = entry.Scope == UninstallScope.PerMachine && !entry.InstalledForCurrentUser ? AppClass.NeedsAdmin : AppClass.PerUser;
        return new InstalledApp(entry.DisplayName ?? string.Empty, entry.Publisher, entry.DisplayVersion, entry.InstallLocation, appClass, entry, null);
    }

    private static List<InstalledApp> StoreApps(IUninstallSource source)
    {
        IReadOnlyList<StorePackage> packages;
        try
        {
            packages = source.ReadStorePackages();
        }
        catch (System.IO.IOException)
        {
            packages = []; // the Store apps are display-only: the registered apps are still listed
        }

        var apps = new List<InstalledApp>();
        foreach (var package in packages)
        {
            if (!string.IsNullOrWhiteSpace(package.DisplayName))
            {
                apps.Add(new InstalledApp(package.DisplayName, package.Publisher, null, package.InstallLocation, AppClass.StoreApp, null, package));
            }
        }

        return apps;
    }
}
