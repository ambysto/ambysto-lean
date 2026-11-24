using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>ASP-16 (intent/2026-10-20-asp-16-uninstall-table.md): the test's own uninstall entries, with known values, under HKCU's
/// Uninstall key (<c>AmbystoLeanUiTest-&lt;guid&gt;</c>, per-user, so the app lists them as removable) and the folders they point to. Their
/// display names start with <c>0LeanUiTable-&lt;id&gt;-</c>, which sorts before almost every real name, so they are the first rows of the
/// list in the inventory's order. Nothing here touches a real entry. On dispose the keys, the folders and any Windows Settings window the
/// hand-off opened are removed.
/// <list type="bullet">
/// <item><see cref="Full"/> A: every value, and an <c>InstallLocation</c> that is a folder that exists (it holds <see cref="MarkerName"/>);</item>
/// <item><see cref="Second"/> B: other values, and an <c>InstallLocation</c> that is rooted but whose folder does not exist;</item>
/// <item><see cref="Bare"/> C: a display name only;</item>
/// <item><see cref="Relative"/> D: <c>InstallLocation</c> is "." (not rooted, and "exists" for the current directory);</item>
/// <item><see cref="FileLocation"/> E: <c>InstallLocation</c> is a rooted path to a file, not a folder;</item>
/// <item><see cref="Long"/> F: a very long name, publisher and version, and the other values;</item>
/// <item><see cref="Disappearing"/> G: <c>InstallLocation</c> is a folder that exists when the view is opened and that the test then removes;</item>
/// <item><see cref="DriveRelative"/> H: <c>C:name</c>, a drive-relative path to a folder that exists relative to the app's current directory;</item>
/// <item><see cref="RootedNoDrive"/> I: <c>\Windows</c>, rooted without a drive, and the folder exists on the current drive;</item>
/// <item><see cref="UncShare"/> J: <c>\\localhost\ambynet\…</c>, a folder that exists on the loopback share (the share's root when no folder can be made);</item>
/// <item><see cref="UncExtended"/> K: <c>\\?\UNC\localhost\ambynet\…</c>, the same folder in the extended form;</item>
/// <item><see cref="DeviceUnc"/> M: <c>\\.\UNC\localhost\ambynet\…</c>, the device form of a network path: no magnifier;</item>
/// <item><see cref="ExtendedLocal"/> N: <c>\\?\C:\…</c>, the extended form of a local folder of its own: the magnifier is shown;</item>
/// <item><see cref="Junction"/> O: a junction (<c>mklink /J</c>, no privilege needed) to a local folder of its own: a link to a local folder, so the magnifier is shown;</item>
/// <item><see cref="Mapped"/> P (only with <c>mappedDriveLetter</c>): a folder on a drive letter mapped to the loopback share: no magnifier;</item>
/// <item><see cref="Unreachable"/> L (only with <c>withUnreachable</c>): a share on an address nothing answers on, which the app must never probe.</item>
/// </list></summary>
internal sealed class UninstallTestApps : IDisposable
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>One test entry: the values written (null: not written).</summary>
    public sealed record App(string Name, string KeyPath, string? Publisher, string? Version, int? SizeKb, string? InstallDate, string? InstallLocation)
    {
        /// <summary>The size cell the table shows (Explorer's format of the KB × 1024), or empty.</summary>
        public string SizeText => SizeKb is { } kb ? Ambysto.Lean.Core.Measure.ByteUnits.Format(kb * 1024L) : string.Empty;

        /// <summary>The installed-on cell: the date as written, in the short date of the current culture, or empty.</summary>
        public string InstalledText => InstallDate is { } d
            ? DateOnly.ParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture).ToString("d", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private readonly Fixture folder = new("ui-uninstall-table");
    private readonly List<AutomationElement> settingsBefore;
    private readonly string disappearing;

    public UninstallTestApps(bool withUnreachable = false, char? mappedDriveLetter = null)
    {
        Id = Guid.NewGuid().ToString("N")[..8];
        settingsBefore = SettingsWindows();
        InstallFolder = Path.Combine(folder.Path, "LeanUiTableApp-" + Id);
        Directory.CreateDirectory(InstallFolder);
        MarkerName = "marker-" + Id + ".leanuimarker"; // an extension no program owns, so Explorer shows the whole name
        File.WriteAllBytes(Path.Combine(InstallFolder, MarkerName), new byte[1024]);
        var file = Path.Combine(folder.Path, "a-file-" + Id + ".leanuimarker");
        File.WriteAllBytes(file, new byte[16]);
        disappearing = Path.Combine(folder.Path, "LeanUiTableGone-" + Id);
        Directory.CreateDirectory(disappearing);
        File.WriteAllBytes(Path.Combine(disappearing, "x.leanuimarker"), new byte[16]);

        Full = Add("A", "LeanUiPublisher-" + Id + "-A", "3.1.4", 2048, "20240315", InstallFolder);
        Second = Add("B", "LeanUiPublisher-" + Id + "-B", "9.0.0", 8192, "20250101", Path.Combine(folder.Path, "no-such-folder-" + Id));
        Bare = Add("C", null, null, null, null, null);
        Relative = Add("D", null, null, null, null, ".");
        FileLocation = Add("E", null, null, null, null, file);
        Long = Add("F-" + new string('x', 250), "LeanUiLongPublisher-" + Id + "-" + new string('p', 110), "1.0." + new string('9', 50), 1, "20200101", null);
        Disappearing = Add("G", "LeanUiPublisher-" + Id + "-G", "1.0", 1024, "20230202", disappearing);

        // Not fully qualified local folders that nevertheless exist (or would, resolved the way the app resolves them): none may show a magnifier.
        var current = Directory.GetCurrentDirectory();
        var sub = Directory.EnumerateDirectories(current).Select(Path.GetFileName).FirstOrDefault();
        if (sub is null)
        {
            sub = "LeanUiRel-" + Id;
            Directory.CreateDirectory(Path.Combine(current, sub));
            madeFolders.Add(Path.Combine(current, sub));
        }

        var drive = Path.GetPathRoot(current)![..2]; // "C:"; relative to the process's current directory when that is on this drive
        DriveRelative = Add("H", null, null, null, null, drive + sub);
        RootedNoDrive = Add("I", null, null, null, null, @"\Windows");
        var shareFolder = @"\\localhost\ambynet";
        try
        {
            var made = Path.Combine(shareFolder, "LeanUiTableApp-" + Id);
            Directory.CreateDirectory(made);
            madeFolders.Add(made);
            shareFolder = made;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the share's root exists too
        }

        Assert.IsTrue(Directory.Exists(shareFolder), "precondition: the loopback share " + shareFolder + " is not reachable");
        UncShare = Add("J", null, null, null, null, shareFolder);
        UncExtended = Add("K", null, null, null, null, @"\\?\UNC\" + shareFolder[2..]);
        DeviceUnc = Add("M", null, null, null, null, @"\\.\UNC\" + shareFolder[2..]);
        // Their own folders, not A's: an entry that names the same install location as A would stop A from ever counting as gone.
        var extendedTarget = Directory.CreateDirectory(Path.Combine(folder.Path, "LeanUiTableExt-" + Id)).FullName;
        var junctionTarget = Directory.CreateDirectory(Path.Combine(folder.Path, "LeanUiTableJct-" + Id)).FullName;
        ExtendedLocal = Add("N", null, null, null, null, @"\\?\" + extendedTarget);
        junctionPath = Path.Combine(folder.Path, "LeanUiTableLink-" + Id);
        var link = Cmd.Run("/c mklink /J \"" + junctionPath + "\" \"" + junctionTarget + "\"");
        Assert.AreEqual(0, link.ExitCode, "precondition: mklink /J failed: " + link.Output);
        Junction = Add("O", null, null, null, null, junctionPath);
        if (mappedDriveLetter is { } letter)
        {
            var leaf = shareFolder.Length > @"\\localhost\ambynet".Length ? shareFolder[(@"\\localhost\ambynet".Length + 1)..] : string.Empty;
            Mapped = Add("P", null, null, null, null, letter + @":\" + leaf);
        }

        if (withUnreachable)
        {
            Unreachable = Add("L", null, null, null, null, @"\\192.0.2.1\leanuitable\app-" + Id); // TEST-NET-1: reserved, nothing answers
        }
    }

    public string Id { get; }

    /// <summary>The install folder of <see cref="Full"/>; it holds <see cref="MarkerName"/>.</summary>
    public string InstallFolder { get; }

    public string MarkerName { get; }

    public App Full { get; }

    public App Second { get; }

    public App Bare { get; }

    public App Relative { get; }

    public App FileLocation { get; }

    public App Long { get; }

    public App Disappearing { get; }

    private readonly List<string> madeFolders = [];
    private readonly string? junctionPath;

    /// <summary>The device form of a network path, <c>\\.\UNC\…</c>.</summary>
    public App DeviceUnc { get; }

    /// <summary>The install folder of <see cref="Full"/> in the extended form <c>\\?\C:\…</c>.</summary>
    public App ExtendedLocal { get; }

    /// <summary>A junction to the install folder of <see cref="Full"/>.</summary>
    public App Junction { get; }

    /// <summary>A folder on a mapped network drive; null unless the set was made with <c>mappedDriveLetter</c>.</summary>
    public App? Mapped { get; }

    public App DriveRelative { get; }

    public App RootedNoDrive { get; }

    public App UncShare { get; }

    public App UncExtended { get; }

    /// <summary>The entry on an unreachable address; null unless the set was made with <c>withUnreachable</c>.</summary>
    public App? Unreachable { get; }

    /// <summary>The folder of <see cref="Disappearing"/>.</summary>
    public string DisappearingFolder => disappearing;

    public IReadOnlyList<App> All => [Full, Second, Bare, Relative, FileLocation, Long, Disappearing, DriveRelative, RootedNoDrive, UncShare, UncExtended, DeviceUnc, ExtendedLocal, Junction, .. MappedOrNone, .. UnreachableOrNone];

    private IEnumerable<App> MappedOrNone => Mapped is null ? [] : [Mapped];

    /// <summary>The entries whose install folder is a local folder that exists, so that the magnifier is shown: A, G (until the test removes it), N and the junction O.</summary>
    public IReadOnlyList<App> WithMagnifier => [Full, Disappearing, ExtendedLocal, Junction];

    private IEnumerable<App> UnreachableOrNone => Unreachable is null ? [] : [Unreachable];

    /// <summary>The entries whose location is not a fully qualified local folder although a folder may exist there: no magnifier.</summary>
    public IReadOnlyList<App> NotLocalAbsolute => [DriveRelative, RootedNoDrive, UncShare, UncExtended, DeviceUnc];

    /// <summary>The entries with no publisher, version, size or install date.</summary>
    public IReadOnlyList<App> Empties => [Bare, Relative, FileLocation];

    private readonly List<App> made = [];

    private App Add(string suffix, string? publisher, string? version, int? sizeKb, string? installDate, string? location)
    {
        var name = "0LeanUiTable-" + Id + "-" + suffix;
        var keyPath = UninstallPath + @"\AmbystoLeanUiTest-" + Guid.NewGuid().ToString("D");
        using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            Assert.IsNull(existing, "precondition: the scratch key exists already");
        }

        var app = new App(name, keyPath, publisher, version, sizeKb, installDate, location);
        made.Add(app); // before it is written: a failure half way still removes the key
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("DisplayName", name);
        if (publisher is not null)
        {
            key.SetValue("Publisher", publisher);
        }

        if (version is not null)
        {
            key.SetValue("DisplayVersion", version);
        }

        if (sizeKb is { } kb)
        {
            key.SetValue("EstimatedSize", kb, RegistryValueKind.DWord);
        }

        if (installDate is not null)
        {
            key.SetValue("InstallDate", installDate);
        }

        if (location is not null)
        {
            key.SetValue("InstallLocation", location);
        }

        key.SetValue("UninstallString", "\"" + Path.Combine(folder.Path, "uninstall-" + Id + ".exe") + "\"");
        return app;
    }

    // ---- the list --------------------------------------------------------------------------------------------------------------

    /// <summary>Opens Uninstall and waits until every test entry has its row; returns the view.</summary>
    public AutomationElement OpenAndWait(AppSession app)
    {
        var view = Ui.OpenView(app, "Uninstall", "UninstallView");
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        foreach (var entry in All)
        {
            Assert.IsNotNull(AppSession.WaitFor(() => RowNamed(list, entry.Name), Ui.ElementTimeout),
                $"precondition: the Uninstall list has no UninstallRow named {entry.Name}: {Table.Dump(view, "UninstallRow")}");
        }

        return view;
    }

    /// <summary>The row of <paramref name="entry"/>, found again (a rebuilt list replaces its rows).</summary>
    public static AutomationElement RowOf(AutomationElement view, App entry)
    {
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var row = AppSession.WaitFor(() => RowNamed(list, entry.Name), Ui.ElementTimeout);
        Assert.IsNotNull(row, $"no UninstallRow named {entry.Name}");
        return row;
    }

    /// <summary>The UninstallRow named <paramref name="name"/> in <paramref name="list"/>, realised if the list virtualises it.</summary>
    public static AutomationElement? RowNamed(AutomationElement list, string name)
    {
        try
        {
            var row = AppSession.FindAll(list, "UninstallRow").FirstOrDefault(r => r.Current.Name == name);
            if (row is not null || !list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var container))
            {
                return row;
            }

            var item = ((ItemContainerPattern)container).FindItemByProperty(null, AutomationElement.NameProperty, name);
            if (item is not null && item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
            {
                ((VirtualizedItemPattern)virtualized).Realize();
            }

            if (item is not null && item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            {
                ((ScrollItemPattern)scroll).ScrollIntoView();
            }

            return item;
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The names of the realised rows of the list, top to bottom.</summary>
    public static List<string> RowNamesNow(AutomationElement list)
    {
        try
        {
            return AppSession.FindAll(list, "UninstallRow")
                .Select(r => (Top: r.Current.BoundingRectangle.Top, Name: r.Current.Name))
                .OrderBy(r => r.Top).Select(r => r.Name).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    // ---- the cells -------------------------------------------------------------------------------------------------------------

    /// <summary>A column of the apps table: the header's name and its horizontal extent on screen (this header's left to the next header's left).</summary>
    public sealed record Column(string Name, double Left, double Right, double HeaderWidth);

    /// <summary>The six columns, left to right, from their header buttons.</summary>
    public static List<Column> Columns(AutomationElement view)
    {
        var names = new[] { Text.ColumnName, Text.ColumnPublisher, Text.ColumnVersion, Text.ColumnSize, Text.ColumnInstalledOn, Text.ColumnLastUpdated };
        var headers = names.Select(n => Table.Header(view, n)).ToList();
        var lefts = headers.Select(h => h.Current.BoundingRectangle.Left).ToList();
        var tableRight = AppSession.Find(view, "UninstallList", Ui.ElementTimeout).Current.BoundingRectangle.Right;
        var columns = new List<Column>();
        for (var i = 0; i < names.Length; i++)
        {
            columns.Add(new Column(names[i], lefts[i], i + 1 < names.Length ? lefts[i + 1] : tableRight, headers[i].Current.BoundingRectangle.Width));
        }

        return columns;
    }

    /// <summary>The text elements of <paramref name="row"/> with a non-empty name whose centre lies in <paramref name="column"/>: (name, help text, rectangle).</summary>
    public static List<(string Name, string Help, System.Windows.Rect Rect)> CellTexts(AutomationElement row, Column column)
    {
        var found = new List<(string, string, System.Windows.Rect)>();
        try
        {
            foreach (AutomationElement element in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
            {
                var rect = element.Current.BoundingRectangle;
                var name = element.Current.Name ?? string.Empty;
                if (rect.IsEmpty || name.Length == 0)
                {
                    continue;
                }

                var centre = rect.Left + (rect.Width / 2);
                if (centre >= column.Left && centre < column.Right && !found.Any(f => f.Item1 == name && f.Item3 == rect))
                {
                    found.Add((name, element.Current.HelpText ?? string.Empty, rect));
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return found;
    }

    /// <summary>What a column of <paramref name="row"/> shows: the names of its text elements joined, empty for an empty cell.</summary>
    public static string CellText(AutomationElement row, Column column) => string.Join(" ", CellTexts(row, column).Select(t => t.Name));

    /// <summary>True when the element is on screen: not off screen and with a rectangle.</summary>
    public static bool Shown(AutomationElement? element)
    {
        try
        {
            return element is not null && !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The RevealRow of <paramref name="row"/> when it is shown now, else null.</summary>
    public static AutomationElement? ShownReveal(AutomationElement row) => Table.InRow(row, "RevealRow") is { } b && Shown(b) ? b : null;

    // ---- the hand-off ----------------------------------------------------------------------------------------------------------

    /// <summary>Presses UninstallInSettings for the selected test entry <paramref name="name"/> (its own entry; Windows Settings opens and
    /// the watch starts, as GoneApp does), waits for the instruction naming it and closes the Settings window it opened.</summary>
    public void HandOff(AutomationElement view, string name)
    {
        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => Shown(offer) && offer.Current.IsEnabled, TimeSpan.FromSeconds(5)), "UninstallInSettings is not shown and enabled for " + name);
        Ui.Click(offer);
        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => instruction.Current.Name == Text.UninstallInSettings(name), Ui.ElementTimeout),
            $"the hand-off did not start for {name}: UninstallInstruction \"{instruction.Current.Name}\"");
        AppSession.WaitUntil(() => SettingsWindows().Count > settingsBefore.Count, TimeSpan.FromSeconds(10));
        CloseNewSettingsWindows();
    }

    /// <summary>How many Windows Settings windows are open that were not open when this set was made.</summary>
    public int NewSettingsWindows()
    {
        var known = settingsBefore.Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        return SettingsWindows().Count(w => !known.Contains(Ui.Key(w)));
    }

    private static List<AutomationElement> SettingsWindows() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"))
            .Cast<AutomationElement>().Where(w =>
            {
                try
                {
                    return w.Current.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }).ToList();

    private void CloseNewSettingsWindows()
    {
        var known = settingsBefore.Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var window in SettingsWindows().Where(w => !known.Contains(Ui.Key(w))))
        {
            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                {
                    ((WindowPattern)pattern).Close();
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        try
        {
            CloseNewSettingsWindows();
        }
        finally
        {
            foreach (var entry in made)
            {
                if (Registry.CurrentUser.OpenSubKey(entry.KeyPath) is { } left)
                {
                    left.Dispose();
                    Registry.CurrentUser.DeleteSubKeyTree(entry.KeyPath, throwOnMissingSubKey: false);
                }
            }

            if (junctionPath is not null && Directory.Exists(junctionPath))
            {
                try
                {
                    Directory.Delete(junctionPath, recursive: false); // removes the junction only
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var made in madeFolders)
            {
                try
                {
                    if (Directory.Exists(made))
                    {
                        Directory.Delete(made, recursive: true);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            folder.Dispose();
        }
    }
}

/// <summary>File Explorer windows opened during a test (the magnifier): the ones there at the start are known, and every other
/// <c>CabinetWClass</c> window is closed on dispose, so a test leaves none behind.</summary>
internal sealed class ExplorerWindows : IDisposable
{
    private readonly HashSet<string> known;

    public ExplorerWindows()
    {
        known = Now().Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The File Explorer windows that were not there at the start.</summary>
    public List<AutomationElement> New() => Now().Where(w => !known.Contains(Ui.Key(w))).ToList();

    private static List<AutomationElement> Now() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass"))
            .Cast<AutomationElement>().ToList();

    public static string NameOf(AutomationElement window)
    {
        try
        {
            return window.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        foreach (var window in New())
        {
            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                {
                    ((WindowPattern)pattern).Close();
                }
            }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException)
            {
            }
        }

        AppSession.WaitUntil(() => New().Count == 0, TimeSpan.FromSeconds(10));
    }
}

/// <summary>Runs <c>cmd.exe</c> with arguments (test set-up only: mklink, net use) and returns its exit code and output.</summary>
internal static class Cmd
{
    public static (int ExitCode, string Output) Run(string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(30_000), "cmd.exe " + arguments + " did not end within 30 s");
        return (process.ExitCode, (output.Result + error.Result).Trim());
    }
}

/// <summary>A drive letter mapped to the loopback share with <c>net use</c> for the length of one test, removed on dispose (also when the test
/// failed). Inconclusive when it cannot be mapped here, with the reason.</summary>
internal sealed class MappedDrive : IDisposable
{
    private const string Share = @"\\localhost\ambynet";
    private bool mapped;

    public MappedDrive()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
        if (free == default)
        {
            Assert.Inconclusive("no free drive letter to map the loopback share to");
        }

        Letter = free;
        var result = Cmd.Run($"/c net use {Letter}: {Share}");
        if (result.ExitCode != 0)
        {
            Assert.Inconclusive($"net use {Letter}: {Share} failed in this context (exit {result.ExitCode}): {result.Output}");
        }

        mapped = true;
        if (new DriveInfo(Letter + ":").DriveType != DriveType.Network)
        {
            Dispose();
            Assert.Inconclusive($"{Letter}: was mapped but is not reported as a network drive");
        }
    }

    public char Letter { get; }

    public void Dispose()
    {
        if (mapped)
        {
            mapped = false;
            Cmd.Run($"/c net use {Letter}: /delete /y");
        }
    }
}
