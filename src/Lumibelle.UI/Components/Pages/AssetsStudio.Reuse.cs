using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private Guid? _reuseOriginAsset;
    private async Task<AssetLibrary?> PrepareAssetReuseAsync()
    {
        if (!await SaveForCloseAsync() || !await SaveNowAsync() || _library is null) return null;
        _reuseOriginAsset = _selectedAssetId;
        return _library.Copy();
    }
    private async Task AssetReuseChangedAsync(AssetReuseResult result)
    {
        if (_disposed || _library is null) return;
        await _saveGate.WaitAsync();
        try
        {
            var saved = await AssetStore.LoadAsync(Id);
            // A media transfer must not discard edits typed while its file copies were in progress.
            _library = AssetLibraryRebase.Merge(_savedLibrary ?? _library, _library, saved);
            _savedLibrary = saved.Copy();
            if (!_library.Assets.Any(a => a.Id == _selectedAssetId)) SwitchAsset(_library.Assets.FirstOrDefault()?.Id);
            else if (_presentation.Selection is { } selected && !AllGalleryItems.Any(i => i.Key == selected))
            { _reels?.ClearDetails(); _voices?.ClearEditing(); UseCreateMode(); }
        }
        catch (WorkspaceConflictException)
        {
            _conflict = true; _saveError = "The media copy was saved, but newer local asset edits need review. Reload or save your draft before continuing.";
            throw;
        }
        finally { _saveGate.Release(); }
        if (result.Library.ProjectId == Id && result.Available && _selectedAssetId == _reuseOriginAsset && !_dirty && !_imageDetailsDirty)
        {
            await SelectAssetAsync(result.AssetId);
            _search = ""; _categoryFilter = null; _presentation.Filter = "All"; _presentation.Search = ""; _lookFilter = "all";
            _revealMedia = result.MediaId;
        }
        await InvokeAsync(StateHasChanged);
    }
}
