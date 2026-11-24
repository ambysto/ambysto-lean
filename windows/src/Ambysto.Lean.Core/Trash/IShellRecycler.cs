namespace Ambysto.Lean.Core.Trash;

/// <summary>What one shell move reported (plan Design "Trasher").</summary>
/// <param name="Performed">The shell's operation returned success.</param>
/// <param name="ReportedNewItemPath">The item the shell reported in the Recycle Bin (<c>PostDeleteItem</c>), or null when
/// it reported none.</param>
/// <param name="AbortedNonRecycle">A pre-delete callback (the item's or a descendant's) did not say "recycle if possible",
/// so the operation was aborted.</param>
/// <param name="ErrorCode">The HRESULT, 0 on success.</param>
public readonly record struct RecycleReport(bool Performed, string? ReportedNewItemPath, bool AbortedNonRecycle, int ErrorCode);

/// <summary>The one shell move of the Trasher. The real one is <c>IFileOperation</c> in <c>Trash/ShellRecycler.cs</c>, the
/// only place the tripwire allows it; tests force its outcomes.</summary>
public interface IShellRecycler
{
    /// <summary>Moves <paramref name="approvedRealPath"/> to the Recycle Bin, aborting on any non-recycle pre-delete.</summary>
    RecycleReport Recycle(string approvedRealPath);
}
