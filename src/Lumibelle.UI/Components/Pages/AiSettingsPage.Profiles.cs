using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private async Task<bool> SaveTextProfilesAsync(IReadOnlyList<TextModelReference> profiles)
    {
        // Apply only the profile library, using the page's revision-aware saved snapshot.
        // Global/project defaults intentionally retain their independently captured values.
        var save = SaveChangeAsync(settings => settings with { TextModelProfiles = profiles.ToList() }, "LLM profiles saved.");
        StateHasChanged();
        try { return await save; }
        finally { StateHasChanged(); }
    }
}
