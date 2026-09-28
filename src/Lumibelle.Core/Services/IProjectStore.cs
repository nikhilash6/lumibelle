using lumibelle.Models;

namespace lumibelle.Services;

public interface IProjectStore
{
    Task<ProjectLibrary> ListAsync(CancellationToken cancellationToken = default);
    Task<ProjectInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectInfo> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);
    Task<ProjectInfo> UpdateAsync(ProjectInfo original, UpdateProjectRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProjectStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);
