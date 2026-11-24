using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>Independent acceptance from ASP-7 Outcome 6, spec 02's recycle-only callback rule and the full24 review
/// briefs: failed setup cannot schedule/perform a move; only the approved root's reported item is authoritative.
/// Managed COM doubles never move/delete/copy anything. SHCreateItemFromParsingName only parses our scratch file.
/// Drafted in /tmp while final baseline gates run; no product implementation bodies were read.</summary>
[TestClass]
public sealed class ShellRecyclerComFailureTests
{
    private const int Denied = unchecked((int)0x80070005);
    private const int Abort = unchecked((int)0x80004004);
    private const string RootPage = @"C:\$Recycle.Bin\test-sid\$Rroot.bin";
    private const string ChildPage = @"C:\$Recycle.Bin\test-sid\$Rchild.bin";

    private static RecycleReport Run(RecordingOperation operation, string path)
    {
        var method = typeof(ShellRecycler).GetMethod("Perform", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(ShellRecycler.IFileOperation), typeof(string)], null);
        Assert.IsNotNull(method, "the existing private Perform(IFileOperation,string) entry is needed for this isolated COM proof");
        return (RecycleReport)method.Invoke(null, [operation, path])!;
    }

    [TestMethod]
    public void FailedAdvisePreservesTheErrorAndNeverConfiguresSchedulesPerformsOrUnadvises()
    {
        using var temp = new TempFolder("com-advise-refused");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation { AdviseResult = Denied };
        var result = Run(fake, path);
        Assert.IsFalse(result.Performed);
        Assert.AreEqual(Denied, result.ErrorCode);
        Assert.AreEqual(1, fake.Count("Advise"));
        Assert.AreEqual(0, fake.Count("SetOperationFlags"), "no operation configuration after Advise fails");
        Assert.AreEqual(0, fake.Count("DeleteItem"), "no item may be scheduled without the safety sink");
        Assert.AreEqual(0, fake.Count("PerformOperations"), "no move without the safety sink");
        Assert.AreEqual(0, fake.Count("Unadvise"), "a failed subscription has no valid cookie to unadvise");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void FailedFlagsNeverScheduleOrPerformAnItemAndReleaseTheSubscription()
    {
        using var temp = new TempFolder("com-flags-refused");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation { FlagsResult = Denied };
        var result = Run(fake, path);
        Assert.IsFalse(result.Performed);
        Assert.AreEqual(Denied, result.ErrorCode);
        Assert.AreEqual(1, fake.Count("SetOperationFlags"));
        Assert.AreEqual(0, fake.Count("DeleteItem"), "no item scheduled without required safe flags");
        Assert.AreEqual(0, fake.Count("PerformOperations"));
        Assert.AreEqual(1, fake.Count("Unadvise"));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void FailedDeleteItemNeverPerformsAnOperationAndReleasesTheSubscription()
    {
        using var temp = new TempFolder("com-schedule-refused");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation { DeleteResult = Denied };
        var result = Run(fake, path);
        Assert.IsFalse(result.Performed);
        Assert.AreEqual(Denied, result.ErrorCode);
        Assert.AreEqual(1, fake.Count("DeleteItem"));
        Assert.AreEqual(0, fake.Count("PerformOperations"), "failed scheduling never reaches the mutating COM call");
        Assert.AreEqual(1, fake.Count("Unadvise"));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void ExplicitShellAbortCannotBeReportedAsPerformed()
    {
        using var temp = new TempFolder("com-aborted");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation { OperationsAborted = true };
        var result = Run(fake, path);
        Assert.AreEqual(1, fake.Count("PerformOperations"));
        Assert.AreEqual(1, fake.Count("GetAnyOperationsAborted"));
        Assert.IsFalse(result.Performed, "the shell's explicit aborted=true must not become success");
        Assert.AreEqual(1, fake.Count("Unadvise"));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void NonRecyclePreDeleteCallbackAbortsAndItsCauseReachesTheReport()
    {
        using var temp = new TempFolder("com-nonrecycle");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation { PreDeleteFlags = 0 };
        var result = Run(fake, path);
        Assert.AreEqual(Abort, fake.PreDeleteAnswer, "real advised safety sink must veto a non-recycle callback");
        Assert.IsTrue(result.AbortedNonRecycle, "the report must retain the specific unsafe-shell cause");
        Assert.IsFalse(result.Performed);
        Assert.AreEqual(1, fake.Count("Unadvise"));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void SuccessfulPostDeleteReportsTheOnlyReturnedItem()
    {
        using var temp = new TempFolder("com-one-report");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation();
        fake.Reports.Add(new PostReport(path, RootPage));
        Assert.AreEqual(RootPage, Run(fake, path).ReportedNewItemPath, "S_OK (hrDelete=0) is a successful report");
        Assert.IsTrue(File.Exists(path), "the fake only reports; it never moves the scratch file");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ApprovedRootReportWinsOverADescendantRegardlessOfOrderOrPathCasing(bool rootLast, bool differentCase)
    {
        using var temp = new TempFolder("com-root-report");
        var path = temp.File("approved.bin", 4096);
        var root = new PostReport(differentCase ? path.ToUpperInvariant() : path, RootPage);
        var descendant = new PostReport(path + @"\child.bin", ChildPage);
        var fake = new RecordingOperation();
        fake.Reports.Add(rootLast ? descendant : root);
        fake.Reports.Add(rootLast ? root : descendant);
        Assert.AreEqual(RootPage, Run(fake, path).ReportedNewItemPath, "post-verify must receive the approved root, never a descendant chosen by callback order");
    }

    [TestMethod]
    public void TwoUnmatchedPostDeleteReportsCannotInventAnAuthoritativeItem()
    {
        using var temp = new TempFolder("com-ambiguous-report");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation();
        fake.Reports.Add(new PostReport(path + "-unmatched1", RootPage));
        fake.Reports.Add(new PostReport(path + "-unmatched2", ChildPage));
        Assert.IsNull(Run(fake, path).ReportedNewItemPath, "multiple reports with no approved-root match are ambiguous");
    }

    [TestMethod]
    public void ASingleUnmatchedReportIsRetainedAsTheDocumentedFallback()
    {
        using var temp = new TempFolder("com-single-fallback");
        var path = temp.File("approved.bin", 4096);
        var fake = new RecordingOperation();
        fake.Reports.Add(new PostReport(path + "-unmatched", RootPage));
        Assert.AreEqual(RootPage, Run(fake, path).ReportedNewItemPath, "positive control for the one-report fallback");
    }

    [TestMethod]
    public void NullAndEmptyPathsAreRejectedBeforeAnyRealFileOperation()
    {
        var recycler = new ShellRecycler();
        Assert.Throws<ArgumentNullException>(() => recycler.Recycle(null!));
        Assert.Throws<ArgumentException>(() => recycler.Recycle(string.Empty));
    }

    private sealed record PostReport(string Source, string Destination);

    private sealed class RecordingOperation : ShellRecycler.IFileOperation
    {
        private const uint Cookie = 73;
        private ShellRecycler.IFileOperationProgressSink? sink;
        private readonly List<string> calls = [];
        public int AdviseResult { get; init; }
        public int FlagsResult { get; init; }
        public int DeleteResult { get; init; }
        public bool OperationsAborted { get; init; }
        public uint? PreDeleteFlags { get; init; }
        public int? PreDeleteAnswer { get; private set; }
        public List<PostReport> Reports { get; } = [];
        public int Count(string name) => calls.FindAll(c => c == name).Count;
        private int Record(string name, int result = 0) { calls.Add(name); return result; }
        private int Unexpected(string name) { calls.Add(name); Assert.Fail("unexpected COM operation: " + name); return Denied; }
        public int Advise(ShellRecycler.IFileOperationProgressSink progressSink, out uint cookie)
        {
            cookie = Cookie;
            if (AdviseResult >= 0) sink = progressSink;
            return Record("Advise", AdviseResult);
        }
        public int Unadvise(uint cookie) { Assert.AreEqual(Cookie, cookie); return Record("Unadvise"); }
        public int SetOperationFlags(uint flags) => Record("SetOperationFlags", FlagsResult);
        public int DeleteItem(nint item, nint itemSink) { Assert.AreNotEqual(nint.Zero, item); return Record("DeleteItem", DeleteResult); }
        public int PerformOperations()
        {
            Record("PerformOperations");
            Assert.IsNotNull(sink, "a safety sink must be subscribed before any perform call");
            if (PreDeleteFlags is { } flags)
            {
                PreDeleteAnswer = sink.PreDeleteItem(flags, null);
                if (PreDeleteAnswer < 0) return PreDeleteAnswer.Value;
            }
            foreach (var report in Reports)
                _ = sink.PostDeleteItem(0x80, new ShellItem(report.Source), 0, new ShellItem(report.Destination));
            return 0;
        }
        public int GetAnyOperationsAborted(out bool aborted) { aborted = OperationsAborted; return Record("GetAnyOperationsAborted"); }
        public int SetProgressMessage(nint message) => Unexpected(nameof(SetProgressMessage));
        public int SetProgressDialog(nint dialog) => Unexpected(nameof(SetProgressDialog));
        public int SetProperties(nint properties) => Unexpected(nameof(SetProperties));
        public int SetOwnerWindow(nint window) => Unexpected(nameof(SetOwnerWindow));
        public int ApplyPropertiesToItem(nint item) => Unexpected(nameof(ApplyPropertiesToItem));
        public int ApplyPropertiesToItems(nint items) => Unexpected(nameof(ApplyPropertiesToItems));
        public int RenameItem(nint item, nint newName, nint itemSink) => Unexpected(nameof(RenameItem));
        public int RenameItems(nint items, nint newName) => Unexpected(nameof(RenameItems));
        public int MoveItem(nint item, nint folder, nint newName, nint itemSink) => Unexpected(nameof(MoveItem));
        public int MoveItems(nint items, nint folder) => Unexpected(nameof(MoveItems));
        public int CopyItem(nint item, nint folder, nint name, nint itemSink) => Unexpected(nameof(CopyItem));
        public int CopyItems(nint items, nint folder) => Unexpected(nameof(CopyItems));
        public int DeleteItems(nint items) => Unexpected(nameof(DeleteItems));
        public int NewItem(nint folder, uint attributes, nint name, nint template, nint itemSink) => Unexpected(nameof(NewItem));
    }

    private sealed class ShellItem(string path) : ShellRecycler.IShellItem
    {
        public int GetDisplayName(uint sigdn, out nint name) { name = Marshal.StringToCoTaskMemUni(path); return 0; }
        public int BindToHandler(nint context, in Guid handler, in Guid riid, out nint result) { result = nint.Zero; return Denied; }
        public int GetParent(out nint parent) { parent = nint.Zero; return Denied; }
        public int GetAttributes(uint mask, out uint attributes) { attributes = 0; return 0; }
        public int Compare(nint other, uint hint, out int order) { order = 0; return Denied; }
    }
}
