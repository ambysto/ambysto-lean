using Ambysto.Kit.Safety;

namespace Ambysto.Lean.Core.Trash;

/// <summary>Lean's wrapper around the Kit's gate, in-use checker and file facts (plan Design "Trash"), so tests can force
/// the environment outcomes (elevated, not interactive) that the Kit's own seam keeps internal. The real one,
/// <see cref="KitItemGate"/>, passes straight through to the Kit.</summary>
public interface IItemGate
{
    /// <summary>docs/spec/02 step 3 (the Kit's whole order): approve or refuse one item.</summary>
    GateResult Check(GateRequest request);

    /// <summary>docs/spec/02 step 4: holders of an approved item.</summary>
    InUseResult InUse(ApprovedItem item);

    /// <summary>docs/spec/05 Put back: the environment check and the destination's rules.</summary>
    GateResult CheckRestoreDestination(string originalPath);

    /// <summary>Identity and facts of an item without opening it for data (post-verify; the Recycle Bin item at Put back).</summary>
    FileFactsResult Facts(string path);
}

/// <summary>The real gate: the Kit's <see cref="SafetyGate"/>, <see cref="InUseChecker"/> and <see cref="FileFacts"/> for
/// this user, with Lean's journal folder protected and the rule of <see cref="Rules.LeanRules"/>. The context is resolved
/// once, when the gate is made (one per deletion or Put back session, as the Kit asks).</summary>
public sealed class KitItemGate : IItemGate
{
    private readonly SafetyGate deleteGate;
    private readonly SafetyGate restoreGate;
    private readonly InUseChecker inUse = new();

    /// <summary>The gate for <paramref name="module"/>'s moves (the restore check uses <see cref="Rules.LeanRules.ForRestore"/>).</summary>
    public KitItemGate(Rules.LeanModule module, System.Collections.Generic.IReadOnlySet<string> moduleItems)
        : this(module, moduleItems, [])
    {
    }

    /// <summary>The gate for <paramref name="module"/>'s moves, with the user's protected folders (docs/spec/02 Windows note)
    /// joining Lean's own folder in the context, so the gate and the restore check refuse them.</summary>
    public KitItemGate(Rules.LeanModule module, System.Collections.Generic.IReadOnlySet<string> moduleItems, System.Collections.Generic.IReadOnlyList<string> userProtectedFolders)
    {
        System.ArgumentNullException.ThrowIfNull(moduleItems);
        System.ArgumentNullException.ThrowIfNull(userProtectedFolders);
        var journalFolder = System.IO.Path.GetDirectoryName(Journal.JournalStore.DefaultPath)!;
        var context = SafetyContext.ForCurrentUser([journalFolder, .. userProtectedFolders]);
        deleteGate = new SafetyGate(context, Rules.LeanRules.ForDelete(module, moduleItems));
        restoreGate = new SafetyGate(context, Rules.LeanRules.ForRestore());
    }

    /// <inheritdoc/>
    public GateResult Check(GateRequest request) => deleteGate.Check(request);

    /// <inheritdoc/>
    public InUseResult InUse(ApprovedItem item) => inUse.Check(item);

    /// <inheritdoc/>
    public GateResult CheckRestoreDestination(string originalPath) => restoreGate.CheckRestoreDestination(originalPath);

    /// <inheritdoc/>
    public FileFactsResult Facts(string path) => FileFacts.Read(path);
}
