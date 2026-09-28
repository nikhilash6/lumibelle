using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiJobCoordinator
{
    public async Task RetryComfyAsync(Guid id, long expectedVersion, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            // A finished worker may still be unwinding after publishing its state.
            // Record the command now; Tick waits for it to exit before reconciliation.
            await store.UpdateAsync(id, current =>
            {
                if (current.Version != expectedVersion) throw new WorkspaceConflictException();
                if (!ComfyQueuePolicy.CanRetry(current)) throw new WorkspaceStoreException("This ComfyUI request no longer needs reconnect/retry. Refresh its status.");
                return current with { ComfyControl = new(Guid.NewGuid(), current.ComfyControl?.PauseRequested == true),
                    Recovery = AiJobRecovery.CheckStatus, ActivityClearedUtc = null, FinishedUtc = null,
                    Error = "Retry requested. Reconnecting to the original ComfyUI server; existing work will be checked before anything is repeated." };
            }, ct);
            _reconciled.Remove(id);
            _progress.TryRemove(id, out _);
            // Persisting intent is enough: the execution owner performs all network
            // work. This also works when commands arrive through another UI process.
            await RefreshAsync(ct);
        }
        finally { _commands.Release(); Wake(); }
    }

    private async Task StopPausedObserversAsync(CancellationToken ct)
    {
        foreach (var job in (await store.ReadAsync(ct)).Jobs.Where(j => j.ComfyControl?.PauseRequested == true))
            if (_workers.TryGetValue(job.Id, out var worker)) worker.Cancellation.Cancel();
    }
}
