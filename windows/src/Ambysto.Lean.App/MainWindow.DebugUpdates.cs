#if DEBUG
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Updates;

namespace Ambysto.Lean.App;

/// <summary>ASP-11: the Debug-only local transport/launcher fixture, never an entitlement override.</summary>
public sealed partial class MainWindow
{
    partial void ConfigureUpdateReader()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--update-qa");
        if (at < 1) return;
        var fixture = new UpdateQa(at + 1 < args.Length ? args[at + 1] : string.Empty);
        updateChecker = new UpdateChecker(fixture);
        SystemLauncher.UseQaWebLaunch(fixture.LaunchAsync);
    }
}

internal static partial class SystemLauncher
{
    private static Func<Uri, Task<bool>>? qaWebLaunch;
    internal static void UseQaWebLaunch(Func<Uri, Task<bool>> launch) => qaWebLaunch = launch;
}

internal sealed class UpdateQa : HttpMessageHandler
{
    private readonly string path;
    private readonly string body = string.Empty;
    private readonly int statusCode = 200;
    private readonly int delayMilliseconds;
    private readonly bool failure = true;
    private readonly bool launchSucceeds;

    internal UpdateQa(string fixturePath)
    {
        path = fixturePath;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return;
            using var stream = File.OpenRead(path);
            if (stream.Length > 131072) return;
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            body = root.GetProperty("body").GetString() ?? string.Empty;
            statusCode = root.TryGetProperty("statusCode", out var status) ? status.GetInt32() : 200;
            delayMilliseconds = root.TryGetProperty("delayMilliseconds", out var delay) ? delay.GetInt32() : 0;
            launchSucceeds = !root.TryGetProperty("launchSucceeds", out var launch) || launch.GetBoolean();
            failure = (root.TryGetProperty("failure", out var fail) && fail.GetBoolean())
                || statusCode < 100 || statusCode > 599 || delayMilliseconds < 0 || delayMilliseconds > 60000;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or InvalidOperationException or FormatException or System.Collections.Generic.KeyNotFoundException)
        {
            // A requested but invalid fixture always stays isolated; it never falls back to the real endpoint.
            failure = true;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Record("GET " + request.RequestUri!.AbsoluteUri);
        if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (failure) throw new HttpRequestException("Invalid or failing update fixture");
        return new HttpResponseMessage((HttpStatusCode)statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    internal Task<bool> LaunchAsync(Uri page)
    {
        Record("OPEN " + page.AbsoluteUri);
        return Task.FromResult(!failure && launchSucceeds);
    }

    private void Record(string value)
    {
        if (!Path.IsPathFullyQualified(path)) return;
        try
        {
            File.AppendAllText(path + ".events", value + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No fixture IO error may enable real network or browser access.
        }
    }
}
#endif
