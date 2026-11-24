using System.Collections.Generic;
using Ambysto.Lean.Core.Copy;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>How a catalog entry reclaims space (ASP-20 decisions 12 and 13).</summary>
public enum CatalogKind
{
    /// <summary>Lean runs the tool's own clean command after the user confirms (no Put back).</summary>
    Command,

    /// <summary>Shown only: Lean opens the app's own page or the folder and deletes nothing.</summary>
    Guide,
}

/// <summary>How strong the evidence is that the location may be cleared (intent/2026-10-21-asp-20-catalog-evidence.md).</summary>
public enum EvidenceGrade
{
    /// <summary>The vendor's documentation or the tool's own clean command.</summary>
    Strong,

    /// <summary>Community reports, an observed layout or the tool's source code; shipped only when the owner accepted it by name.</summary>
    Weak,
}

/// <summary>What a Guide row's button does.</summary>
public enum GuideAction
{
    /// <summary>Opens the browser's own Clear browsing data page.</summary>
    OpenClearBrowsingData,

    /// <summary>Opens the folder in File Explorer.</summary>
    OpenFolder,
}

/// <summary>One entry of the built-in catalog (ASP-20 phase A).</summary>
public sealed record CatalogEntry(string Id, string AppName, CatalogKind Kind, EvidenceGrade Grade, string EvidenceUrl, string Why)
{
    /// <summary>Phase C: the exact uninstall entry key of the app this entry belongs to (chrome, edge, capcut); null otherwise.</summary>
    public string? UninstallKeyName { get; init; }
}

/// <summary>The built-in, reviewed catalog (ASP-20 decisions 3 and 9): matched exactly, never by a name pattern.</summary>
public static class AppCatalog
{
    /// <summary>Every v1 entry, in display order.</summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        new("npm", "npm", CatalogKind.Command, EvidenceGrade.Strong, "https://docs.npmjs.com/cli/v11/commands/npm-cache", Text.WhyNpmCache),
        new("pip", "pip", CatalogKind.Command, EvidenceGrade.Strong, "https://pip.pypa.io/en/stable/cli/pip_cache/", Text.WhyPipCache),
        new("uv", "uv", CatalogKind.Command, EvidenceGrade.Strong, "https://docs.astral.sh/uv/concepts/cache/", Text.WhyUvCache),
        new("nuget-http", "NuGet", CatalogKind.Command, EvidenceGrade.Strong, NuGetEvidence, Text.WhyNuGetHttpCache),
        new("nuget-plugins", "NuGet", CatalogKind.Command, EvidenceGrade.Strong, NuGetEvidence, Text.WhyNuGetPluginsCache),
        new("chrome", "Google Chrome", CatalogKind.Guide, EvidenceGrade.Strong, "https://www.chromium.org/developers/design-documents/network-stack/disk-cache/", Text.WhyBrowserCache) { UninstallKeyName = "Google Chrome" },
        new("edge", "Microsoft Edge", CatalogKind.Guide, EvidenceGrade.Weak, "https://learn.microsoft.com/en-us/answers/a/756905", Text.WhyBrowserCache) { UninstallKeyName = "Microsoft Edge" },
        new("capcut", "CapCut", CatalogKind.Guide, EvidenceGrade.Weak, "https://www.hardware.com.br/noticias/capcut-versoes-antigas-windows-liberar-espaco/amp/", Text.WhyCapCutVersions) { UninstallKeyName = "CapCut" },
        new("electron-updater", "App updaters", CatalogKind.Guide, EvidenceGrade.Weak,
            "https://raw.githubusercontent.com/electron-userland/electron-builder/master/packages/electron-updater/src/DownloadedUpdateHelper.ts", Text.WhyUpdaterPending),
    ];

    // Both NuGet rows rest on the same Microsoft page (dotnet nuget locals <name> --clear).
    private const string NuGetEvidence = "https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders";
}
