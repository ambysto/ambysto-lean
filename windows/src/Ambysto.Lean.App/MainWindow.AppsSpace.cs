using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Ambysto.Lean.App;

/// <summary>The two read-only tables of the Apps and caches view (ASP-20 phase C2, intent/2026-10-21-asp-20-suggest-catalog.plan.md;
/// docs/spec/07): Space by app and Virtual disks, chosen with the selector above the Caches table. Free sees both (owner decision 15).
/// Nothing here ticks, deletes, moves or starts a program: a table is read off the UI thread and listed, and the only button, a
/// disk's Copy, puts the advice text on the clipboard.</summary>
public sealed partial class MainWindow
{
    private enum AppsSection
    {
        Caches,
        Space,
        Disks,
        Developer,
        Installers,
    }

    private AppsSection appsSection = AppsSection.Caches;
    private bool appsSectionsBuilding;

    // The Guide rows of the last Caches read: Space by app uses them when it is shown after that read, else it reads them itself.
    private IReadOnlyList<GuideRow>? appsGuides;

    // A section reads when first shown and on Refresh (and when the view is entered again); a shown one is not read again by a switch.
    private bool spaceIsRead;
    private bool disksIsRead;

    private int spaceVersion;
    private CancellationTokenSource? spaceCancel;
    private int disksVersion;
    private CancellationTokenSource? disksCancel;

    private void InitializeAppsSections()
    {
        SpaceTable.SetColumns(
        [
            new TableColumn(Text.ColumnName, 240),
            new TableColumn(Text.ColumnPublisher, 200),
            new TableColumn(Text.ColumnInstallFolder, 190),
            new TableColumn(Text.ColumnCaches, 100),
            new TableColumn(Text.ColumnTotal),
        ]);
        DisksTable.SetColumns(
        [
            new TableColumn(Text.ColumnName, 200),
            new TableColumn(Text.ColumnKind, 130),
            new TableColumn(Text.ColumnSize, 100),
            new TableColumn(Text.ColumnAdvice),
        ]);
        InitializeAppsDiagnosis();
        appsSectionsBuilding = true;
        try
        {
            AddAppsSectionTab(AppsSection.Caches, Text.SectionCaches, "AppsSectionCaches");
            AddAppsSectionTab(AppsSection.Space, Text.SectionSpaceByApp, "AppsSectionSpace");
            AddAppsSectionTab(AppsSection.Disks, Text.SectionVirtualDisks, "AppsSectionDisks");
            AddAppsSectionTab(AppsSection.Developer, Text.SectionDeveloperSpace, "AppsSectionDeveloper");
            AddAppsSectionTab(AppsSection.Installers, Text.SectionInstallersVersions, "AppsSectionInstallers");
        }
        finally
        {
            appsSectionsBuilding = false;
        }

        Closed += (_, _) =>
        {
            spaceCancel?.Cancel();
            disksCancel?.Cancel();
        };
        ApplyAppsSection();
    }

    private void AddAppsSectionTab(AppsSection section, string label, string automationId)
    {
        var tab = new RadioButton { Content = label, GroupName = "AppsSectionTabs", Style = LeanStyles.Tab, IsChecked = section == appsSection };
        AutomationProperties.SetName(tab, label);
        AutomationProperties.SetAutomationId(tab, automationId);
        tab.Checked += (_, _) =>
        {
            if (!appsSectionsBuilding && appsSection != section)
            {
                appsSection = section;
                ApplyAppsSection();
                ReadShownAppsSection(fresh: false);
            }
        };
        AppsSectionTabs.Children.Add(tab);
    }

