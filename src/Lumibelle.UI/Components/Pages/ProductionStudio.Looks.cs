using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;
public partial class ProductionStudio
{
    private string? ReferenceLookIssue(ShotImageBinding binding) => Selected is { } shot
        ? ShotLooks.Issue(new Shot { Characters = shot.Characters, Dialogue = shot.Dialogue, Images = [binding] }, _assets) : null;
    private string ReferenceLookName(ShotImageBinding b) => _assets.Assets.FirstOrDefault(a => a.Id == b.AssetId)?.Looks.FirstOrDefault(l => l.Id == b.LookId)?.Name ?? "General / unassigned";
    private IReadOnlyList<ShotAppearanceContext> AppearanceContext(Shot shot) => ShotLooks.Capture(shot, _assets);
}
