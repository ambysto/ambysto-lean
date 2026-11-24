using System;
using System.Collections.Generic;

namespace Ambysto.Lean.Core.Pro;

/// <summary>What a verified key says, in Lean's terms.</summary>
public sealed record LicenseFacts(string Licensee, IReadOnlySet<string> ProductIds);

/// <summary>The outcome of checking a key: the facts, or the reason it was refused (the Kit's words).</summary>
public sealed record LicenseCheckResult(LicenseFacts? License, string? RefusalReason);

/// <summary>Checks a pasted key offline. The real one is the Kit's <c>LicenseVerifier</c> with the keys compiled into
/// the release; tests give their own (the Kit's test keys are internal to the Kit).</summary>
public interface ILicenseCheck
{
    /// <summary>Verifies <paramref name="keyText"/>.</summary>
    LicenseCheckResult Verify(string keyText);
}

/// <summary>Free or Pro (docs/spec/06, License): Pro only for a key that verifies offline and whose products contain
/// <c>space</c>. Release builds have no other way to Pro.</summary>
public sealed class Entitlement
{
    /// <summary>The product id of Lean in a key's products (ASP-4 kept it).</summary>
    private const string ProductId = "space";

    private Entitlement(bool isPro, string? registeredTo, string? refusalReason)
    {
        IsPro = isPro;
        RegisteredTo = registeredTo;
        RefusalReason = refusalReason;
    }

    /// <summary>Free, with no key.</summary>
    public static Entitlement Free { get; } = new(false, null, null);

    /// <summary>The entitlement of <paramref name="keyText"/>, checked by the Kit's verifier.</summary>
    public static Entitlement FromKey(string keyText) => FromKey(keyText, KitLicenseCheck.Instance);

    /// <summary>The entitlement of <paramref name="keyText"/>, checked by <paramref name="check"/>. Leading and trailing
    /// white space is removed first (docs/spec/06 Windows note); an empty key is Free with no reason.</summary>
    public static Entitlement FromKey(string keyText, ILicenseCheck check)
    {
        ArgumentNullException.ThrowIfNull(keyText);
        ArgumentNullException.ThrowIfNull(check);
        var key = keyText.Trim();
        if (key.Length == 0)
        {
            return Free;
        }

        LicenseCheckResult result;
        try
        {
            result = check.Verify(key);
        }
#pragma warning disable CA1031 // failing closed: a check that throws gives Free, with a reason the UI shows
        catch (Exception)
        {
            return new Entitlement(false, null, Copy.Text.KeyNotVerified);
        }
#pragma warning restore CA1031

        if (result.License is not { } license)
        {
            return new Entitlement(false, null, string.IsNullOrWhiteSpace(result.RefusalReason) ? Copy.Text.KeyNotVerified : result.RefusalReason);
        }

        // Exactly "space": a look-alike ("spaces", "lean", "com.ambysto.space") is another product.
        if (!license.ProductIds.Contains(ProductId) || string.IsNullOrWhiteSpace(license.Licensee))
        {
            return new Entitlement(false, null, Copy.Text.KeyNotForThisApp);
        }

        return new Entitlement(true, Copy.Text.RegisteredTo(license.Licensee), null);
    }

    /// <summary>True for Pro.</summary>
    public bool IsPro { get; }

    /// <summary>"Registered to &lt;licensee&gt;" for Pro; null for Free.</summary>
    public string? RegisteredTo { get; }

    /// <summary>Why a key gave Free: the Kit's reason, or that the key does not include this app; null when no key was
    /// given or the key gave Pro.</summary>
    public string? RefusalReason { get; }
}

/// <summary>The real check: the Ambysto Kit's offline verifier with the signing keys compiled into the release.</summary>
internal sealed class KitLicenseCheck : ILicenseCheck
{
    public static readonly KitLicenseCheck Instance = new();

    private static readonly Ambysto.Kit.Licensing.LicenseVerifier Verifier = new();

    private KitLicenseCheck()
    {
    }

    public LicenseCheckResult Verify(string keyText)
    {
        var result = Verifier.Verify(keyText);
        if (result.License is { } license)
        {
            var products = new HashSet<string>(StringComparer.Ordinal);
            foreach (var product in license.Products)
            {
                products.Add(Ambysto.Kit.Licensing.ProductInfo.Id(product));
            }

            return new LicenseCheckResult(new LicenseFacts(license.Licensee, products), null);
        }

        return new LicenseCheckResult(null, result.Reason is { } reason ? Ambysto.Kit.Licensing.LicenseReasonInfo.Message(reason) : null);
    }
}
