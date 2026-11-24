using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01 walk rule 3 with Windows notes (3) and (4): allocated size is what the file system reports per
/// file. A folder listing reports 0 allocated bytes for a WOF-compressed file, so such a file (listed with 0 but larger
/// than a file record) is asked once for its allocated size; an all-sparse file still counts 0; the names of one
/// hard-linked file can disagree after a write through one of them, and then the file's own answer is used. The oracle
/// for "what the file system reports" is <c>GetCompressedFileSize</c>, made by the test.</summary>
[TestClass]
public sealed class AllocatedSizeTests
{
    private const long FourHundredKb = 400 * ByteUnits.KB;

    private static Task<ScanTree> Scan(string root) => new Scanner().ScanAsync(root, null, CancellationToken.None);

    /// <summary>Writes a file of <paramref name="length"/> bytes that compresses well (repeated text).</summary>
    private static void WriteCompressible(string path, long length)
    {
        var line = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. 0123456789\r\n");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(line.Length, length - written);
            stream.Write(line, 0, count);
            written += count;
        }
    }

    /// <summary>Compresses <paramref name="path"/> with WOF (<c>compact /c /exe:xpress4k</c>) as the current user, or
    /// ends the test as Inconclusive with compact's answer when the standard user may not.</summary>
    private static void CompactWithWof(string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "compact.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "/c", "/exe:xpress4k", path })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        var compressed = Native.CompressedSizeOf(path);
        var logical = new FileInfo(path).Length;
        if (process.ExitCode != 0 || compressed >= logical)
        {
            Assert.Inconclusive($"compact /c /exe:xpress4k did not compress the file as this user (exit {process.ExitCode}; on disk {compressed} of {logical}):\n{output.Trim()}");
        }
    }

    // docs/spec/01 Windows note (3): a WOF-compressed file is listed with 0 allocated bytes and is asked once for its
    // allocated size: the scan reports what the file system reports per file (GetCompressedFileSize), not 0 and below the length.
    [TestMethod]
    public async Task WofCompressedFileCountsItsCompressedSize()
    {
        using var temp = new TempFolder("alloc-wof");
        var file = temp.Sub("wof.txt");
        WriteCompressible(file, FourHundredKb);
        CompactWithWof(file);
        var oracle = Native.CompressedSizeOf(file);

        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "wof.txt");
        Assert.AreEqual(FourHundredKb, node.LogicalBytes);
        Assert.AreEqual(oracle, node.AllocatedBytes, "allocated size of a WOF file is what the file system reports for it");
        Check.AtLeast(1, node.AllocatedBytes, "a compressed file with data is not 0 bytes on disk");
        Check.True(node.AllocatedBytes < node.LogicalBytes, $"compressed {node.AllocatedBytes} is below the length {node.LogicalBytes}");
    }

    // docs/spec/01 walk rule 3 and Windows note (3): a folder's total sums the per-file answers, the WOF file's included.
    [TestMethod]
    public async Task FolderTotalIncludesTheWofFilesCompressedSize()
    {
        using var temp = new TempFolder("alloc-wof-folder");
        var folder = temp.Folder("docs");
        var wof = Path.Combine(folder, "wof.txt");
        WriteCompressible(wof, FourHundredKb);
        CompactWithWof(wof);
        var plain = temp.File(@"docs\plain.bin", 100 * ByteUnits.KB);
        var wofOracle = Native.CompressedSizeOf(wof);

        var tree = await Scan(temp.Path);
        var docs = Trees.Node(tree, "docs");
        var plainNode = Trees.Node(tree, @"docs\plain.bin");
        Check.AtLeast(100 * ByteUnits.KB, plainNode.AllocatedBytes, "the plain file is allocated in full");
        Assert.AreEqual(wofOracle + plainNode.AllocatedBytes, docs.AllocatedBytes, "the folder total is the sum of the per-file answers");
        Assert.AreEqual(docs.AllocatedBytes, tree.Root.AllocatedBytes);
        Assert.IsTrue(File.Exists(plain));
    }

    // docs/spec/01 Windows note (3): "An all-sparse file then still counts 0": sparse attribute, a length, no data written.
    [TestMethod]
    public async Task AllSparseFileCountsZeroAllocatedBytes()
    {
        using var temp = new TempFolder("alloc-sparse");
        var file = temp.Sub("sparse.bin");
        Native.CreateSparseFile(file, 8 * ByteUnits.MB);
        Assert.AreEqual(0L, Native.CompressedSizeOf(file), "the fixture is not all-sparse");

        var tree = await Scan(temp.Path);
        var node = Trees.Node(tree, "sparse.bin");
        Assert.AreEqual(8 * ByteUnits.MB, node.LogicalBytes);
        Assert.AreEqual(0L, node.AllocatedBytes);
        Assert.AreEqual(0L, tree.Root.AllocatedBytes);
    }

    // docs/spec/01 walk rule 4 with Windows notes (3) and (4): a hard-linked pair whose file is WOF-compressed is counted
    // once, at the name that sorts first, with the file system's answer for its allocated size, not 0.
    [TestMethod]
    public async Task WofCompressedHardLinkPairIsCountedOnceWithItsCompressedSize()
    {
        using var temp = new TempFolder("alloc-wof-links");
        var first = temp.Sub("a-first.txt");
        var second = temp.Sub("b-second.txt");
        WriteCompressible(first, FourHundredKb);
        Native.CreateHardLink(second, first);
        CompactWithWof(second);
        var oracle = Native.CompressedSizeOf(first);
        Assert.AreEqual(oracle, Native.CompressedSizeOf(second), "one file, one answer");

        var tree = await Scan(temp.Path);
        var counted = Trees.Node(tree, "a-first.txt");
        var other = Trees.Node(tree, "b-second.txt");
        Assert.IsFalse(counted.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "the first-sorting name carries the size");
        Assert.AreEqual(oracle, counted.AllocatedBytes, "the compressed size, asked once for the file");
        Check.AtLeast(1, counted.AllocatedBytes, "not 0");
        Assert.IsTrue(other.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(0L, other.AllocatedBytes);
        Assert.AreEqual(oracle, tree.Root.AllocatedBytes, "the folder counts the file once");
        Assert.AreEqual(FourHundredKb, counted.LogicalBytes);
    }

    // docs/spec/01 Windows note (4): NTFS updates only the folder entry of the name a file was written through, so after
    // both names were listed and the file grew through name B, name A's entry may be stale; the names then disagree and
    // the file's own answer is used. When the listing happens to agree, the same expectation holds (a consistency check).
    [TestMethod]
    public async Task HardLinkNamesThatDisagreeUseTheFilesOwnAnswer()
    {
        using var temp = new TempFolder("alloc-stale-link");
        var first = temp.File("a-first.bin", 100 * ByteUnits.KB);
        var second = temp.Sub("b-second.bin");
        Native.CreateHardLink(second, first);
        var listedBefore = new DirectoryInfo(temp.Path).EnumerateFiles().ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(100 * ByteUnits.KB, listedBefore["a-first.bin"]);
        Assert.AreEqual(100 * ByteUnits.KB, listedBefore["b-second.bin"]);

        using (var stream = new FileStream(second, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write(new byte[300 * ByteUnits.KB]);
        }

        var listedAfter = new DirectoryInfo(temp.Path).EnumerateFiles().ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);
        var stale = listedAfter["a-first.bin"] != listedAfter["b-second.bin"];
        Console.WriteLine(stale
            ? $"the names disagree after the write: a-first.bin lists {listedAfter["a-first.bin"]}, b-second.bin lists {listedAfter["b-second.bin"]}"
            : $"the names agree after the write ({listedAfter["a-first.bin"]}): a consistency check only");
        long ownLength;
        using (var handle = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            ownLength = handle.Length;
        }

        Assert.AreEqual(FourHundredKb, ownLength, "the file's own answer after the append");

        var tree = await Scan(temp.Path);
        var counted = Trees.Node(tree, "a-first.bin");
        var other = Trees.Node(tree, "b-second.bin");
        Assert.IsFalse(counted.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere), "the first-sorting name carries the size");
        Assert.AreEqual(ownLength, counted.LogicalBytes, "the size used is the file's own answer, not a stale entry");
        Check.AtLeast(ownLength, counted.AllocatedBytes, "allocated covers the whole file");
        Assert.AreEqual(0L, other.AllocatedBytes);
        Assert.IsTrue(other.Flags.HasFlag(NodeMarks.HardLinkCountedElsewhere));
        Assert.AreEqual(counted.AllocatedBytes, tree.Root.AllocatedBytes);
        Assert.IsNotNull(counted.Stamp);
        Assert.AreEqual(ownLength, counted.Stamp.Value.Length, "the stamp holds the file's own length");
    }

    // docs/spec/01 "Entries are read in batches per folder, never by opening each file"; Windows note (3) "has its allocated
    // size asked for once" (brief M31): a folder of ordinary files over 1 KiB asks no file; one compressed file is asked
    // once; a compressed file with two names is asked once (the walk's "allocation asked" diagnostic).
    [TestMethod]
    public async Task OnlyZeroAllocatedLargeFilesAreAskedOnce()
    {
        using var plain = new TempFolder("alloc-asked-plain");
        plain.File("a.bin", 2 * ByteUnits.KB);
        plain.File("b.bin", 100 * ByteUnits.KB);
        plain.File(@"sub\c.bin", ByteUnits.MB);
        var plainTree = await Scan(plain.Path);
        Assert.AreEqual(0L, plainTree.AllocationFilesOpened, "ordinary files are measured from the folder batches");

        using var one = new TempFolder("alloc-asked-one");
        var wof = one.Sub("wof.txt");
        WriteCompressible(wof, FourHundredKb);
        CompactWithWof(wof);
        one.File("plain.bin", 100 * ByteUnits.KB);
        var oneTree = await Scan(one.Path);
        Assert.AreEqual(1L, oneTree.AllocationFilesOpened, "one compressed file is asked once");
        Assert.AreEqual(Native.CompressedSizeOf(wof), Trees.Node(oneTree, "wof.txt").AllocatedBytes);

        using var linked = new TempFolder("alloc-asked-links");
        var first = linked.Sub("a-first.txt");
        WriteCompressible(first, FourHundredKb);
        Native.CreateHardLink(linked.Sub("b-second.txt"), first);
        CompactWithWof(first);
        var linkedTree = await Scan(linked.Path);
        Assert.AreEqual(1L, linkedTree.AllocationFilesOpened, "a compressed file with two names is asked once");
        Assert.AreEqual(Native.CompressedSizeOf(first), linkedTree.Root.AllocatedBytes, "and counted once");
    }

    // Windows note (3) (brief M32): a compressed file directly at a volume root (the test machine's data disk, E:) is
    // measured at its compressed size, not unreadable.
    [TestMethod]
    public async Task CompressedFileAtAVolumeRootIsMeasured()
    {
        const string volume = @"E:\";
        if (!Directory.Exists(volume))
        {
            Assert.Inconclusive("needs the test machine's data disk at " + volume);
        }

        var file = Path.Combine(volume, "LeanCoreTests-wof-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            try
            {
                WriteCompressible(file, FourHundredKb);
            }
            catch (UnauthorizedAccessException e)
            {
                Assert.Inconclusive("the standard user may not write at the root of " + volume + ": " + e.Message);
            }

            CompactWithWof(file);
            var oracle = Native.CompressedSizeOf(file);
            var tree = await Scan(volume);
            var node = tree.Root.Children.SingleOrDefault(n => string.Equals(n.Name, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(node, "the file at the volume root is in the tree");
            Assert.IsFalse(node.Flags.HasFlag(NodeMarks.Unreadable), "the compressed file at the volume root is marked unreadable");
            Assert.AreEqual(FourHundredKb, node.LogicalBytes);
            Assert.AreEqual(oracle, node.AllocatedBytes, "its compressed size, as the file system reports it");
            Check.True(node.AllocatedBytes > 0 && node.AllocatedBytes < node.LogicalBytes, $"compressed {node.AllocatedBytes} of {node.LogicalBytes}");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
