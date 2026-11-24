using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Acceptance.Journal;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Owner decision 2026-10-18 (option A; main session, with the change): an item that reached the Recycle Bin
/// with the approved identity but whose size there cannot be measured is journaled `trashed` with the "did not match"
/// reason and the size `TrashedIdentity.UnknownSize`; Put back then returns it by its identity alone (docs/spec/05).</summary>
[TestClass]
public sealed class UnknownBinSizeTests
{
    [TestMethod]
    public async Task ABinItemWhoseSizeCannotBeMeasuredIsJournaledWithAnUnknownSizeAndPutBackByIdentity()
    {
        using var b = new TrashBench("trash-unknown-size");
        b.File(@"proj\a.bin", 4096);
        b.File(@"proj\sub\b.bin", 4096);
        var later = b.File("later.bin");
        var tree = await b.ScanAsync();
        IDisposable? denied = null;
        b.Recycler.AfterRecycle = (_, report) => denied ??= DenyListing(Path.Combine(report.ReportedNewItemPath!, "sub"), b.ItemPath(@"proj\sub"));
        try
        {
            var outcomes = b.Run(TrashBench.Session(LeanModule.Map, TrashBench.Item(tree, "proj"), TrashBench.Item(tree, "later.bin")));

            Assert.IsNotNull(denied, "precondition: the bin item's folder was made unreadable: " + b.Log);
            var outcome = outcomes.Single();
            Assert.AreEqual(TrashResult.Trashed, outcome.Result, "reason: " + outcome.Reason + "; calls: " + b.Log);
            Assert.AreEqual(Text.MovedItemDidNotMatch, outcome.Reason);
            Check.True(File.Exists(later), "the session stopped before the later item");
            var entry = b.Entries().Single();
            Assert.AreEqual(JournalResult.Trashed, entry.Result);
            Assert.AreEqual(TrashedIdentity.UnknownSize, entry.TrashedIdentity?.LogicalBytes, "the size is recorded as unknown");

            // Put back while the size still cannot be measured: the identity alone decides.
            b.Gate.FactsFor(entry.TrashPath!, p =>
            {
                var real = FileFacts.Read(p);
                return real.Succeeded ? KitObjects.FactsOk(PutBackBench.InRecycleBin(p), real.Stamp!.Value) : real;
            });
            var back = new PutBack(b.Journal, b.Gate).Restore(entry);
            Check.True(back.Restored, "put back: " + back.Reason);
            Check.True(Directory.Exists(b.ItemPath("proj")), "the folder is back");
        }
        finally
        {
            denied?.Dispose();
        }
    }

    // Denies the current user listing the folder; dispose removes that one rule (the user owns it, so it can undo it),
    // wherever the folder is by then: still in the bin, or put back.
    private static Undo DenyListing(string folder, string putBackPlace)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Undo(() =>
        {
            foreach (var candidate in new[] { folder, putBackPlace })
            {
                if (Directory.Exists(candidate))
                {
                    var here = new DirectoryInfo(candidate);
                    var s = here.GetAccessControl();
                    s.RemoveAccessRuleSpecific(rule);
                    here.SetAccessControl(s);
                }
            }
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? pending = undo;

        public void Dispose() => System.Threading.Interlocked.Exchange(ref pending, null)?.Invoke();
    }
}
