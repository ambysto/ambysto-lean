using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;

namespace Ambysto.Lean.Core.Tests.Acceptance.Journal;

/// <summary>One Put back test's world. The original folder (<c>orig\</c>) and a stand-in for the Recycle Bin's <c>$R</c>
/// items (<c>bin\</c>) are real folders in the scratch folder, on one volume, so the restore move keeps the file id.
/// "Inside a Recycle Bin folder" is faked through the seam: <see cref="FakeItemGate.Facts"/> answers for a stand-in with
/// its real stamp and the canonical path <c>&lt;volume root&gt;\$Recycle.Bin\&lt;current user SID&gt;\&lt;name&gt;</c>, the
/// only place the shell keeps this user's recycled items (docs/spec/05 Windows note on <c>trashPath</c>). A test that wants
/// "not in a Recycle Bin folder" lets the facts report the real canonical path. Nothing touches the real Recycle Bin.</summary>
internal sealed class PutBackBench : IDisposable
{
    private int count;
    private JournalStore? journal;

    public PutBackBench(string name, string? binVolumeFolder = null)
    {
        Temp = new TempFolder(name);
        Orig = Temp.Folder("orig");
        Bin = binVolumeFolder ?? Temp.Folder("bin");
        Directory.CreateDirectory(Bin);
        JournalPath = Temp.Sub("journal", "journal.jsonl");
        Gate = new FakeItemGate();
    }

    public TempFolder Temp { get; }

    public string Orig { get; }

    public string Bin { get; }

    public string JournalPath { get; }

    public FakeItemGate Gate { get; }

    public JournalStore Journal => journal ??= JournalStore.Open(JournalPath);

    public PutBack PutBack() => new(Journal, Gate);

    public static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>The canonical path the facts report for a stand-in: in this user's Recycle Bin folder on its volume.</summary>
    public static string InRecycleBin(string standIn) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(standIn))!, "$Recycle.Bin", Sid, Path.GetFileName(standIn));

    /// <summary>Makes <c>orig\&lt;relative&gt;</c> (a file, or a folder of two files), "recycles" it into the stand-in bin
    /// (a same-volume move: identity kept), journals the <c>trashed</c> entry, and makes the facts for the stand-in report
    /// a path inside this user's Recycle Bin folder (unless <paramref name="reportInBin"/> is false). With
    /// <paramref name="journal"/> false the entry is returned but not journaled (to journal a variant of it instead).</summary>
    public JournalEntry Stage(string relative, bool folder = false, string session = "s1", LeanModule module = LeanModule.Map, bool reportInBin = true, string? originalFolder = null, bool journal = true)
    {
        var original = Path.Combine(originalFolder ?? Orig, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        if (folder)
        {
            Directory.CreateDirectory(original);
            TempFolder.WriteBytes(Path.Combine(original, "one.bin"), 3000, 1);
            Directory.CreateDirectory(Path.Combine(original, "deep"));
            TempFolder.WriteBytes(Path.Combine(original, "deep", "two.bin"), 5000, 2);
        }
        else
        {
            TempFolder.WriteBytes(original, 4096, Interlocked.Increment(ref count));
        }

        var (logical, _) = KitObjects.Measure(original);
        var parentReal = KitObjects.RealFacts(Path.GetDirectoryName(original)!).CanonicalPath!;
        var standIn = Path.Combine(Bin, "$R" + Interlocked.Increment(ref count).ToString("X6", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(original));
        if (folder)
        {
            Directory.Move(original, standIn);
        }
        else
        {
            File.Move(original, standIn);
        }

        // The identity of the item as it is in the bin (equal to the original's on one volume; a fixture whose bin is on
        // another volume gets the identity of its copy there).
        var stamp = KitObjects.RealStamp(standIn);

        var entry = new JournalEntry(Guid.NewGuid().ToString("N"), session, DateTimeOffset.UtcNow, module, original, parentReal, original, logical, standIn,
            new TrashedIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId, logical), JournalResult.Trashed, null, "test");
        if (journal)
        {
            Journal.Append(entry);
        }

        if (reportInBin)
        {
            ReportInBin(standIn);
        }

        return entry;
    }

    /// <summary>The facts for <paramref name="standIn"/> report it inside this user's Recycle Bin folder (the real stamp,
    /// read now; a gone stand-in reads as gone).</summary>
    public void ReportInBin(string standIn) => Gate.FactsFor(standIn, p =>
    {
        var real = FileFacts.Read(p);
        return real.Succeeded ? KitObjects.FactsOk(InRecycleBin(p), real.Stamp!.Value) : real;
    });

    /// <summary>Appends <paramref name="entry"/> (a variant of a staged one) to the journal and returns it.</summary>
    public JournalEntry Add(JournalEntry entry)
    {
        Journal.Append(entry);
        return entry;
    }

    /// <summary>A <c>restored</c> entry for <paramref name="trashed"/>: the same session, paths and identity.</summary>
    public static JournalEntry RestoredOf(JournalEntry trashed) =>
        trashed with { Id = Guid.NewGuid().ToString("N"), Time = DateTimeOffset.UtcNow, Result = JournalResult.Restored };

    public IReadOnlyList<JournalEntry> Entries() => JournalStore.Open(JournalPath).Read();

    public void Dispose() => Temp.Dispose();
}
