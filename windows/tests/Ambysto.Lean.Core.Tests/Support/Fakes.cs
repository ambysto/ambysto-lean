using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ambysto.Lean.Core.Pro;
using Ambysto.Lean.Core.Uninstall;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>An uninstall source the test edits between checks: entries, Store packages and running processes.</summary>
internal sealed class FakeUninstallSource : IUninstallSource
{
    private int reads;

    public List<UninstallEntry> Entries { get; } = [];

    public List<StorePackage> StorePackages { get; } = [];

    public List<string> ProcessImagePaths { get; } = [];

    /// <summary>How many times <see cref="ReadEntries"/> was called.</summary>
    public int Reads => Volatile.Read(ref reads);

    /// <summary>When set, <see cref="ReadEntries"/> throws it (a source part that cannot be read).</summary>
    public Exception? EntriesFailure { get; set; }

    /// <summary>When set, <see cref="ReadStorePackages"/> throws it.</summary>
    public Exception? StoreFailure { get; set; }

    /// <summary>When set, <see cref="ReadProcessImagePaths"/> throws it.</summary>
    public Exception? ProcessFailure { get; set; }

    public IReadOnlyList<UninstallEntry> ReadEntries()
    {
        Interlocked.Increment(ref reads);
        if (EntriesFailure is { } failure)
        {
            throw failure;
        }

        lock (Entries)
        {
            return Entries.ToArray();
        }
    }

    public IReadOnlyList<StorePackage> ReadStorePackages()
    {
        if (StoreFailure is { } failure)
        {
            throw failure;
        }

        lock (StorePackages)
        {
            return StorePackages.ToArray();
        }
    }

    public IReadOnlyList<string> ReadProcessImagePaths()
    {
        if (ProcessFailure is { } failure)
        {
            throw failure;
        }

        lock (ProcessImagePaths)
        {
            return ProcessImagePaths.ToArray();
        }
    }

    /// <summary>A per-user, 64-bit-view entry with the usual values.</summary>
    public static UninstallEntry Entry(string keyName, string? displayName, string? installLocation, UninstallScope scope = UninstallScope.PerUser,
        RegistryViewKind view = RegistryViewKind.Bits64, string? publisher = "Contoso", bool systemComponent = false) =>
        new(keyName, scope, view, displayName, publisher, "1.0", installLocation, systemComponent);
}

/// <summary>A license check that answers from a table: key text → result.</summary>
internal sealed class FakeLicenseCheck : ILicenseCheck
{
    private readonly Dictionary<string, LicenseCheckResult> answers = new(StringComparer.Ordinal);

    public List<string> Asked { get; } = [];

    public FakeLicenseCheck Accepting(string keyText, string licensee, params string[] productIds)
    {
        answers[keyText] = new LicenseCheckResult(new LicenseFacts(licensee, new HashSet<string>(productIds, StringComparer.Ordinal)), null);
        return this;
    }

    public FakeLicenseCheck Refusing(string keyText, string reason)
    {
        answers[keyText] = new LicenseCheckResult(null, reason);
        return this;
    }

    public LicenseCheckResult Verify(string keyText)
    {
        Asked.Add(keyText);
        return answers.TryGetValue(keyText, out var result) ? result : new LicenseCheckResult(null, "Unknown key (fake)");
    }
}

/// <summary>A license check that always gives one answer (also one the table fake cannot express: a refusal with no
/// reason).</summary>
internal sealed class AnsweringLicenseCheck(LicenseCheckResult answer) : ILicenseCheck
{
    public LicenseCheckResult Verify(string keyText) => answer;
}

/// <summary>A license check that throws <see cref="Failure"/> for every key (a verifier that fails).</summary>
internal sealed class ThrowingLicenseCheck(Exception failure) : ILicenseCheck
{
    public Exception Failure { get; } = failure;

    public int Calls { get; private set; }

    public LicenseCheckResult Verify(string keyText)
    {
        Calls++;
        throw Failure;
    }
}
