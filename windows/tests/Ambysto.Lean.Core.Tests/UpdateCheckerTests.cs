using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests;

// Independent acceptance tests from intent/2026-10-23-asp-11-about-updates.md; no live networking.
[TestClass]
public sealed class UpdateCheckerTests
{
    private const string Page = "https://lean.ambysto.com/windows/";
    private static string Manifest(string version, string page = Page) =>
        System.Text.Json.JsonSerializer.Serialize(new { version, downloadUrl = page });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://lean.ambysto.com/windows/latest.json", request.RequestUri!.AbsoluteUri);
            Assert.IsNull(request.Content, "GET has no request body");
            Assert.IsFalse(request.Headers.Contains("Authorization"));
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.Headers.Contains("Proxy-Authorization"));
            foreach (var header in request.Headers)
            {
                Assert.IsTrue(header.Key is "Accept" or "User-Agent" or "Accept-Encoding", "unexpected private/custom header: " + header.Key);
            }
            return send(request, token);
        }
    }

    private static Handler Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
    private static void Failed(UpdateResult result)
    {
        Assert.AreEqual(UpdateState.Failed, result.State);
        Assert.IsNull(result.Version);
        Assert.IsNull(result.DownloadPage);
    }

    [TestMethod]
    public async Task ConstructionIsPassiveAndEveryCallMakesOneFixedGet()
    {
        using var handler = Respond(Manifest("1.10.1"));
        using var checker = new UpdateChecker(handler);
        Assert.AreEqual(0, handler.Calls);
        Assert.AreEqual("https://lean.ambysto.com/windows/latest.json", UpdateChecker.FeedUri.AbsoluteUri);
        Assert.AreEqual(TimeSpan.FromSeconds(10), UpdateChecker.RequestTimeout);
        for (var i = 1; i <= 2; i++)
        {
            var result = await checker.CheckAsync(new Version(1, 9, 999), CancellationToken.None);
            Assert.AreEqual(i, handler.Calls);
            Assert.AreEqual(UpdateState.Available, result.State);
            Assert.AreEqual(new Version(1, 10, 1), result.Version);
            Assert.AreEqual(new Uri(Page), result.DownloadPage);
        }
    }

    [TestMethod]
    [DataRow("1.10.1", "1.10.1", false)]
    [DataRow("1.9.999", "1.10.1", false)]
    [DataRow("1.10.1", "1.9.999", true)]
    [DataRow("2.0.0", "1.65535.65535", true)]
    [DataRow("0.0.0", "0.0.0", false)]
    [DataRow("65535.65535.65535", "65535.65535.65534", true)]
    public async Task VersionOrderingIsNumeric(string remote, string installed, bool available)
    {
        using var checker = new UpdateChecker(Respond(Manifest(remote)));
        var result = await checker.CheckAsync(Version.Parse(installed), CancellationToken.None);
        Assert.AreEqual(available ? UpdateState.Available : UpdateState.Current, result.State);
        Assert.AreEqual(Version.Parse(remote), result.Version);
        Assert.AreEqual(available ? new Uri(Page) : null, result.DownloadPage);
    }

    [TestMethod]
    [DataRow("1.2")]
    [DataRow("1.2.3.4")]
    [DataRow("-1.2.3")]
    [DataRow("+1.2.3")]
    [DataRow("1.2.65536")]
    [DataRow("65536.0.0")]
    [DataRow("1. 2.3")]
    [DataRow(" 1.2.3")]
    [DataRow("1.2.3 ")]
    [DataRow("1.2.٣")]
    [DataRow("1..3")]
    [DataRow("")]
    public async Task InvalidVersionsFailClosed(string version)
    {
        using var checker = new UpdateChecker(Respond(Manifest(version)));
        Failed(await checker.CheckAsync(new Version(9, 0, 0), CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("broken")]
    [DataRow("{\"version\":\"1.2.3\"}")]
    [DataRow("{\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"Version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":\"1.2.3\",\"DownloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":12,\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":null,\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":\"1.2.3\",\"downloadUrl\":true}")]
    [DataRow("{\"version\":\"1.2.3\",\"downloadUrl\":\"\"}")]
    [DataRow("{\"version\":\"1.2.3\",\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/\",\"downloadUrl\":\"https://example.com/\"}")]
    [DataRow("{\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/\",}")]
    // JSON escape text is preserved literally: these are not replacement characters encoded by StringContent.
    [DataRow("""{"version":"\uD800","downloadUrl":"https://example.com/"}""")]
    [DataRow("""{"version":"\uDC00","downloadUrl":"https://example.com/"}""")]
    [DataRow("""{"version":"1.2.\uD800x","downloadUrl":"https://example.com/"}""")]
    [DataRow("""{"version":"1.2.3","downloadUrl":"https://example.com/\uD800"}""")]
    [DataRow("""{"version":"1.2.3","downloadUrl":"https://example.com/\uDC00"}""")]
    [DataRow("""{"version":"1.2.3","downloadUrl":"https://example.com/\uD800x"}""")]
    public async Task MalformedMissingDuplicateOrWrongTypeFieldsFail(string body)
    {
        using var checker = new UpdateChecker(Respond(body));
        Failed(await checker.CheckAsync(new Version(9, 0, 0), CancellationToken.None));
    }

    [TestMethod]
    public async Task UnknownFieldsAreIgnored()
    {
        using var checker = new UpdateChecker(Respond("{\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/page\",\"future\":{\"anything\":[1,true]}}"));
        Assert.AreEqual(UpdateState.Available, (await checker.CheckAsync(new Version(0, 0, 0), CancellationToken.None)).State);
    }

    [TestMethod]
    [DataRow("http://example.com/")]
    [DataRow("file:///C:/update.msi")]
    [DataRow("javascript:alert(1)")]
    [DataRow("/windows/")]
    [DataRow("https:///windows/")]
    [DataRow("https://user:pass@example.com/")]
    [DataRow("https://example.com:444/")]
    [DataRow("https://example.com/\\page")]
    [DataRow("https://example.com/\npage")]
    [DataRow("https://example.com/\tpage")]
    [DataRow("https://example.com/update.MSI")]
    [DataRow("https://example.com/update.exe?token=1")]
    [DataRow("https://example.com/update.msix#download")]
    [DataRow("https://example.com/update.msixbundle")]
    [DataRow("https://example.com/update.zip")]
    // Independent review: validate the decoded path too, before query/fragment can obscure a package suffix.
    [DataRow("https://example.com/page%00name")]
    [DataRow("https://example.com/page%09name")]
    [DataRow("https://example.com/page%0Aname")]
    [DataRow("https://example.com/page%0dname")]
    [DataRow("https://example.com/page%1fname")]
    [DataRow("https://example.com/page%7fname")]
    [DataRow("https://example.com/%5Cpage")]
    [DataRow("https://example.com/%5cpage")]
    [DataRow("https://example.com/update%2eMsI?download=1#release")]
    [DataRow("https://example.com/update.%65Xe?download=1")]
    [DataRow("https://example.com/update%2EMSIX#release")]
    [DataRow("https://example.com/update.mSiXbUnDlE?download=1#release")]
    [DataRow("https://example.com/update%2e%6d%73%69%78%62%75%6e%64%6c%65?download=1")]
    [DataRow("https://example.com/update.ZiP#release")]
    [DataRow("https://example.com/update%2e%7a%69%70?download=1#release")]
    public async Task UnsafePagesAreValidatedEvenForOlderVersions(string page)
    {
        using var checker = new UpdateChecker(Respond(Manifest("0.0.0", page)));
        Failed(await checker.CheckAsync(new Version(9, 0, 0), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(201)] [DataRow(204)] [DataRow(301)] [DataRow(302)] [DataRow(307)] [DataRow(308)] [DataRow(404)] [DataRow(500)]
    public async Task Only200SucceedsAndRedirectsAreNotRetried(int status)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Manifest("2.0.0")) };
            response.Headers.Location = new Uri("https://example.com/redirect");
            return Task.FromResult(response);
        });
        using var checker = new UpdateChecker(handler);
        Failed(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ManifestByteLimitIncludesUnknownLengthResponses(bool knownLength)
    {
        var body = Manifest("2.0.0");
        var exact = Encoding.UTF8.GetBytes(body.PadRight(UpdateChecker.MaxManifestBytes));
        foreach (var length in new[] { exact.Length, exact.Length + 1, 1024 * 1024 })
        {
            var bytes = new byte[length];
            Array.Copy(exact, bytes, exact.Length);
            if (length > exact.Length) Array.Fill(bytes, (byte)' ', exact.Length, length - exact.Length);
            using var stream = new NonSeekable(bytes);
            using var handler = new Handler((_, _) =>
            {
                HttpContent content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(stream);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            using var checker = new UpdateChecker(handler);
            var result = await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None);
            if (length == exact.Length) Assert.AreEqual(UpdateState.Available, result.State);
            else Failed(result);
            Assert.IsTrue(stream.BytesRead <= UpdateChecker.MaxManifestBytes + 1, "unknown-length reading must stop at the limit, allowing one overflow-detection byte");
        }
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private int position;
        public int BytesRead => position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - position);
            Array.Copy(bytes, position, buffer, offset, read);
            position += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task InvalidUtf8FailsClosed()
    {
        var prefix = Encoding.UTF8.GetBytes("{\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/\",\"extra\":\"");
        var bytes = new byte[prefix.Length + 3];
        Array.Copy(prefix, bytes, prefix.Length);
        bytes[^3] = 0xff;
        bytes[^2] = (byte)'"';
        bytes[^1] = (byte)'}';
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var checker = new UpdateChecker(handler);
        Failed(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
    }

    private sealed class InterruptedBody(bool hang) : Stream
    {
        public bool Cancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!hang) throw new IOException("body interrupted");
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("body interrupted");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReadErrorsAndDeadlineAlsoApplyAfterResponseHeaders(bool hang)
    {
        using var body = new InterruptedBody(hang);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var checker = new UpdateChecker(handler);
        Failed(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        if (hang) Assert.IsTrue(body.Cancelled, "total deadline cancels body reading too");
    }

    [TestMethod]
    public async Task NetworkIoAndNonCallerCancellationFailQuietly()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("offline"), new IOException("read failed"), new TaskCanceledException("transport cancelled") })
        {
            using var checker = new UpdateChecker(new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure)));
            Failed(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndCancelsTheTransport()
    {
        using var source = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) => { source.Cancel(); await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        using var checker = new UpdateChecker(handler);
        try { await checker.CheckAsync(new Version(1, 0, 0), source.Token); Assert.Fail("caller cancellation was swallowed"); }
        catch (OperationCanceledException) { Assert.IsTrue(source.IsCancellationRequested); }
    }

    [TestMethod]
    public async Task DeadlineCancelsAHungTransportAndReturnsFailed()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        using var checker = new UpdateChecker(handler);
        var clock = Stopwatch.StartNew();
        Failed(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(8), "deadline fired much earlier than ten seconds");
        Assert.AreEqual(1, handler.Calls);
    }
}
