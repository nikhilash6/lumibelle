using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.Assets;

public interface IComfyLoraCatalog
{
    Task<ComfyLoraCheck> CheckAsync(AiSettings settings, CancellationToken cancellationToken = default);
}

public sealed class ComfyLoraCatalog(IHttpClientFactory clients) : IComfyLoraCatalog
{
    public async Task<ComfyLoraCheck> CheckAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = clients.CreateClient("ComfyUI");
            using var response = await http.GetAsync(settings.ComfyUrl.TrimEnd('/') + "/object_info/LoraLoaderModelOnly", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return Parse(json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "The LoRA catalog check timed out. Refresh to retry.", []); }
        catch (HttpRequestException)
        { return new(false, "Couldn’t read the ComfyUI LoRA catalog. Check the connection and refresh.", []); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { return new(false, "ComfyUI returned an unsupported LoRA loader catalog. Update ComfyUI and refresh.", []); }
    }

    public static ComfyLoraCheck Parse(JsonElement root)
    {
        try { return ParseCore(root); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { return new(false, "ComfyUI returned an unsupported LoRA loader catalog. Update ComfyUI and refresh.", []); }
    }
    private static ComfyLoraCheck ParseCore(JsonElement root)
    {
        if (!root.TryGetProperty("LoraLoaderModelOnly", out var node) || !node.TryGetProperty("input", out var input) ||
            !input.TryGetProperty("required", out var required) || !required.TryGetProperty("model", out _) ||
            !required.TryGetProperty("lora_name", out var names) || !required.TryGetProperty("strength_model", out var strength))
            return new(false, "Update ComfyUI: LoraLoaderModelOnly needs model, lora_name, and strength_model inputs.", []);
        if (required.GetProperty("model")[0].GetString() != "MODEL" || strength[0].GetString() != "FLOAT")
            return new(false, "Update ComfyUI: the model-only LoRA loader has incompatible input types.", []);
        var options = names[0];
        if (options.ValueKind != JsonValueKind.Array && names.GetArrayLength() > 1 && names[1].TryGetProperty("options", out var alternate)) options = alternate;
        var min = strength[1].GetProperty("min").GetSingle(); var max = strength[1].GetProperty("max").GetSingle();
        if (!float.IsFinite(min) || !float.IsFinite(max) || Math.Max(-100, min) > Math.Min(100, max)) return new(false, "The LoRA loader strength limits are invalid.", []);
        return new(true, "Installed LoRA files refreshed. Workflow assignments are provided by you; compatibility is not tested.",
            options.EnumerateArray().Select(item => item.GetString()!).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Math.Max(-100, min), Math.Min(100, max));
    }
}
