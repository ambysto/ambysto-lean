using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>Informational results, bound to the watch/session that produced them. No file action originates here.</summary>
public sealed partial class MainWindow
{
    private int uninstallResultContext;
    private ContentDialog? uninstallResultDialog;
    private sealed record UninstallNotice(InstalledApp App, int Context, string Summary,
        int Approved = -1, int Moved = 0, int Unsuccessful = 0, int Unattempted = 0, bool Recoverable = false);
    private UninstallNotice? pendingUninstallNotice;

    private void InvalidateUninstallResult()
    {
        uninstallResultContext++;
        pendingUninstallNotice = null;
        uninstallResultDialog?.Hide();
    }

    private bool CanShowUninstallResult(UninstallNotice notice) =>
        !aboutClosing && !closeAfterMove && !closeAfterClean && !moveBusy && !cleanBusy && !confirmDialogOpen
        && notice.Context == uninstallResultContext && ReferenceEquals(currentView, UninstallView);

    private static TextBlock UninstallResultLine(string id, string text)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(line, id);
        AutomationProperties.SetName(line, text);
        return line;
    }

    private async Task ShowUninstallResultAsync(UninstallNotice notice)
    {
        if (!CanShowUninstallResult(notice) || Content?.XamlRoot is not { } root) return;
        var body = new StackPanel { Spacing = 12 };
        var mark = new FontIcon { Glyph = notice.Approved >= 0 && notice.Unsuccessful + notice.Unattempted > 0 ? "\uE946" : "\uE73E", FontSize = 28 };
        AutomationProperties.SetName(mark, notice.Summary);
        body.Children.Add(mark);
        body.Children.Add(UninstallResultLine("UninstallResultApp", notice.App.DisplayName));
        body.Children.Add(UninstallResultLine("UninstallResultSummary", notice.Summary));
        if (notice.Approved >= 0)
        {
            body.Children.Add(UninstallResultLine("UninstallApprovedCount", Text.UninstallApprovedCount(notice.Approved)));
            body.Children.Add(UninstallResultLine("UninstallMovedCount", Text.UninstallMovedCount(notice.Moved)));
            body.Children.Add(UninstallResultLine("UninstallUnsuccessfulCount", Text.UninstallUnsuccessfulCount(notice.Unsuccessful)));
            body.Children.Add(UninstallResultLine("UninstallUnattemptedCount", Text.UninstallUnattemptedCount(notice.Unattempted)));
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = Text.UninstallResultTitle,
            Content = new ScrollViewer { Content = body, MaxHeight = 300 },
            PrimaryButtonText = Text.UninstallResultDetails, CloseButtonText = Text.CloseView,
            DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark,
        };
        if (notice.Recoverable) dialog.SecondaryButtonText = Text.UninstallResultPutBack;
        AutomationProperties.SetAutomationId(dialog, "UninstallResult");
        AutomationProperties.SetName(dialog, Text.UninstallResultTitle);
        WireDialogButtons(dialog, "UninstallResultDetails", "UninstallResultClose", "UninstallResultPutBack");
        uninstallResultDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            if (!CanShowUninstallResult(notice)) return;
            if (notice.Approved >= 0 && result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
            {
                MoveReport.Visibility = Visibility.Visible;
                if (result == ContentDialogResult.Secondary && notice.Recoverable)
                    PutBackSessionButton.Focus(FocusState.Programmatic);
                else CloseReportButton.Focus(FocusState.Programmatic);
            }
            else if (result == ContentDialogResult.Primary && LeftoverTable.Count > 0)
                UninstallButton.Focus(FocusState.Programmatic);
            else UninstallButton.Focus(FocusState.Programmatic);
        }
        catch (COMException)
        {
            // Existing modal work owns the window. The inline result remains; this notification is never queued.
        }
        finally { if (ReferenceEquals(uninstallResultDialog, dialog)) uninstallResultDialog = null; }
    }
}
