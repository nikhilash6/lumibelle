using System.Net;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public class ProviderCapacityTests
{
    [Fact]
    public void DevicesRemainSeparateAndInvalidReadingsAreUnavailable()
    {
        using var json = JsonDocument.Parse("""{"devices":[{"name":"Primary","type":"cuda","index":0,"vram_total":21474836480,"vram_free":7516192768},{"name":"Secondary","type":"cuda","index":1,"vram_total":8589934592,"vram_free":1073741824},{"name":"CPU","type":"cpu","vram_total":100,"vram_free":50},{"name":"Bad","vram_total":100,"vram_free":101}]}""");
        var devices = AiProviderCapacityService.ReadDevices(json.RootElement);
        Assert.Equal(4, devices.Count); Assert.Equal(20L << 30, devices[0].TotalBytes); Assert.Equal(7L << 30, devices[0].AvailableBytes);
        Assert.Equal(8L << 30, devices[1].TotalBytes); Assert.Equal("cpu", devices[2].Type); Assert.Null(devices[3].AvailableBytes);
    }
    [Theory]
    [InlineData("null", true, null)] [InlineData("0", true, "0")] [InlineData("10.5", true, "10.5")]
    [InlineData("-1", false, null)] [InlineData("\"invalid\"", false, null)]
    public void KeyCapsDistinguishUnlimitedZeroAndMalformed(string limit, bool known, string? expected)
    {
        using var json = JsonDocument.Parse("{\"data\":{\"limit\":" + limit + ",\"limit_remaining\":0,\"usage\":0.000001}}");
        var key = AiProviderCapacityService.ReadKey(json.RootElement);
        Assert.Equal(known, key.LimitKnown); Assert.Equal(expected, key.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, key.Remaining); Assert.Equal(0.000001m, key.Usage);
    }
    [Fact]
    public async Task ReadOnlyCapacityIsCachedAndInvalidatesAcrossCredentialsAndServers()
    {
        var clock = new CapacityClock(); var settings = new FakeAiSettingsStore();
        var fail = false;
        using var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.AbsolutePath == "/system_stats" ? """{"devices":[{"name":"GPU","vram_total":100,"vram_free":40}]}""" : """{"data":{"limit":10,"limit_remaining":4,"usage":6}}""") }));
        var service = new AiProviderCapacityService(new TestHttpFactory(handler), settings, clock);
        var router = await service.ReadAsync(AiBackend.OpenRouter, ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, router.Key!.Remaining);
        await service.ReadAsync(AiBackend.OpenRouter, ct: TestContext.Current.CancellationToken); Assert.Single(handler.Requests);
        clock.Now += TimeSpan.FromSeconds(61); fail = true;
        var stale = await service.ReadAsync(AiBackend.OpenRouter, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(stale.Error); Assert.Equal(router.CheckedUtc, stale.CheckedUtc); Assert.Equal(4, stale.Key!.Remaining);
        settings.Key = "different-test-key";
        var invalid = await service.ReadAsync(AiBackend.OpenRouter, ct: TestContext.Current.CancellationToken); Assert.Null(invalid.Key); Assert.Null(invalid.CheckedUtc);
        fail = false; await service.ReadAsync(AiBackend.ComfyUI, ct: TestContext.Current.CancellationToken);
        settings.Value = settings.Value with { ComfyUrl = "http://new-server:8188" };
        await service.ReadAsync(AiBackend.ComfyUI, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, r => { Assert.Equal("GET", r.Method); Assert.Contains(r.Path, new[] { "/api/v1/key", "/system_stats" }); });
    }
    private sealed class CapacityClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task ConcurrentReadersShareOneRequestAndMissingDataDoesNotBecomeZero()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHttpHandler(async (_, ct) =>
        {
            received.TrySetResult(); await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":{}}""") };
        });
        var service = new AiProviderCapacityService(new TestHttpFactory(handler), new FakeAiSettingsStore(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        var first = service.ReadAsync(AiBackend.OpenRouter, ct: ct); await received.Task.WaitAsync(ct);
        var second = service.ReadAsync(AiBackend.OpenRouter, ct: ct); release.SetResult();
        var readings = await Task.WhenAll(first, second);
        Assert.Single(handler.Requests);
        Assert.All(readings, r => { Assert.False(r.Key!.LimitKnown); Assert.Null(r.Key.Remaining); Assert.Null(r.Key.Usage); });
    }
}
