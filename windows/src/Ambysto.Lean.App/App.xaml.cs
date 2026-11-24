using System;
using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;

namespace Ambysto.Lean.App;

/// <summary>The application: one main window.</summary>
public partial class App : Application
{
    private MainWindow? window;

    /// <summary>Creates the application.</summary>
    public App()
    {
        // A failure while the app starts or draws ends the process with no window (a real PC, 2026-10-18: 0xc000027b in
        // Microsoft.UI.Xaml.dll). Each unhandled one is appended to crash.log beside the journal, so it can be read; the
        // app still ends as before.
        UnhandledException += (_, e) => CrashLog.Append("UnhandledException", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Append("AppDomain", e.ExceptionObject as Exception, null);
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            CrashLog.Append("App.InitializeComponent", ex, null);
            throw;
        }
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AlertNotifier.UseAppId();
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Append("OnLaunched", ex, null);
            throw;
        }
    }
}

/// <summary>Appends what ended the app to <c>%LOCALAPPDATA%\Ambysto\Space\crash.log</c>. Append only, never read back by
/// the app; writing it can never fail the app a second time.</summary>
internal static class CrashLog
{
    internal static void Append(string where, Exception? exception, string? message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space");
            Directory.CreateDirectory(folder);
            var text = string.Create(CultureInfo.InvariantCulture,
                $"--- {DateTime.UtcNow:O} {where} (Windows {Environment.OSVersion.Version}, {CultureInfo.CurrentUICulture.Name})\n" +
                $"message: {message}\n{Describe(exception)}\n");
            File.AppendAllText(Path.Combine(folder, "crash.log"), text);
        }
        catch (Exception)
        {
            // Nothing more can be done while the app ends.
        }
    }

    private static string Describe(Exception? exception)
    {
        var text = string.Empty;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"{e.GetType().FullName} 0x{e.HResult:X8}: {e.Message}\n{e.StackTrace}\n");
        }

        return text;
    }
}
