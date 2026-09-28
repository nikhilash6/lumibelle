using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private bool _selectingTakes, _moveTakesOpen;
    private Guid? _takeSelectionShot, _moveDestination;
    private readonly HashSet<Guid> _moveTakeIds = [];
    private string? _moveTakesError;
    private bool SelectingTakes => _selectingTakes && _takeSelectionShot == _selected;
    private void ToggleTakeSelection()
    {
        _selectingTakes = !SelectingTakes; _takeSelectionShot = _selected;
        _moveTakeIds.Clear();
    }
    private void SelectTakeForMove(Guid id, bool selected)
    { if (selected) _moveTakeIds.Add(id); else _moveTakeIds.Remove(id); }
    private void OpenMoveTakes()
    {
        _moveTakeIds.IntersectWith(ShotTakes.Select(t => t.Id));
        if (_moveTakeIds.Count == 0) return;
        _moveDestination = _doc.Shots.FirstOrDefault(s => s.Id != _selected)?.Id;
        _moveTakesError = null; _moveTakesOpen = true;
    }
    private async Task MoveSelectedTakes()
    {
        if (_mediaBusy || _moveDestination is not { } destination || _moveTakeIds.Count == 0) return;
        _mediaBusy = true; _moveTakesError = null;
        try
        {
            var ids = _moveTakeIds.ToArray();
            if (!await Save()) throw new WorkspaceStoreException("Save the shot before moving takes.");
            var latest = await Store.LoadAsync(Id, _lifetime.Token);
            if (latest.Takes.Count(t => ids.Contains(t.Id) && t.ShotId == _takeSelectionShot) != ids.Length)
                throw new WorkspaceStoreException("The selected takes changed. Close this dialog and select them again.");
            _doc = await Store.MoveTakesAsync(Id, ids, destination, latest.Revision, _lifetime.Token);
            Baseline(_doc); SyncCoverage(); _undo.Clear();
            _moveTakesOpen = false; _selectingTakes = false; _moveTakeIds.Clear();
            ClearTakeFilters();
            await Select(destination);
            await LoadVideoRunsAsync();
            Notify($"Moved {ids.Length} {(ids.Length == 1 ? "take" : "takes")} to {SourceShot?.Title}.");
        }
        catch (Exception e) { _moveTakesError = e.Message; }
        finally { _mediaBusy = false; }
    }
}
