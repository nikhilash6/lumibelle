using Bunit;
using lumibelle.Models;
using lumibelle.Components.Pages;
using lumibelle.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    private async Task ToggleDescriptionBadges(IRenderedComponent<AssetsStudio> page)
    {
        await page.Find("[aria-label='Gallery options']").ClickAsync(new());
        await _popovers.WaitForElement("[role='menuitemcheckbox'][aria-label='Highlight missing descriptions']").ClickAsync(new());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(" \n ", true)]
    [InlineData("A woman in a red coat.", false)]
    public async Task GalleryFlagsImagesWithoutSavedDescriptions(string? description, bool needsDescription)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png",
            Width = 64, Height = 64, VisualDescription = description, CreatedUtc = DateTimeOffset.UtcNow.AddYears(-1) };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }] };
        var page = Page(); page.WaitForElement(".asset-media-card");
        Assert.Empty(page.FindAll(".image-description-needed"));
        await ToggleDescriptionBadges(page);
        Assert.Equal(needsDescription, page.FindAll(".image-description-needed").Any());
    }

    [Fact]
    public async Task DescriptionBadgesAreRememberedForTheProjectAndCanBeTurnedOff()
    {
        var positions = Services.GetRequiredService<WorkspacePositions>();
        await positions.LoadAsync(_projectId, "assets");
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 64, Height = 64 };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }, Asset("Coat") with { Images = [image with { Id = Guid.NewGuid() }] }] };
        var page = Page(); page.WaitForElement(".asset-media-card");
        await ToggleDescriptionBadges(page);
        Assert.True(positions.Get(_projectId, "assets", "showMissingDescriptions", false));
        Assert.False(positions.Get(Guid.NewGuid(), "assets", "showMissingDescriptions", false));
        await page.FindAll(".asset-choice")[1].ClickAsync(new());
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".image-description-needed")));
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(false);
        page.WaitForElement(".image-description-needed");
        await ToggleDescriptionBadges(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".image-description-needed")));
        Assert.False(positions.Get(_projectId, "assets", "showMissingDescriptions", true));
        Assert.All(_assets.Library.Assets, a => Assert.Null(a.Images[0].VisualDescription));
        Assert.Equal(0, _generator.Calls); Assert.Equal(0, _editor.Calls);
    }

    [Fact]
    public async Task DescriptionBadgeOpensExactImageAndClearsOnlyAfterSaving()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 64, Height = 64 };
        var other = image with { Id = Guid.NewGuid(), Name = "Other image" };
        var asset = Asset("Mira") with { Images = [image, other] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement(".asset-media-card");
        await ToggleDescriptionBadges(page);
        await page.Find($"[data-media-id='{image.Id}'] .image-description-needed").ClickAsync(new());
        var description = _dialogs.WaitForElement("textarea[aria-label='Visual description']");
        Assert.True(description.Closest("details")!.HasAttribute("open"));
        Assert.Empty(page.FindAll(".media-select[aria-pressed='true']"));
        await description.InputAsync(new() { Value = "A woman in a red coat." });
        Assert.Equal(2, page.FindAll(".image-description-needed").Count);
        await ClickReviewAsync("Save details");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".image-description-needed")));
        Assert.Equal("A woman in a red coat.", _assets.Library.Assets[0].Images.Single(i => i.Id == image.Id).VisualDescription);
        Assert.Null(_assets.Library.Assets[0].Images.Single(i => i.Id == other.Id).VisualDescription);
        Assert.Single(page.FindAll($"[data-media-id='{other.Id}'] .image-description-needed"));
        await ClickReviewSelector("[aria-label='Close image review']");

        // A normal preview should not inherit the shortcut's expanded description.
        await page.Find($"[data-media-id='{image.Id}'] .media-preview").ClickAsync(new());
        description = _dialogs.WaitForElement("textarea[aria-label='Visual description']");
        Assert.False(description.Closest("details")!.HasAttribute("open"));
        Assert.Equal(0, _generator.Calls); Assert.Equal(0, _editor.Calls);
    }
}
