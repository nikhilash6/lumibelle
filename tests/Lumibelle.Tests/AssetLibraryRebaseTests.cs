using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AssetLibraryRebaseTests
{
    private static AssetLibrary Baseline()
    {
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", Description = "Original identity", Category = AssetCategory.Character,
            Images = [new() { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 20, Height = 30 }] };
        return new() { ProjectId = Guid.NewGuid(), Revision = 1, Assets = [asset] };
    }

    [Fact]
    public void BackgroundPublicationPreservesNotesAndImageEditsWithoutDroppingReceipts()
    {
        var baseline = Baseline(); var asset = baseline.Assets[0]; var source = asset.Images[0];
        var draft = baseline with { Assets = [asset with { Description = "Still typing", Images = [source with { Tags = ["identity"], IsReference = true }] }] };
        var image = source with { Id = Guid.NewGuid(), FileName = "take.png" };
        var saved = baseline with { Revision = 2, Assets = [asset with { Images = [source, image] }], ImagePublications = [new(Guid.NewGuid(), image.Id, asset.Id, new string('0', 64))] };
        var merged = AssetLibraryRebase.Merge(baseline, draft, saved);
        Assert.True(AssetLibraryRebase.HasMediaChanges(baseline, saved));
        Assert.Equal("Still typing", merged.Assets[0].Description); Assert.Equal(2, merged.Assets[0].Images.Count);
        Assert.True(merged.Assets[0].Images[0].IsReference); Assert.Equal(new[] { "identity" }, merged.Assets[0].Images[0].Tags);
        Assert.Equal(saved.ImagePublications, merged.ImagePublications); Assert.Equal(2, merged.Revision);
        merged.Assets[0].Images[0].Tags.Clear(); Assert.Single(draft.Assets[0].Images[0].Tags);
    }

    [Fact]
    public void DiscardedUneditedImageStaysDiscardedButConflictingImageEditNeedsReview()
    {
        var baseline = Baseline(); var asset = baseline.Assets[0]; var source = asset.Images[0];
        var saved = baseline with { Revision = 2, Assets = [asset with { Images = [] }],
            Trash = [new() { Asset = asset with { Images = [] }, Image = source, DeletedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) }] };
        var draft = baseline with { Assets = [asset with { Description = "New notes" }] };
        var merged = AssetLibraryRebase.Merge(baseline, draft, saved);
        Assert.Empty(merged.Assets[0].Images); Assert.Single(merged.Trash); Assert.Equal("New notes", merged.Assets[0].Description);
        var changedImage = draft with { Assets = [draft.Assets[0] with { Images = [source with { PreservationGuidance = "Keep face" }] }] };
        Assert.Throws<WorkspaceConflictException>(() => AssetLibraryRebase.Merge(baseline, changedImage, saved));
    }

    [Fact]
    public void CompetingMetadataChangesRemainConflictsAndNewAssetsSurviveBackgroundMedia()
    {
        var baseline = Baseline(); var asset = baseline.Assets[0];
        var saved = baseline with { Revision = 2, Assets = [asset with { Description = "Another tab" }] };
        Assert.False(AssetLibraryRebase.HasMediaChanges(baseline, saved));
        var draft = baseline with { Assets = [asset with { Description = "Local draft" }] };
        Assert.Throws<WorkspaceConflictException>(() => AssetLibraryRebase.Merge(baseline, draft, saved));
        var created = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Bedroom", Category = AssetCategory.Environment };
        var merged = AssetLibraryRebase.Merge(baseline, baseline with { Assets = [asset, created] }, saved);
        Assert.Equal(new[] { "Juniper", "Bedroom" }, merged.Assets.Select(a => a.Name));
        Assert.Equal("Another tab", merged.Assets[0].Description);
    }
}
