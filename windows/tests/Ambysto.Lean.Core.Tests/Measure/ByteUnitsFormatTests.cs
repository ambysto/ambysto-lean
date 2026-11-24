using System;
using System.Globalization;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>Owner decision Q3 = A (binary units, as File Explorer) and the <see cref="ByteUnits.Format"/> contract: the text
/// of <c>StrFormatByteSizeEx</c> with <c>SFBS_FLAGS_TRUNCATE_UNDISPLAYED_DECIMAL_DIGITS</c> in the current user's
/// locale: three significant digits, truncated, not rounded. The oracle is that Windows call itself, made by the test
/// (<see cref="Native.ExplorerSizeText"/>): it reads the Windows user locale, not .NET's culture, so the en-US literals
/// are asserted only when the oracle itself prints them. The .NET culture is pinned to en-US for the test and restored.
/// Negative input is not in the doc comment and is not tested here.</summary>
[TestClass]
public sealed class ByteUnitsFormatTests
{
    private const long TB = 1024 * ByteUnits.GB;

    /// <summary><paramref name="units"/> of <paramref name="unit"/>, rounded up to a whole byte (so 15.9 GB is not a hair below 15.9).</summary>
    private static long Of(double units, long unit) => (long)Math.Ceiling(units * unit);

    private static void WithEnUs(Action test)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>Format equals the Windows text; and when Windows prints the en-US form, so must Format.</summary>
    private static void AssertExplorerText(long bytes, string enUs)
    {
        var oracle = Native.ExplorerSizeText(bytes);
        var actual = ByteUnits.Format(bytes);
        Assert.AreEqual(oracle, actual, $"{bytes} bytes: StrFormatByteSizeEx says \"{oracle}\"");
        if (string.Equals(oracle, enUs, StringComparison.Ordinal))
        {
            Assert.AreEqual(enUs, actual);
        }
        else
        {
            Console.WriteLine($"user locale is not en-US: {bytes} bytes shows as \"{oracle}\" (en-US: \"{enUs}\")");
        }
    }

    // ByteUnits.Format: "0 bytes", "1023 bytes": below 1 KB the count is exact.
    [TestMethod]
    public void BytesBelowOneKbAreExact() => WithEnUs(() =>
    {
        AssertExplorerText(0, "0 bytes");
        AssertExplorerText(1, "1 bytes");
        AssertExplorerText(1023, "1023 bytes");
    });

    // ByteUnits.Format: exactly 1 KB, 1 MB and 1 GB show as "1.00" of the unit (Q3 = A: 1 KB = 1024 bytes).
    [TestMethod]
    public void ExactUnitsShowAsOnePointZeroZero() => WithEnUs(() =>
    {
        AssertExplorerText(ByteUnits.KB, "1.00 KB");
        AssertExplorerText(ByteUnits.MB, "1.00 MB");
        AssertExplorerText(ByteUnits.GB, "1.00 GB");
    });

    // ByteUnits.Format: truncation, not rounding: 1.999 MB shows as 1.99 MB.
    [TestMethod]
    public void UndisplayedDigitsAreTruncatedNotRounded() => WithEnUs(() =>
    {
        AssertExplorerText(Of(1.999, ByteUnits.MB), "1.99 MB");
        AssertExplorerText((2 * ByteUnits.MB) - 1, "1.99 MB");
        AssertExplorerText(ByteUnits.KB + 1023, "1.99 KB");
    });

    // ByteUnits.Format: three significant digits: "15.9 GB", "150 MB", "1.49 MB".
    [TestMethod]
    public void ThreeSignificantDigits() => WithEnUs(() =>
    {
        AssertExplorerText(Of(15.9, ByteUnits.GB), "15.9 GB");
        AssertExplorerText(150 * ByteUnits.MB, "150 MB");
        AssertExplorerText(Of(1.49, ByteUnits.MB), "1.49 MB");
        AssertExplorerText(Of(999.9, ByteUnits.MB), "999 MB");
    });

    // ByteUnits.Format: terabytes.
    [TestMethod]
    public void TerabytesAreShownAsTb() => WithEnUs(() =>
    {
        AssertExplorerText(TB, "1.00 TB");
        AssertExplorerText(Of(2.5, TB), "2.50 TB");
    });

    // ByteUnits.Format: the volume card's numbers (used, free, total) go through the same formatter as the map.
    [TestMethod]
    public void MatchesTheWindowsTextAcrossAWideRange() => WithEnUs(() =>
    {
        long[] values = [0, 999, 1024, 1536, 10 * ByteUnits.KB, 1023 * ByteUnits.KB, 5 * ByteUnits.MB, 123 * ByteUnits.MB, 1023 * ByteUnits.MB, 7 * ByteUnits.GB, 321 * ByteUnits.GB, 999 * ByteUnits.GB, 3 * TB];
        foreach (var value in values)
        {
            Assert.AreEqual(Native.ExplorerSizeText(value), ByteUnits.Format(value), value.ToString(CultureInfo.InvariantCulture) + " bytes");
        }
    });

    // The oracle itself: StrFormatByteSizeEx truncates (a self-check, so a wrong flag in the oracle cannot hide a rounding Format).
    [TestMethod]
    public void OracleTruncates()
    {
        var text = Native.ExplorerSizeText((2 * ByteUnits.MB) - 1);
        Check.True(text.Contains('1', StringComparison.Ordinal) && text.Contains("99", StringComparison.Ordinal), "StrFormatByteSizeEx with the truncate flag should print 1.99 MB, got \"" + text + "\"");
        Check.False(text.Contains('2', StringComparison.Ordinal), "rounded up: " + text);
    }
}
