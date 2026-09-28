using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class VisualDescriptionTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5WQAAAAASUVORK5CYII=");

    private static AssetImage Image(string? description = null) => new()
    {
        Id = Guid.NewGuid(), FileName = "reference.png", ContentType = "image/png",
        Width = 1, Height = 1, Name = "Front view", VisualDescription = description,
        PreservationGuidance = "Retain the gold trim."
    };

    private static ReferenceAsset Asset(AssetImage image) => new()
    {
        Id = Guid.NewGuid(), Name = "Reference", Category = AssetCategory.Character,
        Description = "Author's asset notes, not an observation.", Images = [image]
    };

    private static (Shot Shot, AssetLibrary Library) Scene(string? description = "A copper-haired figure in a navy coat.")
    {
        var image = Image(description);
        var asset = Asset(image);
        var shot = new Shot
        {
            Title = "Test shot", Duration = 5,
            Images = [new() { AssetId = asset.Id, MediaId = image.Id, Name = "Front view" }]
        };
        return (shot, new() { ProjectId = Guid.NewGuid(), Assets = [asset] });
    }

    private static PromptCompositionRequest Request(Shot shot, AssetLibrary library, bool? inspect = false) => new(
        library.ProjectId, Guid.NewGuid(), 0, "context", "source", shot, "Scene", [],
        shot.Images.Select(b => new ShotReferenceGuidance(b.Id, "", "", null)).ToArray(), [],
        ResolvedReferences.For(shot).Pictures.Select(p => new CompositionInput(p.BindingId, "hash")).ToArray(),
        "", "", "", new(AiBackend.OpenRouter, "example/text", "Example text model"))
    {
        InspectReferenceImages = inspect,
        VisualDescriptions = CompositionDescriptions.Capture(shot, library)
    };

    [Fact]
    public void MetadataSavesDescriptionSeparatelyAndCanClearIt()
    {
        var image = Image(); var asset = Asset(image);
        var before = ImageMetadataValues.From(image);
        var edit = new ImageMetadataEdit(Guid.NewGuid(), new(asset.Id, image.Id), before,
            before with { VisualDescription = "  A navy coat with gold trim.  " });
        var saved = edit.Apply(asset);
        Assert.Equal("A navy coat with gold trim.", saved.VisualDescription);
        Assert.Equal(image.PreservationGuidance, saved.PreservationGuidance);
        Assert.Equal(image.FileName, saved.FileName);
        var savedValues = ImageMetadataValues.From(saved);
        var cleared = (edit with { Original = savedValues, Value = savedValues with { VisualDescription = " " } })
            .Apply(asset with { Images = [saved] });
        Assert.Null(cleared.VisualDescription);
    }

    [Fact]
    public void EditingNameDoesNotOverwriteConcurrentDescription()
    {
        var image = Image("Earlier description"); var asset = Asset(image);
        var before = ImageMetadataValues.From(image);
        var edit = new ImageMetadataEdit(Guid.NewGuid(), new(asset.Id, image.Id), before, before with { Name = "New name" });
        var current = asset with { Images = [image with { VisualDescription = "Updated in another tab" }] };
        var saved = edit.Apply(current);
        Assert.Equal("New name", saved.Name);
        Assert.Equal("Updated in another tab", saved.VisualDescription);
    }

    [Fact]
    public void ConflictingDescriptionEditsAreRejectedWithoutMutation()
    {
        var image = Image("Original"); var asset = Asset(image);
        var before = ImageMetadataValues.From(image);
        var edit = new ImageMetadataEdit(Guid.NewGuid(), new(asset.Id, image.Id), before,
            before with { VisualDescription = "My draft" });
        var current = asset with { Images = [image with { VisualDescription = "Other tab" }] };
        Assert.Throws<InvalidOperationException>(() => edit.Apply(current));
        Assert.Equal("Other tab", current.Images[0].VisualDescription);
    }

    [Fact]
    public void MetadataRejectsOversizedDescription()
    {
        var image = Image(); var asset = Asset(image); var before = ImageMetadataValues.From(image);
        var edit = new ImageMetadataEdit(Guid.NewGuid(), new(asset.Id, image.Id), before,
            before with { VisualDescription = new string('x', 12001) });
        Assert.Throws<InvalidOperationException>(() => edit.Apply(asset));
    }

    [Fact]
    public void LibraryCopyAndJsonRoundTripKeepDescription()
    {
        var (_, library) = Scene();
        var copy = ShotCopy.Of(library.Copy());
        Assert.Equal(library.Assets[0].Images[0].VisualDescription, copy.Assets[0].Images[0].VisualDescription);
    }

    [Fact]
    public void LegacyImagesOmitNewFieldAndDefaultToNoDescription()
    {
        var image = Image();
        var json = JsonSerializer.Serialize(image, AtomicJsonFile.Options);
        Assert.DoesNotContain("visualDescription", json);
        Assert.Null(JsonSerializer.Deserialize<AssetImage>(json, AtomicJsonFile.Options)!.VisualDescription);
    }

    [Fact]
    public void GuidanceAndDescriptionContextsHaveIndependentTargetsAndText()
    {
        var image = Image("Visible navy coat"); var asset = Asset(image); var project = Guid.NewGuid();
        var oldTarget = new GuidanceTarget(project, asset.Id, GuidanceScope.Image, ImageId: image.Id);
        var newTarget = oldTarget with { Scope = GuidanceScope.ImageDescription };
        var guidance = GuidanceContext.From(oldTarget, asset)!;
        var description = GuidanceContext.From(newTarget, asset)!;
        Assert.Equal(image.PreservationGuidance, guidance.Guidance);
        Assert.Equal(image.VisualDescription, description.Guidance);
        Assert.NotEqual(guidance.Fingerprint(), description.Fingerprint());
        Assert.Null(GuidanceContext.From(newTarget with { ImageId = Guid.NewGuid() }, asset));
    }

    [Fact]
    public void QueueAcceptsExactDescriptionTargetAndSeparatesItsLock()
    {
        var target = new AiJobTarget(Guid.NewGuid(), Guid.NewGuid(),
            GuidanceScope: GuidanceScope.ImageDescription, ImageId: Guid.NewGuid());
        target.Validate(AiJobKind.Guidance);
        Assert.NotEqual(target.LockKey(AiJobKind.Guidance),
            (target with { GuidanceScope = GuidanceScope.Image }).LockKey(AiJobKind.Guidance));
        Assert.Throws<WorkspaceStoreException>(() => (target with { ImageId = null }).Validate(AiJobKind.Guidance));
        Assert.Throws<WorkspaceStoreException>(() => (target with { LookId = Guid.NewGuid() }).Validate(AiJobKind.Guidance));
    }

    [Fact]
    public void DescriptionAssistantRequiresExactlyTheTargetImage()
    {
        var image = Image(); var asset = Asset(image);
        var target = new GuidanceTarget(Guid.NewGuid(), asset.Id, GuidanceScope.ImageDescription, ImageId: image.Id);
        var request = new GuidanceRequest(GuidanceContext.From(target, asset)!,
            new(AiBackend.OpenRouter, "example/vision", "Example vision model"), new(asset.Id, image.Id));
        var messages = GuidanceAssistant.BuildMessages(request, Png);
        Assert.Single(messages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("600–1400", messages[0].Text);
        Assert.Contains("not proof", messages[0].Text);
        Assert.Throws<AiGenerationException>(() => GuidanceAssistant.BuildMessages(request, null));
        Assert.Throws<AiGenerationException>(() => GuidanceAssistant.BuildMessages(request with { InspectionImage = null }, Png));
        Assert.Throws<AiGenerationException>(() => GuidanceAssistant.BuildMessages(
            request with { InspectionImage = new(asset.Id, Guid.NewGuid()) }, Png));
    }

    [Fact]
    public void TextOnlyCompositionContainsDescriptionsButNoImageParts()
    {
        var (shot, library) = Scene(); var request = Request(shot, library);
        var original = JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
        var messages = PromptComposer.BuildMessages(request, []);
        Assert.Empty(messages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("DESCRIPTIONS ONLY", messages[0].Text);
        Assert.Contains("copper-haired", messages[1].Text);
        Assert.Equal(original, JsonSerializer.Serialize(shot, AtomicJsonFile.Options));
        Assert.Single(request.Images); // Reference identity remains even without an attachment.
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void VisionCompositionIncludesImageAndSavedDescription(bool? inspect)
    {
        var (shot, library) = Scene(); var request = Request(shot, library, inspect);
        var messages = PromptComposer.BuildMessages(request, [Png]);
        Assert.Single(messages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("copper-haired", messages[1].Text);
        Assert.DoesNotContain("REFERENCE INPUT MODE: DESCRIPTIONS ONLY", messages[0].Text);
    }

    [Fact]
    public void TextOnlyModeNeverAcceptsAccidentalImageAttachments()
    {
        var (shot, library) = Scene();
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(Request(shot, library), [Png]));
    }

    [Fact]
    public void ImageModeStillRejectsMissingAttachments()
    {
        var (shot, library) = Scene();
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(Request(shot, library, true), []));
    }

    [Fact]
    public void MissingDescriptionBlocksTextOnlyButNotLegacyVisionRequests()
    {
        var (shot, library) = Scene(null);
        Assert.Contains("<Picture 1>", CompositionDescriptions.MissingIssue(shot, library)!);
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(Request(shot, library), []));
        var legacy = Request(shot, library, null) with { VisualDescriptions = null };
        Assert.Single(PromptComposer.BuildMessages(legacy, [Png]).SelectMany(m => m.Contents).OfType<DataContent>());
    }

    [Fact]
    public void CapturedDescriptionDoesNotChangeWhenLibraryIsEdited()
    {
        var (shot, library) = Scene(); var request = Request(shot, library);
        var old = request.VisualDescriptions![0].Text;
        library.Assets[0].Images[0] = library.Assets[0].Images[0] with { VisualDescription = "Changed" };
        Assert.Equal(old, request.VisualDescriptions[0].Text);
        Assert.Equal("Changed", CompositionDescriptions.Capture(shot, library)[0].Text);
    }

    [Fact]
    public void DescriptionMappingIsBoundToReferenceIdentityAndNumber()
    {
        var (shot, library) = Scene(); var request = Request(shot, library);
        var description = request.VisualDescriptions![0];
        Assert.Throws<WorkspaceStoreException>(() => CompositionDescriptions.Validate(request with
        {
            VisualDescriptions = [description with { BindingId = Guid.NewGuid() }]
        }));
        Assert.Throws<WorkspaceStoreException>(() => CompositionDescriptions.Validate(request with
        {
            VisualDescriptions = [description with { Label = "<Picture 2>" }]
        }));
        Assert.Throws<WorkspaceStoreException>(() => CompositionDescriptions.Validate(request with
        {
            VisualDescriptions = [description, description]
        }));
        Assert.Throws<WorkspaceStoreException>(() => CompositionDescriptions.Validate(request with
        {
            VisualDescriptions = [null!]
        }));
    }

    [Fact]
    public void DescriptionFingerprintTracksContentButPreservesUndescribedLegacyHash()
    {
        var (shot, library) = Scene(null);
        Assert.Equal("original", CompositionDescriptions.Fingerprint("original", shot, library));
        library.Assets[0].Images[0] = library.Assets[0].Images[0] with { VisualDescription = "A navy coat" };
        var first = CompositionDescriptions.Fingerprint("original", shot, library);
        Assert.NotEqual("original", first);
        library.Assets[0].Images[0] = library.Assets[0].Images[0] with { VisualDescription = "A red coat" };
        Assert.NotEqual(first, CompositionDescriptions.Fingerprint("original", shot, library));
    }

    [Fact]
    public void CropIsExplicitAndNeverReinterpretedAsAFullImageObservation()
    {
        var (shot, library) = Scene();
        shot.Images[0].Crop = new() { X = .1, Y = .1, Width = .5, Height = .5 };
        var request = Request(shot, library);
        Assert.Equal(shot.Images[0].Crop, request.VisualDescriptions![0].Crop);
        Assert.Contains("full original image", PromptComposer.BuildMessages(request, [])[0].Text);
    }

    [Fact]
    public void QueuedTextOnlySnapshotRoundTripsWithoutVisionAttachments()
    {
        var (shot, library) = Scene(); var payload = Request(shot, library);
        var request = new AiTextJobRequest(2, AiJobKind.PromptComposition, payload.Model, false,
            new AiSettings(), ProductionPolicy.Profile, .7f, 1,
            JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options),
            PromptComposer.BuildMessages(payload, []).Select(AiTextMessage.Capture).ToArray());
        var header = new AiJobHeader
        {
            Id = Guid.NewGuid(), Kind = request.Kind, Backend = request.Model.Backend,
            Target = new(payload.ProjectId, ShotId: payload.Shot.Id, CompositionId: payload.CompositionId),
            ProjectName = "Test", TargetName = "Compose", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test"
        };
        var read = AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options));
        Assert.False(read.InspectsImages);
        Assert.False(read.Payload<PromptCompositionRequest>().InspectReferenceImages);
        Assert.Single(read.Payload<PromptCompositionRequest>().Images);
        Assert.Single(read.Payload<PromptCompositionRequest>().VisualDescriptions!);
        var accidentalImage = request with { Messages = [new("user", [new(Text: "text"), new(Image: Png, MediaType: "image/png")])] };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header,
            JsonSerializer.SerializeToElement(accidentalImage, AtomicJsonFile.Options)));
    }

    [Fact]
    public void LegacyCompositionJsonDoesNotGainNullableFields()
    {
        var (shot, library) = Scene(null);
        var legacy = Request(shot, library, null) with { VisualDescriptions = null };
        var json = JsonSerializer.Serialize(legacy, AtomicJsonFile.Options);
        Assert.DoesNotContain("inspectReferenceImages", json);
        Assert.DoesNotContain("visualDescriptions", json);
        var read = JsonSerializer.Deserialize<PromptCompositionRequest>(json, AtomicJsonFile.Options)!;
        Assert.Null(read.InspectReferenceImages);
        Assert.Null(read.VisualDescriptions);
        CompositionDescriptions.Validate(read);
    }
}
