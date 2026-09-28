using System.Runtime.CompilerServices;
using System.Text;
using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed class CodexChatClient(ICodexClient codex, CodexSettings settings, string model) : IChatClient, IProgressReportingChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) text.Append(update.Text);
        return new(new ChatMessage(ChatRole.Assistant, text.ToString())) { ModelId = model, FinishReason = ChatFinishReason.Stop };
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetStreamingResponseWithProgressAsync(messages, options, cancellationToken))
            if (update.Response is not null) yield return update.Response;
    }
    public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var captured = messages.Select(AiTextMessage.Capture).ToArray();
        var effort = settings.TextEffort;
        if (options?.AdditionalProperties is { } properties && properties.TryGetValue(TextGenerationOptions.ReasoningEffortKey, out var overrideEffort))
            effort = overrideEffort as string;
        var selection = CodexClient.Capture(await codex.CheckAsync(settings, cancellationToken), model, effort);
        var directory = Path.Combine(Path.GetTempPath(), "lumibelle-codex", Guid.NewGuid().ToString("N"));
        await foreach (var update in codex.GenerateAsync(new(settings, selection, directory, captured), cancellationToken))
        {
            if (update.Activity || update.Progress is not null) yield return new(Progress: update.Progress, Activity: update.Activity);
            if (update.Text is not null) yield return new(new(ChatRole.Assistant, update.Text) { ModelId = model });
            if (update.Complete) yield return new(new(ChatRole.Assistant, "") { ModelId = model, FinishReason = ChatFinishReason.Stop });
        }
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
