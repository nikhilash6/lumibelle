using System.Globalization;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class OpenRouterModelMetadata
{
    // PopularityOrder is the position in OpenRouter's most-popular catalog response, not a reported token count.
    public static AiModelCatalogInfo Read(JsonElement model, int? popularityOrder = null)
    {
        var pricing = Object(model, "pricing");
        var additional = new Dictionary<string, decimal?>();
        foreach (var key in new[] { "request", "image", "web_search", "internal_reasoning", "input_cache_read", "input_cache_write" })
            if (pricing.TryGetProperty(key, out var value)) additional[key] = Money(value);
        var id = Text(model, "id");
        var variable = id == "openrouter/auto" || Negative(pricing, "prompt") || Negative(pricing, "completion");
        var conditional = pricing.TryGetProperty("overrides", out var overrides) && overrides.ValueKind == JsonValueKind.Array && overrides.GetArrayLength() > 0;
        var reasoning = model.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array &&
            parameters.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() is "reasoning" or "reasoning_effort" or "include_reasoning");
        var hasReasoning = model.TryGetProperty("reasoning", out var reasoningInfo) && reasoningInfo.ValueKind == JsonValueKind.Object;
        return new(Text(model, "description"), PositiveInteger(model, "context_length"),
            PositiveInteger(Object(model, "top_provider"), "max_completion_tokens"), reasoning || hasReasoning,
            new(Price(pricing, "prompt"), Price(pricing, "completion"), additional, variable, conditional), Added(model), popularityOrder)
        {
            SupportedParameters = Strings(model, "supported_parameters"),
            // An omitted effort field inside a reasoning object means no effort selector;
            // an explicit null means all gateway effort values are accepted.
            SupportedReasoningEfforts = hasReasoning
                ? reasoningInfo.TryGetProperty("supported_efforts", out _) ? Strings(reasoningInfo, "supported_efforts") : Array.Empty<string>()
                : null,
            SupportsReasoningBudget = hasReasoning ? True(reasoningInfo, "supports_max_tokens") : null,
            ReasoningMandatory = hasReasoning ? True(reasoningInfo, "mandatory") : null
        };
    }

    public static string PerMillion(decimal? value) => value is null ? "Not reported" : "$" + (value.Value * 1_000_000m).ToString("0.############################", CultureInfo.InvariantCulture) + " / 1M";
    public static string Context(long value) => value >= 1_000_000 ? (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M"
        : value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K" : value.ToString(CultureInfo.InvariantCulture);
    public static string AdditionalPrice(string key, decimal? value) => key is "internal_reasoning" or "input_cache_read" or "input_cache_write"
        ? PerMillion(value) + " tokens" : value is null ? "Not reported" : "$" + value.Value.ToString("0.############################", CultureInfo.InvariantCulture) + (key == "image" ? " / image" : key == "web_search" ? " / search" : " / request");
    public static string AdditionalLabel(string key) => key switch
    { "request" => "Per request", "image" => "Image input", "web_search" => "Web search", "internal_reasoning" => "Reasoning tokens", "input_cache_read" => "Cache read", "input_cache_write" => "Cache write", _ => key };
    public static string ModelUrl(string id) => "https://openrouter.ai/" + string.Join('/', id.Split('/').Select(Uri.EscapeDataString));

    private static IReadOnlyList<string>? Strings(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array
            ? item.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : null;
    private static bool True(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static JsonElement Object(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object ? item : Empty;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static long? PositiveInteger(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number > 0 ? number : null;
    private static DateTimeOffset? Added(JsonElement model) => PositiveInteger(model, "created") is { } seconds && seconds <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    private static decimal? Price(JsonElement value, string key) => value.TryGetProperty(key, out var item) ? Money(item) : null;
    private static bool Negative(JsonElement value, string key) => value.TryGetProperty(key, out var item) && Number(item) is < 0;
    private static decimal? Money(JsonElement value) => Number(value) is { } number && number >= 0 && number <= decimal.MaxValue / 1_000_000m ? number : null;
    private static decimal? Number(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        // Decimal underflow must not turn a small positive catalog rate into a free rate.
        if (number == 0 && text!.Split('e', 'E')[0].Any(c => c is >= '1' and <= '9')) return null;
        return number;
    }
}
