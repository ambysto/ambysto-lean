using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>"App is gone" (docs/spec/04 Windows note; intent Outcome 4): the entry has been absent from both hives and
/// both views, and no Store package and no other entry names the same install location or display name, for
/// <see cref="SettleTime"/> (checked every <see cref="CheckInterval"/>), and no process runs from its install location.
/// Only then are its leftovers offered (phase B).</summary>
public sealed class AppGoneWatcher
{
    /// <summary>10 seconds.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <summary>2 seconds.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly UninstallEntry entry;
    private readonly IUninstallSource source;
    private readonly TimeProvider time;
    private readonly string? location;
    private readonly string? displayName;
    private readonly object gate = new();
    private long? firstAbsence; // a monotonic timestamp (TimeProvider.GetTimestamp): a wall-clock step cannot shorten the settle time
    private bool gone;

    /// <summary>Watches <paramref name="entry"/> through <paramref name="source"/>, with <paramref name="time"/> as the
    /// clock.</summary>
    public AppGoneWatcher(UninstallEntry entry, IUninstallSource source, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        this.entry = entry;
        this.source = source;
        this.time = time;
        location = NormalLocation(entry.InstallLocation);
        displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? null : entry.DisplayName.Trim();
    }

    /// <summary>Reads the source once, now, and returns <see cref="IsGone"/>. Any sighting of the app restarts the
    /// settle time.</summary>
    public bool Check()
    {
        // Read outside the lock (the source may be slow); decide inside it. A source that cannot answer is a sighting:
        // "app is gone" is never concluded from a failed read.
        bool sighted;
        try
        {
            sighted = IsSighted();
        }
#pragma warning disable CA1031 // failing toward "still installed": any read failure of the source counts as a sighting
        catch (Exception)
        {
            sighted = true;
        }
#pragma warning restore CA1031
        var now = time.GetTimestamp();
        lock (gate)
        {
            if (sighted)
            {
                firstAbsence = null;
                gone = false;
                return false;
            }

            firstAbsence ??= now;
            gone = time.GetElapsedTime(firstAbsence.Value, now) >= SettleTime;
            return gone;
        }
    }

    /// <summary>True once the rule above holds.</summary>
    public bool IsGone
    {
        get
        {
            lock (gate)
            {
                return gone;
            }
        }
    }

    /// <summary>Checks every <see cref="CheckInterval"/> until the app is gone (true) or the token is cancelled (false).</summary>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Check())
                {
                    return true;
                }

                await Task.Delay(CheckInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return false;
    }

    // Anything that says the app is still there. When in doubt, it is still there: a wrong "gone" would offer the
    // leftovers of an app that still runs, a wrong "installed" only waits longer.
    private bool IsSighted()
    {
        foreach (var other in source.ReadEntries())
        {
            // The same entry in either hive and either view, hidden or not.
            if (string.Equals(other.KeyName, entry.KeyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Any other entry, hidden ones included (system components, entries without a display name), that names the
            // same install location or display name.
            if (SameLocation(other.InstallLocation) || SameName(other.DisplayName))
            {
                return true;
            }
        }

        foreach (var package in source.ReadStorePackages())
        {
            if (SameLocation(package.InstallLocation) || SameName(package.DisplayName))
            {
                return true;
            }
        }

        if (location is not null)
        {
            foreach (var image in source.ReadProcessImagePaths())
            {
                if (NormalLocation(image) is { } path && IsAtOrInside(location, path))
                {
                    return true; // the app, or its uninstaller, still runs from the folder
                }
            }
        }

        return false;
    }

    private bool SameLocation(string? other) =>
        location is not null && NormalLocation(other) is { } path && string.Equals(path, location, StringComparison.OrdinalIgnoreCase);

    // Display names compare exactly, never as a substring or prefix; case is ignored (failing toward "still installed").
    private bool SameName(string? other) =>
        displayName is not null && !string.IsNullOrWhiteSpace(other) && string.Equals(other.Trim(), displayName, StringComparison.OrdinalIgnoreCase);

    // A process runs from the location when its image is the location itself (some installers record the exe's own path as
    // the install location; owner answer 3, plan #2173, hard review 2026-10-18) or lies below it by whole components.
    private static bool IsAtOrInside(string folder, string path) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (path.Length == folder.Length || path[folder.Length] == '\\');

    // A location as a Windows path: separators unified, no trailing separator, no extended prefix. An empty location
    // matches nothing (spec 04).
    private static string? NormalLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Some installers (NSIS, Inno Setup) write the location in quotes: "C:\Program Files\X".
        var normal = path.Trim().Trim('"').Trim().Replace('/', '\\');
        if (normal.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normal = normal[4..];
        }

        normal = normal.TrimEnd('\\');
        return normal.Length == 0 ? null : normal;
    }
}
