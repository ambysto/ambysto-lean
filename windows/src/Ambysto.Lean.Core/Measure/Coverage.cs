namespace Ambysto.Lean.Core.Measure;

/// <summary>docs/spec/01 "Coverage": measured + not measured = used by this volume, exactly; not measured is never
/// negative (when the walk measured more than the volume uses, <see cref="MeasuredMoreThanUsed"/> is true and both
/// numbers are shown).</summary>
public sealed record Coverage(
    long MeasuredBytes,
    long UsedBytes,
    long UnreadableCount,
    long CloudNotDownloadedCount)
{
    /// <summary>Used − measured, never below 0.</summary>
    public long NotMeasuredBytes => System.Math.Max(0, UsedBytes - MeasuredBytes);

    /// <summary>True when the walk measured more than the volume reports as used.</summary>
    public bool MeasuredMoreThanUsed => MeasuredBytes > UsedBytes;
}
