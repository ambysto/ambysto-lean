using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Updates;
using Microsoft.UI.Xaml;

namespace Ambysto.Lean.App;

/// <summary>ASP-11: passive About content and a user-requested, browser-only update check.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the Window lifetime; Closing cancels the request and Closed disposes its reader.")]
public sealed partial class MainWindow
{
    private UpdateChecker? updateChecker;
    private CancellationTokenSource? updateCancellation;
    private bool aboutClosing;

    private void InitializeAbout()
    {
        AboutAppNameText.Text = Text.AppName;
        AboutDeveloperText.Text = Text.DevelopedByAmbysto;
        AboutPaymentText.Text = Text.AboutPayment;
        WebsiteButton.Content = Text.Website;
        StarOnGitHubLabel.Text = Text.StarOnGitHub;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StarOnGitHubButton, Text.StarOnGitHub);
        CheckForUpdatesButton.Content = Text.CheckForUpdates;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AmbystoLean.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                AboutLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AboutLogoImage, Text.AppName);
            }
            catch (Exception)
            {
            }
        }
        ConfigureUpdateReader(); // compiled out of Release; no transport is contacted here
        AppWindow.Closing += (_, _) =>
        {
            // Approved work can defer the actual close; update work stops at the first close request.
            aboutClosing = true;
            InvalidateUninstallResult();
            updateCancellation?.Cancel();
            CheckForUpdatesButton.IsEnabled = false;
            WebsiteButton.IsEnabled = false;
            StarOnGitHubButton.IsEnabled = false;
            UpdateStatusText.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) =>
        {
            aboutClosing = true;
            InvalidateUninstallResult();
            updateCancellation?.Cancel();
            updateChecker?.Dispose();
        };
    }

    partial void ConfigureUpdateReader();

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (aboutClosing || updateCancellation is not null) return;
        using var cancel = new CancellationTokenSource();
        updateCancellation = cancel;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = Text.CheckingForUpdates;
        try
        {
            updateChecker ??= new UpdateChecker();
            var installed = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var result = await updateChecker.CheckAsync(installed, cancel.Token);
            if (aboutClosing || cancel.IsCancellationRequested) return;
            if (result.State == UpdateState.Available && result.Version is { } version && result.DownloadPage is { } page)
            {
                UpdateStatusText.Text = Text.UpdateAvailable(version.ToString(3));
                if (!await OpenAboutPageAsync(page) && !aboutClosing)
                {
                    UpdateStatusText.Text = Text.UpdateAvailable(version.ToString(3)) + " " + Text.UpdatePageFailed;
                }
            }
            else
            {
                UpdateStatusText.Text = result.State == UpdateState.Current ? Text.UpdatesCurrent : Text.UpdatesFailed;
            }
        }
        catch (OperationCanceledException)
        {
            // The initial close request cancels the check, including when approved work holds the window open.
        }
        finally
        {
            updateCancellation = null;
            if (!aboutClosing) CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnWebsiteClick(object sender, RoutedEventArgs e)
    {
        if (aboutClosing) return;
        WebsiteButton.IsEnabled = false;
        try
        {
            var opened = await OpenAboutPageAsync(new Uri("https://lean.ambysto.com/"));
            if (!aboutClosing)
            {
                WebsiteMessage.Text = opened ? string.Empty : Text.WebsiteFailed;
                WebsiteMessage.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            if (!aboutClosing) WebsiteButton.IsEnabled = true;
        }
    }

    private async void OnStarOnGitHubClick(object sender, RoutedEventArgs e)
    {
        if (aboutClosing) return;
        StarOnGitHubButton.IsEnabled = false;
        try
        {
            var opened = await OpenAboutPageAsync(new Uri("https://github.com/ambysto/ambysto-lean"));
            if (!aboutClosing)
            {
                StarOnGitHubMessage.Text = opened ? string.Empty : Text.GitHubFailed;
                StarOnGitHubMessage.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            if (!aboutClosing) StarOnGitHubButton.IsEnabled = true;
        }
    }

    private async void OnBuyCoffeeClick(object sender, RoutedEventArgs e)
    {
        if (aboutClosing) return;
        _ = await OpenAboutPageAsync(new Uri("https://buymeacoffee.com/ambysto"));
    }

    private static async Task<bool> OpenAboutPageAsync(Uri page)
    {
        try
        {
            return await SystemLauncher.OpenWebPageAsync(page);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
