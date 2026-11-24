using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ambysto.Lean.Core.Copy;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ambysto.Lean.App;

/// <summary>One column of a <see cref="LeanTable"/>: its header, and its width in pixels (0 = takes the room that is left). A column
/// with an empty header (a row's button) has no header button and is not sorted. Columns that take the room that is left share it by
/// <paramref name="Weight"/>, none narrower than <paramref name="MinWidth"/> (ASP-22: Suggest's Why column is the one that gives way).</summary>
public sealed record TableColumn(string Header, double Width = 0, double Weight = 1, double MinWidth = 120);

/// <summary>One cell: the element that shows it, the value its column sorts by (null: the cell does not sort), and the text the row's
/// automation name takes from it when that differs from the element's own name (a shortened folder: the row holds it whole).</summary>
public sealed record TableCell(UIElement Element, Func<IComparable?>? Key = null, string? RowText = null);

/// <summary>The icon buttons at the right end of a row (ASP-14, row actions): shown while the pointer is over the row or the row or
/// anything in it has keyboard focus, and built the first time that happens. <c>Create</c> makes them (real buttons, with automation
/// names and tooltips); <c>Refresh</c> sets their state each time the row shows them and whenever the view says its state changed.</summary>
public sealed record RowActions(Func<TableRow, IReadOnlyList<Button>> Create, Action<TableRow, IReadOnlyList<Button>> Refresh);

/// <summary>One row of a <see cref="LeanTable"/>: the list item, the data it stands for, and its tick box or Keep radio, if any.</summary>
public sealed class TableRow
{
    private readonly IReadOnlyList<Func<IComparable?>?>? sortKeys;
    private Action? retire;

    internal TableRow(
        ListViewItem item,
        string name,
        object? tag,
        CheckBox? tick,
        RadioButton? keep,
        IReadOnlyList<TableCell> cells,
        IReadOnlyList<Func<IComparable?>?>? sortKeys = null)
    {
        Item = item;
        Name = name;
        Tag = tag;
        Tick = tick;
        Keep = keep;
        Cells = cells;
        this.sortKeys = sortKeys;
    }

    /// <summary>The list item that holds the row.</summary>
    public ListViewItem Item { get; }

    /// <summary>The row's automation name: its cells joined, or the name the view gave the row.</summary>
    public string Name { get; }

    /// <summary>What the row stands for (a Core row, an offer, a journal entry).</summary>
    public object? Tag { get; }

    /// <summary>The Move tick box, when the row has one.</summary>
    public CheckBox? Tick { get; }

    /// <summary>The row's action buttons once they have been built (the row was hovered or focused); null before.</summary>
    public IReadOnlyList<Button>? ActionButtons { get; internal set; }

    /// <summary>The Keep radio, when the row has one.</summary>
    public RadioButton? Keep { get; }

    internal IReadOnlyList<TableCell> Cells { get; private set; }

    /// <summary>True when the row's Move box is ticked.</summary>
    public bool IsTicked => Tick?.IsChecked == true;

    internal IComparable? SortKey(int column) =>
        sortKeys is not null ? sortKeys[column]?.Invoke() : Cells[column].Key?.Invoke();

    internal void SetCells(IReadOnlyList<TableCell> cells) => Cells = cells;

    internal void AttachRetire(Action callback) => retire += callback;

    internal void Retire()
    {
        var callback = retire;
        retire = null;
        callback?.Invoke();
    }
}

/// <summary>The one table control of the lists (ASP-10): a header row of sort buttons over a <see cref="ListView"/> whose rows share the
/// header's columns. A header press sorts by that column, a second press reverses; the default order is the order the rows were
/// added in. Space on a focused row toggles its Move box, and Enter selects its Keep radio. It decides nothing about what may be
/// moved: the views hand it rows with the boxes already enabled or disabled. Look and feel are all in this class, so a theme
/// (ASP-12) restyles every list here.</summary>
public sealed partial class LeanTable : Grid
{
    /// <summary>Characters a folder cell shows before the path is shortened in the middle; the full path is its tooltip.</summary>
    public const int FolderChars = 40;

    private const double CellSpacing = 8;
    private static readonly Thickness RowPadding = new(8, 0, 8, 0);

