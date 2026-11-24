using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Uninstall;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>One measured version leaf, with evidence-based eligibility independent of numerical ordering alone.</summary>
public sealed record InstallerVersionRow(string AppName, string Version, string Path, long? Bytes, bool CanMove, string Reason);

/// <summary>Read-only version discovery. Only the observed CapCut registry/ProductInfo schema can authorize a leaf.</summary>
public static class InstallerVersions
{
    private const int MetadataLimit = 65536;
    private sealed record Evidence(Version Current, HashSet<Version> Retained);

    /// <summary>Lists individual CapCut and Codex version leaves. Unknown references remain visible and unselectable.</summary>
    public static Task<IReadOnlyList<InstallerVersionRow>> ReadAsync(PathContext context, IUninstallSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        return Task.Run<IReadOnlyList<InstallerVersionRow>>(() => Read(context, source, cancellationToken), cancellationToken);
    }

    private static List<InstallerVersionRow> Read(PathContext context, IUninstallSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<InstallerVersionRow>();
        var apps = Path.Combine(context.LocalAppData, "CapCut", "Apps");
        var leaves = VersionLeaves(apps, context);
        var evidence = ReadEvidence(apps, context, source, leaves);
        AddLeaves(rows, "CapCut", leaves, context, evidence, token);
        AddLeaves(rows, "Codex", VersionLeaves(Path.Combine(context.ProfileFolder, ".codex", "releases"), context), context, null, token);
        AddLeaves(rows, "Lark", VersionLeaves(Path.Combine(context.LocalAppData, "Lark", "versions"), context), context, null, token);
        AddArtifact(rows, "Lark", Path.Combine(context.LocalAppData, "Lark", "app"), context, Text.VersionReferencesUnknown, token);
        AddArtifact(rows, "LarkInternational", Path.Combine(context.LocalAppData, "LarkInternational", "update", "update_downloading"), context, Text.InstallerPendingUnknown, token);
        try
        {
            var owners = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source.ReadEntries())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(entry.DisplayName) || entry.InstallLocation is not { Length: > 0 } location
                        || NormalizeReference(location.Trim().Trim('"')) is not { } install) continue;
                    var metadata = Path.Combine(install, "resources", "app-update.yml");
                    if (NormalizeReference(metadata) is null || GuideRows.ReadLimited(metadata, MetadataLimit) is not { } bytes) continue;
                    var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                    var values = text.Split('\n').Where(l => l.StartsWith("updaterCacheDirName:", StringComparison.Ordinal)).ToArray();
                    if (values.Length != 1) continue;
                    var name = values[0][20..].Trim();
                    if (name.Length > 0 && name[0] is '\'' or '"')
                    {
                        if (name.Length < 2 || name[^1] != name[0]) continue;
                        name = name[1..^1];
                    }
                    else if (name.Length > 0 && ("&*!{[>|".Contains(name[0]) || name.Contains('#'))) continue;
                    if (name.Length == 0 || name is "." or ".." || name.Contains('\'') || name.Contains('"') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || name.IndexOfAny(['\\','/',':']) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) continue;
                    var pending = Path.Combine(context.LocalAppData, name, "pending");
                    if (!owners.TryGetValue(pending, out var names)) owners.Add(pending, names = new Dictionary<string, string>(StringComparer.Ordinal));
                    var owner = entry.Scope.ToString() + ":" + entry.View.ToString() + ":" + entry.KeyName;
                    names[owner] = entry.DisplayName;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            }
            foreach (var (pending, names) in owners)
                AddArtifact(rows, names.Count == 1 ? names.Values.Single() : Text.InstallerSharedOwner, pending, context,
                    names.Count == 1 ? Text.InstallerPendingUnknown : Text.InstallerOwnershipUnknown, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        token.ThrowIfCancellationRequested();
        return rows.OrderByDescending(r => r.Bytes ?? -1).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddLeaves(List<InstallerVersionRow> rows, string app, List<(string Path, Version Version)> leaves,
        PathContext context, Evidence? evidence, CancellationToken token)
    {
        foreach (var (path, version) in leaves)
        {
            token.ThrowIfCancellationRequested();
            long? bytes = null;
            var complete = false;
            try
            {
                if (!PlainChain(path, context))
                {
                    rows.Add(new(app, Path.GetFileName(path), path, null, false, Text.VersionNotMeasured));
                    continue;
                }
                var scan = new Scanner().ScanAsync(path, null, token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                complete = !scan.Cancelled && scan.Coverage.UnreadableCount == 0 && scan.Coverage.CloudNotDownloadedCount == 0;
                bytes = complete ? scan.Root.AllocatedBytes : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            var allowed = app == "CapCut" && complete && Eligible(path, version, context, evidence);
            var reason = allowed ? Text.VersionUnusedEvidence : !complete ? Text.VersionNotMeasured
                : evidence is null ? Text.VersionReferencesUnknown : Text.VersionRetained;
            rows.Add(new(app, Path.GetFileName(path), path, bytes, allowed, reason));
        }
    }

    private static void AddArtifact(List<InstallerVersionRow> rows, string app, string path, PathContext context, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Missing artifacts are absent; known unsafe roots are visible without following them.
        if (Path.GetDirectoryName(path) is not { } parent || !PlainChain(parent, context)) return;
        try { if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        long? bytes = null;
        if (PlainChain(path, context))
        {
            try
            {
                var scan = new Scanner().ScanAsync(path, null, token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                if (!scan.Cancelled && scan.Coverage.UnreadableCount == 0 && scan.Coverage.CloudNotDownloadedCount == 0)
                    bytes = scan.Root.AllocatedBytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (bytes == 0) return;
        rows.Add(new(app, Text.Unknown, path, bytes, false, reason));
    }

    /// <summary>Re-reads current metadata, registrations and processes before each move; no preview decision is trusted.</summary>
    public static bool CanMoveNow(string path, PathContext context, IUninstallSource source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        if (GuideRows.FullLocalPath(path) is not { } full) return false;
        var apps = Path.Combine(context.LocalAppData, "CapCut", "Apps");
        if (!string.Equals(Path.GetDirectoryName(full), apps, StringComparison.OrdinalIgnoreCase)) return false;
        var version = ParseVersion(Path.GetFileName(full));
        if (version is null) return false;
        var leaves = VersionLeaves(apps, context);
        return leaves.Any(l => string.Equals(l.Path, full, StringComparison.OrdinalIgnoreCase))
            && Eligible(full, version, context, ReadEvidence(apps, context, source, leaves));
    }

    private static bool Eligible(string path, Version version, PathContext context, Evidence? evidence) =>
        evidence is not null && version < evidence.Current && !evidence.Retained.Contains(version)
        && PlainChain(path, context) && !context.UserProtectedFolders.Any(p => Overlap(p, path))
        && !context.SyncRoots.Any(p => Overlap(p.Path, path));

    private static Evidence? ReadEvidence(string apps, PathContext context, IUninstallSource source, List<(string Path, Version Version)> leaves)
    {
        if (leaves.Count == 0 || !PlainChain(apps, context)) return null;
        try
        {
            var entries = source.ReadEntries();
            if (entries.Any(e => !e.ValuesReadable)) return null;
            var primary = entries.Where(e => string.Equals(e.KeyName, "CapCut", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (primary.Length == 0 || primary.Any(e => e.Scope != UninstallScope.PerUser || e.SystemComponent
                || !string.Equals(e.DisplayName, "CapCut", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(e.Publisher, "Bytedance Pte. Ltd.", StringComparison.OrdinalIgnoreCase))) return null;
            var registered = primary.Select(e => ParseVersion(e.DisplayVersion)).ToArray();
            if (registered.Any(v => v is null) || registered.Distinct().Count() != 1) return null;
            var current = registered[0]!;
            var xmlBytes = GuideRows.ReadLimited(Path.Combine(apps, "ProductInfo.xml"), MetadataLimit);
            if (xmlBytes is null) return null;
            var xml = new UTF8Encoding(false, true).GetString(xmlBytes).TrimStart('\uFEFF');
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != "packet") return null;
            var versions = document.Root.Elements("full_appver").ToArray();
            if (versions.Length != 1 || ParseVersion((string?)versions[0].Attribute("value")) != current
                || !leaves.Any(l => l.Version == current && PlainChain(l.Path, context))) return null;
            var retained = new HashSet<Version> { current, leaves.Max(l => l.Version)! };
            var iniBytes = GuideRows.ReadLimited(Path.Combine(apps, "Configure.ini"), MetadataLimit);
            if (iniBytes is null) return null;
            var ini = new UTF8Encoding(false, true).GetString(iniBytes).TrimStart('\uFEFF');
            var section = false;
            var seen = false;
            foreach (var raw in ini.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { section = line.Equals("[capcut]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!section || line.StartsWith(';') || line.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator < 0)
                {
                    if (line.StartsWith("last_version", StringComparison.OrdinalIgnoreCase)) return null;
                    continue;
                }
                if (!line[..separator].Trim().Equals("last_version", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen) return null;
                seen = true;
                var value = line[(separator + 1)..].Trim();
                if (value.Length > 0)
                {
                    if (ParseVersion(value) is not { } previous) return null;
                    retained.Add(previous);
                }
            }
            if (!seen) return null;
            foreach (var entry in entries)
            {
                if (string.Equals(entry.DisplayName, "CapCut", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Publisher, "Bytedance Pte. Ltd.", StringComparison.OrdinalIgnoreCase))
                {
                    if (ParseVersion(entry.DisplayVersion) is not { } referencedVersion) return null;
                    retained.Add(referencedVersion);
                }
                if (entry.InstallLocation is not { Length: > 0 } location) continue;
                if (NormalizeReference(location.Trim().Trim('"')) is not { } full) return null;
                foreach (var leaf in leaves)
                    if (CanonicalPath.Of(leaf.Path) is not { } canonicalLeaf) return null;
                    else if (Overlap(full, canonicalLeaf)) retained.Add(leaf.Version);
            }
            var capcut = CanonicalPath.Of(Path.GetDirectoryName(apps)!)!;
            foreach (var image in source.ReadProcessImagePaths())
            {
                if (NormalizeReference(image) is not { } normalized || Inside(capcut, normalized)) return null;
            }
            return new(current, retained);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or XmlException
            or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static List<(string Path, Version Version)> VersionLeaves(string parent, PathContext context)
    {
        var leaves = new List<(string Path, Version Version)>();
        if (!PlainChain(parent, context)) return leaves;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(parent))
                if (ParseVersion(Path.GetFileName(path)) is { } version) leaves.Add((path, version));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { leaves.Clear(); }
        return leaves;
    }

    // Compare missing numeric components as zero, so 9.1 and 9.1.0 aliases cannot bypass retained versions.
    private static Version? ParseVersion(string? text)
    {
        if (text is null) return null;
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit))) return null;
        if (!Version.TryParse(text, out var version)) return null;
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private static bool PlainChain(string path, PathContext context)
    {
        var full = GuideRows.FullLocalPath(path);
        if (full is null || !Inside(context.ProfileFolder, full)) return false;
        try
        {
            var current = Path.GetPathRoot(full)!;
            var components = full[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty);
            foreach (var component in components)
            {
                if (component.Length > 0) current = Path.Combine(current, component);
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0
                    || (attributes & FileAttributes.Directory) == 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    private static string? NormalizeReference(string value)
    {
        var raw = value.Replace('/', '\\');
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal)) raw = raw[4..];
        if (!Path.IsPathFullyQualified(raw) || raw.Length < 3 || !char.IsAsciiLetter(raw[0])
            || raw[1] != ':' || raw[2] != '\\') return null;
        if (raw.Split('\\').Any(p => p is not ("." or "..") && (p.EndsWith('.') || p.EndsWith(' ')))) return null;
        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or IOException) { return null; }
        try
        {
            // Inspect from root downward, so a redirected ancestor is refused before probing anything under it.
            var root = Path.GetPathRoot(full)!;
            if (new DriveInfo(root).DriveType == DriveType.Network) return null;
            for (var end = root.Length; ; )
            {
                var part = full[..end];
                FileAttributes attributes;
                try { attributes = File.GetAttributes(part); }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { return null; }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0) return null;
                if (end == full.Length) break;
                var next = full.IndexOf('\\', end == root.Length ? end : end + 1);
                end = next < 0 ? full.Length : next;
            }
            // Final-path metadata also resolves a SUBST spelling; never read the referenced file's contents.
            if (CanonicalPath.Of(full) is not { } canonical) return null;
            var facts = Ambysto.Kit.Safety.FileFacts.Read(canonical);
            if (!facts.Succeeded || facts.CanonicalPath is not { } resolved || !Path.IsPathFullyQualified(resolved)) return null;
            var final = CanonicalPath.Of(resolved);
            return final is { Length: >= 3 } && char.IsAsciiLetter(final[0]) && final[1] == ':' && final[2] == '\\' ? final : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool Inside(string root, string path) => string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Overlap(string a, string b) => Inside(a, b) || Inside(b, a);
}
