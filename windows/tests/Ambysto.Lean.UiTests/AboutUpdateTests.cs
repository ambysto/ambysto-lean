using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

// Independent acceptance tests, frozen ASP-11 contract. ProDebug is the existing console Debug gate.
[TestClass]
[DoNotParallelize]
public sealed class AboutUpdateTests
{
    private const string Feed = "https://lean.ambysto.com/windows/latest.json";
    private const string Page = "https://lean.ambysto.com/windows/";
    private const string Future = "65535.65535.65535";

    private sealed class UpdateFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LeanUpdate-" + Guid.NewGuid().ToString("N") + ".json");
        public UpdateFixture(string version = Future, int status = 200, int delay = 0, bool failure = false, bool launch = true, string? body = null)
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(new
            {
                body = body ?? JsonSerializer.Serialize(new { version, downloadUrl = Page }),
                statusCode = status, delayMilliseconds = delay, failure, launchSucceeds = launch
            }));
        }
        public AppSession Start(params string[] extra)
        {
            Assert.AreEqual("Debug", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "fixture tests require Debug (ProDebug gate)");
            return AppSession.Start(new[] { "--update-qa", Path }.Concat(extra).ToArray());
        }
        public string[] Events()
        {
            try { return File.Exists(Path + ".events") ? File.ReadAllLines(Path + ".events").Where(s => s.Length > 0).ToArray() : []; }
            catch (IOException) { return []; } // append may briefly hold the file; waits retry
        }
        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Path + ".events");
        }
    }

    private static AutomationElement About(AppSession app) => Ui.OpenView(app, "About", "AboutView");
    private static AutomationElement Find(AppSession app, string id) => Ui.FindAnywhere(app, id, Ui.ElementTimeout);
    private static void Check(AppSession app) => Ui.Click(Find(app, "CheckForUpdates"));
    private static void Status(AppSession app, string expected) => Assert.IsTrue(AppSession.WaitUntil(
        () => Find(app, "UpdateStatus").Current.Name == expected, Ui.ElementTimeout), "expected update status: " + expected);
    private static void Events(UpdateFixture fixture, params string[] expected)
    {
        Assert.IsTrue(AppSession.WaitUntil(() => fixture.Events().SequenceEqual(expected), TimeSpan.FromSeconds(3)),
            "events: " + string.Join(" | ", fixture.Events()));
        Thread.Sleep(300);
        CollectionAssert.AreEqual(expected, fixture.Events(), "no retry or extra browser launch");
    }

    [TestMethod]
    [TestCategory("Console")]
    public void AboutShowsProductAssemblyVersionPaymentAndAccessibleActionsWithoutDonate()
    {
        using var app = AppSession.Start();
        var view = About(app);
        Assert.AreEqual("Ambysto Lean", AppSession.Find(view, "AboutAppName", Ui.ElementTimeout).Current.Name);
        Assert.AreEqual("Free to use. We only charge when you are happy to pay.", AppSession.Find(view, "AboutPayment", Ui.ElementTimeout).Current.Name);
        var version = AssemblyName.GetAssemblyName(System.IO.Path.ChangeExtension(AppSession.AppPath(), ".dll")).Version!;
        StringAssert.Contains(AppSession.Find(view, "Version", Ui.ElementTimeout).Current.Name,
            $"{version.Major}.{version.Minor}.{version.Build}");
        foreach (var id in new[] { "Website", "StarOnGitHub", "CheckForUpdates", "LicenseKey", "ActivateKey" })
        {
            var control = AppSession.Find(view, id, Ui.ElementTimeout);
            Assert.IsTrue(control.Current.IsKeyboardFocusable, id + " keyboard access");
            Assert.IsFalse(string.IsNullOrWhiteSpace(control.Current.Name), id + " accessible name");
        }
        Assert.IsFalse(Ui.Names(view).Any(s => s.Contains("Donate", StringComparison.OrdinalIgnoreCase)), "Donate has no visible control in v1");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void DebugLicenseControlsBelongToAboutAndAreAbsentFromSettings()
    {
        using var fixture = new UpdateFixture();
        using var app = fixture.Start();
        var about = About(app);
        foreach (var id in new[] { "LicenseKey", "ActivateKey", "LicenseStatus" })
            Assert.IsNotNull(AppSession.Find(about, id, Ui.ElementTimeout), id + " belongs to About in Debug too");
        var settings = Ui.OpenView(app, "Settings", "SettingsView");
        foreach (var id in new[] { "LicenseKey", "ActivateKey", "LicenseStatus" })
            Assert.IsNull(AppSession.TryFind(settings, id), id + " is absent from the Settings UIA subtree in Debug too");
        Assert.IsNotNull(AppSession.Find(settings, "OpenAtLogin", Ui.ElementTimeout));
        Assert.IsNotNull(AppSession.Find(settings, "LowDiskAlertToggle", Ui.ElementTimeout));
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void LaunchScanNavigationAndLicenseActivationNeverRequestUpdates()
    {
        using var fixture = new UpdateFixture();
        using var tree = new Fixture("ui-update-no-auto");
        tree.File("small.bin", 4096);
        using var app = fixture.Start("--scan", tree.Path);
        Ui.WaitForScan(app);
        Ui.OpenView(app, "Settings", "SettingsView");
        About(app);
        var key = Find(app, "LicenseKey");
        ((ValuePattern)key.GetCurrentPattern(ValuePattern.Pattern)).SetValue(" \r\n ");
        Ui.Click(Find(app, "ActivateKey"));
        Assert.AreEqual(Text.Free, Find(app, "LicenseStatus").Current.Name, "blank key does nothing");
        ((ValuePattern)key.GetCurrentPattern(ValuePattern.Pattern)).SetValue("not-a-real-key");
        Ui.Click(Find(app, "ActivateKey"));
        Assert.IsTrue(AppSession.WaitUntil(() => Find(app, "LicenseStatus").Current.Name != Text.Free, Ui.ElementTimeout));
        Ui.OpenView(app, "Settings", "SettingsView");
        About(app);
        Assert.IsFalse(AppSession.WaitUntil(() => fixture.Events().Length > 0, TimeSpan.FromSeconds(3)), "navigation/activation/scan contacted update transport or browser");
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void NewerReleaseMakesExactlyOneGetAndOpensOnlyItsPage()
    {
        using var fixture = new UpdateFixture();
        using var app = fixture.Start();
        About(app);
        Check(app);
        Status(app, Ui.Copy("UpdateAvailable", Future));
        Events(fixture, "GET " + Feed, "OPEN " + Page);
        Assert.IsTrue(Find(app, "CheckForUpdates").Current.IsEnabled);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    [DataRow(false)] [DataRow(true)]
    public void EqualAndOlderReleasesStayCurrentWithoutOpeningABrowser(bool equal)
    {
        var info = AssemblyName.GetAssemblyName(System.IO.Path.ChangeExtension(AppSession.AppPath(), ".dll")).Version!;
        var current = $"{info.Major}.{info.Minor}.{info.Build}";
        using var fixture = new UpdateFixture(equal ? current : "0.0.0");
        using var app = fixture.Start();
        About(app);
        Check(app);
        Status(app, Ui.Copy("UpdatesCurrent"));
        Events(fixture, "GET " + Feed);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    [DataRow("malformed")] [DataRow("offline")] [DataRow("http")] [DataRow("timeout")]
    public void FailedChecksShowInlineFailureAndPermitOneRequestPerRetry(string mode)
    {
        using var fixture = new UpdateFixture(status: mode == "http" ? 404 : 200, delay: mode == "timeout" ? 60000 : 0,
            failure: mode == "offline", body: mode == "malformed" ? "not JSON" : null);
        using var app = fixture.Start();
        About(app);
        Check(app);
        Status(app, Ui.Copy("UpdatesFailed"));
        Assert.IsTrue(Find(app, "CheckForUpdates").Current.IsEnabled);
        Events(fixture, "GET " + Feed);
        Check(app);
        Status(app, Ui.Copy("UpdatesFailed"));
        Events(fixture, "GET " + Feed, "GET " + Feed);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void BrowserFailureRetainsNewVersionAndExplainsPageFailure()
    {
        using var fixture = new UpdateFixture(launch: false);
        using var app = fixture.Start();
        var view = About(app);
        Check(app);
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.AnyNameContains(view, Ui.Copy("UpdateAvailable", Future)) &&
            Ui.AnyNameContains(view, Ui.Copy("UpdatePageFailed")), Ui.ElementTimeout), "both newer-release and browser-failure messages remain inline");
        Events(fixture, "GET " + Feed, "OPEN " + Page);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void BusyCheckDisablesReentryAndSurvivesNavigationWithoutAnExtraRequest()
    {
        using var fixture = new UpdateFixture(delay: 3000);
        using var app = fixture.Start();
        About(app);
        Check(app);
        Status(app, Ui.Copy("CheckingForUpdates"));
        Assert.IsFalse(Find(app, "CheckForUpdates").Current.IsEnabled);
        try { Ui.Click(Find(app, "CheckForUpdates")); }
        catch (ElementNotEnabledException) { }
        Ui.OpenView(app, "Settings", "SettingsView");
        About(app);
        Status(app, Ui.Copy("UpdateAvailable", Future));
        Events(fixture, "GET " + Feed, "OPEN " + Page);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void ClosingDuringDelayedResponseCancelsPromptlyAndNeverOpensBrowser()
    {
        using var fixture = new UpdateFixture(delay: 3000);
        using var app = fixture.Start();
        About(app);
        Check(app);
        Assert.IsTrue(AppSession.WaitUntil(() => fixture.Events().Length == 1, TimeSpan.FromSeconds(3)));
        app.Process.CloseMainWindow();
        Assert.IsTrue(app.Process.WaitForExit(2000), "close cancels update checking instead of waiting for response");
        Thread.Sleep(3500);
        Events(fixture, "GET " + Feed);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    [DataRow("not a fixture")]
    [DataRow("""{"body":"{\"version\":\"65535.65535.65535\",\"downloadUrl\":\"https://lean.ambysto.com/windows/\"}","statusCode":1e100}""")]
    [DataRow("""{"body":"{\"version\":\"65535.65535.65535\",\"downloadUrl\":\"https://lean.ambysto.com/windows/\"}","statusCode":2147483648}""")]
    [DataRow("""{"body":"{\"version\":\"65535.65535.65535\",\"downloadUrl\":\"https://lean.ambysto.com/windows/\"}","delayMilliseconds":2147483648}""")]
    [DataRow("""{"body":"{\"version\":\"65535.65535.65535\",\"downloadUrl\":\"https://lean.ambysto.com/windows/\"}","delayMilliseconds":1e100}""")]
    public void InvalidFixtureFailsClosedWithoutNetworkFallback(string fixtureJson)
    {
        using var fixture = new UpdateFixture();
        File.WriteAllText(fixture.Path, fixtureJson);
        using var app = fixture.Start();
        About(app);
        Check(app);
        Status(app, Ui.Copy("UpdatesFailed"));
        Assert.IsFalse(app.Process.HasExited, "invalid fixture must not crash the app");
        Assert.IsTrue(Find(app, "CheckForUpdates").Current.IsEnabled, "failed fixture check remains retryable");
        Assert.IsFalse(fixture.Events().Any(s => s.StartsWith("OPEN ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void WebsiteFailureIsInlineAndDoesNotCheckForUpdates()
    {
        using var fixture = new UpdateFixture(launch: false);
        using var app = fixture.Start();
        var view = About(app);
        Ui.Click(Find(app, "Website"));
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.AnyNameContains(view, Ui.Copy("WebsiteFailed")), Ui.ElementTimeout));
        Assert.AreEqual(1, fixture.Events().Length);
        Assert.IsTrue(fixture.Events()[0].StartsWith("OPEN https://lean.ambysto.com", StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("Console")]
    public void AboutActionsRemainKeyboardReachableAtMinimumWindowSize()
    {
        using var app = AppSession.Start();
        var window = app.Window(Ui.WindowTimeout);
        var original = window.Current.BoundingRectangle;
        var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
        try
        {
            transform.Resize(560, 560);
            var view = About(app);
            foreach (var id in new[] { "Website", "StarOnGitHub", "CheckForUpdates", "LicenseKey", "ActivateKey" })
            {
                var control = AppSession.Find(view, id, Ui.ElementTimeout);
                Ui.Focus(control);
                Assert.IsTrue(AppSession.WaitUntil(() => !control.Current.IsOffscreen, TimeSpan.FromSeconds(3)), id + " must scroll into view on keyboard focus");
                var bounds = control.Current.BoundingRectangle;
                var outer = window.Current.BoundingRectangle;
                Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= outer.Left && bounds.Right <= outer.Right + 1 &&
                    bounds.Top >= outer.Top && bounds.Bottom <= outer.Bottom + 1, id + " is clipped at minimum size");
            }
        }
        finally { transform.Resize(original.Width, original.Height); }
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void WebsiteUsesTheFixtureLauncherWithoutCheckingUpdates()
    {
        using var fixture = new UpdateFixture();
        using var app = fixture.Start();
        About(app);
        Ui.Click(Find(app, "Website"));
        Assert.IsTrue(AppSession.WaitUntil(() => fixture.Events().Length == 1, TimeSpan.FromSeconds(3)));
        var events = fixture.Events();
        Assert.IsTrue(events[0].StartsWith("OPEN https://lean.ambysto.com", StringComparison.Ordinal));
        Assert.IsFalse(events.Any(s => s.StartsWith("GET ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void StarOnGitHubUsesTheFixtureLauncherWithoutCheckingUpdates()
    {
        using var fixture = new UpdateFixture();
        using var app = fixture.Start();
        var view = About(app);
        Assert.AreEqual(Ui.Copy("StarOnGitHub"), Find(app, "StarOnGitHub").Current.Name);
        Ui.Click(Find(app, "StarOnGitHub"));
        Assert.IsTrue(AppSession.WaitUntil(() => fixture.Events().Length == 1, TimeSpan.FromSeconds(3)));
        Thread.Sleep(300);
        var events = fixture.Events();
        Assert.AreEqual(1, events.Length, "exactly one event and no update check");
        Assert.IsTrue(events[0].StartsWith("OPEN https://github.com/ambysto/ambysto-lean", StringComparison.Ordinal));
        Assert.IsFalse(Ui.AnyNameContains(view, Ui.Copy("GitHubFailed")));
        Assert.IsTrue(Find(app, "StarOnGitHub").Current.IsEnabled);
    }

    [TestMethod]
    [TestCategory("ProDebug")]
    public void StarOnGitHubFailureIsInlineRetryableAndDoesNotCheckForUpdates()
    {
        using var fixture = new UpdateFixture(launch: false);
        using var app = fixture.Start();
        var view = About(app);
        Ui.Click(Find(app, "StarOnGitHub"));
        Assert.IsTrue(AppSession.WaitUntil(() => Ui.AnyNameContains(view, Ui.Copy("GitHubFailed")), Ui.ElementTimeout));
        Assert.AreEqual(1, fixture.Events().Length);
        Assert.IsTrue(fixture.Events()[0].StartsWith("OPEN https://github.com/ambysto/ambysto-lean", StringComparison.Ordinal));
        Assert.IsFalse(app.Process.HasExited, "launch failure must not crash the app");
        Assert.IsTrue(Find(app, "StarOnGitHub").Current.IsEnabled, "failed launch remains retryable");
    }
}
