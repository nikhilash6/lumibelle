using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiJobCoordinator
{
    public async Task SetPriorityAsync(Guid id, bool priority, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            // The store's scheduling lock serializes this change with ClaimNext.
            // A stale UI cannot cancel, requeue, or reprioritize an already-started job.
            // Use an explicit value (not a toggle) so duplicate commands are idempotent.
            await store.UpdateAsync(id, job =>
            {
                if (!AiQueueOrder.IsWaiting(job))
                    throw new WorkspaceStoreException("Only waiting requests can change priority. This request may already have started; its execution is unchanged.");
                return job.Priority == priority ? job : job with { Priority = priority };
            }, ct);
            await RefreshAsync(ct);
        }
        finally { _commands.Release(); Wake(); }
    }
}
