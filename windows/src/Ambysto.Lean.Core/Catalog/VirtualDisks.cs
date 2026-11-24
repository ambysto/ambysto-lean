using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>What kind of virtual disk a row is.</summary>
public enum VirtualDiskKind
{
    /// <summary>A WSL distribution's ext4.vhdx.</summary>
    Wsl,

    /// <summary>Docker Desktop's disk image.</summary>
    DockerDesktop,

    /// <summary>A VirtualBox machine's disks.</summary>
    VirtualBox,
}

/// <summary>One large virtual disk with the vendor's way to move it to another drive (decision 4: advice only).</summary>
public sealed record VirtualDiskRow(VirtualDiskKind Kind, string Name, string Path, long Bytes, string Advice);

/// <summary>Where the WSL distributions are listed (real: HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{guid}).</summary>
public interface IVirtualDiskSource
{
    /// <summary>Each distribution's name and base folder.</summary>
    IReadOnlyList<(string Name, string BasePath)> WslDistributions();
}

/// <summary>Virtual disks (plan "Phase C"): WSL, Docker Desktop and VirtualBox disks, read-only, with advice. Nothing is
/// written, moved or started; a file is asked for its size by a read-attributes handle and never opened for its content (the
/// two XML files are read as A2 reads a file: not a link, not online-only, at most 4 MiB, DTD processing off).
/// Hyper-V (its list needs an administrator) and VMware (no single documented list) are left out of v1.</summary>
public static class VirtualDisks
{
    private const int XmlLimit = 4 * 1024 * 1024;
    private const uint FileReadAttributes = 0x0080;
    private const uint AttributeOffline = 0x1000;
    private const string ExtendedPrefix = @"\\?\";

    /// <summary>The rows for this user, the WSL distributions read from the registry (read-only), largest first.</summary>
    public static Task<IReadOnlyList<VirtualDiskRow>> ReadAsync(PathContext context, CancellationToken cancellationToken) =>
        ReadAsync(context, new RegistryVirtualDiskSource(), cancellationToken);

    /// <summary>The rows, largest first.</summary>
    public static Task<IReadOnlyList<VirtualDiskRow>> ReadAsync(PathContext context, IVirtualDiskSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        return Task.Run<IReadOnlyList<VirtualDiskRow>>(() => Read(context, source, cancellationToken), cancellationToken);
    }

