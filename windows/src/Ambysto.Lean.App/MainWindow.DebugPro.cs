#if DEBUG
using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Pro;

namespace Ambysto.Lean.App;

/// <summary>ASP-17 phase B (owner decision 2026-10-20): the QA argument <c>--pro</c>, compiled into Debug builds only, so the Pro side
/// of the UI can be tested before the production signing key exists. The whole file is inside <c>#if DEBUG</c>: a Release build has no
/// such argument, no such string and no check that accepts any key (docs/spec/06, License: Release builds have no other way to unlock
/// Pro). The gates prove it on the Release binary (windows/gates.json, binary_checks). It goes through the same seam a real key does,
/// <see cref="Entitlement.FromKey(string, ILicenseCheck)"/>, with a check that accepts this one call.</summary>
public sealed partial class MainWindow
{
    partial void ApplyDebugArguments(string[] arguments)
    {
        if (Array.IndexOf(arguments, "--pro") > 0)
        {
            ApplyEntitlement(Entitlement.FromKey("debug-build-pro", new DebugBuildCheck()));
        }
    }

    private sealed class DebugBuildCheck : ILicenseCheck
    {
        public LicenseCheckResult Verify(string keyText) =>
            new(new LicenseFacts("Debug build (--pro)", new HashSet<string>(StringComparer.Ordinal) { "space" }), null);
    }
}
#endif
