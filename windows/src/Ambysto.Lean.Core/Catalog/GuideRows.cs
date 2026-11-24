using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Uninstall;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>One shown-only row (ASP-20 decision 13): its catalog entry, its title, the folder it names, the bytes measured
/// there and what its button does. Nothing is ticked or deleted from it.</summary>
public sealed record GuideRow(CatalogEntry Entry, string Title, string Folder, long Bytes, GuideAction Action)
{
    /// <summary>Phase C: for an electron-updater row, the key of the uninstall entry that named its folder; null otherwise.</summary>
    public string? UninstallKeyName { get; init; }
}

/// <summary>Reads the Guide rows (plan "Phase A2"): browsers' caches, CapCut's older versions and electron-updater's pending
/// folders. Read-only; starts no process.</summary>
public static class GuideRows
{
    private const int LocalStateLimit = 4 * 1024 * 1024;
    private const int UpdateYmlLimit = 64 * 1024;
    private const uint AttributeOffline = 0x1000;
    private const string UpdaterKey = "updaterCacheDirName:";
    private const uint GenericRead = 0x80000000;
    private const uint FlagOpenNoRecall = 0x00100000;

    private static readonly (string Id, string[] Vendor)[] Browsers =
    [
        ("chrome", ["Google", "Chrome"]),
        ("edge", ["Microsoft", "Edge"]),
    ];

    private static readonly string[][] ProfileCaches = [["Cache", "Cache_Data"], ["Code Cache"], ["GPUCache"]];
    private static readonly string[] SharedCaches = ["ShaderCache", "GrShaderCache"];

