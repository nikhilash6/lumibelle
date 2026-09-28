using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private string _takeInputFilter = "all";
    private string _takeLanguage = "*";

    // A take is new until its generation request has been shown in the batch review or
    // the Takes view: the same unread state that AI activity reports.
    private (long Revision, HashSet<Guid> Jobs) _unreadJobs = (-1, []);
    private HashSet<Guid> UnreadJobs
    {
        get
        {
            var view = AiJobs.View;
            if (_unreadJobs.Revision != view.Revision) _unreadJobs = (view.Revision, view.Jobs.Where(j => j.Unread).Select(j => j.Id).ToHashSet());
            return _unreadJobs.Jobs;
        }
    }
    private bool IsNewTake(ShotTake take) => take.AiJobId is { } job && UnreadJobs.Contains(job);
    private int NewTakeCount(Guid shotId) => _doc.Takes.Count(t => t.ShotId == shotId && IsNewTake(t));
    private AiJobHeader[] ShownTakeJobs(IEnumerable<ShotTake> takes)
    {
        var ids = takes.Where(IsNewTake).Select(t => t.AiJobId!.Value).ToHashSet();
        return AiJobs.View.Jobs.Where(j => ids.Contains(j.Id)).ToArray();
    }
    private Dictionary<Guid, Guid[]> ShownTakeMedia(IEnumerable<ShotTake> takes) =>
        takes.Where(IsNewTake).GroupBy(t => t.AiJobId!.Value).ToDictionary(g => g.Key, g => g.Select(t => t.Id).ToArray());
    private string TakeSetupKey(ShotTake take) => (take.Snapshot.Production?.GenerationSetupId ??
        _production.Compositions.FirstOrDefault(c => c.Id == take.Snapshot.Production?.CompositionId)?.GenerationSetupId ??
        take.Snapshot.Production?.CompositionId)?.ToString() ?? "";
    private IEnumerable<(string Id, string Name)> TakeSetupOptions => _globalSetups.Setups
        .Where(s => !s.Archived || _production.Compositions.Any(c => c.ShotId == _selected && c.GenerationSetupId == s.Id))
        .Select(s => (s.Id.ToString(), s.Name))
        .Concat(_production.Compositions.Where(c => c.ShotId == _selected && c.GenerationSetupId is null).Select(c => (c.Id.ToString(), c.Name)));
    private List<ShotTake> FilteredTakes => ShotTakes.Where(t =>
        lumibelle.Services.Shots.TakeLanguages.Matches(t, _takeLanguage) &&
        (string.IsNullOrEmpty(_takeSetupFilter) || TakeSetupKey(t) == _takeSetupFilter) &&
        MatchesTakeInputFilter(TakeChanges(t))).Reverse().ToList();
    private bool MatchesTakeInputFilter(TakeInputChange changes) => _takeInputFilter switch
    {
        "current" => changes == TakeInputChange.None,
        "changed" => (changes & (TakeInputChange.Script | TakeInputChange.Prompt | TakeInputChange.References)) != 0,
        "script" => changes.HasFlag(TakeInputChange.Script),
        "prompt" => changes.HasFlag(TakeInputChange.Prompt),
        "references" => changes.HasFlag(TakeInputChange.References),
        "unknown" => changes.HasFlag(TakeInputChange.Unavailable),
        _ => true
    };
    private void ClearTakeFilters() { _takeSetupFilter = ""; _takeInputFilter = "all"; _takeLanguage = "*"; }

    private readonly Dictionary<Guid, ScriptSourceSnapshot?> _takeScriptSources = [];
    private ScriptDocument? _takeCurrentScript;

    private TakeInputChange TakeChanges(ShotTake take) => TakeInputChanges.Compare(take.Snapshot,
        _doc.Shots.FirstOrDefault(s => s.Id == take.ShotId),
        _production.Compositions.FirstOrDefault(c => take.ShotId == take.Snapshot.Shot.Id
            ? c.Id == take.Snapshot.Production?.CompositionId : c.ShotId == take.ShotId && !c.Archived),
        _assets, _doc, take.Snapshot.Shot.ApprovedScriptId is { } id ? _takeScriptSources.GetValueOrDefault(id)?.Blocks : null,
        _takeCurrentScript?.Blocks);

    private async Task RefreshTakeSources()
    {
        if (_doc.Takes.Count == 0) return;
        try { _takeCurrentScript = await Scripts.LoadAsync(Id, _lifetime.Token); }
        catch (WorkspaceStoreException) { _takeCurrentScript = null; }
        foreach (var id in _doc.Takes.Select(t => t.Snapshot.Shot.ApprovedScriptId).OfType<Guid>().Distinct())
        {
            if (_takeScriptSources.ContainsKey(id)) continue;
            try { _takeScriptSources[id] = await Scripts.LoadSourceAsync(Id, id, _lifetime.Token); }
            catch (WorkspaceStoreException) { /* Show an unavailable comparison and retry on the next refresh. */ }
        }
    }
}
