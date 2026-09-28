namespace lumibelle.Services.AI;

// Guidance and enhancement have no coverage/production application record. Use their
// existing review document to remember resolution without changing captured requests.
public sealed class TextRequestReviews(IAiJobReviewStore reviews)
{
    public sealed record Resolution(bool Applied = false, bool Discarded = false);
    // Lets pages that flag unresolved reviews, such as the asset list, update at once.
    public event Action<Guid>? Resolved;
    public async Task<bool> IsApplied(Guid id, CancellationToken ct = default) => (await reviews.LoadAsync(id, ct)).Read<Resolution>()?.Applied == true;
    public async Task<bool> IsResolved(Guid id, CancellationToken ct = default) => (await reviews.LoadAsync(id, ct)).Read<Resolution>() is { } r && (r.Applied || r.Discarded);
    public Task Applied(Guid id, CancellationToken ct = default) => Resolve(id, new(Applied: true), ct);
    public Task Discarded(Guid id, CancellationToken ct = default) => Resolve(id, new(Discarded: true), ct);
    private async Task Resolve(Guid id, Resolution resolution, CancellationToken ct)
    {
        var saved = await reviews.LoadAsync(id, ct);
        if (saved.Read<Resolution>() is not { } prior || !(prior.Applied || prior.Discarded))
            await reviews.SaveAsync(id, resolution, saved.Revision, ct);
        Resolved?.Invoke(id);
    }
}
