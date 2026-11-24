using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-15 (intent/2026-10-18-asp-15-duplicates-helpers.md, "Preview, the cloud-file guard"), the parts a session-0
/// run can show: the eight preview types; a local file read in full, up to 20 MB; a larger file not read; a missing file
/// unreadable; a file with the offline attribute never previewed. The online-only placeholder cases are in
/// <see cref="PicturePreviewCloudTests"/>. Written by the test writer before the Core.</summary>
[TestClass]
public sealed class PicturePreviewTests
{
    private const long TwentyMb = 20 * ByteUnits.MB;

    // Preview types: ".jpg, .jpeg, .png, .gif, .bmp, .webp, .tif and .tiff", case ignored.
    [TestMethod]
    public void TheEightPictureTypesAreRecognisedInAnyCase()
    {
        foreach (var path in new[]
        {
            @"C:\p\a.jpg", @"C:\p\a.JPG", @"C:\p\a.jpeg", @"C:\p\a.JpEg", @"C:\p\a.png", @"C:\p\a.PNG", @"C:\p\a.gif", @"C:\p\a.Gif",
            @"C:\p\a.bmp", @"C:\p\a.BMP", @"C:\p\a.webp", @"C:\p\a.WebP", @"C:\p\a.tif", @"C:\p\a.TIF", @"C:\p\a.tiff", @"C:\p\a.TiFf",
            @"C:\p\many.dots.in.name.png",
        })
        {
            Check.True(PicturePreview.IsPictureType(path), path + " is a preview type");
        }
    }

    // Preview types: pictures only, and only the eight; another type, a type hidden behind another extension, a folder
    // with a picture-like name and a name with no extension are not.
    [TestMethod]
    public void OtherTypesAreNotPictureTypes()
    {
        foreach (var path in new[]
        {
            @"C:\p\a.heic", @"C:\p\a.HEIC", @"C:\p\a.mp4", @"C:\p\a.raw", @"C:\p\a.svg", @"C:\p\a.ico", @"C:\p\a.pdf",
            @"C:\p\photo", @"C:\p\jpg", @"C:\p\a.jpg.txt", @"C:\p\a.png.lnk", @"C:\p\a.jpgx", @"C:\p\a.tif2",
            @"C:\p.jpg\readme", @"C:\p\a.",
        })
        {
            Check.False(PicturePreview.IsPictureType(path), path + " is not a preview type");
        }
    }

    // Guard: a local file (no cloud attribute) is opened and read; the result carries its exact bytes.
    [TestMethod]
    public void ALocalPictureIsReadWithItsExactBytes()
    {
        using var temp = new TempFolder("preview-local");
        var path = temp.File("photo.png", 300 * ByteUnits.KB, seed: 5);
        var expected = File.ReadAllBytes(path);

        var read = PicturePreview.Read(path);
        Assert.AreEqual(PreviewResult.Read, read.Result);
        Assert.IsNotNull(read.Bytes, "Read carries the bytes");
        Assert.AreEqual(expected.Length, read.Bytes.Length);
        Check.True(expected.AsSpan().SequenceEqual(read.Bytes), "the bytes are the file's exact bytes");
    }

    // Guard: "reads at most 20 MB": a file of exactly 20 MB is read in full (its last byte included). The file is made by
    // setting its length (NTFS writes no data for it), so the test stays fast.
    [TestMethod]
    public void AFileOfExactlyTwentyMegabytesIsRead()
    {
        Assert.AreEqual(TwentyMb, Check.Constant<long>(typeof(PicturePreview), nameof(PicturePreview.MaxBytes)), "the limit is 20 MB");
        using var temp = new TempFolder("preview-limit");
        var path = temp.Sub("limit.jpg");
        MakeFile(path, TwentyMb, lastByte: 0x5A);

        var read = PicturePreview.Read(path);
        Assert.AreEqual(PreviewResult.Read, read.Result, "a file of exactly the limit is read");
        Assert.IsNotNull(read.Bytes);
        Assert.AreEqual(TwentyMb, read.Bytes.LongLength);
        Assert.AreEqual((byte)0x5A, read.Bytes[^1], "the last byte was read");
    }

