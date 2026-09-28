using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public enum MediaTrashKind { Image, Voice, Take }
public sealed record MediaTrashRow(Guid ProjectId, string ProjectName, long Revision, Guid Id, MediaTrashKind Kind,
    string OwnerName, string Title, DateTimeOffset DeletedUtc, DateTimeOffset ExpiresUtc, bool Purging, string? Error,
    AssetImage? Image = null, VoiceReference? Voice = null, ShotTake? Take = null, long Bytes = 0)
{
    public bool CanRestore(DateTimeOffset now) => !Purging && now < ExpiresUtc;
    public string Remaining(DateTimeOffset now) => Purging ? "Pending permanent deletion" : now >= ExpiresUtc ? "Expired · awaiting cleanup" : $"{Math.Ceiling((ExpiresUtc - now).TotalDays):0} days remaining";
    public string Url => Kind == MediaTrashKind.Image ? $"/media/trash/{ProjectId}/{Id}" : $"/media/production-trash/{ProjectId}/{Kind}/{Id}";
    public string? ThumbnailUrl => Kind == MediaTrashKind.Take ? Url + "?frame=0" : Kind == MediaTrashKind.Voice ? null : Url;
    public string OwnerUrl => $"/projects/{ProjectId}/{(Kind is MediaTrashKind.Image or MediaTrashKind.Voice ? "assets" : "shots")}";
}
public sealed record MediaTrashLibrary(IReadOnlyList<MediaTrashRow> Items, IReadOnlyList<ImageTrashIssue> Issues);
public sealed record MediaTrashChange(IReadOnlyList<Guid> Succeeded, IReadOnlyList<ImageTrashIssue> Issues);
public interface IMediaTrashStore
{
    Task<MediaTrashLibrary> ListAsync(CancellationToken ct = default);
    Task<MediaTrashChange> ChangeAsync(IReadOnlyList<MediaTrashRow> captured, bool purge, CancellationToken ct = default);
    Task<IReadOnlyList<ImageTrashIssue>> CleanupAsync(CancellationToken ct = default);
}
public sealed class MediaTrashStore(ProjectFiles files, IImageTrashStore images, IAssetStore assets, IVoiceStore voices, IShotStore shots, TimeProvider clock) : IMediaTrashStore
{
    public async Task<MediaTrashLibrary> ListAsync(CancellationToken ct = default)
    {
        var old = await images.ListTrashAsync(ct); List<ImageTrashIssue> issues = [.. old.Issues];
        List<MediaTrashRow> rows = [];
        var projects = await files.ListProjectsAsync(ct);
        foreach (var project in projects.Projects)
        {
            try
            {
                var dir = await files.DirectoryAsync(project.Id, ct);
                foreach (var r in old.Images.Where(r => r.ProjectId == project.Id))
                {
                    var image = r.Entry.Image;
                    rows.Add(new(r.ProjectId, r.ProjectName, r.Revision, r.Entry.Id, MediaTrashKind.Image, r.Entry.Asset.Name,
                        image.Name ?? string.Join(", ", image.Tags), r.Entry.DeletedUtc, r.Entry.ExpiresUtc, r.Entry.State == ImageTrashState.Purging,
                        r.Entry.CleanupError, Image: image, Bytes: Size(Path.Combine(dir, "assets", (image.StorageAssetId ?? r.Entry.Asset.Id).ToString("D"), "images", image.FileName))));
                }
                var a = await assets.LoadAsync(project.Id, ct);
                rows.AddRange(a.VoiceTrash.Select(t => new MediaTrashRow(project.Id, project.Name, a.Revision, t.Id, MediaTrashKind.Voice, t.Owner.Name, t.Voice.Name, t.DeletedUtc, t.ExpiresUtc, t.Purging, t.Error, Voice: t.Voice,
                    Bytes: Size(Path.Combine(dir, "assets", t.Voice.AssetId.ToString("D"), "voices", t.Voice.FileName)))));
                var s = await shots.LoadAsync(project.Id, ct);
                rows.AddRange(s.Trash.Select(t => new MediaTrashRow(project.Id, project.Name, s.Revision, t.Id, MediaTrashKind.Take,
                    t.Owner!.Title, RefinementPolicy.Label(t.Take!), t.DeletedUtc, t.ExpiresUtc, t.Purging, t.Error, Take: t.Take, Bytes: t.Take!.Bytes)));
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { issues.Add(new(project.Id, e.Message)); }
        }
        return new(rows.OrderByDescending(r => r.DeletedUtc).ThenBy(r => r.Id).ToList(), issues.Distinct().ToList());
    }
    private static long Size(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }
    public async Task<MediaTrashChange> ChangeAsync(IReadOnlyList<MediaTrashRow> captured, bool purge, CancellationToken ct = default)
    {
        List<Guid> succeeded = []; List<ImageTrashIssue> issues = [];
        foreach (var project in captured.GroupBy(r => r.ProjectId))
        {
            try
            {
                var a = await assets.LoadAsync(project.Key, ct); var s = await shots.LoadAsync(project.Key, ct);
                if (project.Any(r => r.Revision != (r.Kind is MediaTrashKind.Image or MediaTrashKind.Voice ? a.Revision : s.Revision))) throw new WorkspaceConflictException();
                foreach (var kind in project.GroupBy(r => r.Kind))
                {
                    var ids = kind.Select(r => r.Id).Distinct().ToArray();
                    switch (kind.Key)
                    {
                        case MediaTrashKind.Image:
                            a = purge ? (await images.PurgeImagesAsync(project.Key, ids, a.Revision, ct)).Library : await images.RestoreImagesAsync(project.Key, ids, a.Revision, ct);
                            succeeded.AddRange(ids.Where(id => a.Trash.All(t => t.Id != id))); issues.AddRange(a.Trash.Where(t => ids.Contains(t.Id) && t.CleanupError is not null).Select(t => new ImageTrashIssue(project.Key, t.CleanupError!))); break;
                        case MediaTrashKind.Voice:
                            a = purge ? await voices.PurgeVoicesAsync(project.Key, ids, a.Revision, ct) : await voices.RestoreVoicesAsync(project.Key, ids, a.Revision, ct);
                            succeeded.AddRange(ids.Where(id => a.VoiceTrash.All(t => t.Id != id))); issues.AddRange(a.VoiceTrash.Where(t => ids.Contains(t.Id) && t.Error is not null).Select(t => new ImageTrashIssue(project.Key, t.Error!))); break;
                        default:
                            s = purge ? await shots.PurgeAsync(project.Key, ids, s.Revision, ct) : await shots.RestoreAsync(project.Key, ids, s.Revision, ct);
                            succeeded.AddRange(ids.Where(id => s.Trash.All(t => t.Id != id))); issues.AddRange(s.Trash.Where(t => ids.Contains(t.Id) && t.Error is not null).Select(t => new ImageTrashIssue(project.Key, t.Error!))); break;
                    }
                }
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException) { issues.Add(new(project.Key, project.First().ProjectName + ": " + e.Message)); }
        }
        return new(succeeded, issues);
    }
    public async Task<IReadOnlyList<ImageTrashIssue>> CleanupAsync(CancellationToken ct = default)
    {
        List<ImageTrashIssue> issues = [.. await images.CleanupExpiredAsync(ct)];
        issues.AddRange(await voices.CleanupVoiceImportsAsync(ct));
        var library = await ListAsync(ct);
        var expired = library.Items.Where(r => r.Kind != MediaTrashKind.Image && (r.Purging || r.ExpiresUtc <= clock.GetUtcNow())).ToList();
        if (expired.Count > 0) issues.AddRange((await ChangeAsync(expired, true, ct)).Issues);
        return issues;
    }
}
public sealed class MediaTrashCleanupService(IMediaTrashStore trash, TimeProvider clock, ILogger<MediaTrashCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try { foreach (var issue in await trash.CleanupAsync(stoppingToken)) logger.LogWarning("Media cleanup for {ProjectId}: {Message}", issue.ProjectId, issue.Message); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogError(e, "Media cleanup will retry on the next pass."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
