using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Pro;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Ambysto.Lean.App;

/// <summary>The Settings and About views and the Free/Pro gating of the UI (plan group 8, "UI contract for group 8"). The
/// entitlement comes from <see cref="Entitlement.FromKey(string)"/>, the Kit's real verifier; the key is stored nowhere. A
/// release build has no way to Pro in phase A, so the Pro paths below are not reachable yet.</summary>
public sealed partial class MainWindow
{
    private const int ReportTopFolderCount = 10;

    private Entitlement entitlement = Entitlement.Free;
    private VolumeCardFacts? volumeFacts;
    // ASP-33: the four Pro features come through one seam. The public build has none; a release build adds the private module.
    private readonly IProFeatures proFeatures = CreateProFeatures();
    private ILoginItem? loginItem;
    private DispatcherTimer? lowDiskTimer;
    private ILowDiskAlert? lowDiskAlert;
    private bool overlayShown;

#pragma warning disable CA1859 // the seam is the interface on purpose: the module's type exists in only one of the two builds
    private static IProFeatures CreateProFeatures()
#pragma warning restore CA1859
    {
#if LEAN_PRO
        return new Ambysto.Lean.Pro.ProModule();
#else
        return NoProFeatures.Instance;
#endif
    }

    // The overlay is a notice: the next time the user comes to the window it has done its job.
    private void ClearAlertOverlayOnActivation(object sender, WindowActivatedEventArgs args)
    {
        if (overlayShown && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            AlertNotifier.ClearOverlay(WinRT.Interop.WindowNative.GetWindowHandle(this));
            overlayShown = false;
        }
    }
    private long lastFreeBytes;
    private CancellationTokenSource? exportCancel;

