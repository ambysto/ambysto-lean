using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Settings;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Settings;

/// <summary>Lean's settings file (plan "v1 additions", Design, as changed by the main session 2026-10-18 before any
/// implementation; docs/spec/02 Windows note): <c>%LOCALAPPDATA%\Ambysto\Space\settings.jsonl</c>, append-only. Each
/// <see cref="LeanSettings.Save"/> appends one line holding the whole settings, <c>{"version":1,"protectedFolders":[...]}</c>
/// in camel case, starting a new line when the last byte is not <c>\n</c>; nothing is ever replaced or deleted.
/// <see cref="LeanSettings.Load"/> reads the last line that parses, so a torn last line falls back to the line before;
/// <see cref="LoadResult.WasBroken"/> is true only when the file has content but no line parses, and then the defaults are
/// used and the file is left byte for byte as it was. Every file here is in the test's own scratch folder, never at
/// <see cref="LeanSettings.DefaultPath"/>.</summary>
[TestClass]
public sealed class LeanSettingsFileTests
{
    private sealed class Bench : IDisposable
    {
        public Bench(string name)
        {
            Temp = new TempFolder(name);
            Folder = Temp.Folder("Space");
            FilePath = Path.Combine(Folder, "settings.jsonl");
        }

        public TempFolder Temp { get; }

        /// <summary>The folder the settings file is in (Lean's own folder, here a scratch one).</summary>
        public string Folder { get; }

        public string FilePath { get; }

        /// <summary>A real folder to protect.</summary>
        public string Protect(string name) => Temp.Folder("protect", name);

        /// <summary>Settings holding these folders, added in this order.</summary>
        public static LeanSettings With(params string[] folders)
        {
            var settings = new LeanSettings();
            foreach (var folder in folders)
            {
                Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, folder), "setup: add " + folder);
            }

