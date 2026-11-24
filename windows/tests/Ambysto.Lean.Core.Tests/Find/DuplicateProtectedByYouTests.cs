using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>docs/spec/03 Duplicates, Windows note (2026-10-18), with docs/spec/02's Windows note on the user's protected
/// folders: a copy inside one of <see cref="PathContext.UserProtectedFolders"/> stays in its group, display-only, with
/// <see cref="DuplicateCopy.Hint"/> = <see cref="Text.HintProtectedByYou"/>, and is not counted in
/// <see cref="DuplicateGroup.ReclaimableEstimateBytes"/>, whether it is the suggested keep or not. Every other copy has no
/// hint; without protected folders no copy has one. Real files in a pretend profile (<see cref="ProfileFixture"/>).</summary>
[TestClass]
public sealed class DuplicateProtectedByYouTests
{
    private const long TwoMb = 2 * ByteUnits.MB;

    private static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(ProfileFixture fx, PathContext context)
    {
        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        return await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, context, CancellationToken.None);
    }

    private static DuplicateCopy CopyAt(DuplicateGroup group, string path) =>
        group.Copies.SingleOrDefault(c => string.Equals(Path.GetFullPath(c.Node.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertFailedException($"no copy at {path}; copies: {string.Join(", ", group.Copies.Select(c => c.Node.Path))}");

    /// <summary>Three byte-identical 2 MB copies: <c>Documents\Keep\guarded.bin</c> (inside the folder that the tests
    /// protect), <c>Documents\free.bin</c> and <c>Pictures\other.bin</c>. When <paramref name="protectedIsNewest"/>,
    /// guarded.bin is the newest (so the suggested keep); otherwise other.bin is the newest and guarded.bin the oldest.</summary>
    private static (string Keep, string Guarded, string Free, string Other) Tree(ProfileFixture fx, bool protectedIsNewest)
    {
        var keep = ProfileFixture.MakeFolder(fx.InProfile("Documents", "Keep"));
        var guarded = ProfileFixture.MakeFile(Path.Combine(keep, "guarded.bin"), TwoMb, seed: 61);
        var free = ProfileFixture.MakeFile(fx.InProfile("Documents", "free.bin"), TwoMb, seed: 61);
        var other = ProfileFixture.MakeFile(fx.InProfile("Pictures", "other.bin"), TwoMb, seed: 61);
        File.SetLastWriteTimeUtc(free, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(guarded, protectedIsNewest ? new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(other, protectedIsNewest ? new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        return (keep, guarded, free, other);
    }

    // Spec 03 Windows note: the copy inside a protected folder is in the group with Text.HintProtectedByYou, the two other
    // copies have no hint, and the estimate is what the copies other than the suggested keep and the protected copy occupy.
    // Rows: the protected copy is the suggested keep (estimate free + other), and it is not (keep other.bin; estimate free).
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ACopyInAProtectedFolderIsDisplayOnlyAndLeftOutOfTheEstimate(bool protectedIsNewest)
    {
        using var fx = new ProfileFixture("dup-protected-" + (protectedIsNewest ? "keep" : "other"));
        var (keep, guarded, free, other) = Tree(fx, protectedIsNewest);
        var context = fx.Context with { UserProtectedFolders = [keep] };

        var groups = await FindAsync(fx, context);
        Assert.AreEqual(1, groups.Count, "the protected copy stays in its group: one group of three");
        var group = groups[0];
        Assert.AreEqual(3, group.Copies.Count, "copies: " + string.Join(", ", group.Copies.Select(c => c.Node.Path)));

        var guardedCopy = CopyAt(group, guarded);
        var freeCopy = CopyAt(group, free);
        var otherCopy = CopyAt(group, other);
        Assert.AreEqual(Text.HintProtectedByYou, guardedCopy.Hint, "the copy inside the protected folder");
        Assert.AreEqual("Protected by you", guardedCopy.Hint);
        Assert.IsNull(freeCopy.Hint, "a copy outside the protected folder has no hint: " + free);
        Assert.IsNull(otherCopy.Hint, "a copy outside the protected folder has no hint: " + other);

        // The suggested keep is still the newest copy (a badge only), protected or not.
        Assert.AreSame(protectedIsNewest ? guardedCopy : otherCopy, group.SuggestedKeep,
            "suggested keep: " + group.SuggestedKeep.Node.Path);

        var expected = protectedIsNewest
            ? freeCopy.Node.AllocatedBytes + otherCopy.Node.AllocatedBytes
            : freeCopy.Node.AllocatedBytes;
        Assert.AreEqual(expected, group.ReclaimableEstimateBytes,
            "the estimate leaves out the suggested keep and the protected copy (protected copy is the keep: " + protectedIsNewest + ")");
        Check.AtLeast(protectedIsNewest ? 2 * TwoMb : TwoMb, group.ReclaimableEstimateBytes, "the movable copies other than the keep");
        Assert.IsTrue(File.Exists(guarded) && File.Exists(free) && File.Exists(other), "the finder changed nothing");
    }

    // Control: the same tree without protected folders has no hint on any copy, and the estimate counts every copy other
    // than the suggested keep (guarded.bin is then an ordinary copy).
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WithoutProtectedFoldersNoCopyHasAHint(bool protectedIsNewest)
    {
        using var fx = new ProfileFixture("dup-unprotected-" + (protectedIsNewest ? "keep" : "other"));
        Tree(fx, protectedIsNewest);
        Assert.AreEqual(0, fx.Context.UserProtectedFolders.Count, "precondition: the fixture's context has no protected folders");

        var groups = await FindAsync(fx, fx.Context);
        Assert.AreEqual(1, groups.Count);
        var group = groups[0];
        Assert.AreEqual(3, group.Copies.Count);
        foreach (var copy in group.Copies)
        {
            Assert.IsNull(copy.Hint, "a hint without protected folders on " + copy.Node.Path + ": " + copy.Hint);
        }

        var expected = group.Copies.Where(c => !ReferenceEquals(c, group.SuggestedKeep)).Sum(c => c.Node.AllocatedBytes);
        Assert.AreEqual(expected, group.ReclaimableEstimateBytes, "every copy other than the suggested keep is counted");
    }

    // A protected folder beside the copies (a sibling whose name starts the same, Documents\Keep2, holding none of them)
    // gives no hint and leaves the estimate whole.
    [TestMethod]
    public async Task AProtectedFolderBesideTheCopiesChangesNothing()
    {
        using var fx = new ProfileFixture("dup-protected-beside");
        var (keep, guarded, _, _) = Tree(fx, protectedIsNewest: false);
        var beside = ProfileFixture.MakeFolder(keep + "2");
        ProfileFixture.MakeFile(Path.Combine(beside, "note.txt"));
        var context = fx.Context with { UserProtectedFolders = [beside] };

        var groups = await FindAsync(fx, context);
        Assert.AreEqual(1, groups.Count);
        var group = groups[0];
        Assert.IsTrue(group.Copies.All(c => c.Hint is null), "a hint from a folder beside the copies: " + string.Join(", ", group.Copies.Select(c => c.Node.Path + "=" + c.Hint)));
        var expected = group.Copies.Where(c => !ReferenceEquals(c, group.SuggestedKeep)).Sum(c => c.Node.AllocatedBytes);
        Assert.AreEqual(expected, group.ReclaimableEstimateBytes);
        Assert.IsNull(CopyAt(group, guarded).Hint);
    }
}
