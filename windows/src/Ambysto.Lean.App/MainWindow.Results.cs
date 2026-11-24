using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Ambysto.Lean.App;

/// <summary>The results view of Overview once a scan is done (ASP-14, intent/2026-10-18-asp-14-start-and-sidebar.md, "3. Scanned"): the
/// type column (All files and the seven groups of spec 01, each with its measured bytes, and the volume's free space), the map with
/// the tiles of other groups dimmed, and the 500 largest files of the chosen group with a tick box per row, a row's two action
/// buttons and Move to Recycle Bin (module Map). It lists and marks; a move starts only from a Move control, through ConfirmMove and
/// the Trasher (MainWindow.Moves.cs), and the Core's gate decides what may go.</summary>
public sealed partial class MainWindow
{
    private const double LargestRowHeight = 26;

    // The Move column of a table with a Select all box (ASP-15): the box and the column's label side by side.
    private const double SelectAllColumn = 88;

    private ResultsData? results;
    private int resultsVersion;
    private int chosenSlot;
    private bool typeGroupsBuilding;

    // Every visible string, from Copy.Text.
    private void InitializeResults()
    {
        MoveLargestButton.Content = Text.MoveToRecycleBin;
        LargestNote.Text = Text.LargestFilesNote;
        LargestTable.RowMinHeight = LargestRowHeight;
        LargestTable.SetColumns(
        [
            new TableColumn(Text.ColumnMove, SelectAllColumn),
            new TableColumn(Text.ColumnName),
            new TableColumn(Text.ColumnLocation, 200),
            new TableColumn(Text.ColumnSize, 90),
            new TableColumn(Text.ColumnType, 180),
        ]);
        LargestTable.EnableSelectAll(Text.SelectAll, () => !SelectAllBlocked);
    }

    // A new scan empties what the results view lists: its rows named the old tree.
    private void ResetResults()
    {
        resultsVersion++;
        results = null;
        LargestTable.Clear();
        typeGroupsBuilding = true;
        TypeGroups.Items.Clear();
        typeGroupsBuilding = false;
        chosenSlot = 0;
        FreeSpaceRow.Text = string.Empty;
        UpdateMoveControls();
    }

    // The tree of the last scan, worked out off the UI thread (the tree is not changed while it is read: a move waits for the pass).
    private async Task RefreshResultsAsync()
    {
        var version = ++resultsVersion;
        if (tree is not { } scanned)
        {
            return;
        }

        ResultsData data;
        try
        {
            largeOldRuns++;
            try
            {
                var context = await CurrentContextAsync();
                data = await Task.Run(() => ResultsBuilder.Build(scanned, context));
            }
            finally
            {
                largeOldRuns--;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            if (version == resultsVersion)
            {
                StatusText.Text = Text.ScanFailed(scanned.Root.Path, ex.Message);
            }

            return;
        }

        if (version != resultsVersion || !ReferenceEquals(tree, scanned))
        {
            return;
        }

        results = data;
        BuildTypeGroups(data);
        FillLargest();
    }

    // ---- the type column ------------------------------------------------------------------------------------------------

    private static readonly TypeGroupKind[] GroupOrder =
    [
        TypeGroupKind.Video, TypeGroupKind.DiskImage, TypeGroupKind.Archive, TypeGroupKind.Document,
        TypeGroupKind.Application, TypeGroupKind.VirtualMachineImage, TypeGroupKind.Other,
    ];

    // One row per group, All files first; the chosen one stays chosen across a rebuild (after a move).
    private void BuildTypeGroups(ResultsData data)
    {
        typeGroupsBuilding = true;
        try
        {
            TypeGroups.Items.Clear();
            TypeGroups.Items.Add(TypeGroupItem(0, null, Text.AllFiles, data.Bytes[0]));
            foreach (var kind in GroupOrder)
            {
                var slot = ResultsData.SlotOf(kind);
                TypeGroups.Items.Add(TypeGroupItem(slot, kind, TypeName(kind), data.Bytes[slot]));
            }

            TypeGroups.SelectedIndex = Math.Clamp(chosenSlot, 0, ResultsData.Slots - 1);
        }
        finally
        {
            typeGroupsBuilding = false;
        }

        UpdateFreeSpace();
    }

    private static ListViewItem TypeGroupItem(int slot, TypeGroupKind? kind, string name, long bytes)
    {
        var size = ByteUnits.Format(bytes);
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (kind is { } group)
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(BrandPalette.Fill(group)),
                BorderBrush = new SolidColorBrush(BrandPalette.SwatchBorder(group)),
                BorderThickness = new Thickness(1),
            };
            grid.Children.Add(swatch);
        }

        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var amount = new TextBlock { Text = size, Style = LeanStyles.Note, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(amount, 2);
        grid.Children.Add(amount);
        var item = new ListViewItem { Content = grid, Tag = slot, MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(item, "TypeGroup");
        AutomationProperties.SetName(item, Text.NameAndSize(name, size));
        return item;
    }

    // The volume's free space: text only.
    private void UpdateFreeSpace()
    {
        if (volumeFacts is { } facts)
        {
            var size = ByteUnits.Format(facts.FreeBytes);
            FreeSpaceRow.Text = Text.LabelValue(Text.FreeSpace, size);
            AutomationProperties.SetName(FreeSpaceRow, Text.NameAndSize(Text.FreeSpace, size));
        }
    }

    // Choosing a group lists its largest files and dims the other groups' tiles on the map; the layout does not change.
    private void OnTypeGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (typeGroupsBuilding || TypeGroups.SelectedItem is not ListViewItem { Tag: int slot })
        {
            return;
        }

        chosenSlot = slot;
        FillLargest();
        RerenderMap();
    }

