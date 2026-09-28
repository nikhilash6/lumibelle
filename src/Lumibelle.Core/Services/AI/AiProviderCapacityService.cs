using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// Shared across drawers/circuits; only the configured endpoint or credential owns a cached reading.
public sealed class AiProviderCapacityService(IHttpClientFactory clients, IAiSettingsStore settings, TimeProvider clock)
{
    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1);
        public string? Identity;
        public DateTimeOffset? AttemptedUtc;
        public AiProviderCapacity Reading = new();
    }
    private readonly Slot _comfy = new(), _router = new();
    public async Task<AiProviderCapacity> ReadAsync(AiBackend backend, bool force = false, CancellationToken ct = default)
    {
        if (backend is not (AiBackend.ComfyUI or AiBackend.OpenRouter)) throw new ArgumentOutOfRangeException(nameof(backend));
        var slot = backend == AiBackend.ComfyUI ? _comfy : _router;
        var requested = clock.GetUtcNow();
        await slot.Gate.WaitAsync(ct);
        try
        {
            var config = await settings.LoadAsync(ct);
            var key = backend == AiBackend.OpenRouter ? await settings.ReadOpenRouterKeyAsync(ct) : null;
            var identity = backend == AiBackend.ComfyUI ? AiProviderRegistry.NormalizeComfyUrl(config.ComfyUrl) :
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? "")));
            if (slot.Identity != identity) { slot.Identity = identity; slot.AttemptedUtc = null; slot.Reading = new(); }
            var interval = TimeSpan.FromSeconds(backend == AiBackend.ComfyUI ? 5 : 60);
            if (slot.AttemptedUtc is { } previous && (previous >= requested || !force && clock.GetUtcNow() - previous < interval)) return slot.Reading;
            slot.AttemptedUtc = clock.GetUtcNow();
            try
            {
                if (backend == AiBackend.OpenRouter && string.IsNullOrWhiteSpace(key)) throw new AiGenerationException("Add an OpenRouter API key in Connections to see its allowance.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                using var http = clients.CreateClient(backend.ToString());
                using var request = new HttpRequestMessage(HttpMethod.Get, backend == AiBackend.ComfyUI ? identity.TrimEnd('/') + "/system_stats" : "https://openrouter.ai/api/v1/key");
                if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (!response.IsSuccessStatusCode) throw new AiGenerationException($"Capacity check failed (HTTP {(int)response.StatusCode}).");
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(linked.Token), cancellationToken: linked.Token);
                slot.Reading = backend == AiBackend.ComfyUI ? new(clock.GetUtcNow(), Devices: ReadDevices(json.RootElement)) :
                    new(clock.GetUtcNow(), Key: ReadKey(json.RootElement));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { slot.Reading = slot.Reading with { Error = "Capacity check timed out." }; }
            catch (Exception e) when (e is HttpRequestException or JsonException or AiGenerationException or InvalidOperationException or UriFormatException)
            { slot.Reading = slot.Reading with { Error = e is AiGenerationException ? e.Message : "Capacity is unavailable. Check the provider connection." }; }
            return slot.Reading;
        }
        catch (WorkspaceStoreException e) { return new(Error: e.Message); }
        catch (AiGenerationException e) { return new(Error: e.Message); }
        finally { slot.Gate.Release(); }
    }
    internal static IReadOnlyList<ComfyDeviceCapacity> ReadDevices(JsonElement root)
    {
        var devices = OpenRouterUsageParser.Property(root, "devices");
        if (devices.ValueKind != JsonValueKind.Array) throw new JsonException("Missing devices.");
        return devices.EnumerateArray().Select(device =>
        {
            var total = Integer(device, "vram_total"); var free = Integer(device, "vram_free");
            if (total is null or <= 0 || free is null or < 0 || free > total) { total = null; free = null; }
            var index = Integer(device, "index");
            return new ComfyDeviceCapacity(String(device, "name") ?? "Device", String(device, "type") ?? "unknown",
                index is >= 0 and <= int.MaxValue ? (int)index : null, total, free);
        }).ToArray();
    }
    internal static OpenRouterKeyCapacity ReadKey(JsonElement root)
    {
        var data = OpenRouterUsageParser.Property(root, "data");
        if (data.ValueKind != JsonValueKind.Object) throw new JsonException("Missing key details.");
        var limitValue = OpenRouterUsageParser.Property(data, "limit"); var limit = OpenRouterUsageParser.Money(limitValue);
        return new(limitValue.ValueKind == JsonValueKind.Null || limit is not null, limit, Money(data, "limit_remaining"), String(data, "limit_reset"),
            Money(data, "usage"), Money(data, "usage_daily"), Money(data, "usage_weekly"), Money(data, "usage_monthly"));
    }
    private static decimal? Money(JsonElement value, string name) => OpenRouterUsageParser.Money(OpenRouterUsageParser.Property(value, name));
    private static long? Integer(JsonElement value, string name) => OpenRouterUsageParser.Property(value, name) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out var number) ? number : null;
    private static string? String(JsonElement value, string name) => OpenRouterUsageParser.Property(value, name) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;
}
