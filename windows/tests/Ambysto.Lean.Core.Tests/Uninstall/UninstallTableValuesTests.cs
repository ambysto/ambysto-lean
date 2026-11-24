using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Tests.Support;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>ASP-16 (intent/2026-10-20-asp-16-uninstall-table.md, "Core contract"): the three values the Uninstall table shows, read by the
/// real source from real scratch keys under <c>HKCU\…\Uninstall\AmbystoLeanTest-*</c> (removed afterwards, also when a test fails), and
/// the conveniences of <see cref="InstalledApp"/>. <c>EstimatedSizeBytes</c> is the <c>EstimatedSize</c> DWORD (KB) × 1024, null when
/// missing, not a DWORD or 0; <c>InstallDate</c> is exactly 8 digits <c>yyyyMMdd</c>, null when invalid, before 1980 or more than 1 day
/// after today; <c>LastWritten</c> is the key's last write time (UTC) from <c>RegQueryInfoKey</c> on the open key. A value that cannot be
/// read leaves its property null, never the entry out. Not in parallel: the scratch entries are visible to every real inventory read
/// while they exist.</summary>
[TestClass]
// Writes the real HKCU (scratch keys, or the Uninstall key's ACL): kept out of parallel mutation runs, which can stop a test between its
// change and its clean-up (2026-10-18: a stopped run left HKCU\...\Uninstall unreadable).
[TestCategory("SharedState")]
[DoNotParallelize]
public sealed class UninstallTableValuesTests
{
    private static UninstallEntry ReadScratch(string name) =>
        new RegistryUninstallSource().ReadEntries().Single(e => e.Scope == UninstallScope.PerUser && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));

    // ---- EstimatedSize ---------------------------------------------------------------------------------------------------------

    // "EstimatedSizeBytes (long?): the entry's EstimatedSize DWORD (in KB) × 1024. null when the value is missing or not a DWORD; null when
    // the DWORD is 0 (Settings shows no size for it)." The DWORD is unsigned: 0xFFFFFFFF KB is 4398046510080 bytes, not a negative number.
    [TestMethod]
    [DataRow("DWORD 2048", 2048, RegistryValueKind.DWord, 2_097_152L)]
    [DataRow("DWORD 1", 1, RegistryValueKind.DWord, 1_024L)]
    [DataRow("DWORD 2147483647 (a long product, no overflow)", int.MaxValue, RegistryValueKind.DWord, 2_199_023_254_528L)]
    [DataRow("DWORD 0xFFFFFFFF (unsigned)", -1, RegistryValueKind.DWord, 4_398_046_510_080L)]
    [DataRow("DWORD 0", 0, RegistryValueKind.DWord, null)]
    [DataRow("missing", null, RegistryValueKind.DWord, null)]
    [DataRow("string \"2048\"", "2048", RegistryValueKind.String, null)]
    [DataRow("expand string \"2048\"", "2048", RegistryValueKind.ExpandString, null)]
    [DataRow("QWORD 2048", 2048L, RegistryValueKind.QWord, null)]
    [DataRow("string \"0\"", "0", RegistryValueKind.String, null)]
    public void EstimatedSizeIsTheDwordInKbTimes1024(string row, object? value, RegistryValueKind kind, long? expected)
    {
        using var scratch = new ScratchUninstallKeys();
        var name = scratch.MakeWith("Ambysto Lean Size " + row, "EstimatedSize", value, kind);
        var entry = ReadScratch(name);
        Assert.AreEqual(expected, entry.EstimatedSizeBytes, "EstimatedSizeBytes for " + row);
        Assert.AreEqual("Ambysto Lean Size " + row, entry.DisplayName, "the entry is listed with its other values: " + row);
    }

    // ---- InstallDate -----------------------------------------------------------------------------------------------------------

    // "InstallDate (DateOnly?): exactly 8 digits, yyyyMMdd, parsed with the invariant culture; otherwise null. Also null if the date is
    // invalid (20261332), before 1980, or more than 1 day after today (an installer's wrong clock)."
    [TestMethod]
    [DataRow("valid", "20240315", "2024-03-15")]
    [DataRow("leap day", "20240229", "2024-02-29")]
    [DataRow("the first day of 1980", "19800101", "1980-01-01")]
    [DataRow("the last day of 1979", "19791231", null)]
    [DataRow("year 0000", "00000101", null)]
    [DataRow("far future", "99991231", null)]
    [DataRow("7 digits", "2024031", null)]
    [DataRow("9 digits", "202403150", null)]
    [DataRow("letters among digits", "2024O315", null)]
    [DataRow("all letters", "abcdefgh", null)]
    [DataRow("invalid month and day", "20261332", null)]
    [DataRow("no 30 February", "20240230", null)]
    [DataRow("29 February of a common year", "20230229", null)]
    [DataRow("empty", "", null)]
    [DataRow("ISO form", "2024-03-15", null)]
    [DataRow("leading blank", " 20240315", null)]
    [DataRow("trailing blank", "20240315 ", null)]
    [DataRow("non-ASCII digits", "\u0662\u0660\u0662\u0664\u0660\u0663\u0661\u0665", null)]
    public void InstallDateIsEightDigitsAndAPlausibleDate(string row, string text, string? expected)
    {
        using var scratch = new ScratchUninstallKeys();
        var name = scratch.MakeWith("Ambysto Lean Date " + row, "InstallDate", text, RegistryValueKind.String);
        var entry = ReadScratch(name);
        Assert.AreEqual(expected, entry.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"InstallDate for {row} (\"{text}\")");
        Assert.AreEqual("Ambysto Lean Date " + row, entry.DisplayName, "the entry is listed with its other values: " + row);
    }

    [TestMethod]
    public void InstallDateIsNullWhenTheValueIsMissing()
    {
        using var scratch = new ScratchUninstallKeys();
        var entry = ReadScratch(scratch.MakeWith("Ambysto Lean Date missing", "InstallDate", null, RegistryValueKind.String));
        Assert.IsNull(entry.InstallDate);
    }

    // The boundary with the clock: more than 1 day after today is an installer's wrong clock. "Today" may be the local or the UTC date, so
    // the values are chosen to hold for either: a date up to the earlier of the two plus 1 is kept; a date more than the later of the two
    // plus 1 (and the contract's "tomorrow + 2", which is the later plus 3) is null.
    [TestMethod]
    public void InstallDateOfTheFutureIsKeptUpToTomorrowAndNullBeyond()
    {
        var local = DateOnly.FromDateTime(DateTime.Now);
        var utc = DateOnly.FromDateTime(DateTime.UtcNow);
        var earlier = local < utc ? local : utc;
        var later = local < utc ? utc : local;
        var kept = new[] { earlier.AddDays(-1), earlier, later, earlier.AddDays(1) };
        var refused = new[] { later.AddDays(2), later.AddDays(3), later.AddYears(1) };
        using var scratch = new ScratchUninstallKeys();
        var made = kept.Concat(refused).Select(d => (Date: d, Name: scratch.MakeWith("Ambysto Lean Date " + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), "InstallDate", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String))).ToList();
        var entries = new RegistryUninstallSource().ReadEntries();
        foreach (var (date, name) in made)
        {
            var entry = entries.Single(e => e.Scope == UninstallScope.PerUser && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));
            var expected = kept.Contains(date) ? date : (DateOnly?)null;
            Assert.AreEqual(expected, entry.InstallDate, $"InstallDate {date:yyyyMMdd} (local today {local:yyyyMMdd}, UTC today {utc:yyyyMMdd})");
        }
    }

    // ---- LastWritten -----------------------------------------------------------------------------------------------------------

    // "LastWritten (DateTimeOffset?, UTC): the key's last write time from RegQueryInfoKey, on the open key's handle." A fresh key reads
    // within a minute of now, in UTC.
    [TestMethod]
    public void LastWrittenOfAFreshKeyIsWithinAMinuteOfNowInUtc()
    {
        using var scratch = new ScratchUninstallKeys();
        var before = DateTimeOffset.UtcNow;
        var name = scratch.MakeWith("Ambysto Lean LastWritten fresh", "Publisher", "Ambysto", RegistryValueKind.String);
        var after = DateTimeOffset.UtcNow;
        var written = ReadScratch(name).LastWritten;
        Assert.IsNotNull(written, "LastWritten of a fresh key");
        Assert.AreEqual(TimeSpan.Zero, written.Value.Offset, "LastWritten is in UTC");
        Check.True(written.Value >= before - TimeSpan.FromMinutes(1) && written.Value <= after + TimeSpan.FromMinutes(1),
            $"LastWritten {written:O} is not within a minute of the key being made ({before:O} .. {after:O})");
    }

    // It is the time of this key, read from this key: a key written 1.5 s later reads later, and writing a value into a key moves its own time.
    [TestMethod]
    public void LastWrittenIsThatOfEachKeyAndMovesWhenTheKeyIsWritten()
    {
        using var scratch = new ScratchUninstallKeys();
        var first = scratch.MakeWith("Ambysto Lean LastWritten first", "Publisher", "Ambysto", RegistryValueKind.String);
        var firstWritten = ReadScratch(first).LastWritten;
        Thread.Sleep(1500);
        var second = scratch.MakeWith("Ambysto Lean LastWritten second", "Publisher", "Ambysto", RegistryValueKind.String);
        var secondWritten = ReadScratch(second).LastWritten;
        Assert.IsNotNull(firstWritten);
        Assert.IsNotNull(secondWritten);
        Check.True(secondWritten.Value - firstWritten.Value >= TimeSpan.FromSeconds(1), $"the second key ({secondWritten:O}) is not at least 1 s after the first ({firstWritten:O})");

        Thread.Sleep(1500);
        ScratchUninstallKeys.SetValue(first, "DisplayVersion", "2.0");
        var rewritten = ReadScratch(first).LastWritten;
        Assert.IsNotNull(rewritten);
        Check.True(rewritten.Value - firstWritten.Value >= TimeSpan.FromSeconds(1), $"writing a value did not move LastWritten ({firstWritten:O} -> {rewritten:O})");
        Assert.AreEqual(secondWritten, ReadScratch(second).LastWritten, "writing the first key moved the second key's LastWritten");
    }

    // ---- a value that cannot be read -----------------------------------------------------------------------------------------

    // "A value that cannot be read leaves that property null, never the entry out."
    [TestMethod]
    public void ABadValueLeavesOnlyItsPropertyNullAndTheEntryListed()
    {
        using var scratch = new ScratchUninstallKeys();
        var name = scratch.Make(key =>
        {
            key.SetValue("DisplayName", "Ambysto Lean Bad Values");
            key.SetValue("Publisher", "Ambysto Test Publisher");
            key.SetValue("DisplayVersion", "1.2.3");
            key.SetValue("EstimatedSize", "a lot", RegistryValueKind.String);
            key.SetValue("InstallDate", "yesterday", RegistryValueKind.String);
        });
        var entry = ReadScratch(name);
        Assert.AreEqual("Ambysto Lean Bad Values", entry.DisplayName);
        Assert.AreEqual("Ambysto Test Publisher", entry.Publisher);
        Assert.AreEqual("1.2.3", entry.DisplayVersion);
        Assert.IsNull(entry.EstimatedSizeBytes);
        Assert.IsNull(entry.InstallDate);
        Assert.IsNotNull(entry.LastWritten, "the key itself could be read");
        var app = UninstallInventory.Read(new RealOnlySource()).Single(a => a.Entry is { } e && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(app.SizeBytes);
        Assert.IsNull(app.InstalledOn);
        Assert.IsNotNull(app.LastUpdated);
    }

    // All three together with the other values, through the real source and the inventory.
    [TestMethod]
    public void AFullEntryCarriesItsThreeValuesIntoTheInventory()
    {
        using var scratch = new ScratchUninstallKeys();
        var name = scratch.Make(key =>
        {
            key.SetValue("DisplayName", "Ambysto Lean Full Entry");
            key.SetValue("Publisher", "Ambysto Test Publisher");
            key.SetValue("DisplayVersion", "9.8.7");
            key.SetValue("EstimatedSize", 5120, RegistryValueKind.DWord);
            key.SetValue("InstallDate", "20240315", RegistryValueKind.String);
        });
        var entry = ReadScratch(name);
        Assert.AreEqual(5_242_880L, entry.EstimatedSizeBytes);
        Assert.AreEqual(new DateOnly(2024, 3, 15), entry.InstallDate);
        Assert.IsNotNull(entry.LastWritten);
        var app = UninstallInventory.Read(new RealOnlySource()).Single(a => a.Entry is { } e && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(5_242_880L, app.SizeBytes);
        Assert.AreEqual(new DateOnly(2024, 3, 15), app.InstalledOn);
        Assert.AreEqual(entry.LastWritten, app.LastUpdated);
        Assert.AreEqual(AppClass.PerUser, app.Class);
    }

    // "an entry that cannot be read is still listed hidden, as before": the key is reported by its name, hidden, with no values, and the new
    // properties are null too; the inventory does not list it, the owners list does.
    [TestMethod]
    public void AnEntryThatCannotBeReadIsStillListedHiddenWithNoValues()
    {
        using var scratch = new ScratchUninstallKeys();
        var name = scratch.Make(key =>
        {
            key.SetValue("DisplayName", "Ambysto Lean Unreadable " + Guid.NewGuid().ToString("N"));
            key.SetValue("EstimatedSize", 1024, RegistryValueKind.DWord);
            key.SetValue("InstallDate", "20240315", RegistryValueKind.String);
        });
        ScratchUninstallKeys.DenyRead(name);
        using (var denied = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            Assert.Throws<System.Security.SecurityException>(() => denied!.OpenSubKey(name), "fixture: this user may not read the key");
        }

        var source = new RegistryUninstallSource();
        var entry = ReadScratch(name);
        Assert.IsTrue(entry.SystemComponent, "an unreadable key is reported hidden");
        Assert.IsNull(entry.DisplayName);
        Assert.IsNull(entry.Publisher);
        Assert.IsNull(entry.DisplayVersion);
        Assert.IsNull(entry.InstallLocation);
        Assert.IsNull(entry.EstimatedSizeBytes, "no value was read");
        Assert.IsNull(entry.InstallDate, "no value was read");
        Assert.IsNull(entry.LastWritten, "the key could not be opened, so its time is not read");
        Assert.IsFalse(UninstallInventory.Read(source).Any(a => a.Entry is { } e && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase)), "the unreadable key is listed to the user");
        var owner = UninstallInventory.ReadOwners(source).SingleOrDefault(a => a.Entry is { } e && string.Equals(e.KeyName, name, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(owner, "the unreadable key is still an owner (hidden)");
        Assert.IsNull(owner.SizeBytes);
        Assert.IsNull(owner.InstalledOn);
        Assert.IsNull(owner.LastUpdated);
    }

    /// <summary>The real entries only (no Store packages, no processes), so the inventory of a scratch key is read as the app reads it.</summary>
    private sealed class RealOnlySource : IUninstallSource
    {
        private readonly RegistryUninstallSource real = new();

        public System.Collections.Generic.IReadOnlyList<UninstallEntry> ReadEntries() => real.ReadEntries();

        public System.Collections.Generic.IReadOnlyList<StorePackage> ReadStorePackages() => [];

        public System.Collections.Generic.IReadOnlyList<string> ReadProcessImagePaths() => [];
    }

    // ---- InstalledApp conveniences (a fake source: no registry) ------------------------------------------------------------

    private static UninstallEntry EntryWith(string key, long? size, DateOnly? date, DateTimeOffset? written) =>
        new(key, UninstallScope.PerUser, RegistryViewKind.Bits64, "App " + key, "Contoso", "1.0", @"C:\Users\me\AppData\Local\Programs\" + key, false)
        {
            EstimatedSizeBytes = size,
            InstallDate = date,
            LastWritten = written,
        };

    // "InstalledApp gains three read-only conveniences: SizeBytes is the entry's EstimatedSizeBytes; InstalledOn is the entry's InstallDate;
    // LastUpdated is the entry's LastWritten."
    [TestMethod]
    public void ARegisteredAppTakesItsSizeAndItsDatesFromItsEntry()
    {
        var written = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        var source = new FakeUninstallSource();
        source.Entries.Add(EntryWith("full", 2_097_152L, new DateOnly(2024, 3, 15), written));
        source.Entries.Add(EntryWith("bare", null, null, null));
        var apps = UninstallInventory.Read(source).ToDictionary(a => a.DisplayName, StringComparer.Ordinal);
        Assert.AreEqual(2_097_152L, apps["App full"].SizeBytes);
        Assert.AreEqual(new DateOnly(2024, 3, 15), apps["App full"].InstalledOn);
        Assert.AreEqual(written, apps["App full"].LastUpdated);
        Assert.IsNull(apps["App bare"].SizeBytes);
        Assert.IsNull(apps["App bare"].InstalledOn);
        Assert.IsNull(apps["App bare"].LastUpdated);
    }

    // The same values are on the apps of the owners list, hidden ones included.
    [TestMethod]
    public void TheOwnersListCarriesTheSameValues()
    {
        var written = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        var source = new FakeUninstallSource();
        source.Entries.Add(EntryWith("shown", 4096L, new DateOnly(2025, 1, 2), written));
        source.Entries.Add(EntryWith("hidden", 8192L, new DateOnly(2025, 2, 3), written) with { SystemComponent = true });
        var owners = UninstallInventory.ReadOwners(source).ToDictionary(a => a.DisplayName, StringComparer.Ordinal);
        Assert.AreEqual(4096L, owners["App shown"].SizeBytes);
        Assert.AreEqual(8192L, owners["App hidden"].SizeBytes);
        Assert.AreEqual(new DateOnly(2025, 2, 3), owners["App hidden"].InstalledOn);
        Assert.AreEqual(written, owners["App hidden"].LastUpdated);
    }

    // "SizeBytes … is null for a Store app: Lean does not measure package folders. InstalledOn is … the package's InstalledDate (as a date in
    // local time). LastUpdated … is null for a Store app."
    [TestMethod]
    public void AStoreAppHasItsInstalledDateAsALocalDateAndNeitherSizeNorLastUpdated()
    {
        // Built from a local time of day, so that the expected date does not depend on this machine's time zone.
        var localLate = new DateTimeOffset(new DateTime(2024, 3, 15, 23, 30, 0, DateTimeKind.Local));
        var localEarly = new DateTimeOffset(new DateTime(2024, 3, 16, 0, 20, 0, DateTimeKind.Local));
        // An instant given in UTC: its date in local time is what the table shows.
        var utcInstant = new DateTimeOffset(2024, 6, 1, 23, 45, 0, TimeSpan.Zero);
        var source = new FakeUninstallSource();
        source.StorePackages.Add(new StorePackage("Late_1.0_x64__abc", "Store Late", "Contoso", @"C:\Program Files\WindowsApps\Late") { InstalledDate = localLate });
        source.StorePackages.Add(new StorePackage("Early_1.0_x64__abc", "Store Early", "Contoso", @"C:\Program Files\WindowsApps\Early") { InstalledDate = localEarly });
        source.StorePackages.Add(new StorePackage("Utc_1.0_x64__abc", "Store Utc", "Contoso", @"C:\Program Files\WindowsApps\Utc") { InstalledDate = utcInstant });
        source.StorePackages.Add(new StorePackage("None_1.0_x64__abc", "Store None", "Contoso", @"C:\Program Files\WindowsApps\None"));
        var apps = UninstallInventory.Read(source).ToDictionary(a => a.DisplayName, StringComparer.Ordinal);
        Assert.AreEqual(new DateOnly(2024, 3, 15), apps["Store Late"].InstalledOn, "23:30 local is still the 15th");
        Assert.AreEqual(new DateOnly(2024, 3, 16), apps["Store Early"].InstalledOn, "00:20 local is already the 16th");
        Assert.AreEqual(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcInstant, TimeZoneInfo.Local).DateTime), apps["Store Utc"].InstalledOn, "a UTC instant is shown as its date in local time");
        Assert.IsNull(apps["Store None"].InstalledOn, "no InstalledDate");
        foreach (var name in new[] { "Store Late", "Store Early", "Store Utc", "Store None" })
        {
            Assert.AreEqual(AppClass.StoreApp, apps[name].Class);
            Assert.IsNull(apps[name].SizeBytes, name + ": Lean does not measure package folders");
            Assert.IsNull(apps[name].LastUpdated, name + ": a Store app has no last-updated time");
        }
    }

    // "The positional constructor is unchanged, so existing fakes still build": the three properties are null unless set.
    [TestMethod]
    public void AnEntryBuiltTheOldWayHasNoSizeNoInstallDateAndNoLastWritten()
    {
        var entry = FakeUninstallSource.Entry("{A}", "Contoso Notes", @"C:\Users\me\AppData\Local\Programs\Notes");
        Assert.IsNull(entry.EstimatedSizeBytes);
        Assert.IsNull(entry.InstallDate);
        Assert.IsNull(entry.LastWritten);
        var package = new StorePackage("A_1.0_x64__abc", "A", null, @"C:\Program Files\WindowsApps\A");
        Assert.IsNull(package.InstalledDate);
    }

    // ---- Store package dates (review round 1, MINOR 2) -----------------------------------------------------------------------

    // "StorePackage.InstalledDate … is null if reading it throws, or if the date is before 1980 or more than 1 day after now, the same
    // plausibility rule as InstallDate." There is NO SEAM: ReadStorePackages reads Windows.Management.Deployment.Package, a WinRT class a
    // test cannot fake, and the rule sits in a private method taking a Package. So the rule is tested through a helper the builder is asked
    // to expose (agreed signature, found by name):
    //     internal static DateTimeOffset? RegistryUninstallSource.PlausibleInstalledDate(DateTimeOffset? value, DateTimeOffset now)
    // returning value when 1980-01-01 <= value <= now + 1 day, else null (also for null in). It fails until the helper exists.
    private static DateTimeOffset? Plausible(DateTimeOffset? value, DateTimeOffset now)
    {
        var method = typeof(RegistryUninstallSource).GetMethod("PlausibleInstalledDate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            [typeof(DateTimeOffset?), typeof(DateTimeOffset)]);
        Assert.IsNotNull(method, "surface gap: RegistryUninstallSource has no static PlausibleInstalledDate(DateTimeOffset? value, DateTimeOffset now) returning DateTimeOffset? (review round 1, MINOR 2: expose the plausibility rule so that it can be tested; Package cannot be faked)");
        return (DateTimeOffset?)method.Invoke(null, [value, now]);
    }

    [TestMethod]
    public void AStorePackageDateIsKeptOnlyBetween1980AndTomorrow()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var kept = new DateTimeOffset?[]
        {
            new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 3, 15, 23, 30, 0, TimeSpan.Zero),
            now,
            now.AddHours(23),
            now.AddDays(1),
        };
        var refused = new DateTimeOffset?[]
        {
            null,
            new DateTimeOffset(1979, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), // a zero FILETIME
            DateTimeOffset.MinValue,
            now.AddDays(1).AddSeconds(1),
            now.AddDays(2),
            now.AddYears(30),
            DateTimeOffset.MaxValue,
        };
        foreach (var value in kept)
        {
            Assert.AreEqual(value, Plausible(value, now), $"{value:O} is plausible and must be kept");
        }

        foreach (var value in refused)
        {
            Assert.IsNull(Plausible(value, now), $"{value:O} is not plausible and must be null");
        }
    }

    // What the real source gives for this user's packages is plausible, whatever the helper's name (a control for the real read: it passes
    // today unless a package on this machine has an odd date, and fails if the rule is applied wrongly).
    [TestMethod]
    public void EveryRealStorePackageDateIsNullOrPlausible()
    {
        System.Collections.Generic.IReadOnlyList<StorePackage> packages;
        try
        {
            packages = new RegistryUninstallSource().ReadStorePackages();
        }
        catch (System.IO.IOException e)
        {
            Assert.Inconclusive("the Store packages cannot be read here: " + e.Message);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var package in packages.Where(p => p.InstalledDate is not null))
        {
            var date = package.InstalledDate!.Value;
            Check.True(date >= new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) && date <= now.AddDays(1), $"{package.FullName} has the implausible InstalledDate {date:O}");
        }
    }

    // ---- words -------------------------------------------------------------------------------------------------------------------

    // "New texts in Text: ColumnPublisher "Publisher", ColumnVersion "Version", ColumnInstalledOn "Installed on", ColumnLastUpdated "Last updated"."
    [TestMethod]
    public void TheNewColumnHeadersAreTheContractsWords()
    {
        Assert.AreEqual("Publisher", Check.Constant<string>(typeof(Text), "ColumnPublisher"));
        Assert.AreEqual("Version", Check.Constant<string>(typeof(Text), "ColumnVersion"));
        Assert.AreEqual("Installed on", Check.Constant<string>(typeof(Text), "ColumnInstalledOn"));
        Assert.AreEqual("Last updated", Check.Constant<string>(typeof(Text), "ColumnLastUpdated"));
    }
}