    // Guard: "reads at most 20 MB": one byte more is TooLarge, with no bytes. Made by setting the length, so it is fast.
    [TestMethod]
    public void AFileOverTwentyMegabytesIsTooLargeAndNotRead()
    {
        Assert.AreEqual(TwentyMb, Check.Constant<long>(typeof(PicturePreview), nameof(PicturePreview.MaxBytes)), "the limit is 20 MB");
        using var temp = new TempFolder("preview-large");
        var path = temp.Sub("large.jpg");
        MakeFile(path, TwentyMb + 1, lastByte: 0x5A);

        var read = PicturePreview.Read(path);
        Assert.AreEqual(PreviewResult.TooLarge, read.Result);
        Assert.IsNull(read.Bytes, "a file over the limit gives no bytes");
    }

    // Guard: "the file could not be opened or read": a missing file is Unreadable, with no bytes (and no exception).
    [TestMethod]
    public void AMissingFileIsUnreadable()
    {
        using var temp = new TempFolder("preview-missing");
        var read = PicturePreview.Read(temp.Sub("missing.jpg"));
        Assert.AreEqual(PreviewResult.Unreadable, read.Result);
        Assert.IsNull(read.Bytes);
    }

    // Guard: "A file with ... FILE_ATTRIBUTE_OFFLINE gets no preview": a local file carrying the offline attribute is
    // OnlineOnly, with no bytes, although its data is on this disk.
    [TestMethod]
    public void AFileWithTheOfflineAttributeIsOnlineOnly()
    {
        using var temp = new TempFolder("preview-offline");
        var path = temp.File("offline.jpg", 64 * ByteUnits.KB, seed: 9);
        File.SetAttributes(path, FileAttributes.Offline);
        Check.True(File.GetAttributes(path).HasFlag(FileAttributes.Offline), "precondition: the file has the offline attribute");

        var read = PicturePreview.Read(path);
        Assert.AreEqual(PreviewResult.OnlineOnly, read.Result);
        Assert.IsNull(read.Bytes, "an online-only file gives no bytes");
    }

    // Guard (hard review 2026-10-18): "a folder -> Unreadable": a folder, even one named like a picture, is never read.
    [TestMethod]
    public void AFolderIsUnreadable()
    {
        using var temp = new TempFolder("preview-folder");
        var folder = temp.Folder("album.jpg");
        temp.File(@"album.jpg\inside.jpg", 4 * ByteUnits.KB);
        var read = PicturePreview.Read(folder);
        Assert.AreEqual(PreviewResult.Unreadable, read.Result);
        Assert.IsNull(read.Bytes);
    }

