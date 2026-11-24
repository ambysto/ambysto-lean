using System;
using System.Collections.Generic;
using System.Text;

namespace Ambysto.Lean.Core.Measure;

/// <summary>Facts the walk records about one entry (docs/spec/01, walk rules and their Windows notes).</summary>
[Flags]
public enum NodeMarks
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>A hard link whose size is counted at another path (walk rule 4): its own size is 0.</summary>
    HardLinkCountedElsewhere = 1,

    /// <summary>A cloud file whose content is not on this disk (walk rule 6): counted, never opened.</summary>
    CloudNotDownloaded = 2,

    /// <summary>Could not be read (walk rule 7): counted, never treated as empty.</summary>
    Unreadable = 4,

    /// <summary>A link (symbolic link, junction, mount point; walk rule 2): costs its own size, never entered.</summary>
    Link = 8,

    /// <summary>Has the hidden attribute.</summary>
    Hidden = 16,
}

/// <summary>One file or folder of a scan. Built by <see cref="Scanner"/>; tests build trees with <see cref="File"/>,
/// <see cref="Folder"/> and <see cref="Add"/>, and <see cref="ScanTree"/> totals them.
/// A node built by the walker stores only its name and its parent; <see cref="Path"/> is joined on demand. Its stamp is
/// kept as flat fields. Both keep a scan of a million files small (intent criterion 11).</summary>
public sealed class ScanNode
{
    private static readonly IReadOnlyList<ScanNode> NoChildren = [];

    private sealed record FullStamp(ItemStamp Stamp, long LastWriteMinutes);

    private sealed record FolderExtra(List<ScanNode> Children, FullStamp? BoxedStamp);

    private const byte StampNone = 0;
    private const byte StampCompactZeroSerial = 1;
    private const byte StampCompactParentSerial = 2;
    private const byte StampBoxed = 3;

    // The name of the entry, or its full path when explicitPath is set (a root, a node given a path by a caller, a node
    // taken out of its tree).
    private string text;
    private ScanNode? parent;
    private object? extra;
    private bool explicitPath;
    private readonly bool isFolder;
    private byte stampKind;
    private volatile bool unsorted;
    private NodeMarks flags;
    private int id;
    private int fileCount;
    private long logicalBytes;
    private long allocatedBytes;
    private ulong stampFileIdLow;
    private long timeValue;

    private ScanNode(string text, bool explicitPath, bool isFolder, long logicalBytes, long allocatedBytes, long lastWriteMinutes, ItemStamp? stamp, NodeMarks flags, ScanNode? knownParent = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        this.text = text;
        this.explicitPath = explicitPath;
        this.isFolder = isFolder;
        extra = isFolder ? new List<ScanNode>() : null;
        this.logicalBytes = logicalBytes;
        this.allocatedBytes = allocatedBytes;
        fileCount = isFolder ? 0 : 1;
        this.flags = flags;
        SetStamp(stamp, lastWriteMinutes, knownParent);
    }

    /// <summary>Unique within its tree; how later steps (selection, deletion) name an item. 0 until a
    /// <see cref="ScanTree"/> has numbered the node.</summary>
    public long Id
    {
        get => id;
        internal set => id = checked((int)value);
    }

