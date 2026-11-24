using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ambysto.Lean.App;

/// <summary>The Large and old, Duplicates and Uninstall views (plan groups 8b and 11, "UI contract"). They list and mark; a move
/// starts only from their Move controls, through <c>ConfirmMove</c> and the Trasher (MainWindow.Moves.cs). "Uninstall…" only opens
/// Settings through <see cref="SystemLauncher"/>, and a row's Reveal goes through it too. What counts as large, old, identical or
/// gone is decided in the Core.</summary>
public sealed partial class MainWindow
{
    private static readonly long[] SizeChoices = [50 * ByteUnits.MB, 100 * ByteUnits.MB, 500 * ByteUnits.MB, ByteUnits.GB];

    // 6 months, 12 months and 2 years, as days (docs/spec/03: "last used more than <age> ago").
    private static readonly TimeSpan[] AgeChoices = [TimeSpan.FromDays(183), TimeSpan.FromDays(365), TimeSpan.FromDays(730)];

    private const int DefaultSizeChoice = 1;
    private const int DefaultAgeChoice = 1;

    private bool findViewsReady;
    private int largeOldVersion;
    private CancellationTokenSource? duplicatesCancel;

    // Searches still reading the tree, counted until the finder returns: a cancelled search clears duplicatesCancel at
    // once (a new scan resets the views) but may still be exiting (group 11 review round 2, MINOR 1).
    private int duplicatesRuns;
    private CancellationTokenSource? appGoneCancel;
    private DispatcherTimer? duplicatesTimer;
    private int uninstallVersion;
    private bool uninstallRebuilding;
    private int settingsLaunchesPending; // "Uninstall…" launches still waiting for Settings to open: above 0 counts as a running watch for the keep rule
    private int duplicatesGeneration;
    private int largeOldRuns;
    private List<DuplicateGroupModel> duplicateModels = [];
    // Every app and package the owners inventory (hidden entries included) has listed since the window opened. It only grows, so an
    // app found gone later is still known; "the other apps found gone while the app runs" are the ones in it that are no longer listed.
    private readonly List<InstalledApp> knownApps = [];

    // Every visible string, from Copy.Text; the choices start at 100 MB and 12 months (docs/spec/03).
    private void InitializeFindViews()
    {
        LargeOldTitle.Text = Text.LargeOld;
        MinimumSizeBox.Header = Text.MinimumSizeLabel;
        AutomationProperties.SetName(MinimumSizeBox, Text.MinimumSizeLabel);
        MinimumAgeBox.Header = Text.MinimumAgeLabel;
        AutomationProperties.SetName(MinimumAgeBox, Text.MinimumAgeLabel);
        foreach (var bytes in SizeChoices)
        {
            MinimumSizeBox.Items.Add(Choice(ByteUnits.Format(bytes)));
        }

        foreach (var label in new[] { Text.SixMonths, Text.TwelveMonths, Text.TwoYears })
        {
            MinimumAgeBox.Items.Add(Choice(label));
        }

        MinimumSizeBox.SelectedIndex = DefaultSizeChoice;
        MinimumAgeBox.SelectedIndex = DefaultAgeChoice;

        DuplicatesTitle.Text = Text.Duplicates;
        FindDuplicatesButton.Content = Text.FindDuplicates;
        CancelDuplicatesButton.Content = Text.Cancel;
        MoveDuplicatesButton.Content = Text.MoveToRecycleBin;
        MoveLargeOldButton.Content = Text.MoveToRecycleBin;
        SetLargeOldColumns(withWhy: false);

        LeftoverTable.SetColumns(
        [
            new TableColumn(Text.ColumnMove, SelectAllColumn),
            new TableColumn(Text.ColumnFolder),
            new TableColumn(Text.ColumnSize, 90),
            new TableColumn(Text.ColumnNote, 280),
        ]);
        LeftoverTable.EnableSelectAll(Text.SelectAll, () => !SelectAllBlocked);

        UninstallTitle.Text = Text.Uninstall;
        UninstallTable.SetColumns(
        [
            new TableColumn(Text.ColumnName),
            new TableColumn(Text.ColumnPublisher, 180),
            new TableColumn(Text.ColumnVersion, 110),
            new TableColumn(Text.ColumnSize, 90),
            new TableColumn(Text.ColumnInstalledOn, 110),
            new TableColumn(Text.ColumnLastUpdated, 110),
        ]);
        UninstallTable.SingleSelection = true;
        UninstallTable.SelectionChanged += OnUninstallSelectionChanged;
        UninstallInSettingsButton.Content = Text.UninstallInSettingsButton;

        Closed += (_, _) =>
        {
            duplicatesCancel?.Cancel();
            appGoneCancel?.Cancel();
        };
        DuplicatesList.ElementClearing += OnDuplicatesElementClearing;
        InitializeDuplicateHelpers();
        findViewsReady = true;
    }

    // Suggest's own list (ASP-17, ASP-22): the Folder cell is shortened to 26 characters (JetBrains Mono at 14 px is 8.4 px a character, so
    // 218 px, in a 224 px column); the full path is its tooltip.
    private const int SuggestFolderChars = 26;

    // The columns of the Large and old table. Suggest's own list (ASP-17) has Move, Name, Folder, Size, Last modified and Why, and no Type (the
    // date and the kind are in Why), so that every row is one line. ASP-22: every column but Why has the width its cell needs (Name 190, a name
    // of up to 22 characters, longer ones trimmed with the full name as tooltip; Folder 224, Size 90, Last modified 110), and Why alone takes
    // what is left and alone gives way when the window is narrow (down to 40 px, trimmed, its tooltip the full text), so the date is never cut.
    private void SetLargeOldColumns(bool withWhy)
    {
        var columns = new List<TableColumn>
        {
            new(Text.ColumnMove, SelectAllColumn),
            withWhy ? new TableColumn(Text.ColumnName, 190) : new TableColumn(Text.ColumnName),
            new(Text.ColumnFolder, withWhy ? 224 : 290),
            new(Text.ColumnSize, 90),
            new(Text.ColumnLastModified, 110),
        };
        if (withWhy)
        {
            columns.Add(new TableColumn(Text.ColumnWhy, MinWidth: 40));
        }
        else
        {
            columns.Add(new TableColumn(Text.ColumnType, 130));
        }

        LargeOldTable.SetColumns(columns);
        LargeOldTable.EnableSelectAll(Text.SelectAll, () => !SelectAllBlocked);
    }

