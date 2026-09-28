using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    private static List<ReferenceAsset> ClearDefaultVoice(AssetLibrary library, Guid voiceId) =>
        library.Assets.Select(a => a.DefaultVoiceId == voiceId ? a with { DefaultVoiceId = null } : a).ToList();

    public async Task<AssetLibrary> SetDefaultVoiceAsync(Guid projectId, Guid assetId, Guid? voiceId, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var library = await ReadAsync(dir, projectId, ct); EnsureRevision(library, expectedRevision);
        if (!library.Assets.Any(a => a.Id == assetId && a.Category == AssetCategory.Character) ||
            voiceId is { } id && !library.Voices.Any(v => v.Id == id && v.AssetId == assetId))
            throw new WorkspaceStoreException("Choose an active recording owned by this character.");
        return await PublishAsync(dir, library with { Assets = library.Assets.Select(a => a.Id == assetId ? a with { DefaultVoiceId = voiceId } : a).ToList() }, library.Revision, ct);
    }

    public async Task<VoiceImportDraft> StageReelVoiceAsync(Guid projectId, Guid reelId, Stream content, H3Settings settings, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); var library = await ReadAsync(dir, projectId, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId) ?? throw new WorkspaceStoreException("This reel is no longer available.");
        if (!reel.Media.HasAudio || !library.Assets.Any(a => a.Id == reel.AssetId && a.Category == AssetCategory.Character))
            throw new WorkspaceStoreException("Choose a character reel with audio.");
        var id = Guid.NewGuid(); var staging = ImportDirectory(dir, id); Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "source.mp4"); var file = id.ToString("N") + ".wav";
        try
        {
            await using (var output = File.Create(source)) {
                var buffer = new byte[81920]; long bytes = 0; int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0) {
                    bytes += read; if (bytes > reel.Media.Bytes) throw new WorkspaceStoreException("Reel media changed during extraction.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await using (var input = File.OpenRead(source))
                if (input.Length != reel.Media.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != reel.Media.Sha256)
                    throw new WorkspaceStoreException("Reel media changed during extraction.");
            var tools = new ProductionMediaTools(); var path = Path.Combine(staging, file);
            await tools.ExtractReelAudioAsync(source, path, settings, ct);
            var duration = await tools.AudioDurationAsync(path, settings, ct);
            await tools.PrepareAudioPreviewAsync(path, Path.Combine(staging, "preview.mp3"), settings, ct);
            var draft = new VoiceImportDraft(id, reel.AssetId, file, "audio/wav", reel.Name + " voice", duration, clock.GetUtcNow())
                { SourceReel = new(reel.Id, reel.Media.Id, reel.Media.Sha256, reel.Name) };
            await AtomicJsonFile.WriteAsync(Path.Combine(staging, "draft.json"), draft, ct);
            return draft;
        }
        catch { DeleteImportDirectory(staging); throw; }
        finally { TryDelete(source); }
    }
}
