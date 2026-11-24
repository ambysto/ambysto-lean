using System;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Pro;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Pro;

/// <summary>docs/spec/06 "License": Pro is unlocked only by a key that verifies offline and whose products contain
/// <c>space</c>; release builds have no other way; "Registered to &lt;licensee&gt;". Intent criterion 8: a space key
/// unlocks Pro offline; any other product does not. Plan build note (2): the key reaches Entitlement through a check
/// seam; the real verifier is tested on what it must refuse.</summary>
[TestClass]
public sealed class EntitlementTests
{
    private const string SpaceKey = "KEY-SPACE";
    private const string OptimerKey = "KEY-OPTIMER";
    private const string BothKey = "KEY-BOTH";
    private const string BadKey = "KEY-BAD";

    private static FakeLicenseCheck Checker() => new FakeLicenseCheck()
        .Accepting(SpaceKey, "Alice Example", "space")
        .Accepting(OptimerKey, "Bob Example", "optimer")
        .Accepting(BothKey, "Carol Example", "optimer", "space")
        .Refusing(BadKey, "The signature does not match.");

    // docs/spec/06 License: a key whose products list contains space gives Pro and "Registered to <licensee>".
    [TestMethod]
    public void SpaceKeyGivesProAndRegisteredTo()
    {
        var entitlement = Entitlement.FromKey(SpaceKey, Checker());
        Assert.IsTrue(entitlement.IsPro);
        Assert.AreEqual("Registered to Alice Example", entitlement.RegisteredTo);
        Assert.AreEqual(Text.RegisteredTo("Alice Example"), entitlement.RegisteredTo);
        Assert.IsNull(entitlement.RefusalReason);
    }

    // docs/spec/06 License: a key for other products only does not unlock Pro (criterion 8: any other product does not).
    [TestMethod]
    public void KeyForAnotherProductOnlyIsFreeWithAReason()
    {
        var entitlement = Entitlement.FromKey(OptimerKey, Checker());
        Assert.IsFalse(entitlement.IsPro);
        Assert.IsNull(entitlement.RegisteredTo);
        Check.True(!string.IsNullOrWhiteSpace(entitlement.RefusalReason), "a reason says the key does not include this app");
    }

    // docs/spec/06 License: products contain space among others: Pro.
    [TestMethod]
    public void KeyForSeveralProductsIncludingSpaceGivesPro()
    {
        var entitlement = Entitlement.FromKey(BothKey, Checker());
        Assert.IsTrue(entitlement.IsPro);
        Assert.AreEqual("Registered to Carol Example", entitlement.RegisteredTo);
    }

    // docs/spec/06 License and docs/spec/07 Windows note: a refused key is Free, with the Kit's refusal words.
    [TestMethod]
    public void RefusedKeyIsFreeWithTheKitsReason()
    {
        var entitlement = Entitlement.FromKey(BadKey, Checker());
        Assert.IsFalse(entitlement.IsPro);
        Assert.IsNull(entitlement.RegisteredTo);
        Assert.AreEqual("The signature does not match.", entitlement.RefusalReason);
    }

    // docs/spec/06 Feature split: Free is the state without a key: no Pro, no registration, no refusal.
    [TestMethod]
    public void FreeHasNoKeyNoRegistrationAndNoReason()
    {
        var free = Entitlement.Free;
        Assert.IsFalse(free.IsPro);
        Assert.IsNull(free.RegisteredTo);
        Assert.IsNull(free.RefusalReason);
    }

    // docs/spec/06 License: the product id is exactly "space" (ASP-4 kept the product id); a look-alike does not count.
    [TestMethod]
    public void ProductIdMustBeExactlySpace()
    {
        var check = new FakeLicenseCheck().Accepting("K1", "Dan", "spaces").Accepting("K2", "Dan", "lean").Accepting("K3", "Dan", "com.ambysto.space");
        Assert.IsFalse(Entitlement.FromKey("K1", check).IsPro);
        Assert.IsFalse(Entitlement.FromKey("K2", check).IsPro);
        Assert.IsFalse(Entitlement.FromKey("K3", check).IsPro);
    }

