using System;
using System.IO;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-17 phase B (intent/2026-10-20-asp-17-suggest.md): the QA argument <c>--pro</c> exists in Debug builds only.
/// Release: the app started with <c>--pro</c> stays Free (Console, run with the Release build). Debug: the app started with
/// <c>--pro</c> is Pro, licensee "Debug build (--pro)", the Pro controls enabled and no Pro labels; started without it, it is
/// Free (TestCategory ProDebug only, never Console; run with the Debug build). <see cref="AppSession"/> starts the app of the
/// configuration this test assembly was built in, so a ProDebug test that finds itself in a Release build fails with a message
/// instead of passing. Nothing is toggled, invoked or written: the controls are only read.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ProArgumentTests
{
    private const string DebugLicensee = "Debug build (--pro)";
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a test watches for an element that must never appear.</summary>
    private static readonly TimeSpan NeverWatch = TimeSpan.FromSeconds(3);

    private static readonly string[] SettingsProControls = ["OpenAtLogin", "LowDiskAlertToggle"];

    /// <summary>The configuration this test assembly (and so the app <see cref="AppSession"/> starts) was built in.</summary>
    private static string Configuration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    private static void RequireDebugApp() =>
        Assert.AreEqual("Debug", Configuration,
            "this ProDebug test needs the DEBUG app (--pro exists only there), but the tests were built in " + Configuration
            + ": build and run with -c Debug (gates.json suite pro-debug)");

    private static void RequireReleaseApp()
    {
        if (Configuration != "Release")
        {
            Assert.Inconclusive("this test is about the RELEASE app, but the tests were built in " + Configuration + ": run it with -c Release");
        }
    }

    private static Fixture SmallTree()
    {
        var fixture = new Fixture("ui-pro-argument");
        fixture.File(@"alpha\one.bin", 300 * ByteUnits.KB);
        fixture.File("gamma.bin", 50 * ByteUnits.KB);
        return fixture;
    }

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

    private static AutomationElement FindAnywhere(AppSession app, string automationId, TimeSpan timeout)
    {
        var element = AppSession.WaitFor(() => app.FindInAnyWindow(automationId), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} in any window of the app within {timeout.TotalSeconds:F0} s");
        return element;
    }

    private static AutomationElement OpenSettings(AppSession app)
    {
        Click(Ui.SidebarItem(app, "Settings"));
        return FindAnywhere(app, "SettingsView", ElementTimeout);
    }

    private static void AssertFree(AppSession app, AutomationElement view, string how)
    {
        var about = Ui.OpenView(app, "About", "AboutView");
        var status = AppSession.Find(about, "LicenseStatus", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.Free, ElementTimeout),
            $"{how}: LicenseStatus is \"{status.Current.Name}\", expected \"{Text.Free}\"");
        view = OpenSettings(app);
        foreach (var id in SettingsProControls)
        {
            var control = AppSession.Find(view, id, ElementTimeout);
            Assert.IsFalse(control.Current.IsEnabled, $"{how}: {id} is enabled in Free (docs/spec/06: Pro only)");
            Assert.AreEqual(Text.ProLabel, AppSession.Find(view, id + "ProLabel", ElementTimeout).Current.Name, $"{how}: {id}ProLabel");
        }

        var export = FindAnywhere(app, "ExportReport", ElementTimeout);
        Assert.IsFalse(export.Current.IsEnabled, $"{how}: ExportReport is enabled in Free (docs/spec/06: Pro only)");
        Assert.AreEqual(Text.ProLabel, FindAnywhere(app, "ExportReportProLabel", ElementTimeout).Current.Name, $"{how}: ExportReportProLabel");
    }

    // Release: --pro is no argument of the Release app (no code, no string). The app stays Free: LicenseStatus is Free, and
    // OpenAtLogin, LowDiskAlertToggle and ExportReport are disabled, each with its quiet Pro label
    // (SettingsAndAboutTests.ProTogglesAreDisabledWithTheirProLabelInFree, with --pro and after a scan for the export).
    [TestMethod]
    [TestCategory("Console")]
    public void TheReleaseAppStartedWithProStaysFree()
    {
        RequireReleaseApp();
        using var fixture = SmallTree();
        using var app = AppSession.Start("--pro", "--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = OpenSettings(app);
        AssertFree(app, view, "Release app with --pro");
    }

    // Debug: --pro is Pro through the entitlement seam: LicenseStatus "Registered to Debug build (--pro)", the Pro controls enabled
    // (OpenAtLogin, LowDiskAlertToggle, and ExportReport once a scan has finished), and no Pro label anywhere. Nothing is toggled.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void TheDebugAppStartedWithProIsPro()
    {
        RequireDebugApp();
        using var fixture = SmallTree();
        using var app = AppSession.Start("--pro", "--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = OpenSettings(app);

        var about = Ui.OpenView(app, "About", "AboutView");
        var status = AppSession.Find(about, "LicenseStatus", ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.RegisteredTo(DebugLicensee), ElementTimeout),
            $"LicenseStatus is \"{status.Current.Name}\", expected \"Registered to {DebugLicensee}\"");
        Assert.AreEqual("Registered to Debug build (--pro)", status.Current.Name);

        // ASP-33: the three controls need the private module. A build without it (the public one) keeps them in the locked
        // state, the quiet Pro label included, even for a verified key; a build with it unlocks them.
        var hasModule = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppSession.AppPath())!, "Ambysto.Lean.Pro.dll"));
        view = OpenSettings(app);
        foreach (var id in SettingsProControls)
        {
            var control = AppSession.Find(view, id, ElementTimeout);
            if (hasModule)
            {
                Assert.IsTrue(AppSession.WaitUntil(() => control.Current.IsEnabled, ElementTimeout), id + " is disabled in a Pro app");
                Assert.IsFalse(AppSession.WaitUntil(() => AppSession.TryFind(view, id + "ProLabel") is not null, NeverWatch), id + "ProLabel is shown in a Pro app");
            }
            else
            {
                Assert.IsFalse(control.Current.IsEnabled, id + " is enabled without the Pro module");
                Assert.AreEqual(Text.ProLabel, AppSession.Find(view, id + "ProLabel", ElementTimeout).Current.Name, id + "ProLabel without the Pro module");
            }
        }

        var export = FindAnywhere(app, "ExportReport", ElementTimeout);
        if (hasModule)
        {
            Assert.IsTrue(AppSession.WaitUntil(() => export.Current.IsEnabled, ElementTimeout), "ExportReport is disabled in a Pro app after a scan");
            Assert.IsFalse(AppSession.WaitUntil(() => app.FindInAnyWindow("ExportReportProLabel") is not null, NeverWatch), "ExportReportProLabel is shown in a Pro app");
        }
        else
        {
            Assert.IsFalse(export.Current.IsEnabled, "ExportReport is enabled without the Pro module");
            Assert.AreEqual(Text.ProLabel, FindAnywhere(app, "ExportReportProLabel", ElementTimeout).Current.Name, "ExportReportProLabel without the Pro module");
        }
    }

    // Debug without --pro: Free, as any build is with no key.
    [TestMethod]
    [TestCategory("ProDebug")]
    public void TheDebugAppStartedWithoutProIsFree()
    {
        RequireDebugApp();
        using var fixture = SmallTree();
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = OpenSettings(app);
        AssertFree(app, view, "Debug app without --pro");
    }
}
