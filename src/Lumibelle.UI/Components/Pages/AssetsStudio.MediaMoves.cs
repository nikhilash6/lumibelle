using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private AssetGalleryItem? _moveMedia;
    private Guid _moveMediaOwner, _mediaDestination;
    private AssetCategory _moveMediaCategory;
    private bool _movingMedia;
    private string? _mediaMoveError;
    private IEnumerable<ReferenceAsset> MediaDestinations => _library?.Assets.Where(a => a.Id != _moveMediaOwner && a.Category == _moveMediaCategory) ?? [];

    private Task OpenMediaMove(AssetMediaSelection key) => TransitionTools(() =>
    {
        if (SelectedAsset is { } owner && AllGalleryItems.FirstOrDefault(i => i.Key == key) is { } item)
        {
            _moveMedia = item; _moveMediaOwner = owner.Id; _moveMediaCategory = owner.Category;
            _mediaDestination = Guid.Empty; _mediaMoveError = null;
        }
        return Task.CompletedTask;
    });

    private async Task ConfirmMediaMove()
    {
        if (_movingMedia || _moveMedia is not { } item || _mediaDestination == Guid.Empty) return;
        _movingMedia = true; _mediaMoveError = null;
        try
        {
            var moved = false;
            await TransitionTools(async () =>
            {
                var target = MediaDestinations.SingleOrDefault(a => a.Id == _mediaDestination)
                    ?? throw new WorkspaceStoreException("The destination is no longer available. Choose another asset.");
                var wasDefaultVoice = item.Key.Kind == AssetMediaKind.Voice && SelectedAsset?.DefaultVoiceId == item.Key.Id;
                await MutateVoiceAsync(revision => item.Key.Kind == AssetMediaKind.Reel
                    ? AssetStore.MoveReelAsync(Id, _moveMediaOwner, item.Key.Id, target.Id, revision)
                    : AssetStore.MoveVoiceAsync(Id, _moveMediaOwner, item.Key.Id, target.Id, revision));
                if (_presentation.Selection == item.Key) { _reels?.ClearDetails(); _voices?.ClearEditing(); _presentation.Selection = null; }
                SwitchAsset(target.Id);
                _presentation.Selection = item.Key;
                _presentation.Filter = "All"; _presentation.Search = ""; _lookFilter = "all";
                _revealMedia = item.Key.Id;
                _moveMedia = null; moved = true;
                if (_workspace is not null) await _workspace.ShowToolsAsync();
                Snackbar.Add($"{item.Name} moved to {target.Name}." + (wasDefaultVoice ? " The original character’s default voice was cleared." : ""), Severity.Success);
            });
            if (!moved) _mediaMoveError = "Save the current editor's changes before moving this reference.";
        }
        catch (WorkspaceStoreException e) { _mediaMoveError = e.Message; }
        finally { _movingMedia = false; }
    }
}
