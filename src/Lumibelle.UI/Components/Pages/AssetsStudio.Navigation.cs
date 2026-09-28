using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private Guid? _linkedAssetId;

    private async Task ConsumeAssetNavigationAsync()
    {
        if (_disposed || _loadedProject != Id || RequestedAssetId is not { } assetId) return;
        // Image links need their recorded owner until the image has been resolved.
        if (RequestedImageId is { } imageId)
        {
            if (_appliedImageRequest != (Id, imageId, EditRequestedImage)) return;
        }
        else if (_selectedAssetId != assetId) return;

        _linkedAssetId = assetId;
        var parameters = new Dictionary<string, object?> { ["assetId"] = null };
        if (RequestedImageId is not null) { parameters["imageId"] = null; parameters["edit"] = null; }
        var url = Navigation.GetUriWithQueryParameters(parameters);
        if (url == Navigation.Uri) return;
        // Selection lives in workspace preferences; the link is only an entry point.
        await RememberAssetPosition();
        if (_disposed) return;
        // A review link removes its own jobId concurrently. Computed from the same earlier
        // URL, this replacement would otherwise restore it and replay the review on reload.
        if (RequestedJobId is { } job && job == _handledEnhancementNavigation) parameters["jobId"] = null;
        url = Navigation.GetUriWithQueryParameters(parameters);
        if (url != Navigation.Uri) Navigation.NavigateTo(url, replace: true);
    }
}
