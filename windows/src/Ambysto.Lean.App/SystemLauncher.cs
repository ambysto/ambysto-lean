using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Ambysto.Lean.App;

/// <summary>The one place that opens Settings or File Explorer (CLAUDE.md Windows rules), always through
/// <c>Windows.System.Launcher</c>; nothing else starts a process.</summary>
internal static partial class SystemLauncher
{
    /// <summary>ASP-11: opens a validated HTTPS page in the default browser, never a package or another URI scheme.</summary>
    internal static Task<bool> OpenWebPageAsync(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Ambysto.Lean.Core.Updates.UpdateChecker.TryPage(page.OriginalString, out var validated))
        {
            return Task.FromResult(false);
        }
#if DEBUG
        if (qaWebLaunch is { } launch) return launch(validated!);
#endif
        return Windows.System.Launcher.LaunchUriAsync(validated).AsTask();
    }

    /// <summary>Opens a Settings page such as <c>ms-settings:appsfeatures</c>.</summary>
    internal static async Task<bool> OpenSettingsAsync(Uri page)
    {
        if (NextQaStep() is { } step)
        {
            // QA only (--settings-launch): the step's wait and answer stand in for the launch; nothing is opened.
            await Task.Delay(step.Delay).ConfigureAwait(true);
            return step.Succeeds;
        }

        return await Windows.System.Launcher.LaunchUriAsync(page).AsTask().ConfigureAwait(true);
    }

    private static readonly object QaLock = new();
    private static Queue<(TimeSpan Delay, bool Succeeds)> qaSteps = new();

    /// <summary>QA only (<c>--settings-launch</c>): the next calls of <see cref="OpenSettingsAsync"/> take these steps in turn; once they
    /// run out the launcher behaves normally.</summary>
    internal static void UseQaSettingsLaunch(IEnumerable<(TimeSpan Delay, bool Succeeds)> steps)
    {
        lock (QaLock)
        {
            qaSteps = new Queue<(TimeSpan, bool)>(steps);
        }
    }

    private static (TimeSpan Delay, bool Succeeds)? NextQaStep()
    {
        lock (QaLock)
        {
            return qaSteps.Count > 0 ? qaSteps.Dequeue() : null;
        }
    }

    /// <summary>Opens the Recycle Bin in File Explorer (probed 2026-10-17: <c>LaunchUriAsync</c> refuses the <c>shell:</c> scheme,
    /// <c>LaunchFolderPathAsync</c> opens it). It only shows the bin: Lean never empties it.</summary>
    internal static Task<bool> OpenRecycleBinAsync() => Windows.System.Launcher.LaunchFolderPathAsync("shell:RecycleBinFolder").AsTask();

    /// <summary>ASP-16: opens <paramref name="folder"/> itself in File Explorer, showing its contents (the Uninstall table's magnifier).</summary>
    internal static Task<bool> OpenFolderAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return Windows.System.Launcher.LaunchFolderPathAsync(folder).AsTask();
    }

    /// <summary>Opens the folder that holds <paramref name="path"/> in File Explorer with the item selected; when the item
    /// cannot be selected (a path the storage API refuses), opens the folder alone.</summary>
    internal static async Task<bool> RevealInExplorerAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            // A volume root has no parent: show the root itself.
            return await Windows.System.Launcher.LaunchFolderPathAsync(path).AsTask().ConfigureAwait(true);
        }

        try
        {
            IStorageItem item = Directory.Exists(path)
                ? await StorageFolder.GetFolderFromPathAsync(path).AsTask().ConfigureAwait(true)
                : await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(true);
            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(item);
            return await Windows.System.Launcher.LaunchFolderPathAsync(parent, options).AsTask().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            return await Windows.System.Launcher.LaunchFolderPathAsync(parent).AsTask().ConfigureAwait(true);
        }
    }
}
