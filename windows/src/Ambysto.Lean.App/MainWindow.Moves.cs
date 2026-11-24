using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Ambysto.Lean.App;

/// <summary>Moving to the Recycle Bin from the map, Large and old, Duplicates and Uninstall (plan group 11, "UI contract for
/// group 11"). Every move goes <c>ConfirmMove</c> → <see cref="DeletionSession"/> (nodes from the scan or a module, never a typed
/// path) → <see cref="Trasher"/> on a background thread; Put back goes through <see cref="PutBack"/> on a background thread. This
/// file decides nothing about safety: the Core's gate, the Trasher and Put back do.</summary>
public sealed partial class MainWindow
{
    private enum MenuSource
    {
        Tile,
        LargeOldRow,
        LargestRow,
    }

    // What the report's "Put back all from this session" needs: the session, its module, and how many of its items are still in the
    // Recycle Bin.
    private sealed class ReportSession(string sessionId, LeanModule module, IReadOnlySet<string> moduleItems, int remaining)
    {
        public string SessionId { get; } = sessionId;

        public LeanModule Module { get; } = module;

        public IReadOnlySet<string> ModuleItems { get; } = moduleItems;

        public int Remaining { get; set; } = remaining;
    }

    private static readonly IReadOnlySet<string> NoModuleItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private TableRow? menuTableRow; // the Large and old row the shared menu opened on

    private bool moveBusy;
    private bool confirmDialogOpen; // ASP-9: while the move confirmation is up, Esc only closes it

    // ASP-24: closing the window is held while moveBusy, so a moved item never misses its journal entry. Before the run (the hard-link
    // notes, the confirmation) the close ends it as Cancel and nothing moves; during the run it waits until every approved item is moved and
    // journaled (owner decision 2026-10-23, as the clean hold does); during Put back it waits for the end. Then the window closes by itself (closeAfterMove), and
    // does not ask again.
    private bool closeAfterMove;
    private bool sessionRunning; // from the run's start to the end of its refresh
    private bool putBackRunning;
    private ContentDialog? moveDialog;
    private CancellationTokenSource? moveCancel;
    private ReportSession? reportSession;
    private readonly List<(string Path, string Result)> reportRows = [];

    private void InitializeMoves()
    {
        MoveLargeOldButton.Content = Text.MoveToRecycleBin;
        MoveLeftoversButton.Content = Text.MoveToRecycleBin;
        MoveTempButton.Content = Text.MoveToRecycleBin;
        CancelMovingButton.Content = Text.Cancel;
        PutBackSessionButton.Content = Text.PutBackAllFromSession;
        CloseReportButton.Content = Text.CloseView;
        Closed += (_, _) => moveCancel?.Cancel();
        InitializeHistory();
        UpdateMoveControls();
    }

    // A move waits for a scan, a duplicates search or an export (they read the same tree) and any other move or Put back. The
    // controls that start those readers wait for a move in turn, so the tree is only changed when no reader runs.
    // Select all (ASP-15) is off while a scan or a move runs or is being confirmed, as Move is. It is on again when the move ends or its
    // confirmation has closed, never earlier: until then nothing else (Put back, a second move) may start.
    private bool SelectAllBlocked => MoveBlocked;

    private bool MoveBlocked => moveBusy || cleanBusy || cancelScan is not null || checkingRoot || duplicatesCancel is not null || duplicatesRuns > 0 || exportCancel is not null;

    // Every move control follows the same state; a display-only row or tile never enables one.
    private void UpdateMoveControls()
    {
        var blocked = MoveBlocked;
        SetScanControlsEnabled(cancelScan is null && !moveBusy && !checkingRoot);
        FindDuplicatesButton.IsEnabled = duplicatesCancel is null && !moveBusy;
        ExportButton.IsEnabled = entitlement.IsPro && proFeatures.Available && exportCancel is null && !moveBusy;
        MoveLargeOldButton.IsEnabled = !blocked && TickedLargeOld().Count > 0;
        MoveLargestButton.IsEnabled = !blocked && TickedLargest().Count > 0;
        RefreshAllRowActionButtons();
        LargestTable.RefreshSelectAll();
        LargeOldTable.RefreshSelectAll();
        TempTable.RefreshSelectAll();
        LeftoverTable.RefreshSelectAll();
        MoveDuplicatesButton.IsEnabled = !blocked && duplicateModels.Any(m => m.ToMove().Count > 0);
        SelectByRuleButton.IsEnabled = !blocked && duplicateModels.Count > 0;
        MoveLeftoversButton.IsEnabled = !blocked && TickedLeftovers().Count > 0;
        MoveTempButton.IsEnabled = !blocked && TickedTemp().Count > 0;
        CancelMovingButton.Visibility = moveCancel is not null ? Visibility.Visible : Visibility.Collapsed;
        PutBackSessionButton.IsEnabled = !moveBusy && !cleanBusy && !checkingRoot && reportSession is { Remaining: > 0 };
        UpdateSuggestControls();
        UpdateAppsControls();
        UpdateInstallerControls();
    }

