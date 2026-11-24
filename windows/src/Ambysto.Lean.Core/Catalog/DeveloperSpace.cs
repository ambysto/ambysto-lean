using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Uninstall;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>A known local store: its measured share, or null when incomplete or unavailable. Diagnosis grants no cleanup authority.</summary>
public sealed record DeveloperSpaceRow(string Name, string Category, string Path, long? Bytes, string Note);

/// <summary>Read-only diagnosis of explicitly known developer and application stores (ASP-30). No project discovery or cleanup.</summary>
public static class DeveloperSpace
{
    private sealed record Store(string Name, string Category, string Path, string Note)
    {
        public bool Unknown { get; init; }
    }

    /// <summary>Measures known plain roots off the caller's thread. The inventory parameter is reserved for future ownership
    /// attribution; it never expands this exact root list. Missing/empty roots are absent; unsafe/incomplete roots have unknown sizes.</summary>
    public static Task<IReadOnlyList<DeveloperSpaceRow>> ReadAsync(PathContext context, IUninstallSource uninstall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uninstall);
        return Task.Run<IReadOnlyList<DeveloperSpaceRow>>(() => ReadAsync(context, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<DeveloperSpaceRow>> ReadAsync(PathContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stores = new List<Store>();
        foreach (var store in KnownStores(context))
        {
            token.ThrowIfCancellationRequested();
            if (DescribeRoot(store, context) is { } described && !stores.Any(s => Same(s.Path, described.Path)))
            {
                stores.Add(described);
            }
        }

        // Choose only outermost declared roots if a supplied context places one
        // known store inside another. Independent siblings keep catalog order.
        var roots = stores.Where(s => !stores.Any(other => !Same(s.Path, other.Path) && Inside(s.Path, other.Path))).ToList();
        var claimed = new HashSet<(ulong Volume, UInt128 File)>();
        var rows = new List<DeveloperSpaceRow>();
        foreach (var store in roots)
        {
            token.ThrowIfCancellationRequested();
            if (store.Unknown)
            {
                rows.Add(new DeveloperSpaceRow(store.Name, store.Category, store.Path, null, store.Note));
                continue;
            }

            try
            {
                var tree = await new Scanner().ScanAsync(store.Path, null, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (tree.Cancelled || tree.Coverage.UnreadableCount != 0 || tree.Coverage.CloudNotDownloadedCount != 0)
                {
                    rows.Add(UnknownRow(store, Text.DeveloperSpaceIncomplete));
                    continue;
                }

                var pending = new Stack<ScanNode>();
                var identities = new HashSet<(ulong Volume, UInt128 File)>();
                pending.Push(tree.Root);
                long bytes = 0;
                var shared = false;
                var complete = true;
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var node = pending.Pop();
                    if ((node.Flags & (NodeMarks.Link | NodeMarks.CloudNotDownloaded | NodeMarks.Unreadable)) != 0 || node.Stamp is not { } stamp)
                    {
                        complete = false;
                        break;
                    }

                    if (node.IsFolder)
                    {
                        foreach (var child in node.Children) { pending.Push(child); }
                        continue;
                    }

                    if (stamp.FileId == 0)
                    {
                        complete = false;
                        break;
                    }

                    if (node.AllocatedBytes <= 0) { continue; }
                    var identity = (stamp.VolumeSerial, stamp.FileId);
                    if (!identities.Add(identity)) { continue; }
                    if (claimed.Contains(identity)) { shared = true; }
                    else { bytes = checked(bytes + node.AllocatedBytes); }
                }

                if (!complete)
                {
                    rows.Add(UnknownRow(store, Text.DeveloperSpaceIncomplete));
                    continue;
                }
                if (bytes <= 0 && !shared) { continue; }
                // Do not retain trees or claim any identity from an unknown row.
                claimed.UnionWith(identities);
                var note = shared ? Text.TwoSentences(store.Note, Text.DeveloperSpaceSharedBytes) : store.Note;
                rows.Add(new DeveloperSpaceRow(store.Name, store.Category, store.Path, bytes, note));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                rows.Add(UnknownRow(store, Text.DeveloperSpaceUnknownRoot));
            }
        }

        return rows;
    }

    private static IEnumerable<Store> KnownStores(PathContext c)
    {
        yield return new(Text.DeveloperRustup, Text.DeveloperCategoryToolchains, Path.Combine(c.ProfileFolder, ".rustup"), Text.DeveloperNoteTools);
        yield return new(Text.DeveloperCargoRegistry, Text.DeveloperCategoryPackages, Path.Combine(c.ProfileFolder, ".cargo", "registry"), Text.DeveloperNotePackages);
        yield return new(Text.DeveloperCargoBin, Text.DeveloperCategoryTools, Path.Combine(c.ProfileFolder, ".cargo", "bin"), Text.DeveloperNoteTools);
        yield return new(Text.DeveloperSharedCache, Text.DeveloperCategoryModels, Path.Combine(c.ProfileFolder, ".cache"), Text.DeveloperNoteModels);
        yield return new(Text.DeveloperCodex, Text.DeveloperCategoryAccounts, Path.Combine(c.ProfileFolder, ".codex"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperNugetPackages, Text.DeveloperCategoryPackages, Path.Combine(c.ProfileFolder, ".nuget", "packages"), Text.DeveloperNotePackages);
        yield return new(Text.DeveloperUserPrograms, Text.DeveloperCategoryApplications, Path.Combine(c.LocalAppData, "Programs"), Text.DeveloperNoteApplications);
        yield return new(Text.DeveloperStorePackages, Text.DeveloperCategoryAccounts, Path.Combine(c.LocalAppData, "Packages"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperCapcutApps, Text.DeveloperCategoryApplications, Path.Combine(c.LocalAppData, "CapCut", "Apps"), Text.DeveloperNoteApplications);
        yield return new(Text.DeveloperCapcutData, Text.DeveloperCategoryAccounts, Path.Combine(c.LocalAppData, "CapCut", "User Data"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperLark, Text.DeveloperCategoryApplications, Path.Combine(c.LocalAppData, "Lark"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperLarkInternational, Text.DeveloperCategoryApplications, Path.Combine(c.LocalAppData, "LarkInternational"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperHermes, Text.DeveloperCategoryAccounts, Path.Combine(c.LocalAppData, "hermes"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperNotion, Text.DeveloperCategoryAccounts, Path.Combine(c.RoamingAppData, "Notion"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperOrca, Text.DeveloperCategoryAccounts, Path.Combine(c.RoamingAppData, "Orca"), Text.DeveloperNoteAccounts);
        yield return new(Text.DeveloperNpmTools, Text.DeveloperCategoryTools, Path.Combine(c.RoamingAppData, "npm"), Text.DeveloperNotePackages);
        yield return new(Text.DeveloperChromeModel, Text.DeveloperCategoryModels, Path.Combine(c.LocalAppData, "Google", "Chrome", "User Data", "OptGuideOnDeviceModel"), Text.DeveloperNoteModels);
        yield return new(Text.DeveloperNpmTools, Text.DeveloperCategoryTools, Path.Combine(c.LocalAppData, "npm"), Text.DeveloperNotePackages);
    }

    private static DeveloperSpaceRow UnknownRow(Store store, string reason) =>
        new(store.Name, store.Category, store.Path, null, Text.TwoSentences(store.Note, reason));

    private static Store? DescribeRoot(Store store, PathContext context)
    {
        var full = GuideRows.FullLocalPath(store.Path);
        if (full is null || CanonicalPath.Of(full) is not { } canonical) { return null; }
        var described = store with { Path = canonical };
        try
        {
            var rootAttributes = File.GetAttributes(full);
            if ((rootAttributes & FileAttributes.Directory) == 0) { return null; }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return described with { Unknown = true, Note = Text.TwoSentences(store.Note, Text.DeveloperSpaceUnknownRoot) };
        }

        foreach (var sync in context.SyncRoots)
        {
            if (CanonicalPath.Of(sync.Path) is { } root && (Inside(canonical, root) || Inside(root, canonical)))
            {
                return described with { Unknown = true, Note = Text.TwoSentences(store.Note, Text.DeveloperSpaceUnknownRoot) };
            }
        }

        try
        {
            // Ancestors matter: an ordinary leaf below a junction is still a redirected store.
            for (string? cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            {
                var attributes = File.GetAttributes(cursor);
                const FileAttributes unavailable = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & unavailable) != 0)
                {
                    return described with { Unknown = true, Note = Text.TwoSentences(store.Note, Text.DeveloperSpaceUnknownRoot) };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return described with { Unknown = true, Note = Text.TwoSentences(store.Note, Text.DeveloperSpaceUnknownRoot) };
        }

        return described;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string root) => CanonicalPath.Depth(root, path) >= 0;
}
