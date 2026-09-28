using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Projects;

namespace Lumibelle.Tests;

// Every project is in the library folder unless a test adds a location.
internal sealed class FakeProjectFolders : IProjectFolders
{
    public List<ProjectLocation> Locations { get; } = [];
    public Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProjectLocation>>(Locations.ToArray());
    public Task<ProjectLocation?> LocationAsync(Guid project, CancellationToken ct = default) => Task.FromResult(Locations.FirstOrDefault(l => l.ProjectId == project));
    public Task<ProjectInfo> OpenAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProjectLocation> MoveOutAsync(Guid project, string parent, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveAsync(Guid project, CancellationToken ct = default) { Locations.RemoveAll(l => l.ProjectId == project); return Task.CompletedTask; }
}
