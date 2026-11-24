using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Paths;

/// <summary>Finds the cloud-sync roots of this user, for the hint "Synced by &lt;provider&gt;: manage it there" (docs/spec/01
/// Windows notes). The provider is named by its registration in the Explorer sync-root list, not by the folder name.
/// Registry reads only.</summary>
internal static class SyncRootReader
{
    private const string ManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    // Providers that sync a folder under the profile and, in some set-ups, do not register it (the Kit's never-touch list
    // holds them all anyway; this only names them in the hint).
    private static readonly (string Folder, string Provider)[] KnownProfileFolders =
    [
        ("OneDrive", "OneDrive"),
        ("Dropbox", "Dropbox"),
        ("Google Drive", "Google Drive"),
        ("My Drive", "Google Drive"),
        ("Box", "Box"),
        ("Box Sync", "Box"),
        ("pCloud Drive", "pCloud"),
        ("iCloudDrive", "iCloud"),
        ("iCloud Drive", "iCloud"),
    ];

    public static IReadOnlyList<SyncRoot> Read(string profileFolder)
    {
        var roots = new List<SyncRoot>();
        AddRegistered(roots);
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            Add(roots, Environment.GetEnvironmentVariable(variable), "OneDrive");
        }

        foreach (var (folder, provider) in KnownProfileFolders)
        {
            var path = Path.Combine(profileFolder, folder);
            if (Directory.Exists(path))
            {
                Add(roots, path, provider);
            }
        }

        try
        {
            if (Directory.Exists(profileFolder))
            {
                foreach (var business in Directory.EnumerateDirectories(profileFolder, "OneDrive - *"))
                {
                    Add(roots, business, "OneDrive");
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // An unreadable profile names no root here; the Kit's gate still refuses these folders at move time.
        }

        return roots;
    }

    private static void AddRegistered(List<SyncRoot> roots)
    {
        try
        {
            // The registrations live in the machine hive, one UserSyncRoots value per user SID (measured 2026-10-17: the
            // user hive has no such key).
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null)
            {
                return;
            }

            using var manager = Registry.LocalMachine.OpenSubKey(ManagerKey);
            if (manager is null)
            {
                return;
            }

            foreach (var name in manager.GetSubKeyNames())
            {
                // The key is "<Provider>!<SID>!<Account>"; the provider is the part before the first '!'.
                var separator = name.IndexOf('!', StringComparison.Ordinal);
                var provider = separator > 0 ? name[..separator] : name;
                using var userRoots = manager.OpenSubKey(name + @"\UserSyncRoots");
                if (userRoots is null)
                {
                    continue;
                }

                Add(roots, userRoots.GetValue(sid) as string, provider);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable registration just names no root; the Kit's gate still refuses these folders at move time.
        }
    }

    private static void Add(List<SyncRoot> roots, string? path, string provider)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 0 || roots.Exists(r => string.Equals(r.Path.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        roots.Add(new SyncRoot(trimmed, provider));
    }
}
