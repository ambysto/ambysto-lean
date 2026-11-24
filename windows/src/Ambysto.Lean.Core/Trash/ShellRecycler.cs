using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace Ambysto.Lean.Core.Trash;

/// <summary>The one shell move (plan Design "Trasher"; the only place the tripwire allows <c>IFileOperation</c>): one
/// <c>IFileOperation.DeleteItem</c> with <c>FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT</c>, on its
/// own single-threaded apartment thread, whose progress sink aborts the whole operation from any <c>PreDeleteItem</c>
/// (the item's or a descendant's) that does not carry "recycle if possible": Windows would otherwise delete that item, or
/// the whole tree, for good while the operation still reports success (the test-machine notes, IFileOperation facts). It reports
/// what the shell said; the Trasher's post-verify decides.</summary>
public sealed partial class ShellRecycler : IShellRecycler
{
    internal const uint FofSilent = 0x0004;
    internal const uint FofNoConfirmation = 0x0010;
    internal const uint FofAllowUndo = 0x0040;
    internal const uint FofNoErrorUi = 0x0400;

    /// <summary>The operation flags (plan Design "Trasher").</summary>
    internal const uint OperationFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent;

    /// <summary><c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c>, the flag a <c>PreDeleteItem</c> must carry.</summary>
    internal const uint RecycleIfPossible = 0x0080;

    internal const int EAbort = unchecked((int)0x80004004);
    private const int SOk = 0;
    private const uint ClsctxInprocServer = 1;
    private const uint SigdnFileSysPath = 0x80058000;

    private static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid IidFileOperation = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>What the sink answers a <c>PreDeleteItem</c> with <paramref name="flags"/>: <c>S_OK</c> only when the
    /// shell will recycle the item, <c>E_ABORT</c> otherwise (hand mutation check (a) breaks this).</summary>
    internal static int PreDeleteAnswer(uint flags) => (flags & RecycleIfPossible) != 0 ? SOk : EAbort;