    // docs/spec/06 Windows note: leading and trailing white space (a newline copied with it) is removed before the check.
    [TestMethod]
    public void KeyTextIsTrimmedBeforeTheCheck()
    {
        var check = Checker();
        var entitlement = Entitlement.FromKey("  " + SpaceKey + "\r\n", check);
        Assert.IsTrue(entitlement.IsPro, "the pasted key with a newline still unlocks Pro");
        CollectionAssert.Contains(check.Asked, SpaceKey);
        CollectionAssert.DoesNotContain(check.Asked, "  " + SpaceKey + "\r\n");
    }

    // docs/spec/06 Windows note: only the ends are trimmed; white space inside the key stays as pasted.
    [TestMethod]
    public void InnerWhiteSpaceOfAKeyIsKept()
    {
        const string key = "KEY WITH SPACE";
        var check = new FakeLicenseCheck().Accepting(key, "Eve Example", "space");
        Assert.IsTrue(Entitlement.FromKey(" " + key + " ", check).IsPro);
        CollectionAssert.Contains(check.Asked, key);
    }

    // docs/spec/06 License: release builds have no other way to unlock Pro: the real verifier refuses what is not a
    // signed key (the Kit's test vectors are signed with a test key, so they must not give Pro either).
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a key")]
    [DataRow("AMBYSTO-SPACE-PRO-0000-0000-0000")]
    [DataRow("eyJwcm9kdWN0cyI6WyJzcGFjZSJdLCJsaWNlbnNlZSI6IkV2ZSJ9.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("{\"products\":[\"space\"],\"licensee\":\"Eve\",\"signature\":\"\"}")]
    public void RealVerifierRefusesUnsignedOrMalformedKeys(string keyText)
    {
        var entitlement = Entitlement.FromKey(keyText);
        Assert.IsFalse(entitlement.IsPro, "an unsigned or malformed key must never give Pro");
        Assert.IsNull(entitlement.RegisteredTo);
    }

    // docs/spec/06 License: a malformed key gives Free with a reason, never an exception the UI would have to catch.
    [TestMethod]
    public void RealVerifierGivesAReasonForAMalformedKey()
    {
        var entitlement = Entitlement.FromKey("not a key");
        Assert.IsFalse(entitlement.IsPro);
        Check.True(!string.IsNullOrWhiteSpace(entitlement.RefusalReason), "a refusal reason is shown in Settings");
    }

    // Gap brief H08. docs/spec/06 License: "Pro is unlocked only by a license key that verifies offline"; "Release builds
    // have no other way to unlock Pro". A verifier that fails has not verified the key: Free, never Pro, and Settings says
    // the key could not be verified. The spec names no text for this case; Text.KeyNotVerified is the conservative reading
    // (the plan group 8 UI contract shows a refusal reason after a refused key), flagged for the main session.
    [TestMethod]
    [DataRow("InvalidOperationException")]
    [DataRow("FormatException")]
    public void AVerifierThatThrowsGivesFreeWithKeyNotVerified(string kind)
    {
        Exception failure = kind == "FormatException" ? new FormatException("bad key (fake)") : new InvalidOperationException("verifier failed (fake)");
        var check = new ThrowingLicenseCheck(failure);
        // A failure that reached the caller fails the test with that exception (the UI would have to catch it).
        var entitlement = Entitlement.FromKey("any key", check);
        Assert.AreEqual(1, check.Calls, "the key was given to the check");
        Assert.IsFalse(entitlement.IsPro, "a key the verifier could not check never gives Pro");
        Assert.IsNull(entitlement.RegisteredTo, "no \"Registered to\" without a verified key");
        Assert.AreEqual(Text.KeyNotVerified, entitlement.RefusalReason);
    }

    // Gap brief H09. docs/spec/06 and plan group 8 UI contract: LicenseStatus shows "the refusal reason after a refused
    // key"; a check that refuses without a reason (null or blank) still gives Free with a reason, never null or blank.
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void ARefusalWithoutAReasonSaysKeyNotVerified(string? reason)
    {
        var entitlement = Entitlement.FromKey("KEY-NO-REASON", new AnsweringLicenseCheck(new LicenseCheckResult(null, reason)));
        Assert.IsFalse(entitlement.IsPro);
        Assert.IsNull(entitlement.RegisteredTo);
        Check.True(!string.IsNullOrWhiteSpace(entitlement.RefusalReason), "a refused key always says why");
        Assert.AreEqual(Text.KeyNotVerified, entitlement.RefusalReason);
    }
}