    // An option written out as an item, so its UI Automation name is the label whatever the control does with strings.
    private static ComboBoxItem Choice(string label)
    {
        var item = new ComboBoxItem { Content = label };
        AutomationProperties.SetName(item, label);
        return item;
    }

    // The word of a file-type group, as the map's legend writes it.
    private static string TypeName(TypeGroupKind kind) => kind switch
    {
        TypeGroupKind.Video => Text.TypeVideo,
        TypeGroupKind.DiskImage => Text.TypeDiskImage,
        TypeGroupKind.Archive => Text.TypeArchive,
        TypeGroupKind.Document => Text.TypeDocument,
        TypeGroupKind.Application => Text.TypeApplication,
        TypeGroupKind.VirtualMachineImage => Text.TypeVirtualMachineImage,
        TypeGroupKind.OpenFolder => Text.TypeFolder,
        _ => Text.TypeOther,
    };

    // A new scan empties what the views list: their rows named the old tree, and a running search is for that tree.
    private void ResetFindViews()
    {
        duplicatesGeneration++; // the search that runs, if any, is stale: its answer is thrown away
        duplicatesCancel?.Cancel();
        EndDuplicatesSearch();
        largeOldVersion++;
        EndLargeOldSuggestion();
        EndTempSuggestion();
        LargeOldTable.Clear();
        SetDuplicates(null);
        DuplicatesMessage.Text = string.Empty;
    }

    // The view back to idle: no ring, no ticker, Find on and Cancel off.
    private void EndDuplicatesSearch()
    {
        duplicatesTimer?.Stop();
        duplicatesTimer = null;
        duplicatesCancel = null;
        DuplicatesRing.IsActive = false;
        DuplicatesRing.Visibility = Visibility.Collapsed;
        CancelDuplicatesButton.IsEnabled = false;
        FindDuplicatesButton.IsEnabled = true;
        UpdateMoveControls();
    }

    // The groups the list shows, and the ticks they hold; the Move control follows them.
    private void SetDuplicates(List<DuplicateGroupModel>? models)
    {
        duplicateModels = models ?? [];
        if (models is null)
        {
            duplicateTab = 0; // a new search or a new scan starts on All files
            duplicatesWhy = false; // and without Suggest's Why column
        }

        BuildDuplicateTabs();
        ShowDuplicates(toTop: models is null);
        UpdateMoveControls();
    }

    // The scan finished while the Large and old view is open: list its rows now.
    private void OnScanFinishedForViews()
    {
        UpdateSuggestControls(); // Suggest in Large and old needs a scan, and the tree is set after the scan returned to idle
        if (LargeOldView.Visibility == Visibility.Visible)
        {
            _ = RefreshLargeOldAsync();
        }
    }

    // ---- Large and old ------------------------------------------------------------------------------------------------

    private void OnLargeOldClick(object sender, RoutedEventArgs e)
    {
        ShowView(LargeOldView);
        if (suggestBusy)
        {
            largeOldListDeferred = true; // listed when the Suggest walk is over, if it showed nothing
        }
        else if (!largeOldSuggesting)
        {
            _ = RefreshLargeOldAsync(); // Suggest's own list stays while the user looks elsewhere and comes back
        }
    }

