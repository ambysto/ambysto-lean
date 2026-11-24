namespace Ambysto.Lean.Core.Rules;

/// <summary>The module a deletion comes from (docs/spec/05 <c>module</c>; decision 7 (c) binds roots to a module).</summary>
public enum LeanModule
{
    /// <summary>The map.</summary>
    Map,

    /// <summary>Large and old files.</summary>
    LargeOld,

    /// <summary>Duplicates.</summary>
    Duplicates,

    /// <summary>Uninstall leftovers (decision 7 (c), module-bound).</summary>
    Uninstall,

    /// <summary>Temporary files (docs/spec/03, v1 additions); root (a)'s literal exceptions, not module-bound.</summary>
    Temp,

    /// <summary>Evidence-verified obsolete app version leaves, module-bound.</summary>
    Installers,
}
