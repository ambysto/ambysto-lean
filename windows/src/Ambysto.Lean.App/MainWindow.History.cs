using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The History view (plan group 11, docs/spec/05 and 06): the journal's sessions, newest first, each with its entries and
/// Put back for the entries still in the Recycle Bin. Free shows the last 30 days; Pro shows everything. A clean command's run
/// (ASP-20, the command log) is its own entry beside the sessions, with the command and the sizes and no Put back: it did not use the
/// Recycle Bin.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan FreeHistory = TimeSpan.FromDays(30);

    private sealed record HistoryEntryModel(JournalEntry Entry, bool CanPutBack);

    private sealed record HistorySessionModel(string SessionId, DateTimeOffset When, LeanModule Module, int Count, IReadOnlyList<HistoryEntryModel> Entries, bool CanPutBack);

    // One run of a tool's own clean command, from commands.jsonl (docs/spec/05).
    private sealed record HistoryCommandModel(CommandLogEntry Entry);

    private int historyVersion;

    private void InitializeHistory()
    {
        HistoryTitle.Text = Text.History;
        HistoryList.ElementClearing += OnHistoryElementClearing;
    }

    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        ShowView(HistoryView);
        await ReloadHistoryAsync();
    }

    private static string ModuleName(LeanModule module) => module switch
    {
        LeanModule.LargeOld => Text.LargeOld,
        LeanModule.Duplicates => Text.Duplicates,
        LeanModule.Uninstall => Text.Uninstall,
        LeanModule.Installers => Text.SectionInstallersVersions,
        LeanModule.Temp => Text.TemporaryFiles, // hard review 2026-10-18: a Temp session was listed as "Map"
        _ => Text.ModuleMap,
    };

    private static string ResultText(JournalResult result) => result switch
    {
        JournalResult.Trashed => Text.ResultTrashed,
        JournalResult.Skipped => Text.ResultSkipped,
        JournalResult.Restored => Text.ResultRestored,
        JournalResult.RestoreFailed => Text.ResultRestoreFailed,
        _ => Text.DeletedPermanentlyByWindows,
    };

    // The journal is read off the UI thread; the latest request wins.
    private async Task ReloadHistoryAsync()
    {
        var version = ++historyVersion;
        var pro = entitlement.IsPro;
        HistoryNote.Text = pro ? string.Empty : Text.HistoryFreeLimit;
        HistoryNote.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var sessions = await Task.Run(() => BuildHistory(pro));
            if (version != historyVersion)
            {
                return;
            }

            HistoryList.ItemsSource = sessions;
            if (sessions.Count == 0 && pro)
            {
                HistoryNote.Text = Text.HistoryEmpty;
                HistoryNote.Visibility = Visibility.Visible;
            }
            else if (sessions.Count == 0)
            {
                HistoryNote.Text = Text.HistoryEmpty + " " + Text.HistoryFreeLimit;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            if (version == historyVersion)
            {
                HistoryList.ItemsSource = null;
                HistoryNote.Text = Text.HistoryReadFailed;
                HistoryNote.Visibility = Visibility.Visible;
            }
        }
    }

    private static List<object> BuildHistory(bool pro)
    {
        var all = JournalStore.Open(JournalStore.DefaultPath).Read();
        var back = all.Where(e => e.Result == JournalResult.Restored && e.RefersTo is not null).Select(e => e.RefersTo!).ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow - FreeHistory;
        var sessions = new List<HistorySessionModel>();
        foreach (var group in all.GroupBy(e => e.SessionId, StringComparer.Ordinal))
        {
            // Free shows the sessions that started within 30 days (by the session's first entry); a shown session shows all of it.
            if (!pro && group.Min(e => e.Time) < cutoff)
            {
                continue;
            }

            var entries = group.Select(e => new HistoryEntryModel(e, e.Result == JournalResult.Trashed && !back.Contains(e.Id))).ToList();
            var moved = group.Count(e => e.Result is JournalResult.Trashed or JournalResult.Skipped or JournalResult.Lost);
            sessions.Add(new HistorySessionModel(group.Key, group.Min(e => e.Time), group.First().Module, moved, entries, entries.Any(e => e.CanPutBack)));
        }

        var items = sessions.Select(s => (s.When, Model: (object)s)).ToList();
        foreach (var run in ReadCommandRuns().Where(r => pro || r.Time >= cutoff))
        {
            items.Add((run.Time, new HistoryCommandModel(run)));
        }

        return [.. items.OrderByDescending(i => i.When).Select(i => i.Model)];
    }

    // The command log is read apart from the journal: a log that cannot be read leaves out the runs, never the sessions.
    private static IReadOnlyList<CommandLogEntry> ReadCommandRuns()
    {
        try
        {
            return CommandLog.Open(CommandLog.DefaultPath).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    // The repeater builds a session's elements when it realises the session and drops them when it recycles it.
    private void OnHistoryElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is ContentControl host && sender.ItemsSourceView is { } items && args.Index < items.Count)
        {
            host.Content = items.GetAt(args.Index) switch
            {
                HistorySessionModel session => CreateHistorySession(session),
                HistoryCommandModel run => TryCreateHistoryCommand(run),
                _ => null,
            };
        }
    }

    private static void OnHistoryElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is ContentControl host)
        {
            host.Content = null;
        }
    }

    private static string SessionName(HistorySessionModel model) =>
        Text.HistorySessionName(model.When.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), ModuleName(model.Module), model.Count);

    private GroupPanel CreateHistorySession(HistorySessionModel model)
    {
        var name = SessionName(model);
        var panel = new GroupPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(panel, "HistorySession");
        AutomationProperties.SetName(panel, name);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock { Text = name, Style = LeanStyles.Medium, VerticalAlignment = VerticalAlignment.Center });
        if (model.CanPutBack)
        {
            var all = new Button { Content = Text.PutBackSessionButton, Tag = model };
            AutomationProperties.SetAutomationId(all, "PutBackSessionHistory");
            all.Click += OnPutBackSessionHistoryClick;
            header.Children.Add(all);
        }

        panel.Children.Add(header);
        var table = new LeanTable(embedded: true);
        table.SetColumns(
        [
            new TableColumn(Text.ColumnItem),
            new TableColumn(Text.ColumnFolder, 290),
            new TableColumn(Text.ColumnResult, 190),
            new TableColumn(Text.ColumnReason, 240),
            new TableColumn(string.Empty, 100),
        ]);
        foreach (var entry in model.Entries)
        {
            var path = entry.Entry.OriginalPath;
            var reason = entry.Entry.Result == JournalResult.Lost && entry.Entry.Reason == Text.DeletedPermanentlyByWindows ? null : entry.Entry.Reason;
            TableCell action = LeanTable.Empty();
            if (entry.CanPutBack)
            {
                var back = new Button { Content = Text.PutBackButton, Tag = entry };
                AutomationProperties.SetAutomationId(back, "PutBackEntry");
                back.Click += OnPutBackEntryClick;
                action = LeanTable.ButtonCell(back);
            }

            table.AddRow("HistoryEntry", entry,
            [
                LeanTable.Label(Path.GetFileName(path) is { Length: > 0 } file ? file : path, toolTip: path),
                LeanTable.FolderLabel(Path.GetDirectoryName(path) ?? string.Empty),
                LeanTable.Label(ResultText(entry.Entry.Result)),
                reason is null ? LeanTable.Empty() : LeanTable.Label(reason),
                action,
            ]);
        }

        panel.Children.Add(table);
        return panel;
    }

    // History never throws on an entry: a command run that cannot be drawn is left out.
    private static GroupPanel? TryCreateHistoryCommand(HistoryCommandModel model)
    {
        try
        {
            return CreateHistoryCommand(model);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }

    // A command run: one row with the time, what happened, the exact command (the program's full path and its arguments) and the sizes
    // before and after. No Put back: the tool's own command deleted the files for good.
    private static GroupPanel CreateHistoryCommand(HistoryCommandModel model)
    {
        var run = model.Entry;
        var app = AppCatalog.Entries.FirstOrDefault(e => e.Id == run.EntryId)?.AppName ?? run.EntryId;
        var what = run.Result switch
        {
            CleanResult.Cleaned => Text.HistoryCommandDone(app),
            CleanResult.TimedOut => Text.HistoryCommandTimedOut(app),
            _ => Text.HistoryCommandFailed(app, run.ExitCode ?? -1),
        };
        var command = CommandLineText(run.Program, run.Arguments);
        var panel = new GroupPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(panel, "HistoryCommand");
        AutomationProperties.SetName(panel, what);

        var table = new LeanTable(embedded: true);
        table.SetColumns(
        [
            new TableColumn(Text.ColumnWhen, 130),
            new TableColumn(Text.ColumnWhat, 230),
            new TableColumn(Text.ColumnCommand),
            new TableColumn(Text.ColumnBefore, 90),
            new TableColumn(Text.ColumnAfter, 90),
        ]);
        table.AddRow("HistoryCommandEntry", run,
        [
            LeanTable.Label(run.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            LeanTable.Label(what),
            LeanTable.Label(command, toolTip: command),
            LeanTable.Label(ByteUnits.Format(run.BytesBefore)),
            run.BytesAfter is { } after ? LeanTable.Label(ByteUnits.Format(after)) : LeanTable.Empty(),
        ]);
        panel.Children.Add(table);
        return panel;
    }

    private async void OnPutBackEntryClick(object sender, RoutedEventArgs e)
    {
        if (moveBusy || cleanBusy || checkingRoot || sender is not Button { Tag: HistoryEntryModel model })
        {
            return;
        }

        await PutBackFromHistoryAsync(model.Entry.Module, put => [put.Restore(model.Entry)]);
    }

    private async void OnPutBackSessionHistoryClick(object sender, RoutedEventArgs e)
    {
        if (moveBusy || cleanBusy || checkingRoot || sender is not Button { Tag: HistorySessionModel model })
        {
            return;
        }

        await PutBackFromHistoryAsync(model.Module, put => put.RestoreSession(model.SessionId));
    }

    // Put back runs on a background thread, like a move; the view reads the journal again and the volume card is refreshed.
    private async Task PutBackFromHistoryAsync(LeanModule module, Func<PutBack, IReadOnlyList<PutBackOutcome>> restore)
    {
        moveBusy = true;
        putBackRunning = true;
        UpdateMoveControls();
        var userFolders = userProtectedFolders;
        try
        {
            var outcomes = await Task.Run(() =>
            {
                HoldPutBack(); // Debug only: --move-delay
                var put = new PutBack(JournalStore.Open(JournalStore.DefaultPath), new KitItemGate(module, NoModuleItems, userFolders));
                return restore(put);
            });
            var restored = outcomes.Count(o => o.Restored);
            if (!closeAfterMove) // a pending close keeps its own line
            {
                StatusText.Text = outcomes.Count == 1 && !outcomes[0].Restored
                    ? Text.LabelValue(outcomes[0].Entry.OriginalPath, outcomes[0].Reason ?? string.Empty)
                    : Text.PutBackSummary(restored, outcomes.Count);
                _ = LoadVolumeCardAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = Text.PutBackRunFailed(ex.Message);
        }
        finally
        {
            moveBusy = false;
            putBackRunning = false;
            UpdateMoveControls();
        }

        if (!CloseIfHeldForMove())
        {
            await ReloadHistoryAsync();
        }
    }
}
