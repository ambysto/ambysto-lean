using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Trash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>The calls the doubles saw, in order, shared by the gate and the recycler of one test: "facts &lt;path&gt;",
/// "check &lt;path&gt;", "inuse &lt;path&gt;", "restore &lt;path&gt;", "recycle &lt;path&gt;".</summary>
internal sealed class CallLog
{
    private readonly List<string> calls = [];

    public void Add(string verb, string path)
    {
        lock (calls)
        {
            calls.Add(verb + " " + path);
        }
    }

    public IReadOnlyList<string> All
    {
        get
        {
            lock (calls)
            {
                return calls.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Of(string verb) => All.Where(c => c.StartsWith(verb + " ", StringComparison.Ordinal)).ToArray();

    /// <summary>The index of the first call of <paramref name="verb"/> on <paramref name="path"/> (case-insensitive path),
    /// or -1.</summary>
    public int IndexOf(string verb, string path)
    {
        var all = All;
        for (var i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i], verb + " " + path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public bool Mentions(string path) => All.Any(c => c.EndsWith(" " + path, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => string.Join(" | ", All);
}

/// <summary>A double of Lean's gate seam. By default it approves what exists, measured now (the Kit's own facts read),
/// finds nothing in use, approves every restore destination and answers <see cref="Facts"/> with the Kit's real
/// <see cref="FileFacts.Read"/>. Each answer can be replaced per test; every call is logged.</summary>
internal sealed class FakeItemGate : IItemGate
{
    private readonly Dictionary<string, Func<string, FileFactsResult>> facts = new(StringComparer.OrdinalIgnoreCase);

    public FakeItemGate(CallLog? log = null) => Log = log ?? new CallLog();

    public CallLog Log { get; }

    public List<GateRequest> Requests { get; } = [];

    /// <summary>Replaces the gate's answer (default: <see cref="KitObjects.ApproveNow"/>, or "already gone").</summary>
    public Func<GateRequest, GateResult>? OnCheck { get; set; }

    /// <summary>Runs after the answer is chosen, before it is returned (to change the world mid-item).</summary>
    public Action<GateRequest>? AfterCheck { get; set; }

    public Func<ApprovedItem, InUseResult>? OnInUse { get; set; }

    public Action<ApprovedItem>? AfterInUse { get; set; }

    public Func<string, GateResult>? OnRestore { get; set; }

    /// <summary>Answers <see cref="Facts"/> for one path (compared case-insensitively, full path).</summary>
    public void FactsFor(string path, Func<string, FileFactsResult> answer) => facts[Path.GetFullPath(path)] = answer;

    public GateResult Check(GateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Log.Add("check", request.Path);
        lock (Requests)
        {
            Requests.Add(request);
        }

        var result = OnCheck is not null ? OnCheck(request) : DefaultCheck(request.Path);
        AfterCheck?.Invoke(request);
        return result;
    }

    public InUseResult InUse(ApprovedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Log.Add("inuse", item.CanonicalPath);
        var result = OnInUse is not null ? OnInUse(item) : KitObjects.NotInUse();
        AfterInUse?.Invoke(item);
        return result;
    }

    public GateResult CheckRestoreDestination(string originalPath)
    {
        Log.Add("restore", originalPath);
        if (OnRestore is not null)
        {
            return OnRestore(originalPath);
        }

        // As the Kit's restore check (GateRun.CheckRestore): the approved path is the destination under the parent's real
        // path, with the parent's stamp; a missing parent is refused (main session, aligned with the Kit 2026-10-17).
        var parent = Path.GetDirectoryName(originalPath)!;
        if (!Directory.Exists(parent))
        {
            return KitObjects.Refuse(RefusalReason.RestoreFolderMissing, null);
        }

        var realParent = KitObjects.RealFacts(parent).CanonicalPath!;
        return KitObjects.ApproveNow(parent, realParent + "\\" + Path.GetFileName(originalPath));
    }

    /// <summary>Runs at each <see cref="Facts"/> call, before it is answered.</summary>
    public Action<string>? BeforeFacts { get; set; }

    public FileFactsResult Facts(string path)
    {
        Log.Add("facts", path);
        BeforeFacts?.Invoke(path);
        return facts.TryGetValue(SafeFull(path), out var answer) ? answer(path) : FileFacts.Read(path);
    }

    private static GateResult DefaultCheck(string path) =>
        File.Exists(path) || Directory.Exists(path) ? KitObjects.ApproveNow(path) : KitObjects.Refuse(RefusalReason.AlreadyGone);

    private static string SafeFull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>A double of the shell move. By default it does what a good recycle does: it moves the item, keeping its
/// volume serial and file id, into a stand-in Recycle Bin folder in the test's scratch folder (same volume) and reports the
/// new item there. Nothing ever reaches the real Recycle Bin.</summary>
internal sealed class FakeRecycler : IShellRecycler
{
    private int count;

    public FakeRecycler(string binFolder, CallLog? log = null)
    {
        BinFolder = binFolder;
        Directory.CreateDirectory(binFolder);
        Log = log ?? new CallLog();
    }

    public string BinFolder { get; }

    public CallLog Log { get; }

    public List<string> Paths { get; } = [];

    /// <summary>Replaces the default move (gets the path, returns the report).</summary>
    public Func<string, RecycleReport>? OnRecycle { get; set; }

    /// <summary>Runs after the move, before the report is returned.</summary>
    public Action<string, RecycleReport>? AfterRecycle { get; set; }

    public RecycleReport Recycle(string approvedRealPath)
    {
        Log.Add("recycle", approvedRealPath);
        lock (Paths)
        {
            Paths.Add(approvedRealPath);
        }

        var report = OnRecycle is not null ? OnRecycle(approvedRealPath) : MoveToBin(approvedRealPath);
        AfterRecycle?.Invoke(approvedRealPath, report);
        return report;
    }

    /// <summary>A good recycle: the item moved into the stand-in bin, reported there.</summary>
    public RecycleReport MoveToBin(string path)
    {
        var target = NextBinPath(path);
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        return new RecycleReport(true, target, false, 0);
    }

    /// <summary>A fresh <c>$R</c>-style name in the stand-in bin with the item's extension.</summary>
    public string NextBinPath(string path) =>
        System.IO.Path.Combine(BinFolder, "$R" + Interlocked.Increment(ref count).ToString("X6", System.Globalization.CultureInfo.InvariantCulture) + System.IO.Path.GetExtension(path));
}

/// <summary>A shell move that must never be reached (the real-gate refusals of intent criterion 1).</summary>
internal sealed class FailingRecycler : IShellRecycler
{
    private int calls;

    /// <summary>How often the move was reached (checked after the run too, in case a caller swallowed the failure).</summary>
    public int Calls => Volatile.Read(ref calls);

    public RecycleReport Recycle(string approvedRealPath)
    {
        Interlocked.Increment(ref calls);
        Assert.Fail("the shell move was reached for " + approvedRealPath + "; the item had to be refused before any move");
        return default;
    }
}

/// <summary>File-system switches the journal tests flip, each undone by its <see cref="IDisposable"/>.</summary>
internal static class FileSwitches
{
    /// <summary>Marks the file read-only (created empty first if missing); dispose clears the flag.</summary>
    public static IDisposable ReadOnly(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            File.WriteAllBytes(file, []);
        }

        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        return new Undo(() => File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly));
    }

    /// <summary>Denies the current user creating, writing and appending to files in <paramref name="folder"/> (and the
    /// files in it, by inheritance); dispose removes that one rule. The user owns the folder, so it can always undo it.</summary>
    public static IDisposable DenyWrites(string folder)
    {
        var info = new DirectoryInfo(folder);
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.AppendData
            | System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Undo(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(current);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? pending = undo;

        public void Dispose()
        {
            Interlocked.Exchange(ref pending, null)?.Invoke();
        }
    }
}
