#if DEBUG
using System;
using System.Globalization;
using System.Threading;
using Ambysto.Lean.Core.Trash;

namespace Ambysto.Lean.App;

/// <summary>ASP-17 Debug-only QA arguments, compiled into Debug builds only (like <c>--pro</c>, and in a file of their own):
/// <c>--suggest-delay &lt;milliseconds&gt;</c> makes the Suggest walk of Temporary files and of Large and old wait that long after the walk,
/// before its result is applied; <c>--listing-delay &lt;milliseconds&gt;</c> does the same for the listing of Large and old and of Temporary
/// files, after the read and before the result is applied. Both wait off the UI thread, so a test can change the UI (a new listing, a
/// protected folder) meanwhile. The two delays accept only 0 to 60,000; anything else (a negative, a larger, Infinity, NaN, text) is 0,
/// and reading them never throws. <c>--fail-temp-reread</c> makes the first re-read inside Suggest's Temp loop throw once, on purpose,
/// so the read error can be tested. ASP-24: <c>--move-delay &lt;milliseconds&gt;</c> (the same range) makes each item's shell move wait that
/// long before it starts, and each Put back wait before it starts, both off the UI thread, so a test can close the window during a move or
/// a Put back; the Trasher's order and checks are unchanged. They read and change nothing else: no file is opened or changed by them.</summary>
public sealed partial class MainWindow
{
    private const double MaxQaDelayMilliseconds = 60_000;

    private static readonly TimeSpan DebugSuggestDelay = ReadDelay("--suggest-delay");
    private static readonly TimeSpan DebugListingDelay = ReadDelay("--listing-delay");

    static partial void HoldSuggestWalk() => Hold(DebugSuggestDelay);

    static partial void HoldListing() => Hold(DebugListingDelay);

    private static readonly TimeSpan DebugMoveDelay = ReadDelay("--move-delay");

    static partial void HoldPutBack() => Hold(DebugMoveDelay);

    static partial void WrapRecycler(ref IShellRecycler recycler)
    {
        if (DebugMoveDelay > TimeSpan.Zero)
        {
            recycler = new DelayedRecycler(recycler);
        }
    }

    private sealed class DelayedRecycler(IShellRecycler inner) : IShellRecycler
    {
        public RecycleReport Recycle(string approvedRealPath)
        {
            Hold(DebugMoveDelay);
            return inner.Recycle(approvedRealPath);
        }
    }

    // --fail-temp-reread (ASP-17 fix round 3): the first re-read inside Suggest's Temp loop throws once, so the failure path can be tested.
    private static readonly bool DebugFailTempReread = Array.IndexOf(Environment.GetCommandLineArgs(), "--fail-temp-reread") > 0;
    private static int failedTempReread;

    static partial void FailTempReread()
    {
        if (DebugFailTempReread && Interlocked.Exchange(ref failedTempReread, 1) == 0)
        {
            throw new System.IO.IOException("--fail-temp-reread");
        }
    }

    private static void Hold(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private static TimeSpan ReadDelay(string name)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 1; i + 1 < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.Ordinal))
                {
                    return double.TryParse(arguments[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
                        && double.IsFinite(milliseconds) && milliseconds > 0 && milliseconds <= MaxQaDelayMilliseconds
                        ? TimeSpan.FromMilliseconds(milliseconds)
                        : TimeSpan.Zero;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
        {
            // an unusable command line: no delay
        }

        return TimeSpan.Zero;
    }
}
#endif
