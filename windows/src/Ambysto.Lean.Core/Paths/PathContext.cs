using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ambysto.Lean.Core.Paths;

/// <summary>A cloud-sync root and the provider that syncs it (for the hint "Synced by &lt;provider&gt;: manage it there").</summary>
public sealed record SyncRoot(string Path, string ProviderName);

/// <summary>The folders the rules and hints are judged against. <see cref="Current"/> reads this user's; tests give
/// their own (temp folders), so no rule reads the environment directly.</summary>
public sealed record PathContext(
    string ProfileFolder,
    string RoamingAppData,
    string LocalAppData,
    string ProfileVolumeRoot,
    string UsersFolder,
    IReadOnlyList<string> SystemFolders,
    IReadOnlyList<SyncRoot> SyncRoots)
{
    /// <summary>The user's protected folders (docs/spec/02 Windows note): a path inside or holding one is display-only,
    /// "Protected by you". Empty unless the app sets it from its settings.</summary>
    public IReadOnlyList<string> UserProtectedFolders { get; init; } = [];

    /// <summary>This user's folders: the profile, both AppData folders, the profile's volume, the Users folder, the
    /// system folders (Windows, both Program Files, ProgramData) and the cloud-sync roots.</summary>
    public static PathContext Current()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemFolders = new List<string>();
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && !systemFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                systemFolders.Add(path);
            }
        }

        return new PathContext(
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetPathRoot(profile) ?? profile,
            Path.GetDirectoryName(profile) ?? profile,
            systemFolders,
            SyncRootReader.Read(profile));
    }
}
