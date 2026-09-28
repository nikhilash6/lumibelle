using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AssetReusePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static AssetLibrary Library()
    {
        var assetId = Guid.NewGuid(); var look = new CharacterLook { Name = "Day outfit", Description = "White shirt" };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "face.png", ContentType = "image/png", Width = 64, Height = 48,
            Name = "Face", Tags = ["front"], VisualDescription = "Short auburn hair and round glasses.", PreservationGuidance = "Keep the face", LookId = look.Id,
            Origin = AssetImageOrigin.Generated, Generation = new() { AiJobId = Guid.NewGuid(), BatchId = Guid.NewGuid(), CandidateNumber = 1 }, IsCover = true, IsReference = true, CreatedUtc = Now };
        var voice = new VoiceReference { Id = Guid.NewGuid(), AssetId = assetId, Name = "Mira voice", Duration = 5, Start = 1, ExcerptDuration = 2, ContentType = "audio/wav", CreatedUtc = Now };
        voice.FileName = voice.Id.ToString("N") + ".wav";
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new string('A', 64), 4, 64, 48, 48, 24, 2, true);
        var reel = new AssetReferenceReel { AssetId = assetId, Name = "Turntable", UseGuidance = "Identity, not motion", Media = media,
            LookId = look.Id, CreatedUtc = Now, Keyframes = new() { Frames = [new() {
                Frame = new(media.Id, media.Sha256, 12, .5), Notes = "Side profile", Crop = new() { Width = .5 } }] } };
        return new() { ProjectId = Guid.NewGuid(), Assets = [new() { Id = assetId, Name = "Mira", Category = AssetCategory.Character,
            Description = "Main character", PreservationGuidance = "Stable identity", Images = [image], Looks = [look with {
                PreferredAppearanceReferences = [new(image.Id, look.Id, "Outfit")] }], PreferredIdentityReferences = [new(image.Id, look.Id, "Face")],
            DefaultVoiceId = voice.Id, Evidence = [new("Scene one", Guid.NewGuid(), "Mira arrives")], CreatedUtc = Now, UpdatedUtc = Now }], Reels = [reel], Voices = [voice] };
    }
    private static AssetReuseSelection Selection(AssetLibrary library, AssetReuseKind kind = AssetReuseKind.Asset) => new(library.ProjectId, library.Assets[0].Id, kind,
        kind switch { AssetReuseKind.Image => library.Assets[0].Images[0].Id, AssetReuseKind.Reel => library.Reels[0].Id, AssetReuseKind.Voice => library.Voices[0].Id, _ => null });
    private static AssetReuseCommand Command(AssetReuseContent content, Guid destination, Guid? asset = null) =>
        new(Guid.NewGuid(), content.Source, null, AssetReusePolicy.Hash(content), new(destination, "Mira copy", asset));

    [Fact]
    public void WholeAssetRemapsAllIdentitiesAndPreservesReusableDescriptionsAndExcerpts()
    {
        var library = Library(); var original = library.Copy(); var content = AssetReusePolicy.Capture(library, Selection(library));
        var destination = new AssetLibrary { ProjectId = Guid.NewGuid() }; var command = Command(content, destination.ProjectId);
        var result = AssetReusePolicy.Import(content, destination, command, Now); var asset = Assert.Single(result.Library.Assets);
        Assert.NotEqual(content.Asset.Id, asset.Id); Assert.Null(result.MediaId); Assert.Equal("Mira copy", asset.Name);
        Assert.NotEqual(content.Asset.Looks[0].Id, asset.Looks[0].Id); Assert.NotEqual(content.Asset.Images[0].Id, asset.Images[0].Id);
        Assert.Equal(asset.Looks[0].Id, asset.Images[0].LookId); Assert.Equal(asset.Images[0].Id, asset.PreferredIdentityReferences[0].ImageId);
        Assert.Equal(asset.Images[0].Id, asset.Looks[0].PreferredAppearanceReferences[0].ImageId);
        Assert.Equal(content.Asset.Images[0].VisualDescription, asset.Images[0].VisualDescription);
        Assert.Equal(content.Asset.Images[0].PreservationGuidance, asset.Images[0].PreservationGuidance);
        Assert.True(asset.Images[0].IsCover); Assert.True(asset.Images[0].IsReference); Assert.Empty(asset.Evidence);
        Assert.Null(asset.Images[0].Generation); Assert.Null(asset.Images[0].Source); Assert.Equal(AssetImageOrigin.Imported, asset.Images[0].Origin);
        var voice = Assert.Single(result.Library.Voices); Assert.NotEqual(content.Voices[0].Id, voice.Id);
        Assert.Equal(asset.Id, voice.AssetId); Assert.Equal(voice.Id, asset.DefaultVoiceId); Assert.Equal(1, voice.Start); Assert.Equal(2, voice.ExcerptDuration);
        var reel = Assert.Single(result.Library.Reels); Assert.NotEqual(content.Reels[0].Id, reel.Id); Assert.NotEqual(content.Reels[0].Media.Id, reel.Media.Id);
        Assert.Equal(reel.Media.Id, reel.Keyframes!.Frames[0].Frame.MediaId); Assert.Equal(content.Reels[0].Media.Sha256, reel.Keyframes.Frames[0].Frame.Source);
        Assert.NotEqual(content.Reels[0].Keyframes!.Frames[0].Id, reel.Keyframes.Frames[0].Id);
        Assert.Equal("Side profile", reel.Keyframes.Frames[0].Notes); Assert.Equal(.5, reel.Keyframes.Frames[0].Crop!.Width);
        Assert.Empty(destination.Assets); Assert.Equal(AssetReusePolicy.Hash(original), AssetReusePolicy.Hash(library));
    }
    [Theory]
    [InlineData(AssetReuseKind.Image)] [InlineData(AssetReuseKind.Reel)] [InlineData(AssetReuseKind.Voice)]
    public void IndividualSelectionCopiesOnlyItsChosenMedia(AssetReuseKind kind)
    {
        var library = Library(); var content = AssetReusePolicy.Capture(library, Selection(library, kind));
        Assert.Equal(1, content.Asset.Images.Count + content.Reels.Count + content.Voices.Count);
        var result = AssetReusePolicy.Import(content, library, Command(content, library.ProjectId, library.Assets[0].Id), Now);
        Assert.Single(result.Library.Assets); Assert.NotNull(result.MediaId); Assert.NotEqual(content.Source.MediaId, result.MediaId);
        Assert.Equal(kind == AssetReuseKind.Image ? 2 : 1, result.Library.Assets[0].Images.Count);
        Assert.Equal(kind == AssetReuseKind.Reel ? 2 : 1, result.Library.Reels.Count);
        Assert.Equal(kind == AssetReuseKind.Voice ? 2 : 1, result.Library.Voices.Count);
        Assert.Single(result.Library.Assets[0].Images, i => i.IsCover);
        Assert.Equal(library.Assets[0].DefaultVoiceId, result.Library.Assets[0].DefaultVoiceId);
    }
    [Fact]
    public void SameOwnerImageKeepsLookButDifferentOwnerImageIsUnassigned()
    {
        var library = Library(); var content = AssetReusePolicy.Capture(library, Selection(library, AssetReuseKind.Image));
        var same = AssetReusePolicy.Import(content, library, Command(content, library.ProjectId, content.Asset.Id), Now);
        Assert.Equal(content.Asset.Images[0].LookId, same.Library.Assets[0].Images.Last().LookId);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Props", Category = AssetCategory.Prop };
        var different = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner] };
        var copied = AssetReusePolicy.Import(content, different, Command(content, different.ProjectId, owner.Id), Now);
        Assert.Null(Assert.Single(copied.Library.Assets[0].Images).LookId);
    }
    [Theory]
    [InlineData(AssetReuseKind.Asset)] [InlineData(AssetReuseKind.Reel)] [InlineData(AssetReuseKind.Voice)]
    public void IncompatibleDestinationRejectsBeforeMutation(AssetReuseKind kind)
    {
        var source = Library(); var content = AssetReusePolicy.Capture(source, Selection(source, kind));
        var destination = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [new() { Id = Guid.NewGuid(), Name = "A prop", Category = AssetCategory.Prop }] };
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Import(content, destination, Command(content, destination.ProjectId, destination.Assets[0].Id), Now));
        Assert.Empty(destination.Assets[0].Images); Assert.Empty(destination.Reels); Assert.Empty(destination.Voices);
    }
    [Fact]
    public void CapturesAreDetachedFromAuthorMutableLists()
    {
        var source = Library(); var captured = AssetReusePolicy.Capture(source, Selection(source)); var hash = AssetReusePolicy.Hash(captured);
        source.Assets[0].Images[0].Tags.Add("changed"); source.Reels[0].Keyframes!.Frames[0].Notes = "changed"; source.Voices[0].Start = 0;
        Assert.Equal(hash, AssetReusePolicy.Hash(captured));
    }
    [Fact]
    public void StableCommandProducesStableMappingsButDifferentCommandsProduceIndependentCopies()
    {
        var source = Library(); var content = AssetReusePolicy.Capture(source, Selection(source)); var destination = new AssetLibrary { ProjectId = Guid.NewGuid() };
        var command = Command(content, destination.ProjectId);
        var first = AssetReusePolicy.Import(content, destination, command, Now);
        var retry = AssetReusePolicy.Import(content, destination, command, Now);
        Assert.Equal(AssetReusePolicy.Hash(first.Library), AssetReusePolicy.Hash(retry.Library));
        Assert.NotEqual(first.AssetId, AssetReusePolicy.Import(content, destination, command with { Id = Guid.NewGuid() }, Now).AssetId);
    }
    [Fact]
    public void ImportRejectsAChangedCaptureOrDifferentSource()
    {
        var source = Library(); var content = AssetReusePolicy.Capture(source, Selection(source)); var destination = new AssetLibrary { ProjectId = Guid.NewGuid() };
        var command = Command(content, destination.ProjectId);
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Import(content with { Asset = content.Asset with { Description = "Changed" } }, destination, command, Now));
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Import(content, destination, command with { Source = command.Source! with { ProjectId = Guid.NewGuid() } }, Now));
    }
    [Theory]
    [InlineData(AssetReuseKind.Asset)] [InlineData(AssetReuseKind.Image)] [InlineData(AssetReuseKind.Reel)] [InlineData(AssetReuseKind.Voice)]
    public void ClipboardRoundTripsExactSelectionAndFingerprint(AssetReuseKind kind)
    {
        var library = Library(); var selection = Selection(library, kind); var text = AssetReusePolicy.Clipboard(library, selection);
        Assert.True(text.Length < AssetReusePolicy.MaximumClipboardCharacters);
        var item = AssetReusePolicy.ParseClipboard(text); Assert.Equal(1, item.Version); Assert.Equal(selection, item.Source);
        Assert.Equal(AssetReusePolicy.Hash(AssetReusePolicy.Capture(library, selection)), item.Fingerprint);
    }
    [Theory]
    [InlineData("")] [InlineData("https://example.test/file.png")] [InlineData("lumibelle-asset:v1:???")] [InlineData("lumibelle-asset:v1:e30=")]
    public void ClipboardRejectsNonApplicationOrMalformedPayloads(string input) =>
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.ParseClipboard(input));
    [Fact]
    public void ClipboardRejectsOversizedPayloadAndRemovedMedia()
    {
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.ParseClipboard(AssetReusePolicy.ClipboardPrefix + new string('A', 4096)));
        var library = Library(); var source = Selection(library, AssetReuseKind.Image);
        library.Assets[0].Images.Clear(); Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Capture(library, source));
    }
    [Fact]
    public void MoveAndSourceValidationRejectAmbiguousCommands()
    {
        var library = Library(); var content = AssetReusePolicy.Capture(library, Selection(library)); var command = Command(content, library.ProjectId);
        foreach (var invalid in new[] { command with { Id = Guid.Empty }, command with { Source = null }, command with { SharedEntryId = Guid.NewGuid() },
            command with { Move = true }, command with { Destination = command.Destination with { Name = " " } }, command with { SourceFingerprint = "bad" } })
            Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Validate(invalid));
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Validate(Selection(library) with { MediaId = Guid.NewGuid() }));
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.Validate(Selection(library, AssetReuseKind.Image) with { MediaId = null }));
    }
    [Theory]
    [InlineData("../escape")] [InlineData("/absolute")] [InlineData("C:/file")] [InlineData("a\\file")] [InlineData("a//b")] [InlineData("a/./b")]
    public void PackagePathsRejectTraversalAndPlatformAliases(string path) => Assert.False(FileAssetStore.ReuseRelativePath(path));
    [Fact]
    public void IdentityScannerMatchesNestedGuidValuesNotSubstrings()
    {
        var id = Guid.NewGuid(); var ids = new HashSet<Guid> { id };
        Assert.True(AssetReusePolicy.ContainsIdentity(JsonSerializer.SerializeToElement(new { nested = new[] { new { id } } }), ids));
        Assert.False(AssetReusePolicy.ContainsIdentity(JsonSerializer.SerializeToElement(new { note = "Mention " + id, url = "https://test/" + id }), ids));
    }
    [Fact]
    public void NewOptionalReceiptsDoNotChangeLegacySerializationAndCopyDoesNotShareReceiptLists()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(library, AtomicJsonFile.Options));
        Assert.False(json.RootElement.TryGetProperty("assetReuseReceipts", out _)); Assert.False(json.RootElement.TryGetProperty("assetMoveReceipts", out _));
        var receipt = new AssetMoveReceipt(Guid.NewGuid(), new string('A', 64)); library = library with { AssetMoveReceipts = [receipt] };
        var copy = library.Copy(); copy.AssetMoveReceipts!.Clear(); Assert.Single(library.AssetMoveReceipts!);
    }
    [Fact]
    public void InvalidOrDuplicateReceiptsAreRejected()
    {
        var receipt = new AssetMoveReceipt(Guid.NewGuid(), new string('A', 64));
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.ValidateReceipts(new() { ProjectId = Guid.NewGuid(), AssetMoveReceipts = [receipt, receipt] }));
        Assert.Throws<WorkspaceStoreException>(() => AssetReusePolicy.ValidateReceipts(new() { ProjectId = Guid.NewGuid(), AssetMoveReceipts = [receipt with { Fingerprint = "bad" }] }));
    }
}
