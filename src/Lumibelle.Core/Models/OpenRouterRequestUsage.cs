namespace lumibelle.Models;

public sealed record OpenRouterRequestUsage(string? GenerationId = null, string? Model = null, string? Provider = null,
    long? InputTokens = null, long? OutputTokens = null, long? ReasoningTokens = null, long? CachedTokens = null, decimal? Cost = null);