    private bool CanMoveTile(TileModel model) =>
        !MoveBlocked
        && tree is { } scanned
        && model.Node is { } node
        && ReferenceEquals(scanned.Find(node.Id), node)
        && hints.TryGetValue(node.Path, out var hint)
        && hint is null; // null: not display-only

    // The ticked rows of Large and old that may be moved (a display-only row's box is disabled and never counts).
    private List<LargeOldRow> TickedLargeOld() =>
        LargeOldTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<LargeOldRow>().Where(r => r.Hint is null).ToList();

    // ---- where moving starts ------------------------------------------------------------------------------------------

    // The shared menu's Move to Recycle Bin: a map tile's item, or Large and old rows; the row rule applies when Move is chosen (below).
    private void OnMoveMenuClick(object sender, RoutedEventArgs e)
    {
        if (MoveBlocked)
        {
            return;
        }

        if (menuSource == MenuSource.Tile)
        {
            if (menuNode is { } node)
            {
                _ = BeginMoveAsync(LeanModule.Map, [new SessionItem(node, null)], NoModuleItems);
            }
        }
        else if (menuSource == MenuSource.LargestRow)
        {
            // The same row rule over the largest files: a movable row that is not ticked replaces the ticks.
            if (menuTableRow is { Tick: { IsEnabled: true } tick, Tag: LargestRow { Hint: null } } clicked && !clicked.IsTicked)
            {
                foreach (var other in LargestTable.Rows.Where(r => r.IsTicked))
                {
                    other.Tick!.IsChecked = false;
                }

                tick.IsChecked = true;
            }

            StartLargestMove();
        }
        else
        {
            // Row rule (plan group 11, as in File Explorer): chosen on a movable row that is not ticked, Move replaces the ticks by
            // that row alone, visibly, and moves exactly it; chosen on a ticked row it moves the ticked rows.
            if (menuTableRow is { Tick: { IsEnabled: true } tick, Tag: LargeOldRow { Hint: null } } clicked && !clicked.IsTicked)
            {
                foreach (var other in LargeOldTable.Rows.Where(r => r.IsTicked))
                {
                    other.Tick!.IsChecked = false;
                }

                tick.IsChecked = true;
            }

            StartLargeOldMove();
        }
    }

    private void OnMoveLargeOldClick(object sender, RoutedEventArgs e)
    {
        if (!MoveBlocked)
        {
            StartLargeOldMove();
        }
    }

    // The ticked rows (the row menu has already made its row the only tick when it was not ticked, when Move was chosen).
    private void StartLargeOldMove()
    {
        var items = TickedLargeOld().Select(r => new SessionItem(r.Node, null)).ToList();
        if (items.Count > 0)
        {
            _ = BeginMoveAsync(LeanModule.LargeOld, items, NoModuleItems);
        }
    }

    // Each ticked copy goes with the copy its group keeps: the one chosen with the Keep radio (the suggested one at first). The kept
    // copy is never ticked, and a group without a kept copy moves nothing.
    private void OnMoveDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (MoveBlocked)
        {
            return;
        }

        var items = new List<SessionItem>();
        foreach (var model in duplicateModels)
        {
            var ticked = model.ToMove();
            if (ticked.Count == 0)
            {
                continue;
            }

            items.AddRange(ticked.Select(c => new SessionItem(c.Node, model.Keep.Node)));
        }

