namespace lumibelle.Models;

// One ordering policy for dispatch, the activity drawer, and queue-position labels.
// Priority is scheduling metadata on a request, never a provider/model preference.
public static class AiQueueOrder
{
    public static bool IsWaiting(AiJobHeader job) => job.State == AiJobState.Waiting && !job.CancelRequested;

    // OrderByDescending is stable: the saved/manual order remains authoritative
    // inside each priority band. Recovery and provider-capacity guards run separately.
    public static AiJobHeader[] Waiting(IEnumerable<AiJobHeader> jobs, AiBackend provider) =>
        jobs.Where(j => j.Backend == provider && IsWaiting(j)).OrderByDescending(j => j.Priority).ToArray();

    public static AiJobHeader[] Band(IEnumerable<AiJobHeader> jobs, AiJobHeader job) =>
        Waiting(jobs, job.Backend).Where(j => j.Priority == job.Priority).ToArray();

    public static bool IsNext(IEnumerable<AiJobHeader> jobs, AiJobHeader job) =>
        Waiting(jobs, job.Backend).FirstOrDefault()?.Id == job.Id;

    // Reorder only waiting slots within each provider. Running, reconnecting and
    // historical rows keep their places, as do slots belonging to other providers.
    // Call before project filtering/pagination; a filter must not affect queue order.
    public static IReadOnlyList<AiJobHeader> ForDisplay(IReadOnlyList<AiJobHeader> jobs)
    {
        var waiting = jobs.Where(IsWaiting).GroupBy(j => j.Backend).ToDictionary(g => g.Key,
            g => new Queue<AiJobHeader>(g.OrderByDescending(j => j.Priority)));
        return jobs.Select(j => IsWaiting(j) ? waiting[j.Backend].Dequeue() : j).ToArray();
    }
}
