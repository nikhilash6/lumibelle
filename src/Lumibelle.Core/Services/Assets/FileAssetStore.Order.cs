using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<AssetLibrary> ReorderAssetsAsync(Guid projectId, IReadOnlyList<Guid> order,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        if (order.Count != order.Distinct().Count() || !order.ToHashSet().SetEquals(current.Assets.Select(a => a.Id)))
            throw new WorkspaceStoreException("The asset list changed. Reload before moving an asset.");
        if (order.SequenceEqual(current.Assets.Select(a => a.Id))) return current;
        var assets = current.Assets.ToDictionary(a => a.Id);
        return await PublishAsync(directory, current with { Assets = order.Select(id => assets[id]).ToList() }, current.Revision, cancellationToken);
    }
}
