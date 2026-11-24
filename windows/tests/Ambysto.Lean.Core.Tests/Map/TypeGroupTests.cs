using Ambysto.Lean.Core.Map;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Map;

/// <summary>docs/spec/01 (Map colours) and docs/spec/03 (type groups): video, disk image, archive, document, application,
/// virtual machine image (.vmdk, .qcow2, .utm, .vdi, Docker.raw; Windows note: also .vhdx and .vhd), other.</summary>
[TestClass]
public sealed class TypeGroupTests
{
    // docs/spec/03 type groups: virtual machine image (.vmdk, .qcow2, .utm, .vdi, Docker.raw).
    [TestMethod]
    [DataRow(@"C:\VMs\disk.vmdk")]
    [DataRow(@"C:\VMs\disk.qcow2")]
    [DataRow(@"C:\VMs\disk.vdi")]
    [DataRow(@"C:\VMs\machine.utm")]
    [DataRow(@"C:\Users\me\AppData\Local\Docker\Docker.raw")]
    public void VirtualMachineImagesOfTheSpecList(string path) => Assert.AreEqual(TypeGroupKind.VirtualMachineImage, TypeGroup.Of(path));

    // docs/spec/03 Windows note: virtual machine images also include .vhdx and .vhd.
    [TestMethod]
    [DataRow(@"C:\Users\me\AppData\Local\Docker\wsl\data\ext4.vhdx")]
    [DataRow(@"D:\Hyper-V\old.vhd")]
    [DataRow(@"D:\Hyper-V\UPPER.VHDX")]
    public void VhdxAndVhdAreVirtualMachineImages(string path) => Assert.AreEqual(TypeGroupKind.VirtualMachineImage, TypeGroup.Of(path));

    // docs/spec/01 Map colours: video.
    [TestMethod]
    [DataRow(@"C:\Users\me\Videos\clip.mp4")]
    [DataRow(@"C:\Users\me\Videos\clip.mkv")]
    [DataRow(@"C:\Users\me\Videos\clip.mov")]
    [DataRow(@"C:\Users\me\Videos\clip.avi")]
    [DataRow(@"C:\Users\me\Videos\CLIP.MP4")]
    public void CommonVideoExtensionsAreVideo(string path) => Assert.AreEqual(TypeGroupKind.Video, TypeGroup.Of(path));

    // docs/spec/01 Map colours: disk image.
    [TestMethod]
    [DataRow(@"C:\Users\me\Downloads\win11.iso")]
    [DataRow(@"C:\Users\me\Downloads\card.img")]
    public void CommonDiskImageExtensionsAreDiskImages(string path) => Assert.AreEqual(TypeGroupKind.DiskImage, TypeGroup.Of(path));

    // docs/spec/01 Map colours: archive.
    [TestMethod]
    [DataRow(@"C:\Users\me\Downloads\bundle.zip")]
    [DataRow(@"C:\Users\me\Downloads\bundle.7z")]
    [DataRow(@"C:\Users\me\Downloads\bundle.rar")]
    [DataRow(@"C:\Users\me\Downloads\bundle.tar.gz")]
    public void CommonArchiveExtensionsAreArchives(string path) => Assert.AreEqual(TypeGroupKind.Archive, TypeGroup.Of(path));

    // docs/spec/01 Map colours: document.
    [TestMethod]
    [DataRow(@"C:\Users\me\Documents\report.pdf")]
    [DataRow(@"C:\Users\me\Documents\report.docx")]
    [DataRow(@"C:\Users\me\Documents\sheet.xlsx")]
    [DataRow(@"C:\Users\me\Documents\deck.pptx")]
    public void CommonDocumentExtensionsAreDocuments(string path) => Assert.AreEqual(TypeGroupKind.Document, TypeGroup.Of(path));

    // docs/spec/01 Map colours: application.
    [TestMethod]
    [DataRow(@"C:\Users\me\Downloads\setup.exe")]
    [DataRow(@"C:\Users\me\Downloads\setup.msi")]
    public void CommonApplicationExtensionsAreApplications(string path) => Assert.AreEqual(TypeGroupKind.Application, TypeGroup.Of(path));

    // docs/spec/01 Map colours: other.
    [TestMethod]
    [DataRow(@"C:\Users\me\Documents\data.unknownext")]
    [DataRow(@"C:\Users\me\Documents\noextension")]
    [DataRow(@"C:\Users\me\Documents\.gitignore")]
    public void UnknownExtensionsAreOther(string path) => Assert.AreEqual(TypeGroupKind.Other, TypeGroup.Of(path));

    // docs/spec/01 Map colours: Of never answers the folder tint for a file name.
    [TestMethod]
    public void AFileIsNeverTheOpenFolderTint()
    {
        Assert.AreNotEqual(TypeGroupKind.OpenFolder, TypeGroup.Of(@"C:\x\a.mp4"));
        Assert.AreNotEqual(TypeGroupKind.OpenFolder, TypeGroup.Of(@"C:\x\a"));
    }

    // docs/spec/01 Map: a folder drawn as a single tile takes the colour of the file reached by following its largest child.
    [TestMethod]
    public void ColourOfAFolderFollowsItsLargestChildChain()
    {
        const string root = @"C:\Users\me\Stuff";
        var folder = ScanNode.Folder(root)
            .Add(ScanNode.Folder(root + @"\big")
                .Add(Trees.File(root + @"\big\movie.mkv", 900))
                .Add(Trees.File(root + @"\big\notes.pdf", 100)))
            .Add(ScanNode.Folder(root + @"\small")
                .Add(Trees.File(root + @"\small\archive.zip", 500)));
        var tree = Trees.Tree(folder, 1500);
        Assert.AreEqual(TypeGroupKind.Video, TypeGroup.ColourOf(tree.Root), "root → big (1000) → movie.mkv");
        Assert.AreEqual(TypeGroupKind.Archive, TypeGroup.ColourOf(Trees.Node(tree, "small")));
        Assert.AreEqual(TypeGroupKind.Document, TypeGroup.ColourOf(Trees.Node(tree, @"big\notes.pdf")), "a file's own group");
    }

    // docs/spec/01 Map: a folder with nothing inside has no file to follow; it is not an error.
    [TestMethod]
    public void ColourOfAnEmptyFolderIsOther()
    {
        var tree = Trees.Tree(ScanNode.Folder(@"C:\Users\me\Empty"), 0);
        Assert.AreEqual(TypeGroupKind.Other, TypeGroup.ColourOf(tree.Root));
    }
}
