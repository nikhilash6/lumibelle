using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

internal sealed class CodexImageTimingTracker(JsonElement turn, DateTimeOffset submittedUtc, TimeProvider clock)
{
    public CodexImageTiming Snapshot { get; private set; } = new(Seconds(turn, "startedAt") ?? submittedUtc, []);

    public void StartImage(JsonElement parameters, string id)
    {
        if (Snapshot.ImageCalls.Any(c => c.Id == id)) return;
        Snapshot = Snapshot with { ImageCalls = [.. Snapshot.ImageCalls, new(id, Milliseconds(parameters, "startedAtMs") ?? clock.GetUtcNow())] };
    }

    public void FinishImage(JsonElement parameters, string id)
    {
        var calls = Snapshot.ImageCalls.ToList();
        var index = calls.FindIndex(c => c.Id == id);
        var end = Milliseconds(parameters, "completedAtMs") ?? clock.GetUtcNow();
        if (index < 0) calls.Add(new(id, null, end)); // Missing start means the split is unknown.
        else calls[index] = calls[index] with { CompletedUtc = end };
        Snapshot = Snapshot with { ImageCalls = calls.ToArray() };
    }

    public void Complete(JsonElement notification, JsonElement completedTurn)
    {
        var end = Milliseconds(notification, "emittedAtMs") ?? clock.GetUtcNow();
        var start = Snapshot.StartedUtc;
        // Turn timestamps have second precision; duration and emission timestamps refine the final split.
        if (completedTurn.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var ms) && ms is >= 0 and < 86_400_000)
            start = end.AddMilliseconds(-ms);
        Snapshot = Snapshot with { StartedUtc = start, CompletedUtc = end };
    }

    private static DateTimeOffset? Milliseconds(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var ms) && ms is >= -62_135_596_800_000 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    private static DateTimeOffset? Seconds(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var seconds) && seconds is >= -62_135_596_800 and <= 253_402_300_799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
