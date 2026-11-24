using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ambysto.Lean.Core.Updates;

/// <summary>The result of a user-requested update check.</summary>
public enum UpdateState
{
    /// <summary>The installed version is at least as new as the feed.</summary>
    Current,
    /// <summary>A newer version has a validated download page.</summary>
    Available,
    /// <summary>The feed could not be read or validated.</summary>
    Failed,
}

/// <summary>A validated manifest result, or a failure with no remote values.</summary>
public sealed record UpdateResult(UpdateState State, Version? Version, Uri? DownloadPage);

/// <summary>ASP-11: one manual, bounded read of the Windows version manifest.</summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly HttpClient client;
    private static readonly UpdateResult Failure = new(UpdateState.Failed, null, null);
    /// <summary>The one production manifest endpoint.</summary>
    public static Uri FeedUri { get; } = new("https://lean.ambysto.com/windows/latest.json");
    /// <summary>The maximum UTF-8 manifest size.</summary>
    public const int MaxManifestBytes = 16384;
    /// <summary>The total deadline, including the body.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(10);
    /// <summary>Creates the production reader.</summary>
    public UpdateChecker() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        PreAuthenticate = false,
        Credentials = null,
    }) { }
    /// <summary>Creates an isolated reader with the given test transport.</summary>
    public UpdateChecker(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    /// <summary>Checks only when called. Caller cancellation propagates.</summary>
    public async Task<UpdateResult> CheckAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return Failure;
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[MaxManifestBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await body.ReadAsync(bytes.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }

            deadline.Token.ThrowIfCancellationRequested();
            if (length > MaxManifestBytes) return Failure;
            // Decode strictly, including strings in unknown fields: malformed UTF-8 must not be silently repaired.
            var json = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            return Parse(json, installedVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or DecoderFallbackException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure;
        }
    }

    private static UpdateResult Parse(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return Failure;
        string? versionText = null;
        string? pageText = null;
        var versionSeen = false;
        var pageSeen = false;
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.NameEquals("version"))
            {
                if (versionSeen || field.Value.ValueKind != JsonValueKind.String) return Failure;
                versionSeen = true;
                versionText = field.Value.GetString();
            }
            else if (field.NameEquals("downloadUrl"))
            {
                if (pageSeen || field.Value.ValueKind != JsonValueKind.String) return Failure;
                pageSeen = true;
                pageText = field.Value.GetString();
            }
        }

        var parts = versionText?.Split('.');
        if (parts is not { Length: 3 }
            || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var build)
            || !TryPage(pageText, out var page)) return Failure;
        var version = new Version(major, minor, build);
        var current = new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build));
        return version > current ? new(UpdateState.Available, version, page) : new(UpdateState.Current, version, null);
    }

    // The browser receives a page URI, never a local path, credentials, a script or a package download.
    internal static bool TryPage(string? text, out Uri? page)
    {
        page = null;
        if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal)) return false;
        var decoded = Uri.UnescapeDataString(text);
        foreach (var c in decoded)
        {
            if (char.IsControl(c) || c == '\\') return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        foreach (var extension in new[] { ".msi", ".exe", ".msix", ".msixbundle", ".zip" })
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
        }
        page = uri;
        return true;
    }
    /// <summary>Releases the reader.</summary>
    public void Dispose() => client.Dispose();
}
