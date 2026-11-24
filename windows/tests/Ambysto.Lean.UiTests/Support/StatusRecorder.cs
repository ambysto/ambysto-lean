using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Records every text the main window's status line (AutomationId <c>Status</c>) shows while it runs: from the
/// element's Name-changed events and from a poll every 20 ms on a thread of its own, so that a line shown for a moment
/// (one item of a move) is still seen. Read only.</summary>
internal sealed class StatusRecorder : IDisposable
{
    private readonly AutomationElement status;
    private readonly AutomationPropertyChangedEventHandler handler;
    private readonly Thread poller;
    private readonly List<string> seen = [];
    private readonly object gate = new();
    private volatile bool stop;

    public StatusRecorder(AutomationElement status)
    {
        this.status = status;
        Take(Read());
        handler = (_, e) =>
        {
            if (e.NewValue is string text)
            {
                Take(text);
            }
        };
        Automation.AddAutomationPropertyChangedEventHandler(status, TreeScope.Element, handler, AutomationElement.NameProperty);
        poller = new Thread(() =>
        {
            while (!stop)
            {
                Take(Read());
                Thread.Sleep(20);
            }
        })
        { IsBackground = true, Name = "status poll" };
        poller.Start();
    }

    /// <summary>The distinct texts seen so far, in the order first seen.</summary>
    public List<string> Seen
    {
        get
        {
            lock (gate)
            {
                return [.. seen];
            }
        }
    }

    private string? Read()
    {
        try
        {
            return status.Current.Name;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Take(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (gate)
        {
            if (!seen.Contains(text))
            {
                seen.Add(text);
            }
        }
    }

    public void Dispose()
    {
        stop = true;
        poller.Join(2_000);
        try
        {
            Automation.RemoveAutomationPropertyChangedEventHandler(status, handler);
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (ArgumentException)
        {
        }
    }
}