    // Every visible string, from Copy.Text; Free until a key verifies.
    private void InitializeViews()
    {
        SettingsTitle.Text = Text.Settings;
        AboutTitle.Text = Text.About;
        InitializeAbout();
        LicenseKeyBox.Header = Text.LicenseKeyLabel;
        AutomationProperties.SetName(LicenseKeyBox, Text.LicenseKeyLabel);
        ActivateButton.Content = Text.ActivateKey;
        OpenAtLoginBox.Content = Text.OpenAtLogin;
        LowDiskAlertBox.Content = Text.LowDiskAlert;
        ExportButton.Content = Text.ExportReport;
        InitializeNavigationKeys();
        VersionText.Text = Text.Version(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0");
        CancelExportButton.Content = Text.Cancel;
        Closed += (_, _) =>
        {
            StopLowDiskAlert();
            exportCancel?.Cancel();
        };
        ApplyEntitlement(Entitlement.Free);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowView(SettingsView);
        _ = RetryUnreadableSettingsAsync();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) => ShowView(AboutView);

    // The view that is shown, or null on Overview (the start screen or the map).
    private UIElement? currentView;

    // Esc, Alt+Left and the mouse's back button (ASP-9, ASP-14: the sidebar replaced the Back to map buttons) return to Overview from
    // any other view, from anywhere in the window, and not while a dialog or a flyout is open.
    private void InitializeNavigationKeys()
    {
        if (Content is FrameworkElement root)
        {
            // The tunnelling event: a list or a text box that handles Left or Esc itself does not swallow them. BackToMap() itself
            // stands aside while a popup (a combo box's list, a flyout, a dialog) is open, so that one's own Esc still closes it.
            root.PreviewKeyDown += OnWindowKeyDown;
            root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWindowPointerPressed), true);
        }
    }

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Escape || (e.Key == Windows.System.VirtualKey.Left && alt))
        {
            e.Handled = BackToMap();
        }
    }

    private void OnWindowPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsXButton1Pressed)
        {
            e.Handled = BackToMap();
        }
    }

    // Back to Overview, which keeps its scan, folder and selection. Nothing that runs is cancelled. A content dialog (the move
    // confirmation) or any open popup takes the key first, so Esc then only closes it. Returns whether it went back.
    private bool BackToMap()
    {
        if (currentView is null || confirmDialogOpen || Content?.XamlRoot is not { } xamlRoot
            || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0)
        {
            return false;
        }

        ShowView(null);
        MainContent.UpdateLayout();
        OverviewButton.Focus(FocusState.Programmatic);
        return true;
    }

    // The sidebar item of a view; Overview for none.
    private Button ItemOf(UIElement? view) =>
        ReferenceEquals(view, SettingsView) ? SettingsButton
        : ReferenceEquals(view, AboutView) ? AboutButton
        : ReferenceEquals(view, LargeOldView) ? LargeOldButton
        : ReferenceEquals(view, DuplicatesView) ? DuplicatesButton
        : ReferenceEquals(view, UninstallView) ? UninstallButton
        : ReferenceEquals(view, HistoryView) ? HistoryButton
        : ReferenceEquals(view, TempView) ? TemporaryFilesButton
        : ReferenceEquals(view, AppsView) ? AppsAndCachesButton
        : OverviewButton;

    // One view at a time, in place of the main content (which keeps its scan and its map).
    private void ShowView(UIElement? view)
    {
        if (ReferenceEquals(currentView, DuplicatesView) && !ReferenceEquals(view, DuplicatesView))
        {
            ClearDuplicatesStatus();
        }

        if (ReferenceEquals(currentView, UninstallView) && !ReferenceEquals(view, UninstallView))
            InvalidateUninstallResult();

        currentView = view;
        SetCurrentSidebarItem(ItemOf(view));
        MainContent.Visibility = view is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var candidate in new UIElement[] { SettingsView, AboutView, LargeOldView, TempView, DuplicatesView, UninstallView, AppsView, HistoryView })
        {
            candidate.Visibility = ReferenceEquals(view, candidate) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // The pasted key goes to the Kit's verifier and nowhere else; white space around it is removed there. An empty or blank
    // box does nothing: no change and no message.
    private void OnActivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LicenseKeyBox.Text))
        {
            return;
        }

        ApplyEntitlement(Entitlement.FromKey(LicenseKeyBox.Text));
    }

    // Free: the Pro controls are disabled and carry the quiet "Pro" label. Pro: they work and the label goes.
    private void ApplyEntitlement(Entitlement value)
    {
        var wasPro = entitlement.IsPro;
        entitlement = value;
        var pro = value.IsPro;
        LicenseStatusText.Text = pro ? value.RegisteredTo ?? string.Empty : value.RefusalReason ?? Text.Free;
        RegisteredToText.Text = value.RegisteredTo ?? string.Empty;
        RegisteredToText.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        SettingsMessage.Text = string.Empty;

        // The three controls that need the Pro features: a key alone does not unlock them in a build without the module.
        var feature = pro && proFeatures.Available;
        OpenAtLoginBox.IsEnabled = feature;
        LowDiskAlertBox.IsEnabled = feature;
        ExportButton.IsEnabled = feature;
        var label = feature ? Visibility.Collapsed : Visibility.Visible;
        OpenAtLoginProLabel.Text = Text.ProLabel;
        LowDiskAlertProLabel.Text = Text.ProLabel;
        ExportProLabel.Text = Text.ProLabel;
        OpenAtLoginProLabel.Visibility = label;
        LowDiskAlertProLabel.Visibility = label;
        ExportProLabel.Visibility = label;
        OpenAtLoginProBadge.Visibility = label;
        LowDiskAlertProBadge.Visibility = label;
        ExportProBadge.Visibility = label;
        UpdateSuggestControls(); // ASP-11: Suggest follows a key entered in About at once
        UpdateAppsControls(); // ASP-20: so do the tick boxes, Clear and Suggest of Apps and caches

        if (feature)
        {
            OpenAtLoginBox.IsChecked = LoginItemIsOn();
        }
        else
        {
            // Pro to Free: the app turns off its own login item (its own value only) and stops the alert. A Free session
            // that was never Pro touches no registry value.
            if (wasPro)
            {
                TurnLoginItemOff();
            }

            OpenAtLoginBox.IsChecked = false;
            StopLowDiskAlert();
            LowDiskAlertBox.IsChecked = false;
            exportCancel?.Cancel();
        }
    }

    // ---- Open at login (Pro) ----------------------------------------------------------------------------------------

    // The login item for this app's own executable, or null when the app cannot name it.
    private ILoginItem? TryLoginItem()
    {
        if (loginItem is null && Environment.ProcessPath is { } exe)
        {
            try
            {
                loginItem = proFeatures.CreateLoginItem(exe);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return loginItem;
    }

    // Reading the Run key can fail like writing it; the message goes to the Settings view and the app goes on.
    private bool LoginItemIsOn()
    {
        try
        {
            return TryLoginItem()?.IsOn ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            SettingsMessage.Text = Text.OpenAtLoginFailed;
            return false;
        }
    }

    private void TurnLoginItemOff()
    {
        try
        {
            TryLoginItem()?.TurnOff();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            SettingsMessage.Text = Text.OpenAtLoginFailed;
        }
    }

    private void OnOpenAtLoginClick(object sender, RoutedEventArgs e)
    {
        if (!entitlement.IsPro || TryLoginItem() is not { } item)
        {
            OpenAtLoginBox.IsChecked = false;
            return;
        }

        try
        {
            if (OpenAtLoginBox.IsChecked == true)
            {
                item.TurnOn();
            }
            else
            {
                item.TurnOff();
            }

            OpenAtLoginBox.IsChecked = item.IsOn;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            SettingsMessage.Text = Text.OpenAtLoginFailed;
            OpenAtLoginBox.IsChecked = LoginItemIsOn();
        }
    }

    // ---- Low-disk alert (Pro) ---------------------------------------------------------------------------------------

    private void OnLowDiskAlertClick(object sender, RoutedEventArgs e)
    {
        StopLowDiskAlert();
        if (!entitlement.IsPro || LowDiskAlertBox.IsChecked != true || proFeatures.CreateLowDiskAlert(ReadFreeBytes, TimeProvider.System) is not { } created)
        {
            LowDiskAlertBox.IsChecked = false;
            return;
        }

        lowDiskAlert = created;
        lowDiskTimer = new DispatcherTimer { Interval = proFeatures.LowDiskCheckInterval };
        lowDiskTimer.Tick += OnLowDiskTick;
        lowDiskTimer.Start();
    }

    // Runs on the worker thread that checks; the number travels back to the UI thread as the task's result.
    private long ReadFreeBytes() => Interlocked.Exchange(ref lastFreeBytes, Ambysto.Lean.Core.Measure.VolumeCard.Read(ProfileVolumeRoot()).FreeBytes);

    // Stops the timer (a DispatcherTimer has nothing to dispose) and lets go of the alert; also at window close.
    private void StopLowDiskAlert()
    {
        if (lowDiskTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnLowDiskTick;
        }

        lowDiskTimer = null;
        lowDiskAlert = null;
    }

    // Informs only (docs/spec/06): a Windows notification through the installed app id, or the taskbar overlay when
    // Windows will not show one; the status line says it too.
    private async void OnLowDiskTick(object? sender, object e)
    {
        if (lowDiskAlert is not { } alert)
        {
            return;
        }

        try
        {
            var (fired, free) = await Task.Run(() => (alert.Check(), Interlocked.Read(ref lastFreeBytes)));

            // The answer counts only if this alert is still the running one and the app is still Pro.
            if (fired && ReferenceEquals(lowDiskAlert, alert) && entitlement.IsPro)
            {
                var said = Text.LowDiskAlertFired(ByteUnits.Format(free), ByteUnits.Format(proFeatures.LowDiskThresholdBytes));
                StatusText.Text = said;
                if (!AlertNotifier.TryNotify(Text.LowDiskNotificationTitle, Text.LowDiskNotificationBody(ByteUnits.Format(free), ByteUnits.Format(proFeatures.LowDiskThresholdBytes))))
                {
                    overlayShown = AlertNotifier.ShowOverlay(WinRT.Interop.WindowNative.GetWindowHandle(this), said);
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A check that cannot read the volume tries again at the next interval.
        }
    }

    // ---- Export (Pro) -----------------------------------------------------------------------------------------------

    private void OnCancelExportClick(object sender, RoutedEventArgs e) => exportCancel?.Cancel();

    // The report of the last scan as Markdown or JSON, written where the user's save dialog says. Every failure it can
    // meet becomes a message in the Settings view; none reaches the dispatcher.
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!entitlement.IsPro || !proFeatures.Available || moveBusy || exportCancel is not null)
        {
            return;
        }

        if (tree is not { } scanned || volumeFacts is not { } facts)
        {
            SettingsMessage.Text = Text.ExportNeedsScan;
            return;
        }

        using var cancel = new CancellationTokenSource();
        exportCancel = cancel;
        UpdateMoveControls(); // no move starts while the export reads the tree
        var statusBefore = StatusText.Text;
        try
        {
            await ExportAsync(scanned, facts, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            SettingsMessage.Text = Text.ExportCancelled;
        }
        catch (COMException)
        {
            SettingsMessage.Text = Text.ExportPickerFailed;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            SettingsMessage.Text = Text.ExportReadFailed;
        }
        finally
        {
            exportCancel = null;
            CancelExportButton.Visibility = Visibility.Collapsed;
            StatusText.Text = statusBefore;
            UpdateMoveControls();
        }
    }

    private async Task ExportAsync(ScanTree scanned, VolumeCardFacts facts, CancellationToken token)
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Text.ReportTitle,
        };
        picker.FileTypeChoices.Add(Text.ExportMarkdownChoice, new List<string> { ".md" });
        picker.FileTypeChoices.Add(Text.ExportJsonChoice, new List<string> { ".json" });
        var picked = await picker.PickSaveFileAsync();
        if (picked is null)
        {
            return;
        }

        var path = picked.Path;
        var asJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // The duplicates pass reads files: say so, and offer Cancel (the main content with its Cancel is not on screen).
        SettingsMessage.Text = Text.ExportPreparing;
        StatusText.Text = Text.ExportPreparing;
        CancelExportButton.Visibility = Visibility.Visible;
        var context = await CurrentContextAsync(token);
        string text;
        try
        {
            text = await Task.Run(async () =>
            {
                var top = scanned.Root.Children.Where(n => n.IsFolder).OrderByDescending(n => n.LogicalBytes).Take(ReportTopFolderCount).ToList();
                var largeOld = LargeOldFinder.Find(scanned, LargeOldOptions.Default, DateTimeOffset.Now, context);
                var duplicates = await DuplicateFinder.FindAsync(scanned, DuplicateOptions.Default, context, token).ConfigureAwait(false);
                var report = new ScanReport(DateTimeOffset.Now, facts, scanned.Coverage, top, largeOld, duplicates);
                return proFeatures.ExportReport(report, asJson) ?? throw new InvalidOperationException("Pro features are not available.");
            }, token);
        }
        catch (ArgumentException)
        {
            // The report holds a name that is not valid text (a lone surrogate): the export refuses it.
            SettingsMessage.Text = Text.ExportInvalidName;
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), token);
            SettingsMessage.Text = Text.ExportDone(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SettingsMessage.Text = Text.ExportInvalidPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsMessage.Text = Text.ExportFailed(path);
        }
    }
}