    /// <summary>The canonical full path.</summary>
    public string Path
    {
        get
        {
            if (explicitPath || parent is null)
            {
                return text;
            }

            var names = new List<string>();
            var node = this;
            while (!node.explicitPath && node.parent is not null)
            {
                names.Add(node.text);
                node = node.parent;
            }

            var builder = new StringBuilder(node.text);
            for (var i = names.Count - 1; i >= 0; i--)
            {
                if (builder.Length == 0 || builder[^1] != '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(names[i]);
            }

            return builder.ToString();
        }
    }

    /// <summary>The last path component.</summary>
    public string Name
    {
        get
        {
            if (!explicitPath)
            {
                return text;
            }

            var trimmed = text.TrimEnd('\\', '/');
            var cut = trimmed.LastIndexOfAny(['\\', '/']);
            return trimmed.Length == 0 ? text : trimmed[(cut + 1)..];
        }
    }

    /// <summary>True for a folder (a link is never a folder here: it is never entered).</summary>
    public bool IsFolder => isFolder;

    /// <summary>Logical length; for a folder, the sum of its children.</summary>
    public long LogicalBytes
    {
        get => logicalBytes;
        internal set
        {
            if (value == logicalBytes)
            {
                return;
            }

            if (!isFolder && stampKind is StampCompactZeroSerial or StampCompactParentSerial)
            {
                var current = Stamp;
                var minutes = LastWriteMinutes;
                logicalBytes = value;
                SetStamp(current, minutes, parent);
                return;
            }

            logicalBytes = value;
        }
    }

    /// <summary>Allocated bytes on disk (walk rule 3); for a folder, the sum of its children.</summary>
    public long AllocatedBytes
    {
        get => allocatedBytes;
        internal set => allocatedBytes = value;
    }

    /// <summary>Number of files at or below this node (1 for a file).</summary>
    public long FileCount
    {
        get => fileCount;
        internal set => fileCount = checked((int)value);
    }

    /// <summary>Last write time as signed minutes since 1970-01-01 UTC (walk rule 8).</summary>
    public long LastWriteMinutes
    {
        get => stampKind switch
        {
            StampNone => timeValue,
            StampCompactZeroSerial or StampCompactParentSerial => ScanRun.ToMinutes(timeValue),
            _ => GetBoxedStamp()!.LastWriteMinutes,
        };
        internal set
        {
            if (value == LastWriteMinutes)
            {
                return;
            }

            if (stampKind == StampNone)
            {
                timeValue = value;
                return;
            }

            SetStamp(Stamp, value, parent);
        }
    }

    /// <summary>The identity read during the walk; null when the entry could not be read.</summary>
    public ItemStamp? Stamp
    {
        get => stampKind switch
        {
            StampNone => null,
            StampCompactZeroSerial => new ItemStamp(0, new UInt128(0, stampFileIdLow), timeValue, isFolder ? null : logicalBytes),
            StampCompactParentSerial => new ItemStamp(parent!.GetVolumeSerial(), new UInt128(0, stampFileIdLow), timeValue, isFolder ? null : logicalBytes),
            _ => GetBoxedStamp()!.Stamp,
        };
        internal set => SetStamp(value, LastWriteMinutes, parent);
    }

    internal UInt128 FileId => stampKind switch
    {
        StampNone => 0,
        StampCompactZeroSerial or StampCompactParentSerial => new UInt128(0, stampFileIdLow),
        _ => GetBoxedStamp()!.Stamp.FileId,
    };

    private FullStamp? GetBoxedStamp() => extra switch
    {
        FullStamp boxed => boxed,
        FolderExtra folder => folder.BoxedStamp,
        _ => null,
    };

    private void SetBoxedStamp(FullStamp? boxed)
    {
        if (isFolder)
        {
            var list = GetFolderChildren()!;
            extra = boxed is null ? list : new FolderExtra(list, boxed);
        }
        else
        {
            extra = boxed;
        }
    }

    private List<ScanNode>? GetFolderChildren() => extra switch
    {
        List<ScanNode> list => list,
        FolderExtra folder => folder.Children,
        _ => null,
    };

    private bool TryGetVolumeSerial(out ulong serial)
    {
        switch (stampKind)
        {
            case StampCompactZeroSerial:
                serial = 0;
                return true;
            case StampCompactParentSerial when parent is not null:
                return parent.TryGetVolumeSerial(out serial);
            case StampBoxed when GetBoxedStamp() is { } boxed:
                serial = boxed.Stamp.VolumeSerial;
                return true;
            default:
                serial = 0;
                return false;
        }
    }

    private ulong GetVolumeSerial() => TryGetVolumeSerial(out var serial) ? serial : 0;

    private void SetStamp(ItemStamp? value, long minutes, ScanNode? holder)
    {
        if (isFolder && TryGetVolumeSerial(out var prevSerial)
            && (value is not { } nextStamp || nextStamp.VolumeSerial != prevSerial)
            && GetFolderChildren() is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                if (child.stampKind == StampCompactParentSerial)
                {
                    var childStamp = child.Stamp;
                    var childMinutes = child.LastWriteMinutes;
                    child.SetStamp(childStamp, childMinutes, null);
                }
            }
        }

        if (value is not { } stamp)
        {
            stampKind = StampNone;
            stampFileIdLow = 0;
            timeValue = minutes;
            SetBoxedStamp(null);
            return;
        }

        var expectedLength = isFolder ? (long?)null : logicalBytes;
        if ((stamp.FileId >> 64) == 0
            && stamp.Length == expectedLength
            && minutes == ScanRun.ToMinutes(stamp.LastWriteTimeUtcTicks))
        {
            if (stamp.VolumeSerial == 0)
            {
                stampKind = StampCompactZeroSerial;
                stampFileIdLow = (ulong)stamp.FileId;
                timeValue = stamp.LastWriteTimeUtcTicks;
                SetBoxedStamp(null);
                return;
            }

            if (holder is not null && holder.TryGetVolumeSerial(out var parentSerial) && parentSerial == stamp.VolumeSerial)
            {
                stampKind = StampCompactParentSerial;
                stampFileIdLow = (ulong)stamp.FileId;
                timeValue = stamp.LastWriteTimeUtcTicks;
                SetBoxedStamp(null);
                return;
            }
        }

        stampKind = StampBoxed;
        stampFileIdLow = 0;
        timeValue = 0;
        SetBoxedStamp(new FullStamp(stamp, minutes));
    }

