using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ambysto.Lean.Core.Paths;

/// <summary>A <see cref="PathContext"/> with every folder in canonical spelling (<see cref="CanonicalPath"/>), made once per
/// context. A folder that cannot be spelled is null or left out, so nothing matches it.</summary>
internal sealed class CanonicalContext
{
    private static readonly ConditionalWeakTable<PathContext, CanonicalContext> Cache = [];

    private CanonicalContext(PathContext context)
    {
        // A profile is a local folder: a network spelling is not one (Stryker triage H04, 2026-10-18), so it admits nothing.
        Profile = CanonicalPath.Of(context.ProfileFolder) is { } profile && !profile.StartsWith(@"\\", global::System.StringComparison.Ordinal) ? profile : null;
        Roaming = CanonicalPath.Of(context.RoamingAppData);
        Local = CanonicalPath.Of(context.LocalAppData);
        VolumeRoot = CanonicalPath.Of(context.ProfileVolumeRoot);
        Users = CanonicalPath.Of(context.UsersFolder);
        AppData = Profile is null ? null : CanonicalPath.Of(Path.Combine(Profile, "AppData"));
        LocalTemp = Local is null ? null : CanonicalPath.Of(Path.Combine(Local, "Temp"));
        CrashDumps = Local is null ? null : CanonicalPath.Of(Path.Combine(Local, "CrashDumps"));
        foreach (var folder in context.SystemFolders)
        {
            if (CanonicalPath.Of(folder) is { } canonical)
            {
                System.Add(canonical);
            }
        }

        foreach (var folder in context.UserProtectedFolders)
        {
            if (CanonicalPath.Of(folder) is { } canonical)
            {
                UserProtected.Add(canonical);
            }
        }

        foreach (var root in context.SyncRoots)
        {
            if (CanonicalPath.Of(root.Path) is { } canonical)
            {
                Sync.Add((canonical, root.ProviderName));
            }
        }

        if (Roaming is not null)
        {
            DeviceBackups.Add(Path.Combine(Roaming, "Apple Computer", "MobileSync", "Backup"));
        }

        if (Profile is not null)
        {
            DeviceBackups.Add(Path.Combine(Profile, "Apple", "MobileSync", "Backup"));
        }
    }

    public string? Profile { get; }

    public string? Roaming { get; }

    public string? Local { get; }

    public string? VolumeRoot { get; }

    public string? Users { get; }

    /// <summary>The profile's own <c>AppData</c> folder (also when the two data folders are redirected elsewhere).</summary>
    public string? AppData { get; }

    public string? LocalTemp { get; }

    public string? CrashDumps { get; }

    public List<string> System { get; } = [];

    public List<(string Path, string Provider)> Sync { get; } = [];

    /// <summary>The user's protected folders (docs/spec/02 Windows note), canonical.</summary>
    public List<string> UserProtected { get; } = [];

    /// <summary>The two places device backups live: iTunes (desktop) under <c>%APPDATA%</c> and Apple Devices (Store)
    /// under the profile (docs/spec/01 Windows notes on hints; intent decision 8).</summary>
    public List<string> DeviceBackups { get; } = [];

    public static CanonicalContext For(PathContext context) => Cache.GetValue(context, c => new CanonicalContext(c));

    /// <summary>True when <paramref name="canonicalPath"/> is, or lies inside, one of the AppData folders.</summary>
    public bool InAppData(string canonicalPath) => Inside(AppData, canonicalPath) || Inside(Roaming, canonicalPath) || Inside(Local, canonicalPath);

    /// <summary>True when <paramref name="canonicalPath"/> is, or lies inside, <paramref name="folder"/>.</summary>
    public static bool Inside(string? folder, string canonicalPath) => folder is not null && CanonicalPath.Depth(folder, canonicalPath) >= 0;
}
