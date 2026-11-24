using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>The real <see cref="IUninstallSource"/> (docs/spec/04 Windows note): the <c>Uninstall</c> keys of both hives
/// in both registry views, this user's Store packages and the image paths of the running processes. It opens every key
/// read-only and changes nothing; what it cannot read it leaves out.</summary>
internal sealed partial class RegistryUninstallSource : IUninstallSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<UninstallEntry> ReadEntries()
    {
        var entries = new List<UninstallEntry>();

        // HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall is shared between the WOW64 views: reading it through
        // Registry32 reads the same key again. So the user's hive is read once, in the default view; the machine hive
        // has a separate key per view.
        ReadHive(entries, RegistryHive.CurrentUser, RegistryView.Default, UninstallScope.PerUser, RegistryViewKind.Bits64);
        ReadHive(entries, RegistryHive.LocalMachine, RegistryView.Registry64, UninstallScope.PerMachine, RegistryViewKind.Bits64);
        ReadHive(entries, RegistryHive.LocalMachine, RegistryView.Registry32, UninstallScope.PerMachine, RegistryViewKind.Bits32);

        // Safety net: the same key of the same hive and view with the same values is one entry, however it was reached.
        // The machine hive's two views are separate keys, even when their names and values match (Stryker triage H22,
        // 2026-10-18: without the view in the key, a 32-bit entry equal to a 64-bit one was dropped).
        var seen = new HashSet<(UninstallScope, RegistryViewKind, string, string?, string?)>();
        entries.RemoveAll(e => !seen.Add((e.Scope, e.View, e.KeyName, e.DisplayName, e.InstallLocation)));
        return entries;
    }

    public IReadOnlyList<StorePackage> ReadStorePackages()
    {
        // Windows.Management.Deployment is part of the Windows SDK projection: no package reference. Asked for this
        // the current user, it needs no administrator. Frameworks, resource packages and system-signed packages are
        // components, not apps, so they are left out (as Settings leaves them out).
        var packages = new List<StorePackage>();
        try
        {
            // The empty user means the current user.
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                    {
                        continue;
                    }

                    packages.Add(new StorePackage(package.Id.FullName, package.DisplayName, package.PublisherDisplayName, package.InstalledPath) { InstalledDate = ReadInstalledDate(package) });
                }
                catch (Exception exception) when (IsStoreReadFailure(exception))
                {
                    // One package that cannot be read is an error too: it could be the watched app.
                    throw new System.IO.IOException("a Store package could not be read", exception);
                }
            }
        }
        catch (Exception exception) when (IsStoreReadFailure(exception))
        {
            // The package catalog cannot be read (or stopped being readable while it was listed): an error, never an
            // empty list. The inventory then shows the registered apps without the Store ones; "app is gone" treats it
            // as a sighting.
            throw new System.IO.IOException("the Store packages could not be read", exception);
        }

        return packages;
    }

    // The earliest install date either source believes (an installer's zero or default date reads as 1601 or 0001).
    private static readonly DateOnly EarliestInstall = new(1980, 1, 1);

    /// <summary>ASP-16: <paramref name="value"/> when it lies between 1980-01-01 and <paramref name="now"/> plus 1 day (an installer's or
    /// package's wrong clock is not shown), else null. The same bounds as <c>InstallDate</c>.</summary>
    internal static DateTimeOffset? PlausibleInstalledDate(DateTimeOffset? value, DateTimeOffset now) =>
        value is { } v && v >= new DateTimeOffset(EarliestInstall.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) && v <= now.AddDays(1) ? v : null;

    // ASP-16: the date a package was installed; null when reading it throws or it is implausible (the package is still listed).
    private static DateTimeOffset? ReadInstalledDate(Windows.ApplicationModel.Package package)
    {
        try
        {
            return PlausibleInstalledDate(package.InstalledDate, DateTimeOffset.Now);
        }
        catch (Exception exception) when (IsStoreReadFailure(exception))
        {
            return null;
        }
    }

    // Every process's image path, services and other users' processes included: the system's process-id query names
    // the image of any process without opening it (measured 2026-10-17 as the standard user: 146 of 147 processes, the
    // Idle process being the one without an image), so a per-machine app's service still counts as running from its
    // folder ("app is gone" fails toward "still installed"). A process the query cannot name is read through a
    // limited-access handle instead; one neither way can name is left out.
    public unsafe IReadOnlyList<string> ReadProcessImagePaths()
    {
        var paths = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new System.IO.IOException("Running process inventory is unavailable", exception);
        }

        var devices = DeviceToDrive();
        var buffer = new char[32768];
        foreach (var process in processes)
        {
            try
            {
                var id = process.Id;
                if (id == 0)
                {
                    continue; // the Idle process has no image
                }

                if (ImageByProcessId(id, devices) is { } byId)
                {
                    paths.Add(byId);
                    continue;
                }

                string? byHandle = null;
                var handle = OpenProcess(ProcessQueryLimitedInformation, 0, (uint)id);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var size = (uint)buffer.Length;
                        fixed (char* pointer = buffer)
                        {
                            if (QueryFullProcessImageName(handle, 0, pointer, ref size) != 0 && size > 0)
                            {
                                byHandle = new string(buffer, 0, (int)size);
                            }
                        }
                    }
                    finally
                    {
                        _ = CloseHandle(handle);
                    }
                }

                if (byHandle is not null)
                {
                    paths.Add(byHandle);
                }
                else if (!IsPseudoProcess(process.Id, process.ProcessName) && !HasEnded(process))
                {
                    // A running process neither way can name could run from the watched folder: an error, never left
                    // out ("app is gone" treats it as a sighting).
                    throw new System.IO.IOException("the image of process " + id + " could not be read");
                }
            }
            catch (InvalidOperationException)
            {
                // The process ended while it was read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return paths;
    }

    // A process this user cannot even ask about counts as still running (failing toward "still installed").
    private static bool HasEnded(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Kernel pseudo-processes have no image file (System, Registry, Secure System, Memory Compression), nor have the
    // hypervisor's minimal processes that hold a virtual machine's memory: vmmem (Hyper-V, Windows Sandbox) and vmmemWSL (WSL 2).
    // Probe 2026-10-22, WSL 3.0.3 running, standard user: neither the system query nor a limited handle names vmmemWSL's
    // image, so without it "app is gone" never fired while WSL ran. Only a process that neither way can name is judged by
    // name here; a real program called vmmem.exe has an image that the reads name, so it is still seen.
    internal static bool IsPseudoProcess(int id, string name) =>
        id == 4 || name is "System" or "Registry" or "Secure System" or "Memory Compression" or "MemCompression" or "vmmem" or "vmmemWSL";

    private const int SystemProcessIdInformation = 88;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIdInformation
    {
        public IntPtr ProcessId;
        public UnicodeString ImageName;
    }

    // The image of process <id> as a DOS path (C:\..., or \\server\share\... for a network image); null when the query or
    // the path conversion fails.
    private static unsafe string? ImageByProcessId(int id, Dictionary<string, string> devices)
    {
        var bytes = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                var info = new ProcessIdInformation
                {
                    ProcessId = id,
                    ImageName = new UnicodeString { Length = 0, MaximumLength = (ushort)Math.Min(bytes, ushort.MaxValue - 1), Buffer = buffer },
                };
                var status = NtQuerySystemInformation(SystemProcessIdInformation, &info, (uint)sizeof(ProcessIdInformation), out _);
                if (status == StatusInfoLengthMismatch && info.ImageName.MaximumLength > bytes)
                {
                    bytes = info.ImageName.MaximumLength;
                    continue;
                }

                if (status < 0 || info.ImageName.Length == 0)
                {
                    return null;
                }

                var ntPath = new string((char*)buffer, 0, info.ImageName.Length / 2);
                return ToDosPath(ntPath, devices);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    // \Device\HarddiskVolume3\x → C:\x through the drive letters' device names; \Device\Mup\server\share → \\server\share.
    private static string? ToDosPath(string ntPath, Dictionary<string, string> devices)
    {
        const string mup = @"\Device\Mup\";
        if (ntPath.StartsWith(mup, StringComparison.OrdinalIgnoreCase))
        {
            // \Device\Mup\;LanmanRedirector\;Z:0000…\server\share\x: the redirector's own components start with ';'.
            var parts = ntPath[mup.Length..].Split('\\');
            var first = 0;
            while (first < parts.Length && parts[first].StartsWith(';'))
            {
                first++;
            }

            return parts.Length - first >= 2 ? @"\\" + string.Join('\\', parts[first..]) : null;
        }

        foreach (var (device, drive) in devices)
        {
            if (ntPath.Length > device.Length && ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase) && ntPath[device.Length] == '\\')
            {
                return drive + ntPath[device.Length..];
            }
        }

        return null;
    }

    private static unsafe Dictionary<string, string> DeviceToDrive()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var target = new char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = letter + ":";
            fixed (char* pointer = target)
            {
                var length = QueryDosDevice(drive, pointer, (uint)target.Length);
                if (length > 0)
                {
                    var device = new string(target, 0, Array.IndexOf(target, '\0') is var nul && nul >= 0 ? nul : (int)length);
                    map.TryAdd(device, drive);
                }
            }
        }

        // Volumes mounted in a folder without a drive letter: their device by the volume name, their first mount folder.
        var volume = new char[64];
        var mounts = new char[4096];
        fixed (char* volumePointer = volume)
        {
            var find = FindFirstVolume(volumePointer, (uint)volume.Length);
            if (find != new IntPtr(-1))
            {
                try
                {
                    do
                    {
                        var guidPath = new string(volume, 0, Array.IndexOf(volume, '\0') is var end && end >= 0 ? end : volume.Length); // \\?\Volume{…}\
                        var name = guidPath.TrimEnd('\\')[4..];
                        fixed (char* targetPointer = target)
                        fixed (char* mountsPointer = mounts)
                        {
                            var length = QueryDosDevice(name, targetPointer, (uint)target.Length);
                            if (length > 0 && GetVolumePathNamesForVolumeName(guidPath, mountsPointer, (uint)mounts.Length, out _) != 0)
                            {
                                var device = new string(target, 0, Array.IndexOf(target, '\0') is var nul && nul >= 0 ? nul : (int)length);
                                var mount = new string(mounts, 0, Array.IndexOf(mounts, '\0') is var mend && mend >= 0 ? mend : 0).TrimEnd('\\');
                                if (mount.Length > 0)
                                {
                                    map.TryAdd(device, mount);
                                }
                            }
                        }
                    }
                    while (FindNextVolume(find, volumePointer, (uint)volume.Length) != 0);
                }
                finally
                {
                    _ = FindVolumeClose(find);
                }
            }
        }

        return map;
    }

    // A DWORD or QWORD that is not zero, or a text that reads as a non-zero integer, marks a system component.
    private static bool IsSet(object? value) => value switch
    {
        int number => number != 0,
        long number => number != 0,
        string text => long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) && number != 0,
        _ => false,
    };

    private static bool IsStoreReadFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.IO.IOException or System.Security.SecurityException;

    private static void ReadHive(List<UninstallEntry> entries, RegistryHive hive, RegistryView view, UninstallScope scope, RegistryViewKind kind)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is not null)
                    {
                        entries.Add(new UninstallEntry(
                            name,
                            scope,
                            kind,
                            key.GetValue("DisplayName") as string,
                            key.GetValue("Publisher") as string,
                            key.GetValue("DisplayVersion") as string,
                            key.GetValue("InstallLocation") as string,
                            IsSet(key.GetValue("SystemComponent")))
                        {
                            InstalledForCurrentUser = hive == RegistryHive.LocalMachine && IsSet(key.GetValue("WindowsInstaller")) && IsUserInstallerProduct(baseKey, name),
                            EstimatedSizeBytes = ReadEstimatedSizeBytes(key),
                            InstallDate = ReadInstallDate(key),
                            LastWritten = ReadLastWritten(key),
                        });
                    }
                }
                catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
                {
                    // A key that cannot be read is still reported by its name, as a hidden entry with no values: the
                    // inventory does not list it, and "app is gone" still sees the key (a key read wrongly as absent
                    // would make an app look gone).
                    entries.Add(new UninstallEntry(name, scope, kind, null, null, null, null, true) { ValuesReadable = false });
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // A hive or view that cannot be read is an error, never an empty list: "app is gone" treats it as a sighting.
            throw new System.IO.IOException("the uninstall list of " + hive + " (" + view + ") could not be read", exception);
        }
    }

    // ASP-16: the EstimatedSize DWORD (KB, read as unsigned) × 1024; null when missing, not a DWORD, 0 or unreadable.
    private static long? ReadEstimatedSizeBytes(RegistryKey key)
    {
        try
        {
            return key.GetValue("EstimatedSize") is int kilobytes && kilobytes != 0 ? (long)(uint)kilobytes * 1024 : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    // ASP-16: InstallDate is exactly 8 ASCII digits, yyyyMMdd; a date before 1980 or more than 1 day after today (an
    // installer's wrong clock) is null, as is any other text.
    private static DateOnly? ReadInstallDate(RegistryKey key)
    {
        try
        {
            if (key.GetValue("InstallDate") is not string text || text.Length != 8 || !text.All(c => c is >= '0' and <= '9'))
            {
                return null;
            }

            if (!DateOnly.TryParseExact(text, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            {
                return null;
            }

            return date < EarliestInstall || date > DateOnly.FromDateTime(DateTime.Now).AddDays(1) ? null : date;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    // ASP-16: the key's last write time (UTC) from RegQueryInfoKey on the open key's handle; null when the call fails or
    // returns 0.
    private static unsafe DateTimeOffset? ReadLastWritten(RegistryKey key)
    {
        try
        {
            long fileTime;
            var status = RegQueryInfoKey(key.Handle, null, null, null, null, null, null, null, null, null, null, &fileTime);
            if (status != 0 || fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            {
                return null;
            }

            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    // Windows Installer keeps a product installed for one user under Installer\UserData\<that user's SID>\Products, by
    // its packed code (a per-machine product is under S-1-5-18). Read-only; a failure answers no (the label then says
    // "needs admin", the conservative reading).
    private static bool IsUserInstallerProduct(RegistryKey machine, string keyName)
    {
        if (PackedProductCode(keyName) is not { } packed || System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is not { } sid)
        {
            return false;
        }

        try
        {
            using var product = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\" + sid + @"\Products\" + packed);
            return product is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>The packed form Windows Installer uses for a product code: the first three groups reversed, then each byte
    /// of the last two with its two hex digits swapped ({DBEDE699-98FB-4F15-B7A6-6F18E70E037D} → 996EDEBDBF8951F47B6AF6817EE030D7,
    /// checked against the registry 2026-10-17). Null when the key name is not a product code.</summary>
    internal static string? PackedProductCode(string keyName)
    {
        if (!Guid.TryParseExact(keyName, "B", out var guid))
        {
            return null;
        }

        var g = guid.ToString("N").ToUpperInvariant();
        var packed = new System.Text.StringBuilder(32);
        packed.Append(Reverse(g[..8])).Append(Reverse(g[8..12])).Append(Reverse(g[12..16]));
        for (var i = 16; i < 32; i += 2)
        {
            packed.Append(g[i + 1]).Append(g[i]);
        }

        return packed.ToString();

        static string Reverse(string text)
        {
            var chars = text.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static unsafe partial int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, char* className, uint* classNameLength, uint* reserved, uint* subKeys, uint* maxSubKeyLength, uint* maxClassLength, uint* values, uint* maxValueNameLength, uint* maxValueLength, uint* securityDescriptor, long* lastWriteTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static unsafe partial int QueryFullProcessImageName(IntPtr process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    private static unsafe partial IntPtr FindFirstVolume(char* volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    private static unsafe partial int FindNextVolume(IntPtr find, char* volumeName, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FindVolumeClose(IntPtr find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int GetVolumePathNamesForVolumeName(string volumeName, char* pathNames, uint length, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial uint QueryDosDevice(string deviceName, char* targetPath, uint max);
}
