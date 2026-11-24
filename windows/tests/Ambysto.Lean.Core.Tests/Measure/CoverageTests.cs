using Ambysto.Lean.Core.Measure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01, "Coverage": measured + not measured = used by this volume (exactly); not measured = used −
/// measured, never negative; if negative, show "measured more than used" with both numbers.</summary>
[TestClass]
public sealed class CoverageTests
{
    // docs/spec/01 Coverage: not measured = used − measured.
    [TestMethod]
    public void NotMeasuredIsUsedMinusMeasured()
    {
        var coverage = new Coverage(MeasuredBytes: 700, UsedBytes: 1000, UnreadableCount: 0, CloudNotDownloadedCount: 0);
        Assert.AreEqual(300L, coverage.NotMeasuredBytes);
        Assert.IsFalse(coverage.MeasuredMoreThanUsed);
    }

    // docs/spec/01 Coverage: measured + not measured = used by this volume (exactly).
    [TestMethod]
    public void MeasuredPlusNotMeasuredIsExactlyUsed()
    {
        long[] measured = [0, 1, 4095, 4096, 123_456_789_012, 999_999_999_999];
        const long used = 1_000_000_000_000;
        foreach (var m in measured)
        {
            var coverage = new Coverage(m, used, 0, 0);
            Assert.AreEqual(used, coverage.MeasuredBytes + coverage.NotMeasuredBytes, $"measured {m}");
        }
    }

    // docs/spec/01 Coverage: never negative; if negative, show "measured more than used" with both numbers.
    [TestMethod]
    public void MeasuredMoreThanUsedIsFlaggedAndNotMeasuredIsNeverNegative()
    {
        var coverage = new Coverage(MeasuredBytes: 1001, UsedBytes: 1000, UnreadableCount: 0, CloudNotDownloadedCount: 0);
        Assert.IsTrue(coverage.MeasuredMoreThanUsed);
        Assert.AreEqual(0L, coverage.NotMeasuredBytes);
        Assert.AreEqual(1001L, coverage.MeasuredBytes, "both numbers stay available for the readout");
        Assert.AreEqual(1000L, coverage.UsedBytes);
    }

    // docs/spec/01 Coverage: measured equal to used is complete coverage, not "measured more than used".
    [TestMethod]
    public void MeasuredEqualToUsedIsZeroNotMeasuredAndNotFlagged()
    {
        var coverage = new Coverage(1000, 1000, 0, 0);
        Assert.AreEqual(0L, coverage.NotMeasuredBytes);
        Assert.IsFalse(coverage.MeasuredMoreThanUsed);
    }

    // docs/spec/01 Coverage: an empty walk (nothing measured) leaves everything not measured.
    [TestMethod]
    public void NothingMeasuredLeavesEverythingNotMeasured()
    {
        var coverage = new Coverage(0, 5_000_000, 0, 0);
        Assert.AreEqual(5_000_000L, coverage.NotMeasuredBytes);
        Assert.IsFalse(coverage.MeasuredMoreThanUsed);
    }

    // docs/spec/01 Coverage: "Not measured" lists unreadable entries (count) and cloud files not downloaded (count);
    // Windows note (7): alternate data streams are not counted; their bytes are in the remainder.
    [TestMethod]
    public void CountsAreCarriedUnchanged()
    {
        var coverage = new Coverage(10, 20, UnreadableCount: 3, CloudNotDownloadedCount: 4);
        Assert.AreEqual(3L, coverage.UnreadableCount);
        Assert.AreEqual(4L, coverage.CloudNotDownloadedCount);
        Assert.AreEqual(10L, coverage.NotMeasuredBytes, "counts have no known size, so they never change the arithmetic");
    }
}
