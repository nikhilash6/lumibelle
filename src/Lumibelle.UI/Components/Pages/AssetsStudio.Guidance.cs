using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private Guid? _pendingGuidanceImage;
    private void HandleRequestedGuidance()
    {
        if (!_extractionInitialized || RequestedJobId is not { } id || _requestedGuidanceId == id || SelectedAsset is not { } asset) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.Guidance && j.Target.ProjectId == Id && j.Target.AssetId == asset.Id);
        if (job is null) return;
        if (job.Target.GuidanceScope is GuidanceScope.Image or GuidanceScope.ImageDescription) { _lookFilter = "all"; _pendingGuidanceImage = job.Target.ImageId; }
        else _pendingCenterTab = "Details";
        _requestedGuidanceId = id;
        if (GuidanceContext.From(new(Id, asset.Id, job.Target.GuidanceScope!.Value, job.Target.LookId, job.Target.ImageId), asset) is null)
            _saveError = "This guidance field is missing or archived. Restore it to review here; the saved response remains available in AI activity.";
        StateHasChanged();
    }
    private bool IsRequestedGuidanceImage(Guid id) => _requestedGuidanceId is { } job &&
        AiJobs.View.Jobs.Any(j => j.Id == job && j.Target.ProjectId == Id && j.Target.AssetId == _selectedAssetId && j.Target.ImageId == id);
}
