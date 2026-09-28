using lumibelle.Models;

namespace lumibelle.Services.Assets;

public interface IImageTrashStore
{
    Task<ImageTrashLibrary> ListTrashAsync(CancellationToken cancellationToken = default);
    Task<AssetMedia?> OpenTrashImageAsync(Guid projectId, Guid trashId, CancellationToken cancellationToken = default);
    Task<AssetLibrary> RestoreImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<ImagePurgeResult> PurgeImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImageTrashIssue>> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}
