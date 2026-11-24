using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Drives the system folder picker (the common item dialog, window class <c>#32770</c>) that the app opens: types a
/// full path into its folder box and presses its "Select Folder" button (AutomationId <c>1</c>), or Cancel (<c>2</c>). It
/// only picks the test's own folders; nothing in the dialog is renamed, made or deleted.</summary>
internal static class FolderPicker
{
    private const string DialogClass = "#32770";

    /// <summary>The folder box's known AutomationIds: the file-name combo box's edit of the common item dialog.</summary>
    private static readonly string[] FolderBoxIds = ["1152", "1148"];

    /// <summary>The picker dialog of the app, or null now: a top-level window of the process, or a window below one.</summary>
    public static AutomationElement? TryFind(AppSession app)
    {
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                                         new PropertyCondition(AutomationElement.ClassNameProperty, DialogClass));
        try
        {
            foreach (var top in Ui.TopWindows(app))
            {
                if (top.Current.ClassName == DialogClass)
                {
                    return top;
                }

                if (top.FindFirst(TreeScope.Descendants, condition) is { } inside)
                {
                    return inside;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }

    public static AutomationElement WaitFor(AppSession app, TimeSpan timeout)
    {
        var dialog = AppSession.WaitFor(() => TryFind(app), timeout);
        Assert.IsNotNull(dialog, $"no folder picker (a {DialogClass} window of the app) opened within {timeout.TotalSeconds:F0} s");
        return dialog;
    }

    /// <summary>Picks <paramref name="folder"/> in the open picker: its full path typed into the folder box, then "Select
    /// Folder". A picker that navigated into the folder instead of returning it is asked again with the box empty (the
    /// current folder). Fails when the dialog does not close.</summary>
    public static void Choose(AppSession app, string folder, TimeSpan timeout)
    {
        var dialog = WaitFor(app, timeout);
        var box = FolderBox(dialog);
        ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(folder);
        System.Threading.Thread.Sleep(300);
        Ui.Click(Button(dialog, "1"));
        if (AppSession.WaitUntil(() => TryFind(app) is null, TimeSpan.FromSeconds(4)))
        {
            return;
        }

        // Still open: the picker went into the folder. Select the folder it now shows.
        var again = TryFind(app);
        if (again is not null)
        {
            try
            {
                ((ValuePattern)FolderBox(again).GetCurrentPattern(ValuePattern.Pattern)).SetValue(string.Empty);
            }
            catch (InvalidOperationException)
            {
            }

            Ui.Click(Button(again, "1"));
        }

        Assert.IsTrue(AppSession.WaitUntil(() => TryFind(app) is null, timeout), "the folder picker did not close after choosing " + folder);
    }

    /// <summary>Presses Cancel in an open picker, so the app can close; nothing when none is open.</summary>
    public static void CancelIfOpen(AppSession app)
    {
        try
        {
            if (TryFind(app) is { } dialog && dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "2")) is { } cancel)
            {
                Ui.Click(cancel);
                AppSession.WaitUntil(() => TryFind(app) is null, TimeSpan.FromSeconds(5));
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static AutomationElement FolderBox(AutomationElement dialog)
    {
        List<AutomationElement> edits = [];
        AutomationElement? box = null;
        Assert.IsTrue(AppSession.WaitUntil(() =>
            {
                edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToList();
                box = edits.FirstOrDefault(e => FolderBoxIds.Contains(e.Current.AutomationId))
                      ?? edits.FirstOrDefault(e => (e.Current.Name ?? string.Empty).StartsWith("Folder", StringComparison.OrdinalIgnoreCase));
                return box is not null;
            }, TimeSpan.FromSeconds(10)),
            "the folder picker has no folder box; its edits: [" + string.Join(" | ", edits.Select(Ui.Describe)) + "]");
        return box!;
    }

    private static AutomationElement Button(AutomationElement dialog, string automationId)
    {
        var button = AppSession.WaitFor(() => dialog.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))), TimeSpan.FromSeconds(5));
        Assert.IsNotNull(button, $"the folder picker has no button {automationId}");
        return button;
    }
}
