using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests;

/// <summary>Plan group 8, "UI contract for group 8": the Settings and About views, the license status in Free, a refused
/// key, the Pro-only toggles and export disabled with their quiet "Pro" label, no purchase reminder, and the user's Run
/// key left alone (docs/spec/06). Phase A has no way to Pro in a release build, so only Free is seen here. Nothing is
/// ever invoked that opens File Explorer or Windows Settings, toggles a Pro control or writes the registry: the Run key
/// is only read.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SettingsAndAboutTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Ambysto Lean";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a test watches for an element that must never appear.</summary>
    private static readonly TimeSpan NeverWatch = TimeSpan.FromSeconds(3);

    /// <summary>A small folder to scan, so that "after a scan" has a finished scan.</summary>
    private static Fixture SmallTree()
    {
        var fixture = new Fixture("ui-settings");
        fixture.File(@"alpha\one.bin", 300 * ByteUnits.KB);
        fixture.File(@"beta\two.bin", 100 * ByteUnits.KB);
        fixture.File("gamma.bin", 50 * ByteUnits.KB);
        return fixture;
    }

    /// <summary>Clicks a button-like element: Invoke, or Select for a navigation item. Never used on a Pro control, on
    /// OpenRecycleBin, Reveal or Move.</summary>
    private static void Click(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
        }
        else
        {
            Assert.Fail($"{element.Current.AutomationId} can be neither invoked nor selected");
        }
    }

    /// <summary>The element with <paramref name="automationId"/> in any window of the app (a view may be its own window
    /// or a dialog), within <paramref name="timeout"/>.</summary>
    private static AutomationElement FindAnywhere(AppSession app, string automationId, TimeSpan timeout)
    {
        var element = AppSession.WaitFor(() => app.FindInAnyWindow(automationId), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} in any window of the app within {timeout.TotalSeconds:F0} s");
        return element;
    }

    /// <summary>Clicks the sidebar's Settings item (ASP-14) and returns the Settings view.</summary>
    private static AutomationElement OpenSettings(AppSession app)
    {
        Click(Ui.SidebarItem(app, "Settings"));
        return FindAnywhere(app, "SettingsView", ElementTimeout);
    }

    private static void AssertDisabledWithProLabel(AutomationElement scope, string automationId, string name)
    {
        var control = AppSession.Find(scope, automationId, ElementTimeout);
        Assert.AreEqual(name, control.Current.Name, automationId + " label");
        Assert.IsFalse(control.Current.IsEnabled, automationId + " is enabled in Free (docs/spec/06: Pro only)");
        var label = AppSession.Find(scope, automationId + "ProLabel", ElementTimeout);
        Assert.AreEqual(Text.ProLabel, label.Current.Name, automationId + "ProLabel");
    }

    /// <summary>Closes the app's window as a user would and waits for the process to end.</summary>
    private static void Close(AppSession app)
    {
        if (!app.Process.HasExited)
        {
            app.Process.CloseMainWindow();
        }

        Assert.IsTrue(app.Process.WaitForExit(15_000), "the app did not close within 15 s");
    }

    /// <summary>Every value under the user's Run key, as written (no expansion): name, kind and data. Read only.</summary>
    private static Dictionary<string, string> RunValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null)
        {
            return values;
        }

        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = kind + ":" + (data is byte[] bytes ? Convert.ToHexString(bytes) : data is string[] lines ? string.Join("\0", lines) : Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture));
        }

        return values;
    }

    private static string Describe(Dictionary<string, string> values) =>
        values.Count == 0 ? "(none)" : string.Join("; ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => v.Key + "=" + v.Value));

    // ASP-11: About owns the licence controls; Settings keeps preferences and Pro feature controls.
    [TestMethod]
    [TestCategory("Console")]
    public void AboutOwnsLicenseControlsAndSettingsKeepsPreferences()
    {
        using var app = AppSession.Start();
        var view = Ui.OpenView(app, "About", "AboutView");
        var key = AppSession.Find(view, "LicenseKey", ElementTimeout);
        Assert.AreEqual(ControlType.Edit, key.Current.ControlType, "LicenseKey is a text box");
        var activate = AppSession.Find(view, "ActivateKey", ElementTimeout);
        Assert.AreEqual(ControlType.Button, activate.Current.ControlType, "ActivateKey is a button");
        Assert.AreEqual(Text.ActivateKey, activate.Current.Name);
        Assert.IsNotNull(AppSession.Find(view, "LicenseStatus", ElementTimeout));
        var settings = OpenSettings(app);
        foreach (var id in new[] { "LicenseKey", "ActivateKey", "LicenseStatus" })
        {
            Assert.IsNull(AppSession.TryFind(settings, id), id + " must be absent from Settings");
        }
        Assert.IsNotNull(AppSession.Find(settings, "OpenAtLogin", ElementTimeout));
        Assert.IsNotNull(AppSession.Find(settings, "LowDiskAlertToggle", ElementTimeout));
    }

    // Contract: the sidebar has an About item (ASP-14) that opens the About view (AutomationId AboutView) with the version
    // text Version.
    [TestMethod]
    [TestCategory("Console")]
    public void AboutOpensFromTheMainWindowAndShowsAVersion()
    {
        using var app = AppSession.Start();
        Click(Ui.SidebarItem(app, "About"));
        var view = FindAnywhere(app, "AboutView", ElementTimeout);
        var version = AppSession.Find(view, "Version", ElementTimeout);
        var text = version.Current.Name;
        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "the Version text is empty");
        Assert.IsTrue(text.Any(char.IsDigit), $"the Version text \"{text}\" holds no version number");
    }

    // Contract and docs/spec/06 License: with no key, LicenseStatus shows Text.Free.
    [TestMethod]
    [TestCategory("Console")]
    public void LicenseStatusShowsFreeWithNoKey()
    {
        using var app = AppSession.Start();
        var view = Ui.OpenView(app, "About", "AboutView");
        var status = AppSession.Find(view, "LicenseStatus", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.Free, ElementTimeout), $"LicenseStatus is \"{status.Current.Name}\", expected \"{Text.Free}\"");
    }

    // Contract: the real verifier refuses every key in phase A. A pasted malformed key (with the white space a copy
    // brings along), then ActivateKey: LicenseStatus shows a refusal reason (not empty, not Free, not "Registered to"),
    // and the app is still Free: the Pro controls stay disabled with their labels.
    [TestMethod]
    [TestCategory("Console")]
    public void AMalformedKeyIsRefusedWithAReasonAndTheAppStaysFree()
    {
        using var app = AppSession.Start();
        var view = Ui.OpenView(app, "About", "AboutView");
        var key = AppSession.Find(view, "LicenseKey", ElementTimeout);
        var status = AppSession.Find(view, "LicenseStatus", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.Free, ElementTimeout), "Free before the key");
        ((ValuePattern)key.GetCurrentPattern(ValuePattern.Pattern)).SetValue("  not-a-license-key." + Guid.NewGuid().ToString("N") + "\r\n");
        Click(AppSession.Find(view, "ActivateKey", ElementTimeout));

        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name != Text.Free && !string.IsNullOrWhiteSpace(status.Current.Name), ElementTimeout),
            $"LicenseStatus after a refused key is \"{status.Current.Name}\": no refusal reason");
        var reason = status.Current.Name;
        var registered = Text.RegisteredTo(string.Empty).Trim();
        Assert.IsFalse(reason.StartsWith(registered, StringComparison.OrdinalIgnoreCase), $"a malformed key was accepted: \"{reason}\"");

        view = OpenSettings(app);
        AssertDisabledWithProLabel(view, "OpenAtLogin", Text.OpenAtLogin);
        AssertDisabledWithProLabel(view, "LowDiskAlertToggle", Text.LowDiskAlert);
        var export = FindAnywhere(app, "ExportReport", ElementTimeout);
        Assert.IsFalse(export.Current.IsEnabled, "ExportReport is enabled after a refused key");
    }

    // Contract and docs/spec/06: OpenAtLogin (Text.OpenAtLogin) and LowDiskAlertToggle (Text.LowDiskAlert) are Pro-only;
    // in Free each is disabled with a quiet Text.ProLabel, AutomationId <id>ProLabel. Neither is clicked.
    [TestMethod]
    [TestCategory("Console")]
    public void ProTogglesAreDisabledWithTheirProLabelInFree()
    {
        using var app = AppSession.Start();
        var view = OpenSettings(app);
        AssertDisabledWithProLabel(view, "OpenAtLogin", Text.OpenAtLogin);
        AssertDisabledWithProLabel(view, "LowDiskAlertToggle", Text.LowDiskAlert);
        foreach (var id in new[] { "OpenAtLogin", "LowDiskAlertToggle" })
        {
            Assert.IsTrue(AppSession.Find(view, id, ElementTimeout).TryGetCurrentPattern(TogglePattern.Pattern, out _), id + " is a toggle");
        }
    }

    // Contract: ExportReport (Text.ExportReport) is Pro-only: disabled in Free, with ExportReportProLabel. It is looked
    // for in the main window first, then in the Settings view; it is never clicked.
    [TestMethod]
    [TestCategory("Console")]
    public void ExportReportIsDisabledWithItsProLabelInFree()
    {
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);

        var export = AppSession.WaitFor(() => app.FindInAnyWindow("ExportReport"), NeverWatch);
        if (export is null)
        {
            OpenSettings(app);
            export = FindAnywhere(app, "ExportReport", ElementTimeout);
        }

        Assert.AreEqual(Text.ExportReport, export.Current.Name, "ExportReport label");
        Assert.IsFalse(export.Current.IsEnabled, "ExportReport is enabled in Free (docs/spec/06: Pro only)");
        var label = FindAnywhere(app, "ExportReportProLabel", ElementTimeout);
        Assert.AreEqual(Text.ProLabel, label.Current.Name, "ExportReportProLabel");
    }

    // Contract and docs/spec/06 Purchase reminder: shown only when a purchase page exists, and there is none. No element
    // with AutomationId Reminder exists at launch, nor after a scan has finished (the reminder never shows while a scan
    // runs, so after it is the moment it could).
    [TestMethod]
    [TestCategory("Console")]
    public void NoReminderAtLaunchOrAfterAScan()
    {
        using (var app = AppSession.Start())
        {
            AppSession.Find(app.Window(WindowTimeout), "VolumeCard", ElementTimeout);
            Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("Reminder") is not null, NeverWatch), "a Reminder element exists at launch");
        }

        using var fixture = SmallTree();
        using var scanned = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(scanned);
        Assert.IsFalse(AppSession.WaitUntil(() => scanned.FindInAnyWindow("Reminder") is not null, NeverWatch), "a Reminder element exists after the scan");
        OpenSettings(scanned);
        Assert.IsFalse(AppSession.WaitUntil(() => scanned.FindInAnyWindow("Reminder") is not null, NeverWatch), "a Reminder element exists with Settings open");
    }

    // docs/spec/06 Windows note and intent decision 15: "Open at login" is the only registry value the app writes, and
    // only its own module, when a Pro user turns it on. A whole Free session (launch, scan, Settings, a refused key,
    // About, close) leaves the user's Run key exactly as it was: the "Ambysto Lean" value and every other one. The test
    // only reads the key.
    [TestMethod]
    [TestCategory("Console")]
    public void AWholeSessionLeavesTheRunKeyUnchanged()
    {
        var before = RunValues();
        Console.WriteLine($"Run key before: {Describe(before)}");
        Console.WriteLine($"\"{RunValueName}\" before: {(before.TryGetValue(RunValueName, out var mine) ? mine : "(absent)")}");

        using (var fixture = SmallTree())
        {
            using var app = AppSession.Start("--scan", fixture.Path);
            Ui.WaitForScan(app);
            var view = Ui.OpenView(app, "About", "AboutView");
            var key = AppSession.Find(view, "LicenseKey", ElementTimeout);
            ((ValuePattern)key.GetCurrentPattern(ValuePattern.Pattern)).SetValue("not-a-license-key." + Guid.NewGuid().ToString("N"));
            Click(AppSession.Find(view, "ActivateKey", ElementTimeout));
            var status = AppSession.Find(view, "LicenseStatus", ElementTimeout);
            AppSession.WaitUntil(() => status.Current.Name != Text.Free, ElementTimeout);
            var about = AppSession.WaitFor(() => app.FindInAnyWindow("About"), NeverWatch);
            if (about is not null)
            {
                Click(about);
                FindAnywhere(app, "AboutView", ElementTimeout);
            }

            Close(app);
        }

        var after = RunValues();
        Console.WriteLine($"Run key after: {Describe(after)}");
        Assert.AreEqual(before.TryGetValue(RunValueName, out var b) ? b : "(absent)", after.TryGetValue(RunValueName, out var a) ? a : "(absent)", $"the \"{RunValueName}\" Run value changed");
        Assert.AreEqual(Describe(before), Describe(after), "the Run key changed during the session");
    }

    // Contract: activating a key never writes it anywhere in phase A (there is no key store). After a refused key and a
    // closed app, the key's unique text is in no file of the app's folders under %LOCALAPPDATA% and %APPDATA%, and in no
    // Run value. Read only.
    [TestMethod]
    [TestCategory("Console")]
    public void APastedKeyIsWrittenNowhere()
    {
        var marker = "lean-ui-key-" + Guid.NewGuid().ToString("N");
        using (var app = AppSession.Start())
        {
            var view = Ui.OpenView(app, "About", "AboutView");
            var key = AppSession.Find(view, "LicenseKey", ElementTimeout);
            ((ValuePattern)key.GetCurrentPattern(ValuePattern.Pattern)).SetValue(marker);
            Click(AppSession.Find(view, "ActivateKey", ElementTimeout));
            var status = AppSession.Find(view, "LicenseStatus", ElementTimeout);
            AppSession.WaitUntil(() => status.Current.Name != Text.Free, ElementTimeout);
            Close(app);
        }

        var hits = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var folder = Path.Combine(Environment.GetFolderPath(root), "Ambysto");
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    if (Encoding.UTF8.GetString(bytes).Contains(marker, StringComparison.Ordinal) || Encoding.Unicode.GetString(bytes).Contains(marker, StringComparison.Ordinal))
                    {
                        hits.Add(file);
                    }
                }
                catch (IOException)
                {
                    hits.Add(file + " (could not be read)");
                }
            }
        }

        hits.AddRange(RunValues().Where(v => v.Value.Contains(marker, StringComparison.Ordinal)).Select(v => @"HKCU\" + RunKeyPath + " " + v.Key));
        Assert.AreEqual(0, hits.Count, "the pasted key was written to: " + string.Join(", ", hits));
    }
}
