using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>ASP-10 (intent/2026-10-18-asp-10-table-lists.md): the lists are tables, one <c>ListView</c> with a header row
/// of sort buttons and fixed columns. A row is the list item (ControlType ListItem, or DataItem) whose automation name joins
/// its cells, so one row reads as one line; the full path of the Folder cell is part of that name and is its tooltip
/// (HelpText). Rows are found through the contract's AutomationIds on the controls inside them (<c>DuplicateCopy</c>,
/// <c>DuplicateKeep</c>, <c>LargeOldSelect</c>, <c>LeftoverItem</c>, <c>TempItem</c>, <c>PutBackEntry</c>); History rows,
/// which carry no tick box, are the innermost list items of <c>HistoryView</c>. Nothing here invokes a move.</summary>
internal static class Table
{
    /// <summary>A table row: a list item.</summary>
    public static bool IsRow(AutomationElement element)
    {
        try
        {
            var type = element.Current.ControlType;
            return type == ControlType.ListItem || type == ControlType.DataItem;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The row that holds <paramref name="element"/> (the element itself when it is a row), or null.</summary>
    public static AutomationElement? RowOf(AutomationElement element)
    {
        try
        {
            var node = element;
            for (var level = 0; level < 10 && node is not null; level++)
            {
                if (IsRow(node))
                {
                    return node;
                }

                if (Automation.Compare(node, AutomationElement.RootElement))
                {
                    return null;
                }

                node = TreeWalker.ControlViewWalker.GetParent(node);
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    /// <summary>The automation name of the row that holds <paramref name="element"/>; empty when there is none.</summary>
    public static string RowName(AutomationElement element)
    {
        try
        {
            return RowOf(element)?.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    /// <summary>True when a row name holds the item at <paramref name="path"/>: its name and its full folder (the contract
    /// puts the full path in the row's automation name; a row name with the whole path holds both too).</summary>
    public static bool Holds(string rowName, string path)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        return rowName.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
               && rowName.Contains(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every <paramref name="automationId"/> control in <paramref name="scope"/>; empty when the scope is gone.</summary>
    public static List<AutomationElement> Controls(AutomationElement scope, string automationId)
    {
        try
        {
            return AppSession.FindAll(scope, automationId);
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>The <paramref name="automationId"/> control whose row holds the item at <paramref name="path"/>, or null.</summary>
    public static AutomationElement? ControlFor(AutomationElement scope, string automationId, string path) =>
        Controls(scope, automationId).FirstOrDefault(c => Holds(RowName(c), path));

    /// <summary>The <paramref name="automationId"/> control whose row name contains <paramref name="text"/>, or null.</summary>
    public static AutomationElement? ControlWith(AutomationElement scope, string automationId, string text) =>
        Controls(scope, automationId).FirstOrDefault(c => RowName(c).Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <paramref name="automationId"/> control inside <paramref name="row"/>, or null.</summary>
    public static AutomationElement? InRow(AutomationElement row, string automationId)
    {
        try
        {
            return AppSession.TryFind(row, automationId);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The rows of the <paramref name="automationId"/> controls, in the list's order: (row name, state, enabled).</summary>
    public static string Dump(AutomationElement scope, string automationId) =>
        "[" + string.Join(" | ", Controls(scope, automationId).Select(c =>
        {
            try
            {
                var state = c.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) ? " (" + ((TogglePattern)toggle).Current.ToggleState + ")"
                    : c.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select) ? (((SelectionItemPattern)select).Current.IsSelected ? " (chosen)" : string.Empty)
                    : string.Empty;
                return RowName(c) + state + (c.Current.IsEnabled ? string.Empty : " (disabled)");
            }
            catch (ElementNotAvailableException)
            {
                return "(gone)";
            }
        })) + "]";

    public static ToggleState State(AutomationElement box) => ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    public static bool IsChosen(AutomationElement radio) => ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    /// <summary>Ticks <paramref name="box"/> when it is not ticked (a second call does not untick it), then waits for the tick
    /// read through <paramref name="again"/> (the box found again: a list may recycle its rows).</summary>
    public static void Tick(AutomationElement box, Func<AutomationElement?> again, string what)
    {
        Assert.IsTrue(box.Current.IsEnabled, what + ": the tick box is disabled");
        if (State(box) != ToggleState.On)
        {
            ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        }

        Assert.IsTrue(AppSession.WaitUntil(() => again() is { } b && State(b) == ToggleState.On, TimeSpan.FromSeconds(3)), what + " did not tick");
    }

    /// <summary>Tries to tick a box the contract keeps unticked (disabled); a refusal by the control is the contract.</summary>
    public static void TryToTick(AutomationElement box)
    {
        try
        {
            if (box.Current.IsEnabled)
            {
                ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            }
        }
        catch (ElementNotEnabledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Chooses the radio <paramref name="radio"/> (Duplicates' Keep).</summary>
    public static void Choose(AutomationElement radio) => ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    // ---- headers -------------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="name"/> is the header <paramref name="text"/> (a sort state may follow after a comma).</summary>
    private static bool IsHeaderName(string name, string text) =>
        name == text || name.StartsWith(text + ",", StringComparison.Ordinal);

    /// <summary>The column headers named <paramref name="text"/> in <paramref name="scope"/>, in tree order: buttons (the
    /// contract: a header is a button that sorts), outside every row.</summary>
    public static List<AutomationElement> Headers(AutomationElement scope, string text)
    {
        var condition = new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true);
        try
        {
            return scope.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
                .Where(e => IsHeaderName(e.Current.Name ?? string.Empty, text) && RowOf(e) is null).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>The one column header <paramref name="text"/> of the single table in <paramref name="scope"/>.</summary>
    public static AutomationElement Header(AutomationElement scope, string text)
    {
        List<AutomationElement> found = [];
        Assert.IsTrue(AppSession.WaitUntil(() => (found = Headers(scope, text)).Count > 0, Ui.ElementTimeout),
            $"no column header button \"{text}\" in {scope.Current.AutomationId}: {Ui.Dump(scope)}");
        Assert.AreEqual(1, found.Count, $"{found.Count} column headers \"{text}\" in {scope.Current.AutomationId}, expected the one table's");
        return found[0];
    }

    /// <summary>Each of <paramref name="tables"/> tables in <paramref name="scope"/> has the header buttons
    /// <paramref name="columns"/>, in that order from left to right.</summary>
    public static void AssertColumns(AutomationElement scope, int tables, params string[] columns)
    {
        var headers = new List<List<AutomationElement>>();
        foreach (var column in columns)
        {
            List<AutomationElement> found = [];
            Assert.IsTrue(AppSession.WaitUntil(() => (found = Headers(scope, column)).Count == tables, Ui.ElementTimeout),
                $"{scope.Current.AutomationId}: {found.Count} column header buttons \"{column}\", expected {tables} (one per table): {Ui.Dump(scope)}");
            headers.Add(found);
        }

        for (var table = 0; table < tables; table++)
        {
            var lefts = headers.Select(h => h[table].Current.BoundingRectangle.Left).ToList();
            for (var i = 1; i < columns.Length; i++)
            {
                Assert.IsTrue(lefts[i] > lefts[i - 1],
                    $"{scope.Current.AutomationId}, table {table + 1}: the column \"{columns[i]}\" (left {lefts[i]:F0}) is not right of \"{columns[i - 1]}\" (left {lefts[i - 1]:F0}); expected the order {string.Join(", ", columns)}");
            }
        }
    }

    /// <summary>Presses a column header (a button).</summary>
    public static void Press(AutomationElement header) => ((InvokePattern)header.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    // ---- cells ---------------------------------------------------------------------------------------------------------

    /// <summary>A date as the tables show it: the short date of the current culture, local time.</summary>
    public static string ShortDate(DateTime utc) => utc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    /// <summary>True when <paramref name="shown"/> is <paramref name="full"/> shortened in the middle: shorter, with an
    /// ellipsis between a start and an end of the full path.</summary>
    public static bool IsMiddleShortened(string shown, string full)
    {
        foreach (var ellipsis in new[] { "…", "..." })
        {
            var at = shown.IndexOf(ellipsis, StringComparison.Ordinal);
            if (at <= 0 || at + ellipsis.Length >= shown.Length)
            {
                continue;
            }

            var head = shown[..at];
            var tail = shown[(at + ellipsis.Length)..];
            if (shown.Length < full.Length && full.StartsWith(head, StringComparison.OrdinalIgnoreCase) && full.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                && head.Length + tail.Length < full.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The texts of <paramref name="row"/> and everything in it.</summary>
    public static List<(string Name, string Help)> Texts(AutomationElement row)
    {
        try
        {
            return row.FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(e => (e.Current.Name ?? string.Empty, e.Current.HelpText ?? string.Empty)).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>Every hint text a display-only row may carry: the <c>Hint*</c> constants of <see cref="Text"/> and
    /// <see cref="Text.HintSynced"/>'s fixed part.</summary>
    public static IReadOnlyList<string> Hints { get; } = typeof(Text).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(string) && f.Name.StartsWith("Hint", StringComparison.Ordinal))
        .Select(f => (string)f.GetValue(null)!)
        .Append(Text.HintSynced("\u0001").Split('\u0001')[0].Trim())
        .Where(h => h.Length > 0).ToList();

    /// <summary>The first hint <paramref name="text"/> holds, or null.</summary>
    public static string? HintIn(string text) => Hints.FirstOrDefault(h => text.Contains(h, StringComparison.Ordinal));

    // ---- rows without a tick box (History) -----------------------------------------------------------------------------

    /// <summary>The innermost rows in <paramref name="scope"/>, in tree order (a row that holds other rows is a group).</summary>
    public static List<AutomationElement> LeafRows(AutomationElement scope)
    {
        var condition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
        try
        {
            return scope.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
                .Where(r => r.Current.AutomationId != "HistorySession" && r.FindFirst(TreeScope.Descendants, condition) is null).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    /// <summary>The names of <see cref="LeafRows"/>.</summary>
    public static List<string> LeafRowNames(AutomationElement scope)
    {
        var names = new List<string>();
        foreach (var row in LeafRows(scope))
        {
            try
            {
                names.Add(row.Current.Name ?? string.Empty);
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return names;
    }

    /// <summary>A History row's name for (path, result, reason): it holds the item's name, its full folder, the result and
    /// the reason (ordinal for the result: "Put back" is not "Could not be put back"); <paramref name="otherReason"/>, when
    /// given, must not appear in a row without a reason.</summary>
    public static bool IsHistoryRow(string rowName, string path, string result, string? reason, string? otherReason = null) =>
        Holds(rowName, path) && rowName.Contains(result, StringComparison.Ordinal)
        && (reason is null ? otherReason is null || !rowName.Contains(otherReason, StringComparison.Ordinal) : rowName.Contains(reason, StringComparison.Ordinal));
}
