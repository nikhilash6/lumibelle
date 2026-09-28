using System.Runtime.CompilerServices;
using System.Text;
using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed class ClaudeCodeChatClient(IClaudeCodeClient claude, ClaudeCodeSettings settings, string model) : IChatClient, IProgressReportingChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder(); ChatFinishReason? finish = null; var resolved = model;
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        { text.Append(update.Text); finish = update.FinishReason ?? finish; resolved = update.ModelId ?? resolved; }
        return new(new ChatMessage(ChatRole.Assistant, text.ToString())) { ModelId = resolved, FinishReason = finish };
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetStreamingResponseWithProgressAsync(messages, options, cancellationToken))
            if (update.Response is not null) yield return update.Response;
    }
    public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var captured = messages.Select(AiTextMessage.Capture).ToArray();
        // Presence with a null value explicitly means the model's default effort.
        var effort = options?.AdditionalProperties is { } properties && properties.TryGetValue(TextGenerationOptions.ReasoningEffortKey, out var selected)
            ? selected as string : settings.TextEffort;
        var directory = Path.Combine(Path.GetTempPath(), "lumibelle-claude-code", Guid.NewGuid().ToString("N"));
        var resolved = model;
        try
        {
            await foreach (var update in claude.GenerateAsync(new(settings, model, effort, directory, captured), cancellationToken))
            {
                resolved = update.Model ?? resolved;
                if (update.Activity || update.Progress is not null) yield return new(Progress: update.Progress, Activity: update.Activity);
                if (update.Text is not null) yield return new(new(ChatRole.Assistant, update.Text) { ModelId = resolved });
                if (update.Complete) yield return new(new(ChatRole.Assistant, "") { ModelId = resolved, FinishReason = update.Truncated ? ChatFinishReason.Length : ChatFinishReason.Stop });
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