    // Shows the chosen section's table, message and buttons and hides the others (collapsed: they are not in the UI tree). Clear,
    // Suggest and the report of the commands that ran belong to Caches alone.
    private void ApplyAppsSection()
    {
        var caches = appsSection == AppsSection.Caches;
        var space = appsSection == AppsSection.Space;
        var disks = appsSection == AppsSection.Disks;
        var developer = appsSection == AppsSection.Developer;
        var installers = appsSection == AppsSection.Installers;
        AppsTable.Visibility = Show(caches);
        AppsMessage.Visibility = Show(caches);
        AppsReportScroll.Visibility = Show(caches);
        AppsClearBar.Visibility = Show(caches);
        MoveInstallersButton.Visibility = Show(appsSection == AppsSection.Installers);
        SpaceTable.Visibility = Show(space);
        SpaceMessage.Visibility = Show(space && SpaceMessage.Text.Length > 0);
        DisksTable.Visibility = Show(disks);
        DisksMessage.Visibility = Show(disks && DisksMessage.Text.Length > 0);
        DeveloperTable.Visibility = Show(developer);
        DeveloperScroll.Visibility = Show(developer);
        DeveloperMessage.Visibility = Show(developer && DeveloperMessage.Text.Length > 0);
        InstallersTable.Visibility = Show(installers);
        InstallersScroll.Visibility = Show(installers);
        InstallersMessage.Visibility = Show(installers && InstallersMessage.Text.Length > 0);
    }

    private static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    // Reads the shown section when it has not been read (or when Refresh asks); Caches reads through RefreshAppsAsync.
    private void ReadShownAppsSection(bool fresh)
    {
        switch (appsSection)
        {
            case AppsSection.Space when fresh || !spaceIsRead:
                _ = ReadSpaceAsync(fresh);
                break;
            case AppsSection.Disks when fresh || !disksIsRead:
                _ = ReadDisksAsync();
                break;
            case AppsSection.Developer when fresh || !developerIsRead:
                _ = ReadDeveloperAsync();
                break;
            case AppsSection.Installers when fresh || !installersIsRead:
                _ = ReadInstallersAsync();
                break;
        }
    }

    // The view was entered again: the new sections are read again, the shown one now and the other when it is shown.
    private void StaleAppsSections()
    {
        spaceIsRead = false;
        disksIsRead = false;
        // Invalidate a hidden pending read as well as its cache. A completion
        // from before this entry must not mark the new entry's data as fresh.
        developerVersion++;
        developerCancel?.Cancel();
        installersVersion++;
        installersCancel?.Cancel();
        developerIsRead = false;
        installersIsRead = false;
    }

    private void SetSpaceMessage(string text)
    {
        SpaceMessage.Text = text;
        ApplyAppsSection();
    }

    private void SetDisksMessage(string text)
    {
        DisksMessage.Text = text;
        ApplyAppsSection();
    }

    // ---- Space by app -------------------------------------------------------------------------------------------------