    // The group the map keeps in colour; null: all of them.
    private TypeGroupKind? ChosenKind => chosenSlot > 0 ? ResultsData.KindOf(chosenSlot) : null;

    // ---- the largest files ----------------------------------------------------------------------------------------------

    private void FillLargest()
    {
        LargestTable.Clear();
        if (results is { } data)
        {
            LargestTable.Batch(() =>
            {
                foreach (var row in data.Rows[Math.Clamp(chosenSlot, 0, ResultsData.Slots - 1)])
                {
                    AddLargestRow(row);
                }
            });
        }

        UpdateMoveControls();
    }

    // One row: Move (none ticked; a display-only row is disabled, with its hint in its name and tooltip only: every row is one line high), Name, Location, Size, Type.
    private void AddLargestRow(LargestRow row)
    {
        var path = row.Node.Path;
        var folder = Path.GetDirectoryName(path) ?? path;
        var bytes = row.Node.AllocatedBytes;
        var size = ByteUnits.Format(bytes);
        var type = TypeName(row.Type);
        var help = row.Hint is null ? path : path + "\n" + row.Hint;
        var rowName = Text.RowName(Text.RowName(Text.RowName(Text.RowName(row.Node.Name, row.Hint), folder), size), type);
        var (tickCell, box) = LeanTable.TickCell("LargestSelect", row.Hint is null);
        box.MinHeight = 20; // the compact list: a check box's own 32 px would make every row taller than the 26 px it is given
        box.Checked += OnLargestTickChanged;
        box.Unchecked += OnLargestTickChanged;
        var table = LargestTable.AddDeferredRow(
            "LargestRow",
            rowName,
            row,
            box,
            [
                () => box.IsChecked == true ? 0 : 1,
                () => row.Node.Name,
                () => folder,
                () => bytes,
                () => type,
            ],
            () =>
            [
                tickCell,
                LeanTable.Label(row.Node.Name, note: row.Hint, toolTip: help, noteBelow: false),
                LeanTable.FolderLabel(folder),
                LeanTable.SizeLabel(size, bytes),
                LeanTable.Label(type),
            ],
            displayOnly: row.Hint is not null,
            actions: RowActionButtons);
        AutomationProperties.SetHelpText(table.Item, help);
        if (row.Hint is not null)
        {
            // On the row itself, so it opens when the row gets keyboard focus (arrow keys, Tab), not only under the pointer.
            ToolTipService.SetToolTip(table.Item, help);
        }

        table.Item.ContextRequested += OnLargestRowContextRequested;
        table.AttachRetire(() =>
        {
            box.Checked -= OnLargestTickChanged;
            box.Unchecked -= OnLargestTickChanged;
            table.Item.ContextRequested -= OnLargestRowContextRequested;
        });
    }

    private void OnLargestTickChanged(object sender, RoutedEventArgs e) => UpdateMoveControls();

