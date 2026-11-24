using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The Apps and caches view (ASP-20 phase B, intent/2026-10-21-asp-20-suggest-catalog.plan.md; docs/spec/05, 06, 07). The Core's
/// <see cref="CommandRows"/> and <see cref="GuideRows"/> read the rows; this file lists them, with nothing ticked, and a command runs
/// only from <c>ClearApps</c>, through <c>ConfirmClear</c> and the Core's <see cref="CleanRunner"/> (the only code that starts a
/// program is <see cref="ProcessCommandRunner"/>). A guide row is shown only. Free sees every row; the tick boxes, Clear and Suggest
/// are Pro (owner decision 15). Nothing here deletes or moves a file: the tool's own command does, after the user approved it.</summary>
public sealed partial class MainWindow
{
    private sealed record AppsRead(IReadOnlyList<CommandRow> Commands, string? Refusal, IReadOnlyList<GuideRow> Guides);

    // A clean is waiting for its confirmation or running: moves, Suggest and Select all wait (MoveBlocked), and a move blocks Clear.
    private bool cleanBusy;

    // Closing the window is held from Clear pressed to the last row's outcome (a permanent delete must reach the command log). While the
    // confirmation is the only thing open (cleanRunning false) the close ends it as Cancel; while rows run it waits. Either way the window
    // closes by itself when the clean ends (closeAfterClean), and does not ask again.
    private bool cleanRunning;
    private bool closeAfterClean;
    private ContentDialog? clearDialog;

    private int appsVersion;
    private CancellationTokenSource? appsCancel;

    // The command rows as shown (the ones the view lists), by the read that built the table.
    private List<CommandRow> appsCommandRows = [];

    // Every visible string, from Copy.Text.
    private void InitializeApps()
    {
        AppsTitle.Text = Text.AppsAndCaches;
        ClearAppsButton.Content = Text.Clear;
        AutomationProperties.SetName(ClearAppsButton, Text.Clear);
        SuggestAppsButton.Content = Text.Suggest;
        AutomationProperties.SetName(SuggestAppsButton, Text.Suggest);
        RefreshAppsButton.Content = Text.Refresh;
        AutomationProperties.SetName(RefreshAppsButton, Text.Refresh);
        ClearAppsProLabel.Text = Text.ProLabel;
        SuggestAppsProLabel.Text = Text.ProLabel;
        AppsTable.SetColumns(
        [
            new TableColumn(Text.ColumnClear, 70),
            new TableColumn(Text.ColumnName, 140),
            new TableColumn(Text.ColumnSize, 90),
            new TableColumn(Text.ColumnWhy, 300),
            new TableColumn(Text.ColumnSource, 170),
            new TableColumn(Text.ColumnNote),
        ]);
        Closed += (_, _) => appsCancel?.Cancel();
        AppWindow.Closing += OnAppWindowClosing;
        InitializeAppsSections();
        UpdateAppsControls();
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (moveBusy)
        {
            args.Cancel = true;
            HoldCloseForMove(); // ASP-24: a move or Put back (MainWindow.Moves.cs)
            return;
        }

        if (!cleanBusy)
        {
            return; // the normal close, and the deferred close once the clean or the move has ended
        }

        args.Cancel = true;
        closeAfterClean = true;
        if (cleanRunning)
        {
            StatusText.Text = Text.LeanClosesWhenClearingEnds;
        }
        else
        {
            clearDialog?.Hide(); // the confirmation ends as Cancel: nothing runs; the window then closes
        }
    }

    private void OnAppsAndCachesClick(object sender, RoutedEventArgs e)
    {
        ShowView(AppsView);
        StaleAppsSections();
        if (!cleanBusy)
        {
            _ = RefreshAppsAsync(); // while a clean runs, its end reads the rows again
        }

        ReadShownAppsSection(fresh: false);
    }

    private void OnRefreshAppsClick(object sender, RoutedEventArgs e)
    {
        if (appsSection != AppsSection.Caches)
        {
            ReadShownAppsSection(fresh: true); // Refresh reads the table that is shown
        }
        else if (!cleanBusy)
        {
            _ = RefreshAppsAsync();
        }
    }

    // ---- reading ------------------------------------------------------------------------------------------------------