            return settings;
        }

        public byte[] Bytes() => File.ReadAllBytes(FilePath);

        /// <summary>The non-empty lines of the file.</summary>
        public string[] Lines() => Encoding.UTF8.GetString(Bytes()).Split('\n').Where(l => l.Trim().Length > 0).ToArray();

        public void Dispose() => Temp.Dispose();
    }

    private static void SameFolders(LeanSettings expected, LeanSettings actual, string what) =>
        CollectionAssert.AreEqual(expected.ProtectedFolders.ToArray(), actual.ProtectedFolders.ToArray(), what + ": [" + string.Join(" | ", actual.ProtectedFolders) + "]");

    private static void StartsWith(byte[] whole, byte[] prefix, string what)
    {
        Check.True(whole.Length >= prefix.Length && whole.AsSpan(0, prefix.Length).SequenceEqual(prefix), what + ": the earlier bytes were changed, not appended to");
    }

    // Plan Design: the settings file is %LOCALAPPDATA%\Ambysto\Space\settings.jsonl, beside the journal (Lean's own
    // protected folder). Only the path is read; nothing is written there.
    [TestMethod]
    public void TheDefaultPathIsSettingsJsonlBesideTheJournal()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "settings.jsonl");
        Check.True(string.Equals(expected, LeanSettings.DefaultPath, StringComparison.OrdinalIgnoreCase), "DefaultPath is " + LeanSettings.DefaultPath + ", expected " + expected);
        Check.True(string.Equals(Path.GetDirectoryName(JournalStore.DefaultPath), Path.GetDirectoryName(LeanSettings.DefaultPath), StringComparison.OrdinalIgnoreCase), "beside the journal");
    }

    // A missing file gives the defaults (no protected folders), not broken, and Load creates nothing.
    [TestMethod]
    public void AMissingFileGivesTheDefaults()
    {
        using var b = new Bench("settings-missing");
        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken, "a missing file is not a broken one");
        Assert.AreEqual(0, result.Settings.ProtectedFolders.Count);
        Check.False(File.Exists(b.FilePath), "Load created the file");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(b.Folder).Count(), "Load wrote nothing in the folder");
    }

    // "WasBroken is true only when the file has content but no line parses": an empty file has none.
    [TestMethod]
    public void AnEmptyFileGivesTheDefaultsAndIsNotBroken()
    {
        using var b = new Bench("settings-empty");
        File.WriteAllBytes(b.FilePath, []);
        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken);
        Assert.AreEqual(0, result.Settings.ProtectedFolders.Count);
        Assert.AreEqual(0, b.Bytes().Length, "the file is left as it was");
    }

    // Save then Load round-trips the protected folders, in the order they were added (not sorted).
    [TestMethod]
    public void SaveThenLoadRoundTripsTheFoldersInOrder()
    {
        using var b = new Bench("settings-roundtrip");
        var settings = Bench.With(b.Protect("Charlie"), b.Protect("alpha"), b.Protect("Bravo"));
        settings.Save(b.FilePath);

        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken);
        Assert.AreEqual(3, result.Settings.ProtectedFolders.Count);
        SameFolders(settings, result.Settings, "the loaded list");
    }

    // UTF-8: a folder name outside ASCII comes back exactly.
    [TestMethod]
    public void AFolderNameOutsideAsciiRoundTrips()
    {
        using var b = new Bench("settings-utf8");
        var settings = Bench.With(b.Protect("Fotos Ärger 日本 ñ"));
        settings.Save(b.FilePath);
        SameFolders(settings, LeanSettings.Load(b.FilePath).Settings, "the loaded list");
    }

    // Settings without folders save and load as the defaults.
    [TestMethod]
    public void EmptySettingsRoundTrip()
    {
        using var b = new Bench("settings-roundtrip-empty");
        new LeanSettings().Save(b.FilePath);
        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken);
        Assert.AreEqual(0, result.Settings.ProtectedFolders.Count);
    }

    // Several saves: the last one wins.
    [TestMethod]
    public void SeveralSavesTheLastOneWins()
    {
        using var b = new Bench("settings-lastwins");
        var a = b.Protect("A");
        var c = b.Protect("C");
        var settings = Bench.With(a);
        settings.Save(b.FilePath);
        SameFolders(settings, LeanSettings.Load(b.FilePath).Settings, "after the first save");

        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, c));
        settings.Save(b.FilePath);
        Assert.AreEqual(2, LeanSettings.Load(b.FilePath).Settings.ProtectedFolders.Count, "after the second save");

        Assert.IsTrue(ProtectedFolders.Remove(settings, a));
        settings.Save(b.FilePath);
        var last = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(last.WasBroken);
        Assert.AreEqual(1, last.Settings.ProtectedFolders.Count, "after the third save: [" + string.Join(" | ", last.Settings.ProtectedFolders) + "]");
        Check.True(string.Equals(last.Settings.ProtectedFolders[0], settings.ProtectedFolders[0], StringComparison.Ordinal), "the last save's folder");
    }

    // Append-only: each save adds exactly one line; the bytes before it are never changed; the file is never replaced
    // (its file id stays), and nothing else is left in the folder (no temporary file).
    [TestMethod]
    public void EachSaveAppendsExactlyOneLineAndNeverReplacesTheFile()
    {
        using var b = new Bench("settings-append");
        var settings = Bench.With(b.Protect("One"));
        settings.Save(b.FilePath);
        Assert.AreEqual(1, b.Lines().Length, "after the first save");
        var identity = Native.IdentityOf(b.FilePath);
        var before = b.Bytes();

        for (var save = 2; save <= 4; save++)
        {
            if (save == 3)
            {
                Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, b.Protect("Two")));
            }

            settings.Save(b.FilePath); // the fourth save changes nothing and still adds its line
            var after = b.Bytes();
            Assert.AreEqual(save, b.Lines().Length, "lines after save " + save);
            StartsWith(after, before, "save " + save);
            Assert.AreEqual(identity, Native.IdentityOf(b.FilePath), "save " + save + " replaced the file instead of appending to it");
            before = after;
        }

        var entries = Directory.EnumerateFileSystemEntries(b.Folder).Select(e => Path.GetFileName(e)).ToList();
        Assert.AreEqual(1, entries.Count, "only the settings file is in the folder: " + string.Join(", ", entries));
        Assert.AreEqual("settings.jsonl", entries[0]);
    }

    // The JSON shape: one object per line, camel case, "version": 1 and "protectedFolders" as an array of strings.
    [TestMethod]
    public void EachLineIsCamelCaseJsonWithVersionOne()
    {
        using var b = new Bench("settings-shape");
        var settings = Bench.With(b.Protect("X"), b.Protect("Y"));
        settings.Save(b.FilePath);

        var line = b.Lines().Single();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind, line);
        Assert.IsTrue(root.TryGetProperty("version", out var version), "no \"version\": " + line);
        Assert.AreEqual(JsonValueKind.Number, version.ValueKind);
        Assert.AreEqual(1, version.GetInt32());
        Assert.IsTrue(root.TryGetProperty("protectedFolders", out var folders), "no \"protectedFolders\": " + line);
        Assert.AreEqual(JsonValueKind.Array, folders.ValueKind);
        CollectionAssert.AreEqual(settings.ProtectedFolders.ToArray(), folders.EnumerateArray().Select(f => f.GetString()).ToArray());
        foreach (var property in root.EnumerateObject())
        {
            Check.True(property.Name.Length > 0 && char.IsLower(property.Name[0]), "not camel case: \"" + property.Name + "\" in " + line);
        }
    }

    // A torn last line (a crash mid-append) is ignored: the line before it is used, not broken, and Load leaves the
    // file as it is. The next save starts a new line (the last byte is not \n), so it loads.
    [TestMethod]
    public void ATornLastLineFallsBackToTheLineBefore()
    {
        using var b = new Bench("settings-torn");
        var a = b.Protect("A");
        var settings = Bench.With(a);
        settings.Save(b.FilePath);
        var first = Bench.With(a);
        Assert.AreEqual(AddResult.Added, ProtectedFolders.Add(settings, b.Protect("B")));
        settings.Save(b.FilePath);

        // Cut the second line in the middle.
        var whole = b.Bytes();
        var firstLength = Encoding.UTF8.GetBytes(b.Lines()[0]).Length;
        var cut = firstLength + ((whole.Length - firstLength) / 2);
        var torn = whole[..cut];
        File.WriteAllBytes(b.FilePath, torn);
        Check.True(torn[^1] != (byte)'\n', "setup: the torn line ends without a newline");

        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken, "a file with one good line is not broken");
        SameFolders(first, result.Settings, "the line before the torn one");
        CollectionAssert.AreEqual(torn, b.Bytes(), "Load changed the file");

        // The next save appends after the torn bytes, on a line of its own.
        var c = b.Protect("C");
        var next = Bench.With(c);
        next.Save(b.FilePath);
        var after = b.Bytes();
        StartsWith(after, torn, "the save after a torn line");
        Assert.AreEqual((byte)'\n', after[torn.Length], "the save started a new line after the torn one");
        var loaded = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(loaded.WasBroken);
        SameFolders(next, loaded.Settings, "the save after the torn line");
    }

    // A broken line after good ones is skipped the same way (the last line that parses wins).
    [TestMethod]
    public void ABrokenLineAfterAGoodOneIsSkipped()
    {
        using var b = new Bench("settings-brokenlast");
        var settings = Bench.With(b.Protect("A"));
        settings.Save(b.FilePath);
        File.AppendAllText(b.FilePath, "{\"version\":1,\"protectedFolders\":[\"C:\\\\x\"\n", new UTF8Encoding(false));
        var before = b.Bytes();

        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken);
        SameFolders(settings, result.Settings, "the good line");
        CollectionAssert.AreEqual(before, b.Bytes(), "Load changed the file");
    }

    // A file with content but no line that parses: WasBroken, the defaults, and the file byte for byte as it was.
    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("{\"version\":1,\"protectedFolders\":[")]
    [DataRow("garbage\n{\"version\":\n\n")]
    public void AFileWithOnlyGarbageIsBrokenAndLeftAsItWas(string content)
    {
        using var b = new Bench("settings-garbage");
        File.WriteAllText(b.FilePath, content, new UTF8Encoding(false));
        var before = b.Bytes();
        var stamp = File.GetLastWriteTimeUtc(b.FilePath);

        var result = LeanSettings.Load(b.FilePath);

        Assert.IsTrue(result.WasBroken, "content with no line that parses is broken");
        Assert.AreEqual(0, result.Settings.ProtectedFolders.Count, "the defaults");
        CollectionAssert.AreEqual(before, b.Bytes(), "the file was changed");
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(b.FilePath), "the file was written to");
        Assert.AreEqual(1, Directory.EnumerateFileSystemEntries(b.Folder).Count(), "nothing else was written beside it");
    }

    // Bytes that are not UTF-8 text at all: broken, never thrown, left as they were.
    [TestMethod]
    public void BinaryGarbageIsBrokenAndLeftAsItWas()
    {
        using var b = new Bench("settings-binary");
        byte[] garbage = [0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x0A, 0x80, 0x81, 0x7B];
        File.WriteAllBytes(b.FilePath, garbage);

        var result = LeanSettings.Load(b.FilePath);

        Assert.IsTrue(result.WasBroken);
        Assert.AreEqual(0, result.Settings.ProtectedFolders.Count);
        CollectionAssert.AreEqual(garbage, b.Bytes(), "the file was changed");
    }

    // Nothing is ever replaced or deleted: a save after a broken file appends to it; the garbage stays in front.
    [TestMethod]
    public void ASaveAfterABrokenFileAppendsAndKeepsTheOldBytes()
    {
        using var b = new Bench("settings-garbage-save");
        File.WriteAllText(b.FilePath, "garbage", new UTF8Encoding(false));
        var before = b.Bytes();
        Assert.IsTrue(LeanSettings.Load(b.FilePath).WasBroken, "setup");

        var settings = Bench.With(b.Protect("A"));
        settings.Save(b.FilePath);

        StartsWith(b.Bytes(), before, "the save after a broken file");
        var result = LeanSettings.Load(b.FilePath);
        Assert.IsFalse(result.WasBroken, "a line now parses");
        SameFolders(settings, result.Settings, "the new line");
    }
}
