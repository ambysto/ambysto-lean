using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>Suggest (Pro) in Temporary files, Large and old and Duplicates (ASP-17 phase C, intent/2026-10-20-asp-17-suggest.md; docs/spec/03,
/// 06). Which rows are suggested, and why, is the Core's (<see cref="Suggest"/>, and for Duplicates the Newest keep rule); this file ticks
/// them, shows the Why column and asks first when more than 100 would be ticked. It only ticks: nothing is moved, and a move starts from
/// Move through the confirmation as ever. In Free the button is disabled and carries the quiet "Pro" label, and a click does nothing.</summary>
public sealed partial class MainWindow
{
    // A Suggest pass of Large and old is working (it reads the scanned tree off the UI thread): no second one starts.
    private bool suggestBusy;

    // A listing that was asked for while a Suggest walk ran (the view was opened again) waits for the walk: a listing that started then would
    // bump the version and throw the suggestion away. It runs when the walk is over and has shown nothing.
    private bool tempReadDeferred;
    private bool largeOldListDeferred;

    // Temporary files: the Why of every suggested row, by path (null: no suggestion, no Why column).
    private Dictionary<string, string>? tempWhy;

    // Large and old: Suggest's own list is on screen (Why column, filters off, Clear suggestion shown).
    private bool largeOldSuggesting;

    // Duplicates: the Why column is on (every group's table has it; only a copy Suggest ticked shows a reason).
    private bool duplicatesWhy;

    // ASP-22: the status line a Suggest wrote ("Suggest ticked 3 items", "Suggested 3 files, 1.21 GB") and the text it replaced, one pair for
    // each view (the status line is shared, the suggestions are not). When that view's suggestion ends (Clear suggestion, a new listing, a
    // new scan) and the line still reads its words, it goes back to what it replaced (the scan's summary, or another view's Suggest line that
    // is still on screen); a line another action has written since is left alone.
    private sealed class SuggestLine
    {
        public string? Text;
        public string Before = string.Empty;
        public SuggestLine? BeforeLine;
    }

    private readonly SuggestLine tempSuggestLine = new();
    private readonly SuggestLine largeOldSuggestLine = new();
    private readonly SuggestLine appsSuggestLine = new();
    private SuggestLine? currentSuggestLine;

    // Remove a finished (or replaced) suggestion from every restoration chain, including when another view currently owns the line.
    // Use the view's identity: two views can both say "Suggest ticked 1 items".
    private void UnlinkSuggestStatus(SuggestLine line)
    {
        foreach (var other in new[] { tempSuggestLine, largeOldSuggestLine, appsSuggestLine })
        {
            if (other.BeforeLine == line)
            {
                other.BeforeLine = line.BeforeLine;
                other.Before = line.Before;
            }
        }
    }

    private void SetSuggestStatus(SuggestLine line, string text)
    {
        if (currentSuggestLine != line || StatusText.Text != line.Text)
        {
            UnlinkSuggestStatus(line);
            line.Before = StatusText.Text;
            line.BeforeLine = currentSuggestLine is { } current && current.Text == StatusText.Text ? current : null;
        }

        line.Text = text;
        currentSuggestLine = line;
        StatusText.Text = text;
    }

    private void EndSuggestStatus(SuggestLine line)
    {
        if (currentSuggestLine == line && line.Text is not null && StatusText.Text == line.Text)
        {
            StatusText.Text = line.BeforeLine?.Text ?? line.Before;
            currentSuggestLine = line.BeforeLine;
        }

        UnlinkSuggestStatus(line);
        line.Text = null;
        line.BeforeLine = null;
    }

    // Every visible string, from Copy.Text.
    private void InitializeSuggest()
    {
        foreach (var button in new[] { SuggestTempButton, SuggestLargeOldButton, SuggestDuplicatesButton })
        {
            button.Content = Text.Suggest;
            AutomationProperties.SetName(button, Text.Suggest);
        }

        foreach (var label in new[] { SuggestTempProLabel, SuggestLargeOldProLabel, SuggestDuplicatesProLabel })
        {
            label.Text = Text.ProLabel;
        }

        ClearSuggestionButton.Content = Text.ClearSuggestion;
        AutomationProperties.SetName(ClearSuggestionButton, Text.ClearSuggestion);
        UpdateSuggestControls();
    }

    // Suggest is Pro, and off whenever Select all is (MoveBlocked: a scan, a duplicates search, an export, the root check, a move or its
    // confirmation), and while its own pass runs. It also needs something to suggest from: rows in Temporary files, a scan for Large and
    // old, groups in Duplicates. In Free it is always off and the Pro label shows; Pro users never see the label.
    private void UpdateSuggestControls()
    {
        var pro = entitlement.IsPro;
        var open = pro && !MoveBlocked && !suggestBusy;
        SuggestTempButton.IsEnabled = open && tempRows.Count > 0;
        SuggestLargeOldButton.IsEnabled = open && tree is not null;
        SuggestDuplicatesButton.IsEnabled = open && duplicateModels.Count > 0;
        ClearSuggestionButton.IsEnabled = !MoveBlocked && !suggestBusy;
        var label = pro ? Visibility.Collapsed : Visibility.Visible;
        SuggestTempProLabel.Visibility = label;
        SuggestLargeOldProLabel.Visibility = label;
        SuggestDuplicatesProLabel.Visibility = label;
        SuggestTempProBadge.Visibility = label;
        SuggestLargeOldProBadge.Visibility = label;
        SuggestDuplicatesProBadge.Visibility = label;
    }

    // Debug only (MainWindow.DebugQa.cs, --listing-delay): waits inside a listing of Large and old or Temporary files, off the UI thread, after
    // the read and before the result is applied. Release removes the declaration and every call.
    static partial void HoldListing();

    // Debug only (MainWindow.DebugQa.cs, --fail-temp-reread): the first re-read inside Suggest's Temp loop throws once. Release removes it.
    static partial void FailTempReread();

    // Debug only (MainWindow.DebugQa.cs, --suggest-delay): waits inside the Suggest walk, off the UI thread, so a test can change the UI
    // meanwhile. Release removes the declaration and every call.
    static partial void HoldSuggestWalk();

    // The listings asked for during a Suggest walk run now, as a normal open would: Temporary files is read again (which drops an older
    // Why), except when the walk that just ended was Temporary files' own and has just shown its suggestion (an older suggestion, a Cancel
    // and a failure do not count: a failure keeps its read error instead, see SuggestTempAsync).
    private void RunDeferredListings(bool tempSuggestionShown = false)
    {
        if (tempReadDeferred)
        {
            tempReadDeferred = false;
            if (!tempSuggestionShown)
            {
                _ = RefreshTempAsync();
            }
        }

        if (largeOldListDeferred)
        {
            largeOldListDeferred = false;
            if (!largeOldSuggesting)
            {
                _ = RefreshLargeOldAsync();
            }
        }
    }

    // A one-line text cell keeps its full text as its tooltip and as its help text (what a screen reader and UI Automation read).
    private static TableCell WithFullText(TableCell cell, string text)
    {
        if (cell.Element is FrameworkElement element)
        {
            AutomationProperties.SetHelpText(element, text);
            ToolTipService.SetToolTip(element, text);
        }

        return cell;
    }

    // The program files the question counts are the rows named like ASP-15's list; a folder row is never one.
    private static bool IsProgramFile(ScanNode node) => !node.IsFolder && ProgramFileTypes.Contains(Path.GetExtension(node.Name));

    // ---- Temporary files ----------------------------------------------------------------------------------------------

    private void OnSuggestTempClick(object sender, RoutedEventArgs e) => _ = SuggestTempAsync();

    // Every row is unticked first, then the rows Suggest.Temp names are ticked, with their reason in a Why column. The count is worked out
    // before anything changes, so Cancel changes nothing.
    private async Task SuggestTempAsync()
    {
        if (!entitlement.IsPro || MoveBlocked || suggestBusy || tempRows.Count == 0)
        {
            return;
        }

        var shownRows = tempRows;
        var version = tempVersion;
        var age = TempAges[Math.Max(0, AgeChoice.SelectedIndex)];
        var shownNow = false; // this run itself showed a suggestion (an older one, a Cancel or a failure is not that)
        var failed = false;
        suggestBusy = true;
        UpdateMoveControls();
        try
        {
            var context = await CurrentContextAsync();
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;
            var rows = shownRows;
            var suggested = await Task.Run(() =>
            {
                var found = Suggest.Temp(rows, now);
                HoldSuggestWalk(); // Debug only: --suggest-delay
                return found;
            });

            // The protected folders may have changed in Settings while the walk ran: the rows are read again (TempFinder, so their hints are
            // fresh) and judged again, until the folders stay the same, before anything is shown or ticked. The list shows the fresh rows.
            while (!ReferenceEquals(context.UserProtectedFolders, userProtectedFolders))
            {
                context = await CurrentContextAsync();
                var current = context;
                var read = await Task.Run(() =>
                {
                    FailTempReread(); // Debug only: --fail-temp-reread
                    return TempFinder.FindAsync(current, age, TimeProvider.System, CancellationToken.None);
                });
                if (version != tempVersion || !ReferenceEquals(shownRows, tempRows))
                {
                    return; // the list was read again, or a move ended, while the rows were read
                }

                rows = [.. read];
                tempRows = rows;
                shownRows = rows;
                tempWhy = null;
                RebuildTempTable(withWhy: false);
                UpdateTempTotal();
                TempMessage.Text = rows.Count == 0 ? Text.NoTempFiles : string.Empty;
                if (rows.Count == 0)
                {
                    return; // nothing is listed any more: the plain listing's message, no suggestion
                }

                suggested = await Task.Run(() => Suggest.Temp(rows, now));
            }

            if (version != tempVersion || !ReferenceEquals(shownRows, tempRows))
            {
                return; // the list was read again while the walk ran
            }

            if (suggested.Count > RuleAsksAbove
                && !await ConfirmRuleAsync(Text.SuggestConfirm(suggested.Count, suggested.Count(s => IsProgramFile(s.Item.Node))), Text.Suggest))
            {
                return; // Cancel: no tick, no column, no status
            }

            if (MoveBlocked || version != tempVersion || !ReferenceEquals(shownRows, tempRows))
            {
                return; // something started, or the list was read again, while the question was open: the plan is dropped
            }

            tempWhy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in suggested)
            {
                tempWhy[row.Item.Node.Path] = row.Why;
            }

            RebuildTempTable(withWhy: true);
            SetSuggestStatus(tempSuggestLine, Text.SuggestTicked(suggested.Count));
            shownNow = true;
        }
        catch (OperationCanceledException)
        {
            // The window closed: nothing to show.
        }
        catch (Exception)
        {
            // As a failed listing: the read error, no Why column, nothing listed or ticked; opening the view lists again (never hangs).
            // A newer read owns the view when the version moved on, and the error is dropped, as RefreshTempAsync drops it.
            if (version == tempVersion)
            {
                tempWhy = null;
                tempRows = [];
                SetTempColumns(withWhy: false);
                TempTable.Clear();
                TempTotal.Text = string.Empty;
                TempMessage.Text = Text.TempReadFailed;
                failed = true;
            }
        }
        finally
        {
            suggestBusy = false;
            UpdateMoveControls();
            if (failed)
            {
                tempReadDeferred = false; // the view shows its read error, as after a failed listing; opening it again lists
            }

            RunDeferredListings(tempSuggestionShown: shownNow);
        }
    }

    // The table again, from the rows as read: with the Why column and the suggested rows ticked, or without it and nothing ticked.
    private void RebuildTempTable(bool withWhy)
    {
        SetTempColumns(withWhy);
        TempTable.Batch(() =>
        {
            foreach (var row in tempRows)
            {
                AddTempRow(row);
            }
        });
    }

    // The Why column goes (a new scan, a new read of the list): the table is built again without it, and nothing stays ticked.
    private void EndTempSuggestion()
    {
        if (tempWhy is null)
        {
            return;
        }

        tempWhy = null;
        EndSuggestStatus(tempSuggestLine);
        RebuildTempTable(withWhy: false);
        UpdateMoveControls();
    }

    // ---- Large and old ------------------------------------------------------------------------------------------------

    private void OnSuggestLargeOldClick(object sender, RoutedEventArgs e) => _ = SuggestLargeOldAsync();

    // Suggest.LargeOld reads the whole scanned tree and the Downloads folder is asked of the shell, so both run off the UI thread. The
    // list is replaced only when files were found; with none, the status line says so and the list stays as it was.
    private async Task SuggestLargeOldAsync()
    {
        if (!entitlement.IsPro || MoveBlocked || suggestBusy || tree is not { } scanned)
        {
            return;
        }

        var version = largeOldVersion; // a listing that runs now may still finish; it never replaces a suggestion that is shown
        suggestBusy = true;
        UpdateMoveControls();
        try
        {
            IReadOnlyList<SuggestedFile> files;
            largeOldRuns++; // a tree removal after a move waits for this pass (the tree is not safe to change while it is read)
            try
            {
                var context = await CurrentContextAsync();
                var now = DateTimeOffset.Now;
                string? downloads = null;
                files = await Task.Run(() =>
                {
                    downloads = DownloadsFolder.Read();
                    var found = Suggest.LargeOld(scanned, now, context, downloads);
                    HoldSuggestWalk(); // Debug only: --suggest-delay
                    return found;
                });

                // The protected folders may have changed in Settings while the walk ran: judge the files again against the list as of now,
                // until it stays the same, so that no file that is protected now is ticked.
                while (!ReferenceEquals(context.UserProtectedFolders, userProtectedFolders))
                {
                    context = await CurrentContextAsync();
                    var current = context;
                    files = await Task.Run(() => Suggest.LargeOld(scanned, now, current, downloads));
                }
            }
            finally
            {
                largeOldRuns--;
            }

            if (version != largeOldVersion || !ReferenceEquals(tree, scanned))
            {
                return; // a new scan, or the list was made again: this answer is for an old state
            }

            if (files.Count == 0)
            {
                StatusText.Text = Text.NothingToSuggest;
                return;
            }

            if (files.Count > RuleAsksAbove
                && !await ConfirmRuleAsync(Text.SuggestConfirm(files.Count, files.Count(f => IsProgramFile(f.Node))), Text.Suggest))
            {
                return; // Cancel: the list, the filters and the status line stay as they were
            }

            if (MoveBlocked || version != largeOldVersion || !ReferenceEquals(tree, scanned))
            {
                return;
            }

            ShowLargeOldSuggestion(files);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Text.ScanFailed(scanned.Root.Path, ex.Message);
        }
        finally
        {
            suggestBusy = false;
            UpdateMoveControls();
            RunDeferredListings();
        }
    }

    // Suggest's own list: every suggested file of the scan, ticked, with its reason. The size, age and Show system items filters are off
    // while it shows; Clear suggestion brings the filtered list back.
    private void ShowLargeOldSuggestion(IReadOnlyList<SuggestedFile> files)
    {
        largeOldSuggesting = true;
        SetLargeOldColumns(withWhy: true);
        LargeOldTable.Batch(() =>
        {
            foreach (var file in files)
            {
                var row = new LargeOldRow(file.Node, file.LastModified, Text.LastModified, file.Type, null);
                AddLargeOldRow(row, file.Why, ticked: true);
            }
        });
        LargeOldTable.Visibility = Visibility.Visible;
        LargeOldMessage.Text = string.Empty;
        SetLargeOldSuggestionControls(true);
        SetSuggestStatus(largeOldSuggestLine, Text.SuggestedFiles(files.Count, ByteUnits.Format(files.Sum(f => f.Node.AllocatedBytes))));
        UpdateMoveControls();
    }

    private void SetLargeOldSuggestionControls(bool suggesting)
    {
        MinimumSizeBox.IsEnabled = !suggesting;
        MinimumAgeBox.IsEnabled = !suggesting;
        SystemItemsBox.IsEnabled = !suggesting;
        ClearSuggestionButton.Visibility = suggesting ? Visibility.Visible : Visibility.Collapsed;
    }

    // The suggestion is over: the Why column, Clear suggestion, the disabled filters and Suggest's status line go and the table is empty (the caller
    // lists again).
    private void EndLargeOldSuggestion()
    {
        if (!largeOldSuggesting)
        {
            return;
        }

        largeOldSuggesting = false;
        EndSuggestStatus(largeOldSuggestLine);
        SetLargeOldColumns(withWhy: false);
        SetLargeOldSuggestionControls(false);
    }

    private void OnClearSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (!largeOldSuggesting || MoveBlocked || suggestBusy)
        {
            return;
        }

        EndLargeOldSuggestion();
        LargeOldTable.Clear();
        _ = RefreshLargeOldAsync(); // lists the filtered rows, nothing ticked
    }

    // ---- Duplicates ---------------------------------------------------------------------------------------------------

    private void OnSuggestDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (entitlement.IsPro)
        {
            _ = ApplyRuleAsync(KeepRule.NewestModified, null, suggest: true);
        }
    }

    // The reasons and the Why column go: every group forgets what Suggest ticked. The caller rebuilds the groups.
    private void ClearDuplicatesWhy()
    {
        duplicatesWhy = false;
        foreach (var model in duplicateModels)
        {
            model.ClearSuggested();
        }
    }
}
