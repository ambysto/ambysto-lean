using System;
using System.IO;
using Ambysto.Lean.Core.Paths;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>A pretend volume in a scratch folder, with the folders the rules and hints are judged against
/// (docs/spec/02 Windows note on allowed roots; <see cref="PathContext"/>): <c>Vol\Users\me</c> is the profile,
/// <c>Vol\Users\me\AppData\…</c> both AppData folders, <c>Vol\Windows</c>, <c>Vol\Program Files</c>,
/// <c>Vol\Program Files (x86)</c> and <c>Vol\ProgramData</c> the system folders, <c>Vol\Users\me\OneDrive</c> a sync root.
/// Every folder is created by (and so owned by) the test user.</summary>
internal sealed class ProfileFixture : IDisposable
{
    private readonly TempFolder temp;

    public ProfileFixture(string name)
    {
        temp = new TempFolder(name);
        VolumeRoot = temp.Folder("Vol");
        UsersFolder = temp.Folder("Vol", "Users");
        Profile = temp.Folder("Vol", "Users", "me");
        OtherProfile = temp.Folder("Vol", "Users", "someone-else");
        RoamingAppData = temp.Folder("Vol", "Users", "me", "AppData", "Roaming");
        LocalAppData = temp.Folder("Vol", "Users", "me", "AppData", "Local");
        temp.Folder("Vol", "Users", "me", "AppData", "Local", "Temp");
        temp.Folder("Vol", "Users", "me", "AppData", "Local", "CrashDumps");
        temp.Folder("Vol", "Users", "me", "AppData", "Local", "Programs");
        Documents = temp.Folder("Vol", "Users", "me", "Documents");
        OneDrive = temp.Folder("Vol", "Users", "me", "OneDrive");
        WindowsFolder = temp.Folder("Vol", "Windows");
        ProgramFiles = temp.Folder("Vol", "Program Files");
        ProgramFilesX86 = temp.Folder("Vol", "Program Files (x86)");
        ProgramData = temp.Folder("Vol", "ProgramData");
        OtherVolume = temp.Folder("OtherVol");
        Context = new PathContext(Profile, RoamingAppData, LocalAppData, VolumeRoot, UsersFolder,
            [WindowsFolder, ProgramFiles, ProgramFilesX86, ProgramData], [new SyncRoot(OneDrive, "OneDrive")]);
    }

    public string VolumeRoot { get; }

    public string UsersFolder { get; }

    public string Profile { get; }

    public string OtherProfile { get; }

    public string RoamingAppData { get; }

    public string LocalAppData { get; }

    public string Documents { get; }

    public string OneDrive { get; }

    public string WindowsFolder { get; }

    public string ProgramFiles { get; }

    public string ProgramFilesX86 { get; }

    public string ProgramData { get; }

    /// <summary>A folder outside the pretend volume (another volume, by path).</summary>
    public string OtherVolume { get; }

    public PathContext Context { get; }

    /// <summary>A path under the profile.</summary>
    public string InProfile(params string[] parts) => Path.Combine([Profile, .. parts]);

    /// <summary>A path under the pretend volume root.</summary>
    public string OnVolume(params string[] parts) => Path.Combine([VolumeRoot, .. parts]);

    /// <summary>Creates a folder at <paramref name="path"/> and returns it.</summary>
    public static string MakeFolder(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file at <paramref name="path"/> and returns it.</summary>
    public static string MakeFile(string path, long length = 4096, int seed = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TempFolder.WriteBytes(path, length, seed);
        return path;
    }

    public void Dispose() => temp.Dispose();
}
