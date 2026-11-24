using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>The real shell move, <see cref="ShellRecycler"/> (briefs S1–S5 of the phase B gaps): intent Outcome 6 steps 6
/// and 7 ("one <c>IFileOperation</c> per item (allow undo, no confirmation, no error UI, silent)"; "the item the shell
/// reports is opened by handle and has the approved volume serial, file id and size"), docs/spec/05 <c>trashPath</c>
/// ("the <c>$R</c> item in <c>$Recycle.Bin\&lt;user SID&gt;</c>"), docs/spec/02 Windows note on steps 6–7 (any pre-delete
/// callback without "recycle if possible" aborts the move), plan notes "Shell recycler". Fixtures are uniquely named items
/// in this user's real profile (root (a), on C:, a fixed NTFS volume with its Recycle Bin on), made by
/// <see cref="RealScratch"/>. Every item that reaches the real Recycle Bin is put back by Lean's own Put back (the real
/// gate's restore check; the <c>$I</c> record it leaves is the test's own and is removed), or left in the bin; the Recycle
/// Bin is never emptied. Console session; one at a time (the Recycle Bin's count is compared).</summary>
[TestClass]
[DoNotParallelize]
public sealed class ShellRecyclerConsoleTests
{
    private const int ENotFound = unchecked((int)0x80070002);

