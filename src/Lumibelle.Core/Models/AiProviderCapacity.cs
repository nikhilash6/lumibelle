namespace lumibelle.Models;

public sealed record ComfyDeviceCapacity(string Name, string Type, int? Index, long? TotalBytes, long? AvailableBytes);
public sealed record OpenRouterKeyCapacity(bool LimitKnown, decimal? Limit, decimal? Remaining, string? Reset,
    decimal? Usage, decimal? DailyUsage, decimal? WeeklyUsage, decimal? MonthlyUsage);
public sealed record AiProviderCapacity(DateTimeOffset? CheckedUtc = null, IReadOnlyList<ComfyDeviceCapacity>? Devices = null,
    OpenRouterKeyCapacity? Key = null, string? Error = null);
