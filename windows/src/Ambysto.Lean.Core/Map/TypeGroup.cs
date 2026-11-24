using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Map;

/// <summary>The file-type groups of docs/spec/01 (Map) and docs/spec/03 (type groups), plus the neutral tint of a folder
/// drawn with its children inside.</summary>
public enum TypeGroupKind
{
    /// <summary>Video.</summary>
    Video,

    /// <summary>Disk image.</summary>
    DiskImage,

    /// <summary>Archive.</summary>
    Archive,

    /// <summary>Document.</summary>
    Document,

    /// <summary>Application.</summary>
    Application,

    /// <summary>Virtual machine image (Windows note: also <c>.vhdx</c> and <c>.vhd</c>).</summary>
    VirtualMachineImage,

    /// <summary>Anything else.</summary>
    Other,

    /// <summary>A folder drawn with its children inside: a light neutral tint.</summary>
    OpenFolder,
}

/// <summary>Which group a file belongs to, and the colour of a tile.</summary>
public static class TypeGroup
{
    private static readonly FrozenDictionary<string, TypeGroupKind> ByExtension = BuildExtensions();

    /// <summary>The group of a file by its name.</summary>
    public static TypeGroupKind Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var value = path.AsSpan();
        var cut = value.LastIndexOfAny('\\', '/');
        var name = cut < 0 ? value : value[(cut + 1)..];
        if (name.Equals("Docker.raw".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return TypeGroupKind.VirtualMachineImage;
        }

        // A name that only starts with a dot (".gitignore") has no extension.
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return TypeGroupKind.Other;
        }

        return ByExtension.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name[dot..], out var kind) ? kind : TypeGroupKind.Other;
    }

    /// <summary>The colour of a node drawn as one tile: a file's own group; a folder takes the group of the file reached
    /// by following its largest child.</summary>
    public static TypeGroupKind ColourOf(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        while (node.IsFolder)
        {
            ScanNode? largest = null;
            foreach (var child in node.Children)
            {
                if (largest is null || child.AllocatedBytes > largest.AllocatedBytes)
                {
                    largest = child;
                }
            }

            if (largest is null)
            {
                return TypeGroupKind.Other;
            }

            node = largest;
        }

        return Of(node.Name);
    }

    private static FrozenDictionary<string, TypeGroupKind> BuildExtensions()
    {
        var map = new Dictionary<string, TypeGroupKind>(StringComparer.OrdinalIgnoreCase);
        void Add(TypeGroupKind kind, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                map.Add(extension, kind);
            }
        }

        Add(TypeGroupKind.Video, ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".m2ts", ".3gp");
        Add(TypeGroupKind.DiskImage, ".iso", ".img", ".dmg", ".wim", ".esd");
        Add(TypeGroupKind.Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab");
        Add(TypeGroupKind.Document, ".pdf", ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pptm", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".epub");
        Add(TypeGroupKind.Application, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle");
        Add(TypeGroupKind.VirtualMachineImage, ".vmdk", ".qcow2", ".vdi", ".utm", ".vhdx", ".avhdx", ".vhd", ".ova");
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
