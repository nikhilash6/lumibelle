using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private bool _bulkGenerateOpen;
    private bool _bulkGenerateBusy;
    private string? _bulkGenerateError;
    private string? _bulkGenerateMessage;
    private string _bulkGeneratePreset = H3Presets.Keys.FirstOrDefault() ?? "standard";
    private string _bulkGenerateResolution = "current";
    private int _bulkGenerateTakeCount = 1;
    private HashSet<Guid> _bulkGenerateSelected = [];
    private IReadOnlyList<BulkGenerateTarget> _bulkGenerateTargets = [];
    private readonly Dictionary<Guid, AiJobSubmission> _bulkGeneratePending = [];

    private sealed record BulkGenerateTarget(Guid ShotId, Guid? CompositionId, Guid? SceneId, string Title, bool Eligible, string? Issue);
    private bool BulkGenerateLocked => _bulkGenerateBusy || _bulkGeneratePending.Count > 0;

    private int BulkGenerateSelectedCount => _bulkGenerateSelected.Count;

    private void OpenBulkGenerate()
    {
        if (BulkGenerateLocked) { _bulkGenerateOpen = true; return; }
        _bulkGenerateTargets = DiscoverBulkGenerateTargets();
        _bulkGenerateError = null;
        _bulkGenerateMessage = null;
        _bulkGenerateOpen = true;
        _bulkGeneratePreset = Current?.Shot is { } current ? H3Presets.Key(current) : H3Presets.Keys.FirstOrDefault() ?? "standard";
        _bulkGenerateResolution = Current?.Shot is { UpscalePreview: true } ? "upscaled" : Current?.Shot is { } currentShot ? VideoResolutions.Key(VideoResolutions.Selected(currentShot)) : "current";
        _bulkGenerateTakeCount = Current?.TakeCount is > 0 and <= 4 ? Current.TakeCount : 1;
        _bulkGenerateSelected = _bulkGenerateTargets.Where(t => t.Eligible).Select(t => t.ShotId).ToHashSet();
        if (_bulkGenerateTargets.Count == 0) _bulkGenerateError = "No shots are available for bulk generation.";
    }

    private void CloseBulkGenerate()
    {
        if (BulkGenerateLocked) return;
        _bulkGenerateOpen = false;
        _bulkGenerateError = null;
        _bulkGenerateMessage = null;
    }

    private void BulkGenerateVisibility(bool visible)
    {
        if (visible) _bulkGenerateOpen = true;
        else CloseBulkGenerate();
    }

    private async Task QueueBulkGenerateAsync()
    {
        if (_bulkGenerateBusy) return;
        var selected = _bulkGenerateSelected.ToHashSet();
        if (selected.Count == 0)
        {
            _bulkGenerateError = "Select at least one shot to queue.";
            return;
        }

        _bulkGenerateBusy = true;
        _bulkGenerateError = null;
        _bulkGenerateMessage = null;
        try
        {
            var preset = _bulkGeneratePreset;
            var resolution = _bulkGenerateResolution;
            var takeCount = _bulkGenerateTakeCount;
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) { _bulkGenerateError = "Save the current shot before queueing the selection."; return; }
            var queued = 0;
            var errors = new List<string>();
            foreach (var target in _bulkGenerateTargets.Where(t => selected.Contains(t.ShotId)))
            {
                if (!target.Eligible) continue;
                try
                {
                    await QueueCompositionWithPresetAsync(target, preset, resolution, takeCount);
                    _bulkGenerateSelected.Remove(target.ShotId);
                    queued++;
                }
                catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or IOException)
                {
                    errors.Add($"{target.Title}: {e.Message}");
                }
            }

            _bulkGenerateError = errors.Count > 0 ? string.Join(" ", errors) : null;
            if (_bulkGeneratePending.Count > 0) _bulkGenerateError += " Retry the remaining selection to confirm the same saved requests; their captured settings are retained.";
            if (queued == 0 && errors.Count == 0) _bulkGenerateError = "None of the selected shots could be queued. Review each shot's readiness and active jobs.";
            if (queued > 0) _bulkGenerateMessage = $"Queued {queued} shot{(queued == 1 ? string.Empty : "s")} with {H3Presets.Label(preset)}, {BulkResolutionLabel()} and {takeCount} take{(takeCount == 1 ? string.Empty : "s")} each.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or IOException)
        {
            _bulkGenerateError = e.Message;
        }
        finally
        {
            _bulkGenerateBusy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task QueueCompositionWithPresetAsync(BulkGenerateTarget target, string preset, string resolutionKey, int takeCount)
    {
        if (!_bulkGeneratePending.TryGetValue(target.ShotId, out var request))
        {
            var current = _production.Compositions.SingleOrDefault(c => c.Id == target.CompositionId && !c.Archived)
                ?? throw new WorkspaceStoreException("The selected setup is unavailable. Reopen bulk generation.");
            if (VideoLockIssue(target.ShotId, current.Id) is { } issue) throw new WorkspaceStoreException(issue);
            request = await VideoRequests.CaptureCompositionWithPresetAsync(
                Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, current.Id, current.Version,
                takeCount, current.Seed, preset, BulkResolution(resolutionKey), BulkUpscale(resolutionKey), _lifetime.Token, current.GenerationSetupVersion);
            _bulkGeneratePending.Add(target.ShotId, request);
        }
        await AiJobs.EnqueueAsync(request, _lifetime.Token);
        _bulkGeneratePending.Remove(target.ShotId);
    }

    private IReadOnlyList<BulkGenerateTarget> DiscoverBulkGenerateTargets() =>
        _doc.Shots.Select(shot =>
        {
            var composition = Current is { Archived: false } current && current.ShotId == shot.Id ? current :
                _production.Compositions.FirstOrDefault(c => c.ShotId == shot.Id && !c.Archived && !c.GenerationSetupArchived);
            var issue = composition is null
                ? "No active setup"
                : VideoLockIssue(shot.Id, composition.Id) ??
                  (composition.GenerationSetupArchived ? "Restore this global setup or choose another before generating." : null) ??
                  (_project is not null ? ProductionPolicy.Issue(composition, _assets, _doc, _project) : null);
            return new BulkGenerateTarget(shot.Id, composition?.Id, shot.SceneId, string.IsNullOrWhiteSpace(shot.Title) ? "Untitled shot" : shot.Title, issue is null, issue);
        }).ToArray();

    private string? VideoLockIssue(Guid shotId, Guid compositionId) =>
        AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.ShotId == shotId && j.Target.CompositionId == compositionId && j.LocksTarget)
            ? "Video job active"
            : null;

    private static VideoResolution? BulkResolution(string key) =>
        key == "upscaled" || !VideoResolutions.TryParse(key, out var resolution) ? null : resolution;

    private static bool? BulkUpscale(string key) =>
        key == "upscaled" ? true : VideoResolutions.TryParse(key, out _) ? false : null;

    private string BulkResolutionLabel() => _bulkGenerateResolution switch
    {
        "current" => "each shot's resolution",
        "upscaled" => "Upscaled preview",
        _ when VideoResolutions.TryParse(_bulkGenerateResolution, out var resolution) => VideoResolutions.Label(resolution),
        _ => _bulkGenerateResolution
    };
}
