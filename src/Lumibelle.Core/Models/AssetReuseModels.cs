namespace lumibelle.Models;

public enum AssetReuseKind { Asset, Image, Reel, Voice }
public sealed record AssetReuseSelection(Guid ProjectId, Guid AssetId, AssetReuseKind Kind = AssetReuseKind.Asset, Guid? MediaId = null);
public sealed record AssetReuseDestination(Guid ProjectId, string Name, Guid? AssetId = null);
// Exactly one of Source and SharedEntryId is supplied. Id is retained when retrying a failed transfer.
public sealed record AssetReuseCommand(Guid Id, AssetReuseSelection? Source, Guid? SharedEntryId,
    string SourceFingerprint, AssetReuseDestination Destination, bool Move = false);
public sealed record AssetReuseContent(AssetReuseSelection Source, ReferenceAsset Asset,
    IReadOnlyList<AssetReferenceReel> Reels, IReadOnlyList<VoiceReference> Voices);
public sealed record AssetReuseReceipt(Guid CommandId, string Fingerprint, Guid AssetId, Guid? MediaId,
    AssetReuseSelection Source, Guid? SharedEntryId, DateTimeOffset CopiedUtc);
public sealed record AssetMoveReceipt(Guid CommandId, string Fingerprint);
public sealed record AssetReuseResult(AssetLibrary Library, Guid AssetId, Guid? MediaId,
    bool Available, bool SourceRemoved = false, string? Notice = null);
public sealed record SharedAssetEntry(Guid Id, string Name, AssetCategory Category, DateTimeOffset CreatedUtc,
    string SourceFingerprint, AssetReuseSelection Source, int Images, int Reels, int Voices, long Bytes);
public sealed record AssetReuseFile(string Path, long Bytes, string Sha256);
public sealed record AssetReusePackage(int Version, Guid Id, string Name, DateTimeOffset CreatedUtc,
    string SourceFingerprint, AssetReuseContent Content, IReadOnlyList<AssetReuseFile> Files);
public sealed record AssetClipboardItem(int Version, AssetReuseSelection Source, string Fingerprint);

public sealed record AssetReuseInstalledFiles(Guid CommandId, string Fingerprint, IReadOnlyList<AssetReuseFile> Files);
