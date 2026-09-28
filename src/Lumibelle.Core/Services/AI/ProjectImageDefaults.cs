using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class ProjectImageDefaults
{
    // Captured settings and explicit request choices always win over a mutable default.
    public static async Task<ImageWorkflow> ResolveAsync(Guid? projectId, ImageWorkflow? selection, AiSettings settings,
        IProjectAiPreferencesStore? preferences, CancellationToken ct = default)
    {
        if (selection is { } selected) return selected;
        var project = projectId is { } id && preferences is not null ? await preferences.LoadAsync(id, ct) : null;
        return project?.ImageDefault ?? settings.DefaultImageWorkflow;
    }
}