        if (items.Count > 0)
        {
            _ = BeginMoveAsync(LeanModule.Duplicates, items, NoModuleItems);
        }
    }

    // ---- confirmation and run -----------------------------------------------------------------------------------------

    private async Task BeginMoveAsync(LeanModule module, List<SessionItem> items, IReadOnlySet<string> moduleItems)
    {
        if (MoveBlocked || items.Count == 0)
        {
            return;
        }

        // ASP-22: what has the keyboard focus now opened the confirmation (the controls go off below, which takes the focus off them).
        var opener = Content?.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as Control : null;
        var cancelled = false;
        moveBusy = true;
        UpdateMoveControls();
        try
        {
            // The hard-link note opens each file once for its attributes: off the UI thread.
            var notes = await Task.Run(() => items.Select(i => HardLinkNote.For(i.Node)).ToList());
            if (closeAfterMove || !await ConfirmAsync(items, notes))
            {
                cancelled = true;
                return;
            }

            DeletionSession session;
            try
            {
                session = new DeletionSession(module, items);
            }
            catch (ArgumentException ex)
            {
                StatusText.Text = Text.MoveRunFailed(ex.Message);
                return;
            }

            await RunSessionAsync(session, moduleItems);
        }
        finally
        {
            moveBusy = false;
            sessionRunning = false;
            UpdateMoveControls();
            if (cancelled)
            {
                RestoreMoveFocus(opener, module); // Cancel and Esc: the focus goes back to where the user was
            }

            CloseIfHeldForMove();
        }

        if (pendingUninstallNotice is { } notice)
        {
            pendingUninstallNotice = null;
            await ShowUninstallResultAsync(notice);
        }
    }

    // ASP-22 (docs/spec/02): after the confirmation closes without moving, the keyboard focus is on the control that opened it; when that is
    // gone (a row menu that has closed, a tile) or off, on the Move button of the module's own view; with none, where WinUI leaves it. The
    // dialog hands the focus back as it closes, so this runs once now and once more after the layout has settled.
    private void RestoreMoveFocus(Control? opener, LeanModule module)
    {
        void Restore()
        {
            if (moveBusy || closeAfterMove)
            {
                return;
            }

            if (opener is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible })
            {
                // Row actions collapse when the confirmation takes focus. Focus the row first: its GotFocus handler makes the action
                // panel visible again, so the opener can receive focus even though its own Visibility never changed.
                for (DependencyObject? parent = opener; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is ListViewItem row)
                    {
                        row.Focus(FocusState.Programmatic);
                        break;
                    }
                }

                if (opener.Focus(FocusState.Programmatic))
                {
                    return;
                }
            }

            if (MoveButtonOf(module) is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } fallback)
            {
                fallback.Focus(FocusState.Programmatic);
            }
        }

        Restore();
        // WinUI can restore another control (such as Overview) while the dialog finishes closing, not only leave the focus empty.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore);
    }

    private Button? MoveButtonOf(LeanModule module) => module switch
    {
        LeanModule.LargeOld => MoveLargeOldButton,
        LeanModule.Duplicates => MoveDuplicatesButton,
        LeanModule.Temp => MoveTempButton,
        LeanModule.Installers => MoveInstallersButton,
        LeanModule.Uninstall => MoveLeftoversButton,
        LeanModule.Map => MoveLargestButton,
        _ => null,
    };

    // ASP-24: a close while moveBusy (OnAppWindowClosing has cancelled it).
    private void HoldCloseForMove()
    {
        closeAfterMove = true;
        if (sessionRunning)
        {
            StatusText.Text = Text.LeanClosesWhenMovingEnds; // the session runs to its end; also while the views are refreshed after it
        }
        else if (putBackRunning)
        {
            StatusText.Text = Text.LeanClosesWhenPuttingBackEnds;
        }
        else
        {
            moveDialog?.Hide(); // the confirmation ends as Cancel: nothing moves; the window then closes
        }
    }

    // Called once moveBusy is false again: closes the window when a close was held, and says whether it did.
    private bool CloseIfHeldForMove()
    {
        if (!closeAfterMove)
        {
            return false;
        }

        closeAfterMove = false;
        Close(); // moveBusy is false now, so the close is not held again
        return true;
    }

    // ConfirmMove (plan group 11): the items, the total, the re-check line; the default button is Cancel, so Enter moves nothing.
    private async Task<bool> ConfirmAsync(List<SessionItem> items, List<string?> notes)
    {
        if (Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var list = new StackPanel { Spacing = 8 };
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lines = new List<string> { Text.ConfirmItem(item.Node.Path, ByteUnits.Format(item.Node.AllocatedBytes)) };
            if (item.KeptCopy is { } kept)
            {
                lines.Add(Text.Keeping(kept.Path));
            }

            if (notes[i] is { } note)
            {
                lines.Add(note);
            }

            var text = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
            AutomationProperties.SetAutomationId(text, "ConfirmItem");
            AutomationProperties.SetName(text, text.Text);
            list.Children.Add(text);
        }

        var total = new TextBlock { Text = Text.AboutTotal(ByteUnits.Format(items.Sum(i => i.Node.AllocatedBytes))), Style = LeanStyles.Medium };
        AutomationProperties.SetAutomationId(total, "ConfirmTotal");
        AutomationProperties.SetName(total, total.Text);
        var recheck = new TextBlock { Text = Text.CheckedAgainBeforeMove, TextWrapping = TextWrapping.Wrap, Style = LeanStyles.Sentence };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new ScrollViewer { MaxHeight = 360, Content = list });
        body.Children.Add(total);
        body.Children.Add(recheck);

        var title = Text.ConfirmMoveTitle(items.Count);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = body,
            PrimaryButtonText = Text.MoveToRecycleBinConfirm,
            PrimaryButtonStyle = LeanStyles.Primary,
            RequestedTheme = ElementTheme.Dark,
            CloseButtonText = Text.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmMove");
        AutomationProperties.SetName(dialog, title);
        moveDialog = dialog;
        // Cancel, the default button, is drawn with WinUI's accent style, which Theme.xaml makes a ghost button (ASP-12: one primary).
        WireDialogButtons(dialog);
        confirmDialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (COMException)
        {
            // Another dialog is open: nothing is moved.
            return false;
        }
        finally
        {
            confirmDialogOpen = false;
            moveDialog = null;
        }
    }

    // The dialog's own buttons get the contract's ids: the primary one is ConfirmMoveButton, the close one CancelMove (ConfirmClear
    // names them ConfirmClearButton and CancelClear).
    private static void WireDialogButtons(ContentDialog dialog, string primaryId = "ConfirmMoveButton", string closeId = "CancelMove", string? secondaryId = null)
    {
        void Apply() => NameDialogButtons(dialog, primaryId, closeId, secondaryId);
        void OnLayoutUpdated(object? sender, object e) => Apply();
        dialog.Loading += (_, _) => Apply();
        dialog.Loaded += (_, _) => Apply();
        dialog.Opened += (_, _) => Apply();
        dialog.LayoutUpdated += OnLayoutUpdated;
        dialog.Closed += (_, _) => dialog.LayoutUpdated -= OnLayoutUpdated;
    }

    private static void NameDialogButtons(ContentDialog dialog, string primaryId = "ConfirmMoveButton", string closeId = "CancelMove", string? secondaryId = null)
    {
        var stack = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        void Push(DependencyObject? node)
        {
            if (node is not null && visited.Add(node))
            {
                stack.Push(node);
            }
        }

        Push(dialog);
        if (dialog.XamlRoot is { } xamlRoot)
        {
            var dialogId = AutomationProperties.GetAutomationId(dialog);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                var popupId = AutomationProperties.GetAutomationId(popup);
                if (ReferenceEquals(popup.Child, dialog)
                    || string.IsNullOrEmpty(popupId)
                    || string.Equals(popupId, dialogId, StringComparison.Ordinal))
                {
                    Push(popup);
                    Push(popup.Child);
                }
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Button { Name: "PrimaryButton" } primary)
            {
                SetButtonAutomationId(primary, primaryId);
            }
            else if (current is Button { Name: "CloseButton" } close)
            {
                SetButtonAutomationId(close, closeId);
            }

            if (secondaryId is not null && current is Button { Name: "SecondaryButton" } secondary)
            {
                SetButtonAutomationId(secondary, secondaryId);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    private static void SetButtonAutomationId(Button button, string id)
    {
        var current = AutomationProperties.GetAutomationId(button);
        if (!string.Equals(current, id, StringComparison.Ordinal))
        {
            AutomationProperties.SetAutomationId(button, id);
            if (FrameworkElementAutomationPeer.FromElement(button) is { } peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.AutomationIdProperty, current ?? string.Empty, id);
                peer.InvalidatePeer();
            }
        }
    }

    private void OnCancelMovingClick(object sender, RoutedEventArgs e) => moveCancel?.Cancel();

    // One session per confirmation. The Trasher runs on a background thread, one item at a time; Cancel stops between items.
    private async Task RunSessionAsync(DeletionSession session, IReadOnlySet<string> moduleItems)
    {
        using var cancel = new CancellationTokenSource();
        moveCancel = cancel;
        sessionRunning = true;
        var scanned = tree;
        var uninstallApp = session.Module == LeanModule.Uninstall ? leftoversOf : null;
        var uninstallContext = uninstallResultContext;
        var userFolders = userProtectedFolders;
        MoveReport.Visibility = Visibility.Collapsed;
        reportSession = null;
        StatusText.Text = Text.MovingItems;
        UpdateMoveControls();
        // Created on the UI thread, so each report is posted back to it; one that arrives after the run is ignored.
        var progress = new Progress<MoveProgress>(report =>
        {
            if (moveCancel is not null && !closeAfterMove) // a pending close keeps its own line
            {
                StatusText.Text = Text.MovingProgress(report.Index, report.Count, report.Name);
            }
        });

        IReadOnlyList<TrashOutcome> outcomes;
        try
        {
            var token = cancel.Token;
            outcomes = await Task.Run(() =>
            {
                var gate = new KitItemGate(session.Module, moduleItems, userFolders);
                var journal = JournalStore.Open(JournalStore.DefaultPath);
                return new Trasher(gate, NewRecycler(), journal).Run(session, token, progress);
            });
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the window comes back to idle and says so; it never hangs.
            StatusText.Text = Text.MoveRunFailed(ex.Message);
            return;
        }
        finally
        {
            moveCancel = null;
        }

        var trashed = outcomes.Where(o => o.Result == TrashResult.Trashed).ToList();
        ShowReport(session, moduleItems, outcomes, trashed);
        if (closeAfterMove)
        {
            return; // every outcome is journaled; the window closes now (BeginMoveAsync), so nothing reads the tree again
        }

        StatusText.Text = outcomes.Count < session.Items.Count ? Text.MovingStopped
            : trashed.Count == 0 ? Text.NothingMoved
            : Text.MovedToRecycleBin(ByteUnits.Format(ReportBytes(trashed)));

        if (trashed.Count > 0)
        {
            await RefreshAfterMoveAsync(scanned, trashed.Select(o => o.Item.Node).ToList());
        }

        if (uninstallApp is not null)
        {
            var summary = Ambysto.Lean.Core.Uninstall.UninstallCleanupSummary.From(session.Items.Count, outcomes);
            pendingUninstallNotice = new UninstallNotice(uninstallApp, uninstallContext,
                summary.Complete ? Text.UninstallCleanupComplete : Text.UninstallCleanupPartial,
                summary.Approved, summary.Moved, summary.Unsuccessful, summary.Unattempted, summary.Recoverable);
        }
    }

    // The real shell move; a Debug build with --move-delay wraps it so each move waits first (MainWindow.DebugQa.cs).
    private static IShellRecycler NewRecycler()
    {
        IShellRecycler recycler = new ShellRecycler();
        WrapRecycler(ref recycler);
        return recycler;
    }

    // Debug only (MainWindow.DebugQa.cs, --move-delay): the shell move of each item waits first, off the UI thread, so a test can close the
    // window while an item is being moved. Release builds have no body.
    static partial void WrapRecycler(ref IShellRecycler recycler);

    // Debug only (MainWindow.DebugQa.cs, --move-delay): a Put back waits before it starts, off the UI thread.
    static partial void HoldPutBack();

    private static long ReportBytes(IEnumerable<TrashOutcome> trashed) => trashed.Sum(o => o.Entry?.Bytes ?? o.Item.Node.AllocatedBytes);

    // ---- report -------------------------------------------------------------------------------------------------------

    private void ShowReport(DeletionSession session, IReadOnlySet<string> moduleItems, IReadOnlyList<TrashOutcome> outcomes, List<TrashOutcome> trashed)
    {
        if (outcomes.Count == 0)
        {
            return;
        }

        reportRows.Clear();
        // Every outcome that has something to say: not moved, lost, or moved but not journaled (the report names where it went).
        foreach (var outcome in outcomes.Where(o => o.Result != TrashResult.Trashed || !string.IsNullOrEmpty(o.Reason)))
        {
            reportRows.Add((outcome.Item.Node.Path, outcome.Reason ?? string.Empty));
        }

        // Only an item with a journal entry can be put back.
        reportSession = new ReportSession(session.Id, session.Module, moduleItems, trashed.Count(o => o.Entry is not null));
        var any = trashed.Count > 0;
        MovedSummaryText.Text = any ? Text.MovedToRecycleBin(ByteUnits.Format(ReportBytes(trashed))) : string.Empty;
        SpaceReturnsText.Text = any ? Text.SpaceReturnsWhenEmptied : string.Empty;
        MovedSummaryText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SpaceReturnsText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        RenderReportRows();
        MoveReport.Visibility = Visibility.Visible;
        UpdateMoveControls();
    }

    private void RenderReportRows()
    {
        ReportRows.Children.Clear();
        foreach (var (path, result) in reportRows)
        {
            var row = new TextBlock { Text = Text.LabelValue(path, result), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(row, "ReportItem");
            AutomationProperties.SetName(row, row.Text);
            ReportRows.Children.Add(row);
        }
    }

    private void SetReportRow(string path, string result)
    {
        var index = reportRows.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            reportRows[index] = (path, result);
        }
        else
        {
            reportRows.Add((path, result));
        }
    }

    private void OnCloseReportClick(object sender, RoutedEventArgs e)
    {
        MoveReport.Visibility = Visibility.Collapsed;
        reportSession = null;
        UpdateMoveControls();
    }

    // "Put back all from this session": every item of the session still in the Recycle Bin, on a background thread.
    private async void OnPutBackSessionClick(object sender, RoutedEventArgs e)
    {
        if (moveBusy || cleanBusy || checkingRoot || reportSession is not { Remaining: > 0 } session)
        {
            return;
        }

        moveBusy = true;
        putBackRunning = true;
        UpdateMoveControls();
        var userFolders = userProtectedFolders;
        try
        {
            var outcomes = await Task.Run(() =>
            {
                HoldPutBack(); // Debug only: --move-delay
                var gate = new KitItemGate(session.Module, session.ModuleItems, userFolders);
                return new PutBack(JournalStore.Open(JournalStore.DefaultPath), gate).RestoreSession(session.SessionId);
            });
            foreach (var outcome in outcomes)
            {
                SetReportRow(outcome.Entry.OriginalPath, outcome.Restored ? Text.PutBackDone : outcome.Reason ?? string.Empty);
            }

            var restored = outcomes.Count(o => o.Restored);
            session.Remaining = Math.Max(0, session.Remaining - restored);
            RenderReportRows();
            if (!closeAfterMove) // a pending close keeps its own line
            {
                StatusText.Text = Text.PutBackSummary(restored, outcomes.Count);
                _ = LoadVolumeCardAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = Text.PutBackRunFailed(ex.Message);
        }
        finally
        {
            moveBusy = false;
            putBackRunning = false;
            UpdateMoveControls();
            CloseIfHeldForMove();
        }
    }

    // ---- after a move: the scan tree and every view -------------------------------------------------------------------

    private async Task RefreshAfterMoveAsync(ScanTree? scannedBefore, IReadOnlyList<ScanNode> trashedNodes)
    {
        var paths = trashedNodes.Select(n => NormalPath(n.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (scannedBefore is not null && ReferenceEquals(tree, scannedBefore))
        {
            // The tree is not safe to change while anything reads it: the map layout, the Large and old pass, a duplicates search or
            // an export (the last two cannot start during a move, but this never relies on it alone).
            while (layoutRunning || largeOldRuns > 0 || duplicatesCancel is not null || duplicatesRuns > 0 || exportCancel is not null)
            {
                await Task.Delay(20);
            }

            var ids = new List<long>();
            foreach (var node in trashedNodes)
            {
                var target = ReferenceEquals(scannedBefore.Find(node.Id), node) ? node : FindByPath(scannedBefore.Root, node.Path);
                if (target is not null)
                {
                    ids.Add(target.Id);
                }
            }

            scannedBefore.Remove(ids);
            if (shown is null || !IsAttached(shown, scannedBefore.Root))
            {
                shown = scannedBefore.Root;
            }
        }

        PruneDuplicates(paths);
        PruneLeftovers(paths);
        PruneTemp(paths);
        UpdateMoveControls();
        await RelayoutAsync();
        if (largeOldSuggesting)
        {
            // Suggest's own list keeps the rows that stayed (and their ticks); when none is left, the filtered list comes back.
            LargeOldTable.RemoveWhere(r => r.Tag is LargeOldRow row && IsAtOrBelowAny(row.Node.Path, paths));
            if (LargeOldTable.Count == 0)
            {
                EndLargeOldSuggestion();
                LargeOldTable.Clear();
                await RefreshLargeOldAsync();
            }
            else
            {
                UpdateMoveControls();
            }
        }
        else if (LargeOldView.Visibility == Visibility.Visible)
        {
            await RefreshLargeOldAsync();
        }

        await RefreshResultsAsync();
        _ = LoadVolumeCardAsync();
    }

    private static string NormalPath(string path) => path.TrimEnd('\\', '/');

    // True when the path is one of the roots or lies below one (whole components, ignoring case): a parent that moved takes its
    // contents with it.
    private static bool IsAtOrBelowAny(string path, HashSet<string> roots)
    {
        var current = NormalPath(path);
        while (true)
        {
            if (roots.Contains(current))
            {
                return true;
            }

            var cut = current.LastIndexOfAny(['\\', '/']);
            if (cut <= 0)
            {
                return false;
            }

            current = current[..cut];
        }
    }

    private static bool IsAttached(ScanNode node, ScanNode root)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    // The node of the scan at this path, found by walking down from the root; null when the scan has none.
    private static ScanNode? FindByPath(ScanNode root, string path)
    {
        var wanted = NormalPath(path);
        var node = root;
        while (true)
        {
            if (string.Equals(NormalPath(node.Path), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            ScanNode? next = null;
            foreach (var child in node.Children)
            {
                var childPath = NormalPath(child.Path);
                if (wanted.Length > childPath.Length && wanted.StartsWith(childPath, StringComparison.OrdinalIgnoreCase) && wanted[childPath.Length] is '\\' or '/')
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            node = next;
        }
    }

    // The duplicate groups lose the copies that were moved; a group left with fewer than two copies is gone.
    private void PruneDuplicates(HashSet<string> trashedPaths)
    {
        if (duplicateModels.Count == 0)
        {
            return;
        }

        var updated = new List<DuplicateGroupModel>();
        foreach (var model in duplicateModels)
        {
            if (!model.Group.Copies.Any(c => IsAtOrBelowAny(c.Node.Path, trashedPaths)))
            {
                updated.Add(model);
                continue;
            }

            var remaining = model.Group.Copies.Where(c => !IsAtOrBelowAny(c.Node.Path, trashedPaths)).ToList();
            if (remaining.Count < 2)
            {
                continue;
            }

            var keep = remaining.FirstOrDefault(c => ReferenceEquals(c, model.Group.SuggestedKeep)) ?? remaining[0];
            var estimate = remaining.Where(c => !ReferenceEquals(c, keep) && c.Hint is null).Sum(c => c.Node.AllocatedBytes);
            var chosen = remaining.FirstOrDefault(c => ReferenceEquals(c, model.Keep)); // the choice stays when its copy does
            updated.Add(new DuplicateGroupModel(new DuplicateGroup(model.Group.LogicalBytes, remaining, keep, estimate), chosen));
        }

        if (duplicatesWhy && !updated.Any(m => m.Group.Copies.Any(m.ShowsWhy)))
        {
            duplicatesWhy = false; // no copy has a reason any more: the Why column goes
        }

        SetDuplicates(updated);
        if (updated.Count == 0)
        {
            DuplicatesMessage.Text = Text.NoDuplicatesFound;
        }
    }
}
