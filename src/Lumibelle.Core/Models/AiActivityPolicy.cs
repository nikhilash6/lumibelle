namespace lumibelle.Models;

// Global activity is an exception inbox, not a second inbox for every successful
// result. Keep the persisted Unread flag: studios use it for result discovery.
public static class AiActivityPolicy
{
    public static bool NeedsAttention(AiJobHeader job)
    {
        // Reading or clearing an alert cannot establish that remote inference
        // stopped. Keep uncertain work discoverable until reconciliation ends it.
        if (job.RemoteUnconfirmed) return true;
        if (job.ActivityClearedUtc is not null || !job.Unread || job.CancelRequested) return false;

        // Include a terminal result carrying an error as well as explicit failures.
        // Waiting/running progress and ordinary confirmed cancellations are not alerts.
        return job.State == AiJobState.NeedsAttention ||
            job.State == AiJobState.Completed && !string.IsNullOrWhiteSpace(job.Error);
    }
}
