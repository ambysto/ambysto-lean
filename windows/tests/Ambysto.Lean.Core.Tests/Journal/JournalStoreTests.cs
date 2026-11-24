using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Journal;

/// <summary>docs/spec/05 "Entry" and its Windows notes; intent Outcome 5; plan Design "Journal": one JSON object per line
/// with the spec's field names in camel case, <c>mode</c> always <c>trash</c>, flushed after each entry, a torn last line
/// ignored, an unreadable middle line skipped, <c>%LOCALAPPDATA%\Ambysto\Space\journal.jsonl</c> by default, and
/// <c>CanWrite</c> (docs/spec/02 step 5) false when a write would not be accepted.</summary>
[TestClass]
public sealed class JournalStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 9, 30, 15, TimeSpan.Zero);

    internal static JournalEntry Full(string id = "e1", JournalResult result = JournalResult.Trashed, LeanModule module = LeanModule.LargeOld,
        string? reason = null, string session = "s1") =>
        new(id, session, T0, module, @"C:\Users\me\Videos\old.mkv", @"C:\Users\me\Videos", @"C:\Users\me\Videos\old.mkv", 123_456_789,
            @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAB12CD.mkv",
            new TrashedIdentity(0x0123_4567_89AB_CDEFUL, new UInt128(0xFEDC_BA98_7654_3210UL, 0x0011_2233_4455_6677UL), 123_456_000),
            result, reason, "1.2.3");

    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
    }

    // docs/spec/05 "Entry": every field is stored and read back unchanged.
    [TestMethod]
    public void EveryFieldIsReadBackUnchanged()
    {
        using var temp = new TempFolder("journal-fields");
        var store = JournalStore.Open(temp.Sub("journal.jsonl"));
        var entry = Full();
        var skipped = Full("e2", JournalResult.Skipped, LeanModule.Duplicates, "Changed since the scan") with
        {
            OriginalParentRealPath = null, ApprovedRealPath = null, Bytes = null, TrashPath = null, TrashedIdentity = null,
        };
        store.Append(entry);
        store.Append(skipped);

        var read = store.Read();
        Assert.AreEqual(2, read.Count, "two entries appended");
        Assert.AreEqual(entry, read[0], "the first entry read back");
        Assert.AreEqual(skipped, read[1], "the second entry, with its empty fields, read back");
        Assert.AreEqual(TimeSpan.Zero, read[0].Time.Offset, "times are UTC");
        Assert.AreEqual(temp.Sub("journal.jsonl"), store.Path, "Path is the store's path");
    }

    // docs/spec/05 "Entry": one JSON object per line, the spec's names in camel case; mode "trash"; module and result as
    // camel-case enum names; ISO 8601 UTC times; the file id as 32 hex digits (JournalEntry's contract).
    [TestMethod]
    public void EachEntryIsOneJsonLineWithTheSpecsNames()
    {
        using var temp = new TempFolder("journal-json");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(Full());
        store.Append(Full("e2", JournalResult.RestoreFailed, LeanModule.Uninstall, "The original folder no longer exists or moved"));
        store.Append(Full("e3", JournalResult.Lost, LeanModule.Map, "Windows deleted this permanently instead of moving it to the Recycle Bin"));

        var lines = Lines(path);
        Assert.AreEqual(3, lines.Length, "one line per entry:\n" + string.Join("\n", lines));
        using var first = JsonDocument.Parse(lines[0]);
        var o = first.RootElement;
        Assert.AreEqual(JsonValueKind.Object, o.ValueKind);
        foreach (var name in new[] { "id", "sessionId", "time", "module", "originalPath", "originalParentRealPath", "approvedRealPath", "bytes", "mode", "trashPath", "trashedIdentity", "result", "appVersion" })
        {
            Check.True(o.TryGetProperty(name, out _), $"the line has no \"{name}\": {lines[0]}");
        }

        Assert.AreEqual("e1", o.GetProperty("id").GetString());
        Assert.AreEqual("s1", o.GetProperty("sessionId").GetString());
        Assert.AreEqual("trash", o.GetProperty("mode").GetString(), "mode is always trash");
        Assert.AreEqual("largeOld", o.GetProperty("module").GetString(), "module in camel case");
        Assert.AreEqual("trashed", o.GetProperty("result").GetString(), "result in camel case");
        Assert.AreEqual(@"C:\Users\me\Videos\old.mkv", o.GetProperty("originalPath").GetString());
        Assert.AreEqual(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAB12CD.mkv", o.GetProperty("trashPath").GetString());
        Assert.AreEqual(123_456_789L, o.GetProperty("bytes").GetInt64());
        Assert.AreEqual("1.2.3", o.GetProperty("appVersion").GetString());

        var time = o.GetProperty("time").GetString()!;
        Check.True(time.EndsWith('Z') || time.EndsWith("+00:00", StringComparison.Ordinal), "time is ISO 8601 UTC: " + time);
        Assert.AreEqual(T0, DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "time value");

        var identity = o.GetProperty("trashedIdentity");
        Assert.AreEqual(JsonValueKind.Object, identity.ValueKind, "trashedIdentity is an object");
        var fileId = identity.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()!).FirstOrDefault(s => s.Length == 32);
        Assert.IsNotNull(fileId, "trashedIdentity holds the file id as 32 hex digits: " + identity.GetRawText());
        Assert.AreEqual(new UInt128(0xFEDC_BA98_7654_3210UL, 0x0011_2233_4455_6677UL).ToString("X32", CultureInfo.InvariantCulture), fileId.ToUpperInvariant(), "file id value");

        using var second = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("restoreFailed", second.RootElement.GetProperty("result").GetString());
        Assert.AreEqual("uninstall", second.RootElement.GetProperty("module").GetString());
        Assert.AreEqual("The original folder no longer exists or moved", second.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual("trash", second.RootElement.GetProperty("mode").GetString());
        using var third = JsonDocument.Parse(lines[2]);
        Assert.AreEqual("lost", third.RootElement.GetProperty("result").GetString(), "the Windows result lost");
        Assert.AreEqual("map", third.RootElement.GetProperty("module").GetString());
    }

    // Every module and result name in camel case (docs/spec/05 "Entry": map, largeOld, duplicates, uninstall; trashed,
    // skipped, restored, restoreFailed, lost).
    [TestMethod]
    public void ModuleAndResultNamesAreTheSpecs()
    {
        using var temp = new TempFolder("journal-names");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        var modules = new[] { (LeanModule.Map, "map"), (LeanModule.LargeOld, "largeOld"), (LeanModule.Duplicates, "duplicates"), (LeanModule.Uninstall, "uninstall") };
        var results = new[] { (JournalResult.Trashed, "trashed"), (JournalResult.Skipped, "skipped"), (JournalResult.Restored, "restored"), (JournalResult.RestoreFailed, "restoreFailed"), (JournalResult.Lost, "lost") };
        var i = 0;
        foreach (var (module, _) in modules)
        {
            foreach (var (result, _) in results)
            {
                store.Append(Full("e" + i++, result, module, result is JournalResult.Skipped or JournalResult.RestoreFailed or JournalResult.Lost ? "why" : null));
            }
        }

        var lines = Lines(path);
        Assert.AreEqual(modules.Length * results.Length, lines.Length);
        i = 0;
        foreach (var (_, moduleName) in modules)
        {
            foreach (var (_, resultName) in results)
            {
                using var doc = JsonDocument.Parse(lines[i++]);
                Assert.AreEqual(moduleName, doc.RootElement.GetProperty("module").GetString());
                Assert.AreEqual(resultName, doc.RootElement.GetProperty("result").GetString());
            }
        }

        Assert.AreEqual(modules.Length * results.Length, store.Read().Count, "all read back");
    }

    // docs/spec/05: "flushed to disk after each entry": another reader sees the entry the moment Append returns.
    [TestMethod]
    public void EachEntryIsVisibleToAnotherStoreAtOnce()
    {
        using var temp = new TempFolder("journal-flush");
        var path = temp.Sub("journal.jsonl");
        var writer = JournalStore.Open(path);
        var reader = JournalStore.Open(path);
        writer.Append(Full("e1"));
        Assert.AreEqual(1, reader.Read().Count, "a second store reads the first entry at once");
        Assert.AreEqual(1, Lines(path).Length, "the line is on disk at once");
        writer.Append(Full("e2"));
        var read = reader.Read();
        Assert.AreEqual(2, read.Count, "and the second");
        Assert.AreEqual("e2", read[1].Id);
    }

    // Plan Design "Journal": created on the first write, with its folder.
    [TestMethod]
    public void TheFirstWriteCreatesTheFileAndItsFolder()
    {
        using var temp = new TempFolder("journal-create");
        var path = temp.Sub("Ambysto", "Space", "journal.jsonl");
        var store = JournalStore.Open(path);
        Assert.AreEqual(0, store.Read().Count, "a journal that does not exist yet reads as empty");
        Check.True(store.CanWrite(), "a journal whose folder can be created accepts a write");
        store.Append(Full());
        Check.True(File.Exists(path), "the first write created the file and its folders");
        Assert.AreEqual(1, store.Read().Count);
    }

    // docs/spec/05: "A torn last line is ignored when reading".
    [TestMethod]
    public void ATornLastLineIsIgnored()
    {
        using var temp = new TempFolder("journal-torn");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(Full("e1"));
        store.Append(Full("e2"));
        var whole = Lines(path)[1];
        File.AppendAllText(path, whole[..(whole.Length / 2)]);

        var read = store.Read();
        Check.Sequence(read.Select(e => e.Id), "e1", "e2");
    }

    // A torn line followed by a later append is then a middle line: it is skipped and the new entry is still read, so a
    // crash during one write never loses the next entry.
    [TestMethod]
    public void AnAppendAfterATornLineIsStillRead()
    {
        using var temp = new TempFolder("journal-torn-append");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(Full("e1"));
        var whole = Lines(path)[0];
        File.AppendAllText(path, whole[..(whole.Length / 3)]);

        JournalStore.Open(path).Append(Full("e2"));
        Check.Sequence(JournalStore.Open(path).Read().Select(e => e.Id), "e1", "e2");
    }

    // JournalStore.Read: an unreadable line in the middle is skipped and never stops the read.
    [TestMethod]
    public void ABadMiddleLineIsSkipped()
    {
        using var temp = new TempFolder("journal-middle");
        var scratch = temp.Sub("scratch.jsonl");
        var scratchStore = JournalStore.Open(scratch);
        scratchStore.Append(Full("e1"));
        scratchStore.Append(Full("e3"));
        scratchStore.Append(Full("e5"));
        var good = Lines(scratch);
        Assert.AreEqual(3, good.Length);

        var path = temp.Sub("journal.jsonl");
        File.WriteAllText(path, good[0] + "\n" + "this is not json" + "\n" + good[1] + "\n" + "{\"id\":\"half\"," + "\n" + "[1,2,3]" + "\n" + good[2] + "\n");
        Check.Sequence(JournalStore.Open(path).Read().Select(e => e.Id), "e1", "e3", "e5");
    }

    // docs/spec/02 step 5 / JournalStore.CanWrite: false when the store's file cannot be written (read-only).
    [TestMethod]
    public void CanWriteIsFalseForAReadOnlyJournal()
    {
        using var temp = new TempFolder("journal-readonly");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(Full("e1"));
        Check.True(store.CanWrite(), "a writable journal accepts a write");
        using (FileSwitches.ReadOnly(path))
        {
            Check.False(store.CanWrite(), "a read-only journal file does not accept a write");
            AssertAppendFails(store);
            Assert.AreEqual(1, store.Read().Count, "a read-only journal is still read");
        }

        Check.True(store.CanWrite(), "writable again once the flag is cleared");
    }

    // docs/spec/02 step 5: false when the folder denies the user writes (no file yet, and with a file).
    [TestMethod]
    public void CanWriteIsFalseInAFolderDeniedToTheUser()
    {
        using var temp = new TempFolder("journal-denied");
        var folder = temp.Folder("Space");
        var fresh = JournalStore.Open(Path.Combine(folder, "journal.jsonl"));
        using (FileSwitches.DenyWrites(folder))
        {
            Check.False(fresh.CanWrite(), "a journal in a folder that denies creating files does not accept a write");
            AssertAppendFails(fresh);
        }

        fresh.Append(Full("e1"));
        using (FileSwitches.DenyWrites(folder))
        {
            Check.False(fresh.CanWrite(), "an existing journal the user may not append to does not accept a write");
            AssertAppendFails(fresh);
        }

        Assert.AreEqual(1, fresh.Read().Count, "the failed appends left the journal as it was");
    }

    // JournalStore.CanWrite asks; it never writes an entry.
    [TestMethod]
    public void CanWriteAddsNoEntry()
    {
        using var temp = new TempFolder("journal-canwrite");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        store.Append(Full("e1"));
        for (var i = 0; i < 3; i++)
        {
            Check.True(store.CanWrite(), "writable");
        }

        Assert.AreEqual(1, Lines(path).Length, "CanWrite added no line");
        Assert.AreEqual(1, store.Read().Count);
    }

    // docs/spec/05 Windows note: %LOCALAPPDATA%\Ambysto\Space\journal.jsonl (the folder keeps the first name).
    [TestMethod]
    public void TheDefaultPathIsInLocalAppDataUnderAmbystoSpace()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = JournalStore.DefaultPath;
        Check.True(path.EndsWith(@"\Ambysto\Space\journal.jsonl", StringComparison.OrdinalIgnoreCase), "ends with Ambysto\\Space\\journal.jsonl: " + path);
        var expected = Path.Combine(local, "Ambysto", "Space", "journal.jsonl");
        Check.True(string.Equals(expected, path, StringComparison.OrdinalIgnoreCase), $"under %LOCALAPPDATA%: expected {expected}, got {path}");
    }

    // The Mode constant is the spec's only v1 value.
    [TestMethod]
    public void EveryLineSaysModeTrash()
    {
        using var temp = new TempFolder("journal-mode");
        var path = temp.Sub("journal.jsonl");
        var store = JournalStore.Open(path);
        foreach (var result in Enum.GetValues<JournalResult>())
        {
            store.Append(Full(result.ToString(), result));
        }

        foreach (var line in Lines(path))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.AreEqual(JournalEntry.Mode, doc.RootElement.GetProperty("mode").GetString(), line);
        }

        Assert.AreEqual("trash", Check.Constant<string>(typeof(JournalEntry), nameof(JournalEntry.Mode)));
    }

    internal static void AssertAppendFails(JournalStore store)
    {
        try
        {
            store.Append(Full("must-fail"));
        }
        catch (NotImplementedException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }

        Assert.Fail("Append returned although the journal cannot be written");
    }

    internal static IReadOnlyList<JournalEntry> ReadAll(string path) => JournalStore.Open(path).Read();
}
