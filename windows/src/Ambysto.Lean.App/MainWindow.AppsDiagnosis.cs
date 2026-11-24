using System;
using System.Collections.Generic;
using System.Linq;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Xaml.Automation;

namespace Ambysto.Lean.App;

/// <summary>ASP-30's Developer space and Installers and versions tables. Reads and renders only; move wiring belongs to its reviewed flow.</summary>
public sealed partial class MainWindow
{
    private sealed record InstallerOffer(InstallerVersionRow Row, ScanNode? Node);
    private bool installerPreparing;

    private bool developerIsRead;
    private bool installersIsRead;
    private int developerVersion;
    private int installersVersion;
    private CancellationTokenSource? developerCancel;
    private CancellationTokenSource? installersCancel;

    private void InitializeAppsDiagnosis()
    {
        DeveloperTable.SetColumns(
        [
            new TableColumn(Text.ColumnName, Weight: 1.2, MinWidth: 70),
            new TableColumn(Text.ColumnCategory, Weight: 0.9, MinWidth: 70),
            new TableColumn(Text.ColumnLocation, Weight: 1.6, MinWidth: 100),
            new TableColumn(Text.ColumnSize, 80),
            new TableColumn(Text.ColumnNote, Weight: 2, MinWidth: 120),
        ]);
        MoveInstallersButton.Content = Text.MoveToRecycleBin;
        InstallersTable.SetColumns(
        [
            new TableColumn(Text.ColumnMove, 58),
            new TableColumn(Text.ColumnName, Weight: 1.2, MinWidth: 70),
            new TableColumn(Text.ColumnVersion, Weight: 0.8, MinWidth: 60),
            new TableColumn(Text.ColumnLocation, Weight: 1.6, MinWidth: 100),
            new TableColumn(Text.ColumnSize, 80),
            new TableColumn(Text.ColumnReason, Weight: 2, MinWidth: 130),
        ]);
        Closed += (_, _) =>
        {
            developerCancel?.Cancel();
            installersCancel?.Cancel();
        };
    }

    private static TableCell DiagnosisBytes(long? bytes) => bytes is { } measured
        ? LeanTable.ValueLabel(ByteUnits.Format(measured), measured)
        : new TableCell(LeanTable.NameLabel(Text.Unknown).Element, () => null);

    private void SetDeveloperMessage(string text)
    {
        DeveloperMessage.Text = text;
        ApplyAppsSection();
    }

    private void SetInstallersMessage(string text)
    {
        InstallersMessage.Text = text;
        ApplyAppsSection();
    }

    private async Task ReadDeveloperAsync()
    {
        var version = ++developerVersion;
        developerCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        developerCancel = cancel;
        developerIsRead = false;
        DeveloperTable.Clear();
        SetDeveloperMessage(Text.ReadingDeveloperSpace);
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            var rows = await DeveloperSpace.ReadAsync(context, new RegistryUninstallSource(), cancel.Token);
            if (version != developerVersion || cancel.IsCancellationRequested) { return; }
            DeveloperTable.Batch(() =>
            {
                foreach (var row in rows)
                {
                    var table = DeveloperTable.AddRow("DeveloperRow", row,
                    [
                        LeanTable.NameLabel(row.Name),
                        LeanTable.TextValue(row.Category),
                        LeanTable.FolderLabel(row.Path),
                        DiagnosisBytes(row.Bytes),
                        LeanTable.TextValue(row.Note),
                    ]);
                    AutomationProperties.SetHelpText(table.Item, row.Path);
                }
            });
            developerIsRead = true;
            SetDeveloperMessage(rows.Count == 0 ? Text.NoDeveloperSpace : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer read replaced this one, or the window closed.
        }
        catch (Exception)
        {
            if (version == developerVersion && !cancel.IsCancellationRequested)
            {
                DeveloperTable.Clear();
                SetDeveloperMessage(Text.DeveloperSpaceReadFailed);
            }
        }
    }

