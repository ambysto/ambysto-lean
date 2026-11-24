using System;
using System.Globalization;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests;

/// <summary>docs/spec/07 Windows notes (owner decision 2026-10-18) and docs/spec/01, 02, 03: the words of the v1
/// additions. Numbers are formatted in the current culture; these tests set en-US for the duration of each check.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AdditionsCopyTests
{
    private static string InEnglish(Func<string> text)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            return text();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // docs/spec/01 Windows note: the spec's own example, with the unreadable part.
    [TestMethod]
    public void TheScanSummaryLineMatchesTheSpecsExample()
    {
        Assert.AreEqual(
            "Scanned 825,525 files in 61,204 folders in 21 s; 192 could not be read",
            InEnglish(() => Text.ScanSummaryLine(825_525, 61_204, 192, TimeSpan.FromSeconds(21))));
    }

    // docs/spec/01 Windows note: "the last part only when n > 0".
    [TestMethod]
    public void TheScanSummaryLineLeavesOutTheUnreadablePartWhenNothingWasUnreadable()
    {
        var line = InEnglish(() => Text.ScanSummaryLine(825_525, 61_204, 0, TimeSpan.FromSeconds(21)));
        Assert.AreEqual("Scanned 825,525 files in 61,204 folders in 21 s", line);
        Check.DoesNotContain(line, "could not be read");
        Check.DoesNotContain(line, ";");
    }

    // One unreadable entry is still reported.
    [TestMethod]
    public void OneUnreadableEntryIsReported()
    {
        var line = InEnglish(() => Text.ScanSummaryLine(10, 2, 1, TimeSpan.FromSeconds(3)));
        Check.Contains(line, "; 1 could not be read");
    }

    // docs/spec/07 Windows note: "Moving <i> of <n>: <name>".
    [TestMethod]
    public void TheMoveProgressLine()
    {
        Assert.AreEqual("Moving 3 of 12: holiday.mp4", InEnglish(() => Text.MovingProgress(3, 12, "holiday.mp4")));
    }

    // docs/spec/03 and 07: "About <size> could be moved to the Recycle Bin (estimate)"; never "freed" or "released".
    [TestMethod]
    public void TheTempEstimateIsAnEstimateAndNeverClaimsSpace()
    {
        var line = Text.TempEstimate("1.2 GB");
        Assert.AreEqual("About 1.2 GB could be moved to the Recycle Bin (estimate)", line);
        Check.True(line.Contains("estimate", StringComparison.OrdinalIgnoreCase), "says estimate: " + line);
        foreach (var claim in new[] { "freed", "released", "free up", "frees", "reclaimed", "cleaned" })
        {
            Check.False(CopyTests.ContainsWord(line, claim), "\"" + claim + "\" in: " + line);
        }
    }

    // docs/spec/02 and 07 Windows notes: the hint of a row inside or holding a user-protected folder.
    [TestMethod]
    public void TheProtectedByYouHint() => Assert.AreEqual("Protected by you", Check.Constant<string>(typeof(Text), nameof(Text.HintProtectedByYou)));

    // docs/spec/03 Temporary files: the Windows\Temp row's hint.
    [TestMethod]
    public void TheSystemWideHint() =>
        Assert.AreEqual("System-wide: needs an administrator; Lean does not change it", Check.Constant<string>(typeof(Text), nameof(Text.HintSystemWide)));

    // docs/spec/03 Temporary files: the age choices, 1 week (default), 1 month, 1 quarter (3 months).
    [TestMethod]
    public void TheAgeChoices()
    {
        Assert.AreEqual("1 week", Check.Constant<string>(typeof(Text), nameof(Text.AgeWeek)));
        Assert.AreEqual("1 month", Check.Constant<string>(typeof(Text), nameof(Text.AgeMonth)));
        Assert.AreEqual("1 quarter (3 months)", Check.Constant<string>(typeof(Text), nameof(Text.AgeQuarter)));
    }
}
