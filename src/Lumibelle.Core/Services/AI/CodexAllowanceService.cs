using lumibelle.Models;

namespace lumibelle.Services.AI;

// Display cache only: queue admission continues to use the client's own fresh checks.
public sealed class CodexAllowanceService(ICodexClient client, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1);
    private string? _executable, _account;
    private DateTimeOffset? _attempted;
    private CodexUsage? _reading;
    public async Task<CodexUsage> ReadAsync(CodexSettings settings, bool force = false, CancellationToken ct = default)
    {
        var requested = clock.GetUtcNow();
        await _gate.WaitAsync(ct);
        try
        {
            var changed = _executable != settings.ExecutablePath || _account != client.Connection?.AccountId;
            if (changed) { _reading = null; _attempted = null; _executable = settings.ExecutablePath; _account = client.Connection?.AccountId; }
            if (_attempted is { } previous && (previous >= requested || !force && requested - previous < TimeSpan.FromMinutes(1)))
                return _reading ?? new(requested, [], "Allowance unavailable.");
            _attempted = clock.GetUtcNow();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                CodexUsage? usage;
                if (changed || client.Connection?.Success != true)
                {
                    var connection = await client.CheckAsync(settings, linked.Token);
                    _account = connection.AccountId;
                    if (!connection.Success) throw new AiGenerationException(connection.Message);
                    usage = connection.Usage;
                }
                else usage = await client.ReadUsageAsync(settings, linked.Token);
                if (client.Connection is { Success: true, ReportsAllowance: false }) usage = new(clock.GetUtcNow(), []);
                if (usage is null || usage.Error is not null) throw new AiGenerationException(usage?.Error ?? "Allowance unavailable.");
                _reading = usage;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Failed("Allowance check timed out."); }
            catch (AiGenerationException e) { Failed(e.Message); }
            return _reading!;
        }
        finally { _gate.Release(); }
    }
    private void Failed(string error) => _reading = (_reading ?? new(clock.GetUtcNow(), [])) with { Error = error };
}
