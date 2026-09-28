using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [Inject] public TextRequestReviews EnhancementOutcomes { get; set; } = null!;
    // Whether a finished enhancement holds a prompt that was neither applied nor discarded.
    // Results never change once completed; resolutions arrive through TextRequestReviews.
    private readonly Dictionary<Guid, bool> _enhancementPending = [];
    private readonly Dictionary<Guid, AiJobHeader> _enhancementsReady = [];
    private bool _refreshingEnhancements, _refreshEnhancementsAgain;
    private IReadOnlySet<Guid> EnhancementReviewAssets => _enhancementsReady.Keys.ToHashSet();

    // Only each asset's latest enhancement counts; a newer request replaces its review.
    private IEnumerable<AiJobHeader> LatestEnhancements => AiJobs.View.Jobs
        .Where(j => j.Kind == AiJobKind.PromptEnhancement && j.Target.ProjectId == Id && j.Target.AssetId is not null)
        .GroupBy(j => j.Target.AssetId!.Value).Select(g => g.MaxBy(j => j.CreatedUtc)!)
        .Where(j => j.State == AiJobState.Completed && !j.CancelRequested);

    private void EnhancementQueueChanged() { if (!_disposed) _ = InvokeAsync(RefreshEnhancementReviewsAsync); }
    private void EnhancementResolved(Guid job) { if (_disposed) return; _enhancementPending[job] = false; _ = InvokeAsync(RefreshEnhancementReviewsAsync); }

    private async Task RefreshEnhancementReviewsAsync()
    {
        if (_disposed) return;
        if (_refreshingEnhancements) { _refreshEnhancementsAgain = true; return; }
        _refreshingEnhancements = true;
        try {
            do {
                _refreshEnhancementsAgain = false;
                var latest = LatestEnhancements.ToArray();
                foreach (var job in latest.Where(j => !_enhancementPending.ContainsKey(j.Id)))
                    _enhancementPending[job.Id] = await PendingEnhancement(job);
                _enhancementsReady.Clear();
                foreach (var job in latest.Where(j => _enhancementPending[j.Id])) _enhancementsReady[job.Target.AssetId!.Value] = job;
            } while (_refreshEnhancementsAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _refreshingEnhancements = false; if (!_disposed) StateHasChanged(); }
    }

    private async Task<bool> PendingEnhancement(AiJobHeader job)
    {
        try {
            if (await EnhancementOutcomes.IsResolved(job.Id, _extractionLifetime.Token)) return false;
            var result = await AiJobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _extractionLifetime.Token);
            return result is { Complete: true, Error: null } && result.Read<PromptEnhancementResult>()?.Kind == PromptEnhancementKind.Prompt;
        }
        // An unreadable result is shown by the enhancement panel itself; it is not a ready review.
        catch (Exception e) when (e is WorkspaceStoreException or System.Text.Json.JsonException) { return false; }
    }

    // Opens the suggestion itself, like the review link in AI activity.
    private async Task ReviewEnhancement(Guid assetId)
    {
        if (!_enhancementsReady.TryGetValue(assetId, out var job)) return;
        if (_selectedAssetId != assetId) { await SelectAssetAsync(assetId); if (_selectedAssetId != assetId) return; }
        // The review belongs to the image composer, which must be showing to open it.
        if (_presentation.Selection is { Kind: not AssetMediaKind.Image }) await EnterCreate();
        if (!ShowImageTools) await ChangeCreation(new ChangeEventArgs { Value = nameof(AssetCreationKind.Image) });
        if (_workspace is not null) await _workspace.ShowToolsAsync();
        Navigation.NavigateTo(ProjectLink(job.ReviewUrl));
    }
}
