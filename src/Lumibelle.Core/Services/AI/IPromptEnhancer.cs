using lumibelle.Models;

namespace lumibelle.Services.AI;

public interface IPromptEnhancer
{
    IAsyncEnumerable<PromptEnhancementUpdate> EnhanceAsync(PromptEnhancementRequest request, CancellationToken cancellationToken = default);
    Task ValidateInputsAsync(PromptEnhancementContext context, CancellationToken cancellationToken = default);
}
