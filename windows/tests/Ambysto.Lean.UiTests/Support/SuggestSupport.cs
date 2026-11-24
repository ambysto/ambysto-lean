using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>ASP-17 phase C (intent/2026-10-20-asp-17-suggest.md): what the Suggest tests share. The Pro tests run in a Debug build
/// started with <c>--pro</c> (<see cref="StartPro"/>); a ProDebug test run against a Release build fails with a message
/// (<see cref="RequireDebugApp"/>). The Free tests (Console) run in Release (<see cref="RequireReleaseApp"/>).</summary>
internal static class ProApp
{
    /// <summary>The configuration this test assembly (and so the app <see cref="AppSession"/> starts) was built in.</summary>
    public static string Configuration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    public static void RequireDebugApp() =>
        Assert.AreEqual("Debug", Configuration,
            "this ProDebug test needs the DEBUG app (--pro exists only there), but the tests were built in " + Configuration
            + ": build and run with -c Debug (gates.json suite pro-debug)");

    public static void RequireReleaseApp()
    {
        if (Configuration != "Release")
        {
            Assert.Inconclusive("this test is about the RELEASE app (Suggest is Free there), but the tests were built in " + Configuration + ": run it with -c Release");
        }
    }

    /// <summary>Starts the Debug app as a Pro user: <c>--pro</c> and <paramref name="arguments"/>.</summary>
    public static AppSession StartPro(params string[] arguments)
    {
        RequireDebugApp();
        return AppSession.Start(["--pro", .. arguments]);
    }
}

/// <summary>The Suggest button of a view and what Free and Pro show around it.</summary>
internal static class SuggestUi
{
    public static AutomationElement Button(AutomationElement view) => AppSession.Find(view, "Suggest", Ui.ElementTimeout);

    public static AutomationElement? TryButton(AutomationElement view) => AppSession.TryFind(view, "Suggest");

