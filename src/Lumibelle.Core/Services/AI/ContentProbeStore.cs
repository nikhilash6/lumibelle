using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public interface IContentProbeStore
{
    Task<ContentProbeLibrary> LoadLibraryAsync(CancellationToken ct = default);
    Task<ContentProbeLibrary> SaveProbeAsync(ContentProbe probe, long expectedRevision, CancellationToken ct = default);
    Task<ContentProbeLibrary> DeleteProbeAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<ContentProbeLibrary> SetEnabledAsync(Guid id, bool enabled, long expectedRevision, CancellationToken ct = default);
    Task<ContentProbeReview> LoadReviewAsync(Guid jobId, CancellationToken ct = default);
    Task<ContentProbeReview> SaveReviewAsync(ContentProbeReview draft, CancellationToken ct = default);
}

/// <summary>Global probe definitions and per-response human reviews; inference stays in the AI queue.</summary>
public sealed class FileContentProbeStore(ApplicationPaths paths, IAiJobStore jobs, TimeProvider clock) : IContentProbeStore
{
    private string LibraryPath => Path.Combine(paths.Data, "llm-probes", "library.json");
    private string ReviewPath(Guid id) => id == Guid.Empty ? throw new WorkspaceStoreException("Choose a response to review.")
        : Path.Combine(paths.Data, "llm-probes", "ratings", id.ToString("N") + ".json");

