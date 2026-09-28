namespace lumibelle.Services.AI;

public sealed record ComfyObservedTimings(IReadOnlyDictionary<string, double> Seconds, bool Partial);

// Only time intervals bounded by observed node events. History recovery cannot
// reconstruct stage durations, and progress values are not stage start times.
internal sealed class ComfyTimingTracker(TimeProvider clock, IReadOnlyDictionary<string, string> stages)
{
    private readonly Dictionary<string, double> _seconds = [];
    private string? _stage;
    private long _started;
    private bool _sawStart, _sawEnd, _gap;

    public void Start() => _sawStart = true;
    public void Executing(string? node)
    {
        if (_gap) return;
        Close();
        if (node is not null && stages.TryGetValue(node, out var stage)) { _stage = stage; _started = clock.GetTimestamp(); }
        if (node is null) _sawEnd = true;
    }
    public void Cached(IEnumerable<string> nodes)
    {
        if (_gap) return;
        foreach (var node in nodes) if (stages.TryGetValue(node, out var stage)) _seconds.TryAdd(stage, 0);
    }
    public void End() { if (!_gap) { Close(); _sawEnd = true; } }
    public void Gap() { _gap = true; _stage = null; _seconds.Clear(); }
    private void Close()
    {
        if (_stage is { } stage)
            _seconds[stage] = _seconds.GetValueOrDefault(stage) + Math.Max(0, clock.GetElapsedTime(_started).TotalSeconds);
        _stage = null;
    }
    public ComfyObservedTimings Snapshot()
    {
        var result = new Dictionary<string, double>(_seconds);
        if (_stage is { } unfinished) result.Remove(unfinished);
        if (!_sawStart) result.Remove("Preparation");
        return new(result, _gap || !_sawStart || !_sawEnd);
    }
}
