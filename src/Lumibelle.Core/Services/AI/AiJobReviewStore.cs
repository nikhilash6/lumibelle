using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record AiJobReviewDraft(long Revision = 0, JsonElement? Value = null)
{
    public T? Read<T>() => Value is { } value ? value.Deserialize<T>(AtomicJsonFile.Options) : default;
}
public interface IAiJobReviewStore
{
    Task<AiJobReviewDraft> LoadAsync(Guid jobId, CancellationToken ct = default);
    Task<AiJobReviewDraft> SaveAsync<T>(Guid jobId, T value, long expectedRevision, CancellationToken ct = default);
}

// Author edits have their own revision and lock. Worker result/progress writes must
// never replace this draft, and a failed acknowledgement can retry the same edit.
public sealed class AiJobReviewStore(IAiJobStore jobs) : IAiJobReviewStore
{
    private string PathFor(Guid id) => Path.Combine(jobs.DirectoryFor(id), "review-draft.json");
    public async Task<AiJobReviewDraft> LoadAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!(await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == jobId)) throw new WorkspaceStoreException("AI request not found.");
        var saved = await AtomicJsonFile.ReadAsync<AiJobReviewDraft>(PathFor(jobId), ct) ?? new();
        if (saved.Revision < 0 || saved.Value is not null && saved.Value.Value.ValueKind != JsonValueKind.Object)
            throw new WorkspaceStoreException("The saved review draft is invalid. It has not been replaced.");
        return saved;
    }
    public async Task<AiJobReviewDraft> SaveAsync<T>(Guid jobId, T value, long expectedRevision, CancellationToken ct = default)
    {
        var captured = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
        if (expectedRevision < 0 || captured.ValueKind != JsonValueKind.Object) throw new WorkspaceStoreException("A review draft needs a valid revision and content.");
        var path = PathFor(jobId);
        using var gate = await ProjectFiles.LockAsync(path, ct);
        var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == jobId) ?? throw new WorkspaceStoreException("AI request not found.");
        if (job.State != AiJobState.Completed || job.CancelRequested || job.Kind is not (AiJobKind.AssetExtraction or AiJobKind.ShotPlanning or AiJobKind.Guidance or AiJobKind.PromptEnhancement))
            throw new WorkspaceStoreException("Only a completed planning, extraction, guidance or enhancement response can have a review draft.");
        var current = await LoadAsync(jobId, ct);
        if (current.Revision == expectedRevision && current.Value is { } unchanged && JsonElement.DeepEquals(unchanged, captured)) return current;
        if (current.Revision != expectedRevision)
        {
            if (current.Revision == expectedRevision + 1 && current.Value is { } previous && JsonElement.DeepEquals(previous, captured)) return current;
            throw new WorkspaceConflictException();
        }
        var next = new AiJobReviewDraft(current.Revision + 1, captured);
        await AtomicJsonFile.WriteAsync(path, next, ct);
        return next;
    }
}
