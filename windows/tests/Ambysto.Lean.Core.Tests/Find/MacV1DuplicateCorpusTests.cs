using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Find;

/// <summary>ASP-7 criterion 5: reproduce the original macOS-v1 duplicate proof corpus, not just its general properties.
/// Provenance: scripts/make-fixture.swift sections 3 and 4; intent/2026-10-10-ambysto-space-macos-v1.plan.md
/// group 9 and proof 7 (exactly 5 groups of 3; no near-duplicate or hard-link group). Platform-neutral duplicate
/// rules: docs/spec/03. No macOS implementation is used. Other generator sections concern scan performance only.</summary>
[TestClass]
public sealed class MacV1DuplicateCorpusTests
{
    [TestMethod]
    public async Task OriginalMacV1CorpusHasExactlyFiveThreeCopyGroupsAndZeroFalsePositives()
    {
        using var fx = new ProfileFixture("mac-v1-corpus");
        const int length = 1_500_000;
        var expected = new List<string>();
        for (var group = 0; group < 5; group++)
        {
            var paths = new List<string>();
            var bytes = new byte[length];
            Array.Fill(bytes, (byte)(100 + group));
            for (var copy = 0; copy < 3; copy++)
            {
                var path = fx.InProfile("Documents", "dupes", "g" + group, "copy" + copy + ".bin");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                paths.Add(path);
            }
            expected.Add(Key(paths));
        }

        var near = new byte[length];
        Array.Fill(near, (byte)7);
        for (var copy = 0; copy < 3; copy++)
        {
            near[750_000] = (byte)copy; // same length, first 4 KiB and last 4 KiB; only this middle byte differs
            var path = fx.InProfile("Documents", "dupes", "near", "near" + copy + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, near);
        }

        var original = fx.InProfile("Documents", "links", "original.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var linkedBytes = new byte[2_000_000];
        Array.Fill(linkedBytes, (byte)0x41);
        File.WriteAllBytes(original, linkedBytes);
        for (var link = 0; link < 3; link++)
            Native.CreateHardLink(fx.InProfile("Documents", "links", "link" + link + ".bin"), original);

        var tree = await new Scanner().ScanAsync(fx.Profile, null, CancellationToken.None);
        var groups = await DuplicateFinder.FindAsync(tree, DuplicateOptions.Default, fx.Context, CancellationToken.None);

        Assert.AreEqual(5, groups.Count, "original macOS corpus: five planted groups only");
        Assert.IsTrue(groups.All(g => g.Copies.Count == 3 && g.LogicalBytes == length));
        CollectionAssert.AreEquivalent(expected.ToArray(), groups.Select(g => Key(g.Copies.Select(c => c.Node.Path))).ToArray(),
            "every member must be a planted copy: no near-duplicate or hard-link false positive, no omitted planted copy");
    }

    private static string Key(IEnumerable<string> paths) => string.Join("|", paths.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal));
}
