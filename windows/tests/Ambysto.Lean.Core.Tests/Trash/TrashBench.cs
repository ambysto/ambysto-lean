using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Journal;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Trash;
using KitIdentity = Ambysto.Kit.Safety.FileIdentity;

namespace Ambysto.Lean.Core.Tests.Acceptance.Trash;

/// <summary>One Trasher test's world: a scratch folder with the items (<c>items\</c>), a stand-in Recycle Bin
/// (<c>bin\</c>, same volume, so a move keeps the file id), a journal in its own folder, the gate and recycler doubles
/// sharing one call log, and scans by Lean's real <see cref="Scanner"/>.</summary>
internal sealed class TrashBench : IDisposable
{
    public TrashBench(string name)
    {
        Temp = new TempFolder(name);
        Items = Temp.Folder("items");
        JournalPath = Temp.Sub("journal", "journal.jsonl");
        Log = new CallLog();
        Gate = new FakeItemGate(Log);
        Recycler = new FakeRecycler(Temp.Sub("bin"), Log);
    }

    public TempFolder Temp { get; }

    public string Items { get; }

    public string JournalPath { get; }

    public CallLog Log { get; }

    public FakeItemGate Gate { get; }

    public FakeRecycler Recycler { get; }

    private JournalStore? journal;

    /// <summary>The journal (opened on first use, so a failure to open it never leaves the scratch folder behind).</summary>
    public JournalStore Journal => journal ??= JournalStore.Open(JournalPath);

    /// <summary>A file below <c>items\</c>.</summary>
    public string File(string relative, long length = 4096, int seed = 1) => Temp.File(@"items\" + relative, length, seed);

    public string ItemPath(string relative) => Path.Combine(Items, relative);

    public async Task<ScanTree> ScanAsync() => await new Scanner().ScanAsync(Items, null, CancellationToken.None).ConfigureAwait(false);

    public Trasher Trasher() => new(Gate, Recycler, Journal);

    public IReadOnlyList<TrashOutcome> Run(DeletionSession session, CancellationToken cancellationToken = default) =>
        Trasher().Run(session, cancellationToken);

    public IReadOnlyList<JournalEntry> Entries() => JournalStore.Open(JournalPath).Read();

    public static DeletionSession Session(LeanModule module, params SessionItem[] items) => new(module, items);

    public static SessionItem Item(ScanTree tree, string relative, string? keep = null) =>
        new(Trees.Node(tree, relative), keep is null ? null : Trees.Node(tree, keep));

    /// <summary>The Kit stamp the scan recorded for <paramref name="node"/> (the gate's "shown stamp").</summary>
    public static FileStamp ShownStamp(ScanNode node)
    {
        var s = node.Stamp ?? throw new InvalidOperationException("the scan recorded no stamp for " + node.Path);
        return new FileStamp(new KitIdentity(s.VolumeSerial, s.FileId), s.LastWriteTimeUtcTicks, s.Length);
    }

    public void Dispose() => Temp.Dispose();
}
