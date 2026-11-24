using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ambysto.Lean.App;

/// <summary>Overview (ASP-14, intent/2026-10-18-asp-14-start-and-sidebar.md): the start screen with the Scan circle and a card for every
/// fixed drive, the map drawn during the scan from the progress snapshots, and Scan a folder. It decides nothing about what a scan
/// measures or what may be moved: the Core's scanner and gates do.</summary>
public sealed partial class MainWindow
{
    // The map is redrawn from the latest snapshot at most this often (docs/spec/01, Progress and cancel).
    private static readonly TimeSpan ProgressDrawInterval = TimeSpan.FromMilliseconds(250);

    // A drive that does not answer within this is left out of the start screen's cards.
    private static readonly TimeSpan DriveReadLimit = TimeSpan.FromSeconds(2);

    private DispatcherTimer? progressTimer;
    private ScanProgress? latestProgress;
    private bool progressPending;
    private string scanLabel = string.Empty;
    private long? scanUsedBytes;
    private bool folderPickerOpen;
    private List<DriveFacts> driveFacts = [];
    private CheckBox? profileDriveCard;
    private bool profileTicked = true; // ASP-18: the profile drive's card is ticked at every launch; the tick is not saved

    // Every visible string, from Copy.Text.
    private void InitializeOverview()
    {
        ScanCircleLabel.Text = Text.Scan;
        AutomationProperties.SetName(ScanButton, Text.Scan);
        ScanAgainButton.Content = Text.ScanAgain;
        AutomationProperties.SetName(ScanAgainButton, Text.ScanAgain);
        ScanFolderButton.Content = Text.ScanFolder;
        AutomationProperties.SetName(ScanFolderButton, Text.ScanFolder);

        UpdateScanHint();
        // The ink, underlined: the theme has no other link colour (the system's accent blue would be a second accent).
        ScanFolderLink.Content = new TextBlock { Text = Text.ScanFolderLink, TextDecorations = Windows.UI.Text.TextDecorations.Underline, Foreground = new SolidColorBrush(BrandPalette.SidebarBar) };
        AutomationProperties.SetName(ScanFolderLink, Text.ScanFolderLink);

        RecycleBinButton.Content = Text.OpenRecycleBin;
        SystemItemsBox.Content = Text.ShowSystemItems;
        CancelButton.Content = Text.Cancel;
        InitializeReadout();
        UpdateOverview();
    }

    // ---- the three states ---------------------------------------------------------------------------------------------

    // Start (no scan yet): the Scan circle and the drive cards. Scanning: the progress line with Cancel, the terminal readout and the
    // map drawn zone by zone. Scanned: the results view (type column, map, largest files), Scan again and Scan a folder at the top right.
    private void UpdateOverview()
    {
        var scanning = cancelScan is not null;
        var scanned = !scanning && tree is not null;
        var start = !scanning && !scanned;
        StartScroll.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        StartPanel.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        ResultsGrid.Visibility = start ? Visibility.Collapsed : Visibility.Visible;
        TopActions.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ProgressRow.Visibility = scanning || ScanProgressText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanReadout.Visibility = scanning || readoutHold ? Visibility.Visible : Visibility.Collapsed;
        TypeColumn.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;
        ResultsRule.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;
        LargestPanel.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;
        // The map takes the top 55 % and the list the rest; while the scan runs the map takes all of it.
        MapRow.Height = new GridLength(scanned ? 55 : 1, GridUnitType.Star);
        ListRow.Height = scanned ? new GridLength(45, GridUnitType.Star) : new GridLength(0);
        ProgressText.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;
    }

    // Scan, Scan again, Scan a folder and the card of the drive Lean scans: on when no scan and no move runs. Scan and Scan again scan the
    // ticked drives (ASP-18): the Scan circle is off while none is ticked. Scan again keeps its ASP-14 behaviour (the busy rules only; it
    // has its own handler): the cards are not shown on the results view, so a tick cannot be changed there. Scan a folder works whatever
    // is ticked.
    private void SetScanControlsEnabled(bool enabled)
    {
        ScanButton.IsEnabled = enabled && profileTicked;
        ScanAgainButton.IsEnabled = enabled;
        ScanFolderButton.IsEnabled = enabled;
        ScanFolderLink.IsEnabled = enabled;
        if (profileDriveCard is not null)
        {
            profileDriveCard.IsEnabled = enabled;
        }

        UpdateScanHint();
    }