    // Guard: the extended-length spelling \\?\C:\... of a local picture names the same local file: it is Read with its bytes.
    [TestMethod]
    public void TheExtendedLengthSpellingOfALocalPictureIsRead()
    {
        using var temp = new TempFolder("preview-extended");
        var path = temp.File("photo.png", 40 * ByteUnits.KB, seed: 11);
        var extended = @"\\?\" + Path.GetFullPath(path);
        Check.True(extended.StartsWith(@"\\?\", StringComparison.Ordinal) && extended[5] == ':', "precondition: a \\\\?\\<drive>: spelling: " + extended);

        var read = PicturePreview.Read(extended);
        Assert.AreEqual(PreviewResult.Read, read.Result);
        Assert.IsNotNull(read.Bytes);
        Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(read.Bytes), "the bytes are the file's exact bytes");
    }

    // Guard: a 0-byte local file is under the limit and readable: Read with an empty array (not null).
    [TestMethod]
    public void AnEmptyFileIsReadAsAnEmptyArray()
    {
        using var temp = new TempFolder("preview-empty");
        var path = temp.Sub("empty.gif");
        File.WriteAllBytes(path, []);

        var read = PicturePreview.Read(path);
        Assert.AreEqual(PreviewResult.Read, read.Result);
        Assert.IsNotNull(read.Bytes, "Read carries an array, empty");
        Assert.AreEqual(0, read.Bytes.Length);
    }

    // Guard (hard review 2026-10-18, MAJOR): "a name-surrogate reparse tag (a symbolic link) -> Unreadable": links are never
    // followed, even to a local picture. Making a file symbolic link needs Developer Mode or the privilege, which the
    // test machine's standard user lacks: category Admin (elevated over SSH); Inconclusive where no link can be made.
    [TestMethod]
    [TestCategory("Admin")]
    public void ASymbolicLinkToALocalPictureIsUnreadable()
    {
        using var temp = new TempFolder("preview-link-local");
        var target = temp.File(@"real\photo.jpg", 40 * ByteUnits.KB, seed: 13);
        var link = temp.Sub("x.jpg");
        MakeFileLinkOrInconclusive(link, target);

        var read = PicturePreview.Read(link);
        Assert.AreEqual(PreviewResult.Unreadable, read.Result, "a symbolic link is never followed");
        Assert.IsNull(read.Bytes);
        Assert.AreEqual(PreviewResult.Read, PicturePreview.Read(target).Result, "control: the target itself is a readable local picture");
    }

    /// <summary>Makes <paramref name="link"/> a file symbolic link to <paramref name="target"/> (.NET asks with
    /// SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE), or ends the test Inconclusive when this user may not.</summary>
    internal static void MakeFileLinkOrInconclusive(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (IOException e)
        {
            Assert.Inconclusive("a file symbolic link needs Developer Mode or the privilege, which this user lacks: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Assert.Inconclusive("a file symbolic link needs Developer Mode or the privilege, which this user lacks: " + e.Message);
        }

        var info = new FileInfo(link);
        Check.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null, "precondition: " + link + " is a symbolic link");
    }

    /// <summary>A file of <paramref name="length"/> bytes, zeros except the last one, made by setting its length.</summary>
    private static void MakeFile(string path, long length, byte lastByte)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Position = length - 1;
        stream.WriteByte(lastByte);
    }
}

