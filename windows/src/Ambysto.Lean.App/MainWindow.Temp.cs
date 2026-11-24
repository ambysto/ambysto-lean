using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The Temporary files view (v1 additions, "UI contract for group 4"; docs/spec/03). The Core's <see cref="TempFinder"/>
/// decides which children of the temporary folders are old enough and which are display-only; this file lists them, with nothing
/// ticked, and starts a move only from <c>MoveTemp</c>, through <c>ConfirmMove</c> and the Trasher like every other view
/// (module <see cref="LeanModule.Temp"/>).</summary>
public sealed partial class MainWindow
{
    private static readonly TempAge[] TempAges = [TempAge.Week, TempAge.Month, TempAge.Quarter];

    private bool tempViewReady;
    private int tempVersion;

    // Counts the moves that ended (PruneTemp runs at the end of each): a read that started before one may hold rows it moved.
    private int tempMovesEnded;
    private CancellationTokenSource? tempCancel;
    private List<TempItem> tempRows = [];

    // Every visible string, from Copy.Text; the age starts at one week (docs/spec/03).
    private void InitializeTemp()
    {
        TempTitle.Text = Text.TemporaryFiles;
        AgeChoice.Header = Text.TempAgeLabel;
        AutomationProperties.SetName(AgeChoice, Text.TempAgeLabel);
        foreach (var label in new[] { Text.AgeWeek, Text.AgeMonth, Text.AgeQuarter })
        {
            AgeChoice.Items.Add(Choice(label));
        }

        AgeChoice.SelectedIndex = 0;
        SetTempColumns(withWhy: false);
        Closed += (_, _) => tempCancel?.Cancel();
        tempViewReady = true;
    }

    // The columns of the table; Suggest (ASP-17) adds a Why column (240 wide) at the end, and a new read takes it away again. The Note
    // column (a hint, on a few rows only) gives up 100 px to it, so that the whole Why column fits at the default window width.
    private void SetTempColumns(bool withWhy)
    {
        var columns = new List<TableColumn>
        {
            new(Text.ColumnMove, SelectAllColumn),
            new(Text.ColumnName),
            new(Text.ColumnSize, 90),
            new(Text.ColumnLastChanged, 110),
            new(Text.ColumnNote, withWhy ? 200 : 300),
        };
        if (withWhy)
        {
            columns.Add(new TableColumn(Text.ColumnWhy, 240));
        }

        TempTable.SetColumns(columns);
        TempTable.EnableSelectAll(Text.SelectAll, () => !SelectAllBlocked);
    }

    private void OnTemporaryFilesClick(object sender, RoutedEventArgs e)
    {
        ShowView(TempView);
        if (suggestBusy)
        {
            tempReadDeferred = true; // read when the Suggest walk is over, if it showed nothing
        }
        else
        {
            _ = RefreshTempAsync();
        }
    }

