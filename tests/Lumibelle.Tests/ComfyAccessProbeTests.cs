using System.Net;
using System.Text;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class ComfyAccessProbeTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProbeUsesSavedCredentialsForHttpAndTheRealWebSocketHandshakeWithoutGenerating()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example:8443/prefix", "id", "secret", 0, Ct);
        var paths = new List<string>();
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            ComfyAccessTestHttp.AssertPair(request, "id", "secret");
            Assert.Equal(HttpMethod.Get, request.Method);
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/ws", StringComparison.Ordinal)
                ? ComfyAccessTestHttp.Upgrade(request) : ComfyAccessTestHttp.Json());
        }));
        var probe = new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients));
        var result = await probe.CheckAsync("https://comfy.example:8443/prefix", Ct);
        Assert.True(result.HttpSucceeded); Assert.True(result.WebSocketSucceeded);
        Assert.Equal(new[] { "/prefix/system_stats", "/prefix/ws" }, paths);
    }

    [Fact]
    public async Task HttpFailureDoesNotAttemptTheSocketAndDoesNotEchoTheResponseBody()
    {
        using var f = new ComfyAccessFixture();
        var calls = 0;
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("reflected-secret") });
        }));
        var probe = new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients));
        var result = await probe.CheckAsync("https://comfy.example", Ct);
        Assert.False(result.HttpSucceeded); Assert.False(result.WebSocketSucceeded);
        Assert.Equal(1, calls); Assert.Contains("Access", result.HttpMessage);
        Assert.DoesNotContain("reflected-secret", result.ToString());
    }

    [Fact]
    public async Task SocketFailureIsReportedSeparatelyFromWorkingHttp()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        var calls = 0;
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (request, _) =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/ws"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) : ComfyAccessTestHttp.Json());
        }));
        var result = await new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients)).CheckAsync("https://comfy.example", Ct);
        Assert.True(result.HttpSucceeded); Assert.False(result.WebSocketSucceeded);
        Assert.Equal(2, calls); Assert.Contains("Service Auth", result.WebSocketMessage);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"system\":{}}")]
    [InlineData("{\"system\":null,\"devices\":[]}")]
    [InlineData("not-json")]
    public async Task NonComfyResponsesDoNotPassTheCheck(string body)
    {
        using var f = new ComfyAccessFixture();
        var calls = 0;
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (_, _) =>
        { calls++; return Task.FromResult(ComfyAccessTestHttp.Json(body)); }));
        var result = await new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients)).CheckAsync("https://comfy.example", Ct);
        Assert.False(result.HttpSucceeded); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedDiagnosticResponseIsRejectedBeforeJsonParsing()
    {
        using var f = new ComfyAccessFixture();
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, (_, _) =>
            Task.FromResult(ComfyAccessTestHttp.Json(new string('x', 1024 * 1024 + 1)))));
        var result = await new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients)).CheckAsync("https://comfy.example", Ct);
        Assert.False(result.HttpSucceeded); Assert.Contains("large", result.HttpMessage);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAnAuthenticationFailure()
    {
        using var f = new ComfyAccessFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(f.Store, async (_, ct) =>
        {
            cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct);
            return ComfyAccessTestHttp.Json();
        }));
        var probe = new ComfyAccessProbe(clients, new AccessComfyWebSocketFactory(clients));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync("https://comfy.example", cancellation.Token));
    }
}
