using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>docs/spec/02: "A file with more than one hard link is noted 'Frees nothing while other links exist'"; plan
/// group 11 UI contract: the confirmation shows the line from <see cref="HardLinkNote.For"/> for a file with more than one
/// link. The note is read at the time of the call (the link count at move time), not from the scan; a folder and a cloud
/// file never get it. Links are made with CreateHardLinkW in a temp folder.</summary>
[TestClass]
public sealed class HardLinkNoteTests
{
    private static ScanNode FileNode(string path) => ScanNode.File(path, new FileInfo(path).Length, new FileInfo(path).Length, 0);

    // docs/spec/02: a file with 2 links gets the note.
    [TestMethod]
    public void AFileWithTwoLinksGetsTheNote()
    {
        using var temp = new TempFolder("hardlink-two");
        var first = temp.File("one.bin", 8192);
        Native.CreateHardLink(temp.Sub("two.bin"), first);
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(FileNode(first)));
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(FileNode(temp.Sub("two.bin"))), "the second name is a link of the same file");
    }

    // docs/spec/02: a file with one link gets no note.
    [TestMethod]
    public void AFileWithOneLinkGetsNone()
    {
        using var temp = new TempFolder("hardlink-one");
        var only = temp.File("only.bin", 8192);
        Assert.IsNull(HardLinkNote.For(FileNode(only)));
    }

    // The note is about the link count when it is asked (the confirmation, at move time): a link removed after the node
    // was made leaves one link, and no note.
    [TestMethod]
    public void TheLinkCountIsReadWhenAsked()
    {
        using var temp = new TempFolder("hardlink-live");
        var first = temp.File("one.bin", 8192);
        var second = temp.Sub("two.bin");
        Native.CreateHardLink(second, first);
        var node = FileNode(first);
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(node), "two links");
        File.Delete(second); // the test's own fixture link
        Assert.IsNull(HardLinkNote.For(node), "one link left");
    }

    // A folder never gets the note (its link count is not a hard-link count).
    [TestMethod]
    public void AFolderGetsNone()
    {
        using var temp = new TempFolder("hardlink-folder");
        var folder = temp.Folder("sub");
        var inside = temp.File(@"sub\one.bin", 8192);
        Native.CreateHardLink(temp.Sub("outside.bin"), inside);
        var node = ScanNode.Folder(folder);
        node.Add(FileNode(inside));
        Assert.IsNull(HardLinkNote.For(node), "a folder holding a linked file is not itself a linked file");
    }

    // A cloud-marked node (walk rule 6: never opened) gets none, even when the file at its path has two links: the mark
    // alone excludes it.
    [TestMethod]
    public void ACloudMarkedNodeGetsNone()
    {
        using var temp = new TempFolder("hardlink-cloud");
        var first = temp.File("one.bin", 8192);
        Native.CreateHardLink(temp.Sub("two.bin"), first);
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(FileNode(first)), "precondition: the file has two links");
        var cloud = ScanNode.File(first, 8192, 8192, 0, flags: NodeMarks.CloudNotDownloaded);
        Assert.IsNull(HardLinkNote.For(cloud));
    }

    // The nodes a real scan makes: both names of a linked file get the note (the one counted elsewhere too), the plain
    // file and the folder none; a link outside the scanned tree is found too (plan Walker: "found only at move time").
    [TestMethod]
    public async Task NodesOfARealScanGetTheNoteOnlyForLinkedFiles()
    {
        using var temp = new TempFolder("hardlink-scan");
        var root = temp.Folder("scan");
        var linked = temp.File(@"scan\linked.bin", 8192, seed: 2);
        Native.CreateHardLink(temp.Sub("scan", "linked-too.bin"), linked);
        var outsideLinked = temp.File(@"scan\sub\outside-linked.bin", 8192, seed: 3);
        Native.CreateHardLink(temp.Sub("elsewhere.bin"), outsideLinked);
        temp.File(@"scan\plain.bin", 8192, seed: 4);

        var tree = await new Scanner().ScanAsync(root, null, CancellationToken.None);
        var nodes = Flatten(tree.Root).ToDictionary(n => Path.GetFileName(n.Path), n => n, System.StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(nodes["linked.bin"]));
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(nodes["linked-too.bin"]));
        Assert.AreEqual(Text.FreesNothingWhileLinked, HardLinkNote.For(nodes["outside-linked.bin"]), "the other link is outside the scanned tree");
        Assert.IsNull(HardLinkNote.For(nodes["plain.bin"]));
        Assert.IsNull(HardLinkNote.For(nodes["sub"]));
        Assert.IsNull(HardLinkNote.For(tree.Root));
    }

    private static System.Collections.Generic.IEnumerable<ScanNode> Flatten(ScanNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var below in Flatten(child))
            {
                yield return below;
            }
        }
    }
}
