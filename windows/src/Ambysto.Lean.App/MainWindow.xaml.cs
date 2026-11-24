using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using HatchPath = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace Ambysto.Lean.App;

/// <summary>The main window: the sidebar, and on Overview the start screen (Scan, drive cards), the volume card, Cancel with progress and
/// the treemap as one focusable XAML element per tile (plan group 5, "UI contract for phase A"). It decides nothing: sizes, layout and hints come from
/// the Core, and File Explorer is opened only through <see cref="SystemLauncher"/>.</summary>
public sealed partial class MainWindow : Window
{
    private const int ExitCancelled = 3;
    private const int ExitFailed = 1;

    private readonly ConcurrentDictionary<string, string?> hints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> tiles = [];
    private readonly Dictionary<string, TileView> tileViews = new(StringComparer.Ordinal);

    private sealed class TileView(Button button)
    {
        internal Button Button { get; } = button;
        internal TextBlock? Label { get; set; }
        internal Grid? HatchGrid { get; set; }
        internal HatchPath? Hatch { get; set; }
        internal MapRect? HatchBounds { get; set; }
    }
    private readonly MenuFlyout tileMenu = new();
    private readonly MenuFlyoutItem revealItem = new();
    private readonly MenuFlyoutItem moveItem = new();
    private readonly bool quitAfterScan;
    private readonly TimeSpan scanDelay;
    private readonly string? scanArgument;
    private PathContext? pathContext;
    private Action? cancelScan;
    private bool checkingRoot; // the scan root's volume is being looked up (ScanAsync); no other scan starts meanwhile
    private ScanTree? tree;
    private ScanNode? shown;
    private bool volumeLevel;
    private string? menuPath;
    private ScanNode? menuNode;
    private MenuSource menuSource;
    private bool layoutRunning;
    private bool layoutDirty;

