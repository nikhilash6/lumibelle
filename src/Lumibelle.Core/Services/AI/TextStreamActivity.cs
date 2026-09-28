using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace lumibelle.Services.AI;

internal static class TextStreamActivity
{
    public static bool Reasoning(JsonElement delta) => delta.ValueKind == JsonValueKind.Object &&
        (Nonempty(delta, "reasoning") || Nonempty(delta, "reasoning_content") ||
         delta.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array &&
         details.EnumerateArray().Any(d => Nonempty(d, "text") || Nonempty(d, "summary") || Nonempty(d, "data")));
    private static bool Nonempty(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(text.GetString());
    public static bool Reasoning(ChatResponseUpdate response)
    {
        if (response.Contents.OfType<TextReasoningContent>().Any(c => !string.IsNullOrEmpty(c.Text))) return true;
        // The OpenAI adapter does not expose OpenRouter's extension fields as answer content.
        // Its raw model retains those fields, so use them solely as an activity signal.
        if (response.RawRepresentation is not StreamingChatCompletionUpdate raw) return false;
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(raw));
        return json.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            choices.EnumerateArray().Any(c => c.TryGetProperty("delta", out var delta) && Reasoning(delta));
    }
}