/// <summary>ASP-15 (intent "Preview, the cloud-file guard", and its proof): an AmbyCloud online-only picture gets no
/// preview, AmbyCloud's fetch log shows no read by this process, and the placeholder stays online-only; a hydrated one
/// gets a preview. Real placeholders from the test machine's fake provider (AmbyCloud, the test-machine notes); the fetch log is
/// shown able to fail by a positive control (this process reads a control placeholder and the log must record it with
/// this pid). Console session only, as CloudPlaceholderTests: in session 0 some reads are refused
/// before the provider sees them. One fixture at a time. Written by the test writer before the Core.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PicturePreviewCloudTests
{
    private const string Control = "control.bin";
    private const string Photo = "photo.jpg";
    private const long PhotoBytes = 64 * ByteUnits.KB;

    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRoots();
        }
    }

    private static bool IsThisProcess(string line) =>
        line.Contains("\"pid\":" + Environment.ProcessId + ",", StringComparison.Ordinal) || line.Contains("\"pid\":" + Environment.ProcessId + "}", StringComparison.Ordinal);

    private static List<string> FetchesByThisProcess(CloudRoot cloud, string relative) => cloud.FetchesOf(relative).Where(IsThisProcess).ToList();

    /// <summary>The positive control: this process reads one byte of <see cref="Control"/>, and the log must show it with
    /// this pid; then the baseline moves past it.</summary>
    private static void ProveTheOracleCanFail(CloudRoot cloud)
    {
        cloud.RequireOnlineOnly(Control);
        using (var stream = new FileStream(cloud.Combine(Control), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.True(stream.ReadByte() >= 0, "the control read returned nothing");
        }

        Check.True(FetchesByThisProcess(cloud, Control).Count > 0, $"the fetch log did not record this process's (pid {Environment.ProcessId}) read of {Control}; all fetches: [{string.Join(" | ", cloud.Fetches())}]");
        cloud.MarkFetchBaseline();
    }

    /// <summary>AmbyCloud's pattern: byte i = (i*31 + 7) &amp; 0xFF.</summary>
    private static byte[] Pattern(long length)
    {
        var bytes = new byte[length];
        for (long i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        return bytes;
    }

    // Guard: "A file with FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS ... gets no preview" and "No download of a cloud file is
    // ever triggered": an online-only picture is OnlineOnly with no bytes; the fetch log shows no fetch at all since the
    // control (none by this process), and the placeholder is still online-only with nothing on disk.
    [TestMethod]
    [TestCategory("Console")]
    public void AnOnlineOnlyPictureGetsNoPreviewAndIsNeverFetched()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-preview-online");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((Photo, PhotoBytes), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly(Photo);
        ProveTheOracleCanFail(cloud);

        var read = PicturePreview.Read(cloud.Combine(Photo));
        Assert.AreEqual(PreviewResult.OnlineOnly, read.Result);
        Assert.IsNull(read.Bytes, "an online-only picture gives no bytes");
        Assert.AreEqual(0, FetchesByThisProcess(cloud, Photo).Count, $"this process (pid {Environment.ProcessId}) fetched {Photo}: [{string.Join(" | ", cloud.FetchesOf(Photo))}]");
        cloud.AssertNoFetch("the preview of an online-only picture");
        cloud.RequireOnlineOnly(Photo);
    }

    // Guard: "Otherwise Lean opens the file with FILE_FLAG_OPEN_NO_RECALL" and preview "local files only": a cloud picture
    // already downloaded (hydrated) is local, so it is Read with its exact bytes, and nothing more is fetched.
    [TestMethod]
    [TestCategory("Console")]
    public void AHydratedCloudPictureIsRead()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-preview-hydrated");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((Photo, PhotoBytes), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly(Photo);
        ProveTheOracleCanFail(cloud);

        // Hydrate the picture as this process (the provider serves its pattern), then check it is local.
        var expected = Pattern(PhotoBytes);
        Check.True(File.ReadAllBytes(cloud.Combine(Photo)).AsSpan().SequenceEqual(expected), "precondition: the hydrated picture holds AmbyCloud's pattern");
        var status = cloud.Status(Photo);
        Check.True(!status.RecallOnDataAccess && status.OnDisk >= PhotoBytes, $"precondition: {Photo} is local (on disk {status.OnDisk}, recall {status.RecallOnDataAccess})");
        cloud.MarkFetchBaseline();

        var read = PicturePreview.Read(cloud.Combine(Photo));
        Assert.AreEqual(PreviewResult.Read, read.Result, "a hydrated picture is local and gets a preview");
        Assert.IsNotNull(read.Bytes);
        Check.True(expected.AsSpan().SequenceEqual(read.Bytes), "the bytes are the picture's exact bytes");
        cloud.AssertNoFetch("the preview of a hydrated picture");
    }

    // Guard (hard review 2026-10-18, MAJOR): a symbolic link x.jpg to an online-only picture is never followed: Unreadable,
    // no bytes, no fetch by this process (none at all since the control), and the placeholder is still online-only.
    // Console only: the fetch log's positive control fails in session 0, elevated too ("Access to the cloud file is
    // denied", measured 2026-10-18), so an Admin run proves nothing. The link needs Developer Mode or the privilege, which
    // the console's standard user lacks on the test machine today: Inconclusive there until one of them is given.
    [TestMethod]
    [TestCategory("Console")]
    public void ASymbolicLinkToAnOnlineOnlyPictureIsUnreadableAndNeverFetched()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-preview-link");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((Photo, PhotoBytes), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly(Photo);
        var link = temp.Sub("x.jpg");
        PicturePreviewTests.MakeFileLinkOrInconclusive(link, cloud.Combine(Photo));
        ProveTheOracleCanFail(cloud);

        var read = PicturePreview.Read(link);
        Assert.AreEqual(PreviewResult.Unreadable, read.Result, "a link to an online-only picture is never followed");
        Assert.IsNull(read.Bytes);
        Assert.AreEqual(0, FetchesByThisProcess(cloud, Photo).Count, $"this process (pid {Environment.ProcessId}) fetched {Photo}: [{string.Join(" | ", cloud.FetchesOf(Photo))}]");
        cloud.AssertNoFetch("the preview through a link to an online-only picture");
        cloud.RequireOnlineOnly(Photo);
    }
}
