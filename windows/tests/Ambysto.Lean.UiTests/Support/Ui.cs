using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>UI Automation steps shared by the round-1, accessibility and ASP-14 tests (the same steps some other test classes
/// keep as their own private helpers). Nothing here invokes Reveal, OpenRecycleBin or UninstallInSettings; Move and Put back
/// are invoked only by the tests, on their own items under <c>%USERPROFILE%\LeanUiMove-*</c>.</summary>
internal static class Ui
{
    public static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DuplicatesTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a test watches for a change that must not happen.</summary>
    public static readonly TimeSpan NeverWatch = TimeSpan.FromSeconds(3);

    /// <summary>Invoke, or Select for a list item.</summary>
    public static void Click(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
        }
        else
        {
            Assert.Fail($"{element.Current.AutomationId} cannot be invoked");
        }
    }

    public static AutomationElement FindAnywhere(AppSession app, string automationId, TimeSpan timeout)
    {
        var element = AppSession.WaitFor(() => app.FindInAnyWindow(automationId), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} in any window of the app within {timeout.TotalSeconds:F0} s");
        return element;
    }

    /// <summary>Every element with <paramref name="automationId"/> in any window of the app.</summary>
    public static List<AutomationElement> FindAllAnywhere(AppSession app, string automationId)
    {
        var all = new List<AutomationElement>();
        foreach (var top in TopWindows(app))
        {
            all.AddRange(AppSession.FindAll(top, automationId));
        }

        return all;
    }

    /// <summary>The app's top-level windows (the main window, and a popup such as a context menu).</summary>
    public static List<AutomationElement> TopWindows(AppSession app) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id)).Cast<AutomationElement>().ToList();

    /// <summary>The Names of <paramref name="scope"/> and everything below it.</summary>
    public static List<string> Names(AutomationElement scope)
    {
        try
        {
            return scope.FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name ?? string.Empty).ToList();
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
    }

    public static bool AnyNameContains(AutomationElement scope, string text) => Names(scope).Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));

    public static string Dump(AutomationElement scope) => "[" + string.Join(" | ", Names(scope).Where(n => n.Length > 0)) + "]";

    /// <summary>Waits until the scan started with --scan has finished and returns the map: tiles in the map, no scan running
    /// (<see cref="ScanRunning"/>), Scan enabled, and the tiles no longer redrawn (ASP-14: during the scan the map shows the
    /// zones, so the tiles of the final map are the ones that stay).</summary>
    public static AutomationElement WaitForScan(AppSession app)
    {
        var window = app.Window(WindowTimeout);
        var status = AppSession.Find(window, "Status", ElementTimeout);
        string? previous = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                var now = ScanFinished(app) ? TileKeys(app) : null;
                var stable = now is not null && now == previous;
                previous = now;
                if (!stable)
                {
                    System.Threading.Thread.Sleep(250);
                }

                return stable;
            }, ScanTimeout),
            "the scan did not finish (status: \"" + status.Current.Name + "\")");
        return Map(app);
    }

    /// <summary>True once a scan has ended: the map holds tiles, no scan runs and Scan is enabled.</summary>
    public static bool ScanFinished(AppSession app)
    {
        try
        {
            var window = app.Window(WindowTimeout);
            var map = AppSession.TryFind(window, "Map");
            var scan = AppSession.TryFind(window, "Scan");
            return map is not null && AppSession.FindAll(map, "Tile").Count > 0 && !ScanRunning(app) && scan is not null && scan.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>True while a scan runs: the main window's Cancel is there, on screen and enabled (ASP-14: Cancel follows the
    /// progress line during a scan; when no scan runs it is disabled or not shown at all).</summary>
    public static bool ScanRunning(AppSession app)
    {
        try
        {
            return AppSession.TryFind(app.Window(WindowTimeout), "Cancel") is { } cancel && !cancel.Current.IsOffscreen && cancel.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>The runtime ids of the map's tiles now, in order (a redrawn map gives new ones).</summary>
    private static string TileKeys(AppSession app)
    {
        try
        {
            return string.Join(",", AppSession.FindAll(Map(app), "Tile").Select(Key));
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    /// <summary>The map of the main window, found again (after a view closed, the earlier element may be stale).</summary>
    public static AutomationElement Map(AppSession app) => AppSession.Find(app.Window(WindowTimeout), "Map", ElementTimeout);

    public static string[] TileNames(AutomationElement map) => AppSession.FindAll(map, "Tile").Select(t => t.Current.Name).ToArray();

    public static bool HasTile(AutomationElement map, string name) => TileNames(map).Any(n => n.StartsWith(name + ", ", StringComparison.Ordinal));

    public static AutomationElement TileOf(AutomationElement map, string name)
    {
        AutomationElement? tile = null;
        Assert.IsTrue(AppSession.WaitUntil(() => (tile = AppSession.FindAll(map, "Tile").FirstOrDefault(t => t.Current.Name.StartsWith(name + ", ", StringComparison.Ordinal))) is not null, ElementTimeout),
            $"no tile for {name}; tiles: [{string.Join(" | ", TileNames(map))}]");
        return tile!;
    }

    /// <summary>True when the keyboard focus is on <paramref name="element"/>.</summary>
    public static bool HasFocus(AutomationElement element)
    {
        try
        {
            return AutomationElement.FocusedElement is { } focused && Automation.Compare(focused, element);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>Gives <paramref name="element"/> the keyboard focus, or fails.</summary>
    public static void Focus(AutomationElement element)
    {
        element.SetFocus();
        Assert.IsTrue(AppSession.WaitUntil(() => HasFocus(element), TimeSpan.FromSeconds(3)), element.Current.Name + " did not take focus");
    }

    /// <summary>Focuses <paramref name="element"/>, opens its context menu (Shift+F10) and returns the menu item
    /// <paramref name="itemId"/>. The caller invokes it or presses Escape.</summary>
    public static AutomationElement MenuItemOf(AppSession app, AutomationElement element, string itemId)
    {
        Focus(element);
        Keys.PressWithShift(Keys.F10);
        var item = AppSession.WaitFor(() => app.FindInAnyWindow(itemId), TimeSpan.FromSeconds(5));
        Assert.IsNotNull(item, $"no {itemId} item in the context menu of {element.Current.Name}");
        return item;
    }

    /// <summary>Whether the tile's menu offers an enabled Move to Recycle Bin; the menu is closed again (Escape).</summary>
    public static bool TileMoveEnabled(AppSession app, AutomationElement map, string name)
    {
        var move = MenuItemOf(app, TileOf(map, name), "MoveToRecycleBin");
        try
        {
            return move.Current.IsEnabled;
        }
        finally
        {
            Keys.Press(Keys.Escape);
            AppSession.WaitUntil(() => app.FindInAnyWindow("MoveToRecycleBin") is null, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Clicks the sidebar item <paramref name="item"/> (ASP-14: the items keep the AutomationIds the view buttons
    /// had) and returns the view <paramref name="viewId"/>.</summary>
    public static AutomationElement OpenView(AppSession app, string item, string viewId)
    {
        Click(SidebarItem(app, item));
        return FindAnywhere(app, viewId, ElementTimeout);
    }

    /// <summary>The sidebar (ASP-14, AutomationId <c>Sidebar</c>) of the main window.</summary>
    public static AutomationElement Sidebar(AppSession app) => AppSession.Find(app.Window(WindowTimeout), "Sidebar", ElementTimeout);

    /// <summary>The sidebar item <paramref name="id"/> (Overview, LargeOld, TemporaryFiles, Duplicates, Uninstall, History,
    /// Settings, About), found inside the sidebar.</summary>
    public static AutomationElement SidebarItem(AppSession app, string id) => AppSession.Find(Sidebar(app), id, ElementTimeout);

    /// <summary>Goes back from the open view <paramref name="viewId"/> to Overview by its sidebar item (ASP-14: the views have
    /// no BackToMap button any more) and waits until the view is gone.</summary>
    public static void BackToOverview(AppSession app, string viewId)
    {
        Assert.IsNotNull(AppSession.WaitFor(() => app.FindInAnyWindow(viewId), ElementTimeout), $"precondition: {viewId} is not open");
        Click(SidebarItem(app, "Overview"));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow(viewId) is not { } v || v.Current.IsOffscreen, ElementTimeout),
            viewId + " is still shown after the Overview item was clicked");
    }

    /// <summary>The ShowSystemItems toggle, which ASP-14 moves into Large and old (the only list it changes): opens Large and
    /// old by its sidebar item and returns the toggle found inside the view.</summary>
    public static TogglePattern ShowSystemItems(AppSession app)
    {
        var view = OpenView(app, "LargeOld", "LargeOldView");
        var toggle = AppSession.Find(view, "ShowSystemItems", ElementTimeout);
        return (TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
    }

    /// <summary>A Copy.Text string read at run time, so a missing one fails only the test that needs it (a surface gap)
    /// and does not break the build of the other UI tests (as FindAndUninstallViewsTests' TextConstant): a public static
    /// field or property named <paramref name="name"/> when no <paramref name="arguments"/> are given, otherwise a public
    /// static method of that name taking that many arguments.</summary>
    public static string Copy(string name, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var type = typeof(Ambysto.Lean.Core.Copy.Text);
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        if (arguments.Length == 0)
        {
            if (type.GetField(name, Flags) is { } field)
            {
                return (string)field.GetValue(null)!;
            }

            if (type.GetProperty(name, Flags) is { } property)
            {
                return (string)property.GetValue(null)!;
            }
        }

        var method = type.GetMethods(Flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length);
        Assert.IsNotNull(method, $"surface gap: Copy.Text has no public {(arguments.Length == 0 ? "constant" : "method with " + arguments.Length + " argument(s)")} {name} (ASP-14 UI contract)");
        return (string)method.Invoke(null, arguments)!;
    }

    /// <summary>The profile's drive as the contract writes it, "C:".</summary>
    public static string ProfileDrive() =>
        System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!.TrimEnd('\\');

    /// <summary>Presses ConfirmMoveButton and returns the MoveReport once the session has finished.</summary>
    public static AutomationElement ConfirmAndWaitForReport(AppSession app, string spaceReturnsText)
    {
        Click(FindAnywhere(app, "ConfirmMoveButton", ElementTimeout));
        return WaitForReport(app, spaceReturnsText);
    }

    /// <summary>The MoveReport once the session has finished (it holds the "space returns" line or a ReportItem).</summary>
    public static AutomationElement WaitForReport(AppSession app, string spaceReturnsText)
    {
        var report = FindAnywhere(app, "MoveReport", MoveTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => AnyNameContains(report, spaceReturnsText) || AppSession.FindAll(report, "ReportItem").Count > 0, MoveTimeout),
            "the move report did not complete: " + Dump(report));
        return report;
    }

    /// <summary>The selection item pattern of a list row: on the row or on the list item that holds it.</summary>
    public static SelectionItemPattern SelectionItemOf(AutomationElement row)
    {
        var node = row;
        for (var level = 0; level < 4 && node is not null; level++)
        {
            if (node.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            {
                return (SelectionItemPattern)pattern;
            }

            node = TreeWalker.ControlViewWalker.GetParent(node);
        }

        Assert.Fail(row.Current.Name + " cannot be selected (no SelectionItem pattern on it or its list item)");
        return null!;
    }

    /// <summary>True when <paramref name="element"/> is <paramref name="ancestor"/> or lies below it in the control view.</summary>
    public static bool IsWithin(AutomationElement element, AutomationElement ancestor)
    {
        try
        {
            for (var node = element; node is not null; node = TreeWalker.ControlViewWalker.GetParent(node))
            {
                if (Automation.Compare(node, ancestor))
                {
                    return true;
                }

                if (Automation.Compare(node, AutomationElement.RootElement))
                {
                    return false;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return false;
    }

    /// <summary>A short description of an element for a failure message.</summary>
    public static string Describe(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            return $"{current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)} id=\"{current.AutomationId}\" name=\"{current.Name}\" class=\"{current.ClassName}\"{(current.IsOffscreen ? " offscreen" : string.Empty)}";
        }
        catch (ElementNotAvailableException)
        {
            return "(gone)";
        }
    }

    /// <summary>A stable key for an element: its runtime id.</summary>
    public static string Key(AutomationElement element) => string.Join(".", element.GetRuntimeId());
}
