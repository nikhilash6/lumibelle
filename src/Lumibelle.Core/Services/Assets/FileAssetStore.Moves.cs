using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<AssetLibrary> MoveImagesAsync(Guid projectId, Guid sourceAssetId, IReadOnlyCollection<Guid> imageIds,
        ImageDestination destination, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var ids = DistinctIds(imageIds);
        if (destination is null || destination.AssetId == Guid.Empty || destination.AssetId == sourceAssetId)
            throw new WorkspaceStoreException("Choose a different destination asset.");
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var source = current.Assets.SingleOrDefault(a => a.Id == sourceAssetId)
            ?? throw new WorkspaceStoreException("The source asset no longer exists.");
        var images = source.Images.Where(i => ids.Contains(i.Id)).ToArray();
        if (images.Length != ids.Count) throw new WorkspaceStoreException("A selected image was moved or removed. Reload before retrying.");
        var target = current.Assets.SingleOrDefault(a => a.Id == destination.AssetId);
        if (destination.NewAssetName is { } name)
        {
            if (target is not null || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 240 ||
                !Enum.IsDefined(destination.NewAssetCategory) || destination.LookId is not null)
                throw new WorkspaceStoreException("Choose a new asset name and category, without an existing look.");
            target = new() { Id = destination.AssetId, Name = name.Trim(), Category = destination.NewAssetCategory,
                CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow() };
        }
        if (target is null) throw new WorkspaceStoreException("The destination asset no longer exists.");
        if (destination.LookId is { } look && (target.Category != AssetCategory.Character || LookPolicy.Find(target, look) is not { Archived: false }))
            throw new WorkspaceStoreException("Choose an active look belonging to the destination character.");
        foreach (var image in images)
        {
            try { using var media = File.Open(ImagePath(directory, sourceAssetId, image), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new WorkspaceStoreException("A selected image file is missing or unreadable. Nothing was moved.", e); }
        }
        var hasCover = target.Images.Any(i => i.IsCover);
        var moved = images.Select(i => i with
        {
            LookId = destination.LookId, IsCover = i.IsCover && !hasCover,
            StorageAssetId = i.StorageAssetId ?? sourceAssetId,
            PreviousAssetIds = i.PreviousAssetIds.Append(sourceAssetId).Distinct().ToArray()
        }).ToArray();
        var updated = target with { Images = [.. target.Images, .. moved], UpdatedUtc = clock.GetUtcNow() };
        var next = current.Assets.Select(a => a.Id == sourceAssetId ? a with
        {
            Images = a.Images.Where(i => !ids.Contains(i.Id)).ToList(), UpdatedUtc = clock.GetUtcNow(),
            PreferredIdentityReferences = a.PreferredIdentityReferences.Where(r => !ids.Contains(r.ImageId)).ToArray(),
            Looks = a.Looks.Select(l => l with { PreferredAppearanceReferences = l.PreferredAppearanceReferences.Where(r => !ids.Contains(r.ImageId)).ToArray() }).ToArray()
        } : a.Id == target.Id ? updated : a).ToList();
        if (destination.NewAssetName is not null) next.Add(updated);
        // File location, provenance and publication receipts stay immutable. Both
        // memberships change in a single atomic manifest, including on failure.
        return await PublishAsync(directory, current with { Assets = next }, current.Revision, cancellationToken);
    }
}
