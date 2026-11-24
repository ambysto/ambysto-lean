using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Measure;

/// <summary>docs/spec/01 walk rule 6 (Windows note 6) and docs/spec/03 Duplicates "cloud placeholders": online-only files
/// are counted, never fetched (no data read) and never compared. Real placeholders from the test machine's fake provider
/// (AmbyCloud, the test-machine notes); its fetch log is the oracle for "no data read", shown able to fail by a positive
/// control in every test: the test process reads one byte of a placeholder made for that purpose and the log must
/// record that read with this process's pid. Console session only: there, a read by any process reaches the provider
/// (in session 0 some readers are refused before the provider sees them, so the control could not be trusted).
/// One fixture at a time: the class never runs in parallel, and stale roots of earlier runs are swept first.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CloudPlaceholderTests
{
    private const string Control = "control.bin";
    private const long OneHundredKb = 100 * ByteUnits.KB;
    private const long TwoMb = 2 * ByteUnits.MB;

    [ClassInitialize]
    public static void SweepStaleRoots(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File.Exists(CloudRoot.ExePath))
        {
            CloudRoot.SweepStaleRoots();
        }
    }

    /// <summary>The positive control: this process reads one byte of <see cref="Control"/>, and the fetch log must show
    /// it with this pid. Then the baseline moves past it, so the assertions that follow see only new fetches.</summary>
    private static void ProveTheOracleCanFail(CloudRoot cloud, string control = Control)
    {
        cloud.RequireOnlineOnly(control);
        using (var stream = new FileStream(cloud.Combine(control), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var first = stream.ReadByte();
            Check.True(first >= 0, "the control read returned nothing");
        }

        var mine = cloud.FetchesOf(control).Where(line => line.Contains("\"pid\":" + Environment.ProcessId + ",", StringComparison.Ordinal) || line.Contains("\"pid\":" + Environment.ProcessId + "}", StringComparison.Ordinal)).ToList();
        Check.True(mine.Count > 0, $"the fetch log did not record this process's (pid {Environment.ProcessId}) read of {control}; all fetches: [{string.Join(" | ", cloud.Fetches())}]");
        cloud.MarkFetchBaseline();
    }

    // docs/spec/01 walk rule 6 (Windows note 6): a file with recall-on-data-access is counted as a cloud file and never
    // fetched (no data read); "N cloud files not downloaded".
    [TestMethod]
    [TestCategory("Console")]
    public async Task CloudPlaceholdersAreCountedAndNeverFetched()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-walk");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate(("a.bin", ByteUnits.MB), (@"sub\b.bin", 64 * ByteUnits.KB), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly("a.bin");
        cloud.RequireOnlineOnly(@"sub\b.bin");
        temp.File("plain.bin", OneHundredKb);
        ProveTheOracleCanFail(cloud);

        var tree = await new Scanner().ScanAsync(temp.Path, null, CancellationToken.None);
        var a = Trees.Node(tree, @"cloud\a.bin");
        var b = Trees.Node(tree, @"cloud\sub\b.bin");
        Assert.IsTrue(a.Flags.HasFlag(NodeMarks.CloudNotDownloaded), "a.bin is a cloud file not downloaded");
        Assert.IsTrue(b.Flags.HasFlag(NodeMarks.CloudNotDownloaded), "sub\\b.bin is a cloud file not downloaded");
        Assert.IsFalse(Trees.Node(tree, "plain.bin").Flags.HasFlag(NodeMarks.CloudNotDownloaded));
        Assert.AreEqual(2L, tree.Coverage.CloudNotDownloadedCount, "the control is hydrated by now and no longer a cloud file not downloaded");
        Assert.AreEqual(ByteUnits.MB, a.LogicalBytes, "the placeholder's length is known without reading it");
        Check.AtMost(64 * ByteUnits.KB, a.AllocatedBytes, "nothing of the 1 MB is on this disk (walk rule 3)");
        cloud.AssertNoFetch("the walk");
        Assert.AreEqual(0L, cloud.Status("a.bin").OnDisk, "the walk fetched a.bin");
        Assert.AreEqual(0L, cloud.Status(@"sub\b.bin").OnDisk, "the walk fetched sub\\b.bin");

        // Walk rule 6, "never entered" (brief M34): a cloud file is counted as one, not as an entry that could not be read.
        Assert.AreEqual(0L, tree.Coverage.UnreadableCount, "unreadable entries in a walk of readable folders and cloud files");
        Assert.IsFalse(Trees.All(tree.Root).Any(n => n.Flags.HasFlag(NodeMarks.CloudNotDownloaded) && n.Flags.HasFlag(NodeMarks.Unreadable)), "a cloud file carries the unreadable mark");
        Assert.IsFalse(a.Flags.HasFlag(NodeMarks.Unreadable), "a.bin is marked unreadable");
        Assert.IsFalse(b.Flags.HasFlag(NodeMarks.Unreadable), "sub\\b.bin is marked unreadable");
    }

    // docs/spec/01 walk rule 6: a placeholder folder (in sync, populated) is entered like a folder; only files are cloud files.
    [TestMethod]
    [TestCategory("Console")]
    public async Task PopulatedPlaceholderFolderIsEnteredAndNotCountedAsACloudFile()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-walk-folder");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((@"docs\", 0), (@"docs\c.bin", 4 * ByteUnits.KB), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        ProveTheOracleCanFail(cloud);

        var tree = await new Scanner().ScanAsync(temp.Path, null, CancellationToken.None);
        var docs = Trees.Node(tree, @"cloud\docs");
        Assert.IsTrue(docs.IsFolder);
        Assert.IsFalse(docs.Flags.HasFlag(NodeMarks.CloudNotDownloaded), "a populated folder placeholder is not a cloud file");
        Assert.IsTrue(Trees.Node(tree, @"cloud\docs\c.bin").Flags.HasFlag(NodeMarks.CloudNotDownloaded));
        Assert.AreEqual(1L, tree.Coverage.CloudNotDownloadedCount);
        cloud.AssertNoFetch("the walk");
    }

    // docs/spec/03 Duplicates: cloud placeholders are excluded and never fetched (docs/spec/01 rule 6; intent criterion 5:
    // online-only files never opened: here, no data read, which is what a fetch log can show). A real placeholder of
    // the same length as two identical copies: the copies form their group, the placeholder is in no group and stays
    // in the cloud.
    [TestMethod]
    [TestCategory("Console")]
    public async Task CloudPlaceholdersAreNeverComparedOrFetched()
    {
        CloudRoot.RequireInstalled();
        using var fx = new ProfileFixture("cloud-dup");
        using var cloud = CloudRoot.Register(fx.InProfile("Documents", "Cloud"), fx.OnVolume("fetch.log"));
        cloud.Populate(("a.bin", TwoMb), (@"sub\a.bin", TwoMb), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly("a.bin");
        ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 27);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 27);
        ProveTheOracleCanFail(cloud);

        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(1, groups.Count, "the two real copies form one group; the placeholders join none");
        Check.Sequence(groups[0].Copies.Select(c => c.Node.Name).Order(StringComparer.Ordinal), "a.bin", "b.bin");
        Assert.IsFalse(groups[0].Copies.Any(c => c.Node.Path.StartsWith(cloud.Root, StringComparison.OrdinalIgnoreCase)), "a placeholder is in a group");
        cloud.AssertNoFetch("the walk and the duplicates search");
        Assert.AreEqual(0L, cloud.Status("a.bin").OnDisk, "the search fetched a placeholder");
        Assert.AreEqual(0L, cloud.Status(@"sub\a.bin").OnDisk, "the search fetched a placeholder");
    }

    // docs/spec/01 rule 6 and 03 "cloud placeholders" excluded; intent criterion 5 (brief M08): a file that was local at
    // scan time and went online-only before the duplicates search is never read: it joins no group (though its bytes
    // equal the two local copies') and the fetch log stays empty. The cloud copy is hydrated by this process before the
    // scan (so the walk sees it local), then dehydrated by the provider; the oracle is proved able to fail before the
    // scan and again after the provider restarted.
    [TestMethod]
    [TestCategory("Console")]
    public async Task FileMadeOnlineOnlyAfterTheScanIsNeverRead()
    {
        CloudRoot.RequireInstalled();
        const string Control2 = "control2.bin";
        using var fx = new ProfileFixture("cloud-dup-dehydrated");
        using var cloud = CloudRoot.Register(fx.InProfile("Documents", "Cloud"), fx.OnVolume("fetch.log"));
        cloud.Populate(("c.bin", TwoMb), (Control, 4 * ByteUnits.KB), (Control2, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly("c.bin");

        // Seed 0 writes AmbyCloud's own pattern (byte i = (i*31 + 7) & 0xFF), so the cloud file's bytes equal the copies'.
        var a = ProfileFixture.MakeFile(fx.InProfile("Documents", "a.bin"), TwoMb, seed: 0);
        ProfileFixture.MakeFile(fx.InProfile("Documents", "b.bin"), TwoMb, seed: 0);
        var cloudBytes = File.ReadAllBytes(cloud.Combine("c.bin"));
        Check.True(File.ReadAllBytes(a).AsSpan().SequenceEqual(cloudBytes), "precondition: the cloud copy's bytes equal the local copies'");
        var hydrated = cloud.Status("c.bin");
        Check.True(!hydrated.RecallOnDataAccess && hydrated.OnDisk >= TwoMb, $"precondition: c.bin is local before the scan (on disk {hydrated.OnDisk}, recall {hydrated.RecallOnDataAccess})");
        ProveTheOracleCanFail(cloud);

        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        Assert.IsFalse(Trees.Node(tree, @"Documents\Cloud\c.bin").Flags.HasFlag(NodeMarks.CloudNotDownloaded), "precondition: the walk saw c.bin local");

        cloud.Dehydrate("c.bin");
        cloud.RequireOnlineOnly("c.bin");
        ProveTheOracleCanFail(cloud, Control2);

        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);
        Assert.AreEqual(1, groups.Count, "the two local copies form one group");
        Check.Sequence(groups[0].Copies.Select(c => c.Node.Name).Order(StringComparer.Ordinal), "a.bin", "b.bin");
        cloud.AssertNoFetch("the duplicates search");
        Assert.AreEqual(0L, cloud.Status("c.bin").OnDisk, "the search fetched c.bin");
    }

    // docs/spec/01 rule 6 (brief M29, console part): a populated placeholder folder given as the root is a folder and is
    // entered; its file is a cloud file not downloaded, and nothing is fetched.
    [TestMethod]
    [TestCategory("Console")]
    public async Task APopulatedPlaceholderFolderAsTheRootIsEntered()
    {
        CloudRoot.RequireInstalled();
        using var temp = new TempFolder("cloud-walk-root");
        using var cloud = CloudRoot.Register(temp.Sub("cloud"), temp.Sub("fetch.log"));
        cloud.Populate((@"docs\", 0), (@"docs\c.bin", 4 * ByteUnits.KB), (Control, 4 * ByteUnits.KB));
        cloud.Serve();
        cloud.RequireOnlineOnly(@"docs\c.bin");
        ProveTheOracleCanFail(cloud);

        var tree = await new Scanner().ScanAsync(cloud.Combine("docs"), null, CancellationToken.None);
        Assert.IsTrue(tree.Root.IsFolder, "the placeholder folder is a folder");
        Assert.IsFalse(tree.Root.Flags.HasFlag(NodeMarks.Link), "a placeholder folder is not a link");
        Assert.IsFalse(tree.Root.Flags.HasFlag(NodeMarks.Unreadable), "the placeholder folder is readable");
        Assert.IsTrue(Trees.Node(tree, "c.bin").Flags.HasFlag(NodeMarks.CloudNotDownloaded), "entered: its file is listed as a cloud file");
        Assert.AreEqual(1L, tree.Coverage.CloudNotDownloadedCount);
        cloud.AssertNoFetch("the walk");
    }
}
