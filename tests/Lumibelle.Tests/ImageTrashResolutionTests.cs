using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed class ImageTrashResolutionTests
{
    [Fact]
    public void OnlyExactRecordedInputsResolveFromTrashAndPurgingImagesAreUnavailable()
    {
        var project = Guid.NewGuid(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Source asset" };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "image.png", ContentType = "image/png", Width = 10, Height = 20 };
        var now = DateTimeOffset.UtcNow;
        var trash = new TrashedImage { Asset = asset, Image = image, DeletedUtc = now, ExpiresUtc = now.AddDays(30) };
        var library = new AssetLibrary { ProjectId = project, Trash = [trash] };
        var reference = new AssetImageReference(asset.Id, image.Id);
        foreach (var label in new[] { "Source", "Reference 2" })
        {
            var resolved = ReviewImageResolution.Resolve(library, new(reference, label));
            Assert.Equal(ReviewImageState.Trashed, resolved.State);
            Assert.Equal(image.Id, resolved.Image!.Id); Assert.Contains(trash.Id.ToString(), resolved.MediaUrl);
        }
        Assert.Equal(ReviewImageState.Unavailable, ReviewImageResolution.Resolve(library, new(reference, "Take 1", true)).State);
        Assert.Equal(ReviewImageState.Unavailable, ReviewImageResolution.Resolve(library, new(new(Guid.NewGuid(), image.Id), "Source")).State);
        Assert.False(trash.CanRestore(trash.ExpiresUtc));
        library = library with { Trash = [trash with { State = ImageTrashState.Purging }] };
        Assert.Equal(ReviewImageState.Unavailable, ReviewImageResolution.Resolve(library, new(reference, "Source")).State);
        library = library with { Assets = [asset with { Images = [image] }], Trash = [] };
        Assert.Equal(ReviewImageState.Active, ReviewImageResolution.Resolve(library, new(reference, "Source")).State);
    }
}
