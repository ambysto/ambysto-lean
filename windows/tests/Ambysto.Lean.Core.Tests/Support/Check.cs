using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Assertions the tests repeat: a few named checks with messages that show both numbers.</summary>
internal static class Check
{
    public static void True(bool condition, string what) => Assert.IsTrue(condition, what);

    public static void False(bool condition, string what) => Assert.IsFalse(condition, what);

    public static void Contains(string text, string part) =>
        Assert.IsTrue(text.Contains(part, StringComparison.Ordinal), $"expected to find \"{part}\" in:\n{text}");

    public static void DoesNotContain(string text, string part) =>
        Assert.IsFalse(text.Contains(part, StringComparison.OrdinalIgnoreCase), $"did not expect \"{part}\" in:\n{text}");

    public static void Near(double expected, double actual, double tolerance, string what) =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance,
            string.Format(CultureInfo.InvariantCulture, "{0}: expected {1} ± {2}, got {3}", what, expected, tolerance, actual));

    public static void AtMost(long bound, long actual, string what) =>
        Assert.IsTrue(actual <= bound, string.Format(CultureInfo.InvariantCulture, "{0}: expected at most {1}, got {2}", what, bound, actual));

    public static void AtLeast(long bound, long actual, string what) =>
        Assert.IsTrue(actual >= bound, string.Format(CultureInfo.InvariantCulture, "{0}: expected at least {1}, got {2}", what, bound, actual));

    /// <summary>The names in <paramref name="actual"/> are exactly <paramref name="expected"/>, in this order.</summary>
    public static void Sequence(IEnumerable<string> actual, params string[] expected)
    {
        var got = actual.ToArray();
        CollectionAssert.AreEqual(expected, got, $"expected [{string.Join(", ", expected)}], got [{string.Join(", ", got)}]");
    }

    /// <summary>The names in <paramref name="actual"/> are exactly <paramref name="expected"/>, in any order.</summary>
    public static void Set(IEnumerable<string> actual, params string[] expected)
    {
        var got = actual.ToArray();
        CollectionAssert.AreEquivalent(expected, got, $"expected {{{string.Join(", ", expected)}}}, got {{{string.Join(", ", got)}}}");
    }

    /// <summary>The value of the public constant <paramref name="name"/> of <paramref name="type"/>, read at run time (so an
    /// assertion on it is not a comparison of two literals).</summary>
    public static T Constant<T>(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(field, type.Name + " has no public static field " + name);
        return (T)field.GetValue(null)!;
    }
}
