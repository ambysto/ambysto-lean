using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests;

/// <summary>docs/spec/07 "Words": never "junk", "free up", "freed", "cleaned", "clean up your", "boost", "speed up",
/// "optimize", "garbage" in user-facing text; Windows notes: "Recycle Bin" for "Trash" in every string, File Explorer
/// for Finder, no purgeable line; the exact wording of the strings phase A uses (docs/spec/01, 03, 06).</summary>
[TestClass]
public sealed class CopyTests
{
    internal static readonly string[] BannedWords = ["junk", "free up", "freed", "cleaned", "clean up your", "boost", "speed up", "optimize", "optimise", "garbage"];

    internal static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Every public string of <see cref="Text"/>: constants and static string functions called with sample arguments.</summary>
    private static IEnumerable<(string Member, string Value)> AllStrings()
    {
        foreach (var field in typeof(Text).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string)))
        {
            yield return (field.Name, (string)field.GetValue(null)!);
        }

        foreach (var method in typeof(Text).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.ReturnType == typeof(string)))
        {
            var args = method.GetParameters().Select(p => p.ParameterType == typeof(string) ? "Sample" : p.ParameterType == typeof(long) ? 3L : p.ParameterType == typeof(int) ? (object)3 : Activator.CreateInstance(p.ParameterType)).ToArray();
            yield return (method.Name, (string)method.Invoke(null, args)!);
        }
    }

    // docs/spec/07: never used in user-facing text: the banned words.
    [TestMethod]
    public void NoPublicStringUsesABannedWord()
    {
        var hits = AllStrings().SelectMany(s => BannedWords.Where(w => ContainsWord(s.Value, w)).Select(w => s.Member + ": \"" + w + "\"")).ToArray();
        Assert.AreEqual(0, hits.Length, string.Join("; ", hits));
    }

    // docs/spec/07 Windows note: "Trash" is "Recycle Bin" in every string; File Explorer for Finder; no macOS words.
    [TestMethod]
    public void NoPublicStringSaysTrashFinderOrMacOs()
    {
        var hits = AllStrings().Where(s => ContainsWord(s.Value, "Trash") || ContainsWord(s.Value, "Finder") || s.Value.Contains("macOS", StringComparison.Ordinal)).Select(s => s.Member).ToArray();
        Assert.AreEqual(0, hits.Length, string.Join("; ", hits));
    }

    // docs/spec/07: the UI states what happened, never more: every string is non-empty and has no leading or trailing blank.
    [TestMethod]
    public void EveryPublicStringIsTrimmedAndNonEmpty()
    {
        foreach (var (member, value) in AllStrings())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), member);
            Assert.AreEqual(value.Trim(), value, member);
        }
    }

    // docs/spec/01 Progress and cancel: "Scan cancelled — partial".
    [TestMethod]
    public void ScanCancelledWording() => Assert.AreEqual("Scan cancelled — partial", Check.Constant<string>(typeof(Text), nameof(Text.ScanCancelled)));

    // docs/spec/01 walk rule 6: "N cloud files not downloaded".
    [TestMethod]
    public void CloudFilesWording()
    {
        Assert.AreEqual("3 cloud files not downloaded", Text.CloudFilesNotDownloaded(3));
        Assert.AreEqual("0 cloud files not downloaded", Text.CloudFilesNotDownloaded(0));
    }

    // docs/spec/01 Coverage, Windows note: the remainder label; "measured more than used". Plan group 8b: the remainder
    // names only what the walker does not read as the standard user; the parts that stay are checked here, the exact
    // wording is the main session's decision (see NotMeasuredRemainderNamesNoFileTheWalkerMeasures).
    [TestMethod]
    public void CoverageWording()
    {
        var remainder = Check.Constant<string>(typeof(Text), nameof(Text.NotMeasuredRemainder));
        foreach (var part in new[] { "restore points", "NTFS metadata", "alternate data streams", "other users' folders" })
        {
            Check.Contains(remainder, part);
        }

        Assert.AreEqual("measured more than used", Check.Constant<string>(typeof(Text), nameof(Text.MeasuredMoreThanUsed)));
        Assert.AreEqual("Not measured", Check.Constant<string>(typeof(Text), nameof(Text.NotMeasured)));
    }

    // Plan group 8b (owner run 2026-10-17): the "Not measured" remainder names only what the walker does not read as the
    // standard user. Probe 2026-10-17 on the test machine (Core Scanner over C:\ as ambytest): pagefile.sys (10.6 GB),
    // swapfile.sys (256 MB) and, with hibernation turned on for the probe, hiberfil.sys (6.38 GB) were all in the tree
    // with their allocated size, so the remainder names no page, swap or hibernation file.
    [TestMethod]
    public void NotMeasuredRemainderNamesNoFileTheWalkerMeasures()
    {
        var remainder = Check.Constant<string>(typeof(Text), nameof(Text.NotMeasuredRemainder));
        foreach (var word in new[] { "page", "paging", "pagefile", "swap", "swapfile", "hibernation", "hiberfil" })
        {
            Assert.IsFalse(ContainsWord(remainder, word), $"the remainder \"{remainder}\" names \"{word}\", which the walker measures");
        }
    }

    // docs/spec/01 Map: "smaller items"; Windows note: Reveal in File Explorer, Move to Recycle Bin….
    [TestMethod]
    public void MapWording()
    {
        Assert.AreEqual("smaller items", Check.Constant<string>(typeof(Text), nameof(Text.SmallerItems)));
        Assert.AreEqual("Reveal in File Explorer", Check.Constant<string>(typeof(Text), nameof(Text.RevealInExplorer)));
        Assert.AreEqual("Move to Recycle Bin…", Check.Constant<string>(typeof(Text), nameof(Text.MoveToRecycleBin)));
    }

    // docs/spec/01 Volume card; 03 "Show system items" and the estimate label; 04 Windows notes (needs admin, hand-off);
    // the wording is in docs/spec/07, Windows notes.
    [TestMethod]
    public void CardFindAndUninstallWording()
    {
        Assert.AreEqual("Used by this volume", Check.Constant<string>(typeof(Text), nameof(Text.UsedByThisVolume)));
        Assert.AreEqual("Free right now", Check.Constant<string>(typeof(Text), nameof(Text.FreeRightNow)));
        Assert.AreEqual("In the Recycle Bin", Check.Constant<string>(typeof(Text), nameof(Text.InTheRecycleBin)));
        Assert.AreEqual("Open Recycle Bin", Check.Constant<string>(typeof(Text), nameof(Text.OpenRecycleBin)));
        Assert.AreEqual("Show system items", Check.Constant<string>(typeof(Text), nameof(Text.ShowSystemItems)));
        Assert.AreEqual("unknown", Check.Constant<string>(typeof(Text), nameof(Text.Unknown)));
        Assert.AreEqual("Scan", Check.Constant<string>(typeof(Text), nameof(Text.Scan)));
        Assert.AreEqual("Cancel", Check.Constant<string>(typeof(Text), nameof(Text.Cancel)));
        Assert.AreEqual("Reclaimable (estimate)", Check.Constant<string>(typeof(Text), nameof(Text.ReclaimableEstimate)));
        Assert.AreEqual("Needs admin: remove in Settings > Apps", Check.Constant<string>(typeof(Text), nameof(Text.NeedsAdminApp)));
        Assert.AreEqual("Needs admin", Check.Constant<string>(typeof(Text), nameof(Text.NeedsAdmin)));
        Assert.AreEqual("Remove the app in Settings first", Check.Constant<string>(typeof(Text), nameof(Text.RemoveAppFirst)));
        Assert.AreEqual("In Settings > Apps > Installed apps, find Contoso Notes and choose Uninstall. Lean offers its leftovers once it is gone.",
            Text.UninstallInSettings("Contoso Notes"));
    }

    // docs/spec/01 hint table and its Windows notes.
    [TestMethod]
    public void HintWording()
    {
        Assert.AreEqual("Manage in Apple Devices or iTunes", Check.Constant<string>(typeof(Text), nameof(Text.HintDeviceBackups)));
        Assert.AreEqual("System-wide: needs an administrator; Lean does not change it", Check.Constant<string>(typeof(Text), nameof(Text.HintSystemWide)));
        Assert.AreEqual("Manage in Docker Desktop or with wsl.exe", Check.Constant<string>(typeof(Text), nameof(Text.HintDockerWsl)));
        Assert.AreEqual("Synced by OneDrive: manage it there", Text.HintSynced("OneDrive"));
        Assert.AreEqual("Use Ambysto Optimer for developer caches", Check.Constant<string>(typeof(Text), nameof(Text.HintDeveloperCaches)));
        Assert.AreEqual("Outside the folders Lean may change", Check.Constant<string>(typeof(Text), nameof(Text.HintOutside)));
    }

    // docs/spec/03 Windows note: every row says "last modified"; Duplicates: "May share storage; moving it may free less",
    // "Keeping: <path>".
    [TestMethod]
    public void FindWording()
    {
        Assert.AreEqual("last modified", Check.Constant<string>(typeof(Text), nameof(Text.LastModified)));
        Assert.AreEqual("May share storage; moving it may free less", Check.Constant<string>(typeof(Text), nameof(Text.MayShareStorage)));
        Assert.AreEqual(@"Keeping: C:\Users\me\Documents\a.pdf", Text.Keeping(@"C:\Users\me\Documents\a.pdf"));
    }

    // docs/spec/06 Windows note: "Moving is off while running as administrator."; License: "Registered to <licensee>";
    // Purchase reminder: the quiet "Pro" label.
    [TestMethod]
    public void ProWording()
    {
        Assert.AreEqual("Moving is off while running as administrator.", Check.Constant<string>(typeof(Text), nameof(Text.MovingOffElevated)));
        Assert.AreEqual("Registered to Alice Example", Text.RegisteredTo("Alice Example"));
        Assert.AreEqual("Pro", Check.Constant<string>(typeof(Text), nameof(Text.ProLabel)));
    }

    // docs/spec/07: the banned-word check itself catches each banned word (so a passing lint means something).
    [TestMethod]
    public void BannedWordCheckCatchesEachWord()
    {
        foreach (var word in BannedWords)
        {
            Assert.IsTrue(ContainsWord("We will " + word + " now", word), word);
        }

        Assert.IsFalse(ContainsWord("Use Ambysto Optimer for developer caches", "optimize"), "Optimer is not optimize");
        Assert.IsFalse(ContainsWord("freedom", "freed"), "whole words only");
    }

    // docs/spec/01 Coverage: "Not measured" lists the count of entries that could not be read; one entry is "1 item", not
    // "1 items" (en-US; brief M01).
    [TestMethod]
    public void UnreadableCountIsSingularForOne()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.AreEqual("1 item could not be read", Text.UnreadableItems(1));
            Assert.AreEqual("2 items could not be read", Text.UnreadableItems(2));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