    private static void RequireConsoleSession()
    {
        if (Process.GetCurrentProcess().SessionId == 0)
        {
            Assert.Inconclusive("the real Recycle Bin is driven only in the console session (vm-ui.sh)");
        }
    }

    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    /// <summary><c>&lt;volume root&gt;\$Recycle.Bin\&lt;this user's SID&gt;</c> of the volume of <paramref name="path"/>.</summary>
    private static string BinFolderOf(string path) => Path.Combine(Path.GetPathRoot(Path.GetFullPath(path))!, "$Recycle.Bin", Sid);

    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>The entries of this user's Recycle Bin folder on <paramref name="root"/> (hidden ones included).</summary>
    private static HashSet<string> BinEntries(string root)
    {
        var folder = Path.Combine(root, "$Recycle.Bin", Sid);
        return Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*", Everything).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Describe(RecycleReport r) =>
        $"Performed={r.Performed}, ReportedNewItemPath={r.ReportedNewItemPath ?? "null"}, AbortedNonRecycle={r.AbortedNonRecycle}, ErrorCode=0x{r.ErrorCode:X8}";

    /// <summary>Puts the item the shell reported back to <paramref name="original"/> with Lean's own Put back (journal in a
    /// scratch folder, the real gate's restore check), then removes the <c>$I</c> record of that <c>$R</c> item (the
    /// test's own; Put back leaves it, docs/spec/05). When Put back refuses, the item is left in the Recycle Bin.</summary>
    internal static void PutBackOrLeave(string? reported, string original)
    {
        if (reported is null || !Exists(reported) || Exists(original))
        {
            return;
        }

        try
        {
            using var temp = new TempFolder("recycler-putback");
            var journal = JournalStore.Open(temp.Sub("journal.jsonl"));
            var stamp = KitObjects.RealStamp(reported);
            var logical = RecycledItem.Measure(reported) ?? 0;
            var parentReal = FileFacts.Read(Path.GetDirectoryName(original)!).CanonicalPath ?? Path.GetDirectoryName(original);
            var entry = new JournalEntry(Guid.NewGuid().ToString("N"), "recycler-test", DateTimeOffset.UtcNow, LeanModule.Map, original, parentReal, original, logical,
                reported, new TrashedIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId, logical), JournalResult.Trashed, null, "test");
            journal.Append(entry);
            var outcome = new PutBack(journal, new KitItemGate(LeanModule.Map, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).Restore(entry);
            if (outcome.Restored)
            {
                var dollarI = Path.Combine(Path.GetDirectoryName(reported)!, "$I" + Path.GetFileName(reported)[2..]);
                if (File.Exists(dollarI))
                {
                    File.Delete(dollarI);
                }
            }
            else
            {
                Console.WriteLine($"left in the Recycle Bin: {reported} (Put back said: {outcome.Reason})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or AssertFailedException)
        {
            Console.WriteLine($"left in the Recycle Bin: {reported} ({e.Message})");
        }
    }

    // S1 [top]: a 4 KB file and a folder holding two files (one in a subfolder) are recycled: success, no abort, the reported
    // item is a $R item directly inside <C:>\$Recycle.Bin\<this user's SID> with the item's own volume serial and file id;
    // the original path is gone; the folder's files are inside the reported item.
    [TestMethod]
    [TestCategory("Console")]
    public void AFileAndAFolderAreRecycledAndReportedWithTheirIdentity()
    {
        RequireConsoleSession();
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(top, "recycle-me.bin");
        TempFolder.WriteBytes(file, 4096, 21);
        var folder = Path.Combine(top, "recycle-folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        TempFolder.WriteBytes(Path.Combine(folder, "a.bin"), 3000, 22);
        TempFolder.WriteBytes(Path.Combine(folder, "sub", "b.bin"), 5000, 23);

        foreach (var item in new[] { file, folder })
        {
            var before = Native.IdentityOf(item);
            var bin = BinFolderOf(item);
            var report = new ShellRecycler().Recycle(item);
            try
            {
                var what = item + ": " + Describe(report);
                Check.True(report.Performed, "the shell's operation succeeded; " + what);
                Assert.AreEqual(0, report.ErrorCode, what);
                Check.False(report.AbortedNonRecycle, "no abort; " + what);
                Assert.IsNotNull(report.ReportedNewItemPath, "the shell reported the new item; " + what);
                var reported = report.ReportedNewItemPath!;
                Check.True(string.Equals(Path.GetDirectoryName(reported), bin, StringComparison.OrdinalIgnoreCase),
                    $"the reported item is directly inside {bin}: {reported}");
                Check.True(Path.GetFileName(reported).StartsWith("$R", StringComparison.OrdinalIgnoreCase), "a $R item: " + reported);
                Check.False(Exists(item), "the original path no longer exists: " + item);
                Check.True(Exists(reported), "the reported item exists: " + reported);
                var after = Native.IdentityOf(reported);
                Assert.AreEqual(before.VolumeSerial, after.VolumeSerial, "the same volume serial: " + reported);
                Assert.AreEqual(before.FileId, after.FileId, "the same file id: " + reported);
                if (item == folder)
                {
                    Check.True(File.Exists(Path.Combine(reported, "a.bin")), "a.bin is inside the reported folder");
                    Check.True(File.Exists(Path.Combine(reported, "sub", "b.bin")), "sub\\b.bin is inside the reported folder");
                }
            }
            finally
            {
                PutBackOrLeave(report.ReportedNewItemPath, item);
            }
        }
    }

    // S2 [top]: a folder holding a plain file and a file whose full path is 260+ characters (long paths are off on the test
    // bed; the fixture is made through \\?\ paths) is aborted: the shell would delete it for good as a whole. The folder,
    // both files and their file ids stay; the Recycle Bin gains nothing (count, size and entries). Control: S1's folder.
    [TestMethod]
    [TestCategory("Console")]
    public void AFolderWithALongDescendantIsAbortedAndStaysInPlace()
    {
        RequireConsoleSession();
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var folder = Path.Combine(top, "L");
        Directory.CreateDirectory(folder);
        var plain = Path.Combine(folder, "plain.bin");
        TempFolder.WriteBytes(plain, 4096, 31);
        // Every name below the scratch folder is a valid 8.3 name, so NTFS keeps no shorter spelling of them. (First run, 2026-10-18, with
        // 40-character folder names: C: keeps 8.3 names, and the shell recycled the whole folder through them, Performed
        // true, no abort; the test-machine notes probe row "Path of 260+ chars … on C: (8.3 on) … Recycle Bin via the 8.3 name".)
        var deepDir = folder;
        var level = 0;
        while ((deepDir + @"\longfile.bin").Length < 290) // the scratch folder's own name has an 8.3 spelling, 14 characters shorter
        {
            deepDir = Path.Combine(deepDir, "d" + (level++).ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
        }

        var longFile = Path.Combine(deepDir, "longfile.bin");
        RecycleReport report = default;
        try
        {
            Directory.CreateDirectory(@"\\?\" + deepDir);
            TempFolder.WriteBytes(@"\\?\" + longFile, 2048, 32);
            Check.AtLeast(260, longFile.Length, "precondition: the descendant's full path length");
            var shortSpelling = Native.ShortPathOf(@"\\?\" + longFile);
            Check.True(shortSpelling is null || shortSpelling.Length - 4 >= 260, "precondition: no spelling of the descendant under 260 characters: " + shortSpelling);
            var folderId = Native.IdentityOf(folder);
            var plainId = Native.IdentityOf(plain);
            var longId = Native.IdentityOf(@"\\?\" + longFile);
            var root = Path.GetPathRoot(folder)!;
            var binBefore = Native.QueryRecycleBin(root);
            var entriesBefore = BinEntries(root);

            report = new ShellRecycler().Recycle(folder);

            var what = Describe(report);
            Check.True(report.AbortedNonRecycle, "aborted on a pre-delete without \"recycle if possible\"; " + what);
            Check.False(report.Performed, "the operation did not succeed; " + what);
            Assert.AreNotEqual(0, report.ErrorCode, what);
            Assert.IsNull(report.ReportedNewItemPath, what);
            Check.True(Directory.Exists(folder), "the folder stays in place");
            Assert.AreEqual(folderId, Native.IdentityOf(folder), "the same folder");
            Assert.AreEqual(plainId, Native.IdentityOf(plain), "its plain file, the same file");
            Assert.AreEqual(longId, Native.IdentityOf(@"\\?\" + longFile), "its long file, the same file");
            var binAfter = Native.QueryRecycleBin(root);
            Assert.AreEqual(binBefore.Items, binAfter.Items, "the Recycle Bin's item count");
            Assert.AreEqual(binBefore.Bytes, binAfter.Bytes, "the Recycle Bin's size");
            var entriesAfter = BinEntries(root);
            Check.Set(entriesAfter, entriesBefore.ToArray());
        }
        finally
        {
            PutBackOrLeave(report.ReportedNewItemPath, folder);
            if (Directory.Exists(@"\\?\" + folder))
            {
                Directory.Delete(@"\\?\" + folder, recursive: true);
            }
        }
    }

    // S3 [normal]: a missing path fails without a move: the parse error for a missing file, 0x80070002; the recycler
    // reports what the shell said (plan Design "Trasher").
    [TestMethod]
    [TestCategory("Console")]
    public void AMissingPathFailsWithoutAMove()
    {
        RequireConsoleSession();
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var missing = Path.Combine(top, "does-not-exist.bin");
        var root = Path.GetPathRoot(top)!;
        var binBefore = Native.QueryRecycleBin(root);
        var entriesBefore = BinEntries(root);

        var report = new ShellRecycler().Recycle(missing);

        var what = Describe(report);
        Check.False(report.Performed, what);
        Check.False(report.AbortedNonRecycle, what);
        Assert.IsNull(report.ReportedNewItemPath, what);
        Assert.AreEqual(ENotFound, report.ErrorCode, what);
        var binAfter = Native.QueryRecycleBin(root);
        Assert.AreEqual(binBefore.Items, binAfter.Items, "the Recycle Bin's item count");
        Assert.AreEqual(binBefore.Bytes, binAfter.Bytes, "the Recycle Bin's size");
        Check.Set(BinEntries(root), entriesBefore.ToArray());
    }

    // S4 [normal]: a file the test holds open without delete sharing stays in place and is not reported as moved: no new
    // item, no abort, Performed false ("The shell's operation returned success" would be untrue), an error code. A report
    // of success for an item that did not move is a contract question for the main session.
    [TestMethod]
    [TestCategory("Console")]
    public void AFileHeldOpenIsNotReportedAsMoved()
    {
        RequireConsoleSession();
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var file = Path.Combine(top, "held-open.bin");
        TempFolder.WriteBytes(file, 4096, 41);
        var id = Native.IdentityOf(file);
        RecycleReport report = default;
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                report = new ShellRecycler().Recycle(file);
            }

            var what = Describe(report);
            Check.True(File.Exists(file), "the file stays in place; " + what);
            Assert.AreEqual(id, Native.IdentityOf(file), "the same file; " + what);
            Assert.IsNull(report.ReportedNewItemPath, what);
            Check.False(report.AbortedNonRecycle, what);
            Check.False(report.Performed, "not reported as performed; " + what);
            Assert.AreNotEqual(0, report.ErrorCode, what);
        }
        finally
        {
            PutBackOrLeave(report.ReportedNewItemPath, file);
        }
    }

    // S5 [low]: the item is passed in its 8.3 short spelling; the reported item is still found (the one whose source path is
    // the approved path, or the only one reported) and has the file's identity. This row bites only if the shell reports
    // the item under its long spelling; if it reports the short spelling, the row is equivalent to S1.
    [TestMethod]
    [TestCategory("Console")]
    public void TheOnlyReportedItemIsUsedWhenTheSpellingDiffers()
    {
        RequireConsoleSession();
        using var scratch = new RealScratch();
        var top = scratch.NewFolder(RealGate.Profile);
        var folder = Path.Combine(top, "Long Folder Name For Short Spelling");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a file with a long name.bin");
        TempFolder.WriteBytes(file, 4096, 51);
        var shortPath = Native.ShortPathOf(file);
        if (shortPath is null)
        {
            Assert.Inconclusive("the volume keeps no 8.3 name for " + file);
        }

        var id = Native.IdentityOf(file);
        var report = new ShellRecycler().Recycle(shortPath!);
        try
        {
            var what = $"{shortPath}: {Describe(report)}";
            Check.True(report.Performed, what);
            Assert.IsNotNull(report.ReportedNewItemPath, "the reported item is found although the spelling differs; " + what);
            var reported = report.ReportedNewItemPath!;
            Check.True(string.Equals(Path.GetDirectoryName(reported), BinFolderOf(file), StringComparison.OrdinalIgnoreCase), "in this user's Recycle Bin folder: " + reported);
            Check.True(Path.GetFileName(reported).StartsWith("$R", StringComparison.OrdinalIgnoreCase), "a $R item: " + reported);
            Assert.AreEqual(id, Native.IdentityOf(reported), "the file's identity");
            Check.False(File.Exists(file), "moved");
        }
        finally
        {
            PutBackOrLeave(report.ReportedNewItemPath, file);
        }
    }
}
