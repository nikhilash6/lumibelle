namespace lumibelle.Models;

public sealed record CodexImageCallTiming(string Id, DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc = null);
public sealed record CodexImageDurations(TimeSpan Preparation, TimeSpan ImageTool, TimeSpan FollowUp);

// Elapsed activity intervals, including provider waiting and transport, not model compute time.
public sealed record CodexImageTiming(DateTimeOffset StartedUtc, IReadOnlyList<CodexImageCallTiming> ImageCalls,
    DateTimeOffset? CompletedUtc = null)
{
    public CodexImageDurations? DurationsAt(DateTimeOffset now)
    {
        if (ImageCalls.Any(c => c.StartedUtc is null || CompletedUtc is not null && c.CompletedUtc is null)) return null;
        var end = CompletedUtc ?? now;
        if (end < StartedUtc) end = StartedUtc;
        var calls = ImageCalls.OrderBy(c => c.StartedUtc).ToArray();
        var first = calls.Length == 0 ? end : Clamp(calls[0].StartedUtc!.Value);
        var image = TimeSpan.Zero;
        var coveredUntil = first;
        foreach (var call in calls)
        {
            var start = Clamp(call.StartedUtc!.Value);
            var stop = Clamp(call.CompletedUtc ?? end);
            // Concurrent calls count once in the elapsed breakdown.
            if (start < coveredUntil) start = coveredUntil;
            if (stop > start) image += stop - start;
            if (stop > coveredUntil) coveredUntil = stop;
        }
        var preparation = first - StartedUtc;
        return new(preparation, image, end - StartedUtc - preparation - image);

        DateTimeOffset Clamp(DateTimeOffset value) => value < StartedUtc ? StartedUtc : value > end ? end : value;
    }
}
