using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    // The aspect stays with its shot in every setup; a scene usually shares one framing.
    private List<Guid> SceneAspectOthers(Shot shot) => shot.SceneId is not { } scene ? [] :
        _doc.Shots.Where(s => s.SceneId == scene && s.Id != shot.Id &&
            _production.ShotContent.FirstOrDefault(c => c.ShotId == s.Id) is { } content && content.AspectOverride != shot.AspectOverride)
        .Select(s => s.Id).ToList();

    private static string SceneAspectAction(Shot shot, int others)
    {
        var shots = others == 1 ? "1 other shot" : $"{others} other shots";
        return shot.AspectOverride is { } aspect ? $"Use {aspect} for {shots} in this scene" : $"Make {shots} in this scene follow the project";
    }

    private async Task ApplyAspectToScene(IReadOnlyCollection<Guid> others)
    {
        if (_setupBusy || Selected is null) return;
        _setupBusy = true;
        try {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save() || Selected is not { } shot) return;
            _production = await Production.SetAspectAsync(Id, others, shot.AspectOverride, _lifetime.Token);
            _savedComposition = Current?.Copy();
            Notify(others.Count == 1 ? "The other shot in this scene now matches." : $"{others.Count} other shots in this scene now match.");
        } catch (Exception e) { _error = e.Message; }
        finally { _setupBusy = false; }
    }
}
