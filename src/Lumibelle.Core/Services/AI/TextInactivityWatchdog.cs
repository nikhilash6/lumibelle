using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

// Timers use TimeProvider's monotonic clock. UI heartbeats never reset the deadline.
public sealed class TextInactivityWatchdog : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _expired = new();
    private readonly CancellationTokenSource _linked;
    private readonly ITimer _timer;
    private long _lastActivity;
    private bool _stopped;
    private GenerationPhase? _phase;
    private string? _stage;
    private readonly Dictionary<(GenerationPhase, string?, string), double> _counters = [];
    public CancellationToken Token => _linked.Token;
    public bool Expired => _expired.IsCancellationRequested;
    public string Message => $"No new text or generation progress arrived for {_interval.TotalSeconds:N0} seconds. Any saved partial response remains available; retry explicitly.";

    public TextInactivityWatchdog(int seconds, CancellationToken caller = default, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System; _interval = TimeSpan.FromSeconds(seconds);
        _lastActivity = _clock.GetTimestamp();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _expired.Token);
        _timer = _clock.CreateTimer(Check, null, _interval, Timeout.InfiniteTimeSpan);
    }
    private void Check(object? state)
    {
        lock (_gate)
        {
            if (_stopped) return;
            var remaining = _interval - _clock.GetElapsedTime(_lastActivity);
            if (remaining > TimeSpan.Zero) { _timer.Change(remaining, Timeout.InfiniteTimeSpan); return; }
            _stopped = true;
            _expired.Cancel();
        }
    }
    public void Activity()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _lastActivity = _clock.GetTimestamp(); _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }
    public void Observe(GenerationProgress? progress)
    {
        if (progress is null) return;
        lock (_gate)
        {
            var advanced = false;
            if (_phase != progress.Phase || _stage != progress.ExecutionStageId)
            { _phase = progress.Phase; _stage = progress.ExecutionStageId; advanced = true; }
            if (progress.Current is { } value && double.IsFinite(value) && value > 0 && progress.Unit is { } unit)
            {
                var key = (progress.Phase, progress.ExecutionStageId, unit);
                if (!_counters.TryGetValue(key, out var previous) || value > previous) { _counters[key] = value; advanced = true; }
            }
            if (advanced) Activity();
        }
    }
    public void Observe(ChatResponseUpdate? response)
    {
        if (response is null) return;
        if (!string.IsNullOrEmpty(response.Text) || response.Contents.OfType<TextReasoningContent>().Any(c => !string.IsNullOrEmpty(c.Text))) Activity();
        if (response.FinishReason is not null) Stop();
    }
    public void Observe(ProgressingChatUpdate update)
    {
        if (update.Activity) Activity();
        Observe(update.Progress); Observe(update.Response);
    }
    public void Stop()
    {
        lock (_gate) { _stopped = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
    }
    public void Dispose() { lock (_gate) { Stop(); _timer.Dispose(); _linked.Dispose(); _expired.Dispose(); } }
}
