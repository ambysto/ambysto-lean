namespace Ambysto.Lean.Core.Trash;

/// <summary>Reported by the Trasher before each item (docs/spec/07 Windows note: "Moving &lt;i&gt; of &lt;n&gt;: &lt;name&gt;").</summary>
public readonly record struct MoveProgress(int Index, int Count, string Name);
