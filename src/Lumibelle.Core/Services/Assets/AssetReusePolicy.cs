using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class AssetReusePolicy
{
    public const string ClipboardPrefix = "lumibelle-asset:v1:";
    public const int MaximumClipboardCharacters = 2048;
    public const long MaximumPackageBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumPackageFiles = 30000;
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static Guid Identity(Guid command, string kind, Guid original) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"asset-reuse-v1/{command:D}/{kind}/{original:D}")).AsSpan(0, 16));

    public static void Validate(AssetReuseSelection source)
    {
        if (source is null || source.ProjectId == Guid.Empty || source.AssetId == Guid.Empty || !Enum.IsDefined(source.Kind) ||
            source.MediaId == Guid.Empty || (source.MediaId is null) != (source.Kind == AssetReuseKind.Asset))
            throw new WorkspaceStoreException("Choose an exact asset, image, reel or voice to copy.");
    }
    public static void Validate(AssetReuseCommand command)
    {
        if (command is null || command.Id == Guid.Empty || (command.Source is null) == (command.SharedEntryId is null) ||
            command.SharedEntryId == Guid.Empty || !IsHash(command.SourceFingerprint) || command.Destination is not { } target ||
            target.ProjectId == Guid.Empty || target.AssetId == Guid.Empty || string.IsNullOrWhiteSpace(target.Name) || target.Name.Trim().Length > 240 ||
            command.Move && (command.Source is null || command.Source.ProjectId == target.ProjectId))
            throw new WorkspaceStoreException("Choose a name and destination. Move requires two different projects; shared entries are copied, not moved.");
        if (command.Source is { } source) Validate(source);
    }
    public static AssetReuseContent Capture(AssetLibrary library, AssetReuseSelection source)
    {
        Validate(source);
        if (library.ProjectId != source.ProjectId) throw new WorkspaceStoreException("The copied item belongs to another project.");
        var owner = library.Assets.SingleOrDefault(a => a.Id == source.AssetId)
            ?? throw new WorkspaceStoreException("The source asset was removed. Copy an active asset instead.");
        var images = owner.Images.Where(i => source.Kind == AssetReuseKind.Asset || source.Kind == AssetReuseKind.Image && i.Id == source.MediaId).ToList();
        var reels = library.Reels.Where(r => r.AssetId == owner.Id && (source.Kind == AssetReuseKind.Asset || source.Kind == AssetReuseKind.Reel && r.Id == source.MediaId)).ToArray();
        var voices = library.Voices.Where(v => v.AssetId == owner.Id && (source.Kind == AssetReuseKind.Asset || source.Kind == AssetReuseKind.Voice && v.Id == source.MediaId)).ToArray();
        if (source.Kind != AssetReuseKind.Asset && images.Count + reels.Length + voices.Length != 1)
            throw new WorkspaceStoreException("The copied media was moved or removed. Copy its current selection again.");
        var asset = owner with { Images = images, DefaultVoiceId = voices.Any(v => v.Id == owner.DefaultVoiceId) ? owner.DefaultVoiceId : null };
        // Capture by value: caller edits cannot alter a transfer while media is being read.
        return ShotCopy.Of(new AssetReuseContent(source, asset, reels, voices));
    }
    public static string Clipboard(AssetLibrary library, AssetReuseSelection source) => ClipboardPrefix +
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AssetClipboardItem(1, source, Hash(Capture(library, source))), AtomicJsonFile.Options));
    public static AssetClipboardItem ParseClipboard(string text)
    {
        if (text is null || text.Length > MaximumClipboardCharacters || !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            throw new WorkspaceStoreException("The clipboard does not contain a Lumibelle asset. Paste an image or copy an item from Assets.");
        try
        {
            var item = JsonSerializer.Deserialize<AssetClipboardItem>(Convert.FromBase64String(text[ClipboardPrefix.Length..]), AtomicJsonFile.Options);
            if (item is null || item.Version != 1 || !IsHash(item.Fingerprint)) throw new JsonException();
            Validate(item.Source); return item;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        { throw new WorkspaceStoreException("The copied asset reference is invalid. Copy it again from Assets.", e); }
    }

    public static void ValidateReceipts(AssetLibrary library)
    {
        var copies = library.AssetReuseReceipts ?? [];
        var moves = library.AssetMoveReceipts ?? [];
        if (copies.Any(r => r is null || r.CommandId == Guid.Empty || !IsHash(r.Fingerprint) || r.AssetId == Guid.Empty || r.MediaId == Guid.Empty || r.SharedEntryId == Guid.Empty || r.CopiedUtc == default) ||
            moves.Any(r => r is null || r.CommandId == Guid.Empty || !IsHash(r.Fingerprint)) ||
            copies.Select(r => r.CommandId).Distinct().Count() != copies.Count ||
            moves.Select(r => r.CommandId).Distinct().Count() != moves.Count)
            throw new WorkspaceStoreException("Asset-copy receipts are invalid; the library has not been changed.");
        foreach (var receipt in copies) Validate(receipt.Source);
    }

    public static bool ContainsIdentity(JsonElement value, IReadOnlySet<Guid> ids) => value.ValueKind switch
    {
        JsonValueKind.String => Guid.TryParse(value.GetString(), out var id) && ids.Contains(id),
        JsonValueKind.Array => value.EnumerateArray().Any(v => ContainsIdentity(v, ids)),
        JsonValueKind.Object => value.EnumerateObject().Any(p => ContainsIdentity(p.Value, ids)),
        _ => false
    };

    // Media copies are library inputs, not new results of the original generation job.
    // The source metadata remains in the shared snapshot and the copy receipt keeps its origin.
    public static (AssetLibrary Library, Guid AssetId, Guid? MediaId) Import(AssetReuseContent content,
        AssetLibrary destination, AssetReuseCommand command, DateTimeOffset now)
    {
        Validate(command); Validate(content.Source);
        if (AssetReusePolicy.Hash(content) != command.SourceFingerprint || command.Source is { } selected && selected != content.Source)
            throw new WorkspaceStoreException("The captured source does not match this copy command.");
        if (destination.ProjectId != command.Destination.ProjectId) throw new WorkspaceStoreException("Wrong destination project.");
        var source = content.Asset;
        var whole = content.Source.Kind == AssetReuseKind.Asset;
        if (whole && command.Destination.AssetId is not null) throw new WorkspaceStoreException("A whole asset is copied as a new asset, not merged into another one.");
        var existing = command.Destination.AssetId is { } target ? destination.Assets.SingleOrDefault(a => a.Id == target)
            ?? throw new WorkspaceStoreException("The destination asset was removed.") : null;
        if (content.Reels.Count > 0 && existing is not null && existing.Category != source.Category ||
            content.Voices.Count > 0 && existing is not null && existing.Category != AssetCategory.Character)
            throw new WorkspaceStoreException("Reels need an asset of the same category; voice recordings need a character.");
        var assetId = existing?.Id ?? Identity(command.Id, "asset", source.Id);
        var imageIds = source.Images.ToDictionary(i => i.Id, i => Identity(command.Id, "image", i.Id));
        var voiceIds = content.Voices.ToDictionary(v => v.Id, v => Identity(command.Id, "voice", v.Id));
        var lookIds = source.Looks.ToDictionary(l => l.Id, l => Identity(command.Id, "look", l.Id));
        Guid? Look(Guid? id) => id is null ? null : existing is null ? lookIds.GetValueOrDefault(id.Value) :
            destination.ProjectId == content.Source.ProjectId && existing.Id == source.Id ? id : null;
        IReadOnlyList<PreferredImageReference> Preferred(IReadOnlyList<PreferredImageReference> refs) => refs
            .Where(r => imageIds.ContainsKey(r.ImageId)).Select(r => r with { ImageId = imageIds[r.ImageId], LookId = Look(r.LookId) }).ToArray();
        var images = source.Images.Select(i => i with {
            Id = imageIds[i.Id], FileName = imageIds[i.Id].ToString("N") + Path.GetExtension(i.FileName),
            Name = whole ? i.Name : command.Destination.Name.Trim(), StorageAssetId = null, PreviousAssetIds = [],
            LookId = Look(i.LookId), Source = null, Generation = null, Origin = AssetImageOrigin.Imported,
            CreatedUtc = now, IsCover = existing is null && i.IsCover, Tags = [.. i.Tags]
        }).ToList();
        var voices = content.Voices.Select(v => v with {
            Id = voiceIds[v.Id], AssetId = assetId, StorageAssetId = null, PreviousAssetIds = null, SourceReel = null,
            FileName = voiceIds[v.Id].ToString("N") + Path.GetExtension(v.FileName), CreatedUtc = now,
            Name = whole ? v.Name : command.Destination.Name.Trim()
        }).ToList();
        var reels = content.Reels.Select(r => {
            var mediaId = Identity(command.Id, "reel-media", r.Media.Id);
            return r with { Id = Identity(command.Id, "reel", r.Id), AssetId = assetId, OriginalAssetId = null,
                LookId = Look(r.LookId), Media = r.Media with { Id = mediaId }, Generation = null, SourceTakeId = null,
                CreatedUtc = now, Name = whole ? r.Name : command.Destination.Name.Trim(),
                Keyframes = r.Keyframes is null ? null : new ReelKeyframeSet { Frames = r.Keyframes.Frames.Select(f => f with {
                    Id = Identity(command.Id, "keyframe", f.Id), Frame = f.Frame with { MediaId = mediaId } }).ToList() }
            };
        }).ToList();
        var owner = existing is not null ? existing with { Images = [.. existing.Images, .. images], UpdatedUtc = now } : source with {
            Id = assetId, Name = command.Destination.Name.Trim(), Images = images, Evidence = [], CreatedUtc = now, UpdatedUtc = now,
            DefaultVoiceId = source.DefaultVoiceId is { } voice && voiceIds.TryGetValue(voice, out var mapped) ? mapped : null,
            PreferredIdentityReferences = Preferred(source.PreferredIdentityReferences),
            Looks = source.Looks.Select(l => l with { Id = lookIds[l.Id], Evidence = [], PreferredAppearanceReferences = Preferred(l.PreferredAppearanceReferences) }).ToArray()
        };
        var result = destination.Copy() with { AssetReuseReceipts = [.. destination.AssetReuseReceipts ?? []] };
        if (existing is null) result.Assets.Add(owner); else result.Assets[result.Assets.FindIndex(a => a.Id == owner.Id)] = owner;
        result.Voices.AddRange(voices); result.Reels.AddRange(reels);
        Guid? media = content.Source.Kind switch { AssetReuseKind.Image => images.Single().Id,
            AssetReuseKind.Reel => reels.Single().Id, AssetReuseKind.Voice => voices.Single().Id, _ => null };
        result.AssetReuseReceipts!.Add(new(command.Id, Hash(command), assetId, media, content.Source, command.SharedEntryId, now));
        FileAssetStore.Validate(result, result.ProjectId);
        return (result, assetId, media);
    }
}