    public static bool EnabledNow(AutomationElement? element)
    {
        try
        {
            return element is not null && element.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The Why column headers of <paramref name="view"/> (none: there is no Why column).</summary>
    public static List<AutomationElement> WhyHeaders(AutomationElement view) => Table.Headers(view, Text.ColumnWhy);

    public static bool HasWhyColumn(AutomationElement view) => WhyHeaders(view).Count > 0;

    /// <summary>Free: Suggest is shown, named <see cref="Text.Suggest"/>, disabled, with the quiet <see cref="Text.ProLabel"/>
    /// (<c>SuggestProLabel</c>) beside it, in the row of the Move button.</summary>
    public static AutomationElement AssertFree(AutomationElement view, string what)
    {
        var button = Button(view);
        Assert.AreEqual(Text.Suggest, button.Current.Name, what + ": the label of Suggest");
        Assert.IsFalse(button.Current.IsEnabled, what + ": Suggest is enabled in Free (docs/spec/06: Pro only)");
        var label = AppSession.Find(view, "SuggestProLabel", Ui.ElementTimeout);
        Assert.AreEqual(Text.ProLabel, label.Current.Name, what + ": SuggestProLabel");
        var b = button.Current.BoundingRectangle;
        var l = label.Current.BoundingRectangle;
        Assert.IsFalse(b.IsEmpty || l.IsEmpty, what + ": Suggest or its Pro label is not on screen");
        Assert.IsTrue(Math.Abs((b.Top + (b.Height / 2)) - (l.Top + (l.Height / 2))) <= Math.Max(b.Height, l.Height), $"{what}: the Pro label {l} is not beside Suggest {b}");
        return button;
    }

    /// <summary>Pro: Suggest is shown and enabled, named <see cref="Text.Suggest"/>, and no Pro label is shown for it.</summary>
    public static AutomationElement AssertPro(AutomationElement view, string what)
    {
        var button = Button(view);
        Assert.AreEqual(Text.Suggest, button.Current.Name, what + ": the label of Suggest");
        Assert.IsTrue(AppSession.WaitUntil(() => EnabledNow(button), Ui.ElementTimeout), what + ": Suggest is disabled in a Pro app");
        Assert.IsFalse(AppSession.WaitUntil(() => AppSession.TryFind(view, "SuggestProLabel") is not null, Ui.NeverWatch), what + ": SuggestProLabel is shown in a Pro app");
        return button;
    }

    /// <summary>A real left click with the mouse on the centre of <paramref name="element"/> (a disabled button cannot be invoked, so
    /// this is the only way to click it as a user does).</summary>
    public static void MouseClick(AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        Assert.IsFalse(r.IsEmpty, "precondition: " + element.Current.AutomationId + " is on screen");
        Mouse.LeftClickAt(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
    }

    /// <summary>The names of the windows of the app now (a dialog or popup is one more).</summary>
    public static string Windows(AppSession app) => string.Join(" || ", Ui.TopWindows(app).Select(t => Ui.Dump(t)));

    /// <summary>True while a scan runs, whichever view is shown: the status line is the scan's progress line ("Scanning ...", unlike
    /// <see cref="Ui.ScanRunning"/>, which looks for the Cancel button that only Overview shows).</summary>
    public static bool ScanRunningAnywhere(AppSession app)
    {
        try
        {
            var line = Text.ScanningLine("x", 1, "y");
            return Status(app).StartsWith(line[..line.IndexOf('x', StringComparison.Ordinal)], StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static AutomationElement? Scroller(AutomationElement view)
    {
        try
        {
            return view.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && ((ScrollPattern)p).Current.VerticallyScrollable);
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string RowRects(AutomationElement view, string boxId)
    {
        var parts = new List<string>();
        foreach (var box in Table.Controls(view, boxId))
        {
            try
            {
                parts.Add(Table.RowOf(box)?.Current.BoundingRectangle.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
            }
            catch (ElementNotAvailableException)
            {
                parts.Add("gone");
            }
        }

        return string.Join(";", parts);
    }

    /// <summary>ASP-14 / ASP-15 lesson, contract "One line per row": at the default window size every row of the table whose tick
    /// boxes are <paramref name="boxId"/> is one line high. Measured by rectangle after the layout has settled (two samples 150 ms apart
    /// equal): the rows wholly inside the scroll viewport are as high as each other (1.5 px), not higher than 44 px at 96 dpi (one line is about 36), and the
    /// texts of a row have their centres within 6 px of each other (one line). At least <paramref name="atLeast"/> rows are measured.</summary>
    public static void AssertOneLineRows(AppSession app, AutomationElement view, string boxId, string where, int atLeast = 4)
    {
        var last = RowRects(view, boxId);
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                Thread.Sleep(150);
                var now = RowRects(view, boxId);
                var same = now == last;
                last = now;
                return same;
            }, Ui.ElementTimeout), $"{where}: the rows never stopped changing size or place: {last}");

        var viewport = (Scroller(view)?.Current.BoundingRectangle) ?? view.Current.BoundingRectangle;
        var rows = new List<(string Name, double Height, double Spread)>();
        foreach (var box in Table.Controls(view, boxId))
        {
            try
            {
                var row = Table.RowOf(box);
                if (row is null)
                {
                    continue;
                }

                var rect = row.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Top <= viewport.Top + 2 || rect.Bottom >= viewport.Bottom - 2)
                {
                    continue; // a row the viewport cuts reads short: it is measured at another scroll position, or not at all
                }

                var centres = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                    .Select(t => t.Current.BoundingRectangle).Where(r => !r.IsEmpty).Select(r => r.Top + (r.Height / 2)).ToList();
                rows.Add((row.Current.Name ?? string.Empty, rect.Height, centres.Count == 0 ? 0 : centres.Max() - centres.Min()));
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        var scale = WindowOnScreen.Scale(new AutomationElementHandle(new nint(app.Window(Ui.WindowTimeout).Current.NativeWindowHandle)));
        Console.WriteLine($"{where}: row heights " + string.Join(" | ", rows.Select(r => $"{r.Height:F1}/{r.Spread:F1}")));
        Assert.IsTrue(rows.Count >= atLeast, $"{where}: only {rows.Count} whole rows measured on screen (need {atLeast})");
        Assert.IsTrue(rows.Max(r => r.Height) - rows.Min(r => r.Height) <= 1.5,
            $"{where}: rows are not all as high as each other: " + string.Join(" | ", rows.Select(r => $"{r.Height:F1} px: {r.Name}")));
        Assert.IsTrue(rows.Max(r => r.Height) <= 44 * scale, $"{where}: a row is {rows.Max(r => r.Height):F1} px high, more than one line");
        foreach (var row in rows.Where(r => r.Spread > 6 * scale))
        {
            Assert.Fail($"{where}: the texts of one row are on more than one line (their centres are {row.Spread:F1} px apart): {row.Name}");
        }
    }

    /// <summary>The tooltip of the text cell of <paramref name="row"/> that shows <paramref name="needle"/>: the HelpText of the text
    /// element or of an ancestor below the row; null when none holds <paramref name="needle"/> (a cell trimmed with an ellipsis must
    /// have its full text as its tooltip).</summary>
    public static string? TooltipHolding(AutomationElement row, string needle)
    {
        try
        {
            foreach (AutomationElement text in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
            {
                if (!(text.Current.Name ?? string.Empty).Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }

                for (var node = text; node is not null && !Automation.Compare(node, row); node = TreeWalker.ControlViewWalker.GetParent(node))
                {
                    var help = node.Current.HelpText ?? string.Empty;
                    if (help.Contains(needle, StringComparison.Ordinal))
                    {
                        return help;
                    }
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    /// <summary>Asserts the row of <paramref name="box"/> has a cell with the full text <paramref name="needle"/> and that cell's tooltip is that full text.</summary>
    public static void AssertTooltipHolds(AutomationElement box, string needle, string what)
    {
        var row = Table.RowOf(box) ?? throw new AssertFailedException(what + ": the box is in no row");
        Assert.IsNotNull(TooltipHolding(row, needle), $"{what}: no text cell showing \"{needle}\" has it as its tooltip (a trimmed text must keep its full text as tooltip); texts: "
            + string.Join(" | ", Table.Texts(row).Select(t => $"[{t.Name}] help [{t.Help}]")));
    }

    /// <summary>The program files the over-100 question counts (ASP-15's list, owner 2026-10-19), by the row's file extension; a folder row never counts.</summary>
    public static readonly HashSet<string> ProgramTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".sys", ".ocx", ".drv", ".cpl", ".pyd", ".so", ".node", ".jar", ".winmd",
    };

    /// <summary>How many direct children of <paramref name="temp"/> are FILES with a program extension whose last write is before
    /// one month ago: the program files Suggest counts among the rows it would tick (every such file is a movable row; a folder named like
    /// a program file is not counted).</summary>
    public static int ProgramFilesOlderThanAMonth(string temp)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-1);
        return Directory.EnumerateFiles(temp).Count(f => ProgramTypes.Contains(System.IO.Path.GetExtension(f))
            && (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0 && File.GetLastWriteTimeUtc(f) < cutoff);
    }

    /// <summary>The text of the status line.</summary>
    public static string Status(AppSession app) => AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout).Current.Name;

    /// <summary>The first whole number of <paramref name="text"/> after <paramref name="before"/> (counts are read from a dialog).</summary>
    public static int NumberAfter(string text, string before)
    {
        var at = text.IndexOf(before, StringComparison.Ordinal);
        Assert.IsTrue(at >= 0, $"\"{before}\" is not in \"{text}\"");
        var digits = new string(text[(at + before.Length)..].TakeWhile(c => char.IsDigit(c) || c == ',' || c == '.' || c == ' ' || c == ' ').ToArray())
            .Where(char.IsDigit).ToArray();
        return int.Parse(new string(digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The Why text of a row of <see cref="Text.WhyOldTemp"/>, <see cref="Text.WhyOldInstaller"/> and the like, as the
    /// words before the date: the text for an arbitrary date, minus that date.</summary>
    public static string BeforeTheDate(string why, DateTime utc)
    {
        var date = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        Assert.IsTrue(why.EndsWith(date, StringComparison.Ordinal), $"\"{why}\" does not end with the date {date}");
        return why[..^date.Length];
    }

    /// <summary>The words of <see cref="Text.WhyOldTemp"/> before its date ("Unchanged since ").</summary>
    public static string TempWhyPrefix() => BeforeTheDate(Text.WhyOldTemp(DateTime.UtcNow), DateTime.UtcNow);

    /// <summary>The words of <see cref="Text.WhyOldInstaller"/> before its date.</summary>
    public static string InstallerWhyPrefix() => BeforeTheDate(Text.WhyOldInstaller(DateTimeOffset.UtcNow), DateTime.UtcNow);

    /// <summary>The words of <see cref="Text.WhyLargeUnused"/> before its date.</summary>
    public static string LargeWhyPrefix() => BeforeTheDate(Text.WhyLargeUnused(DateTimeOffset.UtcNow), DateTime.UtcNow);

    /// <summary>A UTC moment cut to the whole minute, as the scan keeps times (walk rule 8).</summary>
    public static DateTime WholeMinute(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
}

/// <summary>The Downloads known folder (<c>FOLDERID_Downloads</c>) of this user, which the app reads for the installer rule.</summary>
internal static partial class DownloadsFolder
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Path
    {
        get
        {
            var hr = SHGetKnownFolderPath(in FolderIdDownloads, 0, nint.Zero, out var path);
            Assert.AreEqual(0, hr, "SHGetKnownFolderPath(Downloads) failed");
            try
            {
                return Marshal.PtrToStringUni(path)!;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, nint token, out nint path);
}

/// <summary>A scratch folder inside the user's real Downloads known folder (<c>Downloads\LeanUiSuggest-&lt;name&gt;-&lt;id&gt;</c>,
/// so it cannot clash with anything), because the installer rule is about the Downloads folder and the app reads that one: the known
/// folder cannot be pointed at a scratch folder without changing the user's setting. A scan of this folder sees only the test's own
/// files, all strictly inside Downloads. Removed on dispose, with Downloads itself only when the fixture made it and it is empty.</summary>
internal sealed class DownloadsFixture : IDisposable
{
    private readonly bool madeDownloads;

    public DownloadsFixture(string name)
    {
        var downloads = DownloadsFolder.Path;
        madeDownloads = !Directory.Exists(downloads);
        Path = System.IO.Path.Combine(downloads, "LeanUiSuggest-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string relative) => System.IO.Path.Combine([Path, .. relative.Split('\\')]);

    /// <summary>A file of <paramref name="length"/> bytes whose last write is <paramref name="daysAgo"/> days before now (whole
    /// minute), parents created. Lengths in whole 4 KB clusters keep the allocated size equal to the length.</summary>
    public string File(string relative, long length, int daysAgo, int seed = 1)
    {
        var path = Sub(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var block = new byte[1 << 20];
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = (byte)((i * 31 + seed * 131 + 7) & 0xFF);
            }

            long written = 0;
            while (written < length)
            {
                var count = (int)Math.Min(block.Length, length - written);
                stream.Write(block, 0, count);
                written += count;
            }
        }

        System.IO.File.SetLastWriteTimeUtc(path, WrittenAt(daysAgo));
        return path;
    }

    /// <summary>An empty file (no content, no allocation), recent, parents created.</summary>
    public string Empty(string relative)
    {
        var path = Sub(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (System.IO.File.Create(path))
        {
        }

        return path;
    }

    /// <summary>The time <see cref="File"/> gives a file <paramref name="daysAgo"/> days old, as the app reads it (whole minute).</summary>
    public static DateTime WrittenAt(int daysAgo) => SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-daysAgo));

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        if (madeDownloads)
        {
            try
            {
                var downloads = System.IO.Path.GetDirectoryName(Path)!;
                if (Directory.Exists(downloads) && !Directory.EnumerateFileSystemEntries(downloads).Any())
                {
                    Directory.Delete(downloads);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>The test's own items among the children of this user's real <c>%LOCALAPPDATA%\Temp</c> (named <c>LeanUiSuggest-*</c>),
/// aged relative to now and to the one-month line of Suggest (<c>now.AddMonths(-1)</c>): <see cref="Over"/> a day older than the
/// line, <see cref="Under"/> a day newer (both are listed at a month's age, and Under at the week's default too), a folder
/// <see cref="Folder"/> whose newest file is 35 days old, <see cref="Old"/> 60 days, and <see cref="Recent"/> 20 days. Every time is
/// set to the same instant; removed on dispose.</summary>
internal sealed class SuggestTempFixture : IDisposable
{
    private readonly List<string> paths = [];

    public SuggestTempFixture(string tag = "t")
    {
        var id = tag + "-" + Guid.NewGuid().ToString("N")[..8];
        TempFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        var line = DateTime.UtcNow.AddMonths(-1);
        Old = Make("LeanUiSuggest-old-" + id + ".bin", SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-60)));
        Long = Make("LeanUiSuggest-a-very-long-temporary-item-name-" + id + ".bin", SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-61)));
        Over = Make("LeanUiSuggest-over-" + id + ".bin", SuggestUi.WholeMinute(line.AddDays(-1)));
        Under = Make("LeanUiSuggest-under-" + id + ".bin", SuggestUi.WholeMinute(line.AddDays(1)));
        Recent = Make("LeanUiSuggest-recent-" + id + ".bin", SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-20)));
        var folder = System.IO.Path.Combine(TempFolder, "LeanUiSuggest-folder-" + id);
        var folderTime = SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-35));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(System.IO.Path.Combine(folder, "inside.bin"), new byte[100 * 1024]);
        File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "inside.bin"), folderTime);
        Directory.SetLastWriteTimeUtc(folder, folderTime);
        paths.Add(folder);
        Folder = new Item(folder, folderTime);
    }

    /// <summary>A direct child of Temp and the newest write over it.</summary>
    public sealed record Item(string Path, DateTime NewestUtc)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public string TempFolder { get; }

    /// <summary>60 days old.</summary>
    public Item Old { get; }

    /// <summary>61 days old, with a name of more than 55 characters (a row of the one-line test).</summary>
    public Item Long { get; }

    /// <summary>A day older than one month: suggested.</summary>
    public Item Over { get; }

    /// <summary>A day newer than one month: not suggested.</summary>
    public Item Under { get; }

    /// <summary>20 days old: listed at the week's default, not suggested.</summary>
    public Item Recent { get; }

    /// <summary>A folder whose newest file is 35 days old: suggested.</summary>
    public Item Folder { get; }

    /// <summary>The items Suggest ticks.</summary>
    public Item[] Suggested => [Old, Long, Over, Folder];

    /// <summary>The items Suggest leaves unticked.</summary>
    public Item[] NotSuggested => [Under, Recent];

    private Item Make(string name, DateTime utc)
    {
        var path = System.IO.Path.Combine(TempFolder, name);
        File.WriteAllBytes(path, new byte[100 * 1024]);
        File.SetCreationTimeUtc(path, utc);
        File.SetLastWriteTimeUtc(path, utc);
        File.SetLastAccessTimeUtc(path, utc);
        paths.Add(path);
        return new Item(path, utc);
    }

    /// <summary>More children of Temp, <paramref name="days"/> days old: <paramref name="bin"/> files named .bin and
    /// <paramref name="dll"/> named .dll (program files), and with <paramref name="folderNamedDll"/> a folder named .dll; removed on dispose.</summary>
    public List<Item> Bulk(int bin, int dll, int days, bool folderNamedDll = false)
    {
        var made = new List<Item>();
        var utc = SuggestUi.WholeMinute(DateTime.UtcNow.AddDays(-days));
        var id = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < bin; i++)
        {
            made.Add(Make($"LeanUiSuggest-bulk-{id}-{i:D3}.bin", utc));
        }

        for (var i = 0; i < dll; i++)
        {
            made.Add(Make($"LeanUiSuggest-bulk-{id}-{i:D3}.dll", utc));
        }

        if (folderNamedDll)
        {
            // A folder row named like a program file: it is suggested (old enough) but never counted as a program file.
            var folder = System.IO.Path.Combine(TempFolder, $"LeanUiSuggest-bulk-{id}-dir.dll");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(System.IO.Path.Combine(folder, "inside.bin"), new byte[4096]);
            File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "inside.bin"), utc);
            Directory.SetLastWriteTimeUtc(folder, utc);
            paths.Add(folder);
            made.Add(new Item(folder, utc));
        }

        return made;
    }

    public void Dispose()
    {
        foreach (var path in paths)
        {
            for (var attempt = 0; attempt < 5 && (File.Exists(path) || Directory.Exists(path)); attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}

/// <summary>The over-100 confirmation of Suggest (<see cref="Text.SuggestConfirm"/>, with Tick and Cancel) and the move confirmation.</summary>
internal static class SuggestDialog
{
    /// <summary>The words every <see cref="Text.SuggestConfirm"/> starts with ("Suggest ticks ").</summary>
    public static string Prefix { get; } = Text.SuggestConfirm(1, 0)[..Text.SuggestConfirm(1, 0).IndexOf('1', StringComparison.Ordinal)];

    public static AutomationElement? ButtonNamed(AutomationElement scope, string name)
    {
        try
        {
            return scope.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name)));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The confirmation now (the element reading <see cref="Prefix"/>… with a Tick and a Cancel button beside it), or null.</summary>
    public static (AutomationElement Scope, string Text)? Current(AppSession app)
    {
        foreach (var top in Ui.TopWindows(app))
        {
            try
            {
                var line = top.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                    .FirstOrDefault(e => (e.Current.Name ?? string.Empty).StartsWith(Prefix, StringComparison.Ordinal) && Table.RowOf(e) is null);
                for (var node = line; node is not null; node = TreeWalker.ControlViewWalker.GetParent(node))
                {
                    if (ButtonNamed(node, Text.RuleConfirmTick) is not null && ButtonNamed(node, Text.Cancel) is not null)
                    {
                        return (node, line!.Current.Name.Trim());
                    }

                    if (Automation.Compare(node, top))
                    {
                        break;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return null;
    }

    /// <summary>Waits for the confirmation; fails with the windows' texts when none comes.</summary>
    public static (AutomationElement Scope, string Text) Wait(AppSession app)
    {
        (AutomationElement Scope, string Text)? found = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (found = Current(app)) is not null, Ui.ElementTimeout),
            $"no confirmation reading \"{Prefix}…\" with Tick and Cancel buttons; windows: " + SuggestUi.Windows(app));
        return found!.Value;
    }

    /// <summary>True while a Suggest confirmation is shown.</summary>
    public static bool Shown(AppSession app) => Current(app) is not null;

    public static void Press(AppSession app, AutomationElement scope, string button)
    {
        Ui.Click(ButtonNamed(scope, button) ?? throw new AssertFailedException("no " + button + " button in the confirmation"));
        Assert.IsTrue(AppSession.WaitUntil(() => !Shown(app), Ui.ElementTimeout), $"the confirmation stays open after {button}");
    }

    /// <summary>Asserts the text is <see cref="Text.SuggestConfirm"/> of the counts it holds and returns them (items, program files).</summary>
    public static (int Items, int ProgramFiles) Counts(string text)
    {
        var items = SuggestUi.NumberAfter(text, Prefix);
        var program = text.Contains("program", StringComparison.OrdinalIgnoreCase) ? SuggestUi.NumberAfter(text, ", ") : 0;
        Assert.AreEqual(Text.SuggestConfirm(items, program), text, "the confirmation is not Text.SuggestConfirm of the counts it holds");
        return (items, program);
    }
}
