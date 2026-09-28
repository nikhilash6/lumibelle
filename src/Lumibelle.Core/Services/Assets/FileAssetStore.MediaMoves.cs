using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<AssetLibrary> MoveReelAsync(Guid projectId, Guid sourceAssetId, Guid reelId, Guid destinationAssetId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var reel = current.Reels.SingleOrDefault(r => r.Id == reelId && r.AssetId == sourceAssetId)
            ?? throw new WorkspaceStoreException("This reel was moved or removed. Reopen it before moving.");
        var owner = ReelOwner(current, sourceAssetId, reel.LookId, allowArchived: true);
        MoveMediaDestination(current, owner, destinationAssetId);
        // Only library membership changes. Attachments and publication receipts
        // continue to identify the same immutable media and captured destination.
        var moved = reel with { AssetId = destinationAssetId, LookId = null, OriginalAssetId = reel.OriginalAssetId ?? sourceAssetId };
        return await PublishAsync(directory, current with { Reels = current.Reels.Select(r => r.Id == reelId ? moved : r).ToList() }, current.Revision, cancellationToken);
    }

    public async Task<AssetLibrary> MoveVoiceAsync(Guid projectId, Guid sourceAssetId, Guid voiceId, Guid destinationAssetId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var voice = current.Voices.SingleOrDefault(v => v.Id == voiceId && v.AssetId == sourceAssetId)
            ?? throw new WorkspaceStoreException("This recording was moved or removed. Reopen it before moving.");
        var owner = current.Assets.Single(a => a.Id == sourceAssetId);
        MoveMediaDestination(current, owner, destinationAssetId);
        try { using var media = File.Open(VoicePath(directory, voice), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new WorkspaceStoreException("The recording file is missing or unreadable. Nothing was moved.", e); }
        var moved = voice with { AssetId = destinationAssetId, StorageAssetId = voice.StorageAssetId ?? sourceAssetId,
            PreviousAssetIds = (voice.PreviousAssetIds ?? []).Append(sourceAssetId).Distinct().ToArray() };
        return await PublishAsync(directory, current with { Assets = ClearDefaultVoice(current, voiceId), Voices = current.Voices.Select(v => v.Id == voiceId ? moved : v).ToList() }, current.Revision, cancellationToken);
    }

    private static void MoveMediaDestination(AssetLibrary library, ReferenceAsset source, Guid target)
    {
        if (!ReferenceReels.Supports(source.Category) || source.Id == target || !library.Assets.Any(a => a.Id == target && a.Category == source.Category))
            throw new WorkspaceStoreException("Choose another asset of the same type as the source.");
    }
}
