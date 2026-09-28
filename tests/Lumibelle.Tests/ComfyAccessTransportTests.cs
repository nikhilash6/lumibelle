using System.Net;
using System.Net.WebSockets;
using System.Text;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed class ComfyAccessTransportTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "system_stats")]
    [InlineData("GET", "object_info")]
    [InlineData("POST", "upload/image")]
    [InlineData("POST", "prompt")]
    [InlineData("GET", "history/test-prompt")]
    [InlineData("GET", "view?filename=result.mp4&type=output")]
    [InlineData("POST", "queue")]
    [InlineData("POST", "interrupt")]
    public async Task EveryComfyRouteGetsThePairWithoutChangingItsPayload(string method, string path)
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id.access", "secret", 0, Ct);
        var payload = new byte[1024]; Random.Shared.NextBytes(payload);
        using var http = ComfyAccessTestHttp.Client(f.Store, async (request, ct) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id.access", "secret");
            Assert.Equal(payload, await request.Content!.ReadAsByteArrayAsync(ct));
            return ComfyAccessTestHttp.Json();
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://comfy.example/" + path) { Content = new ByteArrayContent(payload) };
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ComfyAccessTestHttp.AssertNoPair(request);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("https://elsewhere.example/view")]
    [InlineData("https://comfy.example:8443/view")]
    [InlineData("https://comfy.example.attacker.test/view")]
    [InlineData("http://comfy.example/view")]
    public async Task StaleCallerHeadersCannotLeakToAnotherOriginOrInsecureRequest(string target)
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            ComfyAccessTestHttp.AssertNoPair(request);
            return Task.FromResult(ComfyAccessTestHttp.Json());
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Add(ComfyAccessTransport.ClientIdHeader, "stale-id");
        request.Headers.Add(ComfyAccessTransport.ClientSecretHeader, "stale-secret");
        using var response = await http.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ReusedClientSeesRotationAndRemovalOnTheNextRequest()
    {
        using var f = new ComfyAccessFixture();
        var snapshot = await f.Store.SaveAsync("https://comfy.example", "id", "old", 0, Ct);
        var observed = new List<string?>();
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            observed.Add(request.Headers.TryGetValues(ComfyAccessTransport.ClientSecretHeader, out var values) ? values.Single() : null);
            return Task.FromResult(ComfyAccessTestHttp.Json());
        });
        using (await http.GetAsync("https://comfy.example/queue", Ct)) { }
        snapshot = await f.Store.SaveAsync("https://comfy.example", "id", "rotated", snapshot.Revision, Ct);
        using (await http.GetAsync("https://comfy.example/queue", Ct)) { }
        await f.Store.RemoveAsync("https://comfy.example", snapshot.Revision, Ct);
        using (await http.GetAsync("https://comfy.example/queue", Ct)) { }
        Assert.Equal(new string?[] { "old", "rotated", null }, observed);
    }

    [Fact]
    public async Task ConcurrentOriginsNeverShareHeaders()
    {
        using var f = new ComfyAccessFixture();
        var first = await f.Store.SaveAsync("https://a.example", "a-id", "a-secret", 0, Ct);
        await f.Store.SaveAsync("https://b.example", "b-id", "b-secret", first.Revision, Ct);
        using var http = ComfyAccessTestHttp.Client(f.Store, async (request, ct) =>
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            var name = request.RequestUri!.Host[..1];
            ComfyAccessTestHttp.AssertPair(request, name + "-id", name + "-secret");
            return ComfyAccessTestHttp.Json();
        });
        var calls = Enumerable.Range(0, 20).Select(async n =>
        {
            using var result = await http.GetAsync($"https://{(n % 2 == 0 ? "a" : "b")}.example/queue", Ct);
            Assert.True(result.IsSuccessStatusCode);
        });
        await Task.WhenAll(calls);
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    [InlineData(401)] [InlineData(403)]
    public async Task RedirectsAndRejectedAuthenticationHaveSafeActionableErrors(int status)
    {
        using var f = new ComfyAccessFixture();
        const string secret = "never-echo-this-secret";
        await f.Store.SaveAsync("https://comfy.example", "id", secret, 0, Ct);
        var content = new TrackingContent(secret);
        using var http = ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id", secret);
            var result = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
            result.Headers.Location = new("https://attacker.test/" + secret);
            return Task.FromResult(result);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://comfy.example/object_info");
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => http.SendAsync(request, Ct));
        Assert.Contains("Access", error.Message); Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain("attacker", error.ToString()); Assert.True(content.Disposed);
        ComfyAccessTestHttp.AssertNoPair(request);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    public async Task HtmlLoginResponsesAreNotPassedToJsonDecoders(string mediaType)
    {
        using var f = new ComfyAccessFixture();
        using var http = ComfyAccessTestHttp.Client(f.Store, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>SSO login</html>", Encoding.UTF8, mediaType) }));
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => http.GetAsync("https://comfy.example/object_info", Ct));
        Assert.Contains("HTML", error.Message);
    }

    [Fact]
    public async Task StreamingDownloadsAreNotEagerlyReadByAuthentication()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        var stream = new CountReadsStream();
        using var http = ComfyAccessTestHttp.Client(f.Store, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));
        using var response = await http.GetAsync("https://comfy.example/view?filename=large.mp4", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(0, stream.Reads);
        await using var content = await response.Content.ReadAsStreamAsync(Ct);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task CancelledTransportRemovesTemporaryHeaders()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var http = ComfyAccessTestHttp.Client(f.Store, async (request, ct) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id", "secret");
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return ComfyAccessTestHttp.Json();
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://comfy.example/prompt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(request, cancellation.Token));
        ComfyAccessTestHttp.AssertNoPair(request);
    }

    [Fact]
    public void ProductionRegistrationBlocksRedirectsCookiesAndCredentialLogging()
    {
        using var f = new ComfyAccessFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IComfyAccessCredentialStore>(f.Store);
        services.AddComfyAccess(); services.AddHttpClient("OpenRouter");
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AccessComfyWebSocketFactory>(provider.GetRequiredService<IComfyWebSocketFactory>());
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("ComfyUI");
        var accessHandlers = 0;
        while (handler is DelegatingHandler next)
        {
            if (handler is ComfyAccessHandler) accessHandlers++;
            handler = next.InnerHandler!;
        }
        Assert.Equal(1, accessHandlers);
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect); Assert.False(sockets.UseCookies);
        Assert.Null(sockets.SslOptions.RemoteCertificateValidationCallback);
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("ComfyUI");
        Assert.True(options.ShouldRedactHeaderValue(ComfyAccessTransport.ClientIdHeader));
        Assert.True(options.ShouldRedactHeaderValue(ComfyAccessTransport.ClientSecretHeader));
        handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("OpenRouter");
        while (handler is DelegatingHandler next)
        { Assert.IsNotType<ComfyAccessHandler>(handler); handler = next.InnerHandler!; }
    }

    [Fact]
    public async Task RealClientWebSocketHandshakeUsesTheNamedAuthenticatedClientAndReceivesAFrame()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example:8443", "id", "secret", 0, Ct);
        var requests = new List<Uri>();
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id", "secret");
            requests.Add(request.RequestUri!);
            return Task.FromResult(ComfyAccessTestHttp.Upgrade(request));
        }));
        var factory = new AccessComfyWebSocketFactory(clients);
        await using var socket = factory.Create();
        await socket.ConnectAsync(new("wss://comfy.example:8443/prefix/ws?clientId=test"), Ct);
        Assert.Equal(WebSocketState.Open, socket.State);
        var buffer = new byte[16];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), Ct);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("{}", Encoding.UTF8.GetString(buffer, 0, received.Count));
        Assert.Single(requests); Assert.Equal("/prefix/ws", requests[0].AbsolutePath);
    }

    [Fact]
    public async Task WebSocketLoginRedirectIsRejectedWithoutFollowingIt()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        var calls = 0;
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            calls++; ComfyAccessTestHttp.AssertPair(request, "id", "secret");
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new("https://elsewhere.example/login");
            return Task.FromResult(redirect);
        }));
        await using var socket = new AccessComfyWebSocketFactory(clients).Create();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new("wss://comfy.example/ws"), Ct));
        Assert.Equal(1, calls); Assert.NotEqual(WebSocketState.Open, socket.State);
    }

    private sealed class TrackingContent(string value) : StringContent(value)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CountReadsStream : MemoryStream
    {
        public int Reads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return base.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Reads++; return base.ReadAsync(buffer, ct); }
    }
}