    private void OnLargeOldChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (findViewsReady)
        {
            _ = RefreshLargeOldAsync();
        }
    }

    // Show system items (ASP-14: here, the only list it changes): the rows are listed again.
    private void OnSystemItemsClick(object sender, RoutedEventArgs e) => _ = RefreshLargeOldAsync();

    // The rows of the last scan under the chosen thresholds. The Core pass runs off the UI thread; the latest request wins.
    private async Task RefreshLargeOldAsync()
    {
        if (largeOldSuggesting)
        {
            return; // a suggestion on screen is never replaced by a listing; whoever ends it (EndLargeOldSuggestion) lists again
        }

        var version = ++largeOldVersion;
        if (tree is not { } scanned)
        {
            LargeOldTable.Clear();
            UpdateMoveControls();
            LargeOldTable.Visibility = Visibility.Collapsed;
            LargeOldMessage.Text = Text.ScanFirst;
            return;
        }

        var options = new LargeOldOptions(
            SizeChoices[Math.Max(0, MinimumSizeBox.SelectedIndex)],
            AgeChoices[Math.Max(0, MinimumAgeBox.SelectedIndex)],
            SystemItemsBox.IsChecked == true);
        try
        {
            largeOldRuns++; // a tree removal after a move waits for this pass (the tree is not safe to change while it is read)
            IReadOnlyList<LargeOldRow> rows;
            try
            {
                var context = await CurrentContextAsync();
                rows = await Task.Run(() =>
                {
                    var found = LargeOldFinder.Find(scanned, options, DateTimeOffset.Now, context);
                    HoldListing(); // Debug only: --listing-delay
                    return found;
                });
            }
            finally
            {
                largeOldRuns--;
            }

            if (version != largeOldVersion || largeOldSuggesting)
            {
                return; // a newer request, or a suggestion that began while this one read: it must not be overwritten
            }

            LargeOldTable.Clear();
            foreach (var row in rows)
            {
                AddLargeOldRow(row);
            }

            LargeOldTable.Visibility = Visibility.Visible;
            LargeOldMessage.Text = rows.Count == 0 ? Text.NoLargeOldFiles : string.Empty;
            UpdateMoveControls();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            if (version == largeOldVersion)
            {
                StatusText.Text = Text.ScanFailed(scanned.Root.Path, ex.Message);
            }
        }
    }

    // One row: Move (none ticked; a display-only row is disabled, with its hint under the name), Name, Folder, Size, Last modified,
    // Type. The Move box is the only thing that selects a row for moving. Suggest's own list (ASP-17) adds the reason as a last cell and
    // has every row ticked.
    private void AddLargeOldRow(LargeOldRow row, string? why = null, bool ticked = false)
    {
        var path = row.Node.Path;
        var size = ByteUnits.Format(row.Node.AllocatedBytes);
        var modified = DateTimeOffset.UnixEpoch.AddMinutes(row.Node.LastWriteMinutes);
        var (tickCell, box) = LeanTable.TickCell("LargeOldSelect", row.Hint is null);
        box.IsChecked = ticked && row.Hint is null; // before the handlers hook, so the build ticks nothing one by one
        box.Checked += OnLargeOldTickChanged;
        box.Unchecked += OnLargeOldTickChanged;
        var cells = new List<TableCell>
        {
            tickCell,
            why is null ? LeanTable.Label(row.Node.Name, note: row.Hint, toolTip: path)
                        : WithFullText(LeanTable.Label(row.Node.Name, toolTip: path, noteBelow: false), row.Node.Name), // one line, trimmed, full text as tooltip
            LeanTable.FolderLabel(System.IO.Path.GetDirectoryName(path) ?? path, why is null ? LeanTable.FolderChars : SuggestFolderChars),
            LeanTable.SizeLabel(size, row.Node.AllocatedBytes),
            LeanTable.DateLabel(modified),
        };
        if (why is null)
        {
            cells.Add(LeanTable.Label(TypeName(row.Type)));
        }
        else
        {
            cells.Add(WithFullText(LeanTable.Label(why, toolTip: why, noteBelow: false), why));
        }

        var table = LargeOldTable.AddRow("LargeOldRow", row, cells, tick: box, displayOnly: row.Hint is not null, actions: RowActionButtons);
        AutomationProperties.SetHelpText(table.Item, row.Hint is null ? path : path + "\n" + row.Hint);
        table.Item.ContextRequested += OnRowContextRequested;
    }

    private void OnLargeOldTickChanged(object sender, RoutedEventArgs e) => UpdateMoveControls();

    // The same menu as a map tile's: Reveal in File Explorer, and Move to Recycle Bin. Opening the menu changes no tick. The row
    // rule (plan group 11: a row that is not selected becomes the selection first) applies when Move is chosen, in OnMoveMenuClick:
    // a movable row that is not ticked replaces the ticks, visibly, and a ticked row leaves them. A display-only row's Move is disabled.
    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListViewItem { Tag: TableRow { Tag: LargeOldRow row } } item)
        {
            return;
        }

        menuPath = row.Node.Path;
        menuNode = row.Node;
        menuSource = MenuSource.LargeOldRow;
        revealItem.IsEnabled = true;
        menuTableRow = (TableRow)item.Tag;
        moveItem.IsEnabled = !MoveBlocked && row.Hint is null;
        if (e.TryGetPosition(item, out var position))
        {
            tileMenu.ShowAt(item, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
        }
        else
        {
            tileMenu.ShowAt(item);
        }

        e.Handled = true;
    }

    // ---- Duplicates ---------------------------------------------------------------------------------------------------

    private void OnDuplicatesClick(object sender, RoutedEventArgs e)
    {
        ShowView(DuplicatesView);
        if (tree is null && duplicatesCancel is null)
        {
            DuplicatesMessage.Text = Text.ScanFirstDuplicates;
        }
    }

    private void OnCancelDuplicatesClick(object sender, RoutedEventArgs e) => duplicatesCancel?.Cancel();

    // The search reads files, so it runs off the UI thread, shows that it is alive in the status line and can be cancelled. The
    // Core compares every byte of the candidates; this only shows its groups.
    private async void OnFindDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (duplicatesCancel is not null || moveBusy)
        {
            return;
        }

        if (tree is not { } scanned)
        {
            DuplicatesMessage.Text = Text.ScanFirstDuplicates;
            return;
        }

        using var cancel = new CancellationTokenSource();
        var generation = ++duplicatesGeneration;
        duplicatesCancel = cancel;
        duplicatesRuns++;
        SetDuplicates(null);
        DuplicatesMessage.Text = string.Empty;
        FindDuplicatesButton.IsEnabled = false;
        UpdateMoveControls(); // no move starts while the search reads the tree
        CancelDuplicatesButton.IsEnabled = true;
        DuplicatesRing.Visibility = Visibility.Visible;
        DuplicatesRing.IsActive = true;
        var watch = Stopwatch.StartNew();
        SetDuplicatesStatus(Text.DuplicatesSearching(0));
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => SetDuplicatesStatus(Text.DuplicatesSearching((int)watch.Elapsed.TotalSeconds));
        duplicatesTimer = ticker;
        ticker.Start();
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            IReadOnlyList<DuplicateGroup> groups = await DuplicateFinder.FindAsync(scanned, DuplicateOptions.Default, context, cancel.Token);

            // The protected folders may have changed in Settings while the search ran: judge its copies against the list as of now.
            if (!ReferenceEquals(context.UserProtectedFolders, userProtectedFolders))
            {
                var latest = await CurrentContextAsync(cancel.Token);
                var found = groups;
                groups = await Task.Run(() => found.Select(g => RehintGroup(g, latest)).ToList(), cancel.Token);
            }

            // A new scan, or a newer search, replaced this one while it ran: its groups name an old tree.
            if (generation != duplicatesGeneration)
            {
                return;
            }

            // A Cancel that came after the finder's last check still cancels: the groups are not shown.
            cancel.Token.ThrowIfCancellationRequested();

            SetDuplicates(groups.Select(g => new DuplicateGroupModel(g)).ToList());
            var summary = groups.Count == 0 ? Text.NoDuplicatesFound : Text.DuplicateGroupsFound(groups.Count);
            DuplicatesMessage.Text = groups.Count == 0 ? summary : string.Empty;
            SetDuplicatesStatus(summary);
        }
        catch (OperationCanceledException)
        {
            if (generation == duplicatesGeneration)
            {
                SetDuplicates(null);
                DuplicatesMessage.Text = Text.DuplicatesCancelled;
                SetDuplicatesStatus(Text.DuplicatesCancelled);
            }
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the view comes back to idle and says so; it never hangs.
            if (generation == duplicatesGeneration)
            {
                SetDuplicates(null);
                StatusText.Text = Text.ScanFailed(scanned.Root.Path, ex.Message);
            }
        }
        finally
        {
            duplicatesRuns--;
            if (generation == duplicatesGeneration)
            {
                EndDuplicatesSearch();
            }
            else
            {
                ticker.Stop();
            }
        }
    }

    // The group with every copy's hint judged again against <paramref name="context"/>, or the same group when no hint changes. A
    // hinted copy stays in its group but frees nothing, so the estimate counts only the other copies (as the Core's finder does).
    private static DuplicateGroup RehintGroup(DuplicateGroup group, PathContext context)
    {
        var hints = group.Copies.Select(c => DisplayOnly.HintFor(c.Node.Path, context)).ToList();
        if (hints.Select((h, i) => h == group.Copies[i].Hint).All(same => same))
        {
            return group;
        }

        var copies = group.Copies.Select((c, i) => c with { Hint = hints[i] }).ToList();
        var index = Math.Max(0, group.Copies.ToList().FindIndex(c => ReferenceEquals(c, group.SuggestedKeep) || ReferenceEquals(c.Node, group.SuggestedKeep.Node)));
        var keep = copies[index];
        var estimate = copies.Where(c => !ReferenceEquals(c, keep) && c.Hint is null).Sum(c => c.Node.AllocatedBytes);
        return new DuplicateGroup(group.LogicalBytes, copies, keep, estimate);
    }

    // The folders in Settings changed: the groups on show are judged again, so that no copy looks movable while the gate would refuse
    // it. A group whose hints did not change stays as it is, ticks included; a changed one keeps the ticks of the copies that may
    // still be ticked.
    private async Task RefreshDuplicateHintsAsync()
    {
        while (duplicateModels.Count > 0)
        {
            var snapshot = duplicateModels;
            var context = await CurrentContextAsync();
            var groups = snapshot.Select(m => m.Group).ToList();
            var rehinted = await Task.Run(() => groups.Select(g => RehintGroup(g, context)).ToList());
            if (!ReferenceEquals(snapshot, duplicateModels))
            {
                continue; // a move pruned the list meanwhile: judge what is on show now
            }

            var updated = new List<DuplicateGroupModel>();
            for (var i = 0; i < snapshot.Count; i++)
            {
                if (ReferenceEquals(rehinted[i], groups[i]))
                {
                    updated.Add(snapshot[i]);
                    continue;
                }

                var keptAt = groups[i].Copies.ToList().FindIndex(c => ReferenceEquals(c, snapshot[i].Keep));
                var model = new DuplicateGroupModel(rehinted[i], keptAt >= 0 ? rehinted[i].Copies[keptAt] : null);
                foreach (var old in snapshot[i].Selection.Ticked)
                {
                    var at = groups[i].Copies.ToList().FindIndex(c => ReferenceEquals(c, old));
                    if (at >= 0 && model.CanMove(rehinted[i].Copies[at]))
                    {
                        model.Selection.Toggle(rehinted[i].Copies[at]);
                    }
                }

                // The copies Suggest ticked keep their reason as long as they are still ticked.
                var stillSuggested = new List<DuplicateCopy>();
                for (var at = 0; at < groups[i].Copies.Count; at++)
                {
                    if (snapshot[i].ShowsWhy(groups[i].Copies[at]) && model.Selection.Ticked.Any(c => ReferenceEquals(c, rehinted[i].Copies[at])))
                    {
                        stillSuggested.Add(rehinted[i].Copies[at]);
                    }
                }

                model.MarkSuggested(stillSuggested);
                updated.Add(model);
            }

            SetDuplicates(updated);
            return;
        }
    }

    // One group of the list: the Core's group, the ticks of its copies and the copy chosen to be kept. They live here, not in the
    // elements, so a group that scrolls out of view and back keeps them. The kept copy is the suggested one at first (keeping is not
    // a move); it is never ticked, so DuplicateSelection (which also never lets every copy be ticked) always leaves it unticked.
    private sealed class DuplicateGroupModel
    {
        private DuplicateSelection? selection;

        public DuplicateGroupModel(DuplicateGroup group, DuplicateCopy? keep = null)
        {
            Group = group;
            Keep = keep is not null && group.Copies.Any(c => ReferenceEquals(c, keep)) ? keep : SuggestedCopy(group);
            Type = TypeOf(group);
        }

        public DuplicateGroup Group { get; }

        /// <summary>The type tab the group is listed under (spec 03): the type group of its suggested copy; when that is "other", of the first copy (in the list's order) whose type is not "other"; else "other".</summary>
        public TypeGroupKind Type { get; }

        /// <summary>A rule's choice (ASP-15): <paramref name="keep"/> becomes the kept copy and every other copy that may be ticked is ticked
        /// (the Core's <c>TickAllBut</c>: never the kept copy, a display-only copy or the last unticked copy).</summary>
        public void ApplyKeep(DuplicateCopy keep)
        {
            ChooseKeep(keep);
            Selection.TickAllBut(keep);
        }

        /// <summary>The copies Suggest ticked in this group (ASP-17), which show their Why while they stay ticked; null when none.</summary>
        private HashSet<DuplicateCopy>? suggested;

        /// <summary>Suggest ticked <paramref name="copies"/>: they show "Older copy; the newest is kept" while they stay ticked.</summary>
        public void MarkSuggested(IEnumerable<DuplicateCopy> copies) => suggested = new HashSet<DuplicateCopy>(copies, ReferenceEqualityComparer.Instance);

        /// <summary>The group forgets what Suggest ticked (the Why column went, or the kept copy was changed by hand).</summary>
        public void ClearSuggested() => suggested = null;

        /// <summary>True when <paramref name="copy"/> is a copy Suggest ticked and it is still ticked.</summary>
        public bool ShowsWhy(DuplicateCopy copy) =>
            suggested is not null && suggested.Contains(copy) && Selection.Ticked.Any(c => ReferenceEquals(c, copy));

        /// <summary>Unticks every copy of the group; the Keep choice stays.</summary>
        public void ClearTicks()
        {
            foreach (var copy in Selection.Ticked)
            {
                Selection.Toggle(copy);
            }
        }

        private static TypeGroupKind TypeOf(DuplicateGroup group)
        {
            var suggested = TypeGroup.Of(SuggestedCopy(group).Node.Path);
            return suggested != TypeGroupKind.Other
                ? suggested
                : group.Copies.Select(c => TypeGroup.Of(c.Node.Path)).Where(t => t != TypeGroupKind.Other).Cast<TypeGroupKind?>().FirstOrDefault() ?? TypeGroupKind.Other;
        }

        public DuplicateSelection Selection => selection ??= new DuplicateSelection(Group);

        /// <summary>The copy that stays; never a ticked one.</summary>
        public DuplicateCopy Keep { get; private set; }

        /// <summary>Chooses <paramref name="copy"/> as the kept copy; if it was ticked, it is unticked first.</summary>
        public void ChooseKeep(DuplicateCopy copy)
        {
            if (!Group.Copies.Any(c => ReferenceEquals(c, copy)))
            {
                return;
            }

            if (!ReferenceEquals(copy, Keep))
            {
                suggested = null; // "the newest is kept" may not be true any more
            }

            if (Selection.Ticked.Any(c => ReferenceEquals(c, copy)))
            {
                Selection.Toggle(copy); // unticking is always allowed
            }

            Keep = copy;
        }

        /// <summary>True when the copy's Move box may be ticked or stay ticked: not display-only, not the kept copy, and not the
        /// last unticked copy (the Core's rule).</summary>
        public bool CanMove(DuplicateCopy copy) => copy.Hint is null && !ReferenceEquals(copy, Keep) && Selection.CanTick(copy);

        /// <summary>The copies to move: ticked, movable, never the kept copy.</summary>
        public List<DuplicateCopy> ToMove() => Selection.Ticked.Where(c => c.Hint is null && !ReferenceEquals(c, Keep)).ToList();

        private static DuplicateCopy SuggestedCopy(DuplicateGroup group) =>
            group.Copies.FirstOrDefault(c => ReferenceEquals(c, group.SuggestedKeep) || ReferenceEquals(c.Node, group.SuggestedKeep.Node)) ?? group.Copies[0];
    }

    // The repeater builds a group's elements when it realises the group and drops them when it recycles it.
    private void OnDuplicatesElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is ContentControl host && sender.ItemsSourceView is { } items && args.Index < items.Count && items.GetAt(args.Index) is DuplicateGroupModel model)
        {
            host.Content = CreateDuplicateGroup(model, args.Index);
        }
    }

    private static void OnDuplicatesElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is ContentControl host)
        {
            host.Content = null;
        }
    }

    // One group: its name and estimate, and a table of its copies: Keep (one radio per group), Move (a box per copy, none ticked),
    // Name, Folder, Size, Last modified and Suggested. DuplicateSelection decides what may be ticked; the kept copy's box is
    // disabled. Built when the list realises the group.
    private GroupPanel CreateDuplicateGroup(DuplicateGroupModel model, int index)
    {
        var group = model.Group;
        var selection = model.Selection;
        var name = Text.CopiesEach(group.Copies.Count, ByteUnits.Format(group.LogicalBytes));
        // Neighbouring groups alternate between the panel and the ground colour, so that they read apart (ASP-15).
        var panel = new GroupPanel
        {
            Spacing = 4,
            Padding = new Thickness(8),
            Background = BrandPalette.GroupGround(index) is { } ground ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ground) : null,
        };
        AutomationProperties.SetAutomationId(panel, "DuplicateGroup");
        AutomationProperties.SetName(panel, name);
        panel.Children.Add(new TextBlock { Text = name, Style = LeanStyles.Medium });
        var estimate = new TextBlock { Text = Text.LabelValue(Text.ReclaimableEstimate, ByteUnits.Format(group.ReclaimableEstimateBytes)), Style = LeanStyles.Note };
        AutomationProperties.SetAutomationId(estimate, "ReclaimableEstimate");
        panel.Children.Add(estimate);

        var table = new LeanTable(embedded: true) { ColumnBands = false }; // the group has its own ground (ASP-15), exactly LeanPanel or LeanGround
        // ASP-17: while Suggest's reasons show, the Suggested badge column (the newest copy, which the Keep radio now shows) and part of the
        // Folder column give way to the Why column, so that all of it fits at the default window width.
        var columns = new List<TableColumn>
        {
            new(Text.ColumnKeep, 56),
            new(Text.ColumnMove, 64),
            new(Text.ColumnName),
            new(Text.ColumnFolder, duplicatesWhy ? 170 : 290),
            new(Text.ColumnSize, 90),
            new(Text.ColumnLastModified, 110),
        };
        columns.Add(duplicatesWhy ? new TableColumn(Text.ColumnWhy, 280) : new TableColumn(Text.ColumnSuggested, 120));

        table.SetColumns(columns);

        var parts = new List<(CheckBox Box, RadioButton Radio, DuplicateCopy Copy)>();
        var whyBlocks = new List<(TextBlock Block, DuplicateCopy Copy)>();
        foreach (var copy in group.Copies)
        {
            var path = copy.Node.Path;
            var notes = new List<string>();
            if (copy.MayShareStorage)
            {
                notes.Add(Text.MayShareStorage);
            }

            if (copy.Hint is not null)
            {
                notes.Add(copy.Hint); // a copy Lean may not move (for example "Protected by you"): shown, never ticked
            }

            var suggested = ReferenceEquals(copy, group.SuggestedKeep) || ReferenceEquals(copy.Node, group.SuggestedKeep.Node);
            var (keepCell, radio) = LeanTable.KeepCell("DuplicateKeep", ReferenceEquals(copy, model.Keep));
            var (tickCell, box) = LeanTable.TickCell("DuplicateCopy", model.CanMove(copy));
            box.IsChecked = selection.Ticked.Any(c => ReferenceEquals(c, copy)); // the model's ticks, so a rule's show in a group built after it (events are hooked below)
            var modified = DateTimeOffset.UnixEpoch.AddMinutes(copy.Node.LastWriteMinutes);
            var cells = new List<TableCell>
            {
                keepCell,
                tickCell,
                duplicatesWhy
                    ? WithFullText(LeanTable.Label(copy.Node.Name, note: notes.Count == 0 ? null : string.Join("; ", notes), toolTip: notes.Count == 0 ? path : path + "\n" + string.Join("; ", notes), noteBelow: false), copy.Node.Name)
                    : LeanTable.Label(copy.Node.Name, note: notes.Count == 0 ? null : string.Join("; ", notes), toolTip: notes.Count == 0 ? path : path + "\n" + string.Join("; ", notes), noteBelow: false),
                LeanTable.FolderLabel(System.IO.Path.GetDirectoryName(path) ?? path),
                LeanTable.SizeLabel(ByteUnits.Format(copy.Node.AllocatedBytes), copy.Node.AllocatedBytes),
                LeanTable.DateLabel(modified),
            };
            if (!duplicatesWhy)
            {
                cells.Add(suggested ? LeanTable.Label(Text.SuggestedKeep, noteBelow: false) : LeanTable.Empty());
            }
            else
            {
                var whyCell = LeanTable.Empty();
                var whyBlock = (TextBlock)whyCell.Element;
                whyBlock.TextWrapping = TextWrapping.NoWrap; // the rows of this list are one line high
                whyBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                SetWhy(whyBlock, model.ShowsWhy(copy));
                whyBlocks.Add((whyBlock, copy));
                cells.Add(whyCell);
            }

            var row = table.AddRow("DuplicateRow", copy, cells, tick: box, keep: radio, displayOnly: copy.Hint is not null, actions: DuplicateRowActions);
            if (notes.Count > 0)
            {
                // On the row itself, so it opens when the row gets keyboard focus, as a display-only row's reason does in the largest files.
                ToolTipService.SetToolTip(row.Item, path + "\n" + string.Join("; ", notes));
            }

            parts.Add((box, radio, copy));
        }

        // The boxes follow the model: the kept copy's is disabled and unticked, a display-only one never enabled, and the last
        // unticked copy cannot be ticked (the Core's rule).
        void Sync()
        {
            foreach (var (box, _, copy) in parts)
            {
                box.IsChecked = selection.Ticked.Any(c => ReferenceEquals(c, copy));
                box.IsEnabled = model.CanMove(copy);
            }

            foreach (var (_, radio, copy) in parts)
            {
                radio.IsChecked = ReferenceEquals(copy, model.Keep); // one choice per group, kept by hand (see KeepCell)
            }

            foreach (var (block, copy) in whyBlocks)
            {
                SetWhy(block, model.ShowsWhy(copy)); // a reason shows only while the copy Suggest ticked stays ticked
            }

            UpdateMoveControls();
        }

        var syncing = false;
        void OnTickChanged(object sender, RoutedEventArgs args)
        {
            if (syncing || sender is not CheckBox changed)
            {
                return;
            }

            syncing = true;
            try
            {
                var copy = parts.First(p => ReferenceEquals(p.Box, changed)).Copy;
                var isTicked = selection.Ticked.Any(c => ReferenceEquals(c, copy));
                if ((changed.IsChecked == true) != isTicked && ((!isTicked && !model.CanMove(copy)) || !selection.Toggle(copy)))
                {
                    changed.IsChecked = isTicked; // refused: a display-only copy, the kept copy, or the last unticked copy
                }

                Sync();
            }
            finally
            {
                syncing = false;
            }
        }

        void OnKeepChecked(object sender, RoutedEventArgs args)
        {
            if (syncing || sender is not RadioButton chosen)
            {
                return;
            }

            syncing = true;
            try
            {
                model.ChooseKeep(parts.First(p => ReferenceEquals(p.Radio, chosen)).Copy);
                Sync();
            }
            finally
            {
                syncing = false;
            }
        }

        foreach (var (box, radio, _) in parts)
        {
            box.Checked += OnTickChanged;
            box.Unchecked += OnTickChanged;
            radio.Checked += OnKeepChecked;
        }

        panel.Children.Add(table);
        return panel;
    }

    // A Why cell: the reason, or nothing (its automation name follows, so a row's name holds it only while it shows).
    private static void SetWhy(TextBlock block, bool shown)
    {
        block.Text = shown ? Text.WhyOlderCopy : string.Empty;
        AutomationProperties.SetName(block, block.Text);
        AutomationProperties.SetHelpText(block, block.Text); // the full text, as the tooltip of a cell that may be trimmed
        ToolTipService.SetToolTip(block, shown ? Text.WhyOlderCopy : null);
    }

    // ---- Uninstall ----------------------------------------------------------------------------------------------------

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        ShowView(UninstallView);
        // ASP-9: a watch (or its leftovers) left running when the view was left stays as it is (the leftover list, the instruction,
        // the selection); the inventory is read again either way, and a row for an app that is gone drops out of the list.
        var keep = appGoneCancel is not null || settingsLaunchesPending > 0 || LeftoverTable.Visibility == Visibility.Visible;
        if (!keep)
        {
            UninstallInstruction.Text = string.Empty;
        }

        var version = ++uninstallVersion;
        try
        {
            var (apps, owners) = await Task.Run(() =>
            {
                var source = new RegistryUninstallSource();
                // The magnifier shows only for an install folder that exists, checked here, when the rows are built, off the UI thread.
                var inventory = UninstallInventory.Read(source);
                var listed = inventory.Zip(InstallFoldersExist(inventory), (app, hasFolder) => (App: app, HasFolder: hasFolder)).ToList();
                return (listed, UninstallInventory.ReadOwners(source));
            });
            RememberApps(owners);
            if (version != uninstallVersion)
            {
                return;
            }

            keep = appGoneCancel is not null || settingsLaunchesPending > 0 || LeftoverTable.Visibility == Visibility.Visible;
            var previous = keep ? SelectedRegisteredApp()?.App : null;
            uninstallRebuilding = keep; // a rebuild that keeps the watch must not read its own clearing as another selection
            try
            {
                if (!keep)
                {
                    appGoneCancel?.Cancel(); // the inventory is replaced: a watch for an entry of the old one ends
                    ClearLeftovers();
                }

                UninstallTable.Clear();
                UninstallTable.Batch(() =>
                {
                    foreach (var (app, hasFolder) in apps)
                    {
                        AddUninstallRow(app, hasFolder);
                    }
                });
                if (previous is not null && UninstallTable.Rows.FirstOrDefault(r => r.Tag is InstalledApp shown && SameApp(shown, previous)) is { } match)
                {
                    UninstallTable.Select(match);
                }
            }
            finally
            {
                uninstallRebuilding = false;
            }

            UpdateUninstallNote();
            if (keep)
            {
                UninstallInSettingsButton.Visibility = SelectedRegisteredApp() is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Text.UninstallReadFailed;
        }
    }

    // A registered or Store app as one table row (ASP-16): name (its class note is shown in UninstallNote when it is selected), publisher, version, size,
    // installed on and last updated; a missing value is an empty cell. The row's automation name stays the display name, or the name
    // with its note. The magnifier is only on a row whose install folder exists (never a Store app's).
    private void AddUninstallRow(InstalledApp app, bool hasFolder)
    {
        var note = ClassNote(app);
        var row = UninstallTable.AddRow("UninstallRow", app,
        [
            LeanTable.NameLabel(app.DisplayName),
            LeanTable.TextValue(app.Publisher),
            LeanTable.TextValue(app.Version),
            app.SizeBytes is { } bytes ? LeanTable.ValueLabel(ByteUnits.Format(bytes), bytes) : LeanTable.Blank(),
            app.InstalledOn is { } installed ? LeanTable.DateValue(installed) : LeanTable.Blank(),
            app.LastUpdated is { } updated ? LeanTable.DateValue(DateOnly.FromDateTime(updated.ToLocalTime().DateTime)) : LeanTable.Blank(),
        ], actions: hasFolder ? new RowActions(CreateUninstallRowActions, (_, _) => { }) : null,
        name: note is null ? app.DisplayName : Text.WithNote(app.DisplayName, note));
        if (note is not null)
        {
            ToolTipService.SetToolTip(row.Item, Text.WithNote(app.DisplayName, note));
            AutomationProperties.SetHelpText(row.Item, note); // the note alone: the name already ends with it, and a screen reader would read it twice
        }
    }

    private static string? ClassNote(InstalledApp app) => app.Class switch
    {
        AppClass.NeedsAdmin => Text.NeedsAdminApp,
        AppClass.StoreApp => Text.StoreAppDisplayOnly,
        _ => null,
    };

    // The selected app's class note beside "Uninstall…" (a Store app, which has no button, shows it alone); hidden when nothing is selected
    // or the app has none. Follows every selection change, the rebuild that keeps the selection included.
    private void UpdateUninstallNote()
    {
        var note = UninstallTable.SelectedRow?.Tag is InstalledApp app ? ClassNote(app) : null;
        UninstallNote.Text = note ?? string.Empty;
        UninstallNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // True for a registered app whose InstallLocation is a fully qualified local path to a folder that exists. A Store app's folder is
    // under WindowsApps: never. A network location is never probed (no SMB traffic, no stall): not a UNC path of any form, and not a drive
    // letter mapped to a share (GetDriveType answers from the local drive table).
    internal static bool HasInstallFolder(InstalledApp app) =>
        app.Class != AppClass.StoreApp && app.InstallLocation is { Length: > 0 } location && IsLocalFullyQualified(location) && LinksStayLocal(location) && Directory.Exists(location);

    internal static bool IsLocalFullyQualified(string location)
    {
        var path = location.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share, \\?\UNC\..., \\.\UNC\...: network. \\?\C:\... and \\.\C:\... are local device paths.
            if (path.Length < 7 || (path[2] != '?' && path[2] != '.') || path[3] != '\\' || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
            {
                return false;
            }
        }

        return IsOnLocalDrive(path);
    }

    private static bool IsOnLocalDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path.StartsWith(@"\\", StringComparison.Ordinal) ? path[4..] : path);
            return root is { Length: > 0 } && new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    // A folder that is itself a symlink or junction counts by its target, read from the reparse data without following it (no network
    // access): every link on the way must lead to a local fully qualified path. A folder that is not a link has no target.
    private static bool LinksStayLocal(string folder)
    {
        var current = folder;
        for (var hop = 0; hop < 8; hop++)
        {
            try
            {
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: false) is not { } target)
                {
                    return true;
                }

                current = target.FullName;
                if (!IsLocalFullyQualified(current))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        return false; // a chain of links that long is not followed
    }

    // The existence checks: at most 8 run at a time (the slots are shared by every opening of the view, so a check stuck on a slow volume
    // holds one slot and the work never grows with each reopen), and the whole list gets 1.5 s. A check not started by then is never
    // started; a folder that has not answered by then gets no magnifier, and a late answer is dropped. Runs off the UI thread (the caller is a
    // Task.Run).
    private const int MaxExistenceChecks = 8;
    private static readonly TimeSpan ExistenceBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly SemaphoreSlim ExistenceSlots = new(MaxExistenceChecks, MaxExistenceChecks);

    private static bool[] InstallFoldersExist(IReadOnlyList<InstalledApp> apps)
    {
        var clock = Stopwatch.StartNew();
        var answers = new int[apps.Count];
        var running = new CountdownEvent(1); // the dispatcher's own count; not disposed, a late check may still signal it
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (app.Class == AppClass.StoreApp || app.InstallLocation is not { Length: > 0 })
            {
                continue;
            }

            var left = ExistenceBudget - clock.Elapsed;
            if (left <= TimeSpan.Zero || !ExistenceSlots.Wait(left))
            {
                break; // out of time: this and every later check is never started
            }

            var index = i;
            running.AddCount();
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    if (HasInstallFolder(app))
                    {
                        Volatile.Write(ref answers[index], 1);
                    }
                }
                finally
                {
                    ExistenceSlots.Release();
                    running.Signal();
                }
            }, null);
        }

        running.Signal();
        running.Wait(ExistenceBudget > clock.Elapsed ? ExistenceBudget - clock.Elapsed : TimeSpan.Zero);

        // The snapshot is the cut-off: an answer that arrives after it is dropped.
        return answers.Select(a => Volatile.Read(ref a) == 1).ToArray();
    }

    private IReadOnlyList<Button> CreateUninstallRowActions(TableRow row)
    {
        var reveal = RowButton("\uE721", Text.ShowInFileExplorer, "RevealRow"); // Segoe Fluent Icons: Search (the magnifier)
        reveal.Click += (_, _) => _ = OpenInstallFolderAsync((row.Tag as InstalledApp)?.InstallLocation);
        return [reveal];
    }

    // Opens the install folder itself, showing its contents; when that fails the status line says so.
    private async Task OpenInstallFolderAsync(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            if (!await SystemLauncher.OpenFolderAsync(folder))
            {
                StatusText.Text = Text.RevealFailed(folder);
            }
        }
        catch (Exception)
        {
            // Nothing escapes a click handler: a folder that is gone, a path the launcher refuses, any other failure.
            StatusText.Text = Text.RevealFailed(folder);
        }
    }

    // The button belongs to a selected registered app; a Store app is display-only.
    private void OnUninstallSelectionChanged(object? sender, EventArgs e)
    {
        UpdateUninstallNote();
        if (uninstallRebuilding)
        {
            return;
        }

        InvalidateUninstallResult();
        UninstallInstruction.Text = string.Empty;
        appGoneCancel?.Cancel(); // another app (or none) is selected: the watch was for the previous one
        ClearLeftovers();
        UninstallInSettingsButton.Visibility = SelectedRegisteredApp() is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private (InstalledApp App, UninstallEntry Entry)? SelectedRegisteredApp() =>
        UninstallTable.SelectedRow is { Tag: InstalledApp { Entry: { } entry } app } ? (app, entry) : null;

    // Lean never runs an uninstaller (owner decision 2026-10-16): it names the entry, opens Settings > Apps through the one
    // module that does that, and watches until the app is gone.
    private async void OnUninstallInSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRegisteredApp() is not { } selected)
        {
            return;
        }

        UninstallInstruction.Text = SettingsHandOff.Instruction(selected.App);
        settingsLaunchesPending++;
        try
        {
            if (!await SystemLauncher.OpenSettingsAsync(SettingsHandOff.AppsPage))
            {
                StatusText.Text = Text.OpenSettingsFailed;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            StatusText.Text = Text.OpenSettingsFailed;
            return;
        }
        catch (Exception)
        {
            // Nothing escapes an event handler: whatever else went wrong is said in the status line.
            StatusText.Text = Text.OpenSettingsFailed;
            return;
        }
        finally
        {
            settingsLaunchesPending--; // success, failure and exception alike
        }

        // The user may have picked another app, or left the view and come back (the table is then read again: the same app, a new
        // entry object), while Settings opened.
        if (SelectedRegisteredApp() is { } still && SameApp(still.App, selected.App))
        {
            _ = WatchAppGoneAsync(selected.App, selected.Entry);
        }
    }

    // One watch at a time (a new one replaces the old); the real source reads the registry, the Store packages and the running
    // processes, and the Core decides when the app is gone. The answer goes to the status line.
    private async Task WatchAppGoneAsync(InstalledApp app, UninstallEntry entry)
    {
        appGoneCancel?.Cancel();
        using var cancel = new CancellationTokenSource();
        appGoneCancel = cancel;
        var noticeContext = ++uninstallResultContext;
        var watcher = new AppGoneWatcher(entry, new RegistryUninstallSource(), TimeProvider.System);
        try
        {
            var gone = await Task.Run(() => watcher.WaitAsync(cancel.Token), CancellationToken.None);
            if (gone && ReferenceEquals(appGoneCancel, cancel))
            {
                StatusText.Text = Text.AppGone(app.DisplayName);
                await OfferLeftoversAsync(app, cancel.Token, noticeContext);
            }
        }
        catch (Exception)
        {
            // The watch is a background task: its failure is said, not dropped.
            if (ReferenceEquals(appGoneCancel, cancel))
            {
                StatusText.Text = Text.AppGoneWatchFailed;
            }
        }
        finally
        {
            if (ReferenceEquals(appGoneCancel, cancel))
            {
                appGoneCancel = null;
            }
        }
    }
}
