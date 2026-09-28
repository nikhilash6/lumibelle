using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public interface IVoiceStore
{
    Task<VoiceImportDraft> StageReelVoiceAsync(Guid projectId, Guid reelId, Stream content, H3Settings settings, CancellationToken ct = default) => throw new NotSupportedException();
    Task<AssetLibrary> SetDefaultVoiceAsync(Guid projectId, Guid assetId, Guid? voiceId, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    Task<AssetLibrary> CommitVoiceAsync(Guid projectId, Guid importId, string name, double start, double end, bool makeDefault, long expectedRevision, CancellationToken ct = default)
        => makeDefault ? throw new NotSupportedException() : CommitVoiceAsync(projectId, importId, name, start, end, expectedRevision, ct);
    Task<VoiceImportDraft> StageVoiceAsync(Guid projectId, Guid assetId, Stream input, string fileName, H3Settings settings, CancellationToken ct = default) => throw new NotSupportedException();
    Task<AssetLibrary> CommitVoiceAsync(Guid projectId, Guid importId, string name, double start, double end, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    Task CancelVoiceImportAsync(Guid projectId, Guid importId, CancellationToken ct = default) => Task.CompletedTask;
    Task<AssetMedia?> OpenVoicePreviewAsync(Guid projectId, Guid id, H3Settings settings, bool staged = false, bool trash = false, CancellationToken ct = default) => Task.FromResult<AssetMedia?>(null);
    Task<IReadOnlyList<ImageTrashIssue>> CleanupVoiceImportsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ImageTrashIssue>>([]);
    Task<AssetLibrary> AddVoiceAsync(Guid projectId, Guid assetId, Stream input, string fileName, string name, double start, double duration, H3Settings settings, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> UpdateVoiceAsync(Guid projectId, VoiceReference voice, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> DiscardVoiceAsync(Guid projectId, Guid voiceId, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> RestoreVoicesAsync(Guid projectId, IReadOnlyCollection<Guid> ids, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> PurgeVoicesAsync(Guid projectId, IReadOnlyCollection<Guid> ids, long expectedRevision, CancellationToken ct = default);
    Task<AssetMedia?> OpenVoiceAsync(Guid projectId, Guid voiceId, bool trash = false, CancellationToken ct = default);
}
public sealed partial class FileAssetStore : IVoiceStore
{
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    { [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".flac"] = "audio/flac", [".m4a"] = "audio/mp4", [".ogg"] = "audio/ogg" };
    private static string VoicePath(string dir, VoiceReference v) => Path.Combine(dir, "assets", (v.StorageAssetId ?? v.AssetId).ToString("D"), "voices", v.FileName);
    private TrashedVoice TrashVoice(AssetLibrary d, VoiceReference v)
    {
        var now = clock.GetUtcNow();
        return new(Guid.NewGuid(), ShotCopy.Of(v), ShotCopy.Of(d.Assets.Single(a => a.Id == v.AssetId) with { Images = [], DefaultVoiceId = null }), d.Voices.IndexOf(v), now, now.AddDays(30));
    }
    internal static void ValidateVoices(AssetLibrary d)
    {
        if (d.Voices is null) throw new WorkspaceStoreException("Invalid voice library.");
        if (d.Assets.Any(a => a.DefaultVoiceId is { } id && (a.Category != AssetCategory.Character || !d.Voices.Any(v => v.Id == id && v.AssetId == a.Id))))
            throw new WorkspaceStoreException("Choose an active recording owned by this character as its default voice.");
        if (d.VoiceImportReceipts is null || d.VoiceImportReceipts.Any(r => r is null || r.Id == Guid.Empty || r.Fingerprint?.Length != 64) || d.VoiceImportReceipts.Select(r => r.Id).Distinct().Count() != d.VoiceImportReceipts.Count)
            throw new WorkspaceStoreException("Invalid voice import receipts.");
        if (d.Voices is null || d.VoiceTrash is null || d.Voices.Select(v => v.Id).Distinct().Count() != d.Voices.Count ||
            d.VoiceTrash.Select(v => v.Id).Distinct().Count() != d.VoiceTrash.Count) throw new WorkspaceStoreException("Invalid voice library.");
        foreach (var v in d.Voices.Concat(d.VoiceTrash.Select(t => t.Voice)))
            if (v.Id == Guid.Empty || v.AssetId == Guid.Empty || v.StorageAssetId == Guid.Empty || v.PreviousAssetIds?.Any(id => id == Guid.Empty) == true || string.IsNullOrWhiteSpace(v.Name) || !AudioTypes.ContainsKey(Path.GetExtension(v.FileName)) ||
                v.FileName != v.Id.ToString("N") + Path.GetExtension(v.FileName) || !double.IsFinite(v.Duration) || !double.IsFinite(v.Start) || !double.IsFinite(v.ExcerptDuration) ||
                v.Start < 0 || v.ExcerptDuration is < 1 or > 15 || v.Start + v.ExcerptDuration > v.Duration + .01) throw new WorkspaceStoreException("Invalid voice recording or excerpt.");
        if (d.Voices.Any(v => !d.Assets.Any(a => a.Id == v.AssetId && a.Category == AssetCategory.Character)) ||
            d.VoiceTrash.Any(t => t.Owner.Id != t.Voice.AssetId || t.Owner.Images.Count != 0 || t.ExpiresUtc - t.DeletedUtc != TimeSpan.FromDays(30) || d.Voices.Any(v => v.Id == t.Voice.Id)))
            throw new WorkspaceStoreException("Invalid voice ownership or Trash record.");
    }
    public async Task<AssetLibrary> AddVoiceAsync(Guid projectId, Guid assetId, Stream input, string fileName, string name, double start, double duration, H3Settings settings, long expectedRevision, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AudioTypes.TryGetValue(extension, out var type) || string.IsNullOrWhiteSpace(name)) throw new WorkspaceStoreException("Name the recording and choose WAV, MP3, FLAC, M4A, or Ogg audio.");
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, projectId, ct); EnsureRevision(d, expectedRevision);
        if (!d.Assets.Any(a => a.Id == assetId && a.Category == AssetCategory.Character)) throw new WorkspaceStoreException("Choose a character asset for this voice.");
        var v = new VoiceReference { AssetId = assetId, Name = name.Trim(), Start = start, ExcerptDuration = duration, CreatedUtc = clock.GetUtcNow(), ContentType = type };
        v.FileName = v.Id.ToString("N") + extension;
        var path = VoicePath(dir, v); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0) { total += read; if (total > 50L * 1024 * 1024) throw new WorkspaceStoreException("Voice files must be at most 50 MB."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            }
            v.Duration = await new ProductionMediaTools().AudioDurationAsync(path, settings, ct);
            return await PublishAsync(dir, d with { Voices = [.. d.Voices, v] }, d.Revision, ct);
        }
        catch { TryDelete(path); throw; }
    }
    public async Task<AssetLibrary> UpdateVoiceAsync(Guid projectId, VoiceReference voice, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, projectId, ct); EnsureRevision(d, expectedRevision);
        var existing = d.Voices.SingleOrDefault(v => v.Id == voice.Id) ?? throw new WorkspaceStoreException("Voice no longer available.");
        if (existing.AssetId != voice.AssetId) throw new WorkspaceStoreException("This recording was moved. Reopen it in its destination asset before saving.");
        var edited = existing with { Name = voice.Name, Start = voice.Start, ExcerptDuration = voice.ExcerptDuration };
        return await PublishAsync(dir, d with { Voices = d.Voices.Select(v => v.Id == edited.Id ? edited : v).ToList() }, d.Revision, ct);
    }
    public async Task<AssetLibrary> DiscardVoiceAsync(Guid projectId, Guid voiceId, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, projectId, ct); EnsureRevision(d, expectedRevision);
        var v = d.Voices.SingleOrDefault(v => v.Id == voiceId) ?? throw new WorkspaceStoreException("Voice no longer available.");
        return await PublishAsync(dir, d with { Assets = ClearDefaultVoice(d, voiceId), Voices = d.Voices.Where(x => x.Id != voiceId).ToList(), VoiceTrash = [.. d.VoiceTrash, TrashVoice(d, v)] }, d.Revision, ct);
    }
    private static List<TrashedVoice> VoiceSelection(AssetLibrary d, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new WorkspaceStoreException("Choose distinct voices.");
        var result = d.VoiceTrash.Where(v => ids.Contains(v.Id)).ToList();
        if (result.Count != ids.Count) throw new WorkspaceStoreException("Trash changed. Refresh and retry."); return result;
    }
    public async Task<AssetLibrary> RestoreVoicesAsync(Guid projectId, IReadOnlyCollection<Guid> ids, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, projectId, ct); EnsureRevision(d, expectedRevision); var selected = VoiceSelection(d, ids);
        if (selected.Any(t => t.Purging || t.ExpiresUtc <= clock.GetUtcNow() || !File.Exists(VoicePath(dir, t.Voice)))) throw new WorkspaceStoreException("A voice is expired, purging, or missing. Nothing was restored.");
        foreach (var t in selected.OrderBy(t => t.Position))
        {
            var owner = d.Assets.FirstOrDefault(a => a.Id == t.Owner.Id);
            if (owner is null) d.Assets.Add(t.Owner with { Images = [], DefaultVoiceId = null });
            else if (owner.Category != AssetCategory.Character) throw new WorkspaceStoreException("The original asset is no longer a character.");
            d.Voices.Insert(Math.Clamp(t.Position, 0, d.Voices.Count), t.Voice); d.VoiceTrash.Remove(t);
        }
        return await PublishAsync(dir, d, d.Revision, ct);
    }
    public async Task<AssetLibrary> PurgeVoicesAsync(Guid projectId, IReadOnlyCollection<Guid> ids, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, projectId, ct); EnsureRevision(d, expectedRevision); VoiceSelection(d, ids);
        d = await PublishAsync(dir, d with { VoiceTrash = d.VoiceTrash.Select(t => ids.Contains(t.Id) ? t with { Purging = true, Error = null } : t).ToList() }, d.Revision, ct);
        foreach (var t in VoiceSelection(d, ids))
        {
            try { if (File.Exists(VoicePath(dir, t.Voice))) File.Delete(VoicePath(dir, t.Voice)); File.Delete(VoicePath(dir, t.Voice) + ".preview.mp3"); d.VoiceTrash.Remove(t); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { d.VoiceTrash[d.VoiceTrash.IndexOf(t)] = t with { Error = "Could not remove the voice file. Close apps using it and retry." }; }
        }
        return await PublishAsync(dir, d, d.Revision, ct);
    }
    public async Task<AssetMedia?> OpenVoiceAsync(Guid projectId, Guid voiceId, bool trash = false, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); var d = await ReadAsync(dir, projectId, ct);
        var voice = trash ? d.VoiceTrash.FirstOrDefault(v => v.Id == voiceId && !v.Purging && v.ExpiresUtc > clock.GetUtcNow())?.Voice : d.Voices.FirstOrDefault(v => v.Id == voiceId);
        if (voice is null) return null;
        try { return new(new FileStream(VoicePath(dir, voice), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), voice.ContentType, voice.CreatedUtc); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}
