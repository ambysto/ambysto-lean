using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>One run of the built app, driven through UI Automation: start with QA arguments, find the main window and
/// its elements by AutomationId, close in time (a 10 s grace before Kill). Never invokes OpenRecycleBin or Reveal; Move and
/// Put back only in MoveAndPutBackTests, on the test's own items under <c>%USERPROFILE%\LeanUiMove-*</c>.</summary>
internal sealed class AppSession : IDisposable
{
    private const string WindowTitle = "Ambysto Lean";

    private AppSession(Process process, Stopwatch sinceStart)
    {
        Process = process;
        SinceStart = sinceStart;
    }

    public Process Process { get; }

    /// <summary>Running since <see cref="Process.Start()"/>.</summary>
    public Stopwatch SinceStart { get; }

    /// <summary>The built app next to this test assembly's output (tests/…/bin/&lt;cfg&gt;/&lt;tfm&gt;/ → src/…/bin/&lt;cfg&gt;/&lt;tfm&gt;/win-x64/).</summary>
    public static string AppPath()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = here.Parent!.Name;
        var windows = here.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(windows, "src", "Ambysto.Lean.App", "bin", configuration, "net10.0-windows10.0.26100.0", "win-x64", "AmbystoLean.exe");
    }

    /// <summary>Starts the app with <paramref name="arguments"/>.</summary>
    public static AppSession Start(params string[] arguments) => StartWithEnvironment(new Dictionary<string, string>(), arguments);

    /// <summary>Starts the app with <paramref name="arguments"/> and the environment variables <paramref name="environment"/> set
    /// (added to, or replacing, the test's own; ASP-20: PATH with the fake tool's folder first, and the LEAN_FAKETOOL_* variables).</summary>
    public static AppSession StartWithEnvironment(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = AppPath();
        Assert.IsTrue(File.Exists(path), "built app not found at " + path);
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        var clock = Stopwatch.StartNew();
        var process = Process.Start(info)!;
        return new AppSession(process, clock);
    }

    /// <summary>The main window, within <paramref name="timeout"/>.</summary>
    public AutomationElement Window(TimeSpan timeout)
    {
        var window = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Process.Id),
                             new PropertyCondition(AutomationElement.NameProperty, WindowTitle))), timeout);
        Assert.IsNotNull(window, $"no window named {WindowTitle} within {timeout.TotalSeconds:F0} s (exited: {Process.HasExited})");
        return window;
    }

    /// <summary>The element with <paramref name="automationId"/> below <paramref name="scope"/>, within <paramref name="timeout"/>.</summary>
    public static AutomationElement Find(AutomationElement scope, string automationId, TimeSpan timeout)
    {
        var element = WaitFor(() => scope.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)), timeout);
        Assert.IsNotNull(element, $"no element with AutomationId {automationId} within {timeout.TotalSeconds:F0} s");
        return element;
    }

    /// <summary>The element with <paramref name="automationId"/> below <paramref name="scope"/>, or null now.</summary>
    public static AutomationElement? TryFind(AutomationElement scope, string automationId) =>
        scope.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    /// <summary>Every element with <paramref name="automationId"/> below <paramref name="scope"/>.</summary>
    public static List<AutomationElement> FindAll(AutomationElement scope, string automationId) =>
        scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)).Cast<AutomationElement>().ToList();

    /// <summary>The element with <paramref name="automationId"/> in any top-level window of this process (a context menu
    /// is its own popup window), or null now.</summary>
    public AutomationElement? FindInAnyWindow(string automationId)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, Process.Id));
        foreach (AutomationElement top in windows)
        {
            if (TryFind(top, automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static AutomationElement? WaitFor(Func<AutomationElement?> find, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (find() is { } found)
            {
                return found;
            }

            if (watch.Elapsed >= timeout)
            {
                return null;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>Polls <paramref name="condition"/> until true or <paramref name="timeout"/>.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (watch.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>Waits for the process to end and returns its exit code, or fails after <paramref name="timeout"/>.</summary>
    public int ExitCode(TimeSpan timeout)
    {
        Assert.IsTrue(Process.WaitForExit((int)timeout.TotalMilliseconds), $"the app did not exit within {timeout.TotalSeconds:F0} s");
        return Process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.CloseMainWindow();
                if (!Process.WaitForExit(10_000))
                {
                    Process.Kill();
                    Process.WaitForExit(5_000);
                }
            }
        }
        finally
        {
            Process.Dispose();
        }
    }
}
