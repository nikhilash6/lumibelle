using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private Guid? _voiceComposition;
    private bool _voiceDetailsOpen, _applyingReference;
    private Shot? _referenceShot, _originalReferenceShot;
    private ShotVoiceBinding? _voiceDraft, _voiceOriginal;
    private string? _referenceDetailsError, _referenceContext;
    private async Task CheckStoredReferenceTarget(Guid bindingId, bool voice)
    {
        var latest = await Production.LoadAsync(Id, _lifetime.Token);
        var baseline = _savedComposition?.Shot;
        var current = latest.Compositions.FirstOrDefault(c => c.Id == Current?.Id)?.Shot;
        string Target(Shot? s) => Json(new { Exists = s is not null,
            Characters = s is null ? null : ShotReferences.Characters(s),
            Image = voice ? null : s?.Images.FirstOrDefault(b => b.Id == bindingId),
            Voice = voice ? s?.Voices.FirstOrDefault(v => v.VoiceId == bindingId) : null });
        if (Target(baseline) != Target(current))
            throw new WorkspaceStoreException("Another tab changed this reference or its character context. Cancel and reload the saved shot before reviewing it again. Your draft remains here for copying.");
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AtomicJsonFile.Options);
    private string ReferenceSummary(Shot shot, ShotImageBinding b) => string.Join(" · ", new[] {
        ShotReferences.Characters(shot).FirstOrDefault(c => c.Id == b.RepresentsId)?.Name,
        ReferenceSetups.IsAnchor(b) ? null : b.Purpose is { } purpose ? ShotLooks.Label(purpose) : b.Use is { } use ? ReferenceSetups.UseLabel(use) : b.Role,
        ReferenceLookName(b), b.Crop is null ? null : "Cropped" }.Where(v => !string.IsNullOrWhiteSpace(v)));

    private string ImageContext(ShotImageBinding binding, AssetLibrary library)
    {
        var asset = library.Assets.FirstOrDefault(a => a.Id == binding.AssetId);
        var image = asset?.Images.FirstOrDefault(i => i.Id == binding.MediaId);
        return Json(new { Image = image?.Id, image?.LookId, asset?.Name, asset?.Category, asset?.Looks, asset?.PreservationGuidance, Guidance = ShotReferences.Resolve(binding, library, _doc),
            Look = asset?.Looks.FirstOrDefault(l => l.Id == binding.LookId) });
    }

    private bool SameReferenceTarget(Shot current) => _originalReferenceShot is { } original &&
        current.Id == original.Id && Json(ShotReferences.Characters(current)) == Json(ShotReferences.Characters(original));

    private void OpenVoiceDetails(ShotVoiceBinding voice)
    {
        if (Selected is not { } shot) return;
        _voiceComposition = Current?.Id; _referenceShot = shot.Copy(); _originalReferenceShot = shot.Copy(); _voiceOriginal = ShotCopy.Of(voice); _voiceDraft = ShotCopy.Of(voice);
        _referenceContext = Json(_assets.Voices.FirstOrDefault(v => v.Id == voice.VoiceId));
        _referenceDetailsError = null; _voiceDetailsOpen = true;
    }

    private async Task ApplyVoiceDetails()
    {
        if (_applyingReference || _voiceDraft is null || _voiceOriginal is null) return;
        _applyingReference = true;
        try
        {
            await CheckStoredReferenceTarget(_voiceOriginal.VoiceId, true);
            var latest = await AssetStore.LoadAsync(Id, _lifetime.Token);
            var recording = latest.Voices.FirstOrDefault(v => v.Matches(_voiceDraft));
            if (Current?.Id != _voiceComposition || Selected is not { } shot || !SameReferenceTarget(shot) ||
                Json(shot.Voices.FirstOrDefault(v => v.VoiceId == _voiceOriginal.VoiceId)) != Json(_voiceOriginal) ||
                _referenceContext != Json(recording))
                throw new WorkspaceStoreException("This voice or its speaker context changed. Cancel and reopen Edit voice to review the current assignment. Your draft remains here.");
            if (recording is null) throw new WorkspaceStoreException("Restore this voice before applying an excerpt.");
            if (!double.IsFinite(_voiceDraft.Start) || !double.IsFinite(_voiceDraft.Duration) || _voiceDraft.Start < 0 ||
                _voiceDraft.Duration is < 1 or > 15 || _voiceDraft.Start + _voiceDraft.Duration > recording.Duration)
                throw new WorkspaceStoreException("Choose a 1–15 second excerpt within the recording.");
            var index = shot.Voices.FindIndex(v => v.VoiceId == _voiceOriginal.VoiceId);
            Edit(s => s.Voices[index] = ShotCopy.Of(_voiceDraft));
            _voiceDetailsOpen = false;
        }
        catch (Exception e) { _referenceDetailsError = e.Message; }
        finally { _applyingReference = false; }
    }
}