    /// <inheritdoc/>
    public RecycleReport Recycle(string approvedRealPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvedRealPath);
        var report = new RecycleReport(false, null, false, EAbort);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                report = RecycleOnThisThread(approvedRealPath);
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Lean shell recycler",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure is null ? report : new RecycleReport(false, null, false, failure.HResult);
    }

    private static RecycleReport RecycleOnThisThread(string path)
    {
        var wrappers = new StrategyBasedComWrappers();
        var hr = CoCreateInstance(in ClsidFileOperation, 0, ClsctxInprocServer, in IidFileOperation, out var operationPointer);
        if (hr < 0)
        {
            return new RecycleReport(false, null, false, hr);
        }

        var operation = (IFileOperation)wrappers.GetOrCreateObjectForComInstance(operationPointer, CreateObjectFlags.UniqueInstance);
        Marshal.Release(operationPointer);
        try
        {
            return Perform(operation, path);
        }
        finally
        {
            // Released here, on its own apartment's thread, not later by the finalizer (interim review MINOR 5).
            ((ComObject)(object)operation).FinalRelease();
        }
    }

    private static RecycleReport Perform(IFileOperation operation, string path)
    {
        int hr;

        hr = SHCreateItemFromParsingName(path, 0, in IidShellItem, out var itemPointer);
        if (hr < 0)
        {
            return new RecycleReport(false, null, false, hr);
        }

        try
        {
            var sink = new Sink(path);
            hr = operation.Advise(sink, out var cookie);
            if (hr < 0)
            {
                return new RecycleReport(false, null, false, hr);
            }

            try
            {
                hr = operation.SetOperationFlags(OperationFlags);
                if (hr >= 0)
                {
                    hr = operation.DeleteItem(itemPointer, 0);
                }

                if (hr >= 0)
                {
                    hr = operation.PerformOperations();
                }

                var aborted = false;
                if (operation.GetAnyOperationsAborted(out var anyAborted) >= 0)
                {
                    aborted = anyAborted;
                }

                var performed = hr >= 0 && !aborted && !sink.AbortedNonRecycle;
                return new RecycleReport(performed, sink.NewItemPath, sink.AbortedNonRecycle, performed ? 0 : (hr < 0 ? hr : EAbort));
            }
            finally
            {
                _ = operation.Unadvise(cookie);
            }
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static string? PathOf(IShellItem? item)
    {
        if (item is null || item.GetDisplayName(SigdnFileSysPath, out var name) < 0 || name == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    // The progress sink: aborts any non-recycle delete, and records the item the shell created in the Recycle Bin for the
    // approved item (the item whose path is the one asked for; failing that, the only item reported, which post-verify
    // then checks by identity and size).
    [GeneratedComClass]
    internal sealed partial class Sink(string approvedPath) : IFileOperationProgressSink
    {
        private string? matched;
        private string? only;
        private int reported;

        internal bool AbortedNonRecycle { get; private set; }

        internal string? NewItemPath => matched ?? (reported == 1 ? only : null);

        public int PreDeleteItem(uint flags, IShellItem? item)
        {
            var answer = PreDeleteAnswer(flags);
            if (answer != SOk)
            {
                AbortedNonRecycle = true;
            }

            return answer;
        }

        public int PostDeleteItem(uint flags, IShellItem? item, int hrDelete, IShellItem? newlyCreated)
        {
            if (hrDelete >= 0 && PathOf(newlyCreated) is { } created)
            {
                reported++;
                only = created;
                if (string.Equals(PathOf(item), approvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    matched = created;
                }
            }

            return SOk;
        }

        public int StartOperations() => SOk;

        public int FinishOperations(int result) => SOk;

        public int PreRenameItem(uint flags, nint item, nint newName) => SOk;

        public int PostRenameItem(uint flags, nint item, nint newName, int hrRename, nint newlyCreated) => SOk;

        public int PreMoveItem(uint flags, nint item, nint destinationFolder, nint newName) => SOk;

        public int PostMoveItem(uint flags, nint item, nint destinationFolder, nint newName, int hrMove, nint newlyCreated) => SOk;

        public int PreCopyItem(uint flags, nint item, nint destinationFolder, nint newName) => SOk;

        public int PostCopyItem(uint flags, nint item, nint destinationFolder, nint newName, int hrCopy, nint newlyCreated) => SOk;

        public int PreNewItem(uint flags, nint destinationFolder, nint newName) => SOk;

        public int PostNewItem(uint flags, nint destinationFolder, nint newName, nint templateName, uint fileAttributes, int hrNew, nint newItem) => SOk;

        public int UpdateProgress(uint workTotal, uint workSoFar) => SOk;

        public int ResetTimer() => SOk;

        public int PauseTimer() => SOk;

        public int ResumeTimer() => SOk;
    }

    [GeneratedComInterface]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal partial interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

        [PreserveSig] int GetParent(out nint parent);

        [PreserveSig] int GetDisplayName(uint sigdn, out nint name);

        [PreserveSig] int GetAttributes(uint mask, out uint attributes);

        [PreserveSig] int Compare(nint other, uint hint, out int order);
    }

    // The vtable order of shobjidl_core.h; only the members used are typed.
    [GeneratedComInterface]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    internal partial interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);

        [PreserveSig] int Unadvise(uint cookie);

        [PreserveSig] int SetOperationFlags(uint flags);

        [PreserveSig] int SetProgressMessage(nint message);

        [PreserveSig] int SetProgressDialog(nint dialog);

        [PreserveSig] int SetProperties(nint properties);

        [PreserveSig] int SetOwnerWindow(nint window);

        [PreserveSig] int ApplyPropertiesToItem(nint item);

        [PreserveSig] int ApplyPropertiesToItems(nint items);

        [PreserveSig] int RenameItem(nint item, nint newName, nint sink);

        [PreserveSig] int RenameItems(nint items, nint newName);

        [PreserveSig] int MoveItem(nint item, nint destinationFolder, nint newName, nint sink);

        [PreserveSig] int MoveItems(nint items, nint destinationFolder);

        [PreserveSig] int CopyItem(nint item, nint destinationFolder, nint copyName, nint sink);

        [PreserveSig] int CopyItems(nint items, nint destinationFolder);

        [PreserveSig] int DeleteItem(nint item, nint sink);

        [PreserveSig] int DeleteItems(nint items);

        [PreserveSig] int NewItem(nint destinationFolder, uint fileAttributes, nint name, nint templateName, nint sink);

        [PreserveSig] int PerformOperations();

        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [GeneratedComInterface]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    internal partial interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();

        [PreserveSig] int FinishOperations(int result);

        [PreserveSig] int PreRenameItem(uint flags, nint item, nint newName);

        [PreserveSig] int PostRenameItem(uint flags, nint item, nint newName, int hrRename, nint newlyCreated);

        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destinationFolder, nint newName);

        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destinationFolder, nint newName, int hrMove, nint newlyCreated);

        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destinationFolder, nint newName);

        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destinationFolder, nint newName, int hrCopy, nint newlyCreated);

        [PreserveSig] int PreDeleteItem(uint flags, IShellItem? item);

        [PreserveSig] int PostDeleteItem(uint flags, IShellItem? item, int hrDelete, IShellItem? newlyCreated);

        [PreserveSig] int PreNewItem(uint flags, nint destinationFolder, nint newName);

        [PreserveSig] int PostNewItem(uint flags, nint destinationFolder, nint newName, nint templateName, uint fileAttributes, int hrNew, nint newItem);

        [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);

        [PreserveSig] int ResetTimer();

        [PreserveSig] int PauseTimer();

        [PreserveSig] int ResumeTimer();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint result);
}