    // The line under the Scan circle: "Scan C: or scan a folder", or with no drive ticked "Tick a drive to scan, or scan a folder". The line
    // is Copy's; its last words are the link.
    private void UpdateScanHint()
    {
        var line = profileTicked ? Text.ScanDriveOrFolder(ProfileVolumeRoot().TrimEnd('\\')) : Text.TickADriveOrScanAFolder;
        ScanHintText.Text = line.EndsWith(Text.ScanFolderLink, StringComparison.Ordinal) ? line[..^Text.ScanFolderLink.Length].TrimEnd() : line;
        AutomationProperties.SetName(ScanHint, line); // one element that reads the whole line, its last words being the link
    }

    // ---- the progress line, the terminal readout and the map during the scan --------------------------------------------

    private const int MeterCells = 40;
    private const int MeterBlock = 6;
    private const int LogLines = 6;
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530); // a full blink every 1.06 s
    private static readonly TimeSpan ProgressTextInterval = TimeSpan.FromSeconds(1);

    private readonly List<Border> meterCells = [];
    private readonly List<TextBlock> logLines = [];
    private readonly Dictionary<string, (long Order, bool Done)> logged = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Diagnostics.Stopwatch progressTextClock = new();
    private DispatcherTimer? blinkTimer;
    private long logCounter;
    private int meterTick;
    private int meterLit = -1;
    private bool readoutHold;

    // The 40 meter cells and the 6 log lines, built once.
    private void InitializeReadout()
    {
        for (var i = 0; i < MeterCells; i++)
        {
            var cell = new Border { Width = 9, Height = 14 };
            meterCells.Add(cell);
            ScanMeter.Children.Add(cell);
        }

        for (var i = 0; i < LogLines; i++)
        {
            var line = new TextBlock { Style = LeanStyles.Label, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(line, "ScanLogLine");
            logLines.Add(line);
            ScanLog.Children.Add(line);
        }

        ScanCursor.Background = new SolidColorBrush(BrandPalette.Accent);
        PaintMeter(0, 0);
        InitializeNeon();
    }

    // A whole-drive scan is named by its drive ("C:"), a folder scan by its path.
    private void BeginScanProgress(string root)
    {
        var whole = IsVolumeRoot(root);
        scanLabel = whole ? root.TrimEnd('\\') : root;
        scanUsedBytes = whole && volumeFacts is { UsedBytes: > 0 } facts && string.Equals(Path.GetPathRoot(Path.GetFullPath(root)), facts.VolumeRoot, StringComparison.OrdinalIgnoreCase)
            ? facts.UsedBytes
            : null;
        latestProgress = null;
        progressPending = false;
        logged.Clear();
        logCounter = 0;
        readoutHold = false;
        meterTick = 0;
        meterLit = -1;
        foreach (var line in logLines)
        {
            line.Visibility = Visibility.Collapsed;
        }

        ScanProgressText.Text = ProgressLine(0, 0);
        progressTextClock.Restart();
        ScanPromptText.Text = Text.ScanPrompt(whole ? Path.GetPathRoot(Path.GetFullPath(root)) ?? root : root);
        ScanReadingText.Text = Text.ScanReading(Text.MiddleShortened(root, 90)); // until the first report names a folder
        ScanCursor.Opacity = 1;
        ScanCursor.Background = new SolidColorBrush(BrandPalette.Accent);
        PaintMeter(0, 0);
        SetMeterText(scanUsedBytes is null ? Text.ScanMeterRunning : Text.Percent(0), scanUsedBytes is null ? string.Empty : Text.Percent(0));
        progressTimer ??= CreateProgressTimer();
        progressTimer.Start();
        StartBlink();
        UpdateOverview();
        NeonBegin();
    }

    private void EndScanProgress()
    {
        progressTimer?.Stop();
        blinkTimer?.Stop();
        ScanCursor.Opacity = 1;
        latestProgress = null;
        progressPending = false;
        ScanProgressText.Text = string.Empty;
        NeonEnd();
    }

    private DispatcherTimer CreateProgressTimer()
    {
        var timer = new DispatcherTimer { Interval = ProgressDrawInterval };
        timer.Tick += (_, _) => DrawLatestProgress();
        return timer;
    }

    // The cursor blinks every 1.06 s, unless Windows' animation effects are off.
    private void StartBlink()
    {
        if (!BrandPalette.AnimationsEnabled)
        {
            return;
        }

        blinkTimer ??= CreateBlinkTimer();
        blinkTimer.Start();
    }

    private DispatcherTimer CreateBlinkTimer()
    {
        var timer = new DispatcherTimer { Interval = BlinkInterval };
        timer.Tick += (_, _) => ScanCursor.Opacity = ScanCursor.Opacity > 0 ? 0 : 1;
        return timer;
    }

    // High Contrast or the animation effects switched during a scan: the cursor stops blinking and stays lit, and the neon, if built,
    // goes at once (no glow, sweep or scanlines). Checked on every draw and when the system colours change.
    private void ReactToEffectSettings()
    {
        if (!BrandPalette.AnimationsEnabled)
        {
            blinkTimer?.Stop();
            ScanCursor.Opacity = 1;
        }

        NeonSuspendIfNeeded();
    }

    // The walker reports about 4 times a second; the window keeps the latest report and draws it on the timer, so it never draws
    // more often than that, whatever posts arrive.
    private void OnProgress(ScanProgress report)
    {
        if (cancelScan is null)
        {
            return;
        }

        latestProgress = report;
        progressPending = true;
    }

    private void DrawLatestProgress()
    {
        if (!progressPending || cancelScan is null || latestProgress is not { } report)
        {
            return;
        }

        progressPending = false;
        ReactToEffectSettings();
        var measured = report.MeasuredBytes > 0 ? report.MeasuredBytes : report.TopLevelAllocatedBytes.Sum(t => t.Value);

        // The live region announces at most once a second.
        if (progressTextClock.Elapsed >= ProgressTextInterval)
        {
            progressTextClock.Restart();
            var line = ProgressLine(report.FilesSeen, measured);
            if (!string.Equals(ScanProgressText.Text, line, StringComparison.Ordinal))
            {
                ScanProgressText.Text = line;

                // A polite live region: a screen reader is told when the text changes, at most once a second.
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(ScanProgressText)?
                    .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
            }
        }

        DrawReadout(report, measured);
        DrawZones(report);
    }

    // "Scanning C: — 47,931 files, 6.86 GB of about 161.7 GB (4 %)", or for a folder "Scanning C:\Users\me\Videos — 47,931 files, 6.86 GB".
    // The percent is measured / used, never above 99 until the scan ends.
    private string ProgressLine(long files, long measuredBytes)
    {
        var measured = ByteUnits.Format(measuredBytes);
        if (scanUsedBytes is { } used)
        {
            return Text.ScanningLine(scanLabel, files, measured, ByteUnits.Format(used), PercentOf(measuredBytes, used));
        }

        return Text.ScanningLine(scanLabel, files, measured);
    }

    private static int PercentOf(long measuredBytes, long usedBytes) => (int)Math.Clamp(100.0 * measuredBytes / usedBytes, 0, 99);

    // The readout: the folder being read, the meter, and the last six zones.
    private void DrawReadout(ScanProgress report, long measuredBytes)
    {
        if (report.Reading is { } reading)
        {
            ScanReadingText.Text = Text.ScanReading(Text.MiddleShortened(reading, 90));
        }

        if (scanUsedBytes is { } used)
        {
            var percent = PercentOf(measuredBytes, used);
            PaintMeter(percent * MeterCells / 100, 0);
            SetMeterText(Text.Percent(percent), Text.Percent(percent));
        }
        else
        {
            // A folder scan has no total to claim: a block of six lit cells moves along the meter.
            // With Windows' animation effects off the block stands still on its first six cells.
            meterTick = BrandPalette.AnimationsEnabled ? (meterTick + 1) % MeterCells : 0;
            PaintMeter(MeterBlock, meterTick);
            SetMeterText(Text.ScanMeterRunning, string.Empty);
        }

        DrawLog(report);
    }

    private void SetMeterText(string name, string percentText)
    {
        AutomationProperties.SetName(ScanMeter, name);
        ScanPercentText.Text = percentText;
    }

    // Paints the 40 cells: <paramref name="lit"/> cells in the tone starting at <paramref name="start"/> (wrapping), the rest dim. With
    // the start at 0 these are the first cells, a filled meter; the head is the last lit cell.
    private void PaintMeter(int lit, int start)
    {
        var key = lit * MeterCells + start;
        if (key == meterLit)
        {
            return;
        }

        meterLit = key;
        var head = -1;
        for (var i = 0; i < MeterCells; i++)
        {
            var offset = ((i - start) % MeterCells + MeterCells) % MeterCells;
            var on = offset < lit;
            if (on && offset == lit - 1)
            {
                head = i;
            }

            meterCells[i].Background = new SolidColorBrush(on ? BrandPalette.MeterLit(true) : BrandPalette.MeterDim);
            if (BrandPalette.MeterEdge is { } edge)
            {
                meterCells[i].BorderBrush = new SolidColorBrush(edge);
                meterCells[i].BorderThickness = new Thickness(1);
            }
        }

        NeonMeterHead(head);
    }

    // The last six zones to start or finish: "done  Users  6.84 GB", or "....  Windows  reading" while it is read.
    private void DrawLog(ScanProgress report)
    {
        foreach (var zone in report.Zones)
        {
            if (zone.Done)
            {
                if (!logged.TryGetValue(zone.Path, out var entry) || !entry.Done)
                {
                    logged[zone.Path] = (++logCounter, true);
                }
            }
            else if (zone.AllocatedBytes > 0 && !logged.ContainsKey(zone.Path))
            {
                logged[zone.Path] = (++logCounter, false);
            }
        }

        var byPath = report.Zones.ToDictionary(z => z.Path, StringComparer.OrdinalIgnoreCase);
        var latest = logged.OrderByDescending(e => e.Value.Order).Take(LogLines).OrderBy(e => e.Value.Order).ToList();
        for (var i = 0; i < logLines.Count; i++)
        {
            if (i < latest.Count && byPath.TryGetValue(latest[i].Key, out var zone))
            {
                var name = zone.Name ?? Path.GetFileName(zone.Path);
                logLines[i].Text = zone.Done ? Text.ScanLogDone(name, ByteUnits.Format(zone.AllocatedBytes)) : Text.ScanLogReading(name);
                logLines[i].Visibility = Visibility.Visible;
            }
            else
            {
                logLines[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    // The zones of the latest report as tiles: display-only, so a click does nothing and the menu offers no Move.
    private void DrawZones(ScanProgress report)
    {
        var width = MapArea.ActualWidth;
        var height = MapArea.ActualHeight;
        if (!(width >= 1) || !(height >= 1))
        {
            return;
        }

        var models = MapBuilder.BuildZones(report.Zones, new MapRect(0, 0, width, height));
        Render(models);
        NeonSweeps(models);
    }

    // The map's size changed while the scan runs: the latest zones are drawn again at the new size.
    private void RedrawZones()
    {
        if (cancelScan is not null && latestProgress is { } report)
        {
            DrawZones(report);
        }
    }

    // ---- Scan a folder -------------------------------------------------------------------------------------------------

    // The system folder picker; a folder on the profile's drive is scanned with the same scanner, rules and gate as the whole drive. A
    // folder on another drive is refused in the progress line and no scan starts (owner decision 2026-10-18: other drives come after v1).
    private async void OnScanFolderClick(object sender, RoutedEventArgs e)
    {
        if (cancelScan is not null || moveBusy || checkingRoot || folderPickerOpen)
        {
            return;
        }

        folderPickerOpen = true;
        try
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return; // the user closed the picker: nothing changes
            }

            // ScanAsync refuses a folder on another drive, as it does for every way a scan starts.
            folderPickerOpen = false;
            await ScanAsync(folder);
        }
        catch (COMException)
        {
            StatusText.Text = Text.FolderPickerFailed; // the picker could not open: nothing starts
        }
        finally
        {
            folderPickerOpen = false;
        }
    }

    // ---- drive cards ---------------------------------------------------------------------------------------------------

    // Read off the UI thread, each drive with its own 2 s limit; a failure leaves the start screen without cards, never without Scan.
    private async Task LoadDriveCardsAsync()
    {
        try
        {
            driveFacts = await FixedDrives.ReadAsync(DriveReadLimit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            driveFacts = [];
        }

        BuildDriveCards();
    }

    private void BuildDriveCards()
    {
        DriveCardRow.Children.Clear();
        profileDriveCard = null;
        var profile = ProfileVolumeRoot().TrimEnd('\\');
        foreach (var facts in driveFacts)
        {
            var scanned = string.Equals(facts.Drive, profile, StringComparison.OrdinalIgnoreCase);
            var card = DriveCard(facts, scanned);
            DriveCardRow.Children.Add(card);
            if (scanned)
            {
                profileDriveCard = card;
            }
        }

        DriveCards.Visibility = driveFacts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMoveControls();
    }

    // The letter and the label, "<free> free of <total>", a ten-cell meter of used space, and a note. Each card is one tick box whose
    // content is the card (ASP-18): one tab stop, Space or a click ticks or unticks it, and it never starts a scan. Only the profile
    // drive's card is enabled, and ticked at start; every other one is disabled and unticked, and says that scanning it comes after v1.
    private CheckBox DriveCard(DriveFacts facts, bool scanned)
    {
        var name = Text.DriveName(facts.Drive, string.IsNullOrWhiteSpace(facts.Label) ? Text.LocalDisk : facts.Label);
        var space = Text.DriveFreeOfTotal(ByteUnits.Format(facts.FreeBytes), ByteUnits.Format(facts.TotalBytes));
        var note = scanned ? Text.ThisDriveIsScanned : Text.OtherDrivesAfterV1;
        var content = new StackPanel { Spacing = 8 };
        // The right margin keeps a long name clear of the tick box in the corner.
        content.Children.Add(new TextBlock { Text = name, Style = LeanStyles.Medium, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 28, 0) });
        content.Children.Add(new TextBlock { Text = space, Style = LeanStyles.Label });
        content.Children.Add(Meter(facts, scanned));
        content.Children.Add(new TextBlock { Text = note, Style = LeanStyles.Note, TextWrapping = TextWrapping.Wrap });
        var card = new CheckBox
        {
            Style = LeanStyles.Get("LeanDriveCard"),
            Content = content,
            IsEnabled = scanned && !ScanOrMoveBusy,
            IsChecked = scanned && profileTicked,
        };
        AutomationProperties.SetAutomationId(card, "DriveCard");
        AutomationProperties.SetName(card, name);
        AutomationProperties.SetFullDescription(card, space + "\n" + note);
        if (scanned)
        {
            card.Checked += OnProfileDriveTickChanged;
            card.Unchecked += OnProfileDriveTickChanged;
        }

        return card;
    }

    // The scan controls and the cards are off while a scan, a move or the root check runs (ASP-14's busy rules, which the tick box keeps).
    private bool ScanOrMoveBusy => cancelScan is not null || moveBusy || checkingRoot;

    // Ten cells; the lit ones are the used space (the ink, the tone on the drive Lean scans), the others the dim line.
    private static StackPanel Meter(DriveFacts facts, bool scanned)
    {
        var lit = facts.TotalBytes <= 0 ? 0 : (int)Math.Clamp(Math.Round(10.0 * (facts.TotalBytes - facts.FreeBytes) / facts.TotalBytes), 0, 10);
        var meter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        for (var i = 0; i < 10; i++)
        {
            var cell = new Border
            {
                Width = 14,
                Height = 8,
                Background = new SolidColorBrush(i < lit ? BrandPalette.MeterLit(scanned) : BrandPalette.MeterDim),
            };
            if (BrandPalette.MeterEdge is { } edge)
            {
                cell.BorderBrush = new SolidColorBrush(edge);
                cell.BorderThickness = new Thickness(1);
            }

            meter.Children.Add(cell);
        }

        AutomationProperties.SetAccessibilityView(meter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return meter;
    }

    // Ticking only ticks (ASP-18): a click or Space on the profile drive's card changes what Scan scans and starts nothing. A disabled
    // card never reaches here.
    private void OnProfileDriveTickChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
        {
            profileTicked = box.IsChecked == true;
            SetScanControlsEnabled(!ScanOrMoveBusy);
        }
    }
}
