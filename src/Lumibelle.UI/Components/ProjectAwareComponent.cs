using lumibelle.Services;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components;

public abstract class ProjectAwareComponent : ComponentBase
{
    [Inject] private IServiceProvider ProjectServices { get; set; } = null!;
    protected IProjectRoutes? ProjectRoutes => ProjectServices.GetService<IProjectRoutes>();
    protected WorkspacePositions? Positions => ProjectServices.GetService<WorkspacePositions>();
    protected string ProjectLink(Guid id, string suffix = "") => ProjectRoutes?.Path(id, suffix) ?? $"/projects/{id:D}{suffix}";
    protected string ProjectLink(string url) => ProjectRoutes?.Canonicalize(url) ?? url;
    protected T Place<T>(Guid id, string studio, string key, T fallback = default!) => Positions is null ? fallback : Positions.Get(id, studio, key, fallback);
    protected Task Remember<T>(Guid id, string studio, string key, T value) => Positions?.SaveAsync(id, studio, key, value) ?? Task.CompletedTask;
}
