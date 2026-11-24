using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "The clean runner and its log"): <see cref="CommandLog"/>, one JSON object per line with the
/// plan's camelCase names, a line that cannot be parsed skipped, <see cref="CommandLog.CanWrite"/> false for a path whose
/// folder cannot be created. The trash journal is a separate file; this one is only the command log. Session 0; written by the
/// test writer before the code, against a stub.</summary>
[TestClass]
public sealed class CommandLogTests
{
    private static readonly string[] FieldNames =
        ["id", "time", "entryId", "program", "arguments", "location", "bytesBefore", "bytesAfter", "exitCode", "timedOut", "result", "reason", "appVersion"];

    private static CommandLogEntry Sample(string id = "run-1") => new(
        id,
        new DateTimeOffset(2026, 10, 21, 9, 30, 15, 123, TimeSpan.Zero),
        "nuget-http",
        @"C:\Program Files\dotnet\dotnet.exe",
        ["nuget", "locals", "http-cache", "--clear"],
        @"C:\Users\me\AppData\Local\NuGet\v3-cache",
        123456789012L,
        4096L,
        0,
        false,
        CleanResult.Cleaned,
        null,
        "1.0.0");

    private static void AssertSame(CommandLogEntry expected, CommandLogEntry actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Time.UtcDateTime, actual.Time.UtcDateTime);
        Assert.AreEqual(expected.EntryId, actual.EntryId);
        Assert.AreEqual(expected.Program, actual.Program);
        Check.Sequence(actual.Arguments, [.. expected.Arguments]);
        Assert.AreEqual(expected.Location, actual.Location);
        Assert.AreEqual(expected.BytesBefore, actual.BytesBefore);
        Assert.AreEqual(expected.BytesAfter, actual.BytesAfter);
        Assert.AreEqual(expected.ExitCode, actual.ExitCode);
        Assert.AreEqual(expected.TimedOut, actual.TimedOut);
        Assert.AreEqual(expected.Result, actual.Result);
        Assert.AreEqual(expected.Reason, actual.Reason);
        Assert.AreEqual(expected.AppVersion, actual.AppVersion);
    }

    [TestMethod]
    public void EveryFieldSurvivesAWriteAndARead()
    {
        using var temp = new TempFolder("log-roundtrip");
        var log = CommandLog.Open(temp.Sub("commands.jsonl"));
        var entry = Sample() with
        {
            Arguments = ["nuget", "a b", "say \"hi\"", @"C:\x y\", "ünï"],
            ExitCode = 5,
            TimedOut = true,
            Result = CleanResult.Failed,
            Reason = "exit 5: \"nope\", then ünïcode\nand a second line",
        };
        log.Append(entry);
        var read = log.Read();
        Assert.AreEqual(1, read.Count);
        AssertSame(entry, read[0]);
    }

    [TestMethod]
    public void NullFieldsSurviveAWriteAndARead()
    {
        using var temp = new TempFolder("log-nulls");
        var log = CommandLog.Open(temp.Sub("commands.jsonl"));
        var entry = Sample() with { BytesAfter = null, ExitCode = null, TimedOut = true, Result = CleanResult.TimedOut, Reason = null };
        log.Append(entry);
        AssertSame(entry, log.Read().Single());
    }

    [TestMethod]
    public void EveryResultSurvivesAWriteAndARead()
    {
        using var temp = new TempFolder("log-results");
        var log = CommandLog.Open(temp.Sub("commands.jsonl"));
        foreach (var result in Enum.GetValues<CleanResult>())
        {
            log.Append(Sample(result.ToString()) with { Result = result });
        }

        Check.Sequence(log.Read().Select(e => e.Result.ToString()), [.. Enum.GetValues<CleanResult>().Select(r => r.ToString())]);
    }

    // A time with an offset is the same instant when read back; it is written as ISO 8601 UTC.
    [TestMethod]
    public void TheTimeIsWrittenAsIsoUtcAndKeepsItsInstant()
    {
        using var temp = new TempFolder("log-time");
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        var entry = Sample() with { Time = new DateTimeOffset(2026, 10, 21, 16, 30, 15, 123, TimeSpan.FromHours(7)) };
        log.Append(entry);
        using var doc = JsonDocument.Parse(File.ReadAllLines(path).Single());
        var text = doc.RootElement.GetProperty("time").GetString()!;
        Assert.IsTrue(text.EndsWith('Z'), "UTC, ending in Z: " + text);
        Assert.AreEqual(entry.Time.UtcDateTime, DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        Assert.AreEqual(entry.Time.UtcDateTime, log.Read().Single().Time.UtcDateTime);
    }

    // The file is one JSON object per line, with the plan's camelCase names and nothing else.
    [TestMethod]
    public void TheFileIsOneJsonObjectPerLineWithThePlansNames()
    {
        using var temp = new TempFolder("log-format");
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        log.Append(Sample("one"));
        log.Append(Sample("two") with { Reason = "has\na newline" });

        var lines = File.ReadAllLines(path);
        Assert.AreEqual(2, lines.Length, "one line per entry, a newline inside a text is escaped");
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
            Check.Set(doc.RootElement.EnumerateObject().Select(p => p.Name), FieldNames);
        }

        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        Assert.AreEqual("one", root.GetProperty("id").GetString());
        Assert.AreEqual("nuget-http", root.GetProperty("entryId").GetString());
        Assert.AreEqual(@"C:\Program Files\dotnet\dotnet.exe", root.GetProperty("program").GetString());
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("arguments").ValueKind);
        Check.Sequence(root.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!), "nuget", "locals", "http-cache", "--clear");
        Assert.AreEqual(@"C:\Users\me\AppData\Local\NuGet\v3-cache", root.GetProperty("location").GetString());
        Assert.AreEqual(123456789012L, root.GetProperty("bytesBefore").GetInt64());
        Assert.AreEqual(4096L, root.GetProperty("bytesAfter").GetInt64());
        Assert.AreEqual(0, root.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("timedOut").ValueKind);
        Assert.AreEqual("1.0.0", root.GetProperty("appVersion").GetString());
        Assert.AreEqual(JsonValueKind.String, root.GetProperty("time").ValueKind);
    }

    [TestMethod]
    public void EntriesAreReadOldestFirstAndEarlierOnesAreKept()
    {
        using var temp = new TempFolder("log-order");
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        log.Append(Sample("a"));
        log.Append(Sample("b"));
        // A second log object over the same file (a later run of the app) sees the earlier entries and appends after them.
        var again = CommandLog.Open(path);
        again.Append(Sample("c"));
        Check.Sequence(again.Read().Select(e => e.Id), "a", "b", "c");
    }

    [TestMethod]
    public void ALogThatWasNeverWrittenReadsAsEmpty()
    {
        using var temp = new TempFolder("log-none");
        Assert.AreEqual(0, CommandLog.Open(temp.Sub("no-such-folder", "commands.jsonl")).Read().Count);
    }

    // A line that cannot be parsed is skipped; the lines around it are read.
    [TestMethod]
    public void ACorruptLineIsSkipped()
    {
        using var temp = new TempFolder("log-corrupt");
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        log.Append(Sample("before"));
        File.AppendAllText(path, "this is not json\n");
        File.AppendAllText(path, "{\"id\":\"cut off in the mid\n");
        log.Append(Sample("after"));
        Check.Sequence(log.Read().Select(e => e.Id), "before", "after");
    }

    [TestMethod]
    public void BlankLinesAreSkipped()
    {
        using var temp = new TempFolder("log-blank");
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        log.Append(Sample("one"));
        File.AppendAllText(path, "\n\n");
        log.Append(Sample("two"));
        Check.Sequence(log.Read().Select(e => e.Id), "one", "two");
    }

    [TestMethod]
    public void CanWriteIsTrueForAFolderThatExists()
    {
        using var temp = new TempFolder("log-canwrite");
        Assert.IsTrue(CommandLog.Open(temp.Sub("commands.jsonl")).CanWrite());
    }

    // The folder does not exist yet but can be created (the first run on a machine).
    [TestMethod]
    public void CanWriteIsTrueWhenTheFolderCanBeCreated()
    {
        using var temp = new TempFolder("log-canwrite-new");
        var log = CommandLog.Open(temp.Sub("Ambysto", "Space", "commands.jsonl"));
        Assert.IsTrue(log.CanWrite());
        log.Append(Sample());
        Assert.AreEqual(1, log.Read().Count);
    }

    // The folder cannot be created because a path component is a file.
    [TestMethod]
    public void CanWriteIsFalseWhenTheFolderCannotBeCreated()
    {
        using var temp = new TempFolder("log-cannotwrite");
        var blocker = temp.File("blocker.txt", 5);
        Assert.IsFalse(CommandLog.Open(Path.Combine(blocker, "sub", "commands.jsonl")).CanWrite());
    }

    // The check does not write an entry: asking leaves the log empty.
    [TestMethod]
    public void CanWriteAddsNoEntry()
    {
        using var temp = new TempFolder("log-canwrite-quiet");
        var log = CommandLog.Open(temp.Sub("commands.jsonl"));
        Assert.IsTrue(log.CanWrite());
        Assert.AreEqual(0, log.Read().Count);
    }

    // Plan: %LOCALAPPDATA%\Ambysto\Space\commands.jsonl, not the trash journal's file.
    [TestMethod]
    public void TheDefaultPathIsInLocalAppDataAmbystoSpace()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "commands.jsonl");
        Assert.AreEqual(expected, CommandLog.DefaultPath, ignoreCase: true);
        Assert.AreEqual(CommandLog.DefaultPath, CommandLog.Open(CommandLog.DefaultPath).Path);
    }

    [TestMethod]
    public void OpenKeepsThePathItWasGiven()
    {
        using var temp = new TempFolder("log-path");
        var path = temp.Sub("commands.jsonl");
        Assert.AreEqual(path, CommandLog.Open(path).Path);
    }

    // ASP-20 phase B review round 1, item 2: a line that parses but is damaged is skipped like a torn one: an argument that is null,
    // a program, entry id or location that is empty. A good line before and after is read.
    private static void AssertDamagedLineIsSkipped(string name, Func<string, string> damage)
    {
        using var temp = new TempFolder("log-damaged-" + name);
        var path = temp.Sub("commands.jsonl");
        var log = CommandLog.Open(path);
        log.Append(Sample("before"));
        log.Append(Sample("damaged"));
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, [lines[0], damage(lines[1])]);
        log.Append(Sample("after"));
        Check.Sequence(log.Read().Select(e => e.Id), "before", "after");
    }

    [TestMethod]
    public void ALineWithANullArgumentIsSkipped() =>
        AssertDamagedLineIsSkipped("null-arg", line => line.Replace("\"arguments\":[\"nuget\",\"locals\",\"http-cache\",\"--clear\"]", "\"arguments\":[\"cache\",null]", StringComparison.Ordinal));

    [TestMethod]
    public void ALineWithAnEmptyProgramIsSkipped() =>
        AssertDamagedLineIsSkipped("no-program", line => line.Replace("\"program\":\"C:\\\\Program Files\\\\dotnet\\\\dotnet.exe\"", "\"program\":\"\"", StringComparison.Ordinal));

    [TestMethod]
    public void ALineWithAnEmptyEntryIdIsSkipped() =>
        AssertDamagedLineIsSkipped("no-entry", line => line.Replace("\"entryId\":\"nuget-http\"", "\"entryId\":\"\"", StringComparison.Ordinal));

    [TestMethod]
    public void ALineWithAnEmptyLocationIsSkipped() =>
        AssertDamagedLineIsSkipped("no-location", line => line.Replace("\"location\":\"C:\\\\Users\\\\me\\\\AppData\\\\Local\\\\NuGet\\\\v3-cache\"", "\"location\":\"\"", StringComparison.Ordinal));

    // The edits above must really change the line (a test that damages nothing proves nothing).
    [TestMethod]
    public void TheDamagingEditsAreRealEdits()
    {
        using var temp = new TempFolder("log-edits");
        var path = temp.Sub("commands.jsonl");
        CommandLog.Open(path).Append(Sample());
        var line = File.ReadAllLines(path).Single();
        StringAssert.Contains(line, "\"arguments\":[\"nuget\",\"locals\",\"http-cache\",\"--clear\"]");
        StringAssert.Contains(line, "\"program\":\"C:\\\\Program Files\\\\dotnet\\\\dotnet.exe\"");
        StringAssert.Contains(line, "\"entryId\":\"nuget-http\"");
        StringAssert.Contains(line, "\"location\":\"C:\\\\Users\\\\me\\\\AppData\\\\Local\\\\NuGet\\\\v3-cache\"");
    }
}