    private void OnTempAgeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (tempViewReady)
        {
            _ = RefreshTempAsync();
        }
    }

    // The rows for the chosen age. The finder walks the temporary folders, so it runs off the UI thread; the latest request wins.
    // Going back from the view does not cancel a move (a move is a separate run); a list that is still being read is simply replaced
    // the next time the view opens.
    private async Task RefreshTempAsync()
    {
        var version = ++tempVersion;
        var movesAtStart = tempMovesEnded;
        tempCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        tempCancel = cancel;
        var age = TempAges[Math.Max(0, AgeChoice.SelectedIndex)];

        tempRows = [];
        if (tempWhy is not null)
        {
            tempWhy = null; // a new read: the Why column goes (ASP-17), and Suggest's status line (ASP-22)
            EndSuggestStatus(tempSuggestLine);
            SetTempColumns(withWhy: false);
        }

        TempTable.Clear();
        TempTotal.Text = string.Empty;
        TempMessage.Text = Text.ReadingTempFolders;
        UpdateMoveControls();
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            var rows = await Task.Run(async () =>
            {
                var found = await TempFinder.FindAsync(context, age, TimeProvider.System, cancel.Token);
                HoldListing(); // Debug only: --listing-delay
                return found;
            }, cancel.Token);
            if (version != tempVersion)
            {
                return;
            }

            // A move ended after this read began: rows it moved are not shown (a path that is gone drops out).
            if (movesAtStart != tempMovesEnded)
            {
                var read = rows;
                rows = await Task.Run(() => read.Where(r => File.Exists(r.Node.Path) || Directory.Exists(r.Node.Path)).ToList(), cancel.Token);
                if (version != tempVersion)
                {
                    return;
                }
            }

            tempRows = [.. rows];
            TempTable.Batch(() =>
            {
                foreach (var row in tempRows)
                {
                    AddTempRow(row);
                }
            });

            TempMessage.Text = tempRows.Count == 0 ? Text.NoTempFiles : string.Empty;
            UpdateTempTotal();
            UpdateMoveControls();
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one, or the window closed.
        }
        catch (Exception)
        {
            // Whatever went wrong ends the "Reading…" state and says so; it never hangs.
            if (version == tempVersion)
            {
                TempMessage.Text = Text.TempReadFailed;
                UpdateMoveControls();
            }
        }
    }

    // One row: Move (none ticked), Name, Size, Last changed and Note. A display-only row has a disabled box and its hint in the Note
    // column. The system-wide row is never measured, so it names its path and its hint only. While Suggest's reasons are shown (tempWhy)
    // a suggested row is ticked and has its Why as a last cell, any other row an empty one.
    private void AddTempRow(TempItem row)
    {
        var node = row.Node;
        var measured = node.Id != 0;
        string? why = null;
        var suggested = tempWhy is not null && !row.DisplayOnly && tempWhy.TryGetValue(node.Path, out why);
        var (tickCell, box) = LeanTable.TickCell("TempItem", !row.DisplayOnly);
        box.IsChecked = suggested; // before the handlers hook, so the build ticks nothing one by one
        box.Checked += OnTempTickChanged;
        box.Unchecked += OnTempTickChanged;
        // While the reasons show, every text cell is one line, trimmed with an ellipsis, its full text its tooltip (ASP-14: a row is one line).
        var oneLine = tempWhy is not null;
        var cells = new List<TableCell>
        {
            tickCell,
            oneLine ? WithFullText(LeanTable.Label(measured ? node.Name : node.Path, toolTip: node.Path, noteBelow: false), node.Path)
                    : LeanTable.Label(measured ? node.Name : node.Path, toolTip: node.Path),
            measured ? LeanTable.SizeLabel(ByteUnits.Format(node.AllocatedBytes), node.AllocatedBytes) : LeanTable.Empty(),
            measured ? LeanTable.DateLabel(new DateTimeOffset(row.NewestWriteUtc, TimeSpan.Zero)) : LeanTable.Empty(),
            row.Hint is null ? LeanTable.Empty()
                : oneLine ? WithFullText(LeanTable.Label(row.Hint, toolTip: row.Hint, noteBelow: false), row.Hint) : LeanTable.Label(row.Hint),
        };
        if (oneLine)
        {
            cells.Add(suggested ? WithFullText(LeanTable.Label(why!, toolTip: why, noteBelow: false), why!) : LeanTable.Empty());
        }

        var table = TempTable.AddRow("TempRow", row, cells, tick: box, displayOnly: row.DisplayOnly);
        AutomationProperties.SetHelpText(table.Item, node.Path);
    }

    private void OnTempTickChanged(object sender, RoutedEventArgs e) => UpdateMoveControls();

    // An estimate over the rows that can be moved (a display-only row is never counted).
    private void UpdateTempTotal() =>
        TempTotal.Text = Text.TempEstimate(ByteUnits.Format(tempRows.Where(r => !r.DisplayOnly).Sum(r => r.Node.AllocatedBytes)));

    private List<TempItem> TickedTemp() =>
        TempTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<TempItem>().Where(t => !t.DisplayOnly).ToList();

    private void OnMoveTempClick(object sender, RoutedEventArgs e)
    {
        if (MoveBlocked)
        {
            return;
        }

        var items = TickedTemp().Select(r => new SessionItem(r.Node, null)).ToList();
        if (items.Count > 0)
        {
            _ = BeginMoveAsync(LeanModule.Temp, items, NoModuleItems);
        }
    }

    // A moved row leaves the list; the rest stay as the user left them.
    private void PruneTemp(HashSet<string> trashedPaths)
    {
        tempMovesEnded++;
        if (tempRows.Count == 0)
        {
            return;
        }

        tempRows = tempRows.Where(r => !IsAtOrBelowAny(r.Node.Path, trashedPaths)).ToList();
        TempTable.RemoveWhere(r => r.Tag is TempItem row && IsAtOrBelowAny(row.Node.Path, trashedPaths));

        if (tempRows.Count == 0)
        {
            TempMessage.Text = Text.NoTempFiles;
        }

        UpdateTempTotal();
    }
}