    // The rows. The location queries start the tools' read-only commands and the walks take time, so all of it runs off the UI thread;
    // the latest request wins.
    private async Task RefreshAppsAsync()
    {
        var version = ++appsVersion;
        appsCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        appsCancel = cancel;
        appsCommandRows = [];
        appsGuides = null;
        EndSuggestStatus(appsSuggestLine); // the rows are read again and nothing is ticked: Suggest's status line goes (ASP-22)
        AppsTable.Clear();
        AppsMessage.Text = Text.ReadingAppsAndCaches;
        UpdateAppsControls();
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            var userFolders = userProtectedFolders;
            var read = await Task.Run(() => ReadAppsAsync(userFolders, context, cancel.Token), cancel.Token);
            if (version != appsVersion)
            {
                return;
            }

            // A tool that is not installed and an empty cache are not listed (plan).
            appsCommandRows = [.. read.Commands.Where(r => r.State is not (CommandRowState.ProgramMissing or CommandRowState.Empty))];
            appsGuides = read.Guides;
            AppsTable.Batch(() =>
            {
                foreach (var row in appsCommandRows)
                {
                    AddAppsCommandRow(row, read.Refusal);
                }

                foreach (var guide in read.Guides)
                {
                    AddAppsGuideRow(guide);
                }
            });
            AppsMessage.Text = AppsTable.Count == 0 ? Text.NothingToClear : string.Empty;
            UpdateAppsControls();
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one, or the window closed.
        }
        catch (Exception)
        {
            // Whatever went wrong ends the "Reading…" state and says so; it never hangs.
            if (version == appsVersion)
            {
                appsCommandRows = [];
                AppsTable.Clear();
                AppsMessage.Text = Text.AppsReadFailed;
                UpdateAppsControls();
            }
        }
    }

    private static async Task<AppsRead> ReadAppsAsync(IReadOnlyList<string> userFolders, PathContext context, CancellationToken token)
    {
        var environment = new KitCommandEnvironment(new KitItemGate(LeanModule.Map, NoModuleItems, userFolders));
        var commands = CommandRows.ReadAsync(environment, new PathProgramFinder(), new ProcessCommandRunner(), ReadProcessImages, token);
        var guides = GuideRows.ReadAsync(context, new RegistryUninstallSource(), token);
        await Task.WhenAll(commands, guides).ConfigureAwait(false);
        var rows = await commands.ConfigureAwait(false);
        var refusal = rows.Any(r => r.State == CommandRowState.NotAllowedHere) ? environment.Refusal() : null;
        return new AppsRead(rows, refusal, await guides.ConfigureAwait(false));
    }

    private static IReadOnlyList<string> ReadProcessImages() => new RegistryUninstallSource().ReadProcessImagePaths();

    // ---- rows ---------------------------------------------------------------------------------------------------------

    private static string SourceText(CatalogEntry entry) => entry.Grade == EvidenceGrade.Strong ? Text.SourceVendorDocs : Text.SourceCommunity;

    // The same words the runner refuses with, so the row says before the click what the click would say.
    private static string ToolRunningText(CommandRow row) =>
        row.Command.Program == "dotnet.exe" ? Text.CleanDotnetRunning : Text.CleanToolRunning(row.Entry.AppName);

    // One command row: Clear (a tick box, enabled only for a Ready row in Pro, never ticked here), Name, Size, Why, Source and Note.
    // A row whose tool did not name an existing folder has no size.
    private void AddAppsCommandRow(CommandRow row, string? refusal)
    {
        var ready = row.State == CommandRowState.Ready;
        var (tickCell, box) = LeanTable.TickCell("AppsItem", entitlement.IsPro && ready);
        box.Checked += OnAppsTickChanged;
        box.Unchecked += OnAppsTickChanged;
        var note = row.State switch
        {
            CommandRowState.ToolRunning => ToolRunningText(row),
            CommandRowState.LocationUnknown => Text.CacheNotFound,
            CommandRowState.NotAllowedHere => refusal ?? Text.CleanEnvironmentUnknown,
            _ => null,
        };
        var cells = new List<TableCell>
        {
            tickCell,
            LeanTable.Label(row.Entry.AppName),
            row.Location is null ? LeanTable.Blank() : LeanTable.SizeLabel(ByteUnits.Format(row.Bytes), row.Bytes),
            LeanTable.Label(row.Entry.Why),
            LeanTable.Label(SourceText(row.Entry), toolTip: row.Entry.EvidenceUrl),
            note is null ? LeanTable.Empty() : LeanTable.Label(note),
        };
        AppsTable.AddRow("AppsRow", row, cells, tick: box, displayOnly: !ready);
    }

    // One guide row: shown only. A browser's Note says what to press (Lean does not open the browser's page); another row has an
    // Open folder button, which works in Free too.
    private void AddAppsGuideRow(GuideRow row)
    {
        TableCell note;
        if (row.Action == GuideAction.OpenClearBrowsingData)
        {
            note = LeanTable.Label(Text.OpenBrowserClearData(row.Title));
        }
        else
        {
            var button = new Button { Content = Text.OpenFolder };
            AutomationProperties.SetAutomationId(button, "AppsOpenFolder");
            AutomationProperties.SetName(button, Text.OpenFolder);
            button.Click += (_, _) => _ = OpenInstallFolderAsync(row.Folder);
            note = LeanTable.ButtonCell(button);
        }

        var cells = new List<TableCell>
        {
            LeanTable.Blank(),
            LeanTable.Label(row.Title),
            LeanTable.SizeLabel(ByteUnits.Format(row.Bytes), row.Bytes),
            LeanTable.Label(row.Entry.Why),
            LeanTable.Label(SourceText(row.Entry), toolTip: row.Entry.EvidenceUrl),
            note,
        };
        AppsTable.AddRow("AppsRow", row, cells, displayOnly: true);
    }

    // ---- controls and Suggest -----------------------------------------------------------------------------------------

    private void OnAppsTickChanged(object sender, RoutedEventArgs e) => UpdateAppsButtons();

    // Free: the tick boxes, Clear and Suggest are off and the quiet "Pro" label shows; Pro: they work (a Ready row's box) and the label
    // goes. Follows a key entered in Settings at once. A Free app never keeps a tick.
    private void UpdateAppsControls()
    {
        var pro = entitlement.IsPro;
        foreach (var row in AppsTable.Rows)
        {
            if (row.Tag is CommandRow command && row.Tick is { } tick)
            {
                tick.IsEnabled = pro && !cleanBusy && command.State == CommandRowState.Ready;
                if (!pro && tick.IsChecked == true)
                {
                    tick.IsChecked = false;
                }
            }
        }

        UpdateAppsButtons();
    }

    // Clear needs a ticked Ready row and nothing else running (MoveBlocked includes a clean); Suggest a Ready row to tick.
    private void UpdateAppsButtons()
    {
        var pro = entitlement.IsPro;
        var open = pro && !MoveBlocked;
        RefreshAppsButton.IsEnabled = !cleanBusy;
        ClearAppsButton.IsEnabled = open && TickedAppRows().Count > 0;
        SuggestAppsButton.IsEnabled = open && appsCommandRows.Any(r => r.State == CommandRowState.Ready);
        var label = pro ? Visibility.Collapsed : Visibility.Visible;
        ClearAppsProLabel.Visibility = label;
        SuggestAppsProLabel.Visibility = label;
        ClearAppsProBadge.Visibility = label;
        SuggestAppsProBadge.Visibility = label;
    }

    // The ticked rows that were Ready when read.
    private List<CommandRow> TickedAppRows() =>
        AppsTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<CommandRow>().Where(r => r.State == CommandRowState.Ready).ToList();

    // Ticks every Ready row; it runs nothing (Clear still asks first).
    private void OnSuggestAppsClick(object sender, RoutedEventArgs e)
    {
        if (!entitlement.IsPro || MoveBlocked)
        {
            return;
        }

        var count = 0;
        foreach (var row in AppsTable.Rows)
        {
            if (row.Tag is CommandRow { State: CommandRowState.Ready } && row.Tick is { IsEnabled: true } tick)
            {
                tick.IsChecked = true;
                count++;
            }
        }

        SetSuggestStatus(appsSuggestLine, Text.SuggestTicked(count));
        UpdateAppsButtons();
    }

    // ---- Clear --------------------------------------------------------------------------------------------------------

    private void OnClearAppsClick(object sender, RoutedEventArgs e) => _ = ClearAppsAsync();

    // The rows run are the ticked Ready rows as they are now, captured once: the confirmation shows exactly them, and exactly them run.
    // The table is not read again in between.
    private async Task ClearAppsAsync()
    {
        if (!entitlement.IsPro || MoveBlocked)
        {
            return;
        }

        var approved = TickedAppRows();
        if (approved.Count == 0)
        {
            return;
        }

        var ran = false;
        var closed = false;
        cleanBusy = true;
        UpdateMoveControls();
        try
        {
            if (!await ConfirmClearAsync(approved))
            {
                return;
            }

            cleanRunning = true;
            await RunCleansAsync(approved);
            ran = true;
        }
        finally
        {
            cleanBusy = false;
            cleanRunning = false;
            UpdateMoveControls();
            if (closeAfterClean)
            {
                closeAfterClean = false;
                closed = true;
                Close(); // cleanBusy is false now, so the close is not held again
            }
        }

        if (ran && !closed)
        {
            await RefreshAppsAsync();
        }
    }

    // The command as it will run, for the eye: the full program path and every argument, quoted where one holds a space. The runner
    // passes each argument on its own; this text is never run.
    internal static string CommandLineText(string program, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { program }.Concat(arguments).Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument) =>
        argument.Length == 0 || argument.AsSpan().IndexOfAny(' ', '\t') >= 0 ? "\"" + argument + "\"" : argument;

    // ConfirmClear: one item per row (the app, its size and the exact command), the warning always, Clear and Cancel; Cancel is the
    // default button and Escape closes the dialog, so neither runs anything.
    private async Task<bool> ConfirmClearAsync(List<CommandRow> rows)
    {
        if (Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var list = new StackPanel { Spacing = 8 };
        foreach (var row in rows)
        {
            var command = CommandLineText(row.ProgramPath!, CleanCommands.CleanArgumentsFor(row.Command, row.ProgramPath!));
            var text = new TextBlock
            {
                Text = Text.ConfirmClearItem(row.Entry.AppName, ByteUnits.Format(row.Bytes)) + "\n" + command,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false,
            };
            AutomationProperties.SetAutomationId(text, "ConfirmClearItem");
            AutomationProperties.SetName(text, text.Text);
            list.Children.Add(text);
        }

        var warning = new TextBlock { Text = Text.ConfirmClearWarning, TextWrapping = TextWrapping.Wrap, Style = LeanStyles.Medium };
        AutomationProperties.SetAutomationId(warning, "ConfirmClearWarning");
        AutomationProperties.SetName(warning, warning.Text);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new ScrollViewer { MaxHeight = 360, Content = list });
        body.Children.Add(warning);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Text.ConfirmClearTitle,
            Content = body,
            PrimaryButtonText = Text.Clear,
            PrimaryButtonStyle = LeanStyles.Primary,
            RequestedTheme = ElementTheme.Dark,
            CloseButtonText = Text.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmClear");
        AutomationProperties.SetName(dialog, Text.ConfirmClearTitle);
        WireDialogButtons(dialog, "ConfirmClearButton", "CancelClear");
        confirmDialogOpen = true;
        clearDialog = dialog;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (COMException)
        {
            // Another dialog is open: nothing runs.
            return false;
        }
        finally
        {
            confirmDialogOpen = false;
            clearDialog = null;
        }
    }

    // One row at a time, each off the UI thread through the Core's CleanRunner (which checks the row again and logs the run). The
    // report lines stay under the table until the next Clear.
    private async Task RunCleansAsync(List<CommandRow> rows)
    {
        AppsReport.Children.Clear();
        var userFolders = userProtectedFolders;
        var cleared = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var app = row.Entry.AppName;
            if (!closeAfterClean)
            {
                StatusText.Text = Text.Clearing(app, i + 1, rows.Count); // a pending close keeps its own line
            }

            string line;
            try
            {
                var outcome = await Task.Run(() => NewCleanRunner(userFolders).Run(row, CancellationToken.None));
                if (outcome.Result == CleanResult.Cleaned)
                {
                    cleared++;
                }

                line = ReportLine(app, outcome);
            }
            catch (Exception)
            {
                // Whatever went wrong, the next row and the view still go on; it never hangs. The error's own message is not shown.
                line = Text.LabelValue(app, Text.ClearRunFailed(Text.ClearRunFailedReason));
            }

            var report = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(report, "AppsReportLine");
            AutomationProperties.SetName(report, line);
            AppsReport.Children.Add(report);
        }

        if (!closeAfterClean)
        {
            StatusText.Text = Text.ClearSummary(cleared, rows.Count);
        }
    }

    private static CleanRunner NewCleanRunner(IReadOnlyList<string> userFolders) =>
        new(new KitCommandEnvironment(new KitItemGate(LeanModule.Map, NoModuleItems, userFolders)), new PathProgramFinder(),
            new ProcessCommandRunner(), ReadProcessImages, CommandLog.Open(CommandLog.DefaultPath));

    // "<app>: <before> before, <after> now" after a run that ended with 0; "<app>: <reason>" for a refusal, an error or a timeout.
    private static string ReportLine(string app, CleanOutcome outcome)
    {
        if (outcome.Result != CleanResult.Cleaned)
        {
            return Text.LabelValue(app, outcome.Reason ?? string.Empty);
        }

        var after = outcome.BytesAfter is { } bytes ? ByteUnits.Format(bytes) : Text.Unknown;
        var line = Text.ClearReportDone(app, ByteUnits.Format(outcome.BytesBefore), after);
        return outcome.Reason is { } reason ? line + " — " + reason : line;
    }
}
