using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace lumibelle.Services.AI;

internal static class OpenRouterUsageParser
{
    internal static decimal? Money(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0 &&
            (amount != 0 || !text!.Split('e', 'E')[0].Any(c => c is >= '1' and <= '9')) ? amount : null;
    }
    internal static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    private static string? Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;
    private static long? Count(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out var count) && count >= 0 ? count : null;
    internal static OpenRouterRequestUsage Read(JsonElement root)
    {
        var usage = Property(root, "usage");
        return new(Text(root, "id"), Text(root, "model"), Text(root, "provider"), Count(usage, "prompt_tokens"), Count(usage, "completion_tokens"),
            Count(Property(usage, "completion_tokens_details"), "reasoning_tokens"), Count(Property(usage, "prompt_tokens_details"), "cached_tokens"), Money(Property(usage, "cost")));
    }
    internal static OpenRouterRequestUsage? Read(ChatResponseUpdate update)
    {
        if (update.RawRepresentation is not StreamingChatCompletionUpdate raw) return null;
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(raw));
        return Read(json.RootElement);
    }
    internal static OpenRouterRequestUsage Merge(OpenRouterRequestUsage? previous, OpenRouterRequestUsage next) => new(
        next.GenerationId ?? previous?.GenerationId, next.Model ?? previous?.Model, next.Provider ?? previous?.Provider,
        next.InputTokens ?? previous?.InputTokens, next.OutputTokens ?? previous?.OutputTokens, next.ReasoningTokens ?? previous?.ReasoningTokens,
        next.CachedTokens ?? previous?.CachedTokens, next.Cost ?? previous?.Cost);
}
