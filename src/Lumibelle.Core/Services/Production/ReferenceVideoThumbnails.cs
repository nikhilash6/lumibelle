using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed partial class FileReferenceVideoStore
{
    private readonly SemaphoreSlim thumbnailWorkers = new(2);

    public async Task<AssetMedia?> OpenThumbnailAsync(Guid project, Guid media, H3Settings settings, CancellationToken ct = default)
    {
        var directory = await DirectoryAsync(project, media, ct);
        if (!Directory.Exists(directory)) return null;
        var record = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(Path.Combine(directory, "media.json"), ct);
        var source = Path.Combine(directory, "video.mp4");
        if (record?.Id != media || !File.Exists(source)) return null;
        var thumbnail = Path.Combine(directory, "thumbnail-v1.jpg");
        // Cache derived files beside immutable media without modifying recipes or captures.
        // Share a lock across store instances; cancel only this reader's unfinished work.
        if (!File.Exists(thumbnail))
        {
            using var gate = await ProjectFiles.LockAsync(thumbnail, ct);
            if (!File.Exists(thumbnail))
            {
                await thumbnailWorkers.WaitAsync(ct);
                var temporary = thumbnail + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await mediaTools.PrepareVideoThumbnailAsync(source, temporary, settings, ct);
                    await using (var stream = File.OpenRead(temporary))
                    {
                        var info = await SixLabors.ImageSharp.Image.IdentifyAsync(stream, ct);
                        if (info.Width is < 1 or > 480 || info.Height is < 1 or > 320 || stream.Length > 1024 * 1024)
                            throw new WorkspaceStoreException("The reel thumbnail could not be prepared.");
                    }
                    ct.ThrowIfCancellationRequested();
                    DurableFile.Flush(temporary);
                    File.Move(temporary, thumbnail);
                }
                finally { try { File.Delete(temporary); } finally { thumbnailWorkers.Release(); } }
            }
        }
        return new(File.OpenRead(thumbnail), "image/jpeg", File.GetLastWriteTimeUtc(thumbnail));
    }
}
