using lumibelle.Models;
using lumibelle.Services;

namespace Lumibelle.Tests;

internal sealed class FakeProjectStore : IProjectStore
{
    public Func<Task<ProjectLibrary>> List { get; set; } = () => Task.FromResult(new ProjectLibrary([], []));
    public Func<Guid, Task<ProjectInfo?>> Get { get; set; } = _ => Task.FromResult<ProjectInfo?>(null);
    public Func<CreateProjectRequest, Task<ProjectInfo>> Create { get; set; } = request => Task.FromResult(Project(request.Name, request.Description));
    public List<CreateProjectRequest> CreateCalls { get; } = [];
    public Func<ProjectInfo, UpdateProjectRequest, Task<ProjectInfo>> Update { get; set; } = (original, request) =>
        Task.FromResult(original with { Name = request.Name.Trim(), Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim() });
    public List<(ProjectInfo Original, UpdateProjectRequest Request)> UpdateCalls { get; } = [];

    public Task<ProjectLibrary> ListAsync(CancellationToken cancellationToken = default) => List();
    public Task<ProjectInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Get(id);
    public Task<ProjectInfo> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        CreateCalls.Add(request);
        return Create(request);
    }

    public Task<ProjectInfo> UpdateAsync(ProjectInfo original, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        UpdateCalls.Add((original, request));
        return Update(original, request);
    }

    public static ProjectInfo Project(string name = "A little idea", string? description = null) => new()
    {
        SchemaVersion = 1, Id = Guid.NewGuid(), Name = name, Description = description,
        CreatedUtc = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero)
    };
}
