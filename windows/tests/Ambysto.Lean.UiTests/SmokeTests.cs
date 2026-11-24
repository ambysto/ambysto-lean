using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>The built app starts as the standard user in the console session and shows its main window.</summary>
[TestClass]
public sealed class SmokeTests
{
    private static string AppPath()
    {
        // tests/Ambysto.Lean.UiTests/bin/<Configuration>/<tfm>/ → src/Ambysto.Lean.App/bin/<Configuration>/<tfm>/win-x64/
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = here.Parent!.Name;
        var windows = here.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(windows, "src", "Ambysto.Lean.App", "bin", configuration, "net10.0-windows10.0.26100.0", "win-x64", "AmbystoLean.exe");
    }

    [TestMethod]
    [TestCategory("Console")]
    public void AppStartsAndShowsItsMainWindow()
    {
        var path = AppPath();
        Assert.IsTrue(File.Exists(path), "built app not found at " + path);
        using var app = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
        try
        {
            var window = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                                 new PropertyCondition(AutomationElement.NameProperty, "Ambysto Lean"))), TimeSpan.FromSeconds(30));
            Assert.IsNotNull(window, "no window named Ambysto Lean");
            var status = WaitFor(() => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "Status")), TimeSpan.FromSeconds(10));
            Assert.IsNotNull(status, "no element with automation id Status");
        }
        finally
        {
            app.CloseMainWindow();
            if (!app.WaitForExit(10_000))
            {
                app.Kill();
            }
        }
    }

    private static AutomationElement? WaitFor(Func<AutomationElement?> find, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (find() is { } found)
            {
                return found;
            }
            System.Threading.Thread.Sleep(250);
        }
        return null;
    }
}
