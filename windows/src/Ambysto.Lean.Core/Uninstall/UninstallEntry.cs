using System;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>Which hive an uninstall entry is registered in.</summary>
public enum UninstallScope
{
    /// <summary>The user's hive: the user can uninstall it from Settings without an administrator.</summary>
    PerUser,

    /// <summary>The machine's hive: shown as "needs admin".</summary>
    PerMachine,
}

/// <summary>Which registry view an entry was read from.</summary>
public enum RegistryViewKind
{
    /// <summary>The 64-bit view.</summary>
    Bits64,

    /// <summary>The 32-bit view (WOW6432Node).</summary>
    Bits32,
}

/// <summary>One registered uninstaller, as read (docs/spec/04 Windows note). Values that are missing are null; an entry
/// without a display name is not listed (Windows does not list it either).</summary>
public sealed record UninstallEntry(
    string KeyName,
    UninstallScope Scope,
    RegistryViewKind View,
    string? DisplayName,
    string? Publisher,
    string? DisplayVersion,
    string? InstallLocation,
    bool SystemComponent)
{
    /// <summary>False when the registry key exists but its values could not be read; never evidence of absent references.</summary>
    public bool ValuesReadable { get; init; } = true;

    /// <summary>A Windows Installer product installed for the current user only. Windows Installer lists such a product
    /// under the machine hive's <c>Uninstall</c> key (measured 2026-10-17 with Lean's own per-user installer), so its
    /// <see cref="Scope"/> is <see cref="UninstallScope.PerMachine"/>, but no administrator is needed to remove it. Only
    /// the label uses this; the entry's identity (hive, view, key) is unchanged.</summary>
    public bool InstalledForCurrentUser { get; init; }

    /// <summary>ASP-16: the entry's <c>EstimatedSize</c> DWORD (read as unsigned, in KB) × 1024; null when missing, not a DWORD or 0 (Settings shows no size for it).</summary>
    public long? EstimatedSizeBytes { get; init; }

    /// <summary>ASP-16: the entry's <c>InstallDate</c> (exactly 8 digits, yyyyMMdd); null when it is anything else, not a date, before 1980 or more than 1 day after today (an installer's wrong clock).</summary>
    public DateOnly? InstallDate { get; init; }

    /// <summary>ASP-16: the key's last write time (UTC) from <c>RegQueryInfoKey</c> on the open key; null when it cannot be read.</summary>
    public DateTimeOffset? LastWritten { get; init; }
}

/// <summary>One installed Store (MSIX) package: listed display-only.</summary>
public sealed record StorePackage(string FullName, string DisplayName, string? Publisher, string InstallLocation)
{
    /// <summary>ASP-16: <c>Package.InstalledDate</c>, or null when reading it throws.</summary>
    public DateTimeOffset? InstalledDate { get; init; }
}
