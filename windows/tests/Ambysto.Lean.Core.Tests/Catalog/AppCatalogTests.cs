using System.Linq;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (intent/2026-10-21-asp-20-suggest-catalog.md decisions 9, 12 and 13; plan "The catalog"): the
/// built-in catalog has exactly these nine entries, in this order, each with its kind, its evidence grade and the first URL
/// of its row in intent/2026-10-21-asp-20-catalog-evidence.md. Session 0; written by the test writer before the code, against
/// a stub.</summary>
[TestClass]
public sealed class AppCatalogTests
{
    private static readonly (string Id, CatalogKind Kind, EvidenceGrade Grade, string Url)[] Expected =
    [
        ("npm", CatalogKind.Command, EvidenceGrade.Strong, "https://docs.npmjs.com/cli/v11/commands/npm-cache"),
        ("pip", CatalogKind.Command, EvidenceGrade.Strong, "https://pip.pypa.io/en/stable/cli/pip_cache/"),
        ("uv", CatalogKind.Command, EvidenceGrade.Strong, "https://docs.astral.sh/uv/concepts/cache/"),
        ("nuget-http", CatalogKind.Command, EvidenceGrade.Strong, "https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders"),
        ("nuget-plugins", CatalogKind.Command, EvidenceGrade.Strong, "https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders"),
        ("chrome", CatalogKind.Guide, EvidenceGrade.Strong, "https://www.chromium.org/developers/design-documents/network-stack/disk-cache/"),
        ("edge", CatalogKind.Guide, EvidenceGrade.Weak, "https://learn.microsoft.com/en-us/answers/a/756905"),
        ("capcut", CatalogKind.Guide, EvidenceGrade.Weak, "https://www.hardware.com.br/noticias/capcut-versoes-antigas-windows-liberar-espaco/amp/"),
        ("electron-updater", CatalogKind.Guide, EvidenceGrade.Weak, "https://raw.githubusercontent.com/electron-userland/electron-builder/master/packages/electron-updater/src/DownloadedUpdateHelper.ts"),
    ];

    // Decision 9: exactly these nine ids, in display order; nothing else is in v1.
    [TestMethod]
    public void TheCatalogHoldsExactlyTheNineEntriesInOrder()
    {
        Check.Sequence(AppCatalog.Entries.Select(e => e.Id), [.. Expected.Select(e => e.Id)]);
    }

    // Decisions 12 and 13: the five with a documented clean command are Command, the four without are Guide.
    [TestMethod]
    public void EachEntryHasItsKind()
    {
        foreach (var expected in Expected)
        {
            var entry = AppCatalog.Entries.Single(e => e.Id == expected.Id);
            Assert.AreEqual(expected.Kind, entry.Kind, expected.Id);
        }
    }

    // Decision 9: the evidence grade; Edge, CapCut and electron-updater are the Weak ones the owner accepted by name.
    [TestMethod]
    public void EachEntryHasItsEvidenceGrade()
    {
        foreach (var expected in Expected)
        {
            var entry = AppCatalog.Entries.Single(e => e.Id == expected.Id);
            Assert.AreEqual(expected.Grade, entry.Grade, expected.Id);
        }
    }

    // The URL is the first one listed for the entry's row of the evidence table.
    [TestMethod]
    public void EachEntryCarriesItsEvidenceUrl()
    {
        foreach (var expected in Expected)
        {
            var entry = AppCatalog.Entries.Single(e => e.Id == expected.Id);
            Assert.AreEqual(expected.Url, entry.EvidenceUrl, expected.Id);
        }
    }

    // A row shows the app's name and why it is usually safe (decision 13); neither may be empty.
    [TestMethod]
    public void EachEntryNamesTheAppAndSaysWhy()
    {
        foreach (var entry in AppCatalog.Entries)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.AppName), entry.Id + " has no app name");
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Why), entry.Id + " has no why");
        }
    }

    // The catalog is matched exactly by id: ids are unique.
    [TestMethod]
    public void IdsAreUnique()
    {
        Assert.AreEqual(AppCatalog.Entries.Count, AppCatalog.Entries.Select(e => e.Id).Distinct().Count());
    }
}