    // The ticked rows of the list that may be moved (a display-only row's box is disabled and never counts).
    private List<LargestRow> TickedLargest() =>
        LargestTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<LargestRow>().Where(r => r.Hint is null).ToList();

    private void OnMoveLargestClick(object sender, RoutedEventArgs e)
    {
        if (!MoveBlocked)
        {
            StartLargestMove();
        }
    }

    private void StartLargestMove()
    {
        var items = TickedLargest().Select(r => new SessionItem(r.Node, null)).ToList();
        if (items.Count > 0)
        {
            _ = BeginMoveAsync(LeanModule.Map, items, NoModuleItems);
        }
    }

    // The same menu as a map tile's: Show in File Explorer, and Move to Recycle Bin. Opening it changes no tick; the row rule applies
    // when Move is chosen (OnMoveMenuClick).
    private void OnLargestRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListViewItem { Tag: TableRow { Tag: LargestRow row } tableRow } item)
        {
            return;
        }

        menuPath = row.Node.Path;
        menuNode = row.Node;
        menuSource = MenuSource.LargestRow;
        revealItem.IsEnabled = true;
        menuTableRow = tableRow;
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

    // ---- row actions (Large and old, and the largest files) ----------------------------------------------------------

    private RowActions RowActionButtons => new(CreateRowActionButtons, RefreshRowActionButtons);

    // Show in File Explorer opens the folder with the file selected; Move to Recycle Bin… opens ConfirmMove for exactly this row. Real
    // buttons with automation names and tooltips, reached with Tab from inside the row.
    private IReadOnlyList<Button> CreateRowActionButtons(TableRow row)
    {
        var reveal = RowButton("\uE721", Text.ShowInFileExplorer, "RevealRow"); // Segoe Fluent Icons: Search (the magnifier, ASP-15)
        reveal.Click += (_, _) => _ = RevealPathAsync(RowNode(row)?.Path);
        var move = RowButton("", Text.MoveToRecycleBin, "MoveRow"); // Delete
        move.Click += (_, _) => StartRowMove(row);
        return [reveal, move];
    }

    private static Button RowButton(string glyph, string label, string automationId)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            IsTabStop = true,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    // Move is off on a display-only row (its hint is the tooltip) and while a move or a scan runs.
    private void RefreshRowActionButtons(TableRow row, IReadOnlyList<Button> buttons)
    {
        var hint = RowHint(row);
        var move = buttons[1];
        move.IsEnabled = !MoveBlocked && hint is null;
        ToolTipService.SetToolTip(move, hint ?? Text.MoveToRecycleBin);
    }

    private void RefreshAllRowActionButtons()
    {
        foreach (var row in LargestTable.Rows.Concat(LargeOldTable.Rows))
        {
            if (row.ActionButtons is { } buttons)
            {
                RefreshRowActionButtons(row, buttons);
            }
        }
    }

    private static ScanNode? RowNode(TableRow row) => row.Tag switch
    {
        LargestRow largest => largest.Node,
        LargeOldRow largeOld => largeOld.Node,
        DuplicateCopy copy => copy.Node,
        _ => null,
    };

    private static string? RowHint(TableRow row) => row.Tag switch
    {
        LargestRow largest => largest.Hint,
        LargeOldRow largeOld => largeOld.Hint,
        _ => null,
    };

    // The row's own Move: that row alone, the ticks as they are, and always through the confirmation.
    private void StartRowMove(TableRow row)
    {
        if (MoveBlocked || RowHint(row) is not null || RowNode(row) is not { } node)
        {
            return;
        }

        var module = row.Tag is LargestRow ? LeanModule.Map : LeanModule.LargeOld;
        _ = BeginMoveAsync(module, [new SessionItem(node, null)], NoModuleItems);
    }

    private async Task RevealPathAsync(string? path)
    {
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

    // ---- the map with the other groups dimmed ------------------------------------------------------------------------

    // A tile of another group than the chosen one is drawn in the panel colour with the ink label, as a folder drawn with its children
    // inside is; its model stays as it was, so nothing else about the tile changes.
    private TileModel DrawnAs(TileModel model)
    {
        var chosen = ChosenKind;
        if (chosen is { } kind && model is { Kind: TileKind.Item, Colour: not TypeGroupKind.OpenFolder } && model.Colour != kind)
        {
            return model with { Colour = TypeGroupKind.OpenFolder };
        }

        return model;
    }
}