    private async Task ReadSpaceAsync(bool fresh)
    {
        var version = ++spaceVersion;
        spaceCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        spaceCancel = cancel;
        spaceIsRead = false;
        SpaceTable.Clear();
        SetSpaceMessage(Text.ReadingSpaceByApp);
        try
        {
            var guides = fresh ? null : appsGuides;
            var context = guides is null ? await CurrentContextAsync(cancel.Token) : null;
            var rows = await Task.Run(async () =>
            {
                var apps = UninstallInventory.Read(new RegistryUninstallSource());
                var catalog = guides ?? await GuideRows.ReadAsync(context!, new RegistryUninstallSource(), cancel.Token).ConfigureAwait(false);
                return await SpaceByApp.ReadAsync(apps, catalog, cancel.Token).ConfigureAwait(false);
            }, cancel.Token);
            if (version != spaceVersion)
            {
                return;
            }

            SpaceTable.Batch(() =>
            {
                foreach (var row in rows)
                {
                    AddSpaceRow(row);
                }
            });
            spaceIsRead = true;
            SetSpaceMessage(SpaceTable.Count == 0 ? Text.NoAppsFound : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one, or the window closed.
        }
        catch (Exception)
        {
            if (version == spaceVersion)
            {
                SpaceTable.Clear();
                SetSpaceMessage(Text.SpaceByAppReadFailed);
            }
        }
    }

    // One app as one line: name, publisher, the install folder's size (with "estimated" beside the uninstall entry's own number),
    // the caches that belong to it, and the total. A missing value is an empty cell.
    private void AddSpaceRow(AppSpaceRow row)
    {
        var caches = row.CatalogRows.Sum(r => r.Bytes);
        SpaceTable.AddRow("SpaceRow", row,
        [
            LeanTable.NameLabel(row.Name),
            LeanTable.TextValue(row.Publisher),
            row.InstallBytes is { } install ? InstallCell(install, row.InstallEstimated) : LeanTable.Blank(),
            caches > 0 ? LeanTable.ValueLabel(ByteUnits.Format(caches), caches) : LeanTable.Blank(),
            LeanTable.ValueLabel(ByteUnits.Format(row.TotalBytes), row.TotalBytes),
        ]);
    }

    // The size in one text and, when it is the uninstall entry's estimate, the word "estimated" in a quieter one beside it.
    private static TableCell InstallCell(long bytes, bool estimated)
    {
        var size = ByteUnits.Format(bytes);
        if (!estimated)
        {
            return LeanTable.ValueLabel(size, bytes);
        }

        var name = Text.RowName(size, Text.Estimated);
        var sizeText = new TextBlock { Text = size, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetName(sizeText, size);
        var word = new TextBlock { Text = Text.Estimated, Style = LeanStyles.Note, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(word, Text.Estimated);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(sizeText);
        line.Children.Add(word);
        AutomationProperties.SetName(line, name);
        ToolTipService.SetToolTip(line, name);
        return new TableCell(line, () => bytes, name);
    }

    // ---- Virtual disks ------------------------------------------------------------------------------------------------

    private async Task ReadDisksAsync()
    {
        var version = ++disksVersion;
        disksCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        disksCancel = cancel;
        disksIsRead = false;
        DisksTable.Clear();
        SetDisksMessage(Text.ReadingVirtualDisks);
        try
        {
            var context = await CurrentContextAsync(cancel.Token);
            var rows = await VirtualDisks.ReadAsync(context, cancel.Token);
            if (version != disksVersion)
            {
                return;
            }

            DisksTable.Batch(() =>
            {
                foreach (var row in rows)
                {
                    AddDiskRow(row);
                }
            });
            disksIsRead = true;
            SetDisksMessage(DisksTable.Count == 0 ? Text.NoVirtualDisks : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one, or the window closed.
        }
        catch (Exception)
        {
            if (version == disksVersion)
            {
                DisksTable.Clear();
                SetDisksMessage(Text.VirtualDisksReadFailed);
            }
        }
    }

    private static string KindText(VirtualDiskKind kind) => kind switch
    {
        VirtualDiskKind.Wsl => Text.KindWsl,
        VirtualDiskKind.DockerDesktop => Text.KindDockerDesktop,
        _ => Text.KindVirtualBox,
    };

    // One disk as one line: name (the disk file's path as its tooltip), kind, size and the advice, whose full text is its tooltip, with a
    // Copy button that puts the advice on the clipboard.
    private void AddDiskRow(VirtualDiskRow row)
    {
        var name = LeanTable.NameLabel(row.Name);
        ToolTipService.SetToolTip(name.Element, row.Path);
        DisksTable.AddRow("DiskRow", row,
        [
            name,
            LeanTable.TextValue(KindText(row.Kind)),
            LeanTable.ValueLabel(ByteUnits.Format(row.Bytes), row.Bytes),
            AdviceCell(row.Advice),
        ]);
    }

    private TableCell AdviceCell(string advice)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = advice, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(text, advice);
        ToolTipService.SetToolTip(text, advice);
        AutomationProperties.SetHelpText(text, advice);
        line.Children.Add(text);
        if (advice.Length > 0)
        {
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new Button { Content = Text.CopyAdvice, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(copy, "DiskCopyAdvice");
            AutomationProperties.SetName(copy, Text.CopyAdvice);
            copy.Click += (_, _) => CopyAdvice(advice);
            Grid.SetColumn(copy, 1);
            line.Children.Add(copy);
        }

        AutomationProperties.SetName(line, advice);
        return new TableCell(line, () => advice, advice);
    }

    // The advice text, as it is shown, on the clipboard; the status line says so. Nothing is run.
    private void CopyAdvice(string advice)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(advice);
            Clipboard.SetContent(package);
            StatusText.Text = Text.AdviceCopied;
        }
        catch (Exception)
        {
            StatusText.Text = Text.AdviceCopyFailed;
            return;
        }

        try
        {
            Clipboard.Flush(); // the text stays after Lean closes
        }
        catch (Exception)
        {
            // The text is on the clipboard while Lean runs; keeping it afterwards is a courtesy.
        }
    }
}