    /// <summary>Creates the main window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Activated += ClearAlertOverlayOnActivation;
        Title = Text.AppName;
        // The window and taskbar icon (ASP-13): the .ico copied next to the exe. A missing or damaged file leaves the default
        // icon; it never stops the app from starting.
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AmbystoLean.ico");
        if (System.IO.File.Exists(icon))
        {
            try
            {
                AppWindow.SetIcon(icon);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or System.IO.IOException)
            {
                // The default icon stays.
            }
        }
        InitializeMinimumSize(); // ASP-22: 560 x 560 DIP, scaled by the window's DPI
        // The default size (ASP-14: the results view wants room for the map and eight rows under it), kept inside the screen's work area
        // and centred in it, so a small screen never puts the window's bottom under the taskbar.
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1240, Math.Max(640, area.Width - 40));
        var height = Math.Min(900, Math.Max(480, area.Height - 40));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        ApplyTitleBar();

        StatusText.Text = Text.ReadyToScan;
        BuildTileMenu();
        InitializeViews();
        InitializeFindViews();
        InitializeTemp();
        InitializeSuggest();
        InitializeApps();
        InitializeProtectedFolders();
        InitializeMoves();
        InitializeSidebar();
        InitializeOverview();
        InitializeResults();
        Closed += (_, _) => cancelScan?.Invoke();
        ApplyContrast();
        BrandPalette.HighContrastChanged += OnHighContrastChanged;
        Closed += (_, _) => BrandPalette.HighContrastChanged -= OnHighContrastChanged;

        (scanArgument, quitAfterScan, scanDelay) = ReadArguments(Environment.GetCommandLineArgs());
        if (ReadSettingsLaunch(Environment.GetCommandLineArgs()) is { } launchSteps)
        {
            SystemLauncher.UseQaSettingsLaunch(launchSteps);
        }

        ApplyDebugArguments(Environment.GetCommandLineArgs());

        _ = StartAsync();
    }

    // The volume card first (it needs no scan), then the QA scan, so the window is up before the scan begins. The drive cards load beside
    // them off the UI thread and never hold anything up (a drive that does not answer in 2 s is left out).
    private async Task StartAsync()
    {
        _ = LoadDriveCardsAsync();
        await LoadVolumeCardAsync();
        if (scanArgument is not null)
        {
            await ScanAsync(scanArgument);
        }
    }

    // QA arguments, read-only like the macOS ones: --scan <folder>, --quit-after-scan and --scan-delay <milliseconds> (the walk
    // waits that long after each folder batch, so a test can cancel a scan mid-walk).
    private static (string? ScanFolder, bool QuitAfterScan, TimeSpan ScanDelay) ReadArguments(string[] arguments)
    {
        string? folder = null;
        var quit = false;
        var delay = TimeSpan.Zero;
        for (var i = 1; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], "--scan", StringComparison.Ordinal) && i + 1 < arguments.Length)
            {
                folder = arguments[++i];
            }
            else if (string.Equals(arguments[i], "--quit-after-scan", StringComparison.Ordinal))
            {
                quit = true;
            }
            else if (string.Equals(arguments[i], "--scan-delay", StringComparison.Ordinal) && i + 1 < arguments.Length
                && double.TryParse(arguments[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
            {
                delay = TimeSpan.FromMilliseconds(milliseconds);
            }
        }

        return (folder, quit, delay);
    }

    // ASP-17 phase B: implemented only in Debug builds (MainWindow.DebugPro.cs); in Release the call is removed with the declaration.
    partial void ApplyDebugArguments(string[] arguments);

    // QA argument --settings-launch "<delay_ms>:<ok|fail>[,<delay_ms>:<ok|fail>...]": each "Uninstall…" call to the launcher takes the next
    // step, waits delay_ms, opens no Settings and answers ok (true) or fail (false); once the steps run out the launcher is normal. A
    // malformed value is ignored as a whole (null).
    private static List<(TimeSpan Delay, bool Succeeds)>? ReadSettingsLaunch(string[] arguments)
    {
        for (var i = 1; i + 1 < arguments.Length; i++)
        {
            if (!string.Equals(arguments[i], "--settings-launch", StringComparison.Ordinal))
            {
                continue;
            }

            var steps = new List<(TimeSpan, bool)>();
            foreach (var part in arguments[i + 1].Split(','))
            {
                var pair = part.Split(':');
                if (pair.Length != 2
                    || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
                    || (pair[1] != "ok" && pair[1] != "fail"))
                {
                    return null;
                }

                steps.Add((TimeSpan.FromMilliseconds(milliseconds), pair[1] == "ok"));
            }

            return steps;
        }

        return null;
    }

    // The volume that holds the user's profile: the one the card shows and Scan scans.
    private static string ProfileVolumeRoot() => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;

    // ---- volume card -------------------------------------------------------------------------------------------------

    private async Task LoadVolumeCardAsync()
    {
        try
        {
            var facts = await Task.Run(() => Ambysto.Lean.Core.Measure.VolumeCard.Read(ProfileVolumeRoot()));
            volumeFacts = facts;
            UpdateFreeSpace();
            UsedText.Text = Text.LabelValue(Text.UsedByThisVolume, ByteUnits.Format(facts.UsedBytes));
            FreeText.Text = Text.LabelValue(Text.FreeRightNow, ByteUnits.Format(facts.FreeBytes));
            BinText.Text = Text.LabelValue(Text.InTheRecycleBin, facts.RecycleBinBytes is { } bin ? ByteUnits.Format(bin) : Text.Unknown);
            CardPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Text.ScanFailed(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ex.Message);
        }
    }

    // ---- scan and cancel ---------------------------------------------------------------------------------------------

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (!moveBusy && profileTicked)
        {
            _ = ScanAsync(scanArgument ?? ProfileVolumeRoot());
        }
    }

    // Scan again (ASP-14) keeps its behaviour: it is not tied to the ticks, which can no longer be changed on the results view.
    private void OnScanAgainClick(object sender, RoutedEventArgs e)
    {
        if (!moveBusy)
        {
            _ = ScanAsync(scanArgument ?? ProfileVolumeRoot());
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => cancelScan?.Invoke();

    private async Task ScanAsync(string root)
    {
        if (cancelScan is not null || moveBusy || checkingRoot)
        {
            return;
        }

        // Every way a scan starts goes through here (Scan, a drive card, --scan, the picker): only the profile's drive is scanned in v1
        // (owner decision 2026-10-18), so a root on another drive is refused and nothing starts. The volume lookup reads the disk and
        // can hang on a disconnected mapped drive, so it runs on a worker thread; until it answers no other scan starts.
        bool onProfileDrive;
        // Meanwhile the scan controls, every move control and Put back are off (a Put back would make the scan give up).
        checkingRoot = true;
        try
        {
            UpdateMoveControls();
            onProfileDrive = await Task.Run(() => IsOnProfileDrive(root));
        }
        finally
        {
            checkingRoot = false;
            UpdateMoveControls();
        }

        if (moveBusy)
        {
            return;
        }

        if (!onProfileDrive)
        {
            ScanProgressText.Text = Text.FolderNotOnProfileDrive(ProfileVolumeRoot().TrimEnd('\\'));
            UpdateOverview();

            // Announced as progress is: a screen reader is told when the refusal replaces the text.
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(ScanProgressText)?
                .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
            if (quitAfterScan)
            {
                Environment.Exit(ExitFailed);
            }

            return;
        }

        using var cts = new CancellationTokenSource();
        cancelScan = cts.Cancel;
        UpdateMoveControls();
        CancelButton.IsEnabled = true;
        StatusText.Text = Text.Scanning(root);
        ProgressText.Text = string.Empty;
        ClearMap();
        ResetFindViews();
        ResetResults();
        BeginScanProgress(root);

        try
        {
            // The walk starts on a worker thread: opening the root is the first thing it does, and must not block the window.
            var progress = new Progress<ScanProgress>(OnProgress);
            var result = await Task.Run(() => Scanner.ScanAsync(root, progress, scanDelay, cts.Token), CancellationToken.None);

            // The status first, so Cancel is answered at once; the map is built after it. It says what the scan read (docs/spec/01).
            ReturnToIdle();
            tree = result;
            shown = result.Root;
            volumeLevel = IsVolumeRoot(result.Root.Path);
            UpdateOverview();
            _ = RefreshResultsAsync();
            OnScanFinishedForViews();
            var summary = result.Summary;
            var summaryLine = Text.ScanSummaryLine(summary.Files, summary.Folders, summary.Unreadable, summary.Elapsed);
            StatusText.Text = result.Cancelled ? Text.TwoSentences(Text.ScanCancelled, summaryLine) : summaryLine;
            ProgressText.Text = CoverageText(result);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the window comes back to idle and says so; it never hangs.
            ReturnToIdle();
            StatusText.Text = Text.ScanFailed(root, ex.Message);
            ProgressText.Text = string.Empty;
            UpdateOverview();
            if (quitAfterScan)
            {
                Environment.Exit(ExitFailed);
            }

            return;
        }

        if (quitAfterScan)
        {
            Environment.Exit(tree!.Cancelled ? ExitCancelled : 0);
        }

        await RelayoutAsync();
    }

    private static bool IsOnProfileDrive(string root)
    {
        try
        {
            // The volume after mount points, not the path's root: a mount point of another volume inside C: is another volume, and
            // "\\?\C:\..." is on C:. Compared with the volume that holds the user's profile folder, which need not be the drive root's.
            return Ambysto.Lean.Core.Measure.VolumeCard.IsOnSameVolume(Path.GetFullPath(root), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Scan enabled, Cancel disabled, no scan to cancel, no more drawing of the progress.
    private void ReturnToIdle()
    {
        cancelScan = null;
        EndScanProgress();
        UpdateMoveControls();
        CancelButton.IsEnabled = false;
    }

    private static bool IsVolumeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase);
    }

    // Coverage (docs/spec/01): what was measured, against what the volume uses when the whole volume was scanned, and the
    // counts of what could not be read or was not downloaded.
    private string CoverageText(ScanTree scanned)
    {
        var coverage = scanned.Coverage;
        var lines = new List<string>();
        if (volumeLevel)
        {
            var measured = ByteUnits.Format(coverage.MeasuredBytes);
            var used = ByteUnits.Format(coverage.UsedBytes);
            lines.Add(coverage.MeasuredMoreThanUsed ? Text.MeasuredMoreThanUsedBoth(measured, used) : Text.MeasuredOfUsed(measured, used));
        }

        if (coverage.UnreadableCount > 0)
        {
            lines.Add(Text.UnreadableItems(coverage.UnreadableCount));
        }

        if (coverage.CloudNotDownloadedCount > 0)
        {
            lines.Add(Text.CloudFilesNotDownloaded(coverage.CloudNotDownloadedCount));
        }

        return string.Join("\n", lines);
    }

    // ---- map ---------------------------------------------------------------------------------------------------------

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawZones();
        _ = RelayoutAsync();
    }

    private void ClearMap()
    {
        foreach (var tile in tiles)
        {
            RetireTile(tile);
        }

        MapArea.Children.Clear();
        tiles.Clear();
        tileViews.Clear();
        hoveredTile = null;
        currentModels = null;
        Breadcrumb.Children.Clear();
        tree = null;
        shown = null;
        layoutDirty = true;
    }

    // Latest wins: a layout that finds the size or the folder changed while it ran is thrown away and run again.
    private async Task RelayoutAsync()
    {
        layoutDirty = true;
        if (layoutRunning)
        {
            return;
        }

        layoutRunning = true;
        try
        {
            while (layoutDirty)
            {
                layoutDirty = false;
                if (hintsStale)
                {
                    // The user's protected folders changed: the hints cached for the old list are dropped before this layout.
                    hintsStale = false;
                    hints.Clear();
                }

                var folder = shown;
                var scanned = tree;
                var width = MapArea.ActualWidth;
                var height = MapArea.ActualHeight;
                if (folder is null || scanned is null || !(width >= 1) || !(height >= 1))
                {
                    continue;
                }

                var coverage = volumeLevel && ReferenceEquals(folder, scanned.Root) ? scanned.Coverage : null;
                var context = await CurrentContextAsync();
                var models = await Task.Run(() => MapBuilder.Build(folder, new MapRect(0, 0, width, height), coverage, context, hints));
                if (!layoutDirty)
                {
                    Render(models);
                    BuildBreadcrumb();
                }
            }
        }
        catch (Exception ex)
        {
            // A layout that fails shows in the status, like a scan that fails.
            StatusText.Text = Text.ScanFailed(shown?.Path ?? string.Empty, ex.Message);
        }
        finally
        {
            layoutRunning = false;
        }
    }

    // The tiles last drawn, so that choosing a type group can draw them again with the other groups dimmed.
    private List<TileModel>? currentModels;

    private void RerenderMap()
    {
        if (currentModels is { } models)
        {
            Render(models);
        }
    }

    private void Render(List<TileModel> models)
    {
        currentModels = models;
        var preview = cancelScan is not null;
        var keys = new List<string>(models.Count);
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            // A live zone can never become an actionable result by retaining its button/focus.
            keys.Add(!preview && model is { Kind: TileKind.Item, Node: { } node }
                ? "node:" + node.Path
                : preview && model.HelpText.Length > 0
                    ? "zone:" + model.HelpText
                    : (preview ? "preview:" : "readonly:") + model.Kind + ":" + i.ToString(CultureInfo.InvariantCulture));
        }

        var retained = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var key in tileViews.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            var removed = tileViews[key];
            RetireTile(removed.Button);
            removed.HatchGrid?.Children.Clear();
            removed.HatchGrid = null;
            removed.Hatch = null;
            removed.Label = null;
            removed.HatchBounds = null;
            MapArea.Children.Remove(removed.Button);
            tileViews.Remove(key);
        }

        var focused = tiles.FirstOrDefault(tile => tile.FocusState != FocusState.Unfocused);
        var focusState = focused?.FocusState ?? FocusState.Unfocused;
        tiles.Clear();
        for (var i = 0; i < models.Count; i++)
        {
            if (!tileViews.TryGetValue(keys[i], out var view))
            {
                view = new TileView(CreateTile());
                tileViews.Add(keys[i], view);
            }

            UpdateTile(view, models[i]);
            var tile = view.Button;
            tiles.Add(tile);
            if (i < MapArea.Children.Count && ReferenceEquals(MapArea.Children[i], tile))
            {
                continue;
            }

            // Only a changed order detaches a retained child; the usual progress draw does not.
            MapArea.Children.Remove(tile);
            MapArea.Children.Insert(i, tile);
        }

        if (focused is not null && tiles.Contains(focused) && focused.FocusState == FocusState.Unfocused)
        {
            focused.Focus(focusState);
        }
    }

    // A native/UIA peer may outlive a removed control: drop both its model and its window callbacks.
    private void RetireTile(Button tile)
    {
        if (ReferenceEquals(hoveredTile, tile))
        {
            hoveredTile = null;
        }

        tile.Content = null;
        tile.Tag = null;
        ToolTipService.SetToolTip(tile, null);
        AutomationProperties.SetName(tile, string.Empty);
        AutomationProperties.SetHelpText(tile, string.Empty);
        tile.Click -= OnTileClick;
        tile.PointerEntered -= OnTilePointerEntered;
        tile.PointerExited -= OnTilePointerExited;
        tile.GotFocus -= OnTileFocusChanged;
        tile.LostFocus -= OnTileFocusChanged;
        tile.KeyDown -= OnTileKeyDown;
        tile.ContextRequested -= OnTileContextRequested;
    }

    private Button CreateTile()
    {
        var tile = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(3, 1, 3, 1),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(),
            Background = new SolidColorBrush(),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            UseSystemFocusVisuals = true,
            IsTabStop = true,
            Template = TileTemplate,
        };
        AutomationProperties.SetAutomationId(tile, "Tile");
        tile.Click += OnTileClick;
        tile.PointerEntered += OnTilePointerEntered;
        tile.PointerExited += OnTilePointerExited;
        tile.GotFocus += OnTileFocusChanged;
        tile.LostFocus += OnTileFocusChanged;
        tile.KeyDown += OnTileKeyDown;
        tile.ContextRequested += OnTileContextRequested;
        return tile;
    }

    private void UpdateTile(TileView view, TileModel model)
    {
        var tile = view.Button;
        var drawn = DrawnAs(model);
        tile.Tag = model;
        tile.Width = Math.Max(1, model.Bounds.Width);
        tile.Height = Math.Max(1, model.Bounds.Height);
        Canvas.SetLeft(tile, model.Bounds.X);
        Canvas.SetTop(tile, model.Bounds.Y);
        tile.Background = TileBrush(tile.Background, BrandPalette.Fill(drawn));
        ShowTileBorder(tile);
        AutomationProperties.SetName(tile, model.AutomationName);
        AutomationProperties.SetHelpText(tile, model.HelpText);
        ToolTipService.SetToolTip(tile, model.ToolTip);

        var drawsLabel = model.Colour != TypeGroupKind.OpenFolder && model.Bounds.Width >= 48 && model.Bounds.Height >= 18;
        if (drawsLabel)
        {
            view.Label ??= new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false,
            };
            view.Label.Text = drawn.Label;
            view.Label.Foreground = TileBrush(view.Label.Foreground, BrandPalette.Label(drawn));
        }

        var label = drawsLabel ? view.Label : null;
        if (drawn.Kind == TileKind.NotMeasured)
        {
            if (view.HatchGrid is null)
            {
                // Detach a direct label before moving it under the hatch grid.
                tile.Content = null;
                view.Hatch = new HatchPath { Stroke = new SolidColorBrush(), StrokeThickness = 2, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                view.HatchGrid = new Grid { IsHitTestVisible = false };
                view.HatchGrid.Children.Add(view.Hatch);
            }

            var width = Math.Max(1, model.Bounds.Width);
            var height = Math.Max(1, model.Bounds.Height);
            view.HatchGrid.Width = width;
            view.HatchGrid.Height = height;
            view.Hatch!.Width = width;
            view.Hatch.Height = height;
            view.Hatch.Stroke = TileBrush(view.Hatch.Stroke, BrandPalette.Hatch);
            if (view.HatchBounds is not { } previous || previous.Width != width || previous.Height != height)
            {
                view.Hatch.Data = HatchGeometry(width, height);
                view.Hatch.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
                view.HatchBounds = new MapRect(0, 0, width, height);
            }

            if (view.HatchGrid.Children.Count > 1 && !ReferenceEquals(view.HatchGrid.Children[1], label))
            {
                view.HatchGrid.Children.RemoveAt(1);
            }

            if (label is not null && view.HatchGrid.Children.Count == 1)
            {
                label.Margin = new Thickness(0);
                view.HatchGrid.Children.Add(label);
            }

            tile.Content = view.HatchGrid;
        }
        else
        {
            if (view.HatchGrid is not null)
            {
                view.HatchGrid.Children.Clear();
                view.HatchGrid = null;
                view.Hatch = null;
                view.HatchBounds = null;
            }

            tile.Content = label;
        }
    }

    private static SolidColorBrush TileBrush(Brush? existing, Color colour)
    {
        if (existing is SolidColorBrush brush)
        {
            if (brush.Color != colour)
            {
                brush.Color = colour;
            }

            return brush;
        }

        return new SolidColorBrush(colour);
    }

    // Same independent diagonal strokes and clipping as the old Line elements, in one noninteractive Path.
    private static PathGeometry HatchGeometry(double width, double height)
    {
        var geometry = new PathGeometry();
        for (var x = -height; x < width; x += 8)
        {
            var figure = new PathFigure { StartPoint = new Point(x, height), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x + height, 0) });
            geometry.Figures.Add(figure);
        }

        return geometry;
    }

    // One minimal template for every tile: a flat rectangle with the label at its top left. A stock button would impose
    // its own padding and minimum size on tiles a few pixels wide.
    private static readonly Lazy<ControlTemplate> TileTemplateCache = new(() => (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>"
        + "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>"
        + "<ContentPresenter Content='{TemplateBinding Content}' Padding='{TemplateBinding Padding}' HorizontalAlignment='Left' VerticalAlignment='Top' />"
        + "</Border></ControlTemplate>"));

    private static ControlTemplate TileTemplate => TileTemplateCache.Value;

    // The hovered tile and the focused tile (the clicked one, or the keyboard's) get a 2px ink border; the others 1px of the ground
    // between them (the system's text colour in High Contrast).
    private Button? hoveredTile;

    private void OnTilePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        hoveredTile = sender as Button;
        ShowTileBorder(hoveredTile);
    }

    private void OnTilePointerExited(object sender, PointerRoutedEventArgs e)
    {
        var tile = sender as Button;
        if (ReferenceEquals(hoveredTile, tile))
        {
            hoveredTile = null;
        }

        ShowTileBorder(tile);
    }

    private void OnTileFocusChanged(object sender, RoutedEventArgs e) => ShowTileBorder(sender as Button);

    private void ShowTileBorder(Button? tile)
    {
        if (tile is not { Tag: TileModel model })
        {
            return;
        }

        var emphasised = ReferenceEquals(hoveredTile, tile) || tile.FocusState != FocusState.Unfocused;
        tile.BorderBrush = TileBrush(tile.BorderBrush, BrandPalette.Border(DrawnAs(model), emphasised));
        tile.BorderThickness = new Thickness(emphasised ? 2 : 1);
    }

    // High Contrast (ASP-12): the theme's HighContrast dictionary overrides nothing, so the controls follow the system; what the code
    // draws follows it here: the map's tiles and legend, the card's top border and the title bar.
    private void OnHighContrastChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (BrandPalette.HighContrast == contrastDrawn)
        {
            return;
        }

        ApplyContrast();
        ApplyTitleBar();
        layoutDirty = true;
        ReactToEffectSettings();
        RedrawZones();
        _ = RelayoutAsync();
    });

    private bool contrastDrawn;

    private void ApplyContrast()
    {
        contrastDrawn = BrandPalette.HighContrast;
        BuildLegend();
        RefreshSidebar();
        BuildDriveCards();
        CardPanel.BorderBrush = BrandPalette.HighContrast
            ? new SolidColorBrush(BrandPalette.SystemWindowText)
            : (Brush)Application.Current.Resources["LeanToneBrush"];
    }

    // The title bar's colours go through AppWindow.TitleBar; null gives the system's own (High Contrast).
    private void ApplyTitleBar()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = AppWindow.TitleBar;
        bar.ExtendsContentIntoTitleBar = false;
        if (BrandPalette.HighContrast)
        {
            bar.BackgroundColor = null;
            bar.ForegroundColor = null;
            bar.InactiveBackgroundColor = null;
            bar.InactiveForegroundColor = null;
            bar.ButtonBackgroundColor = null;
            bar.ButtonForegroundColor = null;
            bar.ButtonHoverBackgroundColor = null;
            bar.ButtonHoverForegroundColor = null;
            bar.ButtonPressedBackgroundColor = null;
            bar.ButtonPressedForegroundColor = null;
            bar.ButtonInactiveBackgroundColor = null;
            bar.ButtonInactiveForegroundColor = null;
            return;
        }

        bar.BackgroundColor = BrandPalette.Ground;
        bar.ForegroundColor = BrandPalette.Ink;
        bar.InactiveBackgroundColor = BrandPalette.Ground;
        bar.InactiveForegroundColor = BrandPalette.Mute;
        bar.ButtonBackgroundColor = BrandPalette.Ground;
        bar.ButtonForegroundColor = BrandPalette.Ink;
        bar.ButtonHoverBackgroundColor = BrandPalette.Panel;
        bar.ButtonHoverForegroundColor = BrandPalette.Ink;
        bar.ButtonPressedBackgroundColor = BrandPalette.Rule;
        bar.ButtonPressedForegroundColor = BrandPalette.Ink;
        bar.ButtonInactiveBackgroundColor = BrandPalette.Ground;
        bar.ButtonInactiveForegroundColor = BrandPalette.Mute;
    }

    // Click, Enter or Space on a folder tile shows that folder; the breadcrumbs go back.
    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TileModel { Kind: TileKind.Item, Node: { IsFolder: true } folder } } && folder.AllocatedBytes > 0)
        {
            shown = folder;
            _ = RelayoutAsync();
        }
    }

    // The arrow keys move between tiles in drawing order, so the keyboard reaches every tile (Tab enters the map once).
    private void OnTileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down => 1,
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up => -1,
            _ => 0,
        };
        if (step == 0 || sender is not Button current)
        {
            return;
        }

        var index = tiles.IndexOf(current) + step;
        if (index >= 0 && index < tiles.Count)
        {
            tiles[index].Focus(FocusState.Keyboard);
        }

        e.Handled = true;
    }

    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        if (tree is null || shown is null)
        {
            return;
        }

        var chain = new List<ScanNode>();
        for (var node = shown; node is not null; node = node.Parent)
        {
            chain.Insert(0, node);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
            {
                Breadcrumb.Children.Add(new TextBlock { Text = Text.BreadcrumbSeparator, VerticalAlignment = VerticalAlignment.Center });
            }

            var crumb = new Button { Content = i == 0 ? node.Path : node.Name, Tag = node, Padding = new Thickness(6, 2, 6, 2) };
            AutomationProperties.SetAutomationId(crumb, "Breadcrumb");
            crumb.Click += OnBreadcrumbClick;
            Breadcrumb.Children.Add(crumb);
        }
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScanNode node } && !ReferenceEquals(node, shown))
        {
            shown = node;
            _ = RelayoutAsync();
        }
    }

    // ---- tile menu, legend, launcher ---------------------------------------------------------------------------------

    private void BuildTileMenu()
    {
        revealItem.Text = Text.RevealInExplorer;
        AutomationProperties.SetAutomationId(revealItem, "RevealInExplorer");
        revealItem.Click += OnRevealClick;

        // Moving is enabled per request (plan group 11): never on a display-only tile, and never while another move or a scan runs.
        moveItem.Text = Text.MoveToRecycleBin;
        moveItem.IsEnabled = false;
        moveItem.Click += OnMoveMenuClick;
        AutomationProperties.SetAutomationId(moveItem, "MoveToRecycleBin");

        tileMenu.Items.Add(revealItem);
        tileMenu.Items.Add(moveItem);
    }

    private void OnTileContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { Tag: TileModel model } tile)
        {
            return;
        }

        menuPath = model.Node?.Path;
        menuNode = model.Node;
        menuSource = MenuSource.Tile;
        revealItem.IsEnabled = menuPath is not null;
        moveItem.IsEnabled = CanMoveTile(model);
        if (e.TryGetPosition(tile, out var position))
        {
            tileMenu.ShowAt(tile, new FlyoutShowOptions { Position = position });
        }
        else
        {
            tileMenu.ShowAt(tile);
        }

        e.Handled = true;
    }

    private async void OnRevealClick(object sender, RoutedEventArgs e)
    {
        var path = menuPath;
        if (path is null)
        {
            return;
        }

        try
        {
            if (!await SystemLauncher.RevealInExplorerAsync(path))
            {
                StatusText.Text = Text.RevealFailed(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = Text.RevealFailed(path);
        }
    }

    private async void OnOpenRecycleBinClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SystemLauncher.OpenRecycleBinAsync())
            {
                StatusText.Text = Text.OpenRecycleBinFailed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = Text.OpenRecycleBinFailed;
        }
    }

    // A legend under the map: one swatch per file-type group (docs/spec/01, Map), with its word.
    private void BuildLegend()
    {
        Legend.Children.Clear();
        foreach (var (kind, name) in new (TypeGroupKind, string)[]
        {
            (TypeGroupKind.Video, Text.TypeVideo),
            (TypeGroupKind.DiskImage, Text.TypeDiskImage),
            (TypeGroupKind.Archive, Text.TypeArchive),
            (TypeGroupKind.Document, Text.TypeDocument),
            (TypeGroupKind.Application, Text.TypeApplication),
            (TypeGroupKind.VirtualMachineImage, Text.TypeVirtualMachineImage),
            (TypeGroupKind.Other, Text.TypeOther),
            (TypeGroupKind.OpenFolder, Text.TypeFolder),
        })
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            entry.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(BrandPalette.Fill(kind)),
                BorderBrush = new SolidColorBrush(BrandPalette.SwatchBorder(kind)),
                BorderThickness = new Thickness(1),
            });
            entry.Children.Add(new TextBlock { Text = name, Style = LeanStyles.Label });
            Legend.Children.Add(entry);
        }
    }
}