    private async Task ReadInstallersAsync()
    {
        var version = ++installersVersion;
        installersCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        installersCancel = cancel;
        installersIsRead = false;
        InstallersTable.Clear();
        SetInstallersMessage(Text.ReadingInstallersVersions);
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            var rows = await Task.Run(async () =>
            {
                var versions = await InstallerVersions.ReadAsync(context, new RegistryUninstallSource(), cancel.Token).ConfigureAwait(false);
                var offers = new List<InstallerOffer>();
                foreach (var row in versions)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    ScanNode? node = null;
                    if (row.CanMove)
                    {
                        var scan = await new Scanner().ScanAsync(row.Path, null, cancel.Token).ConfigureAwait(false);
                        if (!scan.Cancelled && scan.Root.Stamp is not null && scan.Coverage.UnreadableCount == 0 && scan.Coverage.CloudNotDownloadedCount == 0)
                            node = scan.Root;
                    }
                    offers.Add(new InstallerOffer(row, node));
                }
                return offers;
            }, cancel.Token);
            if (version != installersVersion || cancel.IsCancellationRequested) { return; }
            InstallersTable.Batch(() =>
            {
                foreach (var offer in rows)
                {
                    var row = offer.Row;
                    var (tickCell, tick) = LeanTable.TickCell("InstallerItem", row.CanMove && offer.Node is not null && !MoveBlocked);
                    tick.Checked += (_, _) => UpdateInstallerControls();
                    tick.Unchecked += (_, _) => UpdateInstallerControls();
                    var table = InstallersTable.AddRow("InstallerRow", offer,
                    [
                        tickCell,
                        LeanTable.NameLabel(row.AppName),
                        LeanTable.TextValue(row.Version),
                        LeanTable.FolderLabel(row.Path),
                        DiagnosisBytes(row.Bytes),
                        LeanTable.TextValue(row.Reason),
                    ], tick: tick, displayOnly: !row.CanMove || offer.Node is null);
                    AutomationProperties.SetHelpText(table.Item, row.Path);
                }
            });
            installersIsRead = true;
            UpdateInstallerControls();
            SetInstallersMessage(rows.Count == 0 ? Text.NoInstallersVersions : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer read replaced this one, or the window closed.
        }
        catch (Exception)
        {
            if (version == installersVersion && !cancel.IsCancellationRequested)
            {
                InstallersTable.Clear();
                SetInstallersMessage(Text.InstallersVersionsReadFailed);
            }
        }
    }
    private void UpdateInstallerControls()
    {
        MoveInstallersButton.IsEnabled = !MoveBlocked && !installerPreparing && InstallersTable.Rows.Any(r => r.IsTicked && r.Tag is InstallerOffer { Node: not null, Row.CanMove: true });
        foreach (var row in InstallersTable.Rows)
            if (row.Tick is { } tick && row.Tag is InstallerOffer offer)
                tick.IsEnabled = !MoveBlocked && !installerPreparing && offer.Row.CanMove && offer.Node is not null;
    }

    private async void OnMoveInstallersClick(object sender, RoutedEventArgs e)
    {
        if (MoveBlocked || installerPreparing) return;
        var selected = InstallersTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<InstallerOffer>()
            .Where(o => o.Row.CanMove && o.Node is not null).ToList();
        if (selected.Count == 0) return;
        installerPreparing = true;
        UpdateInstallerControls();
        try
        {
            // Capture only displayed, ticked version leaves. KitRule re-reads evidence inside the gate for each item.
            var paths = selected.Select(o => o.Row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await BeginMoveAsync(LeanModule.Installers, selected.Select(o => new SessionItem(o.Node!, null)).ToList(), paths);
            if (!aboutClosing && selected.Any(o => !System.IO.Directory.Exists(o.Row.Path))) await ReadInstallersAsync();
        }
        finally { installerPreparing = false; UpdateInstallerControls(); }
    }

}
