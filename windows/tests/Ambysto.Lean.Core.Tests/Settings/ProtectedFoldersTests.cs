using System;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Settings;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Settings;

/// <summary>docs/spec/02 Windows note (the user's protected folders, owner decision 2026-10-18) and the plan's Design row
/// <c>Settings.ProtectedFolders</c>: only existing local folders are added, in canonical spelling, ordered, without
/// duplicates; a folder inside (or the same as) an entry is not added; a folder that holds entries replaces them. Remove
/// takes out the entry equal to the folder, ignoring case. Neither saves. Folders are the test's own scratch folders.</summary>
[TestClass]
public sealed class ProtectedFoldersTests
{
    private static string[] Entries(LeanSettings settings) => settings.ProtectedFolders.ToArray();

    private static string Show(LeanSettings settings) => "[" + string.Join(" | ", settings.ProtectedFolders) + "]";

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The list holds exactly these folders (spelled as on disk, compared ignoring case), in any order.</summary>
    private static void HoldsExactly(LeanSettings settings, params string[] folders)
    {
        Assert.AreEqual(folders.Length, settings.ProtectedFolders.Count, "entries: " + Show(settings));
        foreach (var folder in folders)
        {
            Check.True(settings.ProtectedFolders.Any(e => SamePath(e, folder)), folder + " is not in " + Show(settings));
        }
    }

