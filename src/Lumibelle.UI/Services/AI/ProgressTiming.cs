using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class ProgressTiming
{
    public static string Duration(TimeSpan value)
    {
        var seconds = (long)Math.Clamp(Math.Ceiling(value.TotalSeconds), 0, long.MaxValue);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";
    }
    public static string? Estimate(GenerationProgress? progress, DateTimeOffset now)
    {
        if (progress is not { LiveUpdatesAvailable: true, EstimatedRemaining: { } remaining,
            EstimateObservedUtc: { } observed, EstimateExpiresUtc: { } expires, EstimateScope: { Length: > 0 } scope } || now > expires || now < observed) return null;
        var left = remaining - (now - observed);
        return left > TimeSpan.Zero ? $"≈{Duration(left)} left in {scope}" : null;
    }
    public static string ForJob(AiJobHeader job, GenerationProgress? progress, DateTimeOffset now)
    {
        if (job.State == AiJobState.Waiting) return $"{Duration(now - job.CreatedUtc)} waiting";
        if (job.State != AiJobState.Running && !job.RemoteUnconfirmed) return "";
        var elapsed = Duration(now - (job.StartedUtc ?? job.CreatedUtc)) + " elapsed";
        return job.State == AiJobState.Running && Estimate(progress, now) is { } estimate ? elapsed + " · " + estimate : elapsed;
    }
}