    /// <summary>What the walk recorded about this entry.</summary>
    public NodeMarks Flags
    {
        get => flags;
        internal set => flags = value;
    }

    /// <summary>The parent folder; null for the root.</summary>
    public ScanNode? Parent => parent;

    /// <summary>The children, largest allocated size first (once the node is in a <see cref="ScanTree"/>; the order is
    /// made on first use, so a scan does not pay for sorting folders nobody opens).</summary>
    public IReadOnlyList<ScanNode> Children
    {
        get
        {
            var list = GetFolderChildren();
            if (list is null)
            {
                return NoChildren;
            }

            if (unsorted)
            {
                lock (list)
                {
                    if (unsorted)
                    {
                        list.Sort(LargestFirst);
                        unsorted = false;
                    }
                }
            }

            return list;
        }
    }

    // Largest allocated size first; equal sizes by name, so the order is the same on every run.
    internal static int LargestFirst(ScanNode a, ScanNode b)
    {
        var bySize = b.allocatedBytes.CompareTo(a.allocatedBytes);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The children in the order they were added; empty for a file. For the tree, which totals them.</summary>
    internal List<ScanNode> ChildList => GetFolderChildren() ?? throw new InvalidOperationException("a file has no children");

    /// <summary>Marks the children as needing the largest-first order again.</summary>
    internal void MarkUnsorted() => unsorted = true;

    /// <summary>A file node for building a tree in tests or in the walker.</summary>
    public static ScanNode File(string path, long logicalBytes, long allocatedBytes, long lastWriteMinutes, ItemStamp? stamp = null, NodeMarks flags = NodeMarks.None) =>
        new(path, explicitPath: true, isFolder: false, logicalBytes, allocatedBytes, lastWriteMinutes, stamp, flags);

    /// <summary>A folder node; add children with <see cref="Add"/>.</summary>
    public static ScanNode Folder(string path, long lastWriteMinutes = 0, ItemStamp? stamp = null, NodeMarks flags = NodeMarks.None) =>
        new(path, explicitPath: true, isFolder: true, 0, 0, lastWriteMinutes, stamp, flags);

    /// <summary>An entry the walker found: stores its name only; its path is its parent's path joined with it.</summary>
    internal static ScanNode Entry(string name, bool isFolder, long logicalBytes, long allocatedBytes, long lastWriteMinutes, ItemStamp? stamp, NodeMarks flags, ScanNode? parent = null) =>
        new(name, explicitPath: false, isFolder: isFolder, logicalBytes, allocatedBytes, lastWriteMinutes, stamp, flags, parent);

    /// <summary>Adds a child to this folder and returns this folder. Totals are computed by <see cref="ScanTree"/>.</summary>
    public ScanNode Add(ScanNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Attach(child);
        if (child.explicitPath)
        {
            // A path that is just this folder's path plus the child's name is derived, not stored.
            var name = child.Name;
            var parentPath = Path;
            var expected = parentPath.EndsWith('\\') ? parentPath + name : parentPath + "\\" + name;
            if (string.Equals(child.text, expected, StringComparison.Ordinal))
            {
                child.text = name;
                child.explicitPath = false;
            }
        }

        return this;
    }

    /// <summary>Adds a node the walker made with <see cref="Entry"/> (its path is derived).</summary>
    internal void AddEntry(ScanNode child) => Attach(child);

    private void Attach(ScanNode child)
    {
        var list = GetFolderChildren();
        if (list is null)
        {
            throw new InvalidOperationException("a file cannot have children: " + Path);
        }

        if (child.parent is not null || ReferenceEquals(child, this))
        {
            throw new InvalidOperationException("the node already has a parent: " + child.Path);
        }

        child.parent = this;
        if (!child.isFolder && child.stampKind == StampBoxed && child.GetBoxedStamp() is { } boxed)
        {
            child.SetStamp(boxed.Stamp, boxed.LastWriteMinutes, this);
        }

        list.Add(child);
    }

    /// <summary>Takes <paramref name="child"/> out of this folder (the caller re-totals). The child keeps its full path.</summary>
    internal void Detach(ScanNode child)
    {
        var list = GetFolderChildren();
        if (list is not null && ReferenceEquals(child.parent, this))
        {
            var path = child.Path;
            if (child.stampKind == StampCompactParentSerial)
            {
                var currentStamp = child.Stamp;
                var minutes = child.LastWriteMinutes;
                child.SetStamp(currentStamp, minutes, null);
            }

            lock (list)
            {
                list.Remove(child);
            }

            child.text = path;
            child.explicitPath = true;
            child.parent = null;
        }
    }
}
