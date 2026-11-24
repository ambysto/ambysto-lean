using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Journal;

/// <summary>Journal behaviours of the phase B gaps (briefs J1–J4): docs/spec/05 "Entry" (<c>refersTo</c>: "for
/// <c>restored</c> and <c>restoreFailed</c>: the <c>id</c> of the <c>trashed</c> entry it refers to"; <c>id</c> unique,
/// <c>sessionId</c>, <c>originalPath</c>, <c>mode</c> = <c>trash</c>, the only v1 value), "A torn last line is ignored when
/// reading", "Readable without the app"; plan notes "Journal <c>refersTo</c>" (owner approved 2026-10-17) and Design
/// "Journal" (append-only, flushed per entry; the history reads it while a session may append); the
/// <see cref="JournalStore.Read"/> contract (an unreadable line is skipped, never fatal) and the
/// <see cref="JournalStore.Append"/> contract (a torn last line from an earlier failed write must not swallow this entry).</summary>
[TestClass]
public sealed class JournalPhaseBGapTests
{
    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
    }

    /// <summary>The journal line <see cref="JournalStore"/> writes for <paramref name="entry"/> (through a scratch store).</summary>
    private static string LineOf(JournalEntry entry, TempFolder temp)
    {
        var scratch = temp.Sub("line-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        JournalStore.Open(scratch).Append(entry);
        return Lines(scratch).Single();
    }

    private static string? RefersToIn(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("refersTo", out var value) ? value.GetString() : null;
    }

    private static string? ResultIn(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("result").GetString();
    }

    // J1 (a) [normal]: store level: an entry with refersTo is read back equal, refersTo included, and its line has
    // "refersTo":"t1".
    [TestMethod]
    public void ARefersToIsWrittenAndReadBack()
    {
        using var temp = new TempFolder("journal-refersto");
        var path = temp.Sub("journal.jsonl");
        var entry = JournalStoreTests.Full("r1", JournalResult.Restored) with { RefersTo = "t1" };
        JournalStore.Open(path).Append(entry);

        var read = JournalStore.Open(path).Read().Single();

        Assert.AreEqual(entry, read, "read back equal");
        Assert.AreEqual("t1", read.RefersTo, "refersTo read back");
        Assert.AreEqual("t1", RefersToIn(Lines(path).Single()), "the line has \"refersTo\":\"t1\": " + Lines(path).Single());
    }

    // J1 (b) [normal]: Put back level: one entry restored and one refused (destination occupied); the restored and the
    // restoreFailed lines each carry refersTo equal to their trashed entry's id, in the raw line and through a freshly
    // opened store.
    [TestMethod]
    public void RestoreLinesReferToTheirTrashedEntry()
    {
        using var b = new PutBackBench("putback-refersto");
        var restored = b.Stage("a.bin");
        var refused = b.Stage("b.bin");
        File.WriteAllText(refused.OriginalPath, "occupied");
        var putBack = b.PutBack();

        var first = putBack.Restore(restored);
        var second = putBack.Restore(refused);

        Check.True(first.Restored, "a.bin restored; reason: " + first.Reason);
        Check.False(second.Restored, "b.bin refused");
        var lines = Lines(b.JournalPath);
        var restoredLine = lines.Single(l => ResultIn(l) == "restored");
        var failedLine = lines.Single(l => ResultIn(l) == "restoreFailed");
        Assert.AreEqual(restored.Id, RefersToIn(restoredLine), "the restored line refers to its trashed entry: " + restoredLine);
        Assert.AreEqual(refused.Id, RefersToIn(failedLine), "the restoreFailed line refers to its trashed entry: " + failedLine);
        var entries = JournalStore.Open(b.JournalPath).Read();
        Assert.AreEqual(restored.Id, entries.Single(e => e.Result == JournalResult.Restored).RefersTo, "read back by a fresh store");
        Assert.AreEqual(refused.Id, entries.Single(e => e.Result == JournalResult.RestoreFailed).RefersTo, "read back by a fresh store");
    }

    // J2 [low]: a journal written by hand: a good line, then copies of it with "id":"", "sessionId":"", "originalPath":""
    // and "mode":"delete" (one change per line), then a second good line. Read returns exactly the two good entries; Put
    // back of the session restores only those.
    [TestMethod]
    public void LinesWithoutIdSessionOrPathOrWithAnotherModeAreSkipped()
    {
        using var b = new PutBackBench("journal-bad-lines");
        var e1 = b.Stage("a.bin", journal: false);
        var e2 = b.Stage("b.bin", journal: false);
        var good1 = LineOf(e1, b.Temp);
        var good2 = LineOf(e2, b.Temp);
        string Variant(string field, string value)
        {
            var node = JsonNode.Parse(good1)!.AsObject();
            Assert.IsTrue(node.ContainsKey(field), $"precondition: the line has \"{field}\": {good1}");
            node[field] = value;
            return node.ToJsonString();
        }

        var lines = new[] { good1, Variant("id", ""), Variant("sessionId", ""), Variant("originalPath", ""), Variant("mode", "delete"), good2 };
        Directory.CreateDirectory(Path.GetDirectoryName(b.JournalPath)!);
        File.WriteAllText(b.JournalPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

        var read = JournalStore.Open(b.JournalPath).Read();

        Assert.AreEqual(2, read.Count, "only the two good lines: " + string.Join(" | ", read.Select(e => e.Id + "/" + e.SessionId + "/" + e.OriginalPath)));
        Assert.AreEqual(e1, read[0]);
        Assert.AreEqual(e2, read[1]);

        var outcomes = b.PutBack().RestoreSession("s1");

        Assert.AreEqual(2, outcomes.Count, "Put back saw only the two good entries: " + string.Join(", ", outcomes.Select(o => o.Entry.Id + "=" + o.Restored + " " + o.Reason)));
        Check.True(outcomes.All(o => o.Restored), string.Join(", ", outcomes.Select(o => o.Entry.Id + ": " + o.Reason)));
        Check.Set(outcomes.Select(o => o.Entry.Id), e1.Id, e2.Id);
        Check.True(File.Exists(e1.OriginalPath) && File.Exists(e2.OriginalPath), "both restored");
    }

    // J3 [low]: the journal is read while a writer holds it open as an appender does (append, write, share read).
    [TestMethod]
    public void TheJournalIsReadWhileAWriterHoldsItOpen()
    {
        using var temp = new TempFolder("journal-held");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(JournalStoreTests.Full("e1"));
        store.Append(JournalStoreTests.Full("e2"));

        IReadOnlyList<JournalEntry> read;
        using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            try
            {
                read = JournalStore.Open(path).Read();
            }
            catch (IOException e)
            {
                Assert.Fail("the journal could not be read while a writer holds it open: " + e.Message);
                return;
            }
        }

        Check.Sequence(read.Select(e => e.Id), "e1", "e2");
    }

    // J4 [low]: what a crash during an extending write can leave on NTFS: half of a JSON line followed by zero bytes, or a
    // single zero byte at the end. An append after it is still read.
    [TestMethod]
    [DataRow("half-line-and-16-zeros")]
    [DataRow("half-line-and-1-zero")]
    [DataRow("1-zero")]
    public void AnAppendAfterATailOfZeroBytesIsStillRead(string tail)
    {
        using var temp = new TempFolder("journal-zero-tail");
        var path = temp.Sub("journal.jsonl");
        JournalStore.Open(path).Append(JournalStoreTests.Full("e1"));
        var whole = Lines(path)[0];
        var half = Encoding.UTF8.GetBytes(whole[..(whole.Length / 2)]);
        byte[] bytes = tail switch
        {
            "half-line-and-16-zeros" => [.. half, .. new byte[16]],
            "half-line-and-1-zero" => [.. half, 0],
            _ => [0],
        };
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
        }

        JournalStore.Open(path).Append(JournalStoreTests.Full("e2"));

        Check.Sequence(JournalStore.Open(path).Read().Select(e => e.Id), "e1", "e2");
    }
}
