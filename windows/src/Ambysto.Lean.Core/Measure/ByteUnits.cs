namespace Ambysto.Lean.Core.Measure;

/// <summary>The size units of every threshold and every size shown. Binary, as File Explorer counts them
/// (1 MB = 1,048,576 bytes): owner decision Q3 = A, 2026-10-17.</summary>
public static class ByteUnits
{
    private const int TruncateUndisplayedDecimalDigits = 2;

    /// <summary>One kilobyte.</summary>
    public const long KB = 1024;

    /// <summary>One megabyte.</summary>
    public const long MB = 1024 * KB;

    /// <summary>One gigabyte.</summary>
    public const long GB = 1024 * MB;

    /// <summary>A size as File Explorer shows it: the text of <c>StrFormatByteSizeEx</c> with
    /// <c>SFBS_FLAGS_TRUNCATE_UNDISPLAYED_DECIMAL_DIGITS</c> in the current user's locale (for example "0 bytes",
    /// "1023 bytes", "1.00 KB", "1.49 MB", "15.9 GB", "150 MB"; three significant digits, truncated).</summary>
    public static string Format(long bytes)
    {
        System.ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        unsafe
        {
            // 64 characters is far more than any size text needs ("1023 bytes", "999 TB" in any locale).
            char* buffer = stackalloc char[64];
            var result = MeasureNative.StrFormatByteSizeEx((ulong)bytes, TruncateUndisplayedDecimalDigits, buffer, 64);
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
            return new string(buffer);
        }
    }
}
