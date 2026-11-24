using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The user's protected folders in Settings (v1 additions, "UI contract for group 4"; docs/spec/02 Windows note). The
/// list lives in <see cref="LeanSettings"/> (Core, an append-only file); this file lists it, lets the user add a folder through the
/// system folder picker or remove one, saves at once, and hands the folders to every gate (<c>KitItemGate</c>) and every hint (the
/// <see cref="PathContext"/>). It decides nothing about safety: the Core's gate refuses a protected item.</summary>
public sealed partial class MainWindow
{
    private LeanSettings leanSettings = new();
    private bool protectedFoldersBusy;
    private bool hintsStale;

    // The settings file was there but could not be read: Add and Remove stay off (a new line would hide the unread list) until a
    // later load succeeds, tried each time Settings opens.
    private bool settingsUnreadable;

    // A copy, replaced as a whole when the list changes, so a worker thread that captured it never sees it change.
    private IReadOnlyList<string> userProtectedFolders = [];

    // Read at start-up, before any scan can begin (a move needs a scan), so the first gate already has the folders. A file that is
    // there but unreadable is said once in Settings and in the status line, and is left as it is (the Core never overwrites it).
    private void InitializeProtectedFolders()
    {
        ProtectedFoldersHeading.Text = Text.ProtectedFolders;
        ProtectedFoldersNote.Text = Text.ProtectedFoldersNote;
        AutomationProperties.SetName(ProtectedFoldersList, Text.ProtectedFolders);
        AddProtectedFolderButton.Content = Text.AddProtectedFolder;
        RemoveProtectedFolderButton.Content = Text.RemoveProtectedFolder;

        var loaded = LeanSettings.Load(LeanSettings.DefaultPath);
        leanSettings = loaded.Settings;
        userProtectedFolders = [.. leanSettings.ProtectedFolders];
        settingsUnreadable = loaded.WasBroken;
        RebuildProtectedFoldersList();
        if (loaded.WasBroken)
        {
            ProtectedFoldersMessage.Text = Text.SettingsUnreadable;
            StatusText.Text = Text.SettingsUnreadable;
        }
    }

    // Settings opened: a file that could not be read at start-up (or at the last try) is read again. Success puts the list in effect
    // for every gate and hint; failure keeps Add and Remove off and says so.
    private async Task RetryUnreadableSettingsAsync()
    {
        if (!settingsUnreadable || protectedFoldersBusy)
        {
            return;
        }

        protectedFoldersBusy = true;
        UpdateProtectedFolderControls();
        try
        {
            LoadResult loaded;
            try
            {
                loaded = await Task.Run(() => LeanSettings.Load(LeanSettings.DefaultPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                ProtectedFoldersMessage.Text = Text.SettingsUnreadable;
                return;
            }

            if (loaded.WasBroken)
            {
                ProtectedFoldersMessage.Text = Text.SettingsUnreadable;
                return;
            }

            leanSettings = loaded.Settings;
            settingsUnreadable = false;
            ProtectedFoldersMessage.Text = string.Empty;
            userProtectedFolders = [.. leanSettings.ProtectedFolders];
            RebuildProtectedFoldersList();
            RefreshHintsInViews();
        }
        finally
        {
            protectedFoldersBusy = false;
            UpdateProtectedFolderControls();
        }
    }

    // The context every hint and finder is judged against: this user's folders, plus the protected ones, as of now.
    private async Task<PathContext> CurrentContextAsync(CancellationToken token = default)
    {
        var current = pathContext ?? await Task.Run(PathContext.Current, token);
        return pathContext = current with { UserProtectedFolders = userProtectedFolders };
    }

    private void RebuildProtectedFoldersList()
    {
        ProtectedFoldersList.Items.Clear();
        foreach (var folder in userProtectedFolders)
        {
            var item = new ListViewItem { Content = new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = folder };
            AutomationProperties.SetAutomationId(item, "ProtectedFolder");
            AutomationProperties.SetName(item, folder);
            ProtectedFoldersList.Items.Add(item);
        }

        UpdateProtectedFolderControls();
    }

    private void UpdateProtectedFolderControls()
    {
        AddProtectedFolderButton.IsEnabled = !protectedFoldersBusy && !settingsUnreadable;
        RemoveProtectedFolderButton.IsEnabled = !protectedFoldersBusy && !settingsUnreadable && ProtectedFoldersList.SelectedItem is ListViewItem;
    }

    private void OnProtectedFolderSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProtectedFolderControls();

    // The system folder picker, initialised with the window handle (an unpackaged app needs it): the chosen folder's path, or null when
    // the user closed it. Settings' Add and Overview's Scan a folder (ASP-14) both use it; a picker that cannot open throws COMException.
    private async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // The Core decides whether the chosen path may be added.
    private async void OnAddProtectedFolderClick(object sender, RoutedEventArgs e)
    {
        if (protectedFoldersBusy || settingsUnreadable)
        {
            return;
        }

        protectedFoldersBusy = true;
        UpdateProtectedFolderControls();
        try
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return; // the user closed the picker: nothing changes
            }

            var result = ProtectedFolders.Add(leanSettings, folder);
            ProtectedFoldersMessage.Text = result switch
            {
                AddResult.Added => Text.ProtectedFolderAdded,
                AddResult.AlreadyCovered => Text.ProtectedFolderAlreadyCovered,
                AddResult.NotAFolder => Text.ProtectedFolderNotAFolder,
                _ => Text.ProtectedFolderNotUsable,
            };
            if (result == AddResult.Added)
            {
                await ProtectedFoldersChangedAsync();
            }
        }
        catch (COMException)
        {
            // The picker could not open: nothing was added.
            ProtectedFoldersMessage.Text = Text.FolderPickerFailed;
        }
        finally
        {
            protectedFoldersBusy = false;
            UpdateProtectedFolderControls();
        }
    }