    public async Task<ContentProbeLibrary> LoadLibraryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var library = Directory.Exists(Path.GetDirectoryName(LibraryPath))
            ? await AtomicJsonFile.ReadAsync<ContentProbeLibrary>(LibraryPath, ct) ?? new ContentProbeLibrary() : new ContentProbeLibrary();
        ValidateLibrary(library); return library;
    }
    private static void ValidateLibrary(ContentProbeLibrary library)
    {
        if (library.SchemaVersion != 1 || library.Revision < 0 || library.Custom is null || library.DisabledBuiltIns is null ||
            library.Custom.Count > 1000 || library.Custom.Any(p => p is null) ||
            library.Custom.Select(p => p.Id).Distinct().Count() != library.Custom.Count ||
            library.Custom.Any(p => ContentProbeBuiltIns.IsBuiltIn(p.Id)) ||
            library.DisabledBuiltIns.Any(id => !ContentProbeBuiltIns.IsBuiltIn(id)) ||
            library.DisabledBuiltIns.Distinct().Count() != library.DisabledBuiltIns.Count)
            throw new WorkspaceStoreException("The writing-test library is invalid or uses an unsupported format. It has not been replaced.");
        foreach (var probe in library.Custom) ContentProbePolicy.Validate(probe);
    }
    private async Task<ContentProbeLibrary> ChangeAsync(long revision, Func<ContentProbeLibrary, ContentProbeLibrary> change, CancellationToken ct)
    {
        using var gate = await ProjectFiles.LockAsync(LibraryPath, ct);
        var current = await LoadLibraryAsync(ct);
        if (revision != current.Revision) throw new WorkspaceConflictException();
        var next = change(current) with { Revision = checked(current.Revision + 1) };
        ValidateLibrary(next);
        await AtomicJsonFile.WriteAsync(LibraryPath, next, ct); return next;
    }
    public Task<ContentProbeLibrary> SaveProbeAsync(ContentProbe probe, long expectedRevision, CancellationToken ct = default)
    {
        ContentProbePolicy.Validate(probe);
        var copy = ContentProbePolicy.Copy(probe) with { Name = probe.Name.Trim(), Category = probe.Category.Trim(),
            Prompt = probe.Prompt.Trim(), SuccessCriteria = probe.SuccessCriteria.Trim(),
            RequiredPhrases = probe.RequiredPhrases.Select(p => p.Trim()).ToArray() };
        return ChangeAsync(expectedRevision, current =>
        {
            if (ContentProbeBuiltIns.IsBuiltIn(copy.Id)) throw new WorkspaceStoreException("Duplicate a starter test to customize it.");
            var previous = current.Custom.SingleOrDefault(p => p.Id == copy.Id);
            if (previous is null && copy.Revision != 1 || previous is not null && copy.Revision != previous.Revision)
                throw new WorkspaceConflictException();
            var saved = copy with { Revision = previous is null ? 1 : checked(previous.Revision + 1) };
            return current with { Custom = previous is null ? [.. current.Custom, saved]
                : current.Custom.Select(p => p.Id == saved.Id ? saved : p).ToArray() };
        }, ct);
    }
    public Task<ContentProbeLibrary> DeleteProbeAsync(Guid id, long expectedRevision, CancellationToken ct = default) =>
        ChangeAsync(expectedRevision, current =>
        {
            if (!current.Custom.Any(p => p.Id == id)) throw new WorkspaceStoreException("This custom test no longer exists. Starter tests can only be disabled.");
            return current with { Custom = current.Custom.Where(p => p.Id != id).ToArray() };
        }, ct);
    public Task<ContentProbeLibrary> SetEnabledAsync(Guid id, bool enabled, long expectedRevision, CancellationToken ct = default) =>
        ChangeAsync(expectedRevision, current =>
        {
            if (ContentProbeBuiltIns.IsBuiltIn(id)) return current with { DisabledBuiltIns = enabled
                ? current.DisabledBuiltIns.Where(x => x != id).ToArray() : current.DisabledBuiltIns.Append(id).Distinct().ToArray() };
            if (!current.Custom.Any(p => p.Id == id)) throw new WorkspaceStoreException("The custom test no longer exists.");
            // Enablement changes eligibility, not the probe's content revision.
            return current with { Custom = current.Custom.Select(p => p.Id == id ? p with { Enabled = enabled } : p).ToArray() };
        }, ct);

    public async Task<ContentProbeReview> LoadReviewAsync(Guid jobId, CancellationToken ct = default)
    {
        var path = ReviewPath(jobId); ct.ThrowIfCancellationRequested();
        var review = Directory.Exists(Path.GetDirectoryName(path))
            ? await AtomicJsonFile.ReadAsync<ContentProbeReview>(path, ct) ?? new ContentProbeReview(jobId) : new ContentProbeReview(jobId);
        ValidateReview(review);
        if (review.JobId != jobId) throw new WorkspaceStoreException("The saved rating belongs to another response.");
        return review;
    }
    private static void ValidateReview(ContentProbeReview review)
    {
        if (review.JobId == Guid.Empty || review.Revision < 0 || review.Score is < 1 or > 5 ||
            review.Notes is null || review.Notes.Length > 2000 || review.OutputFingerprint is null ||
            review.OutputFingerprint.Length != 0 && (review.OutputFingerprint.Length != 64 || !review.OutputFingerprint.All(Uri.IsHexDigit)))
            throw new WorkspaceStoreException("A review needs a rating from 1–5 (or unrated) and notes of at most 2,000 characters.");
    }
    public async Task<ContentProbeReview> SaveReviewAsync(ContentProbeReview draft, CancellationToken ct = default)
    {
        ValidateReview(draft);
        var path = ReviewPath(draft.JobId);
        using var gate = await ProjectFiles.LockAsync(path, ct);
        var current = await LoadReviewAsync(draft.JobId, ct);
        if (current.Revision != draft.Revision) throw new WorkspaceConflictException();
        var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == draft.JobId && j.Kind == AiJobKind.ContentProbe)
            ?? throw new WorkspaceStoreException("The test response is unavailable.");
        var request = ContentProbePolicy.Read(job, await jobs.ReadSnapshotAsync(job.Id, ct));
        var result = await jobs.ReadArtifactAsync<ContentProbeResult>(job.Id, AiJobArtifact.Result, ct);
        if (!ContentProbePolicy.CanRate(new(job, request, result, current)) || result is null ||
            draft.OutputFingerprint != ContentProbePolicy.OutputFingerprint(result))
            throw new WorkspaceStoreException("The response changed or is incomplete. Refresh before rating; failed, blocked, and truncated requests are not rated.");
        var saved = draft with { Revision = checked(current.Revision + 1), Notes = draft.Notes.Trim(), UpdatedUtc = clock.GetUtcNow() };
        await AtomicJsonFile.WriteAsync(path, saved, ct); return saved;
    }
}
