using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed record ProgressingChatUpdate(
    ChatResponseUpdate? Response = null,
    GenerationProgress? Progress = null, bool Activity = false, OpenRouterRequestUsage? OpenRouterUsage = null);

public interface IProgressReportingChatClient
{
    IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