    private async void OnRemoveProtectedFolderClick(object sender, RoutedEventArgs e)
    {
        if (protectedFoldersBusy || settingsUnreadable || ProtectedFoldersList.SelectedItem is not ListViewItem { Tag: string folder })
        {
            return;
        }

        protectedFoldersBusy = true;
        UpdateProtectedFolderControls();
        try
        {
            if (ProtectedFolders.Remove(leanSettings, folder))
            {
                ProtectedFoldersMessage.Text = Text.ProtectedFolderRemoved; // a failed save replaces it with SettingsNotSaved
                await ProtectedFoldersChangedAsync();
            }
        }
        finally
        {
            protectedFoldersBusy = false;
            UpdateProtectedFolderControls();
        }
    }

    // The list changed in memory: every gate and hint uses it from now on, then it is saved at once. A save that fails says so and
    // keeps the list in memory for this session.
    private async Task ProtectedFoldersChangedAsync()
    {
        userProtectedFolders = [.. leanSettings.ProtectedFolders];
        RebuildProtectedFoldersList();
        RefreshHintsInViews();

        var settings = leanSettings;
        try
        {
            await Task.Run(() => settings.Save(LeanSettings.DefaultPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ProtectedFoldersMessage.Text = Text.SettingsNotSaved;
        }
    }

    // The map's cached hints are dropped (RelayoutAsync clears them before its next layout); the open Large and old and Temporary
    // files views list again; the Duplicates groups are judged again and the leftover candidates worked out again, so that no row
    // looks movable while the gate would refuse it as protected.
    private void RefreshHintsInViews()
    {
        hintsStale = true;
        _ = RelayoutAsync();
        if (tree is not null)
        {
            _ = RefreshResultsAsync();
        }

        _ = RefreshDuplicateHintsAsync();
        RefreshLeftovers();
        if (largeOldSuggesting)
        {
            // A suggestion judged by the old list could tick a row that is protected now: it goes, and the list is made again when shown.
            EndLargeOldSuggestion();
            LargeOldTable.Clear();
        }

        if (LargeOldView.Visibility == Visibility.Visible)
        {
            _ = RefreshLargeOldAsync();
        }

        if (TempView.Visibility == Visibility.Visible)
        {
            _ = RefreshTempAsync();
        }
    }
}