    // A folder that exists is added, and the list holds it.
    [TestMethod]
    public void AnExistingFolderIsAdded()
    {
        using var temp = new TempFolder("protect-add");
        var keep = temp.Folder("Keep");
        var settings = new LeanSettings();

        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));
        HoldsExactly(settings, keep);
    }

    // The same folder again, in any spelling of it, is already covered: nothing changes.
    [TestMethod]
    [DataRow("same")]
    [DataRow("upper")]
    [DataRow("trailing")]
    [DataRow("dotdot")]
    [DataRow("forward")]
    public void TheSameFolderAgainIsAlreadyCovered(string spelling)
    {
        using var temp = new TempFolder("protect-again");
        var keep = temp.Folder("Keep");
        temp.Folder("Other");
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));
        var before = Entries(settings);

        var again = spelling switch
        {
            "same" => keep,
            "upper" => keep.ToUpperInvariant(),
            "trailing" => keep + "\\",
            "dotdot" => Path.Combine(temp.Path, "Other", "..", "Keep"),
            _ => keep.Replace('\\', '/'),
        };

        Assert.AreEqual(AddResult.AlreadyCovered, ProtectedFolders.Add(settings, again), again);
        CollectionAssert.AreEqual(before, Entries(settings), "the list changed: " + Show(settings));
    }

    // Canonical spelling: an entry has no trailing separator and no "." or ".." component, and names the folder.
    [TestMethod]
    public void AnEntryIsStoredInCanonicalSpelling()
    {
        using var temp = new TempFolder("protect-canonical");
        var keep = temp.Folder("Keep");
        temp.Folder("Other");
        var settings = new LeanSettings();
        var spelled = Path.Combine(temp.Path, "Other", "..", ".", "KEEP") + "\\";

        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, spelled));

        var entry = settings.ProtectedFolders.Single();
        Check.True(SamePath(entry, keep), "the entry " + entry + " is not " + keep);
        Check.False(entry.EndsWith('\\') || entry.EndsWith('/'), "a trailing separator: " + entry);
        Check.False(entry.Split('\\').Any(part => part is "." or ".."), "a dot component: " + entry);
        Check.False(entry.Contains('/', StringComparison.Ordinal), "a forward slash: " + entry);
        Check.True(Path.IsPathFullyQualified(entry), "not a full path: " + entry);
    }

    // A folder inside a listed one (any depth) is already covered: nothing changes.
    [TestMethod]
    [DataRow(@"Keep\sub")]
    [DataRow(@"Keep\sub\deeper")]
    public void AFolderInsideAListedOneIsAlreadyCovered(string inner)
    {
        using var temp = new TempFolder("protect-inside");
        var keep = temp.Folder("Keep");
        var inside = temp.Folder(inner.Split('\\'));
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));

        Assert.AreEqual(AddResult.AlreadyCovered, ProtectedFolders.Add(settings, inside));
        HoldsExactly(settings, keep);
    }

    // A folder that holds listed ones replaces them (and only them).
    [TestMethod]
    public void AFolderHoldingListedOnesReplacesThem()
    {
        using var temp = new TempFolder("protect-holding");
        var x = temp.Folder("Outer", "x");
        var z = temp.Folder("Outer", "y", "z");
        var other = temp.Folder("Other");
        var outer = Path.Combine(temp.Path, "Outer");
        var settings = new LeanSettings();
        foreach (var folder in new[] { x, other, z })
        {
            Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, folder), folder);
        }

        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, outer));
        HoldsExactly(settings, outer, other);
    }

    // A name that only starts like a listed folder is not inside it, and does not hold it.
    [TestMethod]
    public void ASiblingWithTheSamePrefixIsNeitherInsideNorHolding()
    {
        using var temp = new TempFolder("protect-prefix");
        var keep = temp.Folder("Keep");
        var keep2 = temp.Folder("Keep2");
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep2));

        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep), "Keep is not inside Keep2");
        HoldsExactly(settings, keep2, keep);

        var fresh = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(fresh, keep));
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(fresh, keep2), "Keep2 is not inside Keep");
        HoldsExactly(fresh, keep, keep2);
    }

    // The list keeps the order the folders were added in.
    [TestMethod]
    public void TheListKeepsTheOrderOfAdding()
    {
        using var temp = new TempFolder("protect-order");
        var c = temp.Folder("c");
        var a = temp.Folder("a");
        var b = temp.Folder("b");
        var settings = new LeanSettings();
        string[] added = [c, a, b];
        foreach (var folder in added)
        {
            Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, folder));
        }

        Assert.AreEqual(3, settings.ProtectedFolders.Count);
        for (var i = 0; i < added.Length; i++)
        {
            Check.True(SamePath(added[i], settings.ProtectedFolders[i]), $"entry {i}: {Show(settings)}");
        }
    }

    // Only folders: a file, or a path where nothing is, is not a folder; nothing changes.
    [TestMethod]
    [DataRow("file")]
    [DataRow("missing")]
    [DataRow("missing-parent")]
    public void NotAFolderIsRefused(string what)
    {
        using var temp = new TempFolder("protect-notfolder");
        var keep = temp.Folder("Keep");
        var path = what switch
        {
            "file" => temp.File(@"Docs\report.pdf", 4096),
            "missing" => temp.Sub("NotThere"),
            _ => temp.Sub("NotThere", "Below"),
        };
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));

        Assert.AreEqual(AddResult.NotAFolder, ProtectedFolders.Add(settings, path), path);
        HoldsExactly(settings, keep);
    }

    // Text that is not a usable local path: empty, blank, a UNC path (even of a share that exists), a device path.
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(@"\\server\share\folder")]
    [DataRow(@"\\localhost\ambynet")]
    [DataRow(@"\\localhost\C$\Users")]
    [DataRow(@"\\.\C:\Users")]
    [DataRow(@"\\?\UNC\localhost\ambynet")]
    [DataRow(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    [DataRow("C:\\Users\\bad\0name")]
    public void UnusableTextIsRefused(string text)
    {
        using var temp = new TempFolder("protect-unusable");
        var keep = temp.Folder("Keep");
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));

        Assert.AreEqual(AddResult.NotUsable, ProtectedFolders.Add(settings, text), "[" + text + "]");
        HoldsExactly(settings, keep);
    }

    // Remove takes out the entry equal to the folder, ignoring case and a trailing separator, and keeps the others.
    [TestMethod]
    [DataRow("same")]
    [DataRow("upper")]
    [DataRow("trailing")]
    public void RemoveTakesOutTheEqualEntry(string spelling)
    {
        using var temp = new TempFolder("protect-remove");
        var a = temp.Folder("A");
        var b = temp.Folder("B");
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, a));
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, b));
        var entry = settings.ProtectedFolders[0];
        var text = spelling switch
        {
            "same" => entry,
            "upper" => entry.ToUpperInvariant(),
            _ => entry + "\\",
        };

        Assert.IsTrue(ProtectedFolders.Remove(settings, text), text);
        HoldsExactly(settings, b);
    }

    // Remove of a folder that is not listed (a missing entry, a folder inside an entry, a folder holding one) is false
    // and changes nothing.
    [TestMethod]
    public void RemoveOfAnEntryThatIsNotListedIsFalse()
    {
        using var temp = new TempFolder("protect-remove-missing");
        var keep = temp.Folder("Outer", "Keep");
        var inner = temp.Folder("Outer", "Keep", "Inner");
        var other = temp.Folder("Other");
        var settings = new LeanSettings();
        Assert.IsFalse(ProtectedFolders.Remove(settings, other), "an empty list");
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));

        Assert.IsFalse(ProtectedFolders.Remove(settings, other), "a folder not in the list");
        Assert.IsFalse(ProtectedFolders.Remove(settings, inner), "a folder inside an entry");
        Assert.IsFalse(ProtectedFolders.Remove(settings, Path.Combine(temp.Path, "Outer")), "a folder holding an entry");
        HoldsExactly(settings, keep);
    }

    // An entry whose folder was deleted since can still be removed (the user tidies the list).
    [TestMethod]
    public void AnEntryWhoseFolderIsGoneCanBeRemoved()
    {
        using var temp = new TempFolder("protect-remove-gone");
        var keep = temp.Folder("Keep");
        var settings = new LeanSettings();
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, keep));
        var entry = settings.ProtectedFolders[0];
        Directory.Delete(keep);

        Assert.IsTrue(ProtectedFolders.Remove(settings, entry));
        Assert.AreEqual(0, settings.ProtectedFolders.Count);
    }
}
