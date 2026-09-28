using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Inject] private IServiceProvider DubbingServices { get; set; } = null!;
    private async Task AddMasterDialogueAsync()
    {
        if (SourceShot is not { } source) return;
        var project = Id;
        try
        {
            var language = "English"; // Historical default for projects without language preferences.
            if (DubbingServices.GetService(typeof(IProjectDubbingStore)) is IProjectDubbingStore store)
                language = (await store.LanguagesAsync(project)).Main?.Name ?? language;
            if (!_disposed && Id == project && SourceShot?.Id == source.Id)
                EditCoverage(shot => shot.Dialogue.Add(new ShotDialogue { Language = language }));
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
}