    private readonly Grid header = new() { ColumnSpacing = CellSpacing, Padding = RowPadding };
    private readonly ListView list = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        IsItemClickEnabled = false,
        TabNavigation = KeyboardNavigationMode.Local, // a list is one tab stop by default (Once); here Tab walks every row's controls
        TabFocusNavigation = KeyboardNavigationMode.Local,
    };

    private readonly List<TableRow> rows = [];
    private readonly List<(Button Button, TextBlock Arrow)> headerButtons = [];
    private IReadOnlyList<TableColumn> columns = [];
    private int sortColumn = -1;
    private bool descending;
    private CheckBox? selectAll;
    private Func<bool>? selectAllAllowed;
    private bool selectAllSyncing;
    private bool bulkTicking;
    private int batching;
    private bool showing;

    /// <summary>ASP-22: true (the default) when the columns alternate (a band in the normal theme, a divider in High Contrast). A list that has
    /// its own alternating grounds (the Duplicates groups, ASP-15: exactly LeanPanel or LeanGround) turns it off. Set before
    /// <see cref="SetColumns"/>.</summary>
    public bool ColumnBands { get; set; } = true;

    /// <summary>The least height of a row; the largest-files list is compact so that eight rows fit under the map (ASP-14).</summary>
    public double RowMinHeight { get; set; } = 36;

    /// <summary>A table that scrolls inside the room it is given.</summary>
    public LeanTable()
        : this(false)
    {
    }

    /// <summary>A table; <paramref name="embedded"/> when it sits inside another list's row and shows all its rows without scrolling.</summary>
    public LeanTable(bool embedded)
    {
        header.Style = LeanStyles.TableHeader;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetRow(list, 1);
        list.SelectionChanged += (_, _) =>
        {
            if (!showing)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        Children.Add(header);
        Children.Add(list);
        if (embedded)
        {
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        }
    }

    /// <summary>The AutomationId of the list under the header.</summary>
    public string ListId
    {
        get => AutomationProperties.GetAutomationId(list);
        set => AutomationProperties.SetAutomationId(list, value);
    }

    /// <summary>ASP-16: true when a click or the arrow keys select one row (the system's selected-row look, the rows keep the
    /// SelectionItemPattern). Off by default: every other table has no selection, only its tick boxes. The Uninstall apps table opts in.</summary>
    public bool SingleSelection
    {
        get => list.SelectionMode == ListViewSelectionMode.Single;
        set => list.SelectionMode = value ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
    }

    /// <summary>Raised when the selected row changed (a user's choice, or the selected row being removed). Not raised while the table
    /// sorts or fills itself and the same row stays selected.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The selected row, or null (a table without <see cref="SingleSelection"/> never has one).</summary>
    public TableRow? SelectedRow => (list.SelectedItem as ListViewItem)?.Tag as TableRow;

    /// <summary>Selects <paramref name="row"/> (or none).</summary>
    public void Select(TableRow? row) => list.SelectedItem = row?.Item;

    /// <summary>Adds many rows and shows them once, in the sort order: <paramref name="fill"/> calls <see cref="AddRow"/>.</summary>
    public void Batch(Action fill)
    {
        batching++;
        try
        {
            fill();
        }
        finally
        {
            batching--;
        }

        if (batching == 0)
        {
            Show();
        }
    }

    /// <summary>The rows, in the order they were added in.</summary>
    public IReadOnlyList<TableRow> Rows => rows;

    /// <summary>The number of rows.</summary>
    public int Count => rows.Count;

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    /// <summary>Sets the columns (once, before rows are added) and builds the header.</summary>
    public void SetColumns(IReadOnlyList<TableColumn> newColumns)
    {
        columns = newColumns;
        header.Children.Clear();
        header.ColumnDefinitions.Clear();
        headerButtons.Clear();
        foreach (var row in rows)
        {
            row.Retire();
        }

        rows.Clear();
        list.Items.Clear();
        sortColumn = -1;
        descending = false;
        for (var i = 0; i < columns.Count; i++)
        {
            header.ColumnDefinitions.Add(ColumnOf(columns[i]));
            if (ColumnBands)
            {
                header.Children.Add(ColumnBand(i));
            }

            if (columns[i].Header.Length == 0)
            {
                continue;
            }

            var index = i;
            var label = new TextBlock { Text = columns[i].Header, Style = LeanStyles.Note, TextTrimming = TextTrimming.CharacterEllipsis };
            var arrow = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(label);
            content.Children.Add(arrow);
            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 4, 0, 4),
                BorderThickness = new Thickness(0),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LeanClearBrush"],
            };
            AutomationProperties.SetAutomationId(button, "ColumnHeader");
            AutomationProperties.SetName(button, columns[i].Header);
            button.Click += (_, _) => SortBy(index);
            SetColumn(button, i);
            header.Children.Add(button);
            headerButtons.Add((button, arrow));
        }
    }

    // ASP-22: behind the cells of column i, in the header and in every row, so the columns tell apart: a band on every other column in the
    // normal theme, a 1px divider before every column but the first in High Contrast (Theme.xaml, LeanColumnBand). It is decoration.
    private static Border ColumnBand(int column)
    {
        var band = new Border { Style = LeanStyles.ColumnBand(column) };
        if (column == 0)
        {
            band.BorderThickness = new Thickness(0);
        }

        AutomationProperties.SetAccessibilityView(band, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SetColumn(band, column);
        return band;
    }

    private static ColumnDefinition ColumnOf(TableColumn column) => column.Width > 0
        ? new ColumnDefinition { Width = new GridLength(column.Width) }
        : new ColumnDefinition { Width = new GridLength(column.Weight, GridUnitType.Star), MinWidth = column.MinWidth };

    private Grid BuildRowGrid(IReadOnlyList<TableCell> cells, bool displayOnly)
    {
        if (cells.Count != columns.Count)
        {
            throw new ArgumentException("A row has one cell per column.", nameof(cells));
        }

        var grid = new Grid { ColumnSpacing = CellSpacing, Style = LeanStyles.TableRow };
        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(ColumnOf(columns[i]));
            if (ColumnBands)
            {
                grid.Children.Add(ColumnBand(i));
            }

            if (cells[i].Element is FrameworkElement element)
            {
                element.VerticalAlignment = VerticalAlignment.Center;
                if (element.Parent is Panel oldParent)
                {
                    oldParent.Children.Remove(element);
                }
            }

            SetColumn((FrameworkElement)cells[i].Element, i);
            grid.Children.Add(cells[i].Element);
            if (displayOnly)
            {
                MuteText(cells[i].Element);
            }
        }

        return grid;
    }

    /// <summary>Adds a row; <paramref name="cells"/> match the columns one to one. The tick box and the radio are placed by the caller
    /// (as cells) and named here by the row's name. The row's automation name is its cells joined unless the view gives
    /// <paramref name="name"/> (the Uninstall table: the display name, or the name with its class note).</summary>
    public TableRow AddRow(string automationId, object? tag, IReadOnlyList<TableCell> cells, CheckBox? tick = null, RadioButton? keep = null,
        bool displayOnly = false, RowActions? actions = null, string? name = null)
    {
        if (cells.Count != columns.Count)
        {
            throw new ArgumentException("A row has one cell per column.", nameof(cells));
        }

        name ??= cells.Select(c => c.RowText ?? AutomationProperties.GetName(c.Element)).Aggregate("", Text.RowName);
        var grid = BuildRowGrid(cells, displayOnly);
        var host = new Grid();
        host.Children.Add(grid);
        var item = new ListViewItem
        {
            Content = host,
            Padding = RowPadding,
            MinHeight = RowMinHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, name);
        if (tick is not null)
        {
            AutomationProperties.SetName(tick, name);
            tick.Checked += OnRowTickChanged;
            tick.Unchecked += OnRowTickChanged;
        }

        if (keep is not null)
        {
            AutomationProperties.SetName(keep, name);
        }

        item.TabNavigation = KeyboardNavigationMode.Local; // Tab walks the controls of each row, not only the row
        item.TabFocusNavigation = KeyboardNavigationMode.Local;
        item.PreviewKeyDown += OnRowKeyDown;
        var row = new TableRow(item, name, tag, tick, keep, cells);
        item.Tag = row;
        if (actions is not null)
        {
            var detachActions = AttachActions(row, host, actions);
            row.AttachRetire(detachActions);
        }

        rows.Add(row);
        if (batching == 0)
        {
            Show();
        }

        return row;
    }

    internal TableRow AddDeferredRow(
        string automationId,
        string name,
        object? tag,
        CheckBox tick,
        IReadOnlyList<Func<IComparable?>?> sortKeys,
        Func<IReadOnlyList<TableCell>> createCells,
        bool displayOnly = false,
        RowActions? actions = null)
    {
        if (sortKeys.Count != columns.Count)
        {
            throw new ArgumentException("A row has one sort key per column.", nameof(sortKeys));
        }

        var item = new ListViewItem
        {
            Padding = RowPadding,
            MinHeight = RowMinHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            TabNavigation = KeyboardNavigationMode.Local,
            TabFocusNavigation = KeyboardNavigationMode.Local,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetName(tick, name);
        tick.Checked += OnRowTickChanged;
        tick.Unchecked += OnRowTickChanged;
        var row = new TableRow(item, name, tag, tick, null, [], sortKeys);
        item.Tag = row;

        Grid? host = null;
        Grid? inner = null;
        Action? detachActions = null;
        var retired = false;

        void Realize()
        {
            if (retired || host is not null)
            {
                return;
            }

            var cells = createCells();
            inner = BuildRowGrid(cells, displayOnly);
            host = new Grid();
            host.Children.Add(inner);
            row.SetCells(cells);
            item.PreviewKeyDown += OnRowKeyDown;
            if (actions is not null)
            {
                detachActions = AttachActions(row, host, actions);
            }

            item.Content = host;
        }

        void Unrealize(bool force)
        {
            if (host is null || (!force && (item.IsLoaded || FocusIsWithin(item))))
            {
                return;
            }

            detachActions?.Invoke();
            detachActions = null;
            item.PreviewKeyDown -= OnRowKeyDown;
            foreach (var cell in row.Cells)
            {
                if (!ReferenceEquals(cell.Element, tick))
                {
                    ToolTipService.SetToolTip(cell.Element, null);
                }
            }

            inner?.Children.Clear();
            inner = null;
            host.Children.Clear();
            host = null;
            item.Content = null;
            row.SetCells([]);
        }

        void OnLoaded(object sender, RoutedEventArgs e) => Realize();
        void OnUnloaded(object sender, RoutedEventArgs e) => Unrealize(force: false);

        item.Loaded += OnLoaded;
        item.Unloaded += OnUnloaded;
        row.AttachRetire(() =>
        {
            retired = true;
            item.Loaded -= OnLoaded;
            item.Unloaded -= OnUnloaded;
            Unrealize(force: true);
            tick.Checked -= OnRowTickChanged;
            tick.Unchecked -= OnRowTickChanged;
            AutomationProperties.SetName(tick, string.Empty);
            AutomationProperties.SetName(item, string.Empty);
            AutomationProperties.SetHelpText(item, string.Empty);
            ToolTipService.SetToolTip(item, null);
            item.Tag = null;
        });

        rows.Add(row);
        if (batching == 0)
        {
            Show();
        }

        return row;
    }

    // The row's icon buttons sit over the right end of the row in a panel of their own, so they take no column space. They are built the
    // first time the pointer enters or the focus lands in the row, and shown while either holds. Focus moving from the row to one of its
    // own buttons loses and gains in that order, so a lost focus is judged after it has settled.
    private static Action AttachActions(TableRow row, Grid host, RowActions actions)
    {
        var item = row.Item;
        var hovered = false;
        var retired = false;
        StackPanel? overlay = null;

        void Update()
        {
            if (retired)
            {
                return;
            }

            var show = hovered || FocusIsWithin(item);
            if (!show)
            {
                if (overlay is not null)
                {
                    overlay.Visibility = Visibility.Collapsed;
                }

                return;
            }

            if (overlay is null)
            {
                var buttons = actions.Create(row);
                row.ActionButtons = buttons;
                overlay = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(4, 0, 0, 0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(BrandPalette.RowActionsGround),
                };
                foreach (var button in buttons)
                {
                    overlay.Children.Add(button);
                }

                host.Children.Add(overlay);
            }

            actions.Refresh(row, row.ActionButtons!);
            overlay.Visibility = Visibility.Visible;
        }

        // Handled-too: the list item's own pointer handling must not hide the pointer from this. A move inside the row counts as over it,
        // so a pointer that came to rest before the handlers ran is still seen.
        void Over(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!hovered)
            {
                hovered = true;
                Update();
            }
        }

        void Exited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            hovered = false;
            Update();
        }

        void GotFocus(object sender, RoutedEventArgs e) => Update();
        void LostFocus(object sender, RoutedEventArgs e) => item.DispatcherQueue.TryEnqueue(Update);

        var overHandler = new Microsoft.UI.Xaml.Input.PointerEventHandler(Over);
        var exitHandler = new Microsoft.UI.Xaml.Input.PointerEventHandler(Exited);
        item.AddHandler(UIElement.PointerEnteredEvent, overHandler, true);
        item.AddHandler(UIElement.PointerMovedEvent, overHandler, true);
        item.AddHandler(UIElement.PointerExitedEvent, exitHandler, true);
        item.GotFocus += GotFocus;
        item.LostFocus += LostFocus;
        if (FocusIsWithin(item))
        {
            Update();
        }

        return () =>
        {
            retired = true;
            hovered = false;
            item.RemoveHandler(UIElement.PointerEnteredEvent, overHandler);
            item.RemoveHandler(UIElement.PointerMovedEvent, overHandler);
            item.RemoveHandler(UIElement.PointerExitedEvent, exitHandler);
            item.GotFocus -= GotFocus;
            item.LostFocus -= LostFocus;
            if (overlay is not null)
            {
                overlay.Children.Clear();
                host.Children.Remove(overlay);
                overlay = null;
            }

            row.ActionButtons = null;
        };
    }

    // True when the element that has the keyboard focus is the item or lies inside it.
    private static bool FocusIsWithin(ListViewItem item)
    {
        if (item.XamlRoot is null || FocusManager.GetFocusedElement(item.XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (var current = focused; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, item))
            {
                return true;
            }
        }

        return false;
    }

    // A display-only row reads in the mute colour (ASP-12): the cell's own text, not its note, which is mute already.
    private static void MuteText(UIElement element)
    {
        switch (element)
        {
            case TextBlock { Style: null } text:
                text.Style = LeanStyles.MuteText;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    MuteText(child);
                }

                break;
        }
    }

    /// <summary>Removes the rows <paramref name="predicate"/> picks.</summary>
    public void RemoveWhere(Func<TableRow, bool> predicate)
    {
        var removed = false;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (predicate(rows[i]))
            {
                rows[i].Retire();
                rows.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            Show();
        }
    }

    /// <summary>Removes every row (the sort stays).</summary>
    public void Clear()
    {
        foreach (var row in rows)
        {
            row.Retire();
        }

        rows.Clear();
        list.Items.Clear();
        RefreshSelectAll();
    }

    // Space on the row itself toggles its Move box (on the box it toggles it natively); Enter on a Keep row selects the radio.
    private static void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListViewItem { Tag: TableRow row } item)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Space && ReferenceEquals(e.OriginalSource, item) && row.Tick is { IsEnabled: true } tick)
        {
            tick.IsChecked = tick.IsChecked != true;
            e.Handled = true;
        }
        // Enter acts on the row itself or on its Keep radio only: on a tick box, a button or a link inside the row it is that control's own.
        else if (e.Key == Windows.System.VirtualKey.Enter && row.Keep is { IsEnabled: true } keep
                 && (ReferenceEquals(e.OriginalSource, item) || ReferenceEquals(e.OriginalSource, keep)))
        {
            if (keep.IsChecked != true)
            {
                keep.IsChecked = true;
            }

            e.Handled = true;
        }
    }

    // A header press: sort by the column, a second press on it reverses.
    private void SortBy(int column)
    {
        descending = sortColumn == column && !descending;
        sortColumn = column;
        Show();
    }

    // The list shows the rows in the sort order; the header marks the sorted column.
    private void Show()
    {
        IEnumerable<TableRow> ordered = rows;
        if (sortColumn >= 0)
        {
            var column = sortColumn;
            // A cell with no value (a null key: ASP-16, an empty Publisher or Size) sorts after every real value in both directions,
            // in the order the rows were added in.
            var keyed = rows.Select(r => (Row: r, Key: r.SortKey(column))).ToList();
            var present = keyed.Where(k => k.Key is not null);
            var sorted = descending ? present.OrderByDescending(k => k.Key, CellComparer.Instance) : present.OrderBy(k => k.Key, CellComparer.Instance);
            ordered = sorted.Concat(keyed.Where(k => k.Key is null)).Select(k => k.Row).ToList();
        }

        // The selected row stays selected through a sort or a refill; the table speaks up only when the selection really changed.
        var selected = SelectedRow;
        showing = true;
        try
        {
            list.Items.Clear();
            foreach (var row in ordered)
            {
                list.Items.Add(row.Item);
            }

            if (selected is not null && rows.Contains(selected))
            {
                list.SelectedItem = selected.Item;
            }
        }
        finally
        {
            showing = false;
        }

        if (!ReferenceEquals(selected, SelectedRow))
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        var shownHeaders = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Header.Length == 0)
            {
                continue;
            }

            var (button, arrow) = headerButtons[shownHeaders++];
            var sorted = i == sortColumn;
            arrow.Text = descending ? "" : ""; // Segoe Fluent Icons: ChevronDown / ChevronUp
            arrow.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
            arrow.Visibility = sorted ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, !sorted ? string.Empty : descending ? Text.SortedDescending : Text.SortedAscending);
        }

        RefreshSelectAll();
    }

    // ---- select all (ASP-15, owner 2026-10-19) ------------------------------------------------------------------------

    /// <summary>Puts a three-state tick box in the header of the first column (the Move column). It ticks every row of the list as it
    /// is shown whose box is enabled (a display-only row's never is), and unticks them all when every such row is ticked; it reads
    /// ticked, unticked or partly ticked as the rows change. It is a user action like a tick: nothing is ticked before it is used.
    /// It is off on a list with no row that can be moved and whenever <paramref name="allowed"/> says no (a scan or a move runs): the
    /// view calls <see cref="RefreshSelectAll"/> when that changes.</summary>
    public void EnableSelectAll(string name, Func<bool> allowed)
    {
        selectAllAllowed = allowed;
        var box = new CheckBox { MinWidth = 0, IsThreeState = true, IsChecked = false, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(box, "SelectAll");
        AutomationProperties.SetName(box, name);
        ToolTipService.SetToolTip(box, name);
        box.Checked += OnSelectAllChanged;
        box.Unchecked += OnSelectAllChanged;
        box.Indeterminate += OnSelectAllChanged;
        selectAll = box;

        // The box takes the left of the column and the column's sort button (its label) the rest, so the box sits over the rows' boxes.
        var holder = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        holder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        holder.Children.Add(box);
        if (columns.Count > 0 && columns[0].Header.Length > 0 && headerButtons.Count > 0)
        {
            var button = headerButtons[0].Button;
            header.Children.Remove(button);
            SetColumn(button, 1);
            holder.Children.Add(button);
        }

        SetColumn(holder, 0);
        header.Children.Add(holder);
        RefreshSelectAll();
    }

    /// <summary>Sets the Select all box from the rows: its state (ticked, unticked, partly ticked) and whether it is on.</summary>
    public void RefreshSelectAll()
    {
        if (selectAll is null)
        {
            return;
        }

        var movable = rows.Count(r => r.Tick is { IsEnabled: true });
        var ticked = rows.Count(r => r.Tick is { IsEnabled: true, IsChecked: true });
        selectAllSyncing = true;
        try
        {
            selectAll.IsChecked = ticked == 0 ? false : ticked == movable ? true : null;
            selectAll.IsEnabled = movable > 0 && (selectAllAllowed?.Invoke() ?? true);
        }
        finally
        {
            selectAllSyncing = false;
        }
    }

    private void OnRowTickChanged(object sender, RoutedEventArgs e)
    {
        if (!bulkTicking)
        {
            RefreshSelectAll();
        }
    }

    // The user used the box: every movable row is ticked, or, when every one already was, unticked.
    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (selectAllSyncing)
        {
            return;
        }

        var movable = rows.Where(r => r.Tick is { IsEnabled: true }).ToList();
        var tickAll = movable.Any(r => r.Tick!.IsChecked != true);
        bulkTicking = true;
        try
        {
            foreach (var row in movable)
            {
                row.Tick!.IsChecked = tickAll;
            }
        }
        finally
        {
            bulkTicking = false;
        }

        RefreshSelectAll();
    }

    // ---- cells --------------------------------------------------------------------------------------------------------

    /// <summary>ASP-16: the name cell of the apps table, one line: the name trimmed with an ellipsis, its full text its tooltip. The class
    /// note is not in the row (owner decision 2026-10-20): it is in the row's tooltip and automation name, and under the table.</summary>
    public static TableCell NameLabel(string text) => ValueLabel(text, text);

    /// <summary>ASP-16: a one-line value cell (a size, a date): the text trimmed with an ellipsis, its full text its tooltip, sorting
    /// by <paramref name="key"/>.</summary>
    public static TableCell ValueLabel(string text, IComparable key)
    {
        var element = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetName(element, text);
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetHelpText(element, text);
        return new TableCell(element, () => key);
    }

    /// <summary>ASP-16: a one-line text cell that sorts by its text, or an empty cell (<see cref="Blank"/>) when there is no text.</summary>
    public static TableCell TextValue(string? text) => string.IsNullOrWhiteSpace(text) ? Blank() : ValueLabel(text, text);

    /// <summary>ASP-16: a date cell in the short date of the current culture; it sorts by the date.</summary>
    public static TableCell DateValue(DateOnly date) => ValueLabel(date.ToString("d", CultureInfo.CurrentCulture), date);

    /// <summary>ASP-16: an empty cell for a value that is missing. Unlike <see cref="Empty"/> it has no sort key, so it sorts after every
    /// real value of its column in both directions.</summary>
    public static TableCell Blank()
    {
        var element = new TextBlock();
        AutomationProperties.SetName(element, string.Empty);
        return new TableCell(element, () => null);
    }

    /// <summary>A text cell; <paramref name="note"/> is a second, quieter line (a hint). Its automation name is the text and the note.
    /// With <paramref name="noteBelow"/> false the note is not drawn: the cell is one line, and the note is only in its automation name
    /// (and the tooltip the caller gives), and the text does not wrap (it is shortened with an ellipsis), as in a list whose rows are all one line high.</summary>
    public static TableCell Label(string text, IComparable? key = null, string? note = null, string? toolTip = null, bool noteBelow = true)
    {
        UIElement element;
        var name = Text.RowName(text, note);
        if (!noteBelow)
        {
            element = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        else if (note is null)
        {
            element = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        }
        else
        {
            var lines = new StackPanel();
            lines.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            lines.Children.Add(new TextBlock { Text = note, Style = LeanStyles.Note, TextWrapping = TextWrapping.Wrap });
            element = lines;
        }

        AutomationProperties.SetName(element, name);
        if (toolTip is not null)
        {
            ToolTipService.SetToolTip(element, toolTip);
        }

        return new TableCell(element, () => key ?? text);
    }

    /// <summary>A folder cell: the path shortened in the middle, the full path as its tooltip and in the row's name.</summary>
    public static TableCell FolderLabel(string path, int chars = FolderChars)
    {
        var element = new TextBlock { Text = Text.MiddleShortened(path, chars), TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetHelpText(element, path);
        ToolTipService.SetToolTip(element, path);
        return new TableCell(element, () => path, path);
    }

    /// <summary>A size cell, "1.2 GB"; it sorts by the bytes.</summary>
    public static TableCell SizeLabel(string size, long bytes) => Label(size, bytes);

    /// <summary>A date cell in the short date of the current culture; it sorts by the date.</summary>
    public static TableCell DateLabel(DateTimeOffset date) =>
        Label(date.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), date);

    /// <summary>The cell of a row's Move box: unticked, enabled when <paramref name="enabled"/>. Its name is set by the row.</summary>
    public static (TableCell Cell, CheckBox Box) TickCell(string automationId, bool enabled)
    {
        var box = new CheckBox { MinWidth = 0, IsChecked = false, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(box, automationId);
        return (new TableCell(box, () => box.IsChecked == true ? 0 : 1), box); // ascending: ticked first
    }

    /// <summary>The cell of a group's Keep radio. It has no radio group of its own: a group's radios are each a tab stop (Tab reaches
    /// every one, not only the chosen one), and the view keeps exactly one of them chosen.</summary>
    public static (TableCell Cell, RadioButton Radio) KeepCell(string automationId, bool isChecked)
    {
        var radio = new RadioButton { MinWidth = 0, IsChecked = isChecked };
        AutomationProperties.SetAutomationId(radio, automationId);
        return (new TableCell(radio, () => radio.IsChecked == true ? 0 : 1), radio); // ascending: the kept copy first
    }

    /// <summary>An empty cell.</summary>
    public static TableCell Empty()
    {
        var element = new TextBlock();
        AutomationProperties.SetName(element, string.Empty);
        return new TableCell(element, () => string.Empty);
    }

    /// <summary>A button cell (a row's Put back); the row keeps its name apart from the button's.</summary>
    public static TableCell ButtonCell(Button button) => new(button, () => string.Empty);

    // Strings by the current culture, ignoring case; numbers and dates by value. A cell with no value never gets here (Show puts it last).
    private sealed class CellComparer : IComparer<IComparable?>
    {
        public static readonly CellComparer Instance = new();

        public int Compare(IComparable? x, IComparable? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            return x is string a && y is string b ? CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase) : x.CompareTo(y);
        }
    }
}