    private static List<VirtualDiskRow> Read(PathContext context, IVirtualDiskSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<VirtualDiskRow>();
        // Docker first, then VirtualBox, then WSL: a disk file is one row, kept for the first source that names it.
        ReadDocker(context, rows, cancellationToken);
        ReadVirtualBox(context, rows, cancellationToken);
        ReadWsl(source, rows, cancellationToken);
        return [.. rows.OrderByDescending(r => r.Bytes).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.Ordinal)];
    }

    private static void ReadWsl(IVirtualDiskSource source, List<VirtualDiskRow> rows, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, string BasePath)> distributions;
        try
        {
            distributions = source.WslDistributions();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return; // a source that fails gives no WSL rows; the other kinds are still read
        }

        foreach (var (name, basePath) in distributions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(name) || IsDockerDistribution(name) || LocalBasePath(basePath) is not { } folder)
            {
                continue;
            }

            var disk = Path.Combine(folder, "ext4.vhdx");
            if (!HasRow(rows, disk) && AllocatedBytesOfPlainFile(disk) is { } bytes)
            {
                rows.Add(new VirtualDiskRow(VirtualDiskKind.Wsl, name, disk, bytes, Text.AdviceWsl));
            }
        }
    }

    /// <summary>True for the two distributions Docker Desktop registers for itself; they are shown as Docker Desktop's rows.</summary>
    private static bool IsDockerDistribution(string name) =>
        string.Equals(name, "docker-desktop", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "docker-desktop-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a row with this disk path (any case) is already there.</summary>
    private static bool HasRow(List<VirtualDiskRow> rows, string path) =>
        rows.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>WSL writes a BasePath as <c>\\?\C:\...</c> at times: that prefix is dropped and the rest must be an absolute
    /// local drive path. Any other <c>\\?\</c> form, a UNC path or a <c>\\.\</c> path is not used.</summary>
    private static string? LocalBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var path = basePath.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? basePath[ExtendedPrefix.Length..] : basePath;
        return GuideRows.FullLocalPath(path);
    }

    private static void ReadDocker(PathContext context, List<VirtualDiskRow> rows, CancellationToken cancellationToken)
    {
        // The two places Docker Desktop's WSL 2 backend keeps its disk; each is a row when it is a plain file.
        foreach (var (name, relative) in new[] { (Text.DockerDesktopRowName, @"Docker\wsl\disk\docker_data.vhdx"), (Text.DockerDesktopDataRowName, @"Docker\wsl\data\ext4.vhdx") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GuideRows.FullLocalPath(context.LocalAppData) is not { } local)
            {
                return;
            }

            var disk = Path.Combine(local, relative);
            if (!HasRow(rows, disk) && AllocatedBytesOfPlainFile(disk) is { } bytes)
            {
                rows.Add(new VirtualDiskRow(VirtualDiskKind.DockerDesktop, name, disk, bytes, Text.AdviceDockerDesktop));
            }
        }
    }

    private static void ReadVirtualBox(PathContext context, List<VirtualDiskRow> rows, CancellationToken cancellationToken)
    {
        if (GuideRows.FullLocalPath(context.ProfileFolder) is not { } profile)
        {
            return;
        }

        var registry = Path.Combine(profile, ".VirtualBox", "VirtualBox.xml");
        if (ReadXml(registry) is not { } document)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Attributes(document, "MachineEntry", "src"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(source, Path.GetDirectoryName(registry)!) is { } vbox && seen.Add(vbox) && ReadMachine(vbox, cancellationToken) is { } row && !HasRow(rows, row.Path))
            {
                rows.Add(row);
            }
        }
    }

    /// <summary>One machine: its .vbox file, the name in it and the sum of its hard disks that are plain files. No row when the
    /// file cannot be read or none of its disks is there.</summary>
    private static VirtualDiskRow? ReadMachine(string vbox, CancellationToken cancellationToken)
    {
        if (ReadXml(vbox) is not { } document)
        {
            return null;
        }

        var name = Attributes(document, "Machine", "name").FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? Path.GetFileNameWithoutExtension(vbox);
        var disks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var location in Attributes(document, "HardDisk", "location"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(location, Path.GetDirectoryName(vbox)!) is { } disk && disks.Add(disk) && AllocatedBytesOfPlainFile(disk) is { } size)
            {
                bytes += size;
            }
        }

        return bytes > 0 ? new VirtualDiskRow(VirtualDiskKind.VirtualBox, name, vbox, bytes, Text.AdviceVirtualBox) : null;
    }

    /// <summary>The full local path of <paramref name="location"/>: as written when it is a full path, else relative to
    /// <paramref name="folder"/> (VirtualBox writes a path inside its own folders relative); null when it is neither.</summary>
    private static string? Resolve(string? location, string folder)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        try
        {
            if (Path.IsPathFullyQualified(location))
            {
                return GuideRows.FullLocalPath(location);
            }

            return Path.IsPathRooted(location) ? null : GuideRows.FullLocalPath(Path.Combine(folder, location));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The value of <paramref name="attribute"/> on every element named <paramref name="element"/> (any namespace),
    /// in document order.</summary>
    private static IEnumerable<string> Attributes(XmlDocument document, string element, string attribute) =>
        document.GetElementsByTagName("*").Cast<XmlNode>().OfType<XmlElement>()
            .Where(e => e.LocalName == element && e.HasAttribute(attribute)).Select(e => e.GetAttribute(attribute));

    /// <summary>The document in the file when the file is a plain, readable file of at most 4 MiB holding XML; else null.
    /// A document type declaration is refused (DTD processing prohibited) and no resolver is set, so nothing is fetched or
    /// expanded.</summary>
    private static XmlDocument? ReadXml(string path)
    {
        if (GuideRows.ReadLimited(path, XmlLimit) is not { } bytes)
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true };
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            return document;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null; // damaged, empty or not XML
        }
    }

    /// <summary>The file's allocated size when it is a plain file (not a folder, a link or an online-only placeholder), asked of
    /// a handle opened for its attributes only, as the walker asks: the file's content is never read, so nothing is downloaded.</summary>
    private static unsafe long? AllocatedBytesOfPlainFile(string path)
    {
        try
        {
            var extended = MeasureNative.ToExtendedPath(path);
            var attributes = RulesNative.GetFileAttributes(extended);
            if (attributes == RulesNative.InvalidFileAttributes || (attributes & (MeasureNative.AttributeDirectory | MeasureNative.AttributeRecallOnOpen
                | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0)
            {
                return null;
            }

            using var handle = MeasureNative.CreateFile(extended, FileReadAttributes, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting, MeasureNative.FlagOpenReparsePoint, 0);
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

            if ((tag[0] & (MeasureNative.AttributeDirectory | MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0
                || ((tag[0] & MeasureNative.AttributeReparsePoint) != 0 && (tag[1] & MeasureNative.ReparseTagNameSurrogate) != 0))
            {
                return null;
            }

            var standard = stackalloc byte[24];
            if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileStandardInfoClass, standard, 24))
            {
                return null;
            }

            return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(standard);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
