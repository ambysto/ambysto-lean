using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Xml;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

// Acceptance-first ASP-30. Only the frozen UI IDs and observed vendor metadata schema were consulted.
// These tests require the official per-user CapCut installation in the console test session. They never alter its metadata.
[TestClass]
[DoNotParallelize]
public sealed partial class InstallerVersionsTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);
    private const string CapCutKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CapCut";

    private static AutomationElement OpenInstallers(AppSession app)
    {
        var view = Ui.OpenView(app, "AppsAndCaches", "AppsView");
        Ui.Click(AppSession.Find(view, "AppsSectionInstallers", Ui.ElementTimeout));
        AppSession.Find(view, "InstallersList", Ui.ElementTimeout);
        return view;
    }

    // ItemContainer walks logical rows, realizing only the matching item. Offscreen versions need not coexist as peers.
    private static AutomationElement? RowFor(AutomationElement list, string path)
    {
        var visible = Table.LeafRows(list).FirstOrDefault(r => Table.Holds(r.Current.Name, path));
        if (visible is not null) return visible;
        if (!list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern)) return null;
        var container = (ItemContainerPattern)pattern;
        AutomationElement? item = null;
        for (var count = 0; count < 4096; count++)
        {
            item = container.FindItemByProperty(item, null!, null!);
            if (item is null) return null;
            if (!Table.Holds(item.Current.Name, path)) continue;
            if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) ((VirtualizedItemPattern)virtualized).Realize();
            if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
            return item;
        }
        Assert.Fail("installer table exceeded the bounded logical-row traversal");
        return null;
    }

    private static AutomationElement WaitRow(AutomationElement view, string path)
    {
        var list = AppSession.Find(view, "InstallersList", Ui.ElementTimeout);
        var row = AppSession.WaitFor(() => RowFor(list, path), ReadTimeout);
        Assert.IsNotNull(row, "dedicated installer/version row missing for " + path + ": " + Ui.Dump(view));
        return row;
    }

    private static AutomationElement OwnTick(AutomationElement view, CapCutFixture fixture) =>
        AppSession.Find(WaitRow(view, fixture.OldPath), "InstallerItem", Ui.ElementTimeout);

    private static void AssertInitialRows(AutomationElement view, CapCutFixture fixture)
    {
        var row = WaitRow(view, fixture.OldPath);
        StringAssert.Contains(row.Current.Name, fixture.Version);
        StringAssert.Contains(row.Current.Name, ByteUnits.Format(fixture.Bytes), "actual allocated size of the only payload");
        var tick = AppSession.Find(row, "InstallerItem", Ui.ElementTimeout);
        Assert.IsTrue(tick.Current.IsEnabled, "proven unreferenced old version must be actionable, not a guide-only pass");
        Assert.AreEqual(ToggleState.Off, Table.State(tick), "old versions are never preselected");
        Assert.IsFalse(AppSession.Find(view, "MoveInstallers", Ui.ElementTimeout).Current.IsEnabled);
        var current = WaitRow(view, fixture.CurrentPath);
        var currentTick = Table.InRow(current, "InstallerItem");
        Assert.IsTrue(currentTick is null || (!currentTick.Current.IsEnabled && Table.State(currentTick) == ToggleState.Off),
            "the authoritative current version remains visible and cannot be selected");
        Assert.IsTrue(Table.Texts(current).Any(t => t.Name.Contains("current", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("referenc", StringComparison.OrdinalIgnoreCase)), "current row explains retention");
        Assert.IsTrue(Table.Texts(row).Any(t => t.Name.Contains("older", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("superseded", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("referenc", StringComparison.OrdinalIgnoreCase)), "eligible row explains its evidence");
        fixture.AssertPreserved();
    }

    private static AutomationElement ConfirmOnlyOwn(AppSession app, AutomationElement view, CapCutFixture fixture)
    {
        var tick = OwnTick(view, fixture);
        Table.Tick(tick, () => OwnTick(view, fixture), fixture.OldPath);
        var move = AppSession.Find(view, "MoveInstallers", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => move.Current.IsEnabled, Ui.ElementTimeout));
        Ui.Click(move);
        var dialog = Ui.FindAnywhere(app, "ConfirmMove", Ui.ElementTimeout);
        var items = AppSession.FindAll(dialog, "ConfirmItem");
        Assert.HasCount(1, items, "approval must contain only the test-owned old version");
        StringAssert.Contains(items[0].Current.Name, fixture.OldPath);
        StringAssert.Contains(items[0].Current.Name, ByteUnits.Format(fixture.Bytes));
        return dialog;
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void DedicatedVersionListShowsCurrentAndOldEvidenceAndCancelDoesNotMoveAnything()
    {
        ProApp.RequireReleaseApp();
        using var settings = new SettingsBackup();
        using var journal = new JournalBackup();
        JournalBackup.Write([]);
        using var fixture = new CapCutFixture();
        using var app = AppSession.Start();
        var view = OpenInstallers(app);
        AssertInitialRows(view, fixture);
        ConfirmOnlyOwn(app, view, fixture);
        Ui.Click(Ui.FindAnywhere(app, "CancelMove", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("ConfirmMove") is null, Ui.ElementTimeout));
        Assert.IsTrue(Directory.Exists(fixture.OldPath));
        Assert.IsNull(RecycleBin.Holding(fixture.OldPath));
        Assert.AreEqual(0, OwnJournalCount(fixture.OldPath, "trashed"));
        fixture.AssertOldPayload();
        fixture.AssertPreserved();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void ApprovedOldVersionUsesRecycleBinJournalAndHistoryPutBackWhileCurrentAndUserDataStayIntact()
    {
        ProApp.RequireReleaseApp();
        using var settings = new SettingsBackup();
        using var journal = new JournalBackup();
        JournalBackup.Write([]);
        using var fixture = new CapCutFixture();
        var identity = MoveFixture.Identity(fixture.OldPath);
        try
        {
            using var app = AppSession.Start();
            var view = OpenInstallers(app);
            AssertInitialRows(view, fixture);
            ConfirmOnlyOwn(app, view, fixture);
            Ui.Click(Ui.FindAnywhere(app, "ConfirmMoveButton", Ui.ElementTimeout));
            Assert.IsTrue(AppSession.WaitUntil(() => !Directory.Exists(fixture.OldPath)
                && RecycleBin.Holding(fixture.OldPath) is not null && OwnJournalCount(fixture.OldPath, "trashed") == 1, Ui.MoveTimeout),
                "approved old version must be verified in the Recycle Bin and journal");
            Ui.FindAnywhere(app, "MoveReport", Ui.ElementTimeout);
            fixture.AssertPreserved();
            RestoreOwn(app, fixture);
            Assert.AreEqual(identity, MoveFixture.Identity(fixture.OldPath), "Put back preserves the directory identity");
            fixture.AssertOldPayload();
            fixture.AssertPreserved();
        }
        finally
        {
            if (RecycleBin.Holding(fixture.OldPath) is not null)
            {
                using var recovery = AppSession.Start();
                RestoreOwn(recovery, fixture);
            }
        }
    }

    private static void RestoreOwn(AppSession app, CapCutFixture fixture)
    {
        var history = Ui.OpenView(app, "History", "HistoryView");
        var restore = AppSession.WaitFor(() => Table.ControlFor(history, "PutBackEntry", fixture.OldPath), Ui.ElementTimeout);
        Assert.IsNotNull(restore, "own old-version journal entry must expose normal Put back");
        Ui.Click(restore);
        Assert.IsTrue(AppSession.WaitUntil(() => Directory.Exists(fixture.OldPath) && RecycleBin.Holding(fixture.OldPath) is null
            && OwnJournalCount(fixture.OldPath, "restored") == 1, Ui.MoveTimeout));
    }

    private static int OwnJournalCount(string path, string result)
    {
        if (!File.Exists(JournalBackup.Path)) return 0;
        using var stream = new FileStream(JournalBackup.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("originalPath", out var original) && string.Equals(original.GetString(), path, StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("result", out var value) && value.GetString() == result) count++;
        }
        return count;
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("SharedState")]
    public void FreeDeveloperDiagnosisShowsTheRealCacheRootAndSizeOrExplicitUncertaintyWithoutCleaning()
    {
        ProApp.RequireReleaseApp();
        using var settings = new SettingsBackup();
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        // The native create establishes ownership atomically. ERROR_ALREADY_EXISTS grants no cleanup ownership.
        var ownsCache = CreateDirectoryW(cache, nint.Zero);
        if (!ownsCache && Marshal.GetLastPInvokeError() != 183)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "create diagnosis fixture root");
        var path = Path.Combine(cache, "LeanUi-diagnosis-" + Guid.NewGuid().ToString("N") + ".bin");
        var ownsFile = false;
        try
        {
            Assert.AreEqual(0, (int)(File.GetAttributes(cache) & FileAttributes.ReparsePoint), "fixture must not write through a redirected cache root");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsFile = true;
                var bytes = new byte[64 * 1024];
                RandomNumberGenerator.Fill(bytes);
                stream.Write(bytes);
            }
            var hash = MoveFixture.Hash(path);
            var before = File.Exists(JournalBackup.Path) ? File.ReadAllBytes(JournalBackup.Path) : null;
            using (var app = AppSession.Start())
            {
                var view = Ui.OpenView(app, "AppsAndCaches", "AppsView");
                Ui.Click(AppSession.Find(view, "AppsSectionDeveloper", Ui.ElementTimeout));
                var list = AppSession.Find(view, "DeveloperList", Ui.ElementTimeout);
                var row = AppSession.WaitFor(() => RowFor(list, cache), ReadTimeout);
                Assert.IsNotNull(row, "existing known developer store must appear without requiring Pro");
                var name = row.Current.Name;
                StringAssert.Contains(name, cache);
                Assert.IsTrue(Regex.IsMatch(name, @"\d+(?:[.,]\d+)?\s?(?:bytes|KB|MB|GB|TB)\b", RegexOptions.CultureInvariant)
                    || name.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("incomplete", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("not measured", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("unavailable", StringComparison.OrdinalIgnoreCase), "diagnosis must show measured bytes or explicit uncertainty: " + name);
                Assert.IsFalse(Table.Controls(row, "InstallerItem").Any(t => t.Current.IsEnabled), "diagnosis never silently authorizes generic cache deletion");
                Assert.AreEqual(hash, MoveFixture.Hash(path));
                Assert.IsNull(RecycleBin.Holding(path));
            }
            if (before is null) Assert.IsFalse(File.Exists(JournalBackup.Path), "read-only diagnosis cannot create move history");
            else CollectionAssert.AreEqual(before, File.ReadAllBytes(JournalBackup.Path), "read-only diagnosis cannot append move history");
        }
        finally
        {
            if (ownsFile) File.Delete(path);
            if (ownsCache && Directory.Exists(cache) && !Directory.EnumerateFileSystemEntries(cache).Any())
                Directory.Delete(cache, recursive: false);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string path, nint securityAttributes);

    private sealed partial class CapCutFixture : IDisposable
    {
        private readonly List<(string Path, string Hash)> preserved = [];
        private readonly List<string> ownedFiles = [];
        private readonly List<string> ownedDirectories = [];
        private readonly string registryVersion;
        private readonly string payload;
        private readonly string payloadHash;
        public string OldPath { get; }
        public string CurrentPath { get; }
        public string Version { get; }
        public long Bytes { get; }

        public CapCutFixture()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapCut");
            var apps = Path.Combine(root, "Apps");
            var productInfo = Path.Combine(apps, "ProductInfo.xml");
            using var key = Registry.CurrentUser.OpenSubKey(CapCutKey);
            if (key is null || !Directory.Exists(apps) || !File.Exists(productInfo))
                Assert.Inconclusive("prerequisite absent: official per-user CapCut installation with ProductInfo.xml");
            Assert.AreEqual("CapCut", key.GetValue("DisplayName") as string);
            Assert.AreEqual("Bytedance Pte. Ltd.", key.GetValue("Publisher") as string);
            registryVersion = key.GetValue("DisplayVersion") as string ?? string.Empty;
            var xmlSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
            using (var xml = XmlReader.Create(productInfo, xmlSettings))
            {
                var versions = new List<string>();
                while (xml.Read()) if (xml.NodeType == XmlNodeType.Element && xml.Name == "full_appver") versions.Add(xml.GetAttribute("value") ?? string.Empty);
                Assert.HasCount(1, versions, "observed current-version XML schema must remain unambiguous");
                Assert.AreEqual(registryVersion, versions[0], "present conflicting metadata is a failed prerequisite, never a skipped action proof");
            }
            Assert.IsTrue(System.Version.TryParse(registryVersion, out var current) && current.Major > 0, "numeric official current version required");
            CurrentPath = Path.Combine(apps, registryVersion);
            Assert.IsTrue(Directory.Exists(CurrentPath), "registered current version must exist");
            foreach (var directory in new[] { root, apps, CurrentPath })
                Assert.AreEqual(0, (int)(File.GetAttributes(directory) & FileAttributes.ReparsePoint), "no redirected fixture parent");
            Version = "0.0.0." + RandomNumberGenerator.GetInt32(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
            OldPath = Path.Combine(apps, Version);
            payload = Path.Combine(OldPath, "LeanUi-owned-payload.bin");
            payloadHash = string.Empty;
            try
            {
                CreateOwnedDirectory(OldPath);
                WriteOwned(payload, 64 * 1024);
                payloadHash = MoveFixture.Hash(payload);
                Bytes = MoveFixture.AllocatedBytes(payload);
                preserved.Add((productInfo, MoveFixture.Hash(productInfo)));
                var configure = Path.Combine(apps, "Configure.ini");
                if (File.Exists(configure)) preserved.Add((configure, MoveFixture.Hash(configure)));
                var userData = Path.Combine(root, "User Data");
                if (!Directory.Exists(userData)) CreateOwnedDirectory(userData);
                Assert.AreEqual(0, (int)(File.GetAttributes(userData) & FileAttributes.ReparsePoint));
                foreach (var parent in new[] { CurrentPath, userData })
                {
                    var file = Path.Combine(parent, "LeanUi-preserve-" + Guid.NewGuid().ToString("N") + ".bin");
                    WriteOwned(file, 4096);
                    preserved.Add((file, MoveFixture.Hash(file)));
                }
            }
            catch { Dispose(); throw; }
        }

        private void CreateOwnedDirectory(string path)
        {
            if (!CreateDirectoryW(path, nint.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "fixture requires a new directory: " + path);
            ownedDirectories.Add(path);
        }

        private void WriteOwned(string path, int bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownedFiles.Add(path);
            var data = new byte[bytes];
            RandomNumberGenerator.Fill(data);
            stream.Write(data);
        }

        public void AssertOldPayload() => Assert.AreEqual(payloadHash, MoveFixture.Hash(payload));

        public void AssertPreserved()
        {
            foreach (var file in preserved) Assert.AreEqual(file.Hash, MoveFixture.Hash(file.Path), "preserve " + file.Path);
            using var key = Registry.CurrentUser.OpenSubKey(CapCutKey);
            Assert.IsNotNull(key);
            Assert.AreEqual(registryVersion, key.GetValue("DisplayVersion") as string, "no current-version switch");
        }

        public void Dispose()
        {
            // Delete only exact CreateNew files. Never recursively delete any shared vendor/profile directory.
            foreach (var path in ownedFiles) if (File.Exists(path)) File.Delete(path);
            foreach (var path in ownedDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
        }

    }
}
