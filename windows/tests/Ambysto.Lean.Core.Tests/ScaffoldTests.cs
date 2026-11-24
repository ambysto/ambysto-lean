using System;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Ambysto.Lean.Core.Tests;

/// <summary>Plan groups 2 and 9: the public surface is the plan's "Public surface" table (phase A plus phase B's scaffold),
/// plus the frozen ASP-11 update contract (intent/2026-10-23-asp-11-about-updates.md), no more and no less.</summary>
[TestClass]
public sealed class ScaffoldTests
{
    private static readonly string[] PhaseAPublicTypes =
    [
        "Catalog.AppCatalog", "Catalog.AppSpaceRow", "Catalog.CatalogEntry", "Catalog.CatalogKind", "Catalog.CleanCommand", "Catalog.CleanCommands", "Catalog.CleanOutcome", "Catalog.CleanResult",
        "Catalog.CleanRunner", "Catalog.CommandLog", "Catalog.CommandLogEntry", "Catalog.CommandRow", "Catalog.CommandRowState", "Catalog.CommandRows", "Catalog.CommandRunResult",
        "Catalog.EvidenceGrade", "Catalog.GuideAction", "Catalog.GuideRow", "Catalog.GuideRows", "Catalog.ICommandEnvironment", "Catalog.ICommandRunner", "Catalog.IProgramFinder", "Catalog.IVirtualDiskSource", "Catalog.KitCommandEnvironment", "Catalog.PathProgramFinder", "Catalog.ProcessCommandRunner",
        "Catalog.SpaceByApp", "Catalog.VirtualDiskKind", "Catalog.VirtualDiskRow", "Catalog.VirtualDisks",
        "Catalog.DeveloperSpace", "Catalog.DeveloperSpaceRow", "Catalog.InstallerVersions", "Catalog.InstallerVersionRow",
        "Uninstall.UninstallCleanupSummary",
        "Copy.Text",
        "Find.DuplicateCopy", "Find.DuplicateFinder", "Find.DuplicateGroup", "Find.DuplicateOptions", "Find.DuplicateSelection",
        "Find.DuplicateRules", "Find.KeepRule",
        "Find.LargeOldFinder", "Find.LargeOldOptions", "Find.LargeOldRow",
        "Find.PicturePreview", "Find.PreviewRead", "Find.PreviewResult",
        "Find.Suggest", "Find.SuggestedFile", "Find.SuggestedTemp", "Find.SuggestionKind",
        "Find.TempAge", "Find.TempFinder", "Find.TempItem",
        "Map.DisplayOnly", "Map.MapLayout", "Map.MapRect", "Map.MapTile", "Map.TileKind", "Map.TypeGroup", "Map.TypeGroupKind",
        "Measure.ByteUnits", "Measure.Coverage", "Measure.ItemStamp", "Measure.NodeMarks", "Measure.ScanNode", "Measure.ScanProgress",
        "Measure.ScanSummary", "Measure.ScanTree", "Measure.Scanner", "Measure.VolumeCard", "Measure.VolumeCardFacts",
        "Paths.PathContext", "Paths.SyncRoot",
        "Pro.Entitlement", "Pro.ILicenseCheck", "Pro.ILoginItem", "Pro.ILowDiskAlert", "Pro.IProFeatures", "Pro.LicenseCheckResult", "Pro.LicenseFacts",
        "Pro.NoProFeatures", "Pro.ScanReport",
        "Journal.JournalEntry", "Journal.JournalResult", "Journal.JournalStore", "Journal.PutBack", "Journal.PutBackOutcome",
        "Journal.TrashedIdentity",
        "Rules.LeanModule", "Rules.LeanRules",
        "Settings.AddResult", "Settings.LeanSettings", "Settings.LoadResult", "Settings.ProtectedFolders",
        "Trash.DeletionSession", "Trash.HardLinkNote", "Trash.IItemGate", "Trash.IShellRecycler", "Trash.KitItemGate", "Trash.MoveProgress", "Trash.RecycleReport",
        "Trash.SessionItem", "Trash.ShellRecycler", "Trash.TrashOutcome", "Trash.TrashResult", "Trash.Trasher",
        "Uninstall.AppClass", "Uninstall.AppGoneWatcher", "Uninstall.IUninstallSource", "Uninstall.InstalledApp",
        "Uninstall.LeftoverCandidate", "Uninstall.LeftoverMatcher",
        "Uninstall.RegistryViewKind", "Uninstall.SettingsHandOff", "Uninstall.StorePackage", "Uninstall.UninstallEntry",
        "Uninstall.UninstallInventory", "Uninstall.UninstallScope",
        "Updates.UpdateChecker", "Updates.UpdateResult", "Updates.UpdateState",
    ];

    private static readonly string[] AllowedReferences = ["Ambysto.Kit.License", "Ambysto.Kit.Safety"];

    [TestMethod]
    public void PublicSurfaceIsThePlansTable()
    {
        var assembly = typeof(Measure.Scanner).Assembly;
        var actual = assembly.GetExportedTypes().Select(t => t.FullName!["Ambysto.Lean.Core.".Length..]).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(PhaseAPublicTypes.Order(StringComparer.Ordinal).ToArray(), actual);
    }

    [TestMethod]
    public void CoreReferencesOnlyTheKitLicenseBeyondTheRuntime()
    {
        var references = typeof(Measure.Scanner).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal) && !n.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
                        && n is not "netstandard" and not "mscorlib" and not "Microsoft.Windows.SDK.NET" and not "WinRT.Runtime")
            .ToArray();
        CollectionAssert.IsSubsetOf(references, AllowedReferences);
    }
}