    /// <summary>The rows, in catalog order; a row measuring 0 bytes is not listed.</summary>
    public static Task<IReadOnlyList<GuideRow>> ReadAsync(PathContext context, IUninstallSource uninstall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uninstall);
        return Task.Run<IReadOnlyList<GuideRow>>(() => Read(context, uninstall, cancellationToken), cancellationToken);
    }

    private static List<GuideRow> Read(PathContext context, IUninstallSource uninstall, CancellationToken cancellationToken)
    {
        var rows = new List<GuideRow>();
        foreach (var (id, vendor) in Browsers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadBrowser(id, vendor, context, cancellationToken) is { } browser)
            {
                rows.Add(browser);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ReadCapCut(context, cancellationToken) is { } capCut)
        {
            rows.Add(capCut);
        }

        rows.AddRange(ReadUpdaters(context, uninstall, cancellationToken));
        return rows;
    }

    private static GuideRow? ReadBrowser(string id, string[] vendor, PathContext context, CancellationToken cancellationToken)
    {
        var userData = Path.Combine([context.LocalAppData, .. vendor, "User Data"]);
        var profiles = ReadProfiles(Path.Combine(userData, "Local State"));
        if (profiles.Count == 0)
        {
            return null;
        }

        long bytes = 0;
        foreach (var profile in profiles)
        {
            foreach (var cache in ProfileCaches)
            {
                bytes += MeasurePlain(Path.Combine([userData, profile, .. cache]), cancellationToken);
            }
        }

        foreach (var shared in SharedCaches)
        {
            bytes += MeasurePlain(Path.Combine(userData, shared), cancellationToken);
        }

        var entry = AppCatalog.Entries.Single(e => e.Id == id);
        return bytes == 0 ? null : new GuideRow(entry, entry.AppName, userData, bytes, GuideAction.OpenClearBrowsingData);
    }

    /// <summary>The profile folder names in <c>profile.info_cache</c>, each a single plain folder name, once (case-insensitive);
    /// empty when the file is missing, too big, not JSON or has no such list.</summary>
    private static List<string> ReadProfiles(string localState)
    {
        var profiles = new List<string>();
        if (ReadLimited(localState, LocalStateLimit) is not { } bytes)
        {
            return profiles;
        }

        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(start));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object
                || !profile.TryGetProperty("info_cache", out var infoCache) || infoCache.ValueKind != JsonValueKind.Object)
            {
                return profiles;
            }

            foreach (var property in infoCache.EnumerateObject())
            {
                if (IsPlainName(property.Name) && !profiles.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    profiles.Add(property.Name);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not JSON, or a name that is not valid UTF-8 (InvalidOperationException, DecoderFallbackException): no row.
            profiles.Clear();
        }

        return profiles;
    }

    private static GuideRow? ReadCapCut(PathContext context, CancellationToken cancellationToken)
    {
        var apps = Path.Combine(context.LocalAppData, "CapCut", "Apps");
        var versions = new List<(Version Version, string Folder)>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(apps))
            {
                if (ParseVersion(Path.GetFileName(folder)) is { } version && CacheFolder.IsPlainFolder(folder))
                {
                    versions.Add((version, folder));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (versions.Count < 2)
        {
            return null;
        }

        var top = versions.Max(v => v.Version);
        var newest = versions.Where(v => v.Version == top).Select(v => v.Folder).Max(StringComparer.Ordinal)!;
        long bytes = 0;
        foreach (var (_, folder) in versions)
        {
            if (!string.Equals(folder, newest, StringComparison.Ordinal))
            {
                bytes += MeasurePlain(folder, cancellationToken);
            }
        }

        var entry = AppCatalog.Entries.Single(e => e.Id == "capcut");
        return bytes == 0 ? null : new GuideRow(entry, entry.AppName, apps, bytes, GuideAction.OpenFolder);
    }

    /// <summary>A version of 2 to 4 dot-separated parts of 1 to 9 digits each, or null.</summary>
    private static Version? ParseVersion(string name)
    {
        var parts = name.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        return Version.TryParse(name, out var version) ? version : null;
    }

    private static IEnumerable<GuideRow> ReadUpdaters(PathContext context, IUninstallSource uninstall, CancellationToken cancellationToken)
    {
        var entry = AppCatalog.Entries.Single(e => e.Id == "electron-updater");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<GuideRow>();
        foreach (var app in uninstall.ReadEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(app.DisplayName) || app.InstallLocation is not { } location
                || FullLocalPath(location) is not { } install
                || ReadUpdaterName(Path.Combine(install, "resources", "app-update.yml")) is not { } name
                || !seen.Add(name))
            {
                continue;
            }

            var pending = Path.Combine(context.LocalAppData, name, "pending");
            var bytes = MeasurePlain(pending, cancellationToken);
            if (bytes > 0)
            {
                rows.Add(new GuideRow(entry, app.DisplayName, pending, bytes, GuideAction.OpenFolder) { UninstallKeyName = app.KeyName });
            }
        }

        return rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The value of the first <c>updaterCacheDirName:</c> line, trimmed and unquoted, when it is a single plain folder
    /// name; else null.</summary>
    private static string? ReadUpdaterName(string file)
    {
        if (ReadLimited(file, UpdateYmlLimit) is not { } bytes)
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(UpdaterKey, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[UpdaterKey.Length..].Trim();
            if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            return IsPlainName(value) ? value : null;
        }

        return null;
    }

    /// <summary>The path made full (<c>/</c> becomes <c>\</c>, <c>.</c> and <c>..</c> resolved) when it is fully qualified and
    /// then an absolute local drive path (<c>X:\...</c>); else null. Done before any <c>\\?\</c> form is built.</summary>
    internal static string? FullLocalPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            var full = Path.GetFullPath(path);
            return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\' ? full : null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True for one folder name: not empty, not "." or "..", no separator or drive colon, no character a file name
    /// cannot hold, and no trailing dot or space (Windows drops those, so the name would mean another folder).</summary>
    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['\\', '/', ':']) < 0 && name[^1] is not ('.' or ' ');

    /// <summary>The folder's measured bytes, or 0 when it is missing or not a plain folder (a link counts nothing).</summary>
    internal static long MeasurePlain(string folder, CancellationToken cancellationToken) =>
        CacheFolder.IsPlainFolder(folder) ? MeasureChecked(folder, cancellationToken) : 0;

    /// <summary>The walker returns the size so far when cancelled, so a cancel after the measure throws: no partial size is used.</summary>
    private static long MeasureChecked(string folder, CancellationToken cancellationToken)
    {
        var bytes = CacheFolder.Measure(folder, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    /// <summary>The file's bytes when it can be read and holds at most <paramref name="limit"/> bytes; else null. Read as
    /// <c>PicturePreview</c> reads a picture (docs/spec/01 rule 6; the handle checks are a small copy of its private
    /// <c>CheckHandle</c>): the attributes by name first, then the file opened without following a link and without recall, the
    /// attributes checked again by the handle, then at most the limit read from that handle. Never opened for writing.</summary>
    internal static unsafe byte[]? ReadLimited(string path, int limit)
    {
        try
        {
            var extended = MeasureNative.ToExtendedPath(path);
            var attributes = RulesNative.GetFileAttributes(extended);
            if (attributes == RulesNative.InvalidFileAttributes || (attributes & MeasureNative.AttributeDirectory) != 0
                || (attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0)
            {
                return null;
            }

            using var handle = MeasureNative.CreateFile(extended, GenericRead, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting,
                FlagOpenNoRecall | MeasureNative.FlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                return null;
            }

            // The name may have changed since the check above: the facts of the opened item, from its handle.
            var tag = stackalloc uint[2];
            if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileAttributeTagInfoClass, (byte*)tag, 8))
            {
                return null;
            }

            var opened = tag[0];
            if ((opened & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0
                || (opened & MeasureNative.AttributeDirectory) != 0
                || ((opened & MeasureNative.AttributeReparsePoint) != 0 && (tag[1] & MeasureNative.ReparseTagNameSurrogate) != 0))
            {
                return null; // online-only, a folder, or a link: never read
            }

            if (RandomAccess.GetLength(handle) > limit)
            {
                return null;
            }

            var buffer = new byte[limit + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = RandomAccess.Read(handle, buffer.AsSpan(total), total)) > 0)
            {
                total += read;
            }

            return total > limit ? null : buffer[..total];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
