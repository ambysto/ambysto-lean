// INDEPENDENT DRAFT ONLY. Real Temp result population, no product source or artificial listing delays.
// One test; no global keyboard, UIA worker, navigation race, or input after watchdog failure.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class TemporaryFilesPopulationTests
{
    private const int OldCount = 2048;
    private sealed class DirectTempItems : IDisposable
    {
        private readonly List<string> created = [];
        public string Prefix { get; } = "LeanUiTempPopulation-" + Guid.NewGuid().ToString("N") + "-";
        public string[] ExpectedNames { get; }
        public DirectTempItems()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            ExpectedNames = Enumerable.Range(0, OldCount).Select(i => Prefix + "old-" + i.ToString("D4", CultureInfo.InvariantCulture) + ".bin").ToArray();
            try
            {
                foreach (var name in ExpectedNames)
                {
                    var path = Path.Combine(root, name);
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created.Add(path); // track ownership immediately after successful CreateNew, including failed writes
                        file.Write(new byte[4096]);
                    }
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                }
                var fresh = Path.Combine(root, Prefix + "fresh.bin");
                using (var file = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created.Add(fresh);
                    file.WriteByte(1);
                }
            }
            catch { Dispose(); throw; }
        }
        public void AssertUntouched()
        {
            foreach (var path in created) Assert.IsTrue(File.Exists(path), "read-only listing moved a fixture item: " + path);
        }
        public void Dispose() { foreach (var path in created) File.Delete(path); }
    }

    [TestMethod]
    [TestCategory("Console")]
    public void RealBulkTemporaryResultPopulationKeepsTheWindowResponsiveAndRowsUnticked()
    {
        using var items = new DirectTempItems();
        using var scan = new Fixture("ui-temp-population-scan", Fixture.ProfileBaseFolder());
        scan.File("one.bin", 4096);
        using var app = AppSession.Start("--scan", scan.Path);
        Ui.WaitForScan(app);
        var window = app.Window(Ui.WindowTimeout);
        var tempNavigation = Ui.SidebarItem(app, "TemporaryFiles");
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        using var watchdog = new NativeWatchdog(app.Process, handle, items.Prefix);
        try
        {
            // All UIA stays on this test thread. If this call blocks, the native watchdog terminates only this app;
            // no background UIA action survives to issue input during cleanup or another test.
            watchdog.Phase = "opening actual Temporary files";
            Ui.Click(tempNavigation);
            watchdog.Check();
            var view = Ui.FindAnywhere(app, "TempView", Ui.ElementTimeout);
            watchdog.Check();
            var format = Text.TempEstimate("\u0001");
            var split = format.IndexOf('\u0001');
            var prefix = format[..split];
            var suffix = format[(split + 1)..];
            watchdog.Phase = "reading and populating actual results";
            Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                watchdog.Check();
                var total = AppSession.TryFind(view, "TempTotal");
                if (total is null) return false;
                var text = total.Current.Name;
                return text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith(suffix, StringComparison.Ordinal)
                    && text.Length > prefix.Length + suffix.Length;
            }, TimeSpan.FromSeconds(90)), "real Temp listing never completed");
            watchdog.Check();

            watchdog.Phase = "observing logical rows, including virtualized rows";
            var list = AppSession.Find(view, "TempList", Ui.ElementTimeout);
            var seen = ObserveOwnRows(list, items, watchdog);
            CollectionAssert.AreEquivalent(items.ExpectedNames, seen.ToArray(), "exactly the 2048 old direct fixture files, with no fresh file or duplicate row");
            Assert.IsFalse(AppSession.Find(view, "MoveTemp", Ui.ElementTimeout).Current.IsEnabled, "no item is preselected anywhere in the list");
            watchdog.Check();
            // Population and traversal are fully observed. Join before normal close: a destroyed HWND can reject
            // WM_NULL immediately while its process is still exiting, which is not a population/UI-thread stall.
            watchdog.Dispose();
            watchdog.Check(); // retain a failure from the final in-flight probe before judging normal close separately
            watchdog.Phase = "normal close after population watchdog joined";
            Assert.IsTrue(app.Process.CloseMainWindow());
            Assert.IsTrue(app.Process.WaitForExit(5000), "read-only population must not prevent normal close");
            watchdog.Check();
            items.AssertUntouched();
        }
        catch (Exception error)
        {
            if (watchdog.Failure is { } failure) Assert.Fail(failure + Environment.NewLine + "UIA/phase failure: " + error);
            throw;
        }
        finally
        {
            // Join BEFORE AppSession.Dispose/fixture disposal. The watcher has no UIA or input code at all.
            watchdog.Dispose();
            Console.WriteLine("Temp population evidence: " + watchdog.WriteProof());
        }
    }

    private static HashSet<string> ObserveOwnRows(AutomationElement list, DirectTempItems items, NativeWatchdog watch)
    {
        var expression = new Regex(Regex.Escape(items.Prefix) + @"(?:old-\d{4}\.bin|fresh\.bin)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Observe(AutomationElement row, bool duplicateIsError)
        {
            watch.Check();
            var name = row.Current.Name;
            var match = expression.Match(name);
            if (!match.Success) return;
            var checkbox = row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TempItem"));
            Assert.IsNotNull(checkbox, "own logical Temp row must expose TempItem: " + match.Value);
            Assert.AreEqual(ToggleState.Off, ((TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState, match.Value + " must be unticked");
            var added = found.Add(match.Value);
            if (duplicateIsError) Assert.IsTrue(added, "duplicate logical fixture row: " + match.Value);
        }

        // IItemContainer enumerates logical items once, even when most have no current visual/container peer.
        // Realize one row at a time only if required; O(N), no repeated all-descendant scans for each filename.
        if (list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern))
        {
            var container = (ItemContainerPattern)pattern;
            AutomationElement? previous = null;
            for (var count = 0; count < 20000; count++)
            {
                watch.Check();
                var next = container.FindItemByProperty(previous, null!, null!);
                if (next is null) return found;
                if (next.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) ((VirtualizedItemPattern)virtualized).Realize();
                Observe(next, duplicateIsError: true);
                previous = next;
            }
            Assert.Fail("logical temporary-item traversal exceeded its explicit 20,000-item fixture/environment bound");
        }

        // Fallback for providers without IItemContainer: one snapshot per viewport, collecting unique names across pages.
        // Never require all 2048 checkbox peers to exist simultaneously. Offscreen peers are excluded from each snapshot.
        var scrollElement = list.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject) ? list
            : list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        if (scrollElement is not null) scrollObject = scrollElement.GetCurrentPattern(ScrollPattern.Pattern);
        var scroll = scrollObject as ScrollPattern;
        if (scroll is { Current.VerticallyScrollable: true }) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        for (var page = 0; page < 512; page++)
        {
            watch.Check();
            var rows = list.FindAll(TreeScope.Descendants, new AndCondition(
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            foreach (AutomationElement row in rows) Observe(row, duplicateIsError: false);
            if (scroll is null || !scroll.Current.VerticallyScrollable || scroll.Current.VerticalScrollPercent >= 100) return found;
            var before = scroll.Current.VerticalScrollPercent;
            scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            Assert.IsTrue(AppSession.WaitUntil(() => { watch.Check(); return scroll.Current.VerticalScrollPercent > before; }, TimeSpan.FromSeconds(5)),
                "Temp viewport did not advance after Scroll; no fixed sleep or synthetic listing delay");
        }
        Assert.Fail("temporary-item scrolling exceeded its explicit 512-page bound");
        return found;
    }

    private sealed class NativeWatchdog : IDisposable
    {
        private readonly Process process;
        private readonly IntPtr window;
        private readonly Thread thread;
        private readonly ManualResetEventSlim stopped = new(false);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> samples = [];
        private readonly string prefix;
        private volatile string phase = "starting";
        private string? failure;
        private bool disposed;
        public string Phase { set => phase = value; }
        public string? Failure => Volatile.Read(ref failure);
        public NativeWatchdog(Process process, IntPtr window, string prefix)
        {
            this.process = process; this.window = window; this.prefix = prefix;
            thread = new Thread(Run) { IsBackground = true, Name = "owned Temp population native watchdog" };
            thread.Start();
        }
        private void Run()
        {
            try
            {
                while (!stopped.Wait(100)) // sampling cadence only; never establishes test/UI ordering
                {
                    if (process.HasExited) return;
                    if (clock.Elapsed > TimeSpan.FromSeconds(180)) { StopOwnedApp("overall 180s population/traversal bound exceeded"); return; }
                    var started = clock.Elapsed.TotalSeconds;
                    var call = Stopwatch.StartNew();
                    var result = SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 3000, out _);
                    samples.Add(new { phase, seconds = started, responseMilliseconds = call.Elapsed.TotalMilliseconds, answered = result != IntPtr.Zero });
                    if (result == IntPtr.Zero && !process.HasExited) { StopOwnedApp("native window did not answer WM_NULL within 3s during " + phase); return; }
                }
            }
            catch (Exception error) { StopOwnedApp("watchdog error: " + error); }
        }
        private void StopOwnedApp(string reason)
        {
            Volatile.Write(ref failure, reason);
            try { if (!process.HasExited) { process.Kill(); _ = process.WaitForExit(5000); } }
            catch (Exception error) { Volatile.Write(ref failure, reason + "; stopping owned app failed: " + error); }
        }
        public void Check() { if (Failure is { } error) Assert.Fail(error); }
        public void Dispose()
        {
            if (disposed) return;
            stopped.Set();
            thread.Join(); // only native bounded sends/process stop remain; no UIA worker is abandoned
            stopped.Dispose();
            disposed = true;
        }
        public string WriteProof()
        {
            var directory = Environment.GetEnvironmentVariable("LEAN_TEMP_POPULATION_PROOF") ?? Path.Combine(Path.GetTempPath(), "LeanTempPopulationProof");
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("LEAN_TEMP_POPULATION_PROOF must be absolute");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, prefix + "proof.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { oldDirectFileCount = OldCount, bytesPerOldFile = 4096,
                syntheticListingDelay = false, globalKeyboardInput = false, nativeResponseLimitMilliseconds = 3000,
                elapsedSeconds = clock.Elapsed.TotalSeconds, phase, failure = Failure, samples }));
            return path;
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
