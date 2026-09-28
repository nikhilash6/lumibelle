using lumibelle.Models;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private ShotTake? OverviewTake(Shot shot) => _doc.Takes.FirstOrDefault(t => t.ShotId == shot.Id && t.Id == shot.SelectedTakeId)
        ?? _doc.Takes.Where(t => t.ShotId == shot.Id && t.Snapshot.Dub is null).OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).FirstOrDefault()
        ?? _doc.Takes.Where(t => t.ShotId == shot.Id).OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).FirstOrDefault();

    private string? ReferenceRepairIssue => Selected is not { } shot ? null
        : 
          (shot.Images.Any(b => ResolveImage(b).Url is null || ResolveImage(b).TrashId is not null || _missing.Contains(b.Id)) ? "Restore or replace unavailable image references."
          : shot.Voices.Any(v => !_assets.Voices.Any(a => a.Matches(v))) ? "Restore or replace unavailable voices." : null);
    private bool UnlinkedCharacter(ShotImageBinding b) => !ReferenceSetups.IsAnchor(b) && b.RepresentsId is null && _assets.Assets.Any(a => a.Id == b.AssetId && a.Category == AssetCategory.Character);
}
